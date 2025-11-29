using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.System
{
    // 使用 partial 关键字，表示这是类的一部分
    public sealed partial class HardwareMonitor : IDisposable
    {
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;

        // =======================================================================
        // [字段] 核心资源与锁
        // =======================================================================
        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();
        
        // 传感器映射字典
        private readonly Dictionary<string, ISensor> _map = new();
        private readonly Dictionary<string, float> _lastValid = new();
        private DateTime _lastMapBuild = DateTime.MinValue;

        // =======================================================================
        // [缓存] 高性能读取缓存 (避免 LINQ)
        // =======================================================================
        // CPU 核心传感器对 (用于加权平均计算)
        private class CpuCoreSensors
        {
            public ISensor? Clock;
            public ISensor? Load;
        }
        private List<CpuCoreSensors> _cpuCoreCache = new();
        
        // 显卡硬件缓存 (用于快速定位)
        private IHardware? _cachedGpu;

        // 网络/磁盘 智能缓存
        private IHardware? _cachedNetHw;
        private DateTime _lastNetScan = DateTime.MinValue;
        private IHardware? _cachedDiskHw;
        private DateTime _lastDiskScan = DateTime.MinValue;

        // =======================================================================
        // [构造与析构]
        // =======================================================================
        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            _computer = new Computer()
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false
            };

            // 异步初始化，防止卡顿 UI
            Task.Run(() =>
            {
                try
                {
                    _computer.Open();
                    BuildSensorMap();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[HardwareMonitor] Init failed: " + ex.Message);
                }
            });
        }

        public void Dispose() => _computer.Close();
        
        // =======================================================================
        // [生命周期] 定时更新 (修复版：加回网络和磁盘)
        // =======================================================================
        public void UpdateAll()
        {
            try
            {
                // 1. 预判需要更新的硬件类型
                bool needCpu = _cfg.Enabled.CpuLoad || _cfg.Enabled.CpuTemp || _cfg.Enabled.CpuClock || _cfg.Enabled.CpuPower;
                bool needGpu = _cfg.Enabled.GpuLoad || _cfg.Enabled.GpuTemp || _cfg.Enabled.GpuVram || _cfg.Enabled.GpuClock || _cfg.Enabled.GpuPower;
                bool needMem = _cfg.Enabled.MemLoad;
                
                // ★★★ 修复：加回网络和磁盘的开关判断 ★★★
                bool needNet = _cfg.Enabled.NetUp || _cfg.Enabled.NetDown;
                bool needDisk = _cfg.Enabled.DiskRead || _cfg.Enabled.DiskWrite;

                foreach (var hw in _computer.Hardware)
                {
                    // --- CPU ---
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        if (needCpu) hw.Update();
                        continue;
                    }

                    // --- GPU ---
                    if (hw.HardwareType == HardwareType.GpuNvidia || 
                        hw.HardwareType == HardwareType.GpuAmd || 
                        hw.HardwareType == HardwareType.GpuIntel)
                    {
                        if (needGpu) hw.Update();
                        continue;
                    }

                    // --- 内存 ---
                    if (hw.HardwareType == HardwareType.Memory)
                    {
                        if (needMem) hw.Update();
                        continue;
                    }

                    // --- ★★★ 修复：网络 (Network) ★★★ ---
                    if (hw.HardwareType == HardwareType.Network)
                    {
                        if (needNet) hw.Update();
                        continue;
                    }

                    // --- ★★★ 修复：磁盘 (Storage) ★★★ ---
                    if (hw.HardwareType == HardwareType.Storage)
                    {
                        if (needDisk) hw.Update();
                        continue;
                    }
                }
                
                OnValuesUpdated?.Invoke();
            }
            catch { }
        }

        // =======================================================================
        // [核心] 构建传感器映射与缓存 (最复杂的构建逻辑)
        // =======================================================================
        private void BuildSensorMap()
        {
            // 1. 准备临时容器 (线程安全)
            var newMap = new Dictionary<string, ISensor>();
            var newCpuCache = new List<CpuCoreSensors>();
            IHardware? newGpu = null;

            // 局部递归函数
            void RegisterTo(IHardware hw)
            {
                hw.Update();

                // --- 填充 CPU 缓存 (用于加权平均) ---
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    // 查找所有核心频率 (排除总线频率)
                    var clocks = hw.Sensors.Where(s => s.SensorType == SensorType.Clock && Has(s.Name, "core") && !Has(s.Name, "bus"));
                    
                    foreach (var clock in clocks)
                    {
                        // 查找对应的负载传感器 (名字通常一致，如 "CPU Core #1")
                        var load = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == clock.Name);
                        newCpuCache.Add(new CpuCoreSensors { Clock = clock, Load = load });
                    }
                }

                // --- 填充 GPU 缓存 (取第一个找到的显卡) ---
                if (newGpu == null && (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel))
                {
                    newGpu = hw;
                }

                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = NormalizeKey(hw, s); // 调用 Logic 文件中的方法
                    if (!string.IsNullOrEmpty(key) && !newMap.ContainsKey(key))
                        newMap[key] = s;
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub);
            }

            // 按优先级排序并注册
            var ordered = _computer.Hardware.OrderBy(h => GetHwPriority(h));
            foreach (var hw in ordered) RegisterTo(hw);

            // 2. 原子交换数据 (加锁)
            lock (_lock)
            {
                _map.Clear();
                foreach (var kv in newMap) _map[kv.Key] = kv.Value;
                
                _cpuCoreCache = newCpuCache;
                _cachedGpu = newGpu;
                
                _lastMapBuild = DateTime.Now;
            }
        }

        private static int GetHwPriority(IHardware hw)
        {
            return hw.HardwareType switch
            {
                HardwareType.GpuNvidia => 0,
                HardwareType.GpuAmd => 1,
                HardwareType.GpuIntel => 2,
                _ => 3
            };
        }

        private void EnsureMapFresh()
        {
            if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
                BuildSensorMap();
        }
    }
}
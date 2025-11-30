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
        // [生命周期] 定时更新 (终极优化版)
        // =======================================================================
        
        // 1. 定义时间标记
        private DateTime _startTime = DateTime.Now;      // 启动时间
        // [核心修复] 初始值为 Now，强迫程序启动时先等 3 秒再进行慢速全盘扫描，防止卡顿
        private DateTime _lastSlowScan = DateTime.Now;   

        public void UpdateAll()
        {
            try
            {
                // 1. 预判开关
                bool needCpu = _cfg.Enabled.CpuLoad || _cfg.Enabled.CpuTemp || _cfg.Enabled.CpuClock || _cfg.Enabled.CpuPower;
                bool needGpu = _cfg.Enabled.GpuLoad || _cfg.Enabled.GpuTemp || _cfg.Enabled.GpuVram || _cfg.Enabled.GpuClock || _cfg.Enabled.GpuPower;
                bool needMem = _cfg.Enabled.MemLoad;
                bool needNet = _cfg.Enabled.NetUp || _cfg.Enabled.NetDown;
                bool needDisk = _cfg.Enabled.DiskRead || _cfg.Enabled.DiskWrite;

                // 2. 启动保护期 (前3秒)
                bool isStartupPhase = (DateTime.Now - _startTime).TotalSeconds < 3;
                
                // 3. 慢速扫描信号 (每3秒一次)
                bool isSlowScanTick = (DateTime.Now - _lastSlowScan).TotalSeconds > 3;

                foreach (var hw in _computer.Hardware)
                {
                    // --- CPU / GPU / Memory (核心硬件：每秒实时更新) ---
                    if (hw.HardwareType == HardwareType.Cpu) { if (needCpu) hw.Update(); continue; }
                    if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel) { if (needGpu) hw.Update(); continue; }
                    if (hw.HardwareType == HardwareType.Memory) { if (needMem) hw.Update(); continue; }

                    // --- 网络 (Network) ---
                    if (hw.HardwareType == HardwareType.Network)
                    {
                        if (needNet) 
                        {
                            // 判断是否为“特权网卡” (满足以下任一条件即为特权)：
                            // 1. 它是当前缓存正在读的 (Cached)
                            // 2. 它是上次自动记忆的 (LastAuto)
                            // 3. 它是用户手动指定的 (Preferred) -> 即使是虚拟网卡也能由用户强制开启
                            bool isTarget = (_cachedNetHw != null && hw == _cachedNetHw) || 
                                            (hw.Name == _cfg.LastAutoNetwork) ||
                                            (hw.Name == _cfg.PreferredNetwork);

                            // 逻辑分流：
                            if (isTarget)
                            {
                                hw.Update(); // 特权：无视保护期，无视虚拟身份，每秒全速更新 (秒出数据)
                            }
                            else if (isStartupPhase || IsVirtualNetwork(hw.Name))
                            {
                                continue;    // 垃圾/闲置：启动期跳过，虚拟网卡跳过 (优化启动速度)
                            }
                            else if (isSlowScanTick)
                            {
                                hw.Update(); // 普通物理网卡：慢速巡检 (3秒一次)
                            }
                        }
                        continue;
                    }

                    // --- 磁盘 (Storage) ---
                    if (hw.HardwareType == HardwareType.Storage)
                    {
                        if (needDisk) 
                        {
                            // 磁盘同理：如果是上次记住的盘，直接更新，不等3秒
                            bool isTarget = (_cachedDiskHw != null && hw == _cachedDiskHw) || 
                                            (hw.Name == _cfg.LastAutoDisk) || 
                                            (hw.Name == _cfg.PreferredDisk);

                            if (isTarget)
                            {
                                hw.Update(); 
                            }
                            else if (isStartupPhase) // 其他盘受启动保护
                            {
                                continue;
                            }
                            else if (isSlowScanTick) // 慢速扫描
                            {
                                hw.Update();
                            }
                        }
                        continue;
                    }
                }
                
                // 如果执行了慢速扫描，重置计时器
                if (isSlowScanTick) _lastSlowScan = DateTime.Now;

                OnValuesUpdated?.Invoke();
            }
            catch { }
        }

        // [新增] 辅助方法：复用 Logic.cs 中的关键字判断是否为虚拟网卡
        private bool IsVirtualNetwork(string name)
        {
            // _virtualNicKW 定义在 Logic.cs 中，因为是 partial class 所以可以直接访问
            foreach (var k in _virtualNicKW)
            {
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) 
                    return true;
            }
            return false;
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
                        // ★★★ 修复：AMD 负载叫 "CPU Core #1"，频率叫 "Core #1"，不相等。
                        // 改用 EndsWith 匹配，既支持 Intel (名字一样) 也支持 AMD (带前缀)
                        var load = hw.Sensors.FirstOrDefault(s => 
                            s.SensorType == SensorType.Load && 
                            s.Name.EndsWith(clock.Name, StringComparison.OrdinalIgnoreCase)); // <--- 修改了这里
                            
                        newCpuCache.Add(new CpuCoreSensors { Clock = clock, Load = load });
                    }
                }

                // --- 填充 GPU 缓存 (优化版：智能选择独显) ---
                if (hw.HardwareType == HardwareType.GpuNvidia || 
                    hw.HardwareType == HardwareType.GpuAmd || 
                    hw.HardwareType == HardwareType.GpuIntel)
                {
                    // 如果还没找到显卡，直接用当前这个
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }
                    else
                    {
                        // 如果已经找到了一个显卡，但它是“弱鸡”核显，而当前这个是“强力”独显，则替换！
                        // 判断逻辑：
                        // 1. 旧的是 generic (如 "AMD Radeon(TM) Graphics"), 新的是具体型号 (如 "Intel Arc B580")
                        // 2. 旧的是 Intel 核显，新的是 Nvidia/AMD 独显
                        // 3. 特别针对 B580: 如果新卡名字包含 "Arc"，绝对优先
                        
                        bool oldIsGeneric = IsGenericGpuName(newGpu.Name);
                        bool newIsSpecific = !IsGenericGpuName(hw.Name);
                        bool newIsArc = hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                        bool oldIsArc = newGpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

                        // 优先选 Arc，其次选非通用名称的卡
                        if ((!oldIsArc && newIsArc) || (oldIsGeneric && newIsSpecific))
                        {
                            newGpu = hw;
                        }
                    }
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

        // [新增] 辅助方法：判断是否为通用核显名称
        private bool IsGenericGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            // 常见核显名称: "AMD Radeon(TM) Graphics", "Intel(R) UHD Graphics"
            if (name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("Iris", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static int GetHwPriority(IHardware hw)
        {
            // 如果是 Intel Arc，提到最高优先级
            if (hw.HardwareType == HardwareType.GpuIntel && 
                hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return 0;

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
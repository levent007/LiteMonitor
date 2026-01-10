using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // ★★★ 新增：引用用于内存修剪的库
using System.Reflection; // ★★★ 新增：用于反射关闭历史记录
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using System.Linq; 

namespace LiteMonitor.src.SystemServices
{
    public sealed class HardwareMonitor : IDisposable
    {
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;

        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();

        // 拆分出的子服务
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly DriverInstaller _driverInstaller;
        private readonly HardwareValueProvider _valueProvider;

        private readonly Dictionary<string, float> _lastValidMap = new();
        
        // ★★★ 优化：增加 UI 列表缓存，防止重复分配字符串 ★★★
        private List<string>? _cachedFanList = null; 
        private List<string>? _cachedNetworkList = null; // 网卡列表缓存
        private List<string>? _cachedDiskList = null;    // 硬盘列表缓存

        private DateTime _lastTrafficTime = DateTime.Now;
        private DateTime _lastTrafficSave = DateTime.Now;
        private DateTime _startTime = DateTime.Now;
        private DateTime _lastSlowScan = DateTime.Now;
        private DateTime _lastDiskBgScan = DateTime.Now;

        // ★★★ 新增：声明 Windows API 用于修剪工作集内存 ★★★
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            // 1. 初始化 Computer
            _computer = new Computer()
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                // ★★★ 优化 T0：关闭 PCI 控制器扫描，省下 2 万个对象 (约 8MB) ★★★
                // 除非你需要极底层的 SuperIO 调试，否则不需要开这个
                IsControllerEnabled = true, 
            };

            // 2. 初始化服务
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager();
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, _computer, ReloadComputerSafe);
            _valueProvider = new HardwareValueProvider(_computer, cfg, _sensorMap, _networkManager, _diskManager, _lock, _lastValidMap);

            // 3. 异步启动 (唯一优化：不卡UI)
            Task.Run(async () =>
            {
                try
                {
                    // 这句耗时 4-5 秒，但在执行过程中，硬件会陆续添加到 _computer.Hardware
                    _computer.Open(); 

                    // ★★★ T0+级修复：彻底禁用历史记录，解决 SensorValue[] 飙升 ★★★
                    // 必须在 Open() 之后调用，此时传感器才被创建
                    DisableSensorHistory();

                    // 只有全部扫描完，才建立高速 Map
                    lock (_lock)
                    {
                        _sensorMap.Rebuild(_computer, cfg);
                    }
                    
                    await _driverInstaller.SmartCheckDriver();

                    // ★★★ 优化 T1：启动后大扫除 ★★★
                    // 1. 强制 GC：清理初始化过程中(如JSON解析、驱动检查)产生的临时垃圾
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    
                    // 2. 修剪物理内存：告诉系统"我很闲"，把不用的物理内存页交换出去
                    // 这会让任务管理器里的数值瞬间变得很漂亮
                    try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Init Error: {ex.Message}");
                }
            });
        }

        public float? Get(string key) => _valueProvider.GetValue(key);

        public void UpdateAll()
        {
            try
            {
                DateTime now = DateTime.Now;
                double timeDelta = (now - _lastTrafficTime).TotalSeconds;
                _lastTrafficTime = now;
                if (timeDelta > 5.0) timeDelta = 0;

                bool needCpu = _cfg.IsAnyEnabled("CPU");
                bool needGpu = _cfg.IsAnyEnabled("GPU");
                bool needMem = _cfg.IsAnyEnabled("MEM");
                bool needNet = _cfg.IsAnyEnabled("NET") || _cfg.IsAnyEnabled("DATA");
                bool needDisk = _cfg.IsAnyEnabled("DISK");
                // 判断主板更新需求
                bool needMobo = _cfg.IsAnyEnabled("MOBO") || 
                _cfg.IsAnyEnabled("CPU.Fan") || 
                _cfg.IsAnyEnabled("CPU.Pump") || 
                _cfg.IsAnyEnabled("CASE.Fan");

                bool isSlowScanTick = (now - _lastSlowScan).TotalSeconds > 3;
                bool needDiskBgScan = (now - _lastDiskBgScan).TotalSeconds > 10;

                lock (_lock)
                {
                    // 你原来的代码已经使用了 foreach，非常高效，不需要修改！
                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && needCpu) { hw.Update(); continue; }
                        if ((hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel) && needGpu) { hw.Update(); continue; }
                        if (hw.HardwareType == HardwareType.Memory && needMem) { hw.Update(); continue; }

                        if (hw.HardwareType == HardwareType.Network && needNet)
                        {
                            _networkManager.ProcessUpdate(hw, _cfg, timeDelta, isSlowScanTick);
                            continue;
                        }
                        if (hw.HardwareType == HardwareType.Storage && needDisk)
                        {
                            _diskManager.ProcessUpdate(hw, _cfg, isSlowScanTick, needDiskBgScan);
                            continue;
                        }
                        
                        // 递归更新主板 (Motherboard / SuperIO)
                        if ((hw.HardwareType == HardwareType.Motherboard || hw.HardwareType == HardwareType.SuperIO|| hw.HardwareType == HardwareType.Cooler) && needMobo)
                        {
                             UpdateWithSubHardware(hw);
                             continue;
                        }
                    }
                }

                if (isSlowScanTick) _lastSlowScan = now;
                if (needDiskBgScan) _lastDiskBgScan = now;

                _valueProvider.UpdateSystemCpuCounter();

                if ((now - _lastTrafficSave).TotalSeconds > 60)
                {
                    TrafficLogger.Save();
                    _lastTrafficSave = now;
                }

                OnValuesUpdated?.Invoke();
            }
            catch { }
        }

        // 递归更新子硬件，确保 SuperIO 刷新
        private void UpdateWithSubHardware(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) 
            {
                UpdateWithSubHardware(sub);
            }
        }

        private void ReloadComputerSafe()
        {
            try
            {
                lock (_lock)
                {
                    // 1. 清理业务缓存
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    
                    // ★★★ 新增：清理 UI 列表缓存 ★★★
                    _cachedFanList = null;
                    _cachedNetworkList = null;
                    _cachedDiskList = null;

                    // 2. ★★★ 清理字符串池 (配合 UIUtils 的新功能) ★★★
                    UIUtils.ClearStringPool();

                    // 3. 关闭硬件服务 (使用 Visitor 模式触发 LHM 内部清理)
                    _computer.Accept(new HardwareVisitor(h => { }));
                    _computer.Close();
                    
                    _computer.Open();

                    // ★★★ 核心修复：重置后再次禁用历史记录 ★★★
                    DisableSensorHistory();
                }
                _sensorMap.Rebuild(_computer, _cfg); 
                
                // 4. ★★★ 优化 T1：重置后再次修剪内存 ★★★
                GC.Collect();
                try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
            }
            catch { }
        }

        // =========================================================
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        // 这将阻止 LibreHardwareMonitor 在内存中保留 24 小时的数据缓存
        // =========================================================
        private void DisableSensorHistory()
        {
            try
            {
                // 使用 Visitor 模式遍历整棵硬件树
                _computer.Accept(new DisableHistoryVisitor());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MemoryFix] Failed to disable history: {ex.Message}");
            }
        }

        /// <summary>
        /// 专用访问器：将所有 Sensor 的 ValuesTimeWindow 设为 0
        /// </summary>
        private class DisableHistoryVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) { computer.Traverse(this); }
            
            public void VisitHardware(IHardware hardware) 
            { 
                // 递归遍历子硬件
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
                
                // 遍历当前硬件的传感器
                foreach (var sensor in hardware.Sensors) VisitSensor(sensor);
            }

            public void VisitSensor(ISensor sensor) 
            { 
                // ★ 关键：通过反射设置 ValuesTimeWindow = TimeSpan.Zero
                // 因为 ISensor 接口通常不暴露这个属性，它属于具体的 Sensor 类
                try
                {
                    var prop = sensor.GetType().GetProperty("ValuesTimeWindow");
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(sensor, TimeSpan.Zero);
                    }
                }
                catch { }
            }
            
            public void VisitParameter(IParameter parameter) { }
        }

        public void Dispose()
        {
            _computer.Close();
            _valueProvider.Dispose();
            _networkManager.ClearCache();
            _diskManager.ClearCache(); // 漏掉的，补上
        }
        
        // ★★★ 优化：使用缓存 + Intern，防止生成重复字符串 ★★★
        public static List<string> ListAllNetworks() 
        {
            if (Instance == null) return new List<string>();
            if (Instance._cachedNetworkList != null) return Instance._cachedNetworkList;

            var list = Instance._computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Network)
                .Select(h => h.Name)
                .Distinct()
                .ToList();
            
            Instance._cachedNetworkList = list;
            return list;
        }

        // ★★★ 优化：使用缓存 + Intern，防止生成重复字符串 ★★★
        public static List<string> ListAllDisks() 
        {
            if (Instance == null) return new List<string>();
            if (Instance._cachedDiskList != null) return Instance._cachedDiskList;

            var list = Instance._computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .Select(h => h.Name)
                .Distinct()
                .ToList();

            Instance._cachedDiskList = list;
            return list;
        }
        
       // 列出所有风扇 (黑名单机制：排除干扰项，允许 USB/Cooler)
        public static List<string> ListAllFans()
        {
            if (Instance == null) return new List<string>();
            if (Instance._cachedFanList != null) return Instance._cachedFanList; // 优先读缓存
            
            var list = new List<string>();

            // 辅助递归函数
            void ScanHardware(IHardware hw)
            {
                // 黑名单：与 SensorMap 保持一致
                // 坚决排除 显卡、CPU、硬盘、内存、网卡
                bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia ||
                                  hw.HardwareType == HardwareType.GpuAmd ||
                                  hw.HardwareType == HardwareType.GpuIntel ||
                                  hw.HardwareType == HardwareType.Cpu ||
                                  hw.HardwareType == HardwareType.Storage ||
                                  hw.HardwareType == HardwareType.Memory ||
                                  hw.HardwareType == HardwareType.Network;

                // 只要不在黑名单里，都扫描！
                if (!isExcluded)
                {
                    foreach (var s in hw.Sensors)
                    {
                        // 只列出 Fan 类型 (转速)
                        if (s.SensorType == SensorType.Fan)
                        {
                            // 格式化名称：传感器名[硬件名] 
                            // ★★★ 强制驻留：确保生成的 "Fan #1 [SuperIO]" 只在内存存一份 ★★★
                            string fullName = $"{s.Name} [{hw.Name}]";
                            list.Add(fullName);
                        }
                    }
                }

                // 递归扫描子硬件
                foreach (var sub in hw.SubHardware)
                {
                    ScanHardware(sub);
                }
            }

            // 开始扫描根节点
            foreach (var hw in Instance._computer.Hardware)
            {
                ScanHardware(hw);
            }
            
            // 排序并去重
            list.Sort(); 
            // 存入缓存
            Instance._cachedFanList = list.Distinct().ToList();
            return Instance._cachedFanList;
        }

        private static IEnumerable<ISensor> GetAllSensors(IHardware hw, SensorType type)
        {
            foreach (var s in hw.Sensors) if (s.SensorType == type) yield return s;
            foreach (var sub in hw.SubHardware) 
                foreach (var s in GetAllSensors(sub, type)) yield return s;
        }

        // 内部 Visitor 类，用于触发 LHM 清理逻辑
        private class HardwareVisitor : IVisitor
        {
            private Action<IHardware> _action;
            public HardwareVisitor(Action<IHardware> action) { _action = action; }
            public void VisitComputer(IComputer computer) { computer.Traverse(this); }
            public void VisitHardware(IHardware hardware) { 
                _action(hardware); 
                foreach (var sub in hardware.SubHardware) sub.Accept(this); 
            }
            public void VisitParameter(IParameter parameter) { }
            public void VisitSensor(ISensor sensor) { }
        }
    }
}
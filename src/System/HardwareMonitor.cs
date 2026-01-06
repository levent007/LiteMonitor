using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using System.Linq; // 确保引用

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

        private DateTime _lastTrafficTime = DateTime.Now;
        private DateTime _lastTrafficSave = DateTime.Now;
        private DateTime _startTime = DateTime.Now;
        private DateTime _lastSlowScan = DateTime.Now;
        private DateTime _lastDiskBgScan = DateTime.Now;

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

            // 初始化子服务
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager();
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, _computer, ReloadComputerSafe);
            _valueProvider = new HardwareValueProvider(_computer, cfg, _sensorMap, _networkManager, _diskManager, _lock, _lastValidMap);

            Task.Run(async () =>
            {
                try
                {
                    _computer.Open();
                    _sensorMap.Rebuild(_computer);
                    await _driverInstaller.SmartCheckDriver();
                }
                catch { }
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

                bool isSlowScanTick = (now - _lastSlowScan).TotalSeconds > 3;
                bool needDiskBgScan = (now - _lastDiskBgScan).TotalSeconds > 10;

                lock (_lock)
                {
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

        private void ReloadComputerSafe()
        {
            try
            {
                lock (_lock)
                {
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    _computer.Close();
                    _computer.Open();
                }
                _sensorMap.Rebuild(_computer);
            }
            catch { }
        }

        public void Dispose()
        {
            _computer.Close();
            _valueProvider.Dispose();
            _networkManager.ClearCache();
            _diskManager.ClearCache(); // 漏掉的，补上
        }
        
        // 静态辅助方法 (UI用)
        public static List<string> ListAllNetworks() => Instance?._computer.Hardware.Where(h => h.HardwareType == HardwareType.Network).Select(h => h.Name).Distinct().ToList() ?? new List<string>();
        public static List<string> ListAllDisks() => Instance?._computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).Select(h => h.Name).Distinct().ToList() ?? new List<string>();
    }
}
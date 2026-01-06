using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public class HardwareValueProvider : IDisposable
    {
        private readonly Computer _computer;
        private readonly Settings _cfg;
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly object _lock;
        private readonly Dictionary<string, float> _lastValidMap; // 引用 HardwareMonitor 的 map

        // 系统计数器
        private PerformanceCounter? _cpuPerfCounter;
        private float _lastSystemCpuLoad = 0f;

        public HardwareValueProvider(Computer c, Settings s, SensorMap map, NetworkManager net, DiskManager disk, object syncLock, Dictionary<string, float> lastValid)
        {
            _computer = c;
            _cfg = s;
            _sensorMap = map;
            _networkManager = net;
            _diskManager = disk;
            _lock = syncLock;
            _lastValidMap = lastValid;
        }

        public void UpdateSystemCpuCounter()
        {
            // ★★★ [新增] 更新系统 CPU 计数器 (算法增强版) ★★★
            if (_cfg.UseSystemCpuLoad)
            {
                if (_cpuPerfCounter == null)
                {
                    try { _cpuPerfCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); }
                    catch 
                    {
                        try { _cpuPerfCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
                        catch { }
                    }
                    if (_cpuPerfCounter != null) _cpuPerfCounter.NextValue();
                }

                if (_cpuPerfCounter != null)
                {
                    try
                    {
                        float rawVal = _cpuPerfCounter.NextValue();
                        if (rawVal > 100f) rawVal = 100f;
                        _lastSystemCpuLoad = rawVal;
                    }
                    catch { _cpuPerfCounter.Dispose(); _cpuPerfCounter = null; }
                }
            }
            else
            {
                if (_cpuPerfCounter != null) { _cpuPerfCounter.Dispose(); _cpuPerfCounter = null; }
            }
        }

        // ===========================================================
        // ===================== 公共取值入口 =========================
        // ===========================================================
        public float? GetValue(string key)
        {
            _sensorMap.EnsureFresh(_computer);

            // ★★★ [新增] 拦截 CPU.Load 请求 ★★★
            if (key == "CPU.Load")
            {
                // 1. 系统计数器模式
                if (_cfg.UseSystemCpuLoad) return _lastSystemCpuLoad;

                // 2. 手动聚合核心负载 (解决 0.8% 问题)
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    double totalLoad = 0;
                    int coreCount = 0;
                    foreach (var s in cpu.Sensors)
                    {
                        if (s.SensorType != SensorType.Load) continue;
                        // 严格过滤策略
                        if (SensorMap.Has(s.Name, "Core") && SensorMap.Has(s.Name, "#") && 
                            !SensorMap.Has(s.Name, "Total") && !SensorMap.Has(s.Name, "SOC") && 
                            !SensorMap.Has(s.Name, "Max") && !SensorMap.Has(s.Name, "Average"))
                        {
                            if (s.Value.HasValue) { totalLoad += s.Value.Value; coreCount++; }
                        }
                    }
                    if (coreCount > 0) return (float)(totalLoad / coreCount);
                }
                
                // 3. 兜底策略
                lock (_lock) { if (_sensorMap.TryGetSensor("CPU.Load", out var s) && s.Value.HasValue) return s.Value.Value; }
                return 0f;
            }

            // ★★★ [终极修复] CPU.Temp 智能取最大值 ★★★
            if (key == "CPU.Temp")
            {
                float maxTemp = -1000f;
                bool found = false;
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    foreach (var s in cpu.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!s.Value.HasValue || s.Value.Value <= 0) continue;
                        if (SensorMap.Has(s.Name, "Distance") || SensorMap.Has(s.Name, "Average") || SensorMap.Has(s.Name, "Max")) continue;
                        if (s.Value.Value > maxTemp) { maxTemp = s.Value.Value; found = true; }
                    }
                }
                if (found) return maxTemp;
                lock (_lock) { if (_sensorMap.TryGetSensor("CPU.Temp", out var s) && s.Value.HasValue) return s.Value.Value; }
                return 0f;
            }

            // 1. 网络与磁盘
            if (key.StartsWith("NET")) return _networkManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
            if (key.StartsWith("DISK")) return _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);

            // ★★★ [新增] 获取今日流量 ★★★
            if (key == "DATA.DayUp") return TrafficLogger.GetTodayStats().up;
            if (key == "DATA.DayDown") return TrafficLogger.GetTodayStats().down;

            // 2. 频率与功耗 (复合计算逻辑)
            if (key.Contains("Clock") || key.Contains("Power")) return GetCompositeValue(key);

            // ★ 修改：增强的内存计算逻辑
            if (key == "MEM.Load")
            {
                if (Settings.DetectedRamTotalGB <= 0)
                {
                    lock (_lock)
                    {
                        if (_sensorMap.TryGetSensor("MEM.Used", out var u) && _sensorMap.TryGetSensor("MEM.Available", out var a))
                        {
                            if (u.Value.HasValue && a.Value.HasValue)
                            {
                                float rawTotal = u.Value.Value + a.Value.Value;
                                Settings.DetectedRamTotalGB = rawTotal > 512.0f ? rawTotal / 1024.0f : rawTotal;
                            }
                        }
                    }
                }
            }

            // 3. 显存百分比 (特殊计算)
            if (key == "GPU.VRAM")
            {
                float? used = GetValue("GPU.VRAM.Used");
                float? total = GetValue("GPU.VRAM.Total");
                if (used.HasValue && total.HasValue && total > 0)
                {
                    if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                    if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                    return used / total * 100f;
                }
                lock (_lock) { if (_sensorMap.TryGetSensor("GPU.VRAM.Load", out var s) && s.Value.HasValue) return s.Value; }
                return null;
            }

            // 4. 普通传感器 (直接读字典)
            lock (_lock)
            {
                if (_sensorMap.TryGetSensor(key, out var sensor))
                {
                    var val = sensor.Value;
                    if (val.HasValue && !float.IsNaN(val.Value)) { _lastValidMap[key] = val.Value; return val.Value; }
                    if (_lastValidMap.TryGetValue(key, out var last)) return last;
                }
            }
            return null;
        }

        // ===========================================================
        // ========= [核心算法] CPU/GPU 频率功耗复合计算 ==============
        // ===========================================================
        private float? GetCompositeValue(string key)
        {
            if (key == "CPU.Clock")
            {
                if (_sensorMap.CpuCoreCache.Count == 0) return null;
                double sum = 0; int count = 0; float maxRaw = 0;
                float correctionFactor = 1.0f;
                // Zen 5 修正
                if (_sensorMap.CpuBusSpeedSensor != null && _sensorMap.CpuBusSpeedSensor.Value.HasValue)
                {
                    float bus = _sensorMap.CpuBusSpeedSensor.Value.Value;
                    if (bus > 1.0f && bus < 20.0f) { float factor = 100.0f / bus; if (factor > 2.0f && factor < 10.0f) correctionFactor = factor; }
                }

                foreach (var core in _sensorMap.CpuCoreCache)
                {
                    if (core.Clock == null || !core.Clock.Value.HasValue) continue;
                    float clk = core.Clock.Value.Value * correctionFactor;
                    if (clk > maxRaw) maxRaw = clk;
                    // ★★★ 核心逻辑：只过滤明显错误的极低值 ★★★
                    if (clk > 400f) { sum += clk; count++; }
                }
                if (maxRaw > 0) _cfg.UpdateMaxRecord(key, maxRaw);
                if (count > 0) return (float)(sum / count);
                return maxRaw;
            }
            
            if (key == "CPU.Power")
            {
                lock (_lock) { if (_sensorMap.TryGetSensor("CPU.Power", out var s) && s.Value.HasValue) { _cfg.UpdateMaxRecord(key, s.Value.Value); return s.Value.Value; } }
                return null;
            }

            if (key.StartsWith("GPU"))
            {
                var gpu = _sensorMap.CachedGpu;
                if (gpu == null) return null;

                if (key == "GPU.Clock")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    // ★★★ 【修复 1】频率异常过滤 ★★★
                    if (s != null && s.Value.HasValue) { float val = s.Value.Value; if (val > 6000.0f) return null; _cfg.UpdateMaxRecord(key, val); return val; }
                }
                else if (key == "GPU.Power")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    // ★★★ 【修复 2】功耗异常过滤 ★★★
                    if (s != null && s.Value.HasValue) { float val = s.Value.Value; if (val > 2000.0f) return null; _cfg.UpdateMaxRecord(key, val); return val; }
                }
            }
            return null;
        }

        public void Dispose()
        {
            _cpuPerfCounter?.Dispose();
        }
    }
}
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
        private readonly Dictionary<string, float> _lastValidMap; 

        // 系统计数器
        private PerformanceCounter? _cpuPerfCounter;
        private float _lastSystemCpuLoad = 0f;

        // ★★★ [新增] 错误重试计数器，防止无限重建 ★★★
        private int _perfCounterErrorCount = 0;
        private DateTime _lastPerfCounterRetry = DateTime.MinValue;

        // ★★★ [新增] Tick 级智能缓存 (防止同帧重复计算) ★★★
        private readonly Dictionary<string, float> _tickCache = new();

        // ★★★ [终极优化] 对象级缓存：(Sensor对象, 配置来源字符串) ★★★
        // 缓存住找到的 ISensor 对象，彻底消除每秒的字符串解析和遍历开销
        private readonly Dictionary<string, (ISensor Sensor, string ConfigSource)> _manualSensorCache = new();

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

        // ★★★ [新增] 清空缓存（当硬件重启时必须调用） ★★★
        public void ClearCache()
        {
            _manualSensorCache.Clear();
            _tickCache.Clear();
        }

        public void UpdateSystemCpuCounter()
        {
            // ★★★ [新增] 每一轮更新开始时，清空本轮缓存 ★★★
            _tickCache.Clear();

            if (_cfg.UseSystemCpuLoad)
            {
                // ★★★ [优化] 智能重试机制：失败 10 次后，每 30 秒才重试一次 ★★★
                if (_cpuPerfCounter == null)
                {
                    if (_perfCounterErrorCount > 10 && (DateTime.Now - _lastPerfCounterRetry).TotalSeconds < 30)
                        return; // 冷却中，跳过

                    try 
                    { 
                        _cpuPerfCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); 
                        _lastPerfCounterRetry = DateTime.Now;
                    }
                    catch 
                    {
                        try 
                        { 
                            _cpuPerfCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); 
                            _lastPerfCounterRetry = DateTime.Now;
                        }
                        catch { _perfCounterErrorCount++; }
                    }
                    if (_cpuPerfCounter != null) 
                    {
                         try { _cpuPerfCounter.NextValue(); _perfCounterErrorCount = 0; } catch { }
                    }
                }

                if (_cpuPerfCounter != null)
                {
                    try
                    {
                        float rawVal = _cpuPerfCounter.NextValue();
                        if (rawVal > 100f) rawVal = 100f;
                        _lastSystemCpuLoad = rawVal;
                        _perfCounterErrorCount = 0; // 成功则重置错误计数
                    }
                    catch 
                    { 
                        _cpuPerfCounter.Dispose(); 
                        _cpuPerfCounter = null; 
                        _perfCounterErrorCount++; 
                    }
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
            // ★★★ [新增 3] 优先查缓存，如果本帧算过，直接返回 ★★★
            if (_tickCache.TryGetValue(key, out float cachedVal)) return cachedVal;

            _sensorMap.EnsureFresh(_computer, _cfg);

            // 定义临时结果变量
            float? result = null;

            // ★★★ [终极优化] 使用 switch 替代 if-else 链，实现 O(1) 哈希跳转 ★★★
            switch (key)
            {
                // 1. CPU.Load
                case "CPU.Load":
                    if (_cfg.UseSystemCpuLoad)
                    {
                        result = _lastSystemCpuLoad;
                    }
                    else
                    {
                        // 手动聚合
                        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                        if (cpu != null)
                        {
                            double totalLoad = 0;
                            int coreCount = 0;
                            foreach (var s in cpu.Sensors)
                            {
                                if (s.SensorType != SensorType.Load) continue;
                                if (SensorMap.Has(s.Name, "Core") && SensorMap.Has(s.Name, "#") && 
                                    !SensorMap.Has(s.Name, "Total") && !SensorMap.Has(s.Name, "SOC") && 
                                    !SensorMap.Has(s.Name, "Max") && !SensorMap.Has(s.Name, "Average"))
                                {
                                    if (s.Value.HasValue) { totalLoad += s.Value.Value; coreCount++; }
                                }
                            }
                            if (coreCount > 0) result = (float)(totalLoad / coreCount);
                        }
                        
                        // 兜底
                        if (result == null)
                        {
                            lock (_lock) { if (_sensorMap.TryGetSensor("CPU.Load", out var s) && s.Value.HasValue) result = s.Value.Value; }
                        }
                        // 如果还是没值，默认为 0
                        if (result == null) result = 0f;
                    }
                    break;

                // 2. CPU.Temp
                case "CPU.Temp":
                    float maxTemp = -1000f;
                    bool found = false;
                    var cpuT = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                    if (cpuT != null)
                    {
                        foreach (var s in cpuT.Sensors)
                        {
                            if (s.SensorType != SensorType.Temperature) continue;
                            if (!s.Value.HasValue || s.Value.Value <= 0) continue;
                            if (SensorMap.Has(s.Name, "Distance") || SensorMap.Has(s.Name, "Average") || SensorMap.Has(s.Name, "Max")) continue;
                            if (s.Value.Value > maxTemp) { maxTemp = s.Value.Value; found = true; }
                        }
                    }
                    if (found) result = maxTemp;
                    
                    if (result == null)
                    {
                        lock (_lock) { if (_sensorMap.TryGetSensor("CPU.Temp", out var s) && s.Value.HasValue) result = s.Value.Value; }
                    }
                    if (result == null) result = 0f;
                    break;

                // 4. 每日流量
                case "DATA.DayUp":
                    result = TrafficLogger.GetTodayStats().up;
                    break;
                case "DATA.DayDown":
                    result = TrafficLogger.GetTodayStats().down;
                    break;

                // 6. 内存
                case "MEM.Load":
                    // 检测总内存逻辑
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
                    // 下面 break 后会走到通用传感器逻辑去取值
                    break;

                // 7. 显存
                case "GPU.VRAM":
                    // 注意：这里递归调用了 GetValue，会用到缓存，非常高效
                    float? used = GetValue("GPU.VRAM.Used");
                    float? total = GetValue("GPU.VRAM.Total");
                    if (used.HasValue && total.HasValue && total > 0)
                    {
                        if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                        // 单位转换
                        if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                        result = used / total * 100f;
                    }
                    else
                    {
                        lock (_lock) { if (_sensorMap.TryGetSensor("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value; }
                    }
                    break;

                // 8. 风扇/泵 (带 Max 记录) + [终极优化：缓存优先 -> 急速反向查找]
                case "CPU.Fan":
                case "CPU.Pump":
                case "CASE.Fan":
                case "GPU.Fan":
                    string prefFan = "";
                    if (key == "CPU.Fan") prefFan = _cfg.PreferredCpuFan;
                    else if (key == "CPU.Pump") prefFan = _cfg.PreferredCpuPump;
                    else if (key == "CASE.Fan") prefFan = _cfg.PreferredCaseFan;

                    // A. 查对象缓存 (O(1) 访问)
                    bool foundFan = false;
                    if (_manualSensorCache.TryGetValue(key, out var cachedFan))
                    {
                        if (cachedFan.ConfigSource == prefFan) // 校验配置未变
                        {
                            result = cachedFan.Sensor.Value;
                            foundFan = true;
                        }
                    }

                    // B. 缓存失效，执行急速反向查找
                    if (!foundFan)
                    {
                        ISensor? s = FindSensorReverse(prefFan, SensorType.Fan);
                        if (s != null)
                        {
                            _manualSensorCache[key] = (s, prefFan); // 更新缓存
                            result = s.Value;
                        }
                        else 
                        {
                            // 没找到，走自动
                            lock (_lock)
                            {
                                if (_sensorMap.TryGetSensor(key, out var autoS) && autoS.Value.HasValue)
                                    result = autoS.Value.Value;
                            }
                        }
                    }
                    if (result.HasValue) _cfg.UpdateMaxRecord(key, result.Value);
                    break;

                // [插入/修改逻辑] 主板温度 (缓存 + 急速查找)
                case "MOBO.Temp":
                    string prefMobo = _cfg.PreferredMoboTemp;
                    bool foundMobo = false;

                    // A. 查缓存
                    if (_manualSensorCache.TryGetValue(key, out var cachedMobo))
                    {
                        if (cachedMobo.ConfigSource == prefMobo)
                        {
                            result = cachedMobo.Sensor.Value;
                            foundMobo = true;
                        }
                    }

                    // B. 查找并更新
                    if (!foundMobo)
                    {
                        ISensor? s = FindSensorReverse(prefMobo, SensorType.Temperature);
                        if (s != null)
                        {
                            _manualSensorCache[key] = (s, prefMobo);
                            result = s.Value;
                        }
                    }
                    // 没找到则 break，走下方通用兜底
                    break;

                // 默认分支：处理模糊匹配 (StartsWith/Contains)
                default:
                    // 3. 网络与磁盘
                    if (key.StartsWith("NET"))
                    {
                        result = _networkManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                    }
                    else if (key.StartsWith("DISK"))
                    {
                        result = _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                    }
                    // 5. 频率与功耗
                    else if (key.Contains("Clock") || key.Contains("Power"))
                    {
                        result = GetCompositeValue(key);
                    }
                    break;
            }

            // 10. 通用传感器查找 (兜底)
            if (result == null)
            {
                lock (_lock)
                {
                    if (_sensorMap.TryGetSensor(key, out var sensor))
                    {
                        var val = sensor.Value;
                        if (val.HasValue && !float.IsNaN(val.Value)) 
                        { 
                            _lastValidMap[key] = val.Value; 
                            result = val.Value; 
                        }
                        else if (_lastValidMap.TryGetValue(key, out var last))
                        {
                            result = last;
                        }
                    }
                }
            }

            // ★★★ [新增 4] 写入缓存并返回 ★★★
            if (result.HasValue)
            {
                _tickCache[key] = result.Value;
                return result.Value;
            }

            return null;
        }

        // =====================================================================
        // ★★★ [急速反向查找] 解析父级名 -> 定位根硬件 -> 查找分支 ★★★
        // 只在配置改变或启动时运行一次，随后进入缓存
        // =====================================================================
        private ISensor? FindSensorReverse(string savedString, SensorType type)
        {
            if (string.IsNullOrEmpty(savedString) || savedString.Contains("Auto") || savedString.Contains("自动")) 
                return null;

            int idx = savedString.LastIndexOf('[');
            if (idx < 0) return null; 

            // 预处理字符串
            string targetSensorName = savedString.Substring(0, idx).Trim();
            string targetHardwareName = savedString.Substring(idx + 1).TrimEnd(']');

            // 局部递归
            ISensor? SearchBranch(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == type && s.Name == targetSensorName)
                        return s; 
                }
                foreach (var sub in h.SubHardware)
                {
                    var s = SearchBranch(sub);
                    if (s != null) return s;
                }
                return null;
            }

            // 极速定位：只遍历 5-10 个根节点
            foreach (var hw in _computer.Hardware)
            {
                if (hw.Name == targetHardwareName)
                {
                    return SearchBranch(hw);
                }
            }

            return null;
        }

        // ===========================================================
        // ========= [核心算法] CPU/GPU 频率功耗复合计算 ==============
        // ===========================================================
        // ... (GetCompositeValue 方法保持不变) ...
        private float? GetCompositeValue(string key)
        {
            // 代码无需修改，上面的逻辑已经通过 GetValue 调用到了这里
            // 这里为了节省篇幅省略，请保留你原有的 GetCompositeValue 代码
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
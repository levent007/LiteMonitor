using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.System
{
    // 依然是 HardwareMonitor 的一部分
    public sealed partial class HardwareMonitor
    {
        // ===========================================================
        // ===================== 公共取值入口 =========================
        // ===========================================================
        public float? Get(string key)
        {
            EnsureMapFresh();

            // 1. 网络与磁盘 (独立逻辑)
            switch (key)
            {
                case "NET.Up": case "NET.Down": return GetNetworkValue(key);
                case "DISK.Read": case "DISK.Write": return GetDiskValue(key);
            }

            // 2. 频率与功耗 (复合计算逻辑)
            if (key.Contains("Clock") || key.Contains("Power"))
            {
                return GetHardwareCompositeValue(key);
            }

            // 3. 显存百分比 (特殊计算)
            if (key == "GPU.VRAM")
            {
                float? used = Get("GPU.VRAM.Used");
                float? total = Get("GPU.VRAM.Total");
                if (used.HasValue && total.HasValue && total > 0)
                {
                    // 简单单位换算防止数值过大溢出 (虽 float 够用，但为了逻辑统一)
                    if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                    return used / total * 100f;
                }
                // Fallback: 如果有 Load 传感器直接用
                lock (_lock) { if (_map.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) return s.Value; }
                return null;
            }

            // 4. 普通传感器 (直接读字典)
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var sensor))
                {
                    var val = sensor.Value;
                    if (val.HasValue && !float.IsNaN(val.Value))
                    {
                        _lastValid[key] = val.Value;
                        return val.Value;
                    }
                    if (_lastValid.TryGetValue(key, out var last)) return last;
                }
            }

            return null;
        }

        // ===========================================================
        // ========= [核心算法] CPU/GPU 频率功耗复合计算 ==============
        // ===========================================================
        private float? GetHardwareCompositeValue(string key)
        {
            // --- CPU 频率：加权平均算法 ---
            if (key == "CPU.Clock")
            {
                if (_cpuCoreCache.Count == 0) return null;

                double weightedSum = 0;
                double totalLoad = 0;
                float maxRawClock = 0;

                // 遍历缓存，零 GC，极速
                foreach (var core in _cpuCoreCache)
                {
                    if (core.Clock == null || !core.Clock.Value.HasValue) continue;

                    float clk = core.Clock.Value.Value;
                    if (clk > maxRawClock) maxRawClock = clk;

                    // 加权逻辑
                    if (core.Load != null && core.Load.Value.HasValue)
                    {
                        float ld = core.Load.Value.Value;
                        weightedSum += clk * ld;
                        totalLoad += ld;
                    }
                }

                // 记录物理最高频 (用于颜色自适应)
                if (maxRawClock > 0) _cfg.UpdateMaxRecord(key, maxRawClock);

                // 待机处理：若无负载，返回 0 或最小频率
                if (totalLoad <= 0.001) return 0;

                return (float)(weightedSum / totalLoad);
            }

            // --- CPU 功耗：直接读取或回落 ---
            if (key == "CPU.Power")
            {
                // 优先从 Map 读 (NormalizeKey 已处理 Package 映射)
                lock (_lock)
                {
                    if (_map.TryGetValue("CPU.Power", out var s) && s.Value.HasValue)
                    {
                        _cfg.UpdateMaxRecord(key, s.Value.Value);
                        return s.Value.Value;
                    }
                }
                return null;
            }

            // --- GPU 频率/功耗：使用显卡缓存 ---
            if (key.StartsWith("GPU"))
            {
                if (_cachedGpu == null) return null;

                ISensor? s = null;
                // 注意：GPU 传感器少，LINQ 查询开销可忽略
                if (key == "GPU.Clock")
                    // ★★★ 优化：增加 "shader" 匹配 ★★★
                    s = _cachedGpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && 
                        (Has(x.Name, "graphics") || Has(x.Name, "core") || Has(x.Name, "shader")));
                
                else if (key == "GPU.Power")
                    // ★★★ 优化：增加 "core" 匹配 (修复 B580 等显卡) ★★★
                    s = _cachedGpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && 
                        (Has(x.Name, "package") || Has(x.Name, "ppt") || Has(x.Name, "board") || Has(x.Name, "core")) || Has(x.Name, "gpu") );

                if (s != null && s.Value.HasValue)
                {
                    _cfg.UpdateMaxRecord(key, s.Value.Value);
                    return s.Value.Value;
                }
            }

            return null;
        }

        // ===========================================================
        // ==================== 网络 (Network) =======================
        // ===========================================================
        private float? GetNetworkValue(string key)
        {
            // 1. 优先手动指定
            if (!string.IsNullOrWhiteSpace(_cfg.PreferredNetwork))
            {
                var hw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network && h.Name.Equals(_cfg.PreferredNetwork, StringComparison.OrdinalIgnoreCase));
                if (hw != null) return ReadNetworkSensor(hw, key);
            }

            // 2. 自动选优 (带缓存)
            return GetBestNetworkValue(key);
        }

        private float? GetBestNetworkValue(string key)
        {
            // A. 尝试运行时缓存
            if (_cachedNetHw != null)
            {
                float? cachedVal = ReadNetworkSensor(_cachedNetHw, key);
                // 逻辑优化：
                // 1. 如果有流量，直接用。
                // 2. 如果没流量，但距离上次全盘扫描还不到 3 秒 (配合 UpdateAll 的节奏)，也直接用 0。
                //    不要每秒都去扫，那样太耗 CPU。
                if ((cachedVal.HasValue && cachedVal.Value > 0.1f) || 
                    (DateTime.Now - _lastNetScan).TotalSeconds < 3) 
                {
                    return cachedVal;
                }
                // 如果超过 3 秒还是没流量，说明可能切网卡了，放行到底部去全盘扫描
            }

            // ★★★ [漏掉的部分] B. 尝试启动时缓存 (Settings 中的记录) ★★★
            // 确保 Settings.cs 里已经定义了 public string LastAutoNetwork { get; set; } = "";
            if (_cachedNetHw == null && !string.IsNullOrEmpty(_cfg.LastAutoNetwork))
            {
                // 尝试直接找上次记住的网卡
                var savedHw = _computer.Hardware.FirstOrDefault(h => h.Name == _cfg.LastAutoNetwork);
                if (savedHw != null)
                {
                    // 找到了！直接设为缓存，跳过全盘扫描
                    _cachedNetHw = savedHw;
                    _lastNetScan = DateTime.Now;
                    return ReadNetworkSensor(savedHw, key);
                }
            }

            // C. 全盘扫描 (代码保持不变)
            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
            {
                // ... (你的原有扫描逻辑) ...
                // ... (复制你文件里 foreach 的内容) ...
                double penalty = _virtualNicKW.Any(k => Has(hw.Name, k)) ? -1e9 : 0;
                ISensor? up = null, down = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => Has(s.Name, k))) up ??= s;
                    if (_downKW.Any(k => Has(s.Name, k))) down ??= s;
                }
                if (up == null && down == null) continue;
                double score = (up?.Value ?? 0) + (down?.Value ?? 0) + penalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "NET.Up") ? up : down;
                }
            }

            // D. 更新缓存
            if (bestHw != null)
            {
                _cachedNetHw = bestHw;
                _lastNetScan = DateTime.Now;
                
                // ★★★ [漏掉的部分] 记住这次的选择 ★★★
                if (_cfg.LastAutoNetwork != bestHw.Name)
                {
                    _cfg.LastAutoNetwork = bestHw.Name;
                }
            }

            // ... (返回结果部分保持不变) ...
            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (_lock) _lastValid[key] = v;
                return v;
            }
            lock (_lock) { if (_lastValid.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private float? ReadNetworkSensor(IHardware hw, string key)
        {
            ISensor? target = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "NET.Up" && _upKW.Any(k => Has(s.Name, k))) { target = s; break; } // 找到即停
                if (key == "NET.Down" && _downKW.Any(k => Has(s.Name, k))) { target = s; break; }
            }

            if (target?.Value is float v && !float.IsNaN(v))
            {
                lock (_lock) _lastValid[key] = v;
                return v;
            }
            lock (_lock) { if (_lastValid.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private static readonly string[] _upKW = { "upload", "up", "sent", "send", "tx", "transmit" };
        private static readonly string[] _downKW = { "download", "down", "received", "receive", "rx" };
        private static readonly string[] _virtualNicKW = { "virtual", "vmware", "hyper-v", "hyper v", "vbox", "loopback", "tunnel", "tap", "tun", "bluetooth", "zerotier", "tailscale", "wan miniport" };

        // ===========================================================
        // ===================== 磁盘 (Disk) =========================
        // ===========================================================
        private float? GetDiskValue(string key)
        {
            if (!string.IsNullOrWhiteSpace(_cfg.PreferredDisk))
            {
                var hw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Storage && h.Name.Equals(_cfg.PreferredDisk, StringComparison.OrdinalIgnoreCase));
                if (hw != null) return ReadDiskSensor(hw, key);
            }
            return GetBestDiskValue(key);
        }

        private float? GetBestDiskValue(string key)
        {
            // A. 尝试运行时缓存
            if (_cachedDiskHw != null)
            {
                float? cachedVal = ReadDiskSensor(_cachedDiskHw, key);
                // 有读写活动或冷却期内，直接返回
                if ((cachedVal.HasValue && cachedVal.Value > 0.1f) || (DateTime.Now - _lastDiskScan).TotalSeconds < 10)
                    return cachedVal;
            }

            // ★★★ [新增] B. 尝试启动时缓存 (Settings 记忆) ★★★
            if (_cachedDiskHw == null && !string.IsNullOrEmpty(_cfg.LastAutoDisk))
            {
                var savedHw = _computer.Hardware.FirstOrDefault(h => h.Name == _cfg.LastAutoDisk);
                if (savedHw != null)
                {
                    // 命中缓存！跳过全盘扫描
                    _cachedDiskHw = savedHw;
                    _lastDiskScan = DateTime.Now;
                    return ReadDiskSensor(savedHw, key);
                }
            }

            // C. 全盘扫描 (逻辑保持不变)
            string sysPrefix = "";
            try { sysPrefix = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2) ?? ""; } catch { }

            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
            {
                // ... (复制你原有的扫描逻辑) ...
                bool isSystem = !string.IsNullOrEmpty(sysPrefix) && (Has(hw.Name, sysPrefix) || hw.Sensors.Any(s => Has(s.Name, sysPrefix)));
                
                ISensor? read = null, write = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (Has(s.Name, "read")) read ??= s;
                    if (Has(s.Name, "write")) write ??= s;
                }

                if (read == null && write == null) continue;

                double score = (read?.Value ?? 0) + (write?.Value ?? 0);
                if (isSystem) score += 1e9; // 系统盘优先

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "DISK.Read") ? read : write;
                }
            }

            // D. 更新缓存
            if (bestHw != null)
            {
                _cachedDiskHw = bestHw;
                _lastDiskScan = DateTime.Now;
                
                // ★★★ [新增] 记住这次的选择 ★★★
                if (_cfg.LastAutoDisk != bestHw.Name)
                {
                    _cfg.LastAutoDisk = bestHw.Name;
                }
            }

            // E. 返回结果
            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (_lock) _lastValid[key] = v;
                return v;
            }
            lock (_lock) { if (_lastValid.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private float? ReadDiskSensor(IHardware hw, string key)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "DISK.Read" && Has(s.Name, "read")) return SafeRead(s, key);
                if (key == "DISK.Write" && Has(s.Name, "write")) return SafeRead(s, key);
            }
            return SafeRead(null, key);
        }

        private float? SafeRead(ISensor? s, string key)
        {
            if (s?.Value is float v && !float.IsNaN(v))
            {
                lock (_lock) _lastValid[key] = v;
                return v;
            }
            lock (_lock) { if (_lastValid.TryGetValue(key, out var last)) return last; }
            return null;
        }

        // ===========================================================
        // ================== 辅助 / 映射 (Helpers) ===================
        // ===========================================================
        
        // 静态工具：菜单使用
        public static List<string> ListAllNetworks() => Instance?._computer.Hardware.Where(h => h.HardwareType == HardwareType.Network).Select(h => h.Name).Distinct().ToList() ?? new List<string>();
        public static List<string> ListAllDisks() => Instance?._computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).Select(h => h.Name).Distinct().ToList() ?? new List<string>();

        // [重要] 传感器名称标准化映射
        private static string? NormalizeKey(IHardware hw, ISensor s)
        {
            string name = s.Name;
            var type = hw.HardwareType;

            // --- CPU ---
            if (type == HardwareType.Cpu)
            {
                if (s.SensorType == SensorType.Load && Has(name, "total")) return "CPU.Load";
                if (s.SensorType == SensorType.Temperature)
                {
                    if (Has(name, "package") || Has(name, "average") || Has(name, "cores")) return "CPU.Temp";
                    if (Has(name, "tctl") || Has(name, "tdie") || Has(name, "ccd")) return "CPU.Temp"; // <--- 增加了这里
                }
                if (s.SensorType == SensorType.Power && (Has(name, "package") || Has(name, "cores"))) return "CPU.Power";
                // 注意：Clock 不走 Map，走加权平均缓存，所以这里不需要映射
            }

            // --- GPU ---
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (s.SensorType == SensorType.Load && (Has(name, "core") || Has(name, "d3d 3d"))) return "GPU.Load";
                if (s.SensorType == SensorType.Temperature && (Has(name, "core") || Has(name, "hot spot") || Has(name, "soc") || Has(name, "vr"))) return "GPU.Temp";
                
                // VRAM
                if (s.SensorType == SensorType.SmallData && (Has(name, "memory") || Has(name, "dedicated")))
                {
                    if (Has(name, "used")) return "GPU.VRAM.Used";
                    if (Has(name, "total")) return "GPU.VRAM.Total";
                }
                if (s.SensorType == SensorType.Load && Has(name, "memory")) return "GPU.VRAM.Load";
            }

            // --- Memory ---
            if (type == HardwareType.Memory && s.SensorType == SensorType.Load && Has(name, "memory")) return "MEM.Load";

            return null;
        }

        private static bool Has(string source, string sub)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sub)) return false;
            return source.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
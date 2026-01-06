using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    public class DiskManager
    {
        // ★★★ [新增] 2. 记录每个硬盘最后一次活跃的时间 (用于判断是否深睡) ★★★
        private Dictionary<IHardware, DateTime> _diskLastActiveTime = new();
        // 磁盘智能缓存
        private IHardware? _cachedDiskHw;
        private DateTime _lastDiskScan = DateTime.MinValue;

        public void ClearCache()
        {
            _diskLastActiveTime.Clear();
            _cachedDiskHw = null;
        }

        // ===========================================================
        // 更新逻辑 (原 UpdateAll 中的部分)
        // ===========================================================
        public void ProcessUpdate(IHardware hw, Settings cfg, bool isSlowScanTick, bool needDiskBgScan)
        {
            // 1. 严格遵守首选磁盘锁定
            if (!string.IsNullOrEmpty(cfg.PreferredDisk) && 
                !hw.Name.Equals(cfg.PreferredDisk, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 初始化活跃时间记录
            if (!_diskLastActiveTime.ContainsKey(hw)) _diskLastActiveTime[hw] = DateTime.Now;

            // 判断是否是当前 UI 上显示的那个盘
            bool isTarget = (_cachedDiskHw != null && hw == _cachedDiskHw) ||
                            (hw.Name == cfg.LastAutoDisk) ||
                            (hw.Name == cfg.PreferredDisk);
            
            bool shouldUpdate = false;
            double idleMinutes = (DateTime.Now - _diskLastActiveTime[hw]).TotalMinutes;

            // === 🧠 智能退避核心逻辑 ===
            if (isTarget)
            {
                shouldUpdate = true;
            }
            else
            {
                // B. 如果是后台盘（比如你的 E 盘）：
                if (idleMinutes > 5) shouldUpdate = false; // [💤 深睡模式]
                else if (idleMinutes > 1) { if (needDiskBgScan) shouldUpdate = true; } // [❄️ 冷却模式]
                else { if (isSlowScanTick) shouldUpdate = true; } // [🔥 活跃模式]
            }

            // 执行更新
            if (shouldUpdate)
            {
                hw.Update();
                // ★★★ 检查是否有流量，如果有，重置活跃计时器 ★★★
                bool hasTraffic = false;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Throughput && s.Value.HasValue && s.Value.Value > 1024) // > 1KB/s
                    {
                        hasTraffic = true;
                        break;
                    }
                }
                if (hasTraffic) _diskLastActiveTime[hw] = DateTime.Now;
            }
        }

        // ===========================================================
        // 获取最佳磁盘数值 (原 Logic.cs 中的 GetDiskValue/GetBestDiskValue)
        // ===========================================================
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            if (!string.IsNullOrWhiteSpace(cfg.PreferredDisk))
            {
                var hw = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Storage && h.Name.Equals(cfg.PreferredDisk, StringComparison.OrdinalIgnoreCase));
                if (hw != null) return ReadDiskSensor(hw, key, lastValidMap, syncLock);
            }

            // 1. 运行时缓存
            if (_cachedDiskHw != null)
            {
                // ★★★ 【修复 2】存活检查：防止持有僵尸对象的引用 ★★★
                if (!computer.Hardware.Contains(_cachedDiskHw))
                {
                    _cachedDiskHw = null;
                }
                else
                {
                    float? cachedVal = ReadDiskSensor(_cachedDiskHw, key, lastValidMap, syncLock);
                    // 有读写活动或冷却期内，直接返回
                    if ((cachedVal.HasValue && cachedVal.Value > 0.1f) || (DateTime.Now - _lastDiskScan).TotalSeconds < 10)
                        return cachedVal;
                }
            }

            // ★★★ [新增] B. 尝试启动时缓存 (Settings 记忆) ★★★
            if (_cachedDiskHw == null && !string.IsNullOrEmpty(cfg.LastAutoDisk))
            {
                var savedHw = computer.Hardware.FirstOrDefault(h => h.Name == cfg.LastAutoDisk);
                if (savedHw != null)
                {
                    _cachedDiskHw = savedHw;
                    _lastDiskScan = DateTime.Now;
                    return ReadDiskSensor(savedHw, key, lastValidMap, syncLock);
                }
            }

            // C. 全盘扫描
            string sysPrefix = "";
            try { sysPrefix = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2) ?? ""; } catch { }

            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
            {
                bool isSystem = !string.IsNullOrEmpty(sysPrefix) && (SensorMap.Has(hw.Name, sysPrefix) || hw.Sensors.Any(s => SensorMap.Has(s.Name, sysPrefix)));
                ISensor? read = null, write = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (SensorMap.Has(s.Name, "read")) read ??= s;
                    if (SensorMap.Has(s.Name, "write")) write ??= s;
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
                if (cfg.LastAutoDisk != bestHw.Name)
                {
                    cfg.LastAutoDisk = bestHw.Name;
                }
            }

            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private float? ReadDiskSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "DISK.Read" && SensorMap.Has(s.Name, "read")) return SafeRead(s, key, lastValidMap, syncLock);
                if (key == "DISK.Write" && SensorMap.Has(s.Name, "write")) return SafeRead(s, key, lastValidMap, syncLock);
            }
            return SafeRead(null, key, lastValidMap, syncLock);
        }

        private float? SafeRead(ISensor? s, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            if (s?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }
    }
}
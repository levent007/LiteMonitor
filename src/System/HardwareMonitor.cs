using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.System
{
    public sealed class HardwareMonitor : IDisposable
    {
        private readonly Computer _computer;
        private readonly Dictionary<string, ISensor> _map = new();
        private readonly Dictionary<string, float> _lastValid = new();
        private DateTime _lastMapBuild = DateTime.MinValue;

        private readonly Settings _cfg;

        public static HardwareMonitor? Instance { get; private set; }

        public event Action? OnValuesUpdated;

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

            Task.Run(() =>
            {
                try
                {
                    _computer.Open();
                    BuildSensorMap();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[HardwareMonitor] init failed: " + ex.Message);
                }
            });
        }

        // ===========================================================
        // ========== Sensor Map 建立（CPU/GPU/MEM） ================
        // ===========================================================
        private void BuildSensorMap()
        {
            _map.Clear();
            foreach (var hw in _computer.Hardware)
                RegisterHardware(hw);
            _lastMapBuild = DateTime.Now;
        }

        private void RegisterHardware(IHardware hw)
        {
            hw.Update();

            foreach (var s in hw.Sensors)
            {
                string? key = NormalizeKey(hw, s);
                if (!string.IsNullOrEmpty(key) && !_map.ContainsKey(key))
                    _map[key] = s;
            }

            foreach (var sub in hw.SubHardware)
                RegisterHardware(sub);
        }

        private static string? NormalizeKey(IHardware hw, ISensor s)
        {
            string name = s.Name.ToLower();
            var type = hw.HardwareType;

            // ========== CPU ==========
            if (type == HardwareType.Cpu)
            {
                if (s.SensorType == SensorType.Load && name.Contains("total"))
                    return "CPU.Load";

                if (s.SensorType == SensorType.Temperature)
                {
                    if (name.Contains("average") || name.Contains("core average"))
                        return "CPU.Temp";
                    if (name.Contains("package") || name.Contains("tctl"))
                        return "CPU.Temp";
                }
            }

            // ========== GPU ==========
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (s.SensorType == SensorType.Temperature &&
                    (name.Contains("core") || name.Contains("hotspot")))
                    return "GPU.Temp";

                if (s.SensorType == SensorType.Load &&
                    (name.Contains("core") || name.Contains("gpu")))
                    return "GPU.Load";

                if (s.SensorType == SensorType.SmallData)
                {
                    if ((name.Contains("dedicated") || name.Contains("memory")) && name.Contains("used"))
                        return "GPU.VRAM.Used";
                    if ((name.Contains("dedicated") || name.Contains("memory")) && name.Contains("total"))
                        return "GPU.VRAM.Total";
                }

                if (s.SensorType == SensorType.Load && name.Contains("memory"))
                    return "GPU.VRAM.Load";
            }

            // ========== Memory ==========
            if (type == HardwareType.Memory)
            {
                if (s.SensorType == SensorType.Load && name.Contains("memory"))
                    return "MEM.Load";
            }

            return null;
        }

        private void EnsureMapFresh()
        {
            if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
                BuildSensorMap();
        }

        // ===========================================================
        // ===================== 核心 Get ============================
        // ===========================================================
        public float? Get(string key)
        {
            EnsureMapFresh();

            switch (key)
            {
                case "NET.Up":
                case "NET.Down":
                    return GetNetworkValue(key);

                case "DISK.Read":
                case "DISK.Write":
                    return GetDiskValue(key);
            }

            // ===== GPU VRAM 额外计算 =====
            if (key == "GPU.VRAM")
            {
                float? used = Get("GPU.VRAM.Used");
                float? total = Get("GPU.VRAM.Total");
                if (used.HasValue && total.HasValue && total > 0)
                {
                    if (total > 1024 * 1024 * 10)
                    {
                        used /= 1024f * 1024f;
                        total /= 1024f * 1024f;
                    }
                    return used / total * 100f;
                }
                if (_map.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue)
                    return s.Value;
            }

            // ===== 普通传感器 =====
            if (_map.TryGetValue(key, out var sensor))
            {
                var val = sensor.Value;
                if (val.HasValue && !float.IsNaN(val.Value))
                {
                    _lastValid[key] = val.Value;
                    return val.Value;
                }
                if (_lastValid.TryGetValue(key, out var last))
                    return last;
            }

            return null;
        }

        // ===========================================================
        // =============== 手动 / 自动 — 网卡 ========================
        // ===========================================================
        private float? GetNetworkValue(string key)
        {
            // ========== 手动模式 ==========
            if (!string.IsNullOrWhiteSpace(_cfg.PreferredNetwork))
            {
                var hw = _computer.Hardware
                    .FirstOrDefault(h =>
                        h.HardwareType == HardwareType.Network &&
                        h.Name.Equals(_cfg.PreferredNetwork, StringComparison.OrdinalIgnoreCase));

                if (hw != null)
                    return ReadNetworkSensor(hw, key);

                // 找不到 → 回到自动
            }

            return GetBestNetworkValue(key);
        }

        // --- 帮助函数：从指定网卡读取 Up/Down ---
        private float? ReadNetworkSensor(IHardware hw, string key)
        {
            ISensor? up = null;
            ISensor? down = null;

            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;

                string sn = s.Name.ToLower();

                if (_upKW.Any(k => sn.Contains(k))) up ??= s;
                if (_downKW.Any(k => sn.Contains(k))) down ??= s;
            }

            ISensor? t = key == "NET.Up" ? up : down;

            if (t?.Value is float v && !float.IsNaN(v))
            {
                _lastValid[key] = v;
                return v;
            }

            if (_lastValid.TryGetValue(key, out var last))
                return last;

            return null;
        }

        private static readonly string[] _upKW = { "upload", "up", "sent", "send", "tx", "transmit" };
        private static readonly string[] _downKW = { "download", "down", "received", "receive", "rx" };
        private static readonly string[] _virtualNicKW =
        {
            "virtual","vmware","hyper-v","hyper v","vbox",
            "loopback","tunnel","tap","tun","bluetooth",
            "zerotier","tailscale","wi-fi direct","wifi direct","wan miniport"
        };

        // --- 自动：选择最活跃网卡 ---
        private float? GetBestNetworkValue(string key)
        {
            ISensor? bestUp = null;
            ISensor? bestDown = null;
            double bestScore = double.MinValue;

            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
            {
                string lname = hw.Name.ToLower();
                double penalty = _virtualNicKW.Any(v => lname.Contains(v)) ? -1e9 : 0;

                ISensor? up = null;
                ISensor? down = null;

                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;

                    string sn = s.Name.ToLower();

                    if (_upKW.Any(k => sn.Contains(k))) up ??= s;
                    if (_downKW.Any(k => sn.Contains(k))) down ??= s;
                }

                if (up == null && down == null)
                    continue;

                double upVal = up?.Value ?? 0;
                double downVal = down?.Value ?? 0;
                double score = upVal + downVal + penalty;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestUp = up;
                    bestDown = down;
                }
            }

            ISensor? t = key == "NET.Up" ? bestUp : bestDown;

            if (t?.Value is float v && !float.IsNaN(v))
            {
                _lastValid[key] = v;
                return v;
            }

            if (_lastValid.TryGetValue(key, out var last))
                return last;

            return null;
        }

        // ===========================================================
        // =============== 手动 / 自动 — 磁盘 =========================
        // ===========================================================
        private float? GetDiskValue(string key)
        {
            // ========== 手动模式 ==========
            if (!string.IsNullOrWhiteSpace(_cfg.PreferredDisk))
            {
                var hw = _computer.Hardware
                    .FirstOrDefault(h =>
                        h.HardwareType == HardwareType.Storage &&
                        h.Name.Equals(_cfg.PreferredDisk, StringComparison.OrdinalIgnoreCase));

                if (hw != null)
                    return ReadDiskSensor(hw, key);
            }

            return GetBestDiskValue(key);
        }

        // --- 帮助：从指定磁盘读取 ---
        private float? ReadDiskSensor(IHardware hw, string key)
        {
            ISensor? read = null;
            ISensor? write = null;

            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;

                string sn = s.Name.ToLower();
                if (sn.Contains("read")) read ??= s;
                if (sn.Contains("write")) write ??= s;
            }

            ISensor? t = key == "DISK.Read" ? read : write;

            if (t?.Value is float v && !float.IsNaN(v))
            {
                _lastValid[key] = v;
                return v;
            }

            if (_lastValid.TryGetValue(key, out var last))
                return last;

            return null;
        }

        // --- 自动：系统盘优先 + 活跃度 ---
        private float? GetBestDiskValue(string key)
        {
            char? sys = null;
            try
            {
                string path = Environment.SystemDirectory;
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                    sys = char.ToUpperInvariant(root[0]);
            }
            catch { }

            ISensor? bestRead = null;
            ISensor? bestWrite = null;
            double bestScore = double.MinValue;

            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
            {
                bool isSystemDisk = false;
                string lname = hw.Name.ToLower();
                string sysStr = sys.HasValue ? $"{sys.Value.ToString().ToLower()}:" : "";

                if (sysStr != "")
                {
                    if (lname.Contains(sysStr))
                        isSystemDisk = true;
                    else
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.Name.ToLower().Contains(sysStr))
                            {
                                isSystemDisk = true;
                                break;
                            }
                        }
                    }
                }

                ISensor? read = null;
                ISensor? write = null;

                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput)
                        continue;

                    string sn = s.Name.ToLower();
                    if (sn.Contains("read")) read ??= s;
                    if (sn.Contains("write")) write ??= s;
                }

                if (read == null && write == null)
                    continue;

                double rVal = read?.Value ?? 0;
                double wVal = write?.Value ?? 0;
                double activity = rVal + wVal;

                double score = activity;
                if (isSystemDisk) score += 1e9;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRead = read;
                    bestWrite = write;
                }
            }

            ISensor? t = key == "DISK.Read" ? bestRead : bestWrite;

            if (t?.Value is float v && !float.IsNaN(v))
            {
                _lastValid[key] = v;
                return v;
            }

            if (_lastValid.TryGetValue(key, out var last))
                return last;

            return null;
        }

        // ===========================================================
        // =============== 用于菜单枚举设备 ==========================
        // ===========================================================
        public static List<string> ListAllNetworks()
        {
            if (Instance == null) return new List<string>();

            return Instance._computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Network)
                .Select(h => h.Name)
                .Distinct()
                .ToList();
        }

        public static List<string> ListAllDisks()
        {
            if (Instance == null) return new List<string>();

            return Instance._computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .Select(h => h.Name)
                .Distinct()
                .ToList();
        }

        // ===========================================================
        public void UpdateAll()
        {
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType is HardwareType.GpuNvidia
                        or HardwareType.GpuAmd
                        or HardwareType.GpuIntel
                        or HardwareType.Cpu)
                        hw.Update();
                    else if ((DateTime.Now - _lastMapBuild).TotalSeconds > 3)
                        hw.Update();
                }

                OnValuesUpdated?.Invoke();
            }
            catch { }
        }

        public void Dispose() => _computer.Close();
    }
}

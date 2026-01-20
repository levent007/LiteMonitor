using System;
using System.Linq;

namespace LiteMonitor.src.Core
{
    public enum MetricType
    {
        Percent,    // Load, BatPercent
        Temperature,// Temp
        Memory,     // Mem, Vram
        DataSize,   // Data Total
        DataSpeed,  // Net, Disk
        Frequency,  // Clock
        Power,      // Power
        Voltage,    // Voltage
        Current,    // Current
        RPM,        // Fan, Pump
        FPS,        // FPS
        Unknown
    }

    /// <summary>
    /// LiteMonitor 核心指标处理工具
    /// 包含：类型解析、数据格式化、阈值评估、状态管理
    /// </summary>
    public static class MetricUtils
    {
        // =========================================================
        // 1. 全局状态
        // =========================================================
        public static bool IsBatteryCharging = false;

        public const int STATE_SAFE = 0;
        public const int STATE_WARN = 1;
        public const int STATE_CRIT = 2;

        // =========================================================
        // 2. 类型解析 (原 MetricHelper)
        // =========================================================
        public static MetricType GetType(string key)
        {
            if (string.IsNullOrEmpty(key)) return MetricType.Unknown;
            
            if (key.Equals("FPS", StringComparison.OrdinalIgnoreCase)) return MetricType.FPS;
            if (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase))
            {
                if (key.IndexOf("Percent", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Percent;
                if (key.IndexOf("Voltage", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Voltage;
                if (key.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Current;
                if (key.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Power;
                return MetricType.Percent;
            }
            if (key.IndexOf("LOAD", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Percent;
            if (key.IndexOf("TEMP", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Temperature;
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Frequency;
            if (key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Power;
            if (key.IndexOf("VOLTAGE", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Voltage;
            if (key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.RPM;
            if (key.StartsWith("NET", StringComparison.OrdinalIgnoreCase) || 
                key.StartsWith("DISK", StringComparison.OrdinalIgnoreCase)) return MetricType.DataSpeed;
            if (key.IndexOf("DATA", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.DataSize;
            if (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Memory;

            return MetricType.Unknown;
        }

        // =========================================================
        // 3. 格式化逻辑 (原 Formatter)
        // =========================================================
        public static (string valStr, string unitStr) FormatValueParts(string key, float? raw)
        {
            float v = raw ?? 0.0f;
            var type = GetType(key);

            // 内存特殊处理
            if (type == MetricType.Memory)
            {
                 var cfg = Settings.Load();
                 if (cfg.MemoryDisplayMode == 1) // 容量模式
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : Settings.DetectedGpuVramTotalGB;

                     if (totalGB > 0)
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, 1);
                 }
                 return ($"{v:0.0}", "%");
            }

            if (type == MetricType.DataSpeed || type == MetricType.DataSize)
                return FormatDataSizeParts(v, -1);

            string suffix = (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase) && IsBatteryCharging) ? "⚡" : "";

            return type switch
            {
                MetricType.FPS         => ($"{v:0}", " FPS"),
                MetricType.RPM         => ($"{v:0}", " RPM"),
                MetricType.Temperature => ($"{v:0.0}", "°C"),
                MetricType.Frequency   => ($"{v/1000f:F1}", "GHz"),
                MetricType.Percent     => ($"{v:0.0}", "%" + suffix),
                MetricType.Voltage     => ($"{v:F2}", "V" + suffix),
                MetricType.Current     => ($"{v:F2}", "A" + suffix),
                MetricType.Power       => (key.StartsWith("BAT") ? $"{v:F1}" : $"{v:F0}", "W" + suffix),
                _                      => ($"{v:0.0}", "")
            };
        }

        public static string GetDefaultUnit(string key, bool isTaskbar)
        {
            var type = GetType(key);
            if (type == MetricType.Memory) return Settings.Load().MemoryDisplayMode == 1 ? "{u}" : "%";
            if (type == MetricType.DataSize) return "{u}";
            if (type == MetricType.DataSpeed) return isTaskbar ? "{u}" : "{u}/s";

            return type switch
            {
                MetricType.Percent     => "%",
                MetricType.Temperature => "°C",
                MetricType.Frequency   => "GHz",
                MetricType.Power       => "W",
                MetricType.Voltage     => "V",
                MetricType.Current     => "A",
                MetricType.FPS         => isTaskbar ? "F" : " FPS",
                MetricType.RPM         => isTaskbar ? "R" : " RPM",
                _ => ""
            };
        }

        public static string GetDisplayUnit(string key, string calculatedUnit, string userFormat)
        {
            if (string.IsNullOrEmpty(userFormat) || userFormat.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(userFormat) && userFormat != null) return ""; // Empty = Hide
                return (GetType(key) == MetricType.DataSpeed) ? calculatedUnit + "/s" : calculatedUnit;
            }
            return userFormat.Contains("{u}") ? userFormat.Replace("{u}", calculatedUnit) : userFormat;
        }

        public static (string val, string unit) FormatDataSizeParts(double bytes, int decimals = -1)
        {
            string[] sizes = { "KB", "MB", "GB", "TB", "PB" };
            double len = bytes / 1024.0; 
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024.0; }

            string format = decimals switch {
                < 0 => order <= 1 ? "0.0" : "0.00",
                0 => "0",
                _ => "0." + new string('0', decimals)
            };
            return (len.ToString(format), sizes[order]);
        }

        public static string FormatDataSize(double bytes, string suffix = "", int decimals = -1)
        {
            var (val, unit) = FormatDataSizeParts(bytes, decimals);
            return $"{val}{unit}{suffix}";
        }

        public static string FormatHorizontalValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            string clean = value.Replace("/s", "").Replace("RPM", "R").Replace("FPS", "F").Trim();

            int splitIndex = -1;
            for (int i = 0; i < clean.Length; i++)
            {
                if (!char.IsDigit(clean[i]) && clean[i] != '.' && clean[i] != '-') { splitIndex = i; break; }
            }

            if (splitIndex <= 0) return clean;

            string numStr = clean.Substring(0, splitIndex);
            string unit = clean.Substring(splitIndex).Trim();

            if (double.TryParse(numStr, out double num))
                return (num >= 100 ? ((int)Math.Round(num)).ToString() : numStr) + unit;
            
            return clean;
        }

        // =========================================================
        // 4. 评估逻辑 (原 MetricEvaluator)
        // =========================================================
        public static int GetState(string key, double value)
        {
            if (double.IsNaN(value)) return STATE_SAFE;
            if (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase) && value < 0) return STATE_SAFE;

            var type = GetType(key);
            double checkValue = value;

            // 自适应缩放
            if (type is MetricType.Frequency or MetricType.Power or MetricType.RPM or MetricType.FPS or MetricType.Voltage or MetricType.Current)
            {
                 checkValue = GetUnifiedPercent(key, value) * 100.0;
            }
            else if (type is MetricType.DataSpeed or MetricType.DataSize)
            {
                checkValue = value / 1048576.0; // To MB
            }

            var (warn, crit) = GetThresholds(key);

            // 电池低电量反向判断
            if (key.Equals("BAT.Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (checkValue <= crit) return STATE_CRIT;
                if (checkValue <= warn) return STATE_WARN;
                return STATE_SAFE;
            }

            if (checkValue >= crit) return STATE_CRIT;
            if (checkValue >= warn) return STATE_WARN;

            return STATE_SAFE;
        }

        public static (double warn, double crit) GetThresholds(string key)
        {
            var cfg = Settings.Load();
            var th = cfg.Thresholds;
            var type = GetType(key);

            return type switch
            {
                MetricType.Temperature => (th.Temp.Warn, th.Temp.Crit),
                MetricType.DataSpeed => key.StartsWith("NET") 
                    ? (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 ? (th.NetUpMB.Warn, th.NetUpMB.Crit) : (th.NetDownMB.Warn, th.NetDownMB.Crit))
                    : (th.DiskIOMB.Warn, th.DiskIOMB.Crit),
                MetricType.DataSize => (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 ? (th.DataUpMB.Warn, th.DataUpMB.Crit) : (th.DataDownMB.Warn, th.DataDownMB.Crit)),
                MetricType.Percent => key.Equals("BAT.Percent", StringComparison.OrdinalIgnoreCase) ? (60, 20) : (th.Load.Warn, th.Load.Crit),
                _ => (th.Load.Warn, th.Load.Crit)
            };
        }

        public static double GetUnifiedPercent(string key, double value)
        {
            var type = GetType(key);
            if (type is MetricType.Frequency or MetricType.Power or MetricType.RPM or MetricType.FPS or MetricType.Voltage or MetricType.Current)
                return GetAdaptivePercentage(key, value);
            
            return value / 100.0;
        }

        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (key == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (key == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (key == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (key == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (key == "FPS") max = cfg.RecordedMaxFps;
            else if (key == "CPU.Voltage") max = 1.6f;
            else if (key == "BAT.Power") max = 100f;
            else if (key == "BAT.Voltage") max = 20f;
            else if (key == "BAT.Current") max = 5f;

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
        
        // =========================================================
        // 5. 基础转换
        // =========================================================
        public static int ParseInt(string s) => int.TryParse(new string(s?.Where(c => char.IsDigit(c) || c == '-').ToArray()), out int v) ? v : 0;
        public static double ParseDouble(string s) => double.TryParse(new string(s?.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray()), out double v) ? v : 0;
        public static string ToStr(double v, string format = "F1") => v.ToString(format);
    }
}

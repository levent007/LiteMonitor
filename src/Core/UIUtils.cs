using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using System.Text.RegularExpressions;

namespace LiteMonitor.Common
{
    /// <summary>
    /// LiteMonitor 的公共 UI 工具库（所有渲染器可用）
    /// </summary>
    public static class UIUtils
    {
        // ============================================================
        // ① 通用数值格式化（统一入口）
        // ============================================================
        public static string FormatValue(string key, float? raw)
        {
            string k = key.ToUpperInvariant();
            float v = raw ?? 0.0f;

            if (k.Contains("LOAD") || k.Contains("VRAM") || k.Contains("MEM")) return $"{v:0.0}%";
            if (k.Contains("TEMP")) return $"{v:0.0}°C";

            // 频率
            if (key.Contains("Clock"))
            {
                return $"{v / 1000.0:F1}GHz";
                // if (v >= 1000) return $"{v / 1000.0:F1}GHz";
                // return $"{v:F0}MHz";
            }

            // 功耗
            if (key.Contains("Power"))
            {
                return $"{v:F0}W";
            }

            // NET / DISK = 流量类
            if (k.StartsWith("NET") || k.StartsWith("DISK"))
            {
                double kb = v / 1024.0;
                double mb = kb / 1024.0;
                return kb >= 1024
                    ? $"{mb:0.00}MB/s"
                    : $"{kb:0.0}KB/s";
            }

            return $"{v:0.0}";
        }

        // ============================================================
        // ② 阈值解析（各类指标）
        // ============================================================
        public static (double warn, double crit) GetThresholds(string key, Theme t)
        {
            string k = key.ToUpperInvariant();

            if (k.Contains("LOAD")) return (t.Thresholds.Load.Warn, t.Thresholds.Load.Crit);
            if (k.Contains("TEMP")) return (t.Thresholds.Temp.Warn, t.Thresholds.Temp.Crit);
            if (k.StartsWith("MEM")) return (t.Thresholds.Mem.Warn, t.Thresholds.Mem.Crit);
            if (k.StartsWith("VRAM")) return (t.Thresholds.Vram.Warn, t.Thresholds.Vram.Crit);
            if (k.StartsWith("NET") || k.StartsWith("DISK")) return (t.Thresholds.NetKBps.Warn, t.Thresholds.NetKBps.Crit);

            return (t.Thresholds.Load.Warn, t.Thresholds.Load.Crit);
        }

        // ============================================================
        // ③ 统一颜色选择
        // ============================================================
        public static Color GetColor(string key, double value, Theme t, bool isValueText = true)
        {
            if (double.IsNaN(value)) return ThemeManager.ParseColor(t.Color.TextPrimary);

            string k = key.ToUpperInvariant();

            // 1. 网络 / 磁盘
            if (k.StartsWith("NET") || k.StartsWith("DISK"))
            {
                double kbps = value / 1024.0;
                if (kbps < t.Thresholds.NetKBps.Warn) return ThemeManager.ParseColor(t.Color.ValueSafe);
                if (kbps < t.Thresholds.NetKBps.Crit) return ThemeManager.ParseColor(t.Color.ValueWarn);
                return ThemeManager.ParseColor(t.Color.ValueCrit);
            }

            // 2. 频率与功耗 (自适应)
            if (key.Contains("Clock") || key.Contains("Power"))
            {
                double pct = GetAdaptivePercentage(key, value);
                if (pct >= 0.9) return ThemeManager.ParseColor(t.Color.ValueCrit);
                if (pct >= 0.6) return ThemeManager.ParseColor(t.Color.ValueWarn);
                return ThemeManager.ParseColor(t.Color.ValueSafe);
            }

            // 3. 常规指标
            var (warn, crit) = GetThresholds(key, t);
            if (value < warn) return ThemeManager.ParseColor(isValueText ? t.Color.ValueSafe : t.Color.BarLow);
            if (value < crit) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
        }

        // ============================================================
        // ④ 通用图形
        // ============================================================
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c)
        {
            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }

        // ============================================================
        // ⑤ 完整进度条 (恢复最低 5% 版本)
        // ============================================================
        public static void DrawBar(Graphics g, Rectangle bar, double value, string key, Theme t)
        {
            // 1. 绘制背景槽
            using (var bgPath = RoundRect(bar, bar.Height / 2))
            {
                g.FillPath(new SolidBrush(ThemeManager.ParseColor(t.Color.BarBackground)), bgPath);
            }

            // =========================================================
            // 核心计算逻辑
            // =========================================================
            double percent;
            string colorCode;

            if (key.Contains("Clock") || key.Contains("Power"))
            {
                // --- 频率/功耗 (Value / Max) ---
                // 从 Settings 读取历史最大值作为分母
                var cfg = Settings.Load();
                float max = 1.0f;
                if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
                else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
                else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
                else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;

                if (max < 1) max = 1;
                percent = value / max;

                // 颜色策略
                if (percent >= 0.9) colorCode = t.Color.BarHigh;
                else if (percent >= 0.6) colorCode = t.Color.BarMid;
                else colorCode = t.Color.BarLow;
            }
            else
            {
                // --- 默认处理 (Value / 100) ---
                percent = value / 100.0;

                // 颜色策略 (原有阈值)
                var (warn, crit) = GetThresholds(key, t);
                if (value >= crit) colorCode = t.Color.BarHigh;
                else if (value >= warn) colorCode = t.Color.BarMid;
                else colorCode = t.Color.BarLow;
            }

            // ★★★ 恢复您的逻辑：限制范围在 5% ~ 100% 之间 ★★★
            // 即使 value 是 0，也显示 5% 的长度，保持视觉统一
            percent = Math.Max(0.05, Math.Min(1.0, percent));

            // 2. 绘制前景条
            int w = (int)(bar.Width * percent);
            
            // 确保至少有 2px 宽度，避免圆角绘制异常
            if (w < 2) w = 2; 

            if (w > 0)
            {
                var filled = new Rectangle(bar.X, bar.Y, w, bar.Height);
                
                // 简单防越界
                if (filled.Width > bar.Width) filled.Width = bar.Width;

                using (var fgPath = RoundRect(filled, filled.Height / 2))
                {
                    g.FillPath(new SolidBrush(ThemeManager.ParseColor(colorCode)), fgPath);
                }
            }
        }

        // ============================================================
        // ⑥ 横屏模式 格式化数值 (修复 1024 进制问题)
        // ============================================================
        public static string FormatHorizontalValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            value = value.Replace("/s", "", StringComparison.OrdinalIgnoreCase).Trim();
            //value = value.Replace("Hz", "", StringComparison.OrdinalIgnoreCase).Trim();
            var m = Regex.Match(value, @"^([\d.]+)([A-Za-z%°℃]+)$");
            if (!m.Success) return value;

            double num = double.Parse(m.Groups[1].Value);
            string unit = m.Groups[2].Value;

            // 只有 B, KB, MB 等单位才需要 /1024 处理
            // Hz, W, % 等单位应保持 10 进制或不缩放
            bool isMemoryOrNet = unit.Contains("B", StringComparison.OrdinalIgnoreCase);

            if (unit.Length <= 3 && isMemoryOrNet)
            {
                if (num >= 1000) return (num / 1024.0).ToString("0.0") + "MB"; // 针对 KB -> MB
            }

            // 统一小数位
            return num >= 100
                ? ((int)Math.Round(num)) + unit
                : num.ToString("0.0") + unit;
        }

        // ============================================================
        // 辅助：获取自适应百分比 (从 Settings 读取)
        // ============================================================
        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
    }
}
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LiteMonitor.src.Core;

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
            if (!raw.HasValue) return "--";

            float v = raw.Value;
            string k = key.ToUpperInvariant();

            if (k.Contains("LOAD") || k.Contains("VRAM") || k.Contains("MEM")) return $"{0.0}%";
            if (k.Contains("TEMP")) return $"{0.0}°C";

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

            if (k.StartsWith("CPU.") || k.StartsWith("GPU.") || k.Contains("LOAD"))
                return (t.Thresholds.Load.Warn, t.Thresholds.Load.Crit);

            if (k.Contains("TEMP"))
                return (t.Thresholds.Temp.Warn, t.Thresholds.Temp.Crit);

            if (k.StartsWith("MEM"))
                return (t.Thresholds.Mem.Warn, t.Thresholds.Mem.Crit);

            if (k.StartsWith("GPU.VRAM"))
                return (t.Thresholds.Vram.Warn, t.Thresholds.Vram.Crit);

            if (k.StartsWith("NET"))
                return (t.Thresholds.NetKBps.Warn, t.Thresholds.NetKBps.Crit);

            // 默认走 Load 阈值
            return (t.Thresholds.Load.Warn, t.Thresholds.Load.Crit);
        }

        // ============================================================
        // ③ 统一颜色选择（普通指标 + 网络 + 磁盘）
        // ============================================================
        public static Color GetColor(string key, double value, Theme t, bool isValueText = true)
        {
            if (double.IsNaN(value))
                return ThemeManager.ParseColor(t.Color.TextPrimary);

            string k = key.ToUpperInvariant();

            // --- 网络 / 磁盘 → 使用 KBps 阈值 ---
            if (k.StartsWith("NET") || k.StartsWith("DISK"))
            {
                double kbps = value / 1024.0;
                double warn = t.Thresholds.NetKBps.Warn;
                double crit = t.Thresholds.NetKBps.Crit;

                if (kbps < warn) return ThemeManager.ParseColor(t.Color.ValueSafe);
                if (kbps < crit) return ThemeManager.ParseColor(t.Color.ValueWarn);
                return ThemeManager.ParseColor(t.Color.ValueCrit);
            }

            // --- 常规指标（Load / Temp / Vram / Mem） ---
            var (warn2, crit2) = GetThresholds(key, t);

            if (value < warn2)
                return ThemeManager.ParseColor(isValueText ? t.Color.ValueSafe : t.Color.BarLow);

            if (value < crit2)
                return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);

            return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
        }

        // ============================================================
        // ④ 通用圆角矩形
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
        // ⑤ 完整进度条（基于 key 取阈值）
        // ============================================================
        public static void DrawBar(Graphics g, Rectangle bar, double value, string key, Theme t)
        {
            float pct = Math.Max(5f, Math.Min(100f, (float)value));
            int w = (int)(bar.Width * (pct / 100f));

            // 背景
            using (var bgPath = RoundRect(bar, bar.Height / 2))
                g.FillPath(new SolidBrush(ThemeManager.ParseColor(t.Color.BarBackground)), bgPath);

            // 前景色：由 key → 阈值 → 颜色
            var (warn, crit) = GetThresholds(key, t);
            string barColor =
                value >= crit ? t.Color.BarHigh :
                value >= warn ? t.Color.BarMid :
                t.Color.BarLow;

            var filled = new Rectangle(bar.X, bar.Y, w, bar.Height);

            using (var fgPath = RoundRect(filled, filled.Height / 2))
                g.FillPath(new SolidBrush(ThemeManager.ParseColor(barColor)), fgPath);
        }

        // ============================================================
        // ⑥ 文本测量（水平模式会用）
        // ============================================================
        public static int TextWidth(string s, Font f)
            => TextRenderer.MeasureText(
                   s, f,
                   new Size(int.MaxValue, int.MaxValue),
                   TextFormatFlags.NoPadding | TextFormatFlags.NoClipping
               ).Width;

        public static int TextHeight(string s, Font f)
            => TextRenderer.MeasureText(
                   s, f,
                   new Size(int.MaxValue, int.MaxValue),
                   TextFormatFlags.NoPadding | TextFormatFlags.NoClipping
               ).Height;
    }
}

using LiteMonitor.Common;
using LiteMonitor.src.Core;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 任务栏渲染器（仅负责绘制，不再负责布局）
    /// </summary>
    public static class TaskbarRenderer
    {
        private static readonly Settings _settings = Settings.Load();
        
        // 字体缓存 - 直接初始化，避免每次渲染都创建字体
        private static Font _cachedFont = new Font(
                _settings.TaskbarFontFamily,
                _settings.TaskbarFontSize,
                _settings.TaskbarFontBold ? FontStyle.Bold : FontStyle.Regular
            );

        // 浅色主题
        private static readonly Color LABEL_LIGHT = Color.FromArgb(20, 20, 20);
        private static readonly Color SAFE_LIGHT = Color.FromArgb(0x00, 0x80, 0x40);
        private static readonly Color WARN_LIGHT = Color.FromArgb(0xB5, 0x75, 0x00);
        private static readonly Color CRIT_LIGHT = Color.FromArgb(0xC0, 0x30, 0x30);

        // 深色主题
        private static readonly Color LABEL_DARK = Color.White;
        private static readonly Color SAFE_DARK = Color.FromArgb(0x66, 0xFF, 0x99);
        private static readonly Color WARN_DARK = Color.FromArgb(0xFF, 0xD6, 0x66);
        private static readonly Color CRIT_DARK = Color.FromArgb(0xFF, 0x66, 0x66);

        public static void Render(Graphics g, List<Column> cols, bool light) // <--- 新的
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 使用传入的 light 参数，避免每次都查询系统主题瓠
            //bool light = IsSystemLight();

            foreach (var col in cols)
            {
                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                    DrawItem(g, col.Top, col.BoundsTop, light);

                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                    DrawItem(g, col.Bottom, col.BoundsBottom, light);
            }
        }

        private static void DrawItem(Graphics g, MetricItem item, Rectangle rc, bool light)
        {
            string label = LanguageManager.T($"Short.{item.Key}");
            string value = UIUtils.FormatHorizontalValue(
                               UIUtils.FormatValue(item.Key, item.DisplayValue)
                           );

            // 直接使用缓存的字体，不再 new Font
            Font font = _cachedFont!;

            Color labelColor = light ? LABEL_LIGHT : LABEL_DARK;
            Color valueColor = PickColor(item.Key, item.DisplayValue, light);

            // Label 左对齐
            TextRenderer.DrawText(
                g, label, font, rc, labelColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );

            // Value 右对齐
            TextRenderer.DrawText(
                g, value, font, rc, valueColor,
                TextFormatFlags.Right |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );
        }

        private static Color PickColor(string key, double v, bool light)
        {
            if (double.IsNaN(v)) return light ? LABEL_LIGHT : LABEL_DARK;

            // === 1. 频率与功耗 (调用 UIUtils 算法) ===
            if (key.Contains("Clock") || key.Contains("Power"))
            {
                // ★★★ 直接调用 UIUtils 的公共方法 ★★★
                double pct = UIUtils.GetAdaptivePercentage(key, v);

                // 依然使用任务栏专用的高对比度颜色 (红/黄/绿)
                if (pct >= 0.9) return light ? CRIT_LIGHT : CRIT_DARK;
                if (pct >= 0.6) return light ? WARN_LIGHT : WARN_DARK;
                return light ? SAFE_LIGHT : SAFE_DARK;
            }

            // === 2. 网络 / 磁盘 (单位换算) ===
            if (key.StartsWith("NET") || key.StartsWith("DISK"))
                v /= 1024.0;

            // === 3. 常规阈值判断 (Load/Temp/Net/Disk) ===
            var (warn, crit) = UIUtils.GetThresholds(key, ThemeManager.Current);

            if (v >= crit) return light ? CRIT_LIGHT : CRIT_DARK;
            if (v >= warn) return light ? WARN_LIGHT : WARN_DARK;
            return light ? SAFE_LIGHT : SAFE_DARK;
        }
    }
}

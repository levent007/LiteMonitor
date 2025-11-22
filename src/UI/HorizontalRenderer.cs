using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public static class HorizontalRenderer
    {
        public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)
        {
            int panelHeight = (int)g.VisibleClipBounds.Height;

            // 正确：Layout 给出的 panelWidth 才是真宽度，Renderer 不参与计算
            using (var bg = new SolidBrush(ThemeManager.ParseColor(t.Color.Background)))
                g.FillRectangle(bg, new Rectangle(0, 0, panelWidth, panelHeight));

            foreach (var col in cols)
                DrawColumn(g, col, t);
        }



        private static void DrawColumn(Graphics g, Column col, Theme t)
        {
            if (col.Bounds == Rectangle.Empty)
                return;

            int half = col.Bounds.Height / 2;

            var rectTop = new Rectangle(col.Bounds.X, col.Bounds.Y, col.Bounds.Width, half);
            var rectBottom = new Rectangle(col.Bounds.X, col.Bounds.Y + half, col.Bounds.Width, half);

            if (col.Top != null)
                DrawItem(g, col.Top, rectTop, t);

            if (col.Bottom != null)
                DrawItem(g, col.Bottom, rectBottom, t);
        }

        private static void DrawItem(Graphics g, MetricItem it, Rectangle rc, Theme t)
        {
            string label = LanguageManager.T($"Short.{it.Key}");
            string value = FormatValue(it);

            int colW = rc.Width;
            int fontH = t.FontItem.Height;

            // 测量 label 宽度
            int wLabel = TextRenderer.MeasureText(
                g, label, t.FontItem,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding
            ).Width;

            // padding
            wLabel += fontH / 2;

            // label 宽不能超过列宽
            if (wLabel > colW - fontH)
                wLabel = colW - fontH;

            // 剩下的全部给 value
            int wValue = colW - wLabel;

            Rectangle rcLabel = new Rectangle(rc.X, rc.Y, wLabel, rc.Height);
            Rectangle rcValue = new Rectangle(rc.X + wLabel, rc.Y, wValue, rc.Height);

            // 绘制 label
            TextRenderer.DrawText(
                g,
                label,
                t.FontItem,
                rcLabel,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );

            // 绘制 value（右对齐）
            TextRenderer.DrawText(
                g,
                value,
                t.FontValue,
                rcValue,
                GetValueColor(it, t),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );
        }




        public static string FormatValue(MetricItem it)
        {
            string k = it.Key.ToUpperInvariant();

            if (k.Contains("LOAD") || k.Contains("VRAM") || k.Contains("MEM"))
                return $"{it.DisplayValue:0}%";

            if (k.Contains("TEMP"))
                return $"{it.DisplayValue:0}°C";

            if (k.Contains("READ") || k.Contains("WRITE") ||
                k.Contains("UP") || k.Contains("DOWN"))
            {
                double kb = it.DisplayValue / 1024.0;
                if (kb >= 1024) return $"{kb / 1024:0.0}MB";
                return $"{kb:0}KB";
            }

            return $"{it.DisplayValue:0}";
        }

        private static Color GetValueColor(MetricItem it, Theme t)
        {
            var (warn, crit) = UIRenderer.GetThresholds(it.Key, t);
            float v = it.DisplayValue;

            if (v >= crit)
                return ThemeManager.ParseColor(t.Color.ValueCrit);
            if (v >= warn)
                return ThemeManager.ParseColor(t.Color.ValueWarn);

            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }
    }
}

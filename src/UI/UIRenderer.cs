using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// -------- 渲染层：只负责画，不参与布局 --------
    /// </summary>
    public static class UIRenderer
    {
        private static readonly Dictionary<string, SolidBrush> _brushCache = new();

        private static SolidBrush GetBrush(string color, Theme t)
        {
            if (!_brushCache.TryGetValue(color, out var br))
            {
                br = new SolidBrush(ThemeManager.ParseColor(color));
                _brushCache[color] = br;
            }
            return br;
        }

        public static void ClearCache() => _brushCache.Clear();

        /// <summary>
        /// 绘制整个面板
        /// </summary>
        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 计算背景高度（与 UILayout 保持一致）
            int bgH = 0;
            if (groups.Count > 0)
            {
                var last = groups[^1];
                bgH = last.Bounds.Bottom + t.Layout.GroupBottom + t.Layout.Padding;
            }
            else
            {
                bgH = t.Layout.Padding * 2;
            }

            g.FillRectangle(
                GetBrush(t.Color.Background, t),
                new Rectangle(0, 0, t.Layout.Width, bgH));

            // ===== 绘制主标题 =====
            string title = LanguageManager.T("Title");
            if (string.IsNullOrEmpty(title) || title == "Title")
                title = "LiteMonitor";

            int titleH = TextRenderer.MeasureText(title, t.FontTitle).Height;

            var titleRect = new Rectangle(
                t.Layout.Padding,
                t.Layout.Padding,
                t.Layout.Width - t.Layout.Padding * 2,
                titleH + 4);

            TextRenderer.DrawText(
                g, title, t.FontTitle, titleRect,
                ThemeManager.ParseColor(t.Color.TextTitle),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // ===== 绘制各分组 =====
            foreach (var gr in groups)
                DrawGroup(g, gr, t);
        }

        /// <summary>
        /// 绘制单个组块（标题仍漂浮在外部）
        /// </summary>
        private static void DrawGroup(Graphics g, GroupLayoutInfo gr, Theme t)
        {
            int gp = t.Layout.GroupPadding;
            int radius = t.Layout.GroupRadius;

            // === 背景块（不再扣 GroupBottom）===
            var block = new Rectangle(gr.Bounds.X, gr.Bounds.Y, gr.Bounds.Width, gr.Bounds.Height);

            using (var path = RoundedRect(block, radius))
                g.FillPath(GetBrush(t.Color.GroupBackground, t), path);

            // === 组标题（漂浮在块外）===
            string label = LanguageManager.T($"Groups.{gr.GroupName}");
            if (string.IsNullOrEmpty(label)) label = gr.GroupName;

            int titleH = t.FontGroup.Height;
            int titleY = block.Y - t.Layout.GroupTitleOffset - titleH;

            var rectTitle = new Rectangle(
                block.X + gp,
                Math.Max(0, titleY),
                block.Width - gp * 2,
                titleH);

            TextRenderer.DrawText(
                g,
                label,
                t.FontGroup,
                rectTitle,
                ThemeManager.ParseColor(t.Color.TextGroup),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );

            // === 特殊分组：NET、DISK（双行）===
            if (gr.GroupName.Equals("DISK", StringComparison.OrdinalIgnoreCase))
            {
                DrawTwoColsRow(g, gr.Items, t,
                    "DISK", "DISK.Read", "DISK.Write",
                    LanguageManager.T("Items.DISK.Read"),
                    LanguageManager.T("Items.DISK.Write"));
                return;
            }

            if (gr.GroupName.Equals("NET", StringComparison.OrdinalIgnoreCase))
            {
                DrawTwoColsRow(g, gr.Items, t,
                    "NET", "NET.Up", "NET.Down",
                    LanguageManager.T("Items.NET.Up"),
                    LanguageManager.T("Items.NET.Down"));
                return;
            }

            // === 普通绘制 ===
            foreach (var it in gr.Items)
                DrawMetricItem(g, it, t);
        }

        /// <summary>
        /// （保留原样）单行 + 进度条绘制
        /// </summary>
        private static void DrawMetricItem(Graphics g, MetricItem it, Theme t)
        {
            if (it.Bounds == Rectangle.Empty) return;

            var inner = new Rectangle(it.Bounds.X + 10, it.Bounds.Y, it.Bounds.Width - 20, it.Bounds.Height);
            int topH = (int)(inner.Height * 0.55);

            var topRect = new Rectangle(inner.X, inner.Y, inner.Width, topH);

            string label = LanguageManager.T($"Items.{it.Key}");
            if (label == $"Items.{it.Key}") label = it.Label;

            TextRenderer.DrawText(
                g, label, t.FontItem, topRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            string text = BuildValueText(it);
            var (warn, crit) = GetThresholds(it.Key, t);
            Color valColor = ChooseColor(it.DisplayValue, warn, crit, t, true);

            TextRenderer.DrawText(
                g, text, t.FontValue, topRect,
                valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int barH = Math.Max(6, (int)(inner.Height * 0.25));
            int barY = inner.Bottom - barH - 3;

            var barRect = new Rectangle(inner.X, barY, inner.Width, barH);

            DrawBar(g, barRect, it.DisplayValue, warn, crit, t);
        }

        // ========== 双列绘制（NET / DISK） ==========
        private static void DrawTwoColsRow(
              Graphics g,
              List<MetricItem> items,
              Theme t,
              string prefix,
              string leftKey,
              string rightKey,
              string leftLabel,
              string rightLabel,
              double verticalOffset = 0.1
          )
        {
            var leftItem = items.FirstOrDefault(i => i.Key.Equals(leftKey, StringComparison.OrdinalIgnoreCase));
            var rightItem = items.FirstOrDefault(i => i.Key.Equals(rightKey, StringComparison.OrdinalIgnoreCase));
            if (leftItem == null && rightItem == null) return;

            var baseRect = (leftItem ?? rightItem)!.Bounds;
            int rowH = baseRect.Height;
            int colW = baseRect.Width / 2;

            var left = new Rectangle(baseRect.X, baseRect.Y, colW, rowH);
            var right = new Rectangle(left.Right, baseRect.Y, colW, rowH);

            int offsetY = (int)(rowH * verticalOffset);

            // 标签
            var leftShift = new Rectangle(left.X, left.Y + offsetY, left.Width, left.Height);
            var rightShift = new Rectangle(right.X, right.Y + offsetY, right.Width, right.Height);

            TextRenderer.DrawText(
                g,
                leftLabel,
                t.FontItem,
                leftShift,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding
            );
            TextRenderer.DrawText(
                g,
                rightLabel,
                t.FontItem,
                rightShift,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding
            );

            // 数值
            int valueExtraOffset = (int)(rowH * verticalOffset);
            var leftValueRect = new Rectangle(leftShift.X, leftShift.Y + valueExtraOffset, leftShift.Width, leftShift.Height);
            var rightValueRect = new Rectangle(rightShift.X, rightShift.Y + valueExtraOffset, rightShift.Width, rightShift.Height);

            string leftStr = FormatNet(leftItem?.Value);
            string rightStr = FormatNet(rightItem?.Value);

            double leftKBps = (leftItem?.Value ?? 0f) / 1024.0;
            double rightKBps = (rightItem?.Value ?? 0f) / 1024.0;

            var leftColor = ChooseNetColor(leftKBps, t);
            var rightColor = ChooseNetColor(rightKBps, t);

            TextRenderer.DrawText(
                g,
                leftStr,
                t.FontValue,
                leftValueRect,
                leftColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding
            );
            TextRenderer.DrawText(
                g,
                rightStr,
                t.FontValue,
                rightValueRect,
                rightColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding
            );
        }



        // ========== 工具函数 ==========

        private static string BuildValueText(MetricItem it)
        {
            string k = it.Key.ToUpperInvariant();
            if (k.Contains("LOAD") || k.Contains("VRAM") || k.Contains("MEM")) return $"{it.DisplayValue:0.0}%";
            if (k.Contains("TEMP")) return $"{it.DisplayValue:0.0}°C";
            return $"{it.DisplayValue:0.0}";
        }

        public static (double warn, double crit) GetThresholds(string key, Theme t)
        {
            string k = key.ToUpperInvariant();
            if (k.Contains("LOAD")) return (t.Thresholds.Load.Warn, t.Thresholds.Load.Crit);
            if (k.Contains("TEMP")) return (t.Thresholds.Temp.Warn, t.Thresholds.Temp.Crit);
            if (k.Contains("VRAM")) return (t.Thresholds.Vram.Warn, t.Thresholds.Vram.Crit);
            if (k.Contains("MEM")) return (t.Thresholds.Mem.Warn, t.Thresholds.Mem.Crit);
            return (t.Thresholds.Load.Warn, t.Thresholds.Load.Crit);
        }

        private static Color ChooseColor(double v, double warn, double crit, Theme t, bool forValue)
        {
            if (double.IsNaN(v)) return ThemeManager.ParseColor(t.Color.TextPrimary);
            if (v < warn) return ThemeManager.ParseColor(forValue ? t.Color.ValueSafe : t.Color.BarLow);
            if (v < crit) return ThemeManager.ParseColor(forValue ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(forValue ? t.Color.ValueCrit : t.Color.BarHigh);
        }


        private static string FormatNet(float? bytes)
        {
            if (!bytes.HasValue) return "0.0KB/s";
            double kb = bytes.Value / 1024.0;
            return kb >= 1024 ? $"{kb / 1024.0:0.00}MB/s" : $"{kb:0.0}KB/s";
        }

        private static Color ChooseNetColor(double kbps, Theme t)
        {
            if (double.IsNaN(kbps)) return ThemeManager.ParseColor(t.Color.TextPrimary);
            double warn = t.Thresholds.NetKBps.Warn;
            double crit = t.Thresholds.NetKBps.Crit;
            if (kbps < warn) return ThemeManager.ParseColor(t.Color.ValueSafe);
            if (kbps < crit) return ThemeManager.ParseColor(t.Color.ValueWarn);
            return ThemeManager.ParseColor(t.Color.ValueCrit);
        }

        private static void DrawBar(Graphics g, Rectangle bar, float v, double warn, double crit, Theme t)
        {
            float pct = Math.Max(5f, Math.Min(100f, v));
            int w = (int)(bar.Width * (pct / 100f));

            using var pathBg = RoundedRect(bar, bar.Height / 2);
            g.FillPath(GetBrush(t.Color.BarBackground, t), pathBg);

            var filled = new Rectangle(bar.X, bar.Y, w, bar.Height);

            string c = (v >= crit)
                ? t.Color.BarHigh
                : (v >= warn ? t.Color.BarMid : t.Color.BarLow);

            using var pathFill = RoundedRect(filled, bar.Height / 2);
            g.FillPath(GetBrush(c, t), pathFill);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
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
    }
}

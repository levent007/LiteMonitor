using LiteMonitor.src.Core;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 横屏布局（每列独立宽度模式）
    /// 目的：让每个列根据自己的“标签宽 + 最大数值宽”来计算不同宽度
    /// </summary>
    public class HorizontalLayout
    {
        private readonly Theme _t;      // 当前主题对象，用来拿字体与布局参数
        private readonly int _padding;  // 左右上下 padding
        private readonly int _rowH;     // 单行高度（取 itemFont 与 valueFont 的最大值）

        public int PanelWidth { get; private set; } // 横屏整体宽度（计算后赋值）

        // 固定最大数值模板（重要！这个决定了该列“数值部分”的理论最大宽度）
        // 普通列最大值（CPU/GPU/MEM/VRAM/TEMP/LOAD）
        private const string MAX_VALUE_NORMAL = "100°C";
        // IO 列最大值（磁盘+网络）
        private const string MAX_VALUE_IO = "999.9KB";

        // 列间距（所有列之间额外加入的空隙）
        private const int COLUMN_GAP = 12;

        public HorizontalLayout(Theme t, int initialWidth)
        {
            _t = t;
            _padding = t.Layout.Padding;

            // 行高 = max(字体大小), 让上下两行高度一致
            _rowH = System.Math.Max(t.FontItem.Height, t.FontValue.Height);

            // 初始 panelWidth，只是占位，后面 Build() 会真正计算
            PanelWidth = initialWidth;
        }

        /// <summary>
        /// 核心：每列独立宽度 = 标签宽(labelWidth) + 数值宽(valueWidth) + padding
        /// 注意：只计算一次（横屏切换或者初始化时）
        /// </summary>
        public int Build(List<Column> cols)
        {
            int pad = _padding;

            // panel 总宽度初始 = 左右 padding * 2
            int totalWidth = pad * 2;

            // 使用 GDI 测量文本宽度
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (var col in cols)
                {
                    //
                    // ========== 1. 计算标签宽度 wLabel ==========
                    //
                    // col.Top != null → 取语言包中的对应 Short.xxx 文本
                    // （横屏标签只看 top 的 Key）
                    string label =
                        col.Top != null ? LanguageManager.T($"Short.{col.Top.Key}") : "";

                    // 用 TextRenderer 测量标签宽度（最精确的 WinForms 文本测量方式）
                    int wLabel = TextRenderer.MeasureText(
                        g, label, _t.FontItem,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    //
                    // ========== 2. 计算最大数值宽度 wValue ==========
                    //
                    // 不是实时值，而是“理论最大可出现的文本”
                    // 普通列 → "100°C"
                    // IO 列   → "999KB/s"
                    string maxValue = GetMaxValueSample(col);

                    int wValue = TextRenderer.MeasureText(
                        g, maxValue, _t.FontValue,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    //
                    // ========== 3. 计算列宽 colW ==========
                    //
                    // wLabel + wValue + _rowH(作为左右 padding)
                    // 注意：此处不等宽，每列 colW 不同
                    //
                    int colW = wLabel + wValue ;

                    // 保存列宽
                    col.ColumnWidth = colW;

                    // panel 宽度累加
                    totalWidth += colW;
                }
            }

            //
            // ========== 4. 增加每列之间的列间距 COLUMN_GAP ==========
            //
            // n 列 → (n-1) 个间距
            //
            totalWidth += (cols.Count - 1) * COLUMN_GAP;

            // 最终横屏宽度
            PanelWidth = totalWidth;

            //
            // ========== 5. 设置每列的 Bounds（坐标与宽度） ==========
            //
            int x = pad;

            foreach (var col in cols)
            {
                // col.ColumnWidth 是我们上面算好的宽度
                col.Bounds = new Rectangle(x, pad, col.ColumnWidth, _rowH * 2);

                // 下一个列的 x 坐标：当前列宽 + gap
                x += col.ColumnWidth + COLUMN_GAP;
            }

            //
            // ========== 6. 返回整体高度（固定 = 两行高度 + padding*2） ==========
            //
            return pad * 2 + _rowH * 2;
        }

        /// <summary>
        /// 根据列类型返回固定最大值模板（决定列宽的重要因素）
        /// </summary>
        private string GetMaxValueSample(Column col)
        {
            string key = col.Top?.Key?.ToUpperInvariant() ?? "";

            // 如果是磁盘 or 网络 → 使用 IO 模板
            if (key.Contains("READ") || key.Contains("WRITE") ||
                key.Contains("UP") || key.Contains("DOWN"))
                return MAX_VALUE_IO;

            // 其他列（CPU/GPU/MEM/etc）使用普通模板
            return MAX_VALUE_NORMAL;
        }
    }

    public class Column
    {
        public MetricItem? Top;
        public MetricItem? Bottom;

        public int ColumnWidth;              // 该列独有的宽度（核心）
        public Rectangle Bounds = Rectangle.Empty; // 绘制区域
    }
}

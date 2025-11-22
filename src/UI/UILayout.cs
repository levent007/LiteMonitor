using LiteMonitor.src.Core;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 单个组的布局信息（名称 + 块区域 + 子项）
    /// </summary>
    public class GroupLayoutInfo
    {
        public string GroupName { get; set; }
        public Rectangle Bounds { get; set; }
        public List<MetricItem> Items { get; set; }

        public GroupLayoutInfo(string name, List<MetricItem> items)
        {
            GroupName = name;
            Items = items;
            Bounds = Rectangle.Empty;
        }
    }

    /// <summary>
    /// -------- UILayout：布局计算层 --------
    /// 只做数学计算，不参与绘制
    /// 新规则：
    ///   1. GroupBottom = 外间距（只控制组块之间 & 最后一组到底部）
    ///   2. 标题放块外，不计入组块高度
    ///   3. groupHeight = Padding*2 + innerHeight（纯内容）
    /// </summary>
    public class UILayout
    {
        private readonly Theme _t;
        public UILayout(Theme t) { _t = t; }

        /// <summary>
        /// 计算所有组块与子项的位置，并返回内容总高度
        /// </summary>
        public int Build(List<GroupLayoutInfo> groups)
        {
            int x = _t.Layout.Padding;
            int y = _t.Layout.Padding;
            int w = _t.Layout.Width - _t.Layout.Padding * 2;
            int rowH = _t.Layout.RowHeight;

            // ===== 主标题占位（块外）=====
            string title = LanguageManager.T("Title");
            if (!string.IsNullOrEmpty(title) && title != "Title")
                y += rowH + _t.Layout.Padding;

            // ===== 遍历所有分组 =====
            for (int idx = 0; idx < groups.Count; idx++)
            {
                var g = groups[idx];

                // ==== 计算内部内容高度 ====
                int innerHeight;

                if (g.GroupName.Equals("NET", System.StringComparison.OrdinalIgnoreCase) ||
                    g.GroupName.Equals("DISK", System.StringComparison.OrdinalIgnoreCase))
                {
                    // 双行（Up/Down / Read/Write）
                    int twoLineH = rowH + (int)System.Math.Ceiling(rowH * 0.10);
                    innerHeight = twoLineH + _t.Layout.ItemGap;
                }
                else
                {
                    innerHeight =
                        g.Items.Count * rowH +
                        (g.Items.Count - 1) * _t.Layout.ItemGap;
                }

                // ==== 计算组块高度（不再包含 GroupBottom）====
                int groupHeight =
                    _t.Layout.GroupPadding * 2 +
                    innerHeight;

                // 保存块区域
                g.Bounds = new Rectangle(x, y, w, groupHeight);

                // 子项排布
                int itemY = y + _t.Layout.GroupPadding;
                foreach (var it in g.Items)
                {
                    it.Bounds = new Rectangle(x, itemY, w, rowH);
                    itemY += rowH + _t.Layout.ItemGap;
                }

                // 不是最后一组 → 组间加 spacing + bottom
                if (idx < groups.Count - 1)
                    y += groupHeight + _t.Layout.GroupSpacing + _t.Layout.GroupBottom;
                else
                    y += groupHeight; // 最后一组不加  spacing + bottom
            }

            // ===== 内容总高度（最后一组到底部 = Padding + GroupBottom）=====
            int contentHeight = y + _t.Layout.Padding;

            return contentHeight;
        }
    }
}

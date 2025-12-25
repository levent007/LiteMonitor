using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MonitorPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        public MonitorPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            InitHeader();

            _container = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(20, 5, 20, 20)
            };
            this.Controls.Add(_container);
            this.Controls.SetChildIndex(_container, 0);
        }

        private void InitHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 35, BackColor = UIColors.MainBg };
            header.Padding = new Padding(20, 0, 20, 0);

            void AddLabel(string text, int x)
            {
                header.Controls.Add(new Label {
                    Text = text, Location = new Point(x + 20, 10), AutoSize = true,
                    ForeColor = UIColors.TextSub, Font = UIFonts.Bold(8F)
                });
            }

            AddLabel(LanguageManager.T("Menu.MonitorItem"), MonitorLayout.X_ID);
            AddLabel(LanguageManager.T("Menu.name"), MonitorLayout.X_NAME);
            AddLabel(LanguageManager.T("Menu.short"), MonitorLayout.X_SHORT);
            AddLabel(LanguageManager.T("Menu.showHide"), MonitorLayout.X_PANEL);
            AddLabel(LanguageManager.T("Menu.sort"), MonitorLayout.X_SORT);

            this.Controls.Add(header);
            header.BringToFront();
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            _container.Controls.Clear();

            // 1. 数据准备 (SortIndex 越小越靠前)
            var allItems = Config.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            var groups = allItems.GroupBy(x => x.Key.Split('.')[0]);

            // 2. 倒序添加 (因为 Dock=Top，后添加的会显示在上方)
            foreach (var g in groups.Reverse())
            {
                var block = CreateGroupBlock(g.Key, g.ToList());
                _container.Controls.Add(block);
            }

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private GroupBlock CreateGroupBlock(string groupKey, List<MonitorItemConfig> items)
        {
            string alias = Config.GroupAliases.ContainsKey(groupKey) ? Config.GroupAliases[groupKey] : "";
            
            var header = new MonitorGroupHeader(groupKey, alias);
            var rowsPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White };

            var block = new GroupBlock(header, rowsPanel);

            // 移动逻辑保持不变 (用户反馈现在是准确的)
            // MoveUp (-1) 在 Dock=Top 逻辑下通常意味着增加索引 (往顶部跑)
            header.MoveUp += (s, e) => MoveControl(block, -1);
            header.MoveDown += (s, e) => MoveControl(block, 1);

            // 行也是倒序添加
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var row = new MonitorItemRow(items[i]);
                row.MoveUp += (s, e) => MoveControl(row, -1);
                row.MoveDown += (s, e) => MoveControl(row, 1);
                rowsPanel.Controls.Add(row);
            }

            return block;
        }

        private void MoveControl(Control c, int dir)
        {
            var p = c.Parent;
            if (p == null) return;
            
            int idx = p.Controls.GetChildIndex(c);
            // 保持之前修正过的逻辑
            int newIdx = idx - dir; 

            if (newIdx >= 0 && newIdx < p.Controls.Count)
            {
                p.Controls.SetChildIndex(c, newIdx);
            }
        }

        public override void Save()
        {
            if (!_isLoaded) return;
            
            var flatList = new List<MonitorItemConfig>();
            int sortIndex = 0;

            // ★★★ 核心修复：倒序遍历 ★★★
            // WinForms Controls 集合中，Index 0 是最底部，Index Count-1 是最顶部。
            // 我们需要按照视觉顺序（从上到下）保存，所以必须从 Controls 的末尾开始遍历。
            
            // 1. 获取所有分组 (从视觉顶部到底部)
            var blocks = _container.Controls.Cast<Control>().Reverse().ToList();

            foreach (Control c in blocks)
            {
                if (c is GroupBlock block)
                {
                    // 保存别名
                    string alias = block.Header.InputAlias.Inner.Text.Trim();
                    if (!string.IsNullOrEmpty(alias)) 
                        Config.GroupAliases[block.Header.GroupKey] = alias;
                    else 
                        Config.GroupAliases.Remove(block.Header.GroupKey);

                    // 2. 获取组内所有行 (同样需要倒序遍历，从视觉顶部到底部)
                    var rows = block.RowsPanel.Controls.Cast<Control>().Reverse().ToList();

                    foreach (Control rc in rows)
                    {
                        if (rc is MonitorItemRow row)
                        {
                            row.SyncToConfig();
                            row.Config.SortIndex = sortIndex++;
                            flatList.Add(row.Config);
                        }
                    }
                }
            }

            Config.MonitorItems = flatList;
            Config.SyncToLanguage();
            AppActions.ApplyMonitorLayout(UI, MainForm);
        }

        // 内部封装类保持不变
        private class GroupBlock : Panel
        {
            public MonitorGroupHeader Header { get; private set; }
            public Panel RowsPanel { get; private set; }

            public GroupBlock(MonitorGroupHeader header, Panel rowsPanel)
            {
                this.Header = header;
                this.RowsPanel = rowsPanel;

                this.Dock = DockStyle.Top;
                this.AutoSize = true;
                this.Padding = new Padding(0, 0, 0, 20);

                var card = new LiteCard { Dock = DockStyle.Top };
                // 同样注意添加顺序：先加内容(下)，再加表头(上)
                card.Controls.Add(rowsPanel);
                card.Controls.Add(header); 

                this.Controls.Add(card);
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class TaskbarPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;
        private List<Control> _customColorInputs = new List<Control>();

        public TaskbarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) }; 
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;
            
            _container.SuspendLayout();
            _container.Controls.Clear();

            CreateGeneralGroup(); 
            CreateColorGroup();   

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateGeneralGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarSettings"));

            // 1. 总开关
            AddBool(group, "Menu.TaskbarShow", 
                () => Config.ShowTaskbar, 
                v => Config.ShowTaskbar = v,
                chk => chk.CheckedChanged += (s, e) => EnsureSafeVisibility(null, null, chk)
            );

            // 2. 鼠标穿透
            AddBool(group, "Menu.ClickThrough", () => Config.TaskbarClickThrough, v => Config.TaskbarClickThrough = v);

            // ★★★ 新增：单行模式开关 ★★★
            // 提示：你需要在语言文件(zh.json)中添加 "Menu.TaskbarSingleLine": "单行显示"
            AddBool(group, "Menu.TaskbarSingleLine", 
                () => Config.TaskbarSingleLine, 
                v => Config.TaskbarSingleLine = v
            );

            // 3. 样式 (Bold/Regular)
            AddComboIndex(group, "Menu.TaskbarStyle",
                new[] { LanguageManager.T("Menu.TaskbarStyleBold"), LanguageManager.T("Menu.TaskbarStyleRegular") },
                () => (Math.Abs(Config.TaskbarFontSize - 9f) < 0.1f && !Config.TaskbarFontBold) ? 1 : 0,
                idx => {
                    if (idx == 1) { Config.TaskbarFontSize = 9f; Config.TaskbarFontBold = false; }
                    else { Config.TaskbarFontSize = 10f; Config.TaskbarFontBold = true; }
                }
            );
            
            // ★★★ 新增：双击动作设置 ★★★
            string[] actions = { 
                LanguageManager.T("Menu.ActionToggleVisible"),    // 0: 显示/隐藏主界面
                LanguageManager.T("Menu.ActionTaskMgr"),      // 1: 任务管理器
                LanguageManager.T("Menu.ActionSettings"),           // 2: 设置
                LanguageManager.T("Menu.ActionTrafficHistory")      // 3: 历史流量
            };
            AddComboIndex(group, "Menu.DoubleClickAction", actions,
                () => Config.TaskbarDoubleClickAction,
                idx => Config.TaskbarDoubleClickAction = idx
            );

            // 4. 对齐
            AddComboIndex(group, "Menu.TaskbarAlign",
                new[] { LanguageManager.T("Menu.TaskbarAlignRight"), LanguageManager.T("Menu.TaskbarAlignLeft") },
                () => Config.TaskbarAlignLeft ? 1 : 0,
                idx => Config.TaskbarAlignLeft = (idx == 1)
            );

            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarAlignTip"), 0));
            AddGroupToPage(group);
        }

        private void CreateColorGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarCustomColors"));
            _customColorInputs.Clear();

            // 1. 【第一行-左侧】自定义开关 (AddBool 内部会调用 AddItem 占用左边一格)
            AddBool(group, "Menu.TaskbarCustomColors", 
                () => Config.TaskbarCustomStyle, 
                v => Config.TaskbarCustomStyle = v,
                chk => chk.CheckedChanged += (s, e) => {
                    foreach(var c in _customColorInputs) c.Enabled = chk.Checked;
                }
            );

            // 2. 【第一行-右侧】屏幕取色工具 (AddItem 会自动填到右边那一格)
            var tbResult = new LiteUnderlineInput("#000000", "", "", 65, null, HorizontalAlignment.Center);
            tbResult.Inner.ReadOnly = true; 
            var btnPick = new LiteSortBtn("🖌"); 
            btnPick.Location = new Point(70, 1);

            btnPick.Click += (s, e) => {
                using (Form f = new Form { FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized, TopMost = true, Cursor = Cursors.Cross })
                {
                    Bitmap bmp = new Bitmap(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                    f.BackgroundImage = bmp;
                    f.MouseClick += (ms, me) => {
                        Color c = bmp.GetPixel(me.X, me.Y);
                        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                        tbResult.Inner.Text = hex;
                        f.Close();

                        // 弹出询问：使用国际化函数
                        string confirmMsg = string.Format("{0} {1}?", LanguageManager.T("Menu.ScreenColorPickerTip"), hex);
                        if (MessageBox.Show(confirmMsg, "LiteMonitor", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        {
                            // 1. 更新物理配置
                            Config.TaskbarColorBg = hex;

                            // 2. 联动更新 UI (遍历已有的颜色输入框找到背景色那一项)
                            foreach (var control in _customColorInputs)
                            {
                                if (control is LiteColorInput ci && ci.Input.Inner.Tag?.ToString() == "Menu.BackgroundColor")
                                {
                                    ci.HexValue = hex; // 这会触发 UI 上的色块和文字同时更新
                                    break;
                                }
                            }
                        }
                    };
                    f.ShowDialog();
                }
            };

            Panel toolCtrl = new Panel { Size = new Size(96, 26) };
            toolCtrl.Controls.Add(tbResult);
            toolCtrl.Controls.Add(btnPick);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.ScreenColorPicker"), toolCtrl));

            // 3. 【第二行】说明文案 (占满一整行)
            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarCustomTip"), 0));

            // 4. 【后续行】批量添加颜色列表
            void AddC(string key, Func<string> get, Action<string> set)
            {
                var input = AddColor(group, key, get, set, Config.TaskbarCustomStyle);
                _customColorInputs.Add(input);
                
                // 为了方便上面的取色器联动，我们在创建时给 Inner 增加一个标记
                if (input is LiteColorInput lci)
                {
                    lci.Input.Inner.Tag = key;
                }
            }

            AddC("Menu.LabelColor",      () => Config.TaskbarColorLabel, v => Config.TaskbarColorLabel = v);
            AddC("Menu.ValueSafeColor",  () => Config.TaskbarColorSafe,  v => Config.TaskbarColorSafe = v);
            AddC("Menu.ValueWarnColor",  () => Config.TaskbarColorWarn,  v => Config.TaskbarColorWarn = v);
            AddC("Menu.ValueCritColor",  () => Config.TaskbarColorCrit,  v => Config.TaskbarColorCrit = v);
            AddC("Menu.BackgroundColor", () => Config.TaskbarColorBg,    v => Config.TaskbarColorBg = v);

            AddGroupToPage(group);
        }
        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
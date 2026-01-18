using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq; 
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
        
        // 缓存自定义布局控件，便于联动启用/禁用
        private List<Control> _customLayoutInputs = new List<Control>();
        // 缓存样式下拉框，用于互斥控制
        private Control _styleCombo;
        // ★★★ [新增] 缓存总开关控件，用于在Save时优先读取 ★★★
        private CheckBox _chkCustomLayout;

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
            
            // Dispose old controls
            while (_container.Controls.Count > 0)
            {
                var ctrl = _container.Controls[0];
                _container.Controls.RemoveAt(0);
                ctrl.Dispose();
            }

            CreateGeneralGroup(); 
            CreateLayoutGroup();
            CreateColorGroup();   

            _container.ResumeLayout();
            _isLoaded = true;
        }

        // ★★★ 核心修复：重写保存逻辑 ★★★
        public override void Save()
        {
            // 1. 【关键步骤】保存前，强制先更新“是否开启自定义”的状态
            // 否则后续保存 CheckBox(粗体) 时，Config.TaskbarCustomLayout 还是旧值，会导致逻辑判断失效
            if (_chkCustomLayout != null)
            {
                Config.TaskbarCustomLayout = _chkCustomLayout.Checked;
            }

            // 2. 执行基类保存（这会触发所有控件的 Setter）
            // 因为第1步已经更新了开关，所以 chkBold 的 Setter 里的 if 判断就能正确工作了
            base.Save();

            // 3. 持久化到磁盘
            Config.Save();

            // 4. 强制刷新渲染器缓存
            // 此时 Config 里的 FontBold 和 CustomLayout 都是完全正确的组合
            TaskbarRenderer.ReloadStyle(Config);
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

            // 3. 样式 (Bold/Regular)
            var combo = AddComboIndex(group, "Menu.TaskbarStyle",
                new[] { LanguageManager.T("Menu.TaskbarStyleBold"), LanguageManager.T("Menu.TaskbarStyleRegular") },
                // 只有在【标准模式 + 9pt】时才显示为"小字"，否则默认"大字"
                () => (!Config.TaskbarFontBold && Math.Abs(Config.TaskbarFontSize - 9f) < 0.1f) ? 1 : 0,
                idx => {
                    // 只有在【未开启自定义】时，才允许修改标准配置
                    if (!Config.TaskbarCustomLayout) {
                        if (idx == 1) { Config.TaskbarFontSize = 9f; Config.TaskbarFontBold = false; } // 小字
                        else { Config.TaskbarFontSize = 10f; Config.TaskbarFontBold = true; } // 大字
                    }
                }
            );
            _styleCombo = combo; 
            _styleCombo.Enabled = !Config.TaskbarCustomLayout; // 初始状态

             // 4. 单行显示
            AddBool(group, "Menu.TaskbarSingleLine", 
                () => Config.TaskbarSingleLine, 
                v => Config.TaskbarSingleLine = v
            );

            // 2. 鼠标穿透
            AddBool(group, "Menu.ClickThrough", () => Config.TaskbarClickThrough, v => Config.TaskbarClickThrough = v);
           
            // 选择显示器
            var screens = Screen.AllScreens;
            var screenNames = screens.Select((s, i) => 
                $"{i + 1}: {s.DeviceName.Replace(@"\\.\DISPLAY", "Display ")}{(s.Primary ? " [Main]" : "")}"
            ).ToList();
            
            screenNames.Insert(0, LanguageManager.T("Menu.Auto"));
            AddComboIndex(group, "Menu.TaskbarMonitor", screenNames.ToArray(), 
                () => {
                    if (string.IsNullOrEmpty(Config.TaskbarMonitorDevice)) return 0;
                    var idx = Array.FindIndex(screens, s => s.DeviceName == Config.TaskbarMonitorDevice);
                    return idx >= 0 ? idx + 1 : 0;
                },
                idx => {
                    if (idx == 0) Config.TaskbarMonitorDevice = ""; 
                    else Config.TaskbarMonitorDevice = screens[idx - 1].DeviceName;
                }
            );

            // 5. 双击操作
            string[] actions = { 
                LanguageManager.T("Menu.ActionToggleVisible"),
                LanguageManager.T("Menu.ActionTaskMgr"), 
                LanguageManager.T("Menu.ActionSettings"),
                LanguageManager.T("Menu.ActionTrafficHistory")
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

            // 手动偏移量修正
            AddNumberInt(group, "Menu.TaskbarOffset", "px", 
                () => Config.TaskbarManualOffset, 
                v => Config.TaskbarManualOffset = v
            );

            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarAlignTip"), 0));
            AddGroupToPage(group);
        }

        private void CreateLayoutGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarCustomLayout")); 
            _customLayoutInputs.Clear();

            // 1. 自定义总开关
            AddBool(group, "Menu.TaskbarCustomLayout", 
                () => Config.TaskbarCustomLayout, 
                v => Config.TaskbarCustomLayout = v,
                chk => {
                    // ★ 捕获控件引用
                    _chkCustomLayout = chk as CheckBox; 
                    chk.CheckedChanged += (s, e) => {
                        // 界面联动
                        foreach(var c in _customLayoutInputs) c.Enabled = chk.Checked;
                        if (_styleCombo != null) _styleCombo.Enabled = !chk.Checked;
                    };
                }
            );

            void AddL(string key, Control ctrl) {
                _customLayoutInputs.Add(ctrl);
                ctrl.Enabled = Config.TaskbarCustomLayout;
            }

            // 2. 字体选择
            var installedFonts = System.Drawing.FontFamily.Families.Select(f => f.Name).ToList();
            if (!installedFonts.Contains(Config.TaskbarFontFamily)) 
                installedFonts.Insert(0, Config.TaskbarFontFamily);

            var cbFont = AddCombo(group, "Menu.TaskbarFont", installedFonts, 
                () => Config.TaskbarFontFamily, 
                v => Config.TaskbarFontFamily = v
            );
            AddL("", cbFont);

            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarCustomLayoutTip"), 0));

            // 3. 字号
            var nbSize = AddNumberDouble(group, "Menu.TaskbarFontSize", "pt", 
                () => Config.TaskbarFontSize, 
                v => Config.TaskbarFontSize = (float)v
            );
            AddL("", nbSize);

            // 4. 粗体
            // ★★★ 修复：增加条件锁，只有开启自定义时才允许写入配置 ★★★
            var chkBold = AddBool(group, "Menu.TaskbarFontBold", 
                () => Config.TaskbarFontBold, 
                v => { 
                    // 如果当前是标准模式，禁止这个控件修改 Config
                    // 这样就不会把标准模式的"不加粗"覆盖成"加粗"了
                    if (Config.TaskbarCustomLayout) Config.TaskbarFontBold = v; 
                }
            );
            AddL("", chkBold);

            // 5. 间距配置
            var nbItemSp = AddNumberInt(group, "Menu.TaskbarItemSpacing", "px", 
                () => Config.TaskbarItemSpacing, 
                v => Config.TaskbarItemSpacing = v
            );
            AddL("", nbItemSp);

            var nbInnerSp = AddNumberInt(group, "Menu.TaskbarInnerSpacing", "px", 
                () => Config.TaskbarInnerSpacing, 
                v => Config.TaskbarInnerSpacing = v
            );
            AddL("", nbInnerSp);

            var nbVertPad = AddNumberInt(group, "Menu.TaskbarVerticalPadding", "px", 
                () => Config.TaskbarVerticalPadding, 
                v => Config.TaskbarVerticalPadding = v
            );
            AddL("", nbVertPad);

            AddGroupToPage(group);
        }

        private void CreateColorGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarCustomColors"));
            _customColorInputs.Clear();

            AddBool(group, "Menu.TaskbarCustomColors", 
                () => Config.TaskbarCustomStyle, 
                v => Config.TaskbarCustomStyle = v,
                chk => chk.CheckedChanged += (s, e) => {
                    foreach(var c in _customColorInputs) c.Enabled = chk.Checked;
                }
            );

            // 屏幕取色器
            var tbResult = new LiteUnderlineInput("#000000", "", "", 65, null, HorizontalAlignment.Center);
            tbResult.Padding = UIUtils.S(new Padding(0, 5, 0, 1)); 
            tbResult.Inner.ReadOnly = true; 

            var btnPick = new LiteSortBtn("🖌"); 
            btnPick.Location = new Point(UIUtils.S(70), UIUtils.S(1));

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
                        
                        string confirmMsg = string.Format("{0} {1}?", LanguageManager.T("Menu.ScreenColorPickerTip"), hex);
                        if (MessageBox.Show(confirmMsg, "LiteMonitor", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        {
                            Config.TaskbarColorBg = hex;
                            foreach (var control in _customColorInputs)
                            {
                                if (control is LiteColorInput ci && ci.Input.Inner.Tag?.ToString() == "Menu.BackgroundColor")
                                {
                                    ci.HexValue = hex; 
                                    break;
                                }
                            }
                        }
                    };
                    f.ShowDialog();
                }
            };

            Panel toolCtrl = new Panel { Size = new Size(UIUtils.S(96), UIUtils.S(26)) };
            toolCtrl.Controls.Add(tbResult);
            toolCtrl.Controls.Add(btnPick);
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.ScreenColorPicker"), toolCtrl));

            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarCustomTip"), 0));

            void AddC(string key, Func<string> get, Action<string> set)
            {
                var input = AddColor(group, key, get, set, Config.TaskbarCustomStyle);
                _customColorInputs.Add(input);
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
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, UIUtils.S(20)) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
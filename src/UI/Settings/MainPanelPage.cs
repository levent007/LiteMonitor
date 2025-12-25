using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MainPanelPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        // 控件引用保留，用于布局或事件，但不再需要在Save中手动读取
        private LiteCheck _chkHideMain;
        private LiteCheck _chkAutoHide;
        private LiteCheck _chkTopMost;
        private LiteCheck _chkClickThrough;
        private LiteCheck _chkClamp;

        private LiteComboBox _cmbTheme;
        private LiteComboBox _cmbOrientation;
        private LiteComboBox _cmbWidth;
        private LiteComboBox _cmbOpacity;
        private LiteComboBox _cmbScale;

        public MainPanelPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow(); // ★ 必须调用：清理旧的绑定
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            _container.Controls.Clear();

            CreateBehaviorCard();
            CreateAppearanceCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateBehaviorCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.MainFormSettings"));

            // 1. 显示/隐藏开关 (绑定 + 安全检查)
            _chkHideMain = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkHideMain, 
                () => Config.HideMainForm, 
                v => Config.HideMainForm = v);
            
            // 安全检查事件：当用户点击时触发检查
            _chkHideMain.CheckedChanged += (s, e) => EnsureSafeVisibility(_chkHideMain, null, null);
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.HideMainForm"), _chkHideMain));

            // 2. 窗口置顶
            _chkTopMost = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkTopMost, () => Config.TopMost, v => Config.TopMost = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TopMost"), _chkTopMost));

            // 3. 限制在屏幕内
            _chkClamp = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkClamp, () => Config.ClampToScreen, v => Config.ClampToScreen = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.ClampToScreen"), _chkClamp));
            
            // 4. 自动隐藏
            _chkAutoHide = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkAutoHide, () => Config.AutoHide, v => Config.AutoHide = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.AutoHide"), _chkAutoHide));

            // 5. 鼠标穿透
            _chkClickThrough = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkClickThrough, () => Config.ClickThrough, v => Config.ClickThrough = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.ClickThrough"), _chkClickThrough));

            AddGroupToPage(group);
        }

        private void CreateAppearanceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Appearance"));

            // 1. 主题 (BindCombo)
            _cmbTheme = new LiteComboBox();
            foreach (var t in ThemeManager.GetAvailableThemes()) _cmbTheme.Items.Add(t);
            BindCombo(_cmbTheme, 
                () => Config.Skin, 
                v => Config.Skin = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Theme"), _cmbTheme));

            // 2. 方向 (BindComboIndex - 稍微特殊一点，映射 bool 到 0/1)
            _cmbOrientation = new LiteComboBox();
            _cmbOrientation.Items.Add(LanguageManager.T("Menu.Vertical"));   
            _cmbOrientation.Items.Add(LanguageManager.T("Menu.Horizontal"));
            BindComboIndex(_cmbOrientation, 
                () => Config.HorizontalMode ? 1 : 0, 
                idx => Config.HorizontalMode = (idx == 1));
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.DisplayMode"), _cmbOrientation));

            // 3. 宽度 (带单位处理)
            _cmbWidth = new LiteComboBox();
            int[] widths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            foreach (var w in widths) _cmbWidth.Items.Add(w + " px");
            
            BindCombo(_cmbWidth,
                () => Config.PanelWidth + " px", 
                s => Config.PanelWidth = UIUtils.ParseInt(s)); // 利用 UIUtils 自动忽略 " px"
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Width"), _cmbWidth));

            // 4. 缩放 (带百分比转换)
            _cmbScale = new LiteComboBox();
            double[] scales = { 0.5, 0.75, 0.9, 1.0, 1.25, 1.5, 1.75, 2.0 };
            foreach (var s in scales) _cmbScale.Items.Add((s * 100) + "%");

            BindCombo(_cmbScale,
                () => (Config.UIScale * 100) + "%",
                s => Config.UIScale = UIUtils.ParseDouble(s) / 100.0); // 利用 UIUtils 忽略 "%" 并转回小数

            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Scale"), _cmbScale));

            // 5. 透明度
            _cmbOpacity = new LiteComboBox();
            double[] presetOps = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            foreach (var op in presetOps) _cmbOpacity.Items.Add((op * 100) + "%");

            BindCombo(_cmbOpacity,
                () => Math.Round(Config.Opacity * 100) + "%",
                s => Config.Opacity = UIUtils.ParseDouble(s) / 100.0);

            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Opacity"), _cmbOpacity));

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }

        public override void Save()
        {
            if (!_isLoaded) return;

            // ★ 核心改变：调用基类 Save() 自动执行所有 Bind 的 setter
            base.Save();

            // 执行应用变更的操作 (UI刷新等)
            AppActions.ApplyVisibility(Config, MainForm);
            AppActions.ApplyWindowAttributes(Config, MainForm);
            AppActions.ApplyThemeAndLayout(Config, UI, MainForm);
        }
    }
}
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class AppearancePage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        private LiteComboBox _cmbTheme;
        private LiteComboBox _cmbOrientation;
        private LiteComboBox _cmbWidth;
        private LiteComboBox _cmbOpacity;
        private LiteComboBox _cmbScale;
        
        private LiteCheck _chkTaskbarCompact;
        private LiteCheck _chkTaskbarAlignLeft;

        public AppearancePage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            if (Config == null || _isLoaded) return;
            _container.SuspendLayout();
            _container.Controls.Clear();

            CreateThemeCard();
            CreateTaskbarCard(); 

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateThemeCard()
        {
            var group = new LiteSettingsGroup("主界面设置");

            _cmbTheme = new LiteComboBox();
            foreach (var t in ThemeManager.GetAvailableThemes()) _cmbTheme.Items.Add(t);
            SetComboVal(_cmbTheme, Config.Skin);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Theme"), _cmbTheme));

            _cmbOrientation = new LiteComboBox();
            _cmbOrientation.Items.Add("Vertical");
            _cmbOrientation.Items.Add("Horizontal");
            _cmbOrientation.SelectedIndex = Config.HorizontalMode ? 1 : 0;
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.DisplayMode"), _cmbOrientation));

            _cmbWidth = new LiteComboBox();
            int[] widths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 600, 800 };
            foreach (var w in widths) _cmbWidth.Items.Add(w + " px");
            SetComboVal(_cmbWidth, Config.PanelWidth + " px");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Width"), _cmbWidth));

              _cmbScale = new LiteComboBox();
            double[] scales = { 0.5, 0.75, 0.9, 1.0, 1.25, 1.5, 1.75, 2.0 };
            foreach (var s in scales) _cmbScale.Items.Add((s * 100) + "%");
            SetComboVal(_cmbScale, (Config.UIScale * 100) + "%");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Scale"), _cmbScale));

            _cmbOpacity = new LiteComboBox();
            for (int i = 100; i >= 30; i -= 10) _cmbOpacity.Items.Add(i + "%");
            SetComboVal(_cmbOpacity, Math.Round(Config.Opacity * 100) + "%");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Opacity"), _cmbOpacity));

            AddGroupToPage(group);
        }


        private void CreateTaskbarCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarSettings"));

            bool isCompact = (Math.Abs(Config.TaskbarFontSize - 9f) < 0.1f) && !Config.TaskbarFontBold;
            // ★ 传入 "Enable"
            _chkTaskbarCompact = new LiteCheck(isCompact, "Enable");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarCompact"), _chkTaskbarCompact));

            // ★ 传入 "Enable"
            _chkTaskbarAlignLeft = new LiteCheck(Config.TaskbarAlignLeft, "Enable");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarAlignLeft"), _chkTaskbarAlignLeft));

            var tips = new Label { 
                Text = "Note: Alignment only works when Win11 Taskbar is centered.", 
                AutoSize = true, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 8F),
                Padding = new Padding(5)
            };
            group.AddFullItem(tips);

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
        
        private void SetComboVal(LiteComboBox cmb, string val)
        {
            if (!cmb.Items.Contains(val)) cmb.Items.Insert(0, val);
            cmb.SelectedItem = val;
        }

        public override void Save()
        {
            if (!_isLoaded) return;

            if (_cmbTheme.SelectedItem != null) Config.Skin = _cmbTheme.SelectedItem.ToString();
            Config.HorizontalMode = (_cmbOrientation.SelectedIndex == 1);
            Config.PanelWidth = ParseInt(_cmbWidth.Text);
            Config.UIScale = ParsePercent(_cmbScale.Text);
            Config.Opacity = ParsePercent(_cmbOpacity.Text);

            if (_chkTaskbarCompact.Checked) {
                Config.TaskbarFontSize = 9f;
                Config.TaskbarFontBold = false;
            } else {
                Config.TaskbarFontSize = 10f;
                Config.TaskbarFontBold = true;
            }
            Config.TaskbarAlignLeft = _chkTaskbarAlignLeft.Checked;
        }

        private int ParseInt(string s) {
            string clean = new string(s.Where(char.IsDigit).ToArray());
            return int.TryParse(clean, out int v) ? v : 0;
        }
        private double ParsePercent(string s) {
            int v = ParseInt(s);
            return v > 0 ? v / 100.0 : 1.0;
        }
    }
}
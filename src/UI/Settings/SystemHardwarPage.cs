using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class SystemHardwarPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;
        private string _originalLanguage; // 用于检测语言是否变更

        public SystemHardwarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }
    
        public override void OnShow()
        {
            base.OnShow(); // ★ 必须调用
            if (Config == null || _isLoaded) return;
            
            _container.SuspendLayout();
            _container.Controls.Clear();
            
            _originalLanguage = Config.Language; // 记录初始语言

            CreateSystemCard();   
            CreateCalibrationCard();
            CreateSourceCard();   

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateSystemCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.SystemSettings"));
            
            // 1. 语言选择
            var cmbLang = new LiteComboBox();
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            if (Directory.Exists(langDir)) {
                foreach (var file in Directory.EnumerateFiles(langDir, "*.json")) {
                    string code = Path.GetFileNameWithoutExtension(file);
                    cmbLang.Items.Add(code.ToUpper()); // 显示大写代码
                }
            }
            // 绑定语言：显示时大写匹配，保存时存小写
            // (注意：这里简化了逻辑，假设文件名就是语言代码)
            BindCombo(cmbLang, 
                () => string.IsNullOrEmpty(Config.Language) ? "EN" : Config.Language.ToUpper(),
                v => Config.Language = (v == "AUTO") ? "" : v.ToLower());
                
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Language"), cmbLang));

            // 2. 开机自启
            var chkAutoStart = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(chkAutoStart, () => Config.AutoStart, v => Config.AutoStart = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.AutoStart"), chkAutoStart));

            // 3. 隐藏托盘图标 (带安全检查)
            var chkHideTray = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(chkHideTray, 
                () => Config.HideTrayIcon, 
                v => Config.HideTrayIcon = v);
                
            chkHideTray.CheckedChanged += (s, e) => EnsureSafeVisibility(null, chkHideTray, null);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.HideTrayIcon"), chkHideTray));

            AddGroupToPage(group);
        }

        private void CreateCalibrationCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Calibration"));
            string suffix = " (" + LanguageManager.T("Menu.MaxLimits") + ")";

            // 辅助方法：快速添加一行校准输入
            void AddCalibItem(string title, string unit, Func<float> get, Action<float> set)
            {
                var input = new LiteUnderlineInput("0", unit, "", 60);
                // BindDouble 通用性更强，我们在 setter 里转 float
                BindDouble(input, () => get(), v => set((float)v)); 
                group.AddItem(new LiteSettingsItem(title + suffix, input));
            }

            AddCalibItem(LanguageManager.T("Items.CPU.Power"), "W", 
                () => Config.RecordedMaxCpuPower, v => Config.RecordedMaxCpuPower = v);

            AddCalibItem(LanguageManager.T("Items.CPU.Clock"), "MHz", 
                () => Config.RecordedMaxCpuClock, v => Config.RecordedMaxCpuClock = v);

            AddCalibItem(LanguageManager.T("Items.GPU.Power"), "W", 
                () => Config.RecordedMaxGpuPower, v => Config.RecordedMaxGpuPower = v);

            AddCalibItem(LanguageManager.T("Items.GPU.Clock"), "MHz", 
                () => Config.RecordedMaxGpuClock, v => Config.RecordedMaxGpuClock = v);

            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.CalibrationTip"), 0));
            AddGroupToPage(group);
        }

        private void CreateSourceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.HardwareSettings"));
            string strAuto = LanguageManager.T("Menu.Auto"); 

            // 1. 磁盘源
            var cmbDisk = new LiteComboBox();
            cmbDisk.Items.Add(strAuto); 
            foreach (var d in HardwareMonitor.ListAllDisks()) cmbDisk.Items.Add(d);
            
            // 绑定逻辑：如果是空字符串显示 "Auto"，否则显示具体值；保存反之
            BindCombo(cmbDisk, 
                () => string.IsNullOrEmpty(Config.PreferredDisk) ? strAuto : Config.PreferredDisk,
                v => Config.PreferredDisk = (v == strAuto) ? "" : v);
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.DiskSource"), cmbDisk));

            // 2. 网络源
            var cmbNet = new LiteComboBox();
            cmbNet.Items.Add(strAuto);
            foreach (var n in HardwareMonitor.ListAllNetworks()) cmbNet.Items.Add(n);
            
            BindCombo(cmbNet, 
                () => string.IsNullOrEmpty(Config.PreferredNetwork) ? strAuto : Config.PreferredNetwork,
                v => Config.PreferredNetwork = (v == strAuto) ? "" : v);

            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.NetworkSource"), cmbNet));

            // 3. 刷新率
            var cmbRefresh = new LiteComboBox();
            int[] rates = { 100, 200, 300, 500, 600, 700, 800, 1000, 1500, 2000, 3000 };
            foreach (var r in rates) cmbRefresh.Items.Add(r + " ms");
            
            BindCombo(cmbRefresh,
                () => Config.RefreshMs + " ms",
                v => {
                    int val = UIUtils.ParseInt(v);
                    Config.RefreshMs = val < 50 ? 1000 : val;
                });

            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Refresh"), cmbRefresh));
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

            // 1. 自动保存所有绑定值
            base.Save(); 

            // 2. 应用更改
            AppActions.ApplyAutoStart(Config);
            AppActions.ApplyVisibility(Config, this.MainForm);
            AppActions.ApplyMonitorLayout(this.UI, this.MainForm); // 刷新硬件源

            // 3. 语言特殊处理
            if (_originalLanguage != Config.Language) {
                AppActions.ApplyLanguage(Config, this.UI, this.MainForm);
                _originalLanguage = Config.Language; 
            }
        }
    }
}
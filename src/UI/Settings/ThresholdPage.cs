using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class ThresholdPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        // 不需要保留成员变量引用了，因为全是自动绑定
        private LiteCheck _chkAlertTemp;
        private LiteUnderlineInput _inAlertTemp;

        public ThresholdPage()
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

            // === 1. 高温告警分组 ===
            var grpAlert = new LiteSettingsGroup(LanguageManager.T("Menu.AlertTemp"));
            
            // 开关
            _chkAlertTemp = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkAlertTemp, 
                () => Config.AlertTempEnabled, 
                v => Config.AlertTempEnabled = v);
            grpAlert.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.AlertTemp"), _chkAlertTemp));

            // 阈值 (int)
            _inAlertTemp = new LiteUnderlineInput("0", "°C", "", 80, UIColors.TextCrit, HorizontalAlignment.Center);
            BindInt(_inAlertTemp, 
                () => Config.AlertTempThreshold, 
                v => Config.AlertTempThreshold = v);
            grpAlert.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.AlertThreshold"), _inAlertTemp));

            AddGroupToPage(grpAlert);

            // === 2. 硬件负载 ===
            var grpHardware = new LiteSettingsGroup(LanguageManager.T("Menu.GeneralHardware"));
            
            AddThresholdRow(grpHardware, LanguageManager.T("Menu.HardwareLoad"), 
                () => Config.Thresholds.Load.Warn, v => Config.Thresholds.Load.Warn = v,
                () => Config.Thresholds.Load.Crit, v => Config.Thresholds.Load.Crit = v,
                "%", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddThresholdRow(grpHardware, LanguageManager.T("Menu.HardwareTemp"), 
                () => Config.Thresholds.Temp.Warn, v => Config.Thresholds.Temp.Warn = v,
                () => Config.Thresholds.Temp.Crit, v => Config.Thresholds.Temp.Crit = v,
                "°C", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddGroupToPage(grpHardware);

            // === 3. 网络与磁盘 ===
            var grpNet = new LiteSettingsGroup(LanguageManager.T("Menu.NetworkDiskSpeed"));
            
            AddThresholdRow(grpNet, LanguageManager.T("Menu.DiskIOSpeed"), 
                () => Config.Thresholds.DiskIOMB.Warn, v => Config.Thresholds.DiskIOMB.Warn = v,
                () => Config.Thresholds.DiskIOMB.Crit, v => Config.Thresholds.DiskIOMB.Crit = v,
                "MB/s", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddThresholdRow(grpNet, LanguageManager.T("Menu.UploadSpeed"), 
                () => Config.Thresholds.NetUpMB.Warn, v => Config.Thresholds.NetUpMB.Warn = v,
                () => Config.Thresholds.NetUpMB.Crit, v => Config.Thresholds.NetUpMB.Crit = v,
                "MB/s", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddThresholdRow(grpNet, LanguageManager.T("Menu.DownloadSpeed"), 
                () => Config.Thresholds.NetDownMB.Warn, v => Config.Thresholds.NetDownMB.Warn = v,
                () => Config.Thresholds.NetDownMB.Crit, v => Config.Thresholds.NetDownMB.Crit = v,
                "MB/s", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddGroupToPage(grpNet);

            // === 4. 流量限额 ===
            var grpData = new LiteSettingsGroup(LanguageManager.T("Menu.DailyTraffic"));

            AddThresholdRow(grpData, LanguageManager.T("Items.DATA.DayUp"), 
                () => Config.Thresholds.DataUpMB.Warn, v => Config.Thresholds.DataUpMB.Warn = v,
                () => Config.Thresholds.DataUpMB.Crit, v => Config.Thresholds.DataUpMB.Crit = v,
                "MB", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddThresholdRow(grpData, LanguageManager.T("Items.DATA.DayDown"), 
                () => Config.Thresholds.DataDownMB.Warn, v => Config.Thresholds.DataDownMB.Warn = v,
                () => Config.Thresholds.DataDownMB.Crit, v => Config.Thresholds.DataDownMB.Crit = v,
                "MB", LanguageManager.T("Menu.ValueWarnColor"), LanguageManager.T("Menu.ValueCritColor"));

            AddGroupToPage(grpData);

            _container.ResumeLayout();
            _isLoaded = true;
        }

        // 改造后的 Helper：直接接收 Getter/Setter 委托
        private void AddThresholdRow(
            LiteSettingsGroup group, 
            string title, 
            Func<double> getWarn, Action<double> setWarn,
            Func<double> getCrit, Action<double> setCrit,
            string unit,
            string labelWarn, 
            string labelCrit)
        {
            var panel = new Panel { Height = 40, Margin = new Padding(0), Padding = new Padding(0) };

            var lblTitle = new Label {
                Text = title, AutoSize = true, 
                Font = new Font("Microsoft YaHei UI", 9F), ForeColor = UIColors.TextMain,
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(lblTitle);

            // 右侧流式布局
            var rightBox = new FlowLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, 
                BackColor = Color.Transparent, Padding = new Padding(0)
            };

            // 创建控件
            var inputWarn = new LiteUnderlineInput("0", unit, labelWarn, 140, UIColors.TextWarn, HorizontalAlignment.Center);
            var arrow = new Label { 
                Text = "➜", AutoSize = true, ForeColor = Color.LightGray, 
                Font = new Font("Microsoft YaHei UI", 9F), Margin = new Padding(5, 4, 5, 0) 
            };
            var inputCrit = new LiteUnderlineInput("0", unit, labelCrit, 140, UIColors.TextCrit, HorizontalAlignment.Center);

            // ★ 核心：立即绑定 (Config.Thresholds 都是 double)
            BindDouble(inputWarn, getWarn, setWarn);
            BindDouble(inputCrit, getCrit, setCrit);

            rightBox.Controls.Add(inputWarn);
            rightBox.Controls.Add(arrow);
            rightBox.Controls.Add(inputCrit);

            panel.Controls.Add(rightBox);

            // 布局事件
            panel.Layout += (s, e) => {
                lblTitle.Location = new Point(0, (panel.Height - lblTitle.Height) / 2);
                rightBox.Location = new Point(panel.Width - rightBox.Width, (panel.Height - rightBox.Height) / 2);
            };

            // 底部分割线
            panel.Paint += (s, e) => {
                using(var p = new Pen(Color.FromArgb(240, 240, 240))) 
                    e.Graphics.DrawLine(p, 0, panel.Height-1, panel.Width, panel.Height-1);
            };

            group.AddFullItem(panel);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
        
        // ★ Save() 方法已被基类接管，无需重写
    }
}
using System;
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
        
        // 需要在交互中引用的控件
        private LiteCheck _chkTaskbarCustom;
        private LiteColorInput _inColorLabel, _inColorSafe, _inColorWarn, _inColorCrit, _inColorBg;

        public TaskbarPage()
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

            CreateGeneralGroup(); 
            CreateColorGroup();   

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateGeneralGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarSettings"));

            // 1. 总开关 (带安全检查)
            var chkShowTaskbar = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(chkShowTaskbar, 
                () => Config.ShowTaskbar, 
                v => Config.ShowTaskbar = v);
            
            chkShowTaskbar.CheckedChanged += (s, e) => EnsureSafeVisibility(null, null, chkShowTaskbar);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarShow"), chkShowTaskbar));

            // 2. 鼠标穿透
            var chkClickThrough = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(chkClickThrough, () => Config.TaskbarClickThrough, v => Config.TaskbarClickThrough = v);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.ClickThrough"), chkClickThrough));   

            // 3. 样式 (Bold/Regular 映射逻辑)
            var cmbStyle = new LiteComboBox();
            cmbStyle.Items.Add(LanguageManager.T("Menu.TaskbarStyleBold"));    // Index 0
            cmbStyle.Items.Add(LanguageManager.T("Menu.TaskbarStyleRegular")); // Index 1
            
            BindComboIndex(cmbStyle,
                () => {
                    // 判断是否为 Regular (9号字且非粗体)
                    bool isCompact = (Math.Abs(Config.TaskbarFontSize - 9f) < 0.1f) && !Config.TaskbarFontBold;
                    return isCompact ? 1 : 0;
                },
                idx => {
                    if (idx == 1) { // Regular
                        Config.TaskbarFontSize = 9f;
                        Config.TaskbarFontBold = false;
                    } else {        // Bold
                        Config.TaskbarFontSize = 10f;
                        Config.TaskbarFontBold = true;
                    }
                });
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarStyle"), cmbStyle));

            // 4. 对齐
            var cmbAlign = new LiteComboBox();
            cmbAlign.Items.Add(LanguageManager.T("Menu.TaskbarAlignRight")); // Index 0
            cmbAlign.Items.Add(LanguageManager.T("Menu.TaskbarAlignLeft"));  // Index 1
            
            BindComboIndex(cmbAlign,
                () => Config.TaskbarAlignLeft ? 1 : 0,
                idx => Config.TaskbarAlignLeft = (idx == 1));
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarAlign"), cmbAlign));

            // 提示
            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarAlignTip"), 0));
            AddGroupToPage(group);
        }

        private void CreateColorGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarCustomColors"));

            // 1. 自定义开关
            _chkTaskbarCustom = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(_chkTaskbarCustom, 
                () => Config.TaskbarCustomStyle, 
                v => Config.TaskbarCustomStyle = v);
            
            // 交互：开关影响下面控件状态
            _chkTaskbarCustom.CheckedChanged += (s, e) => ToggleColorInputs(_chkTaskbarCustom.Checked);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarCustomColors"), _chkTaskbarCustom));
            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.TaskbarCustomTip"), 0));

            // 辅助方法：快速添加颜色输入
            LiteColorInput AddColorItem(string titleKey, Func<string> get, Action<string> set)
            {
                // 注意：LiteColorInput 构造函数需要传入初始 hex，但我们现在通过 Bind 来赋值
                // 所以这里传空或默认即可，OnShow 时 Bind 会刷新它
                var input = new LiteColorInput(get()); 
                BindColor(input, get, set);
                group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
                return input;
            }

            _inColorLabel = AddColorItem("Menu.LabelColor",      () => Config.TaskbarColorLabel, v => Config.TaskbarColorLabel = v);
            _inColorSafe  = AddColorItem("Menu.ValueSafeColor",  () => Config.TaskbarColorSafe,  v => Config.TaskbarColorSafe = v);
            _inColorWarn  = AddColorItem("Menu.ValueWarnColor",  () => Config.TaskbarColorWarn,  v => Config.TaskbarColorWarn = v);
            _inColorCrit  = AddColorItem("Menu.ValueCritColor",  () => Config.TaskbarColorCrit,  v => Config.TaskbarColorCrit = v);
            _inColorBg    = AddColorItem("Menu.BackgroundColor", () => Config.TaskbarColorBg,    v => Config.TaskbarColorBg = v);

            AddGroupToPage(group);
            
            // 初始化启用状态
            ToggleColorInputs(Config.TaskbarCustomStyle);
        }

        private void ToggleColorInputs(bool enabled)
        {
            // 如果 LiteColorInput 没有实现 Enabled 属性的视觉变化，可以用 Visible
            // 或者假设你在 LiteColorInput 里已经处理了 Enabled
            if (_inColorLabel == null) return;
            
            _inColorLabel.Enabled = enabled;
            _inColorSafe.Enabled = enabled;
            _inColorWarn.Enabled = enabled;
            _inColorCrit.Enabled = enabled;
            _inColorBg.Enabled = enabled;
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

            base.Save(); // ★ 自动保存常规和颜色设置

            // 应用变更
            AppActions.ApplyVisibility(Config, MainForm);
            AppActions.ApplyTaskbarStyle(Config, UI);
        }
    }
}
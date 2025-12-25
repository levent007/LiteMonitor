using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public interface ISettingsPage
    {
        void Save();
        void OnShow();
    }

    public class SettingsPageBase : UserControl, ISettingsPage
    {
        protected Settings Config;
        protected MainForm MainForm;
        protected UIController UI;
        
        // ★ 核心 1: 自动保存队列
        private List<Action> _saveActions = new List<Action>();

        public static readonly Color GlobalBackColor = Color.FromArgb(249, 249, 249); 

        public SettingsPageBase() 
        {
            this.BackColor = GlobalBackColor; 
            this.Dock = DockStyle.Fill;
        }

        public void SetContext(Settings cfg, MainForm form, UIController ui)
        {
            Config = cfg;
            MainForm = form;
            UI = ui;
        }

        // ★ 核心 2: 强力绑定方法群

        // 绑定 CheckBox
        protected void BindCheck(LiteCheck chk, Func<bool> getter, Action<bool> setter)
        {
            chk.Checked = getter();
            _saveActions.Add(() => setter(chk.Checked));
        }

        // 绑定 ComboBox (基于 Items 字符串匹配)
        protected void BindCombo(LiteComboBox cmb, Func<string> getter, Action<string> setter)
        {
            string val = getter();
            if (!cmb.Items.Contains(val)) cmb.Items.Insert(0, val);
            cmb.SelectedItem = val;

            _saveActions.Add(() => {
                if (cmb.SelectedItem != null) setter(cmb.SelectedItem.ToString());
            });
        }
        
        // 绑定 ComboBox (基于索引)
        protected void BindComboIndex(LiteComboBox cmb, Func<int> getter, Action<int> setter)
        {
            int idx = getter();
            if (idx >= 0 && idx < cmb.Items.Count) cmb.SelectedIndex = idx;
            _saveActions.Add(() => setter(cmb.SelectedIndex));
        }

        // 绑定 输入框 (自动解析 int)
        protected void BindInt(LiteUnderlineInput input, Func<int> getter, Action<int> setter)
        {
            input.Inner.Text = getter().ToString();
            _saveActions.Add(() => setter(UIUtils.ParseInt(input.Inner.Text)));
        }

        // 绑定 输入框 (自动解析 double)
        protected void BindDouble(LiteUnderlineInput input, Func<double> getter, Action<double> setter)
        {
            input.Inner.Text = getter().ToString(); // 默认ToString，也可以加格式化参数
            _saveActions.Add(() => setter(UIUtils.ParseDouble(input.Inner.Text)));
        }
        
        // 绑定 颜色输入
        protected void BindColor(LiteColorInput input, Func<string> getter, Action<string> setter)
        {
            input.HexValue = getter();
            _saveActions.Add(() => setter(input.HexValue));
        }

        // ★ 核心 3: 统一安全检查
        protected void EnsureSafeVisibility(LiteCheck chkHideMain, LiteCheck chkHideTray, LiteCheck chkShowTaskbar)
        {
            // 如果传入 null，说明当前页面只控制其中一项，其他项从 Config 读取
            bool hideMain = chkHideMain != null ? chkHideMain.Checked : Config.HideMainForm;
            bool hideTray = chkHideTray != null ? chkHideTray.Checked : Config.HideTrayIcon;
            bool showBar  = chkShowTaskbar != null ? chkShowTaskbar.Checked : Config.ShowTaskbar;

            if (hideMain && hideTray && !showBar)
            {
                MessageBox.Show("为了防止程序无法唤出，不能同时隐藏 [主界面]、[托盘图标] 和 [任务栏]。", 
                                "安全警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                
                // 强制回滚当前操作的控件
                if (chkHideMain != null) chkHideMain.Checked = false;
                if (chkHideTray != null) chkHideTray.Checked = false;
                if (chkShowTaskbar != null) chkShowTaskbar.Checked = true;
            }
        }

        public virtual void Save() 
        {
            // 自动执行所有绑定
            foreach (var action in _saveActions) action();
        }

        public virtual void OnShow() 
        {
            // 清理旧的绑定动作，防止重复添加 (非常重要！)
            //_saveActions.Clear();
        }
    }
}
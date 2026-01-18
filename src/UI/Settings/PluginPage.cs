using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class PluginPage : SettingsPageBase
    {
        private Panel _container;

        public PluginPage()
        {
            // Strictly follow MainPanelPage layout
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            // MainPanelPage uses 20 padding.
            _container = new BufferedPanel 
            { 
                Dock = DockStyle.Fill, 
                AutoScroll = true, 
                Padding = new Padding(20) 
            };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow(); // Execute any base load actions
            RebuildUI();
        }

        private void RebuildUI()
        {
            _container.SuspendLayout();
            
            // Dispose old controls to prevent GDI handle leaks
            // Win32 Parent Window Error occurs if we just Clear() without disposing
            while (_container.Controls.Count > 0)
            {
                var ctrl = _container.Controls[0];
                _container.Controls.RemoveAt(0);
                ctrl.Dispose();
            }

            var templates = PluginManager.Instance.GetAllTemplates();
            var instances = Settings.Load().PluginInstances;

            // 1. Hint Note (Standard LiteNote)
            var hint = new LiteNote("⚠️说明：如需修改插件监控目标的显示名称、单位或排序，请前往 [监控项显示] 设置页面");
            hint.Dock = DockStyle.Top;
            // Matches MainPanelPage wrapper padding style roughly
            var hintWrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            hintWrapper.Controls.Add(hint);
            _container.Controls.Add(hintWrapper);
            

            if (instances == null || instances.Count == 0)
            {
                var lbl = new Label { 
                    Text = "暂无插件实例", 
                    AutoSize = true, 
                    ForeColor = UIColors.TextSub, 
                    Location = new Point(UIUtils.S(20), UIUtils.S(60)) 
                };
                _container.Controls.Add(lbl);
            }
            else
            {
                var grouped = instances.GroupBy(i => i.TemplateId);

                foreach (var grp in grouped)
                {
                    var tmpl = templates.FirstOrDefault(t => t.Id == grp.Key);
                    if (tmpl == null) continue;

                    var list = grp.ToList();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var inst = list[i];
                        bool isDefault = (i == 0); 
                        CreatePluginGroup(inst, tmpl, isDefault);
                    }
                }
            }
            
            _container.ResumeLayout();
        }

        private void CreatePluginGroup(PluginInstanceConfig inst, PluginTemplate tmpl, bool isDefault)
        {
            // Title: Name + Version + Author + ID
            string title = $"{tmpl.Meta.Name} v{tmpl.Meta.Version} (ID: {inst.Id}) by: {tmpl.Meta.Author}";
            var group = new LiteSettingsGroup(title);

            // 1. Description & Actions (Header Panel)
            // Use LiteSettingsGroup's built-in header action support
            if (isDefault)
            {
                var linkCopy = new LiteLink(LanguageManager.T("Menu.Copy") == "Menu.Copy" ? "复制副本" : LanguageManager.T("Menu.Copy"), () => CopyInstance(inst));
                group.AddHeaderAction(linkCopy);
            }
            else
            {
                var linkDel = new LiteLink(LanguageManager.T("Menu.Delete") == "Menu.Delete" ? "删除插件" : LanguageManager.T("Menu.Delete"), () => DeleteInstance(inst));
                linkDel.ForeColor = Color.IndianRed;
                // Override hover color for delete
                linkDel.SetColor(Color.IndianRed, Color.Red);
                group.AddHeaderAction(linkDel);
            }

            // Description as a note below header (if needed) or merged?
            // The previous LiteActionHeader displayed description as title.
            // But LiteSettingsGroup already has a title.
            // Let's add the description as a LiteNote if it's different from title, or just ignore if it's redundant.
            // The user's original request was "PluginPage UI refactoring".
            // The previous code put description in LiteActionHeader title.
            // But LiteSettingsGroup title is "Name v1.0 ...".
            // If description is useful, we add it as a Note.
            if (!string.IsNullOrEmpty(tmpl.Meta.Description))
            {
                 var note = new LiteNote(tmpl.Meta.Description);
                 group.AddFullItem(note);
            }

            // 2. Enable Switch (Label = Plugin Name)
            AddBool(group, tmpl.Meta.Name, 
                () => inst.Enabled, 
                v => {
                    if (inst.Enabled != v) {
                        inst.Enabled = v;
                        SaveAndRestart(inst);
                    }
                }
            );

            // 3. Refresh Rate (Replaces ID Input)
            AddNumberInt(group, "刷新频率", "s", 
                () => inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval,
                v => {
                    if (inst.CustomInterval != v) {
                        inst.CustomInterval = v;
                        SaveAndRestart(inst);
                    }
                }
            );

            // Split Inputs
            var globalInputs = tmpl.Inputs.Where(x => x.Scope != "target").ToList();
            var targetInputs = tmpl.Inputs.Where(x => x.Scope == "target").ToList();

            // 4. Global Inputs
            foreach (var input in globalInputs)
            {
                AddString(group, input.Label, 
                    () => inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue,
                    v => {
                        string old = inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue;
                        if (old != v) {
                            inst.InputValues[input.Key] = v;
                            SaveAndRestart(inst);
                        }
                    }, 
                    input.Placeholder
                );
            }

            // 5. Targets Section (Only if plugin has target inputs)
            if (targetInputs.Count > 0)
            {
                 // Auto-Migrate: If Targets is empty but we have values in InputValues for target inputs
                if ((inst.Targets == null || inst.Targets.Count == 0))
                {
                    var legacyTarget = new Dictionary<string, string>();
                    bool hasLegacy = false;
                    foreach (var tInput in targetInputs)
                    {
                        if (inst.InputValues.ContainsKey(tInput.Key))
                        {
                            legacyTarget[tInput.Key] = inst.InputValues[tInput.Key];
                            inst.InputValues.Remove(tInput.Key); // Remove from global
                            hasLegacy = true;
                        }
                    }
                    
                    if (hasLegacy)
                    {
                        inst.Targets = new List<Dictionary<string, string>> { legacyTarget };
                        Settings.Load().Save(); // Save migration
                    }
                }

                // // Header for Targets
                // var targetsHeader = new LiteNote("--- 监控目标列表 ---");
                // targetsHeader.Padding = new Padding(0, 10, 0, 5);
                // group.AddFullItem(targetsHeader);

                if (inst.Targets == null) inst.Targets = new List<Dictionary<string, string>>();
                
                for (int i = 0; i < inst.Targets.Count; i++)
                {
                    int index = i; 
                    var targetVals = inst.Targets[i];
                    
                    // Target Header
                    // Use LiteSettingsItem to display Title + Remove Link
                    
                    // Remove Action
                    var linkRem = new LiteLink("移除", () => {
                        inst.Targets.RemoveAt(index);
                        SaveAndRestart(inst);
                        RebuildUI();
                    });
                    linkRem.SetColor(Color.IndianRed, Color.Red);

                    // [Logic] Prevent removing the last target
                    if (inst.Targets.Count <= 1)
                    {
                        linkRem.Enabled = false;
                    }

                    var headerItem = new LiteSettingsItem($"# 监控目标 {index + 1}", linkRem);
                    // Customize style to look like a sub-header
                    headerItem.Label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                    headerItem.Label.ForeColor = UIColors.Primary;
                    
                    group.AddFullItem(headerItem);

                    foreach (var input in targetInputs)
                    {
                        var val = targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue;
                        
                        if (input.Type == "select" && input.Options != null)
                        {
                            AddComboPair(group, "  " + input.Label, input.Options,
                                () => targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue,
                                v => {
                                    string old = targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue;
                                    if (old != v) {
                                        targetVals[input.Key] = v;
                                        SaveAndRestart(inst);
                                    }
                                }
                            );
                        }
                        else
                        {
                            AddString(group, "  " + input.Label, 
                                () => targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue,
                                v => {
                                    string old = targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue;
                                    if (old != v) {
                                        targetVals[input.Key] = v;
                                        SaveAndRestart(inst);
                                    }
                                }, 
                                input.Placeholder
                            );
                        }
                    }
                }

                // Add Target Button
                var btnAdd = new LiteButton("+ 添加新监控目标", false, true); // Dashed style
                btnAdd.Click += (s, e) => {
                    // Pre-fill with default values
                    var newTarget = new Dictionary<string, string>();
                    if (targetInputs != null)
                    {
                        foreach(var input in targetInputs)
                        {
                            newTarget[input.Key] = input.DefaultValue;
                        }
                    }
                    inst.Targets.Add(newTarget);
                    // [Optimization] Do not trigger Save/Restart immediately to avoid empty requests
                    // SaveAndRestart(inst); 
                    RebuildUI();
                };
                
                group.AddFullItem(btnAdd);
                // [Fix] Manually override margin after AddFullItem resets it to create space
                btnAdd.Margin = UIUtils.S(new Padding(0, 15, 0, 0));
            }

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            // Copied from MainPanelPage.cs
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }

        // =================================================================================
        // Local Helpers (Mimic SettingsPageBase but without persistent _loadActions)
        // =================================================================================

        private void SaveAndRestart(PluginInstanceConfig inst)
        {
            Settings.Load().Save();
            PluginManager.Instance.RestartInstance(inst.Id);
        }

        private void CopyInstance(PluginInstanceConfig source)
        {
            var newInst = new PluginInstanceConfig
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                TemplateId = source.TemplateId,
                Enabled = source.Enabled,
                InputValues = new Dictionary<string, string>(source.InputValues),
                CustomInterval = source.CustomInterval
            };
            
            // Copy Targets
            if (source.Targets != null)
            {
                 foreach(var t in source.Targets)
                 {
                     newInst.Targets.Add(new Dictionary<string, string>(t));
                 }
            }

            Settings.Load().PluginInstances.Add(newInst);
            Settings.Load().Save();
            
            // SyncMonitorItem needs to be called to create the DASH item in Settings.MonitorItems
            PluginManager.Instance.SyncMonitorItem(newInst);
            Settings.Load().Save(); // Ensure the new MonitorItem is saved to disk
            
            // RebuildUI to show the new group
            RebuildUI();
        }

        private void DeleteInstance(PluginInstanceConfig inst)
        {
            if (MessageBox.Show("确定要删除此插件副本吗？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                Settings.Load().PluginInstances.Remove(inst);
                Settings.Load().Save();
                PluginManager.Instance.RemoveInstance(inst.Id);
                RebuildUI();
            }
        }
    }
}

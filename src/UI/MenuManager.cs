using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.Helpers;
using System.Collections.Generic;
using System.Diagnostics;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor
{
    public static class MenuManager
    {
        /// <summary>
        /// 构建 LiteMonitor 主菜单（右键菜单 + 托盘菜单）
        /// </summary>
        public static ContextMenuStrip Build(MainForm form, Settings cfg, UIController? ui, string targetPage = null)
        {
            var menu = new ContextMenuStrip();
            // 标记是否为任务栏模式 (影响监控项的勾选逻辑)
            bool isTaskbarMode = targetPage == "Taskbar";

            // ==================================================================================
            // 1. 基础功能区 (置顶、显示模式、任务栏开关、隐藏主界面/托盘)
            // ==================================================================================

            // === 清理内存 ===
            var cleanMem = new ToolStripMenuItem(LanguageManager.T("Menu.CleanMemory"));
            cleanMem.Image = Properties.Resources.CleanMem;
            cleanMem.Click += (_, __) => form.CleanMemory();
            menu.Items.Add(cleanMem);
            menu.Items.Add(new ToolStripSeparator());

            // === 置顶 ===
            var topMost = new ToolStripMenuItem(LanguageManager.T("Menu.TopMost"))
            {
                Checked = cfg.TopMost,
                CheckOnClick = true
            };
            topMost.CheckedChanged += (_, __) =>
            {
                cfg.TopMost = topMost.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyWindowAttributes(cfg, form);
            };
            // menu.Items.Add(topMost); // Moved to DisplayMode
            // menu.Items.Add(new ToolStripSeparator());

            // === 显示模式 ===
            var modeRoot = new ToolStripMenuItem(LanguageManager.T("Menu.DisplayMode"));

            var vertical = new ToolStripMenuItem(LanguageManager.T("Menu.Vertical"))
            {
                Checked = !cfg.HorizontalMode
            };
            var horizontal = new ToolStripMenuItem(LanguageManager.T("Menu.Horizontal"))
            {
                Checked = cfg.HorizontalMode
            };

            // 辅助点击事件
            void SetMode(bool isHorizontal)
            {
                cfg.HorizontalMode = isHorizontal;
                cfg.Save();
                // ★ 统一调用 (含主题、布局刷新)
                AppActions.ApplyThemeAndLayout(cfg, ui, form);
            }

            vertical.Click += (_, __) => SetMode(false);
            horizontal.Click += (_, __) => SetMode(true);

            modeRoot.DropDownItems.Add(vertical);
            modeRoot.DropDownItems.Add(horizontal);
            modeRoot.DropDownItems.Add(new ToolStripSeparator());

            // === 任务栏显示 ===
            var taskbarMode = new ToolStripMenuItem(LanguageManager.T("Menu.TaskbarShow"))
            {
                Checked = cfg.ShowTaskbar
            };

            taskbarMode.Click += (_, __) =>
            {
                cfg.ShowTaskbar = !cfg.ShowTaskbar;
                // 保存
                cfg.Save(); 
                // ★ 统一调用 (含防呆检查、显隐逻辑、菜单刷新)
                AppActions.ApplyVisibility(cfg, form);
            };

            modeRoot.DropDownItems.Add(taskbarMode);


            // =========================================================
            // ★★★ [修改] 网页显示选项 (改为二级菜单结构) ★★★
            // =========================================================
            var itemWeb = new ToolStripMenuItem(LanguageManager.T("Menu.WebServer")); // 请确保语言包有 "Menu.WebServer"
            
            // 1. 子项：启用/禁用
            var itemWebEnable = new ToolStripMenuItem(LanguageManager.T("Menu.Enable")) // 请确保语言包有 "Menu.WebServerEnabled"
            {
                Checked = cfg.WebServerEnabled,
                CheckOnClick = true
            };

            // 2. 子项：打开网页 (动态获取 IP)
            var itemWebOpen = new ToolStripMenuItem(LanguageManager.T("Menu.OpenWeb")); // 请确保语言包有 "Menu.OpenWeb"
            itemWebOpen.Enabled = cfg.WebServerEnabled; // 只有开启时才可用

            // 事件：切换开关
            itemWebEnable.CheckedChanged += (s, e) => 
            {
                // 1. 更新配置
                cfg.WebServerEnabled = itemWebEnable.Checked;
                cfg.Save(); 

                // 2. ★ 立即应用（调用 AppActions 重启服务）
                AppActions.ApplyWebServer(cfg); 
                
                // 3. 刷新“打开网页”按钮的可用状态
                itemWebOpen.Enabled = cfg.WebServerEnabled;

                // 4. [新增] 开启时弹窗引导
                if (cfg.WebServerEnabled)
                {
                    string msg = LanguageManager.T("Menu.WebServerTip");
                    if (MessageBox.Show(msg, "LiteMonitor", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                    {
                        itemWebOpen.PerformClick();
                    }
                }
            };

            // 事件：打开网页
            itemWebOpen.Click += (s, e) => 
            {
                WebActions.OpenWebMonitor(cfg);
            };

            itemWeb.ToolTipText = LanguageManager.T("Menu.WebServerTip");
            // 将子项加入父菜单
            itemWeb.DropDownItems.Add(itemWebEnable);
            itemWeb.DropDownItems.Add(itemWebOpen);
            // 将父菜单加入“显示模式”组 (或者您可以根据喜好移到 menu.Items.Add(itemWeb) 放到外层)
            modeRoot.DropDownItems.Add(itemWeb);
            
            modeRoot.DropDownItems.Add(new ToolStripSeparator());
            // =========================================================


            // === 自动隐藏 ===
            var autoHide = new ToolStripMenuItem(LanguageManager.T("Menu.AutoHide"))
            {
                Checked = cfg.AutoHide,
                CheckOnClick = true
            };
            autoHide.CheckedChanged += (_, __) =>
            {
                cfg.AutoHide = autoHide.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyWindowAttributes(cfg, form);
            };
            
            // Move TopMost here
            modeRoot.DropDownItems.Add(topMost);
            modeRoot.DropDownItems.Add(autoHide);

            // === 限制窗口拖出屏幕 (纯数据开关) ===
            var clampItem = new ToolStripMenuItem(LanguageManager.T("Menu.ClampToScreen"))
            {
                Checked = cfg.ClampToScreen,
                CheckOnClick = true
            };
            clampItem.CheckedChanged += (_, __) =>
            {
                cfg.ClampToScreen = clampItem.Checked;
                cfg.Save();
            };
            modeRoot.DropDownItems.Add(clampItem);

            // === 鼠标穿透 ===
            var clickThrough = new ToolStripMenuItem(LanguageManager.T("Menu.ClickThrough"))
            {
                Checked = cfg.ClickThrough,
                CheckOnClick = true
            };
            clickThrough.CheckedChanged += (_, __) =>
            {
                cfg.ClickThrough = clickThrough.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyWindowAttributes(cfg, form);
            };
            modeRoot.DropDownItems.Add(clickThrough);

            modeRoot.DropDownItems.Add(new ToolStripSeparator());

            
           

            // === 透明度 ===
            var opacityRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Opacity"));
            double[] presetOps = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            
            // [Optimization] Shared handler to avoid closure per item
            EventHandler onOpacityClick = (s, e) => 
            {
                if (s is ToolStripMenuItem item && item.Tag is double val)
                {
                    cfg.Opacity = val;
                    cfg.Save();
                    AppActions.ApplyWindowAttributes(cfg, form);
                }
            };

            foreach (var val in presetOps)
            {
                var item = new ToolStripMenuItem($"{val * 100:0}%")
                {
                    Checked = Math.Abs(cfg.Opacity - val) < 0.01,
                    Tag = val
                };
                item.Click += onOpacityClick;
                opacityRoot.DropDownItems.Add(item);
            }
            modeRoot.DropDownItems.Add(opacityRoot);

            // === 界面宽度 ===
            var widthRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Width"));
            int[] presetWidths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            int currentW = cfg.PanelWidth;

            // [Optimization] Shared handler
            EventHandler onWidthClick = (s, e) => 
            {
                if (s is ToolStripMenuItem item && item.Tag is int w)
                {
                    cfg.PanelWidth = w;
                    cfg.Save();
                    AppActions.ApplyThemeAndLayout(cfg, ui, form, retainData: true);
                }
            };

            foreach (var w in presetWidths)
            {
                var item = new ToolStripMenuItem($"{w}px")
                {
                    Checked = Math.Abs(currentW - w) < 1,
                    Tag = w
                };
                item.Click += onWidthClick;
                widthRoot.DropDownItems.Add(item);
            }
            modeRoot.DropDownItems.Add(widthRoot);

            // === 界面缩放 ===
            var scaleRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Scale"));
            (double val, string key)[] presetScales =
            {
                (2.00, "200%"), (1.75, "175%"), (1.50, "150%"), (1.25, "125%"),
                (1.00, "100%"), (0.90, "90%"),  (0.85, "85%"),  (0.80, "80%"),
                (0.75, "75%"),  (0.70, "70%"),  (0.60, "60%"),  (0.50, "50%")
            };

            double currentScale = cfg.UIScale;
            
            // [Optimization] Shared handler
            EventHandler onScaleClick = (s, e) => 
            {
                if (s is ToolStripMenuItem item && item.Tag is double scale)
                {
                    cfg.UIScale = scale;
                    cfg.Save();
                    AppActions.ApplyThemeAndLayout(cfg, ui, form, retainData: true);
                }
            };

            foreach (var (scale, label) in presetScales)
            {
                var item = new ToolStripMenuItem(label)
                {
                    Checked = Math.Abs(currentScale - scale) < 0.01,
                    Tag = scale
                };
                item.Click += onScaleClick;
                scaleRoot.DropDownItems.Add(item);
            }

            modeRoot.DropDownItems.Add(scaleRoot);
            modeRoot.DropDownItems.Add(new ToolStripSeparator());


            
             // === 隐藏主窗口 ===
            var hideMainForm = new ToolStripMenuItem(LanguageManager.T("Menu.HideMainForm"))
            {
                Checked = cfg.HideMainForm,
                CheckOnClick = true
            };

            hideMainForm.CheckedChanged += (_, __) =>
            {
                cfg.HideMainForm = hideMainForm.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyVisibility(cfg, form);
            };
            modeRoot.DropDownItems.Add(hideMainForm);


             // === 隐藏托盘图标 ===
            var hideTrayIcon = new ToolStripMenuItem(LanguageManager.T("Menu.HideTrayIcon"))
            {
                Checked = cfg.HideTrayIcon,
                CheckOnClick = true
            };

            hideTrayIcon.CheckedChanged += (_, __) =>
            {
                // 注意：旧的 CheckIfAllowHide 逻辑已整合进 AppActions.ApplyVisibility 的防呆检查中
                // 这里只需修改配置并调用 Action 即可
                
                cfg.HideTrayIcon = hideTrayIcon.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyVisibility(cfg, form);
            }; 
            modeRoot.DropDownItems.Add(hideTrayIcon);
            menu.Items.Add(modeRoot);



           // ==================================================================================
            // 2. 显示监控项 (委托给 MenuMonitorHelper 生成)
            // ==================================================================================
            
            // 调用新 Helper 生成监控项菜单
            var monitorRoot = MenuMonitorHelper.Build(form, cfg, ui, isTaskbarMode);
            menu.Items.Add(monitorRoot);

            // ==================================================================================
            // 3. 主题、工具与更多功能
            // ==================================================================================

            // === 主题 ===
            var themeRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Theme"));
            // 主题编辑器 (独立窗口，保持原样)
            var themeEditor = new ToolStripMenuItem(LanguageManager.T("Menu.ThemeEditor"));
            themeEditor.Image = Properties.Resources.ThemeIcon;
            themeEditor.Click += (_, __) => new ThemeEditor.ThemeEditorForm().Show();
            themeRoot.DropDownItems.Add(themeEditor);
            themeRoot.DropDownItems.Add(new ToolStripSeparator());

            foreach (var name in ThemeManager.GetAvailableThemes())
            {
                var item = new ToolStripMenuItem(name)
                {
                    Checked = name.Equals(cfg.Skin, StringComparison.OrdinalIgnoreCase)
                };

                item.Click += (_, __) =>
                {
                    cfg.Skin = name;
                    cfg.Save();
                    // ★ 统一调用
                    AppActions.ApplyThemeAndLayout(cfg, ui, form);
                };
                themeRoot.DropDownItems.Add(item);
            }
            menu.Items.Add(themeRoot);
            menu.Items.Add(new ToolStripSeparator());


            // --- [系统硬件详情] ---
            var btnHardware = new ToolStripMenuItem(LanguageManager.T("Menu.HardwareInfo")); 
            btnHardware.Image = Properties.Resources.HardwareInfo; // 或者找个图标
            btnHardware.Click += (s, e) => 
            {
                // 这里的模式是：每次点击都 new 一个新的，关闭即销毁。
                // 不占用后台内存。
                var form = new HardwareInfoForm();
                form.Show(); // 非模态显示，允许用户一边看一边操作其他
            };
            menu.Items.Add(btnHardware);
            // --- [新增代码结束] ---

            menu.Items.Add(new ToolStripSeparator());


            // 网络测速 (独立窗口，保持原样)
            var speedWindow = new ToolStripMenuItem(LanguageManager.T("Menu.Speedtest"));
            speedWindow.Image = Properties.Resources.NetworkIcon;
            speedWindow.Click += (_, __) =>
            {
                var f = new SpeedTestForm();
                f.Show();
            };
            menu.Items.Add(speedWindow);


            // 历史流量统计 (独立窗口，保持原样)
            var trafficItem = new ToolStripMenuItem(LanguageManager.T("Menu.Traffic"));
            trafficItem.Image = Properties.Resources.TrafficIcon;
            trafficItem.Click += (_, __) =>
            {
                var formHistory = new TrafficHistoryForm(cfg);
                formHistory.Show();
            };
            menu.Items.Add(trafficItem);
            menu.Items.Add(new ToolStripSeparator());
             // =================================================================
            // [新增] 设置中心入口
            // =================================================================
            var itemSettings = new ToolStripMenuItem(LanguageManager.T("Menu.SettingsPanel")); 
            itemSettings.Image = Properties.Resources.Settings;
            
            // 临时写死中文，等面板做完善了再换成 LanguageManager.T("Menu.Settings")
            
            itemSettings.Font = new Font(itemSettings.Font, FontStyle.Bold); 

            itemSettings.Click += (_, __) =>
            {
                try
                {
                    // 打开设置窗口
                    using (var f = new LiteMonitor.src.UI.SettingsForm(cfg, ui, form))
                    {
                        if (!string.IsNullOrEmpty(targetPage)) f.SwitchPage(targetPage);
                        f.ShowDialog(form);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("设置面板启动失败: " + ex.Message);
                }
            };
            menu.Items.Add(itemSettings);
            
            menu.Items.Add(new ToolStripSeparator());


            // === 语言切换 ===
            var langRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Language"));
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");

            if (Directory.Exists(langDir))
            {
                // [Optimization] Shared handler
                EventHandler onLangClick = (s, e) => 
                {
                    if (s is ToolStripMenuItem item && item.Tag is string code)
                    {
                        cfg.Language = code;
                        cfg.Save();
                        AppActions.ApplyLanguage(cfg, ui, form);
                    }
                };

                foreach (var file in Directory.EnumerateFiles(langDir, "*.json"))
                {
                    string code = Path.GetFileNameWithoutExtension(file);

                    var item = new ToolStripMenuItem(code.ToUpper())
                    {
                        Checked = cfg.Language.Equals(code, StringComparison.OrdinalIgnoreCase),
                        Tag = code
                    };
                    item.Click += onLangClick;

                    langRoot.DropDownItems.Add(item);
                }
            }

            menu.Items.Add(langRoot);
            menu.Items.Add(new ToolStripSeparator());

            // === 开机启动 ===
            var autoStart = new ToolStripMenuItem(LanguageManager.T("Menu.AutoStart"))
            {
                Checked = cfg.AutoStart,
                CheckOnClick = true
            };
            autoStart.CheckedChanged += (_, __) =>
            {
                cfg.AutoStart = autoStart.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyAutoStart(cfg);
            };
            menu.Items.Add(autoStart);

            // === 关于 ===
            var about = new ToolStripMenuItem(LanguageManager.T("Menu.About"));
            about.Click += (_, __) => 
            {
                using (var f = new AboutForm())
                {
                    f.ShowDialog(form);
                }
            };
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            // === 发现新版本 ===
            if (UpdateChecker.IsUpdateFound)
            {
                bool isZh = cfg.Language?.ToLower().Contains("zh") == true;
                string text = isZh ? $"💡发现新版本(v{UpdateChecker.LatestVersionInfo?.latest})" : $"🔄New version(v{UpdateChecker.LatestVersionInfo?.latest})";
                
                var updateItem = new ToolStripMenuItem(text);
                // 鼠标停留提示更新日期与内容摘要 (移除加粗和自定义颜色以解决托盘菜单闪烁问题)
                string? rawLog = UpdateChecker.LatestVersionInfo?.changelog;
                string logSummary = string.IsNullOrEmpty(rawLog) ? "" : rawLog.Replace("\r", "").Replace("\n", " ");
                if (logSummary.Length > 45) logSummary = string.Concat(logSummary.AsSpan(0, 45), "...");
                updateItem.ToolTipText = $"{UpdateChecker.LatestVersionInfo?.releaseDate}: {logSummary}";
                updateItem.ForeColor = Color.RoyalBlue;
                updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
                
                updateItem.Click += async (_, __) => await UpdateChecker.CheckAsync(true);
                menu.Items.Add(updateItem);
                menu.Items.Add(new ToolStripSeparator());
            }

            // === 退出 ===
            var exit = new ToolStripMenuItem(LanguageManager.T("Menu.Exit"));
            exit.Click += (_, __) => form.Close();
            menu.Items.Add(exit);

            return menu;
        }
    }
}

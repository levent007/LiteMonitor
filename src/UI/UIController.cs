using LiteMonitor.src.Core;
using LiteMonitor.src.System;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class UIController : IDisposable
    {
        private readonly Settings _cfg;
        private readonly Form _form;
        private readonly HardwareMonitor _mon;
        private readonly System.Windows.Forms.Timer _timer;

        private UILayout _layout;
        private bool _layoutDirty = true;
        private bool _dragging = false;

        private List<GroupLayoutInfo> _groups = new();
        private List<Column> _hxCols = new();
        private List<Column> _hxColsHorizontal = new();
        private List<Column> _hxColsTaskbar = new();
        private HorizontalLayout? _hxLayout;
        public MainForm MainForm => (MainForm)_form;
       


        // 任务栏模式：公开横版列数据（只读引用）
        public List<Column> GetTaskbarColumns() => _hxColsTaskbar;
        



        public UIController(Settings cfg, Form form)
        {
            _cfg = cfg;
            _form = form;
            _mon = new HardwareMonitor(cfg);
            _mon.OnValuesUpdated += () => _form.Invalidate();

            // 初始化_layout字段，避免null引用警告
            _layout = new UILayout(ThemeManager.Current);

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(80, _cfg.RefreshMs) };
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            ApplyTheme(_cfg.Skin);
        }

        public float GetCurrentDpiScale()
        {
            using (Graphics g = _form.CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        /// <summary>
        /// 真·换主题时调用
        /// </summary>
        public void ApplyTheme(string name)
        {
            // 加载语言与主题
            LanguageManager.Load(_cfg.Language);
            ThemeManager.Load(name);

            // 清理绘制缓存
            UIRenderer.ClearCache();
            var t = ThemeManager.Current;

            // ========== DPI 处理 ==========
            
            float dpiScale = GetCurrentDpiScale();   // 系统DPI
            float userScale = (float)_cfg.UIScale;    // 用户自定义缩放
            float finalScale = dpiScale * userScale;

            // 让 Theme 根据两个缩放因子分别缩放界面和字体
            t.Scale(dpiScale, userScale);
            // 竖屏模式：使用 PanelWidth
            if (!_cfg.HorizontalMode)
            {
                t.Layout.Width = (int)(_cfg.PanelWidth * finalScale);
                _form.Width = t.Layout.Width;
            }

            // 背景色
            _form.BackColor = ThemeManager.ParseColor(t.Color.Background);

            // 重建竖屏布局对象
            _layout = new UILayout(t);

            // ★★ 新增：强制重建横屏布局对象（DPI变化时需要重新计算）
            _hxLayout = null;

            // 重建指标数据
            BuildMetrics();
            _layoutDirty = true;

            // ★★ 新增：初始化横版列数据（任务栏也要用）
            BuildHorizontalColumns();

            // 刷新 Timer 的刷新间隔（关键）
            _timer.Interval = Math.Max(80, _cfg.RefreshMs);

            // 刷新渲染
            _form.Invalidate();
            _form.Update();
        }



        /// <summary>
        /// 轻量级更新（不重新读主题）
        /// </summary>
        public void RebuildLayout()
        {
            BuildMetrics();
            _layoutDirty = true;

            _form.Invalidate();
            _form.Update();
            //BuildHorizontalColumns();// 无论竖屏还是横屏，都构建横版列数据
        }

        /// <summary>
        /// 窗体拖动状态
        /// </summary>
        public void SetDragging(bool dragging) => _dragging = dragging;

        /// <summary>
        /// 主渲染入口
        /// </summary>
        public void Render(Graphics g)
        {
            var t = ThemeManager.Current;
            _layout ??= new UILayout(t);

            // === 横屏模式 ===
            if (_cfg.HorizontalMode)
            {
                // 确保横屏布局已初始化
                _hxLayout ??= new HorizontalLayout(
                    t,
                    _form.Width,
                    LayoutMode.Horizontal   // ★ 新增：横版模式
                );
                
                // 只在布局需要重建时重新计算
                if (_layoutDirty)
                {
                    // layout.Build 计算面板高度 & 面板宽度
                    int h = _hxLayout.Build(_hxColsHorizontal);
            
                    // ★★ 正确设置横屏宽度：Layout 已经算好了 panelWidth
                    _form.Width = _hxLayout.PanelWidth;
                    _form.Height = h;
                    _layoutDirty = false;
                }
            
                // Renderer 使用 panelWidth
                HorizontalRenderer.Render(g, t, _hxColsHorizontal, _hxLayout.PanelWidth);
                return;
            }


            // =====================
            //     竖屏模式
            // =====================
            if (_layoutDirty)
            {
                int h = _layout.Build(_groups);
                _form.Height = h;
                _layoutDirty = false;
            }

            UIRenderer.Render(g, _groups, t);
        }



        private bool _busy = false;

        private async void Tick()
        {
            if (_dragging || _busy) return;
            _busy = true;

            try
            {
                await System.Threading.Tasks.Task.Run(() => _mon.UpdateAll());

                // ① 更新竖屏用的 items
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                    {
                        it.Value = _mon.Get(it.Key);
                        it.TickSmooth(_cfg.AnimationSpeed);
                    }

                // ② ★ 新增：同步更新横版 / 任务栏用的列数据
                void UpdateCol(Column col)
                {
                    if (col.Top != null)
                    {
                        col.Top.Value = _mon.Get(col.Top.Key);
                        col.Top.TickSmooth(_cfg.AnimationSpeed);
                    }
                    if (col.Bottom != null)
                    {
                        col.Bottom.Value = _mon.Get(col.Bottom.Key);
                        col.Bottom.TickSmooth(_cfg.AnimationSpeed);
                    }
                }
                // 主窗口横屏列
                foreach (var col in _hxColsHorizontal)
                {
                    UpdateCol(col);
                }
                // 任务栏列
                foreach (var col in _hxColsTaskbar)
                {
                    UpdateCol(col);
                }
 
                CheckTemperatureAlert();
                _form.Invalidate();   // 主窗体刷新（竖屏 / 横屏）
            }
            finally
            {
                _busy = false;
            }
        }


        /// <summary>
        /// 生成各分组与项目
        /// </summary>
        private void BuildMetrics()
        {
            var t = ThemeManager.Current;
            _groups = new List<GroupLayoutInfo>();

            // === CPU ===
            var cpu = new List<MetricItem>();
            if (_cfg.Enabled.CpuLoad)
                cpu.Add(new MetricItem { Key = "CPU.Load", Label = LanguageManager.T("Items.CPU.Load") });
            if (_cfg.Enabled.CpuTemp)
                cpu.Add(new MetricItem { Key = "CPU.Temp", Label = LanguageManager.T("Items.CPU.Temp") });
            // ★★★ 新增 ★★★
            if (_cfg.Enabled.CpuClock)
                 cpu.Add(new MetricItem { Key = "CPU.Clock", Label = LanguageManager.T("Items.CPU.Clock") });
            if (_cfg.Enabled.CpuPower) 
                cpu.Add(new MetricItem { Key = "CPU.Power", Label = LanguageManager.T("Items.CPU.Power") });
            
            if (cpu.Count > 0) _groups.Add(new GroupLayoutInfo("CPU", cpu));

            // === GPU ===
            var gpu = new List<MetricItem>();
            if (_cfg.Enabled.GpuLoad)
                gpu.Add(new MetricItem { Key = "GPU.Load", Label = LanguageManager.T("Items.GPU.Load") });
            if (_cfg.Enabled.GpuTemp)
                gpu.Add(new MetricItem { Key = "GPU.Temp", Label = LanguageManager.T("Items.GPU.Temp") });
            if (_cfg.Enabled.GpuVram)
                gpu.Add(new MetricItem { Key = "GPU.VRAM", Label = LanguageManager.T("Items.GPU.VRAM") });
            // ★★★ 新增 ★★★
            if (_cfg.Enabled.GpuClock)
                 gpu.Add(new MetricItem { Key = "GPU.Clock", Label = LanguageManager.T("Items.GPU.Clock") });
            if (_cfg.Enabled.GpuPower)
                 gpu.Add(new MetricItem { Key = "GPU.Power", Label = LanguageManager.T("Items.GPU.Power") });
            if (gpu.Count > 0) _groups.Add(new GroupLayoutInfo("GPU", gpu));

            // === MEM ===
            var mem = new List<MetricItem>();
            if (_cfg.Enabled.MemLoad)
                mem.Add(new MetricItem { Key = "MEM.Load", Label = LanguageManager.T("Items.MEM.Load") });
            if (mem.Count > 0) _groups.Add(new GroupLayoutInfo("MEM", mem));

            // === DISK ===
            var disk = new List<MetricItem>();
            if (_cfg.Enabled.DiskRead)
                disk.Add(new MetricItem { Key = "DISK.Read", Label = LanguageManager.T("Items.DISK.Read") });
            if (_cfg.Enabled.DiskWrite)
                disk.Add(new MetricItem { Key = "DISK.Write", Label = LanguageManager.T("Items.DISK.Write") });
            if (disk.Count > 0) _groups.Add(new GroupLayoutInfo("DISK", disk));

            // === NET ===
            var net = new List<MetricItem>();
            if (_cfg.Enabled.NetUp)
                net.Add(new MetricItem { Key = "NET.Up", Label = LanguageManager.T("Items.NET.Up") });
            if (_cfg.Enabled.NetDown)
                net.Add(new MetricItem { Key = "NET.Down", Label = LanguageManager.T("Items.NET.Down") });
            if (net.Count > 0) _groups.Add(new GroupLayoutInfo("NET", net));

            // === DATA (今日流量 - 两列布局) ===
            // 假设 TrafficDay 是控制 Data 组的总开关
            var data = new List<MetricItem>();
            if (_cfg.Enabled.TrafficDay)
            {
                // 注意：UILayout.cs 必须被修改以将 "DATA" 视为双列组
                data.Add(new MetricItem { Key = "DATA.DayUp", Label = LanguageManager.T("Items.DATA.DayUp") });
                data.Add(new MetricItem { Key = "DATA.DayDown", Label = LanguageManager.T("Items.DATA.DayDown") });
            }
            if (data.Count > 0) _groups.Add(new GroupLayoutInfo("DATA", data));
        

            // ★★★ 在方法最后，添加这段初始化代码 ★★★
            // 强制同步当前值，防止动画重置
            foreach (var g in _groups)
            {
                foreach (var it in g.Items)
                {
                    // 1. 获取最新值
                    float? val = _mon.Get(it.Key);
                    it.Value = val;
                    
                    // 2. ★★★ 关键：直接把显示值设为当前值，跳过 0->Target 的动画 ★★★
                    if (val.HasValue) it.DisplayValue = val.Value;
                }
            }
        }

        private void BuildHorizontalColumns()
        {
            // 主窗口横屏列表
            _hxColsHorizontal = BuildColumnsCore();

            // 任务栏列表：必须是独立的一份（不能引用同一对象）
            _hxColsTaskbar = BuildColumnsCore();
        }

        // 修改后的 BuildColumnsCore 方法
        private List<Column> BuildColumnsCore()
        {
            var cols = new List<Column>();

            // =========================================================
            // 第一步：构建“流式布局池” (自动填补空位)
            // 包含：CPU, GPU, MEM
            // 逻辑：将所有开启的此类项目按顺序加入列表，然后两两配对
            // =========================================================
            var flowItems = new List<MetricItem>();

            // 1.1 收集 CPU 项目
            if (_cfg.Enabled.CpuLoad)  flowItems.Add(new MetricItem { Key = "CPU.Load" });
            if (_cfg.Enabled.CpuTemp)  flowItems.Add(new MetricItem { Key = "CPU.Temp" });
            if (_cfg.Enabled.CpuClock) flowItems.Add(new MetricItem { Key = "CPU.Clock" });
            if (_cfg.Enabled.CpuPower) flowItems.Add(new MetricItem { Key = "CPU.Power" });

            // 1.2 收集 GPU 项目
            if (_cfg.Enabled.GpuLoad)  flowItems.Add(new MetricItem { Key = "GPU.Load" });
            if (_cfg.Enabled.GpuTemp)  flowItems.Add(new MetricItem { Key = "GPU.Temp" });
            if (_cfg.Enabled.GpuVram)  flowItems.Add(new MetricItem { Key = "GPU.VRAM" });
            if (_cfg.Enabled.GpuClock) flowItems.Add(new MetricItem { Key = "GPU.Clock" });
            if (_cfg.Enabled.GpuPower) flowItems.Add(new MetricItem { Key = "GPU.Power" });

            // 1.3 收集 MEM 项目
            if (_cfg.Enabled.MemLoad)  flowItems.Add(new MetricItem { Key = "MEM.Load" });

            // 1.4 开始填坑：两两一组生成 Column
            for (int i = 0; i < flowItems.Count; i += 2)
            {
                var col = new Column();
                col.Top = flowItems[i]; // 第一个放上面

                // 如果还有下一个，放下面；否则下面留空 (null)
                if (i + 1 < flowItems.Count)
                {
                    col.Bottom = flowItems[i + 1];
                }

                cols.Add(col);
            }

            // =========================================================
            // 第二步：处理“固定组合” (磁盘, 网络, 流量)
            // 逻辑：这些项目保持独立列，不参与上面的混排，也不让上面的项目插进来
            // =========================================================

            // --- DISK (读/写) ---
            if (_cfg.Enabled.DiskRead || _cfg.Enabled.DiskWrite)
            {
                cols.Add(new Column
                {
                    // 即使只开了一个，另一个位置也留空 (null)，确保这列只属于磁盘
                    Top = _cfg.Enabled.DiskRead ? new MetricItem { Key = "DISK.Read" } : null,
                    Bottom = _cfg.Enabled.DiskWrite ? new MetricItem { Key = "DISK.Write" } : null
                });
            }

            // --- NET (上传/下载) ---
            if (_cfg.Enabled.NetUp || _cfg.Enabled.NetDown)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.NetUp ? new MetricItem { Key = "NET.Up" } : null,
                    Bottom = _cfg.Enabled.NetDown ? new MetricItem { Key = "NET.Down" } : null
                });
            }

            // --- DATA (今日流量) ---
            if (_cfg.Enabled.TrafficDay)
            {
                cols.Add(new Column
                {
                    Top = new MetricItem { Key = "DATA.DayUp" },
                    Bottom = new MetricItem { Key = "DATA.DayDown" }
                });
            }

            // =========================================================
            // 第三步：初始化数值 (防止切换时显示 0 然后跳变)
            // =========================================================
            foreach (var c in cols)
            {
                InitMetricValue(c.Top);
                InitMetricValue(c.Bottom);
            }

            return cols;
        }

        // 辅助方法：初始化单个指标的值
        private void InitMetricValue(MetricItem? item)
        {
            if (item == null) return;
            
            // 从 HardwareMonitor 获取当前值
            float? val = _mon.Get(item.Key);
            item.Value = val;
            
            // 关键：强制 DisplayValue = Value，跳过动画平滑
            // 这样新添加的项目会直接显示数值，而不是从 0 慢慢涨上来
            if (val.HasValue) 
            {
                item.DisplayValue = val.Value;
            }
        }
        
       // ★★★ 新增：检查高温报警 (UI 优化版) ★★★
        private void CheckTemperatureAlert()
        {
            // 1. 基础检查
            if (!_cfg.AlertTempEnabled) return;
            if ((DateTime.Now - _cfg.LastAlertTime).TotalMinutes < 3) return;

            int threshold = _cfg.AlertTempThreshold;
            
            // 2. 使用 List 收集报警信息，方便后续用换行符拼接
            List<string> alertLines = new List<string>();

            // 3. 准备标题和正文
            // 标题：高温报警 (>80°C)
            string alertTitle = LanguageManager.T("Menu.AlertTemp"); 
            
            // --- 检查 CPU ---
            float? cpuTemp = _mon.Get("CPU.Temp");
            if (cpuTemp.HasValue && cpuTemp.Value >= threshold)
            {
                // 简洁格式：CPU: 🔥85°C
                alertLines.Add($"CPU {alertTitle}: 🔥{cpuTemp:F0}°C");
            }

            // --- 检查 GPU ---
            float? gpuTemp = _mon.Get("GPU.Temp");
            if (gpuTemp.HasValue && gpuTemp.Value >= threshold)
            {
                // 简洁格式：GPU: 🔥82°C
                alertLines.Add($"GPU {alertTitle}: 🔥{gpuTemp:F0}°C");
            }

            // --- 触发报警 ---
            if (alertLines.Count > 0)
            {
                
                alertTitle+= $" (>{threshold}°C)";
                // 正文：使用换行符连接多行
                // 效果：
                // CPU: 🔥85°C
                // GPU: 🔥82°C
                string bodyText = string.Join("\n", alertLines);

                // 4. 调用弹窗 (注意参数顺序：Title, Text, Icon)
                // 您之前的写法 ShowNotification(msg, msg...) 把正文当标题用了，会导致重复且难看
                ((MainForm)_form).ShowNotification(alertTitle, bodyText, ToolTipIcon.Warning);
                
                // 更新防抖时间
                _cfg.LastAlertTime = DateTime.Now;
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _mon.Dispose();
        }
    }
}
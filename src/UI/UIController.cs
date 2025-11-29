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

        // 提取公共创建逻辑（完全复用你原来的列构建逻辑）
        private List<Column> BuildColumnsCore()
        {
            var cols = new List<Column>();

            // ==== CPU ====
            if (_cfg.Enabled.CpuLoad || _cfg.Enabled.CpuTemp)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.CpuLoad ? new MetricItem { Key = "CPU.Load" } : null,
                    Bottom = _cfg.Enabled.CpuTemp ? new MetricItem { Key = "CPU.Temp" } : null
                });
            }
            // ★★★ 2. 新增：CPU Clock / Power (新列) ★★★
            if (_cfg.Enabled.CpuClock || _cfg.Enabled.CpuPower)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.CpuClock ? new MetricItem { Key = "CPU.Clock" } : null,
                    Bottom = _cfg.Enabled.CpuPower ? new MetricItem { Key = "CPU.Power" } : null
                });
            }

            // ==== GPU ====
            if (_cfg.Enabled.GpuLoad || _cfg.Enabled.GpuTemp)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.GpuLoad ? new MetricItem { Key = "GPU.Load" } : null,
                    Bottom = _cfg.Enabled.GpuTemp ? new MetricItem { Key = "GPU.Temp" } : null
                });
            }

            // ★★★ 4. 新增：GPU Clock / Power (新列) ★★★
            if (_cfg.Enabled.GpuClock || _cfg.Enabled.GpuPower)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.GpuClock ? new MetricItem { Key = "GPU.Clock" } : null,
                    Bottom = _cfg.Enabled.GpuPower ? new MetricItem { Key = "GPU.Power" } : null
                });
            }


            // ==== VRAM + MEM ====
            if (_cfg.Enabled.MemLoad || _cfg.Enabled.GpuVram)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.GpuVram ? new MetricItem { Key = "GPU.VRAM" } : null,
                    Bottom = _cfg.Enabled.MemLoad ? new MetricItem { Key = "MEM.Load" } : null
                });
            }

            // ==== DISK ====
            if (_cfg.Enabled.DiskRead || _cfg.Enabled.DiskWrite)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.DiskRead ? new MetricItem { Key = "DISK.Read" } : null,
                    Bottom = _cfg.Enabled.DiskWrite ? new MetricItem { Key = "DISK.Write" } : null
                });
            }

            // ==== NET ====
            if (_cfg.Enabled.NetUp || _cfg.Enabled.NetDown)
            {
                cols.Add(new Column
                {
                    Top = _cfg.Enabled.NetUp ? new MetricItem { Key = "NET.Up" } : null,
                    Bottom = _cfg.Enabled.NetDown ? new MetricItem { Key = "NET.Down" } : null
                });
            }

            // ★★★ 修改这里：初始化数值并“瞬移”到位 ★★★
            foreach (var c in cols)
            {
                if (c.Top != null)
                {
                    float? val = _mon.Get(c.Top.Key);
                    c.Top.Value = val;
                    // 关键：强制 DisplayValue = Value
                    if (val.HasValue) c.Top.DisplayValue = val.Value; 
                }
                if (c.Bottom != null)
                {
                    float? val = _mon.Get(c.Bottom.Key);
                    c.Bottom.Value = val;
                    // 关键：强制 DisplayValue = Value
                    if (val.HasValue) c.Bottom.DisplayValue = val.Value;
                }
            }

            return cols;
        }



        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _mon.Dispose();
        }
    }
}
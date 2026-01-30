using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor; // TaskbarForm

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 负责管理任务栏窗口的悬浮提示逻辑
    /// </summary>
    public class TaskbarTooltipHelper : IDisposable
    {
        private readonly Form _targetForm;
        private readonly Settings _cfg;
        private readonly UIController _ui;
        
        // 使用自定义的高性能无闪烁窗体替代 System.Windows.Forms.ToolTip
        private LiteTooltipForm? _tooltipForm;
        private bool _isHovering = false;
        
        // 悬停延迟机制
        private System.Windows.Forms.Timer? _hoverTimer;
        // 穿透模式下的坐标轮询检测器
        private System.Windows.Forms.Timer? _pollingTimer;
        
        private bool _canShow = false;
        private const int HOVER_DELAY_MS = 400; // 400ms 延迟
        private const int POLLING_INTERVAL_MS = 500; // 穿透模式下的检测频率
        private const int TOOLTIP_BASE_WIDTH = 180; // 悬浮窗基准宽度

        public TaskbarTooltipHelper(Form targetForm, Settings cfg, UIController ui)
        {
            _targetForm = targetForm;
            _cfg = cfg;
            _ui = ui;

            Initialize();
        }

        private void Initialize()
        {
            SetupMode();
        }

        public void ReloadMode()
        {
            // 清理旧的事件和计时器
            if (!_targetForm.IsDisposed)
            {
                _targetForm.MouseEnter -= OnMouseEnter;
                _targetForm.MouseLeave -= OnMouseLeave;
                _targetForm.MouseMove -= OnMouseMove;
            }

            _hoverTimer?.Stop();
            _hoverTimer?.Dispose();
            _hoverTimer = null;

            _pollingTimer?.Stop();
            _pollingTimer?.Dispose();
            _pollingTimer = null;

            // 重新初始化
            SetupMode();
        }

        private void SetupMode()
        {
            // 只要开启了悬浮窗功能，就进行初始化 (不再屏蔽穿透模式)
            if (_cfg.TaskbarHoverShowAll)
            {
                if (_tooltipForm == null || _tooltipForm.IsDisposed)
                {
                    _tooltipForm = new LiteTooltipForm();
                }
                
                // 初始化延迟计时器
                _hoverTimer = new System.Windows.Forms.Timer();
                _hoverTimer.Interval = HOVER_DELAY_MS;
                _hoverTimer.Tick += OnHoverTimerTick;

                if (_cfg.TaskbarClickThrough)
                {
                    // 模式 A: 穿透模式 (无法接收鼠标事件，使用坐标轮询)
                    _pollingTimer = new System.Windows.Forms.Timer();
                    _pollingTimer.Interval = POLLING_INTERVAL_MS;
                    _pollingTimer.Tick += OnPollingTimerTick;
                    _pollingTimer.Start();
                }
                else
                {
                    // 模式 B: 普通模式 (使用事件驱动，更高效)
                    _targetForm.MouseEnter += OnMouseEnter;
                    _targetForm.MouseLeave += OnMouseLeave;
                    _targetForm.MouseMove += OnMouseMove;
                }
            }
            else
            {
                // 如果功能关闭，销毁窗体
                _tooltipForm?.Dispose();
                _tooltipForm = null;
            }
        }

        /// <summary>
        /// 轮询检测逻辑 (仅在穿透模式下使用)
        /// </summary>
        private void OnPollingTimerTick(object? sender, EventArgs e)
        {
            if (_targetForm.IsDisposed) return;

            bool containsMouse = _targetForm.Bounds.Contains(Cursor.Position);

            if (containsMouse && !_isHovering)
            {
                // 鼠标刚进入
                OnMouseEnter(sender, e);
            }
            else if (!containsMouse && _isHovering)
            {
                // 鼠标刚离开
                OnMouseLeave(sender, e);
            }
            // 如果一直在内部 (_isHovering && containsMouse)，什么都不用做，等待 _hoverTimer 触发显示
        }

        private void OnMouseEnter(object? sender, EventArgs e)
        {
            _isHovering = true;
            _canShow = false; // 进入时不立即显示
            
            _hoverTimer?.Stop();
            _hoverTimer?.Start(); // 开始计时
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            // 如果 ToolTip 还没显示，且鼠标在移动，则重置计时器 (防抖)
            // 这样只有鼠标停下 (或移动非常慢) HOVER_DELAY_MS 后才会显示
            if (_tooltipForm != null && !_tooltipForm.Visible)
            {
                _hoverTimer?.Stop();
                _hoverTimer?.Start();
            }
        }

        private void OnMouseLeave(object? sender, EventArgs e)
        {
            _isHovering = false;
            _canShow = false;
            _hoverTimer?.Stop();
            _tooltipForm?.Hide();
        }

        private void OnHoverTimerTick(object? sender, EventArgs e)
        {
            _hoverTimer?.Stop();
            if (_isHovering)
            {
                _canShow = true;
                UpdateContent(); // 此时 _canShow 为 true，会触发显示
            }
        }

        /// <summary>
        /// 更新 ToolTip 内容。建议在数据刷新周期（Tick）或鼠标悬浮时调用。
        /// </summary>
        public void UpdateContent()
        {
            // 基础检查
            if (_tooltipForm == null || !_cfg.TaskbarHoverShowAll) return;

            // 如果没有悬浮，直接隐藏（防御性编程，防止 MouseLeave 漏掉）
            if (!_isHovering)
            {
                if (_tooltipForm.Visible) _tooltipForm.Hide();
                return;
            }

            // 检查菜单是否打开 (互斥逻辑：菜单打开时不显示悬浮窗)
            if (_targetForm is TaskbarForm tf && tf.IsMenuOpen)
            {
                if (_tooltipForm.Visible) _tooltipForm.Hide();
                return;
            }

            // 再次确认鼠标是否真的在 Form 内（防止 Alt-Tab 等情况导致的事件丢失）
            if (!_targetForm.Bounds.Contains(Cursor.Position))
            {
                _isHovering = false;
                _tooltipForm?.Hide();
                return;
            }
            
            // 核心逻辑：只有计时器触发后才允许显示
            if (!_canShow && !_tooltipForm.Visible)
            {
                return;
            }
            
            // 优化：只有当需要显示时才获取数据
            var groups = _ui.GetMainGroups();
            if (groups == null) return;
            
            // 传递结构化数据、当前主题、透明度和缩放比例
            var theme = ThemeManager.Current;
            
            // 获取当前缩放比例 (确保不为 0)
            float scale = theme.Layout.LayoutScale;
            if (scale <= 0.1f) scale = 1.0f;

            // 使用基准宽度并应用 DPI 缩放
            int targetWidth = (int)(TOOLTIP_BASE_WIDTH * scale);
            
            // 获取任务栏字体设置 (大字模式/自定义模式)
            bool isBold = _cfg.GetStyle().Bold;

            _tooltipForm.SetData(groups, theme, _cfg.Opacity, targetWidth, scale, isBold);

            // 如果未显示，则显示并定位
            if (!_tooltipForm.Visible)
            {
                _tooltipForm.UpdatePosition(Cursor.Position);
                _tooltipForm.Show(_targetForm); // 指定 Owner 确保层级正确
            }
            // 注意：我们不在每一帧都 UpdatePosition，否则 ToolTip 会跟着鼠标微颤，
            // 除非我们想实现跟随鼠标的效果。这里保持位置固定直到下一次 MouseEnter
            // 或者：如果用户移动了鼠标，我们也不动，直到鼠标移出。
        }

        public void Dispose()
        {
            if (_tooltipForm != null)
            {
                if (!_targetForm.IsDisposed)
                {
                    _targetForm.MouseEnter -= OnMouseEnter;
                    _targetForm.MouseLeave -= OnMouseLeave;
                    _targetForm.MouseMove -= OnMouseMove;
                }
                
                _hoverTimer?.Stop();
                _hoverTimer?.Dispose();
                _hoverTimer = null;

                _pollingTimer?.Stop();
                _pollingTimer?.Dispose();
                _pollingTimer = null;

                _tooltipForm.Dispose();
                _tooltipForm = null;
            }
        }
    }
}

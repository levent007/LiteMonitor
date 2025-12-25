using LiteMonitor.src.Core;

namespace LiteMonitor
{
    /// <summary>
    /// 定义该指标项的渲染风格
    /// </summary>
    public enum MetricRenderStyle
    {
        StandardBar, // 标准：左标签 + 右数值 + 底部进度条 (CPU/MEM/GPU)
        TwoColumn    // 双列：居中标签 + 居中数值 (NET/DISK)
    }

    public class MetricItem
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        
        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;

        // =============================
        // ★★★ 新增：缓存字段 ★★★
        // =============================
        private float _cachedDisplayValue = -99999f; // 上一次格式化时的数值
        private string _cachedNormalText = "";       // 缓存竖屏文本
        private string _cachedHorizontalText = "";   // 缓存横屏/任务栏文本

        /// <summary>
        /// 获取格式化后的文本（带缓存机制）
        /// </summary>
        /// <param name="isHorizontal">是否为横屏/任务栏模式（需要极简格式）</param>
        public string GetFormattedText(bool isHorizontal)
        {
            // 1. 检查数值变化是否足以触发重新格式化
            // 阈值设为 0.05f 配合 FormatValue 的 "0.0" 格式，避免显示内容没变但重绘了字符串
            if (Math.Abs(DisplayValue - _cachedDisplayValue) > 0.05f)
            {
                // 更新缓存基准值
                _cachedDisplayValue = DisplayValue;

                // 2. 重新生成基础字符串 (竖屏用)
                // 这一步避免了每帧调用 float.ToString()
                _cachedNormalText = UIUtils.FormatValue(Key, DisplayValue);

                // 3. 标记横屏缓存失效 (惰性更新，或立即更新)
                // 由于 FormatHorizontalValue 包含正则，开销大，我们只在基础文本变化后才重算
                _cachedHorizontalText = UIUtils.FormatHorizontalValue(_cachedNormalText);
            }

            // 4. 根据模式返回对应的缓存
            return isHorizontal ? _cachedHorizontalText : _cachedNormalText;
        }

        // =============================
        // 布局数据 (由 UILayout 计算填充)
        // =============================
        
        /// <summary>
        /// 渲染风格
        /// </summary>
        public MetricRenderStyle Style { get; set; } = MetricRenderStyle.StandardBar;

        /// <summary>
        /// 整个项目的边界（用于鼠标交互或调试）
        /// </summary>
        public Rectangle Bounds { get; set; } = Rectangle.Empty;

        // --- 内部组件区域 ---
        public Rectangle LabelRect;   // 标签文本区域
        public Rectangle ValueRect;   // 数值文本区域
        public Rectangle BarRect;     // 进度条区域 (仅 StandardBar 有效)
        public Rectangle BackRect;    // 背景区域 (用于圆角矩形等)

        /// <summary>
        /// 平滑更新显示值
        /// </summary>
        public void TickSmooth(double speed)
        {
            if (!Value.HasValue) return;
            float target = Value.Value;
            float diff = Math.Abs(target - DisplayValue);

            if (diff < 0.05f) return;

            if (diff > 15f || speed >= 0.9)
                DisplayValue = target;
            else
                DisplayValue += (float)((target - DisplayValue) * speed);
        }
    }
}
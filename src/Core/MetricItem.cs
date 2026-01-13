using System;
using System.Drawing;
using System.Linq; // 需要 Linq 来查询 Config
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public enum MetricRenderStyle
    {
        StandardBar, 
        TwoColumn,   
        TextOnly     
    }

    public class MetricItem
    {
        private string _key = "";
        public string Key 
        { 
            get => _key;
            set => _key = UIUtils.Intern(value); 
        }

        private string _label = "";
        public string Label 
        {
            get => _label;
            set => _label = UIUtils.Intern(value);
        }
        
        private string _shortLabel = "";
        public string ShortLabel 
        {
            get => _shortLabel;
            set => _shortLabel = UIUtils.Intern(value);
        }
        
        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;
        public string TextValue { get; set; } = null;

        // =============================
        // 缓存字段
        // =============================
        private float _cachedDisplayValue = -99999f; 
        private string _cachedNormalText = "";       // 完整文本 (值+单位)
        private string _cachedHorizontalText = "";   // 完整横屏文本
        
        // ★★★ [新增] 分离缓存 ★★★
        public string CachedValueText { get; private set; } = "";
        public string CachedUnitText { get; private set; } = "";
        public bool HasCustomUnit { get; private set; } = false; // 标记是否使用了自定义单位

        public int CachedColorState { get; private set; } = 0;
        public double CachedPercent { get; private set; } = 0.0;

        public Color GetTextColor(Theme t)
        {
            return UIUtils.GetStateColor(CachedColorState, t, true);
        }

        public string GetFormattedText(bool isHorizontal)
        {
            if (TextValue != null) return TextValue;

            // 只有数值变化时才重新计算字符串
            if (Math.Abs(DisplayValue - _cachedDisplayValue) > 0.05f)
            {
                _cachedDisplayValue = DisplayValue;

                // 1. 获取基础数值和原始单位 (如 "10.5", "MB")
                var (valStr, rawUnit) = UIUtils.FormatValueParts(Key, DisplayValue);
                CachedValueText = valStr;

                // 2. 获取用户配置 (注意：这里需要快速查找，Settings是单例)
                // 考虑到性能，这里不建议每帧 Linq 查找，但由于 _cachedDisplayValue 过滤了大部分调用，
                // 且 Item 数量很少(20个)，所以 FirstOrDefault 开销可忽略。
                var cfg = Settings.Load().MonitorItems.FirstOrDefault(x => x.Key == Key);

                // 3. 确定使用哪个单位配置
                // 如果是横屏/任务栏模式 -> 用 UnitTaskbar，否则用 UnitPanel
                string userFormat = isHorizontal ? cfg?.UnitTaskbar : cfg?.UnitPanel;
                HasCustomUnit = !string.IsNullOrEmpty(userFormat) && userFormat != "Auto";

                // 4. 生成最终单位
                CachedUnitText = UIUtils.GetDisplayUnit(Key, rawUnit, userFormat);

                // 5. 组合缓存 (为了兼容旧渲染器)
                _cachedNormalText = CachedValueText + CachedUnitText;

                // 6. 生成横屏文本
                // 如果用户自定义了单位，我们直接使用组合文本，不进行 "去单位" 处理
                if (HasCustomUnit)
                {
                    _cachedHorizontalText = _cachedNormalText;
                }
                else
                {
                    // 默认逻辑：智能精简
                    // 以前是 FormatHorizontalValue(val+unit)，现在我们手动拼装
                    // 为了保持 FormatHorizontalValue 的移除 /s 逻辑，我们传入默认组合
                    // 但 UIUtils.GetDisplayUnit 已经处理了 Auto 逻辑
                    // 简单起见，这里直接用 FormatHorizontalValue 处理默认组合
                    string defaultFull = CachedValueText + (isHorizontal ? rawUnit : CachedUnitText); 
                    // 这里稍微有点绕，为了保证任务栏 "Auto" 模式下能自动去掉 /s
                    // 我们还是使用旧的 FormatHorizontalValue 逻辑来处理默认情况
                    if (isHorizontal) 
                        _cachedHorizontalText = UIUtils.FormatHorizontalValue(valStr + rawUnit + "/s"); // 模拟带/s让它去切
                    else
                        _cachedHorizontalText = _cachedNormalText; 
                    
                    // 修正：上面的模拟不太稳。
                    // 正确逻辑：如果是默认 Auto，任务栏模式下，我们希望它是 "10.5MB" 而不是 "10.5MB/s"
                    if (string.IsNullOrEmpty(userFormat) || userFormat == "Auto")
                    {
                         // 只有 NET/DISK 默认会带 /s，任务栏需要去掉
                         // UIUtils.FormatHorizontalValue 会去掉 /s
                         string autoUnit = UIUtils.GetDisplayUnit(Key, rawUnit, "Auto"); // 带/s
                         _cachedHorizontalText = UIUtils.FormatHorizontalValue(valStr + autoUnit);
                    }
                    else
                    {
                        _cachedHorizontalText = valStr + CachedUnitText;
                    }
                }

                CachedColorState = UIUtils.GetColorResult(Key, DisplayValue);
                CachedPercent = UIUtils.GetUnifiedPercent(Key, DisplayValue);
            }
            return isHorizontal ? _cachedHorizontalText : _cachedNormalText;
        }

        public MetricRenderStyle Style { get; set; } = MetricRenderStyle.StandardBar;
        public Rectangle Bounds { get; set; } = Rectangle.Empty;

        public Rectangle LabelRect;   
        public Rectangle ValueRect;   
        public Rectangle BarRect;     
        public Rectangle BackRect;    

        public void TickSmooth(double speed)
        {
            if (!Value.HasValue) return;
            float target = Value.Value;
            float diff = Math.Abs(target - DisplayValue);
            if (diff < 0.05f) return;
            if (diff > 15f || speed >= 0.9) DisplayValue = target;
            else DisplayValue += (float)((target - DisplayValue) * speed);
        }
    }
}
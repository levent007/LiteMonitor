using System;
using System.Drawing;
using System.Linq; // 需要 Linq 来查询 Config
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService; // [New] For Plugin Color Override

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
        // [新增] 绑定原始配置对象，实现动态 Label
        public MonitorItemConfig BoundConfig { get; set; }

        private string _key = "";
        public string Key 
        { 
            get => _key;
            set => _key = UIUtils.Intern(value); 
        }

        private string _label = "";
        public string Label 
        {
            get 
            {
                // [核心逻辑] 优先读取用户配置
                if (BoundConfig != null && !string.IsNullOrEmpty(BoundConfig.UserLabel))
                {
                    return BoundConfig.UserLabel;
                }

                // [Refactor] 尝试从 InfoService 读取动态 Label (由 PluginExecutor 注入)
                string dynLabel = InfoService.Instance.GetValue("PROP.Label." + Key);
                if (!string.IsNullOrEmpty(dynLabel)) return dynLabel;

                // 兼容旧逻辑：Config 中的 DynamicLabel
                if (BoundConfig != null && !string.IsNullOrEmpty(BoundConfig.DynamicLabel))
                {
                    return BoundConfig.DynamicLabel;
                }

                return _label;
            }
            set => _label = UIUtils.Intern(value);
        }
        
        private string _shortLabel = "";
        public string ShortLabel 
        {
            get 
            {
                if (BoundConfig != null && !string.IsNullOrEmpty(BoundConfig.TaskbarLabel))
                {
                    return BoundConfig.TaskbarLabel;
                }

                // [Refactor] 尝试从 InfoService 读取动态 ShortLabel
                string dynShort = InfoService.Instance.GetValue("PROP.ShortLabel." + Key);
                if (!string.IsNullOrEmpty(dynShort)) return dynShort;

                if (BoundConfig != null && !string.IsNullOrEmpty(BoundConfig.DynamicTaskbarLabel))
                {
                    return BoundConfig.DynamicTaskbarLabel;
                }

                return _shortLabel;
            }
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
            // [Debug & Fix] 1. Always update color state for Plugin Items FIRST
            if (Key.StartsWith("DASH."))
            {
                string dashKey = Key.Substring(5);
                string colorVal = InfoService.Instance.GetValue(dashKey + ".Color");
                
                if (!string.IsNullOrEmpty(colorVal))
                {
                    if (int.TryParse(colorVal, out int state)) 
                    {
                        CachedColorState = state;
                    }
                }
                else
                {
                    CachedColorState = 0; // Default Safe if no color override
                }
            }

            // 2. Load Config
            var cfg = Settings.Load().MonitorItems.FirstOrDefault(x => x.Key == Key);
            string userFormat = isHorizontal ? cfg?.UnitTaskbar : cfg?.UnitPanel;
            HasCustomUnit = !string.IsNullOrEmpty(userFormat) && userFormat != "Auto";

            // 3. Return TextValue (Plugin/Dashboard items)
            if (TextValue != null) 
            {
                if (HasCustomUnit && !TextValue.EndsWith(userFormat))
                    return TextValue + userFormat;
                return TextValue;
            }

            // 4. Numeric Value Processing (Hardware items)
            if (Math.Abs(DisplayValue - _cachedDisplayValue) > 0.05f)
            {
                _cachedDisplayValue = DisplayValue;

                var (valStr, rawUnit) = MetricUtils.FormatValueParts(Key, DisplayValue);
                CachedValueText = valStr;
                CachedUnitText = MetricUtils.GetDisplayUnit(Key, rawUnit, userFormat);
                _cachedNormalText = CachedValueText + CachedUnitText;

                if (HasCustomUnit)
                {
                    _cachedHorizontalText = _cachedNormalText;
                }
                else
                {
                    if (string.IsNullOrEmpty(userFormat) || userFormat == "Auto")
                    {
                         string autoUnit = MetricUtils.GetDisplayUnit(Key, rawUnit, "Auto"); 
                         _cachedHorizontalText = MetricUtils.FormatHorizontalValue(valStr + autoUnit);
                    }
                    else
                    {
                        _cachedHorizontalText = valStr + CachedUnitText;
                    }
                }

                // Only calculate color if NOT a plugin item (already handled above)
                if (!Key.StartsWith("DASH."))
                {
                    CachedColorState = MetricUtils.GetState(Key, DisplayValue);
                }
                
                CachedPercent = MetricUtils.GetUnifiedPercent(Key, DisplayValue);
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
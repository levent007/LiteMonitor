using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件管理器 (Refactored)
    /// 负责插件的加载、生命周期管理、配置同步以及调度执行
    /// [优化] 修复 Timer 重入问题、增强缓存清理逻辑
    /// </summary>
    public class PluginManager
    {
        private static PluginManager _instance;
        public static PluginManager Instance => _instance ??= new PluginManager();

        private readonly List<PluginTemplate> _templates = new();
        private readonly Dictionary<string, System.Timers.Timer> _timers = new();
        // [New] CancellationTokenSource for each instance to support cancellation
        private readonly Dictionary<string, System.Threading.CancellationTokenSource> _cts = new();
        // [New] Config snapshots for incremental updates
        private readonly Dictionary<string, string> _configSnapshots = new();
        private readonly PluginExecutor _executor;

        public event Action OnPluginSchemaChanged;

        private PluginManager()
        {
            _executor = new PluginExecutor();
            _executor.OnSchemaChanged += () => OnPluginSchemaChanged?.Invoke();
        }

        public void LoadPlugins(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                try { Directory.CreateDirectory(directoryPath); } catch { }
                return;
            }

            // 1. 加载模版
            _templates.Clear();
            var files = Directory.GetFiles(directoryPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var tmpl = JsonSerializer.Deserialize<PluginTemplate>(json);
                    if (tmpl != null && !string.IsNullOrEmpty(tmpl.Id))
                    {
                        _templates.Add(tmpl);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load plugin {file}: {ex.Message}");
                }
            }

            // 2. 自动同步逻辑
            var settings = Settings.Load();
            bool changed = false;
            foreach (var tmpl in _templates)
            {
                if (!settings.PluginInstances.Any(x => x.TemplateId == tmpl.Id))
                {
                    string newId = tmpl.Id;
                    if (settings.PluginInstances.Any(x => x.Id == newId))
                    {
                         newId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    }

                    var newInst = new PluginInstanceConfig
                    {
                        Id = newId,
                        TemplateId = tmpl.Id,
                        Enabled = false
                    };
                    
                    foreach(var input in tmpl.Inputs)
                    {
                        newInst.InputValues[input.Key] = input.DefaultValue;
                    }
                    
                    settings.PluginInstances.Add(newInst);
                    changed = true;
                }
            }
            if (changed) settings.Save();
        }

        public List<PluginTemplate> GetAllTemplates()
        {
            return _templates;
        }

        // [Refactor] Incremental Reload (Reconcile)
        public void Reload(Settings cfg)
        {
            // 1. Identify active instances
            var currentIds = _timers.Keys.ToList();
            var newInstances = cfg.PluginInstances.Where(x => x.Enabled).ToDictionary(x => x.Id);

            // 2. Process Removals
            foreach (var id in currentIds)
            {
                if (!newInstances.ContainsKey(id))
                {
                    StopInstance(id);
                    _configSnapshots.Remove(id);
                }
            }

            // 3. Process Additions & Updates
            foreach (var kv in newInstances)
            {
                var inst = kv.Value;
                string newHash = GetConfigHash(inst);
                bool needsRestart = false;

                if (_timers.ContainsKey(inst.Id))
                {
                    // Exists. Check if changed.
                    if (_configSnapshots.TryGetValue(inst.Id, out var oldHash) && oldHash == newHash)
                    {
                        continue; // Stable, skip
                    }
                    // Changed
                    needsRestart = true;
                    StopInstance(inst.Id);
                }
                else
                {
                    // New
                    needsRestart = true;
                }

                if (needsRestart)
                {
                    var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                    if (tmpl != null)
                    {
                        SyncMonitorItem(inst); 
                        StartInstance(inst, tmpl);
                        _configSnapshots[inst.Id] = newHash;
                    }
                }
            }
        }
        
        private string GetConfigHash(PluginInstanceConfig inst)
        {
            try { return JsonSerializer.Serialize(inst); } catch { return ""; }
        }

        private void StopInstance(string instanceId)
        {
            if (_timers.TryGetValue(instanceId, out var timer))
            {
                timer.Stop();
                timer.Dispose();
                _timers.Remove(instanceId);
            }

            if (_cts.TryGetValue(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _cts.Remove(instanceId);
            }
        }

        public void Start()
        {
            Stop(); 
            _configSnapshots.Clear();

            var settings = Settings.Load();
            foreach (var inst in settings.PluginInstances)
            {
                if (!inst.Enabled) continue;

                var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                if (tmpl == null) continue;

                SyncMonitorItem(inst);
                StartInstance(inst, tmpl);
                _configSnapshots[inst.Id] = GetConfigHash(inst);
            }
        }
        
        public void RestartInstance(string instanceId)
        {
            StopInstance(instanceId);
            
            // [Optimization] Do not clear cache on restart to allow incremental updates
            // Only new/changed targets will generate new cache keys.
            // Old targets will hit the cache in PluginExecutor.
            // _executor.ClearCache(instanceId);

            var settings = Settings.Load();
            var inst = settings.PluginInstances.FirstOrDefault(x => x.Id == instanceId);
            
            // ★★★ [Fix] 如果实例被禁用(Enabled=false)或者已删除，应该清理其关联的 MonitorItems ★★★
            if (inst == null || !inst.Enabled)
            {
                // Clean up items if disabled
                string mainKey = "DASH." + instanceId;
                var itemsToRemove = settings.MonitorItems.Where(x => x.Key == mainKey || x.Key.StartsWith(mainKey + ".")).ToList();
                if (itemsToRemove.Count > 0)
                {
                    foreach (var item in itemsToRemove) settings.MonitorItems.Remove(item);
                    settings.Save();
                }
                return;
            }
            
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            SyncMonitorItem(inst); 
            StartInstance(inst, tmpl);
            _configSnapshots[inst.Id] = GetConfigHash(inst);
        }

        public void RemoveInstance(string instanceId)
        {
            StopInstance(instanceId);
            _configSnapshots.Remove(instanceId);

            var settings = Settings.Load();
            string mainKey = "DASH." + instanceId;
            var itemsToRemove = settings.MonitorItems.Where(x => x.Key == mainKey || x.Key.StartsWith(mainKey + ".")).ToList();
            
            if (itemsToRemove.Count > 0)
            {
                foreach(var item in itemsToRemove)
                {
                    settings.MonitorItems.Remove(item);
                }
                settings.Save();
            }
        }

        public void Stop()
        {
            foreach (var t in _timers.Values)
            {
                t.Stop();
                t.Dispose();
            }
            _timers.Clear();

            foreach (var cts in _cts.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _cts.Clear();
        }

        private void StartInstance(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            var cts = new System.Threading.CancellationTokenSource();
            _cts[inst.Id] = cts;

            // 立即执行一次
            Task.Run(() => _executor.ExecuteInstanceAsync(inst, tmpl, cts.Token));

            // 设定间隔 (单位：秒)
            int interval = inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval;
            if (interval < 1) interval = 1; // Minimum 1s

            // [Refactor] Timer 逻辑重构：Stop-Wait 模式
            // 避免 AutoReset=true 导致的重入问题（即上一次还没跑完，下一次又触发了）
            var newTimer = new System.Timers.Timer(interval * 1000); // Convert Seconds to Milliseconds
            newTimer.AutoReset = false; // 关键：执行完才触发下一次
            
            newTimer.Elapsed += async (s, e) => 
            {
                if (cts.IsCancellationRequested) return;
                try 
                {
                    await _executor.ExecuteInstanceAsync(inst, tmpl, cts.Token);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Timer execution failed: {ex.Message}");
                }
                finally 
                {
                    // 只有当定时器还在列表里（未被停止/移除）时，才启动下一次
                    // 注意：这里需要 lock 吗？一般 _timers 仅在主线程操作，
                    // 但 Elapsed 在线程池。为了安全，我们可以简单判断实例状态。
                    if (_timers.ContainsKey(inst.Id) && inst.Enabled && !cts.IsCancellationRequested)
                    {
                        // 吞掉 ObjectDisposedException 以防万一
                        try { newTimer.Start(); } catch {} 
                    }
                }
            };
            
            newTimer.Start();
            _timers[inst.Id] = newTimer;
        }

        public void SyncMonitorItem(PluginInstanceConfig inst)
        {
            var settings = Settings.Load();
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            bool changed = false;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };
            var validKeys = new HashSet<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                var targetInputs = targets[i];
                var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                foreach (var kv in targetInputs) mergedInputs[kv.Key] = kv.Value;
                
                foreach (var input in tmpl.Inputs)
                    if (!mergedInputs.ContainsKey(input.Key))
                        mergedInputs[input.Key] = input.DefaultValue;

                if (tmpl.Inputs != null)
                {
                    foreach (var input in tmpl.Inputs)
                    {
                        // [Optimization] Ensure default values are applied if input is missing or empty
                         if (input.DefaultValue != null && (!mergedInputs.ContainsKey(input.Key) || string.IsNullOrEmpty(mergedInputs[input.Key])))
                         {
                             mergedInputs[input.Key] = input.DefaultValue;
                         }
                    }
                }

                string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{i}" : "";
                
                if (tmpl.Outputs != null)
                {
                    foreach (var output in tmpl.Outputs)
                    {
                        string itemKey = "DASH." + inst.Id + keySuffix + "." + output.Key;
                        validKeys.Add(itemKey);

                        // 注入 Loading 状态 (仅当当前无值时)
                        if (string.IsNullOrEmpty(InfoService.Instance.GetValue(itemKey)))
                        {
                             InfoService.Instance.InjectValue(itemKey, "...");
                        }

                        var item = settings.MonitorItems.FirstOrDefault(x => x.Key == itemKey);
                        
                        string labelPattern = output.Label;
                        if (string.IsNullOrEmpty(labelPattern)) labelPattern = (tmpl.Meta.Name) + " " + output.Key;
                        
                        // [Refactor] Use generalized ResolveTemplate with Fallback support (e.g. {{city_display ?? city}})
                        string finalName = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);
                        string finalShort = PluginProcessor.ResolveTemplate(output.ShortLabel, mergedInputs);

                        // [Fix] Check if all variables in label pattern are available in inputs (or resolved via fallback)
                        bool canResolveLabel = CanResolveAllVars(labelPattern, mergedInputs);
                        bool canResolveShort = CanResolveAllVars(output.ShortLabel, mergedInputs);
                        
                        // [Fix] Double check: if finalName is just static text (e.g. "天气") but pattern had variables, treat as unresolved
                        if (labelPattern.Contains("{{") && !finalName.Contains("{{") && finalName.Length < 3) 
                        {
                             // Simple heuristic: if result is too short but pattern was complex, likely fallback failed to empty strings
                        }

                        if (item == null)
                        {
                            // [Fix] Even if variables are not resolved, we MUST add the item if it doesn't exist.
                            // Otherwise, the item will never appear in the UI, and the user cannot see it.
                            // We use whatever 'finalName' we have (e.g. fallback value, or partially resolved string).
                            // If finalName is empty, we use Key as fallback.
                            string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (tmpl.Meta.Name + " " + output.Key);
                            string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                            // [Fix] Auto-append to the end of the list
                            int maxSort = settings.MonitorItems.Count > 0 ? settings.MonitorItems.Max(x => x.SortIndex) : 0;
                            int maxTbSort = settings.MonitorItems.Count > 0 ? settings.MonitorItems.Max(x => x.TaskbarSortIndex) : 0;

                            item = new MonitorItemConfig
                            {
                                Key = itemKey,
                                // [Scientific Fix] 初始化时 UserLabel 设为空，表示"自动模式"
                                // 我们把初始值放入 DynamicLabel，以便立即显示，但不持久化
                                UserLabel = "", 
                                DynamicLabel = safeLabel, 
                                TaskbarLabel = "", // 同理，简称也进入自动模式
                                DynamicTaskbarLabel = safeShort,
                                UnitPanel = output.Unit,
                                VisibleInPanel = true,
                                SortIndex = maxSort + 1,
                                TaskbarSortIndex = maxTbSort + 1,
                            };
                            settings.MonitorItems.Add(item);
                            changed = true;
                        }
                        else
                        {
                            // [Scientific Fix] 对于已存在的 Item，我们需要确保 DynamicLabel 有初始值
                            // 否则在插件数据返回前，界面会显示为空或默认 Key，且保存逻辑可能误判
                            string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (tmpl.Meta.Name + " " + output.Key);
                            string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                            if (string.IsNullOrEmpty(item.DynamicLabel)) item.DynamicLabel = safeLabel;
                            if (string.IsNullOrEmpty(item.DynamicTaskbarLabel)) item.DynamicTaskbarLabel = safeShort;


                            if (item.UnitPanel != output.Unit) { item.UnitPanel = output.Unit; changed = true; }
                        }
                    }
                }
            }

            var toRemove = settings.MonitorItems
                .Where(x => x.Key.StartsWith("DASH." + inst.Id + ".") && !validKeys.Contains(x.Key))
                .ToList();

            foreach (var item in toRemove)
            {
                settings.MonitorItems.Remove(item);
                changed = true;
            }

            if (changed) settings.Save();
        }

        private bool CanResolveAllVars(string pattern, Dictionary<string, string> inputs)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (!pattern.Contains("{{")) return true;

            bool success = true;
            // Regex match {{...}}
            var matches = Regex.Matches(pattern, @"\{\{(.+?)\}\}");
            foreach (Match m in matches)
            {
                string content = m.Groups[1].Value.Trim();
                bool resolved = false;

                if (content.Contains("??"))
                {
                    var parts = content.Split(new[] { "??" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string key = part.Trim();
                        // 只要有一个 fallback 变量存在且不为空，就算 resolve 成功
                        if (inputs.TryGetValue(key, out string val) && !string.IsNullOrEmpty(val))
                        {
                            resolved = true;
                            break;
                        }
                    }
                }
                else
                {
                     if (inputs.ContainsKey(content) && !string.IsNullOrEmpty(inputs[content])) resolved = true;
                }
                
                if (!resolved)
                {
                    success = false;
                    break;
                }
            }
            return success;
        }

        // ★★★ [UI Fix] 提供一个公共方法，供 UI 层在 DynamicLabel 缺失时"自救"查询 ★★★
        public string TryGetSmartLabel(string itemKey, string targetField = "label")
        {
            try
            {
                if (string.IsNullOrEmpty(itemKey) || !itemKey.StartsWith("DASH.")) return "";

                // 1. 尝试从已加载的插件实例中反向查找
                // Key Format: DASH.{InstanceId}.{Suffix}
                var settings = Settings.Load();
                
                foreach (var inst in settings.PluginInstances)
                {
                    string prefix = "DASH." + inst.Id + ".";
                    if (itemKey.StartsWith(prefix) || itemKey == "DASH." + inst.Id) // 兼容某些怪异 Key
                    {
                        // 2. 找到 Template
                        var tmpl = _templates.FirstOrDefault(t => t.Id == inst.TemplateId);
                        if (tmpl == null) continue;

                        // 3. 提取 Output Key
                        string suffix = itemKey.Substring(prefix.Length);
                        // 如果有 Target Index (例如 DASH.id.0.key)，需要进一步拆解
                        // 简单处理：遍历所有 Outputs，看 Key 是否匹配
                        if (tmpl.Outputs != null)
                        {
                            foreach (var output in tmpl.Outputs)
                            {
                                if (suffix == output.Key || suffix.EndsWith("." + output.Key))
                                {
                                    // 4. 构造 Inputs 进行模拟解析
                                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                                    if (tmpl.Inputs != null)
                                    {
                                        foreach (var input in tmpl.Inputs)
                                            if (!mergedInputs.ContainsKey(input.Key))
                                                mergedInputs[input.Key] = input.DefaultValue;
                                    }

                                    // 5. 尝试解析 Label 或 ShortLabel
                                    string labelPattern;
                                    if (targetField == "short_label")
                                        labelPattern = output.ShortLabel;
                                    else
                                        labelPattern = output.Label;
                                    
                                    // 如果没定义，回退到 Plugin Name
                                    if (string.IsNullOrEmpty(labelPattern)) labelPattern = tmpl.Meta.Name;

                                    string resolved = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);
                                    
                                    // 如果解析结果为空，或者还是包含 {{ (解析失败)，则回退到插件名
                                    if (string.IsNullOrEmpty(resolved) || resolved.Contains("{{"))
                                        return tmpl.Meta.Name + " " + output.Key;
                                    
                                    return resolved;
                                }
                            }
                        }
                        // 没找到 Output，至少返回插件名
                        return tmpl.Meta.Name;
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
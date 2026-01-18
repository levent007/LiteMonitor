using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件执行引擎 (Refactored)
    /// 负责执行 API 请求、链式步骤、数据处理和结果注入
    /// [优化] 线程安全缓存、移除 IO 阻塞、修复参数缓存冲突
    /// </summary>
    public class PluginExecutor
    {
        private readonly HttpClient _http;
        
        // [Refactor] In-flight requests for Request Coalescing
        private readonly ConcurrentDictionary<string, Task<string>> _inflightRequests = new();
        
        // [Refactor] 使用线程安全的字典，防止多线程并发读写导致 Crash
        // Key = InstanceID_StepID_ParamsHash
        private class CacheItem
        {
            public string RawResponse { get; set; } // [Refactor] Store Raw Response
            public DateTime Timestamp { get; set; }
        }
        private readonly ConcurrentDictionary<string, CacheItem> _stepCache = new();

        // 当插件动态修改了 UI 配置（如 Label）时触发
        public event Action OnSchemaChanged;

        public PluginExecutor()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(10); // 默认超时
            _http.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
            
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        public void ClearCache(string instanceId = null)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                _stepCache.Clear();
            }
            else
            {
                // 清除特定实例的所有缓存
                var keysToRemove = _stepCache.Keys.Where(k => k.StartsWith(instanceId)).ToList();
                foreach (var k in keysToRemove) _stepCache.TryRemove(k, out _);
            }
        }

        public async Task ExecuteInstanceAsync(PluginInstanceConfig inst, PluginTemplate tmpl, System.Threading.CancellationToken token = default)
        {
            // 防御性编程：检查配置有效性
            if (inst == null || tmpl == null) return;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };

            var tasks = new List<Task>();
            for (int i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var idx = i; // Capture loop variable
                tasks.Add(Task.Run(async () => 
                {
                    if (token.IsCancellationRequested) return;

                    // [Optimization] Parallel execution with slight staggered start for rate limiting
                    if (idx > 0) 
                    {
                        try { await Task.Delay(idx * 50, token); } catch (OperationCanceledException) { return; }
                    }

                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                    foreach (var kv in targets[idx])
                    {
                        mergedInputs[kv.Key] = kv.Value;
                    }
                    
                    if (tmpl.Inputs != null)
                    {
                        foreach (var input in tmpl.Inputs)
                        {
                            if (!mergedInputs.ContainsKey(input.Key))
                            {
                                mergedInputs[input.Key] = input.DefaultValue;
                            }
                        }
                    }

                    string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{idx}" : "";
                    
                    await ExecuteSingleTargetAsync(inst, tmpl, mergedInputs, keySuffix, token);
                }, token));
            }
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
        }

        private async Task ExecuteSingleTargetAsync(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix, System.Threading.CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return;

                string url = PluginProcessor.ResolveTemplate(tmpl.Execution.Url, inputs);
                string body = tmpl.Execution.Body ?? "";
                body = PluginProcessor.ResolveTemplate(body, inputs);

                string resultRaw = "";
                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "api_text")
                {
                    var method = (tmpl.Execution.Method?.ToUpper() == "POST") ? HttpMethod.Post : HttpMethod.Get;
                    var request = new HttpRequestMessage(method, url);
                    
                    if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    }

                    if (tmpl.Execution.Headers != null)
                    {
                        foreach (var h in tmpl.Execution.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    
                    var response = await _http.SendAsync(request, token);
                    resultRaw = await response.Content.ReadAsStringAsync(token);
                }

                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "chain")
                {
                    if (tmpl.Execution.Type == "chain")
                    {
                        if (tmpl.Execution.Steps != null)
                        {
                            foreach (var step in tmpl.Execution.Steps)
                            {
                                if (token.IsCancellationRequested) return;
                                await ExecuteStepAsync(inst, step, inputs, keySuffix, token);
                            }
                        }
                    }
                    else
                    {
                        // 尝试解析 JSON，如果失败则捕获异常
                        using var doc = JsonDocument.Parse(resultRaw);
                        var root = doc.RootElement;
                        
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errProp) && errProp.GetBoolean())
                        {
                             // 可选：处理 API 返回的逻辑错误
                        }

                        if (tmpl.Execution.Extract != null)
                        {
                            foreach (var v in tmpl.Execution.Extract)
                            {
                                inputs[v.Key] = PluginProcessor.ExtractJsonValue(root, v.Value);
                            }
                        }
                    }

                    PluginProcessor.ApplyTransforms(tmpl.Execution.Process, inputs);
                    
                    if (tmpl.Outputs != null)
                    {
                        ProcessOutputs(inst, tmpl, inputs, keySuffix);
                    }
                }
                else
                {
                     // api_text
                     string injectKey = inst.Id + keySuffix;
                     InfoService.Instance.InjectValue(injectKey, resultRaw);
                }
            }
            catch (OperationCanceledException) 
            {
                // Ignored
            }
            catch (Exception ex)
            {
                 HandleExecutionError(inst, tmpl, keySuffix, ex);
            }
        }

        private async Task ExecuteStepAsync(PluginInstanceConfig inst, PluginExecutionStep step, Dictionary<string, string> context, string keySuffix, System.Threading.CancellationToken token)
        {
            try {
                // 0. Check Skip Condition
                if (!string.IsNullOrEmpty(step.SkipIfSet))
                {
                    if (context.TryGetValue(step.SkipIfSet, out var val) && !string.IsNullOrEmpty(val))
                    {
                        return; // Skip this step
                    }
                }

                // 1. 预解析 URL 和 Body，用于生成精确的 CacheKey
                string url = PluginProcessor.ResolveTemplate(step.Url, context);
                string body = PluginProcessor.ResolveTemplate(step.Body ?? "", context);

                // [Fix] 生成包含参数哈希的 CacheKey，彻底解决参数变更后缓存不更新的问题
                string contentHash = (url + "|" + body).GetHashCode().ToString("X"); 
                string cacheKey = $"{inst.Id}{keySuffix}_{step.Id}_{contentHash}";

                // 2. 检查缓存
                bool hit = false;
                string resultRaw = "";

                if (step.CacheMinutes > 0)
                {
                    if (_stepCache.TryGetValue(cacheKey, out var cached))
                    {
                        if (DateTime.Now - cached.Timestamp < TimeSpan.FromMinutes(step.CacheMinutes))
                        {
                            resultRaw = cached.RawResponse;
                            hit = true;
                        }
                        else
                        {
                            _stepCache.TryRemove(cacheKey, out _); // Expired
                        }
                    }
                }

                if (!hit)
                {
                    // 3. 发起请求 (Only if NOT hit)
                // [Optimization] Request Coalescing (防止并发请求穿透缓存)
                // [Fix] Detach the request from the caller's cancellation token to ensure it completes and populates cache
                // This prevents "Zombie" requests from being cancelled mid-flight during a Reload, which would cause cache misses for the next immediate request.
                var task = _inflightRequests.GetOrAdd(cacheKey, _ => FetchStepRawAsync(step, url, body, cacheKey, CancellationToken.None));
                
                try 
                {
                    // We still await it, but we respect the caller's token for waiting
                    // If caller cancels, we stop waiting, but the background task continues
                    var tcs = new TaskCompletionSource<string>();
                    using (token.Register(() => tcs.TrySetCanceled()))
                    {
                        var finishedTask = await Task.WhenAny(task, tcs.Task);
                        if (finishedTask == tcs.Task) throw new OperationCanceledException(token);
                        resultRaw = await task;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Caller gave up, but FetchStepRawAsync continues in background
                    throw;
                }
                catch (Exception)
                {
                    // 如果请求失败，确保从 inflight 中移除（虽然 FetchStepRawAsync 内部也移除了）
                    _inflightRequests.TryRemove(cacheKey, out _);
                    throw;
                }
                }

                // 4. 解析结果 (Always Run, even if cached)
                // [Optimization] This ensures that 'Process' logic (which may depend on dynamic settings like 'style')
                // is re-executed every time, even if the API response is cached.
                if (step.Extract != null && step.Extract.Count > 0)
                {
                    string json = resultRaw.Trim();
                    string format = step.ResponseFormat?.ToLower() ?? "json";
                    
                    if (format == "jsonp")
                    {
                        if (json.StartsWith("(") && json.EndsWith(")"))
                        {
                            json = json.Substring(1, json.Length - 2);
                        }
                    }
                    
                    if (format == "json" || format == "jsonp")
                    {
                        // 简单判断是否是合法 JSON 对象或数组
                        if (json.StartsWith("{") || json.StartsWith("["))
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            foreach (var kv in step.Extract)
                            {
                                context[kv.Key] = PluginProcessor.ExtractJsonValue(root, kv.Value);
                            }
                        }
                    }
                    else if (format == "text")
                    {
                        // [New] Support text extraction
                        foreach (var kv in step.Extract)
                        {
                            // If value is "$", extract entire raw text
                            if (kv.Value == "$")
                            {
                                context[kv.Key] = resultRaw; // Don't trim if user wants raw, but resultRaw is already string
                            }
                        }
                    }
                }

                // [Fix] Even if Extract is null, we should allow Process to run (e.g. if Process uses previously extracted vars)
                // BUT current logic applies transforms AFTER extract.
                // If Extract is null, context is not updated with new data from resultRaw unless we explicitly extract it.
                // For text format, we usually extract "$" to a var, then process it.
                // So the above block handles extraction.
                
                PluginProcessor.ApplyTransforms(step.Process, context);

                // 5. 写入缓存 (Deprecated: We now cache RawResponse above)
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step {step.Id} Error: {ex.Message}");
                // 抛出异常以中断后续步骤（根据业务逻辑，链式步骤失败通常意味着后续无法进行）
                throw; 
            }
        }

        private async Task<string> FetchStepRawAsync(PluginExecutionStep step, string url, string body, string cacheKey, System.Threading.CancellationToken token)
        {
            try
            {
                var method = (step.Method?.ToUpper() == "POST") ? HttpMethod.Post : HttpMethod.Get;
                var request = new HttpRequestMessage(method, url);
                if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
                if (step.Headers != null)
                {
                    foreach (var h in step.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                var response = await _http.SendAsync(request, token);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
                
                string resultRaw;
                if (step.ResponseEncoding?.ToLower() == "gbk")
                {
                    resultRaw = Encoding.GetEncoding("GBK").GetString(bytes);
                }
                else
                {
                    resultRaw = Encoding.UTF8.GetString(bytes);
                }

                // 写入缓存
                if (step.CacheMinutes != 0)
                {
                    _stepCache[cacheKey] = new CacheItem
                    {
                        RawResponse = resultRaw,
                        Timestamp = DateTime.Now
                    };
                }
                
                return resultRaw;
            }
            finally
            {
                _inflightRequests.TryRemove(cacheKey, out _);
            }
        }

        private void ProcessOutputs(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix)
        {
            bool schemaChanged = false;
            // 获取单例，无需重新 Load
            var settings = Settings.Load(); 

            // [Optimization] 使用 lock 保护 Settings 集合的修改，防止 UI 线程遍历时 crash
            lock (settings) 
            {
                foreach (var output in tmpl.Outputs)
                {
                    // 1. 注入数值 (Update InfoService)
                    string val = PluginProcessor.ResolveTemplate(output.FormatVal, inputs);
                    string injectKey = inst.Id + keySuffix + "." + output.Key;
                    
                    if (string.IsNullOrEmpty(val)) val = "[Empty]";
                    InfoService.Instance.InjectValue(injectKey, val);

                    // 2. 动态更新 Label (Conditional Update)
                    // [Strategy] "Write-Once" with Auto-Recovery
                    // - If label is a System Placeholder (e.g. "Weather main"), it means init failed -> Allow Update.
                    // - If label is User Custom (or already valid) -> Do NOT Update (Preserve user choice).
                    string itemKey = "DASH." + injectKey;
                    var item = settings.MonitorItems.FirstOrDefault(x => x.Key == itemKey);
                    if (item != null)
                    {
                        string labelPattern = output.Label;
                        if (string.IsNullOrEmpty(labelPattern)) labelPattern = (tmpl.Meta.Name) + " " + output.Key;
                        
                        string newName = PluginProcessor.ResolveTemplate(labelPattern, inputs);
                        string newShort = PluginProcessor.ResolveTemplate(output.ShortLabel ?? "", inputs);
                        
                        if (tmpl.Inputs != null)
                        {
                            foreach (var input in tmpl.Inputs)
                            {
                                if (!inputs.ContainsKey(input.Key))
                                {
                                    newName = newName.Replace("{{" + input.Key + "}}", input.DefaultValue);
                                    newShort = newShort.Replace("{{" + input.Key + "}}", input.DefaultValue);
                                }
                            }
                        }

                        // [Scientific Fix] 分离状态与配置
                        // 1. 始终更新 DynamicLabel (内存状态)
                        // 2. 绝不触碰 UserLabel (用户配置)
                        
                        if (item.DynamicLabel != newName)
                        {
                            item.DynamicLabel = newName;
                            // 只有当 UserLabel 为空(自动模式)时，DynamicLabel 的变化才需要通知 UI 重绘
                            if (string.IsNullOrEmpty(item.UserLabel)) schemaChanged = true;
                        }
                        
                        // [Scientific Fix] ShortLabel 也采用分离逻辑
                        if (item.DynamicTaskbarLabel != newShort)
                        {
                            item.DynamicTaskbarLabel = newShort;
                            if (string.IsNullOrEmpty(item.TaskbarLabel)) schemaChanged = true;
                        }
                    }
                }
            }
            
            if (schemaChanged)
            {
                OnSchemaChanged?.Invoke();
            }

        }

        private void HandleExecutionError(PluginInstanceConfig inst, PluginTemplate tmpl, string keySuffix, Exception ex)
        {
            if (tmpl.Outputs != null)
            {
                foreach(var o in tmpl.Outputs) 
                {
                    string injectKey = inst.Id + keySuffix + "." + o.Key;
                    InfoService.Instance.InjectValue(injectKey, "Err");
                }
            }
            System.Diagnostics.Debug.WriteLine($"Plugin exec error ({inst.Id}): {ex.Message}");
        }
    }
}
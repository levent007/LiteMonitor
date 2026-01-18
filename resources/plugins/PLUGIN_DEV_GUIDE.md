# LiteMonitor 插件开发指南 (v2.0)

本指南详细介绍了如何为 LiteMonitor 开发插件。系统采用基于 JSON 的配置模型，支持 API 调用、链式执行、数据解析以及动态 UI 更新。

## 1. 文件位置
请将插件文件放置在 `resources/plugins/` 目录下，并使用 `.json` 扩展名（例如 `resources/plugins/my_plugin.json`）。

## 2. 基本结构 (JSON)
```json
{
  "id": "unique_plugin_id",
  "meta": {
    "name": "插件名称",
    "version": "1.0.0",
    "author": "作者",
    "description": "插件功能描述"
  },
  "inputs": [],
  "execution": {},
  "parsing": {}
}
```

## 3. 配置详解

### 3.1 Inputs (用户输入)
定义用户需要配置的字段（例如：城市名、API Key）。

```json
"inputs": [
  {
    "key": "city",
    "label": "城市名称",
    "type": "text", 
    "default": "上海",
    "placeholder": "请输入城市名称",
    "scope": "target" 
  },
  {
    "key": "display_mode",
    "label": "显示模式",
    "type": "select",
    "options": [
      {"label": "简约模式", "value": "simple"},
      {"label": "详细模式", "value": "detail"}
    ],
    "default": "simple"
  }
]
```
*   **scope**: 
    *   `"global"`: 所有目标共享此配置（例如 API Key）。
    *   `"target"`: 每个监控目标单独配置（例如 股票代码、城市名）。
*   **default**: 默认值。当用户未输入时，系统将使用此值。
*   **options**: (仅 `select` 类型) 下拉选项列表，包含 `label` 和 `value`。

### 3.2 Execution (执行逻辑)
当前仅推荐使用 `chain` (链式工作流) 模式。

#### Chain 模式
顺序执行多个步骤。上下文变量（Inputs + 步骤输出）在所有步骤间共享。

```json
"execution": {
  "type": "chain",
  "interval": 300, // 刷新间隔 (秒)
  "steps": [
    {
      "id": "step_locate",
      "skip_if_set": "city", // 关键特性：如果用户已配置 'city' 变量，则跳过此步骤
      "url": "http://whois.pconline.com.cn/ipJson.jsp?json=true",
      "extract": { "auto_city": "city" }, // 将接口返回的 city 存为 auto_city
      "cache_minutes": 43200 // 缓存 30 天
    },
    {
      "id": "step_weather",
      "url": "http://api.weather.com/query?city={{city ?? auto_city}}", // 优先使用用户输入的 city，否则用自动定位的
      "extract": { "temp": "data.current_temp" }
    }
  ]
}
```

#### 步骤配置 (Step Configuration)
| 字段 | 描述 |
| :--- | :--- |
| `id` | 步骤唯一标识。 |
| `skip_if_set` | **(新)** 如果上下文中已存在指定的变量且不为空，则跳过此步骤。常用于“手动配置优先，自动获取兜底”的场景。 |
| `url` | 目标 URL。支持 `{{var}}` 和 `{{var ?? fallback}}` 变量替换。 |
| `method` | `GET` (默认) 或 `POST`。 |
| `headers` | 自定义请求头。支持模板变量。 |
| `body` | POST 请求体。支持模板变量。 |
| `response_format` | `json` (默认), `jsonp` (自动去除回调包裹), `text`。 |
| `response_encoding` | `utf-8` (默认) 或 `gbk` (适配旧中文接口)。 |
| `cache_minutes` | 缓存时间（分钟）。`0`=不缓存。**系统会缓存 Raw Response**。即使缓存命中，后续的 `extract` 和 `process` 逻辑仍会执行，确保数据解析规则变更后能立即生效。 |
| `extract` | 变量映射：`变量名` -> `JSON路径`。 |
| `process` | 数据清洗规则 (见下文)。 |

### 3.3 Extract (数据提取)
使用点号分隔符提取 JSON 数据：
*   `data.value`: 提取对象属性。
*   `list[0].name`: 提取数组元素中的属性。
*   `[0].ref`: 提取根数组的第一个元素。

### 3.4 Process (数据处理)
在提取数据后，对变量进行二次处理。

```json
"process": [
  {
    "var": "city_display",
    "source": "city",
    "function": "regex_replace",
    "pattern": "(市|区|县)$", // 正则表达式
    "to": "" // 替换为... (支持模板变量，如 "{{province}}")
  },
  {
    "var": "status_icon",
    "source": "status_code",
    "function": "map",
    "map": { "200": "✅", "404": "❌" }
  },
  {
      "var": "final_display",
      "source": "template",// 模板字符串变量
      "function": "resolve_template",
  }
]
```

### 3.5 Outputs (UI 显示)
定义数据如何在监控面板中显示。

```json
"outputs": [
  {
    "key": "temp",
    "label": "{{city_display ?? city ?? auto_city}}天气", // 强烈推荐使用 Fallback 语法
    "short_label": "{{city_display ?? city ?? auto_city}}",
    "format_val": "{{temp}}°C",
    "unit": ""
  }
]
```

#### 最佳实践：Label 的 Fallback 机制
在异步加载场景中，某些变量（如 `city_display`）可能需要等待 API 请求完成后才有值。
为了避免 UI 在加载过程中显示为空白或损坏的标签（如 `{{city_display}}天气`），**强烈建议**使用 Fallback 语法：
`{{优先变量 ?? 次选变量 ?? 兜底变量}}`

例如：`{{city_display ?? city ?? auto_city}}`
1. 优先显示处理过的短名 `city_display`。
2. 如果未就绪，显示用户输入的 `city`。
3. 如果用户未输入，显示自动定位的 `auto_city`。

## 4. 动态 UI 与性能最佳实践

### 动态标签 (Dynamic Labels)
你可以在 `label` 和 `short_label` 中使用变量。当这些值发生变化时，系统会自动更新 UI。

**⚠️ 性能警告：**
*   **推荐做法：** 仅使用**静态元数据**（如 `{{city}}`, `{{ip}}`）。这些数据很少变化。
*   **禁止做法：** 在 **Label** 中使用**高频变化的数据**（如 `{{cpu_usage}}` 或 `{{temperature}}`）。
    *   **原因：** Label 的变化会触发 `Settings.Save()` (磁盘写入) 和 `UI.Rebuild()` (界面重绘)。
    *   **正确做法：** 将动态数值放在 `format` 字段中。`format` 的更新是纯内存操作，极其高效。

## 5. 高级特性 (系统内核)
*   **Request Coalescing (请求合并)**: 系统会自动合并并发的相同请求（URL + 参数相同）。例如，同时添加 3 个自动定位的目标，只会发起 1 次网络请求。
*   **Detached Requests (防中断)**: 即使插件配置重载导致当前任务被取消，底层的网络请求也会在后台坚持完成并写入缓存，确保下一次任务能直接命中缓存。
*   **Raw Cache**: 系统缓存的是原始 HTTP 响应。这意味着你可以随意修改 `extract` 或 `process` 规则，只需重载配置即可立即看到新结果，无需重新等待网络请求。

## 6. 故障排查
*   **"Err"**: 发生异常 (请查看 Debug 输出)。
*   **"?"**: JSON 解析失败 (检查 JSON Path 是否正确)。
*   **"[Empty]"**: 变量提取成功但内容为空。
*   **API Err**: API 返回了包含错误的响应对象。

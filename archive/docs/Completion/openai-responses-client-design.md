# OpenAI Responses Client 设计草案

> **用途**：为 `prototypes/Completion` 设计一个符合 `OpenAI Responses API` 语义、但仍实现现有 `prototypes/Completion.Abstractions/ICompletionClient.cs` 的 provider。
> **适用范围**：`prototypes/Completion/OpenAI/*`
> **最后更新**：2026-05-27

---

## 一句话结论

建议在不改 `ICompletionClient` 的前提下，新增一套与现有 `OpenAIChatClient` 并列的 `OpenAIResponsesClient` 实现：

- 请求侧改走 `POST /v1/responses`
- 历史侧从 `messages` 改投影为 `input items`
- 流式侧从 `choices[].delta` 改为 `response.*` 事件状态机
- 默认采用**无状态 full-input replay**，不依赖 `previous_response_id`
- 用 provider-specific `ReasoningBlock` 保存 OpenAI reasoning item，满足后续 tool continuation replay

这样可以复用现有 `CompletionAggregator`、`JsonToolSchemaBuilder`、HTTP transport pipeline 和 `ICompletionClient` 契约，同时把 OpenAI 新主线接口纳入同一抽象层。

---

## 设计目标

1. **不改上层抽象**
   - `Agent.Core` 及其他调用方继续只认 `ICompletionClient` / `CompletionRequest` / `CompletionResult`。
2. **遵循官方主语义**
   - 新实现以 OpenAI 官方 `Responses API` 为标准，而不是在 `chat/completions` 之上打补丁。
3. **保留后续多步实施空间**
   - 先把 text + custom function calling + reasoning replay 主链设计清楚，再逐步补 built-in tools、structured outputs 等能力。
4. **尽量复用既有骨架**
   - 继续使用 `Client + MessageConverter + StreamParser + ApiModels` 四层结构，降低心智切换成本。

---

## 官方依据

以下判断都以 OpenAI 官方文档为准：

- OpenAI 明确说明：`Responses API` 是新的主推荐接口，`Chat Completions` 仍支持，但**新项目推荐使用 Responses**。
  - 来源：<https://developers.openai.com/api/docs/guides/migrate-to-responses>
- `Responses` 的核心模型不是 `messages`，而是 typed `items`；输出也不再是 `choices[].message`，而是 `response.output[]`。
  - 来源：同上
- `Responses` 的 function calling 里，`function_call` 和 `function_call_output` 是两个独立 item，通过 `call_id` 关联。
  - 来源：<https://developers.openai.com/api/docs/guides/function-calling>
- 流式事件不再是 chat delta，而是诸如 `response.output_text.delta`、`response.output_item.added`、`response.function_call_arguments.delta`、`response.function_call_arguments.done` 这样的事件流。
  - 来源：<https://developers.openai.com/api/docs/guides/function-calling#streaming>
- OpenAI 在迁移指南中明确指出：
  - `Responses` 里的 function definition 形状不同
  - `Responses` 中 function 默认是 `strict`
  - Structured Outputs 从 `response_format` 改为 `text.format`
  - 来源：<https://developers.openai.com/api/docs/guides/migrate-to-responses>
- 官方还特别提醒：对于 reasoning models，**如果一轮响应里返回了 reasoning items 和 tool calls，那么后续把 tool output 回灌时，也必须把 reasoning items 一起传回**。
  - 来源：<https://developers.openai.com/api/docs/guides/function-calling#function-tool-example>

---

## 与当前抽象的关键张力

`ICompletionClient` 当前只有：

- `CompletionRequest.ModelId`
- `CompletionRequest.SystemPrompt`
- `CompletionRequest.Context`
- `CompletionRequest.Tools`

它**没有**以下 OpenAI Responses 专属字段：

- `previous_response_id`
- `conversation`
- `store`
- `include`
- `text.format`
- `reasoning`
- built-in tool 配置

这意味着新实现必须回答两个问题：

### 1. 多轮状态怎么走

建议答案：**默认走 full-input replay，不走 `previous_response_id`。**

理由：

- 当前抽象层没有保存 remote `response_id` 的位置。
- 现有兄弟实现都是“把完整历史重新投影给 provider”，而不是依赖 provider 的服务端状态。
- 这样更 portable，也更符合 `Completion.Abstractions` 现阶段的设计哲学。

### 2. reasoning continuity 怎么保

建议答案：**在 provider 层新增 `OpenAIResponsesReasoningBlock`，保存可 replay 的 reasoning item 原样载荷。**

理由：

- 官方要求 tool continuation 时带回 reasoning items。
- 现有抽象已允许 provider-specific `ActionBlock.ReasoningBlock`。
- 这和 Gemini 用 `GeminiReplayBlock` 保留 provider-native replay payload 的思路是一致的，只是粒度更细。

---

## 总体方案

新增以下文件：

- `prototypes/Completion/OpenAI/OpenAIResponsesClient.cs`
- `prototypes/Completion/OpenAI/OpenAIResponsesApiModels.cs`
- `prototypes/Completion/OpenAI/OpenAIResponsesMessageConverter.cs`
- `prototypes/Completion/OpenAI/OpenAIResponsesStreamParser.cs`
- `prototypes/Completion/OpenAI/OpenAIResponsesReasoningBlock.cs`
- `prototypes/Completion/OpenAI/OpenAIResponsesClientOptions.cs`

建议 `ApiSpecId`：

```csharp
"openai-responses-v1"
```

建议构造形状：

```csharp
public sealed class OpenAIResponsesClient : ICompletionClient {
    public OpenAIResponsesClient(
        string? apiKey,
        HttpClient httpClient,
        OpenAIResponsesClientOptions? options = null
    ) { ... }
}
```

这与现有 `OpenAIChatClient` 保持同样的使用习惯。

---

## 推荐默认策略

### 1. `store`

建议默认：

```json
"store": false
```

原因：

- 当前实现不打算依赖 `previous_response_id`
- 对现有 `ICompletionClient` 来说，服务端存储收益有限
- 无状态 replay 更容易与 Anthropic/Gemini 的现有心智模型对齐

### 2. `include`

建议默认在启用 reasoning replay 时附带：

```json
["reasoning.encrypted_content"]
```

原因：

- OpenAI 官方明确把它作为“无状态但保 reasoning continuity”的标准方案
- 我们的设计天然偏向 full-input replay，因此应优先保证 reasoning items 可被重新投影

### 3. `parallel_tool_calls`

建议默认：

```json
true
```

原因：

- 这与官方默认语义一致
- 现有 `ActionMessage` / `RawToolCall[]` 也能表达同一轮多个 tool call

### 4. 自定义函数 `strict`

虽然迁移指南说 Responses 中 functions 默认就是 strict，仍建议**显式写出**：

```json
"strict": true
```

原因：

- 减少“文档说默认如此，但兼容端实现不完全一致”的歧义
- 与当前代码库一贯的“形状尽量显式”风格一致

---

## 请求投影设计

### 顶层 request 形状

`OpenAIResponsesMessageConverter` 产出的核心请求建议为：

```json
{
  "model": "...",
  "instructions": "...",
  "input": [ ...items... ],
  "tools": [ ...function tools... ],
  "stream": true,
  "store": false,
  "include": ["reasoning.encrypted_content"],
  "parallel_tool_calls": true
}
```

其中：

- `instructions` 对应 `CompletionRequest.SystemPrompt`
- `input` 对应 `CompletionRequest.Context`
- `tools` 只先支持 `ToolDefinition -> function tool`
- 其他尚无抽象层承载的字段，通过 `OpenAIResponsesClientOptions.ExtraBody` 注入

### 为什么 `SystemPrompt -> instructions`

官方文档明确把 `instructions` 作为 system/developer guidance 的顶层位置；同时文档还说明：若配合 `previous_response_id`，新的 `instructions` 不会自动继承上一轮。但我们当前不走 `previous_response_id`，因此可以直接稳定映射为：

```text
CompletionRequest.SystemPrompt -> request.instructions
```

---

## `IHistoryMessage -> Responses input items` 映射

这是整个设计的核心。

### 1. `ObservationMessage`

映射为一个 `message` item：

```json
{
  "type": "message",
  "role": "user",
  "content": [
    { "type": "input_text", "text": "..." }
  ]
}
```

建议：

- `null` 或空白 observation 直接跳过，不制造无信息 item

### 2. `ActionMessage`

`Responses` 不适合像 chat/completions 那样把 text / tool_calls / reasoning 混到一条 assistant message 中。

建议按 block 顺序拆成多个 item：

- `ActionBlock.Text` -> assistant `message` item
- `ActionBlock.ToolCall` -> `function_call` item
- `ActionBlock.ReasoningBlock` -> provider-specific reasoning item

这样做的好处是：

- 更贴近 Responses 的原生 item 语义
- 不会把多个不同语义硬塞回一条 assistant message
- 便于严格维护 tool result 的邻接关系

#### `ActionBlock.Text`

建议把连续文本块合并为一个 assistant `message` item：

```json
{
  "type": "message",
  "role": "assistant",
  "content": [
    { "type": "output_text", "text": "..." }
  ]
}
```

#### `ActionBlock.ToolCall`

映射为：

```json
{
  "type": "function_call",
  "call_id": "<RawToolCall.ToolCallId>",
  "name": "<RawToolCall.ToolName>",
  "arguments": "<RawToolCall.RawArgumentsJson>"
}
```

说明：

- `RawToolCall.ToolCallId` 在 Responses 侧应被视为 `call_id`
- 如 future API 还要求额外 `id`，可在 provider 内合成 deterministic item id，但**抽象真相源仍是 `call_id`**

#### `ActionBlock.ReasoningBlock`

仅允许 replay 与 OpenAI Responses 同源的 reasoning block：

- `OpenAIResponsesReasoningBlock` -> 直接吐回原始 item JSON
- `ActionBlock.TextReasoningBlock` 不做跨 provider replay

这与当前 Anthropic / Gemini 的“只 replay 同源 reasoning payload”原则一致。

### 3. `ToolResultsMessage`

这部分要严格处理。

Responses 里工具调用和工具结果是两种不同 item，且要靠 `call_id` 对齐。

建议沿用当前兄弟实现的严格顺序校验：

1. `ActionMessage` 中的 `function_call` items 进入 pending 队列
2. 下一个 `ToolResultsMessage.Results` 必须与 pending 逐个对齐
3. 每个 `ToolResult` 投影为：

```json
{
  "type": "function_call_output",
  "call_id": "<ToolResult.ToolCallId>",
  "output": "<ToolResult.GetFlattenedText()>"
}
```

4. 若 `ToolResultsMessage.Content` 非空，则在所有 `function_call_output` 之后再追加一个新的 user `message`

这里的“紧随其后”是指 **history message 边界**：一旦出现 pending `function_call`，下一个历史消息必须是对应的 `ToolResultsMessage`。但在同一个 `ActionMessage` 内，仍按原始 `ActionBlock` 顺序逐项投影；如果 provider 原生输出就是 `text -> function_call -> text`，则仍保留这一顺序，而不是在投影层重排。

这和当前 OpenAI Chat 严格模式的语义完全一致，只是从：

- `assistant.tool_calls`
- `role=tool`

改为：

- `function_call`
- `function_call_output`

### 投影顺序示意

假设 canonical history 是：

```text
Observation("帮我查天气")
Action(Text("我先查一下"), ToolCall(get_weather))
ToolResults(Result(get_weather=晴天))
Observation("再顺便给个穿衣建议")
```

则 Responses `input` 应近似为：

```json
[
  { "type": "message", "role": "user", "content": [{ "type": "input_text", "text": "帮我查天气" }] },
  { "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": "我先查一下" }] },
  { "type": "function_call", "call_id": "call_1", "name": "get_weather", "arguments": "{...}" },
  { "type": "function_call_output", "call_id": "call_1", "output": "晴天" },
  { "type": "message", "role": "user", "content": [{ "type": "input_text", "text": "再顺便给个穿衣建议" }] }
]
```

---

## Tool schema 设计

继续复用现有 `JsonToolSchemaBuilder`，但目标形状从 chat/completions 改为 Responses function tool：

```json
{
  "type": "function",
  "name": "get_weather",
  "description": "...",
  "parameters": { ...json schema... },
  "strict": true
}
```

说明：

- `parameters` 仍来自 `ToolDefinition.InputSchema`
- 先只支持 custom function tools
- built-in tools 不在 phase 1 范围内，因为 `CompletionRequest.Tools` 当前只表达本地函数定义，不表达 `web_search` / `file_search` 这类 hosted tool 配置

---

## 流式解析设计

### 为什么不能复用 `OpenAIChatStreamParser`

因为 Responses 的流式协议已经不是：

- `choices[].delta.content`
- `choices[].delta.tool_calls[]`

而是新的 typed event 流：

- `response.output_text.delta`
- `response.output_item.added`
- `response.function_call_arguments.delta`
- `response.function_call_arguments.done`
- `response.output_item.done`
- `response.completed`

因此必须新写 parser。

### 客户端主循环

`OpenAIResponsesClient.StreamCompletionAsync(...)` 仍然可以沿用兄弟实现的骨架：

1. `POST v1/responses`
2. `ResponseHeadersRead`
3. `StreamReader.ReadLineAsync(...)`
4. 过滤空行
5. 忽略 `event:` 行，只解析 `data:` 行里的 JSON
6. 把 JSON 交给 `OpenAIResponsesStreamParser.ParseEvent(...)`

也就是说，虽然 Responses 的 SSE 帧里通常同时有 `event:` 和 `data:`，但**真正作为状态机真相源的还是 JSON 里的 `type` 字段**。

### 需要处理的事件

#### `response.output_text.delta`

直接：

```text
delta -> CompletionAggregator.AppendContent(delta)
```

#### `response.output_item.added`

只在以下场景建立中间状态：

- `item.type == "function_call"` -> 建立 pending function-call state
- `item.type == "reasoning"` -> 记录 reasoning item 生命周期已开始
- `item.type == "message"` -> 通常无需额外状态

#### `response.function_call_arguments.delta`

按 `item_id` 聚合 `arguments` 字符串片段。

建议内部状态：

```csharp
Dictionary<string, FunctionCallState>
```

`FunctionCallState` 至少保存：

- `ItemId`
- `OutputIndex`
- `CallId`
- `ToolName`
- `StringBuilder ArgumentsBuilder`

#### `response.function_call_arguments.done`

优先在此事件上 finalize：

- 取最终 `item.name`
- 取最终 `item.call_id`
- 取最终 `item.arguments`
- 归一化 JSON 文本
- `aggregator.AppendToolCall(...)`

然后从 pending 状态移除。

#### `response.output_item.done`

这个事件用来兜底和补充：

- `item.type == "function_call"` 且前面没收到 `arguments.done` 时，作为兜底 finalize
- `item.type == "reasoning"` 时，构造 `OpenAIResponsesReasoningBlock`
- `item.type == "message"` 时通常无需额外动作，因为文本已经靠 delta 进入 aggregator

#### `response.completed`

标记流正常结束，并要求 parser flush 所有允许 flush 的完整状态。

#### `response.failed` / `error`

与现有实现一致：

- 不在 parser 内直接抛 provider-specific 业务异常
- 交给 `CompletionAggregator.AppendError(...)`
- transport 级非 2xx 仍由 `CompletionHttpRequestUtility` 抛 `HttpRequestException`

### 早停语义

与 `OpenAIChatClient` / `GeminiClient` 保持一致：

- 若 `observer.ShouldStop == true`
- client 中断 read loop
- parser 丢弃未完成的 function-call 中间状态
- aggregator 调用 `AbortIncompleteStreamingState()`

这样不会把半截 arguments JSON 误当成完整 `RawToolCall`

---

## `OpenAIResponsesReasoningBlock` 设计

建议新增一个 provider-specific reasoning block：

```csharp
public sealed record OpenAIResponsesReasoningBlock(
    string RawItemJson,
    CompletionDescriptor Origin,
    string? PlainTextForDebug = null
) : ActionBlock.ReasoningBlock(Origin, PlainTextForDebug);
```

设计原则：

- **真相源是 `RawItemJson`**
- `PlainTextForDebug` 只是调试字段
- converter replay 时直接把 `RawItemJson` 反序列化回 input item

为什么不用单纯的 `TextReasoningBlock`：

- OpenAI 官方推荐的无状态 reasoning continuity 依赖的可能是 `encrypted_content`
- 这不是明文字符串，不能靠 `TextReasoningBlock` 表达
- 如果把 provider-native item 结构抹平，就会失去后续 replay 能力

这和 `GeminiReplayBlock` 的精神一致，只是我们这里更推荐把 replay payload 绑定到 reasoning block 本身，而不是整轮 response。

---

## `OpenAIResponsesClientOptions` 建议

建议只放少量真正高价值的开关：

```csharp
public sealed class OpenAIResponsesClientOptions {
    public bool Store { get; init; } = false;
    public bool IncludeEncryptedReasoning { get; init; } = true;
    public bool ParallelToolCalls { get; init; } = true;
    public JsonObject? ExtraBody { get; init; }
}
```

说明：

- 先不要引入 `Dialect`
  - 这是 OpenAI 官方 endpoint，不是 openai-compatible 野生方言场
  - 等真出现第 2、第 3 个差异点再抽象
- `ExtraBody` 保留逃生口，方便临时试验 `reasoning`、`text.format`、`service_tier` 等字段

---

## 与现有兄弟实现的对应关系

### 与 `OpenAIChatClient`

相同点：

- 同样使用 OpenAI HTTP auth
- 同样走 SSE
- 同样复用 `CompletionAggregator`
- 同样复用 `JsonToolSchemaBuilder`

不同点：

- endpoint 从 `v1/chat/completions` 改为 `v1/responses`
- request body 从 `messages` 改为 `input items`
- function calling 从 `tool_calls[]` 改为 `function_call` / `function_call_output`
- parser 从 `choices[].delta` 改为 `response.*` event state machine

### 与 `AnthropicClient`

最相似的点其实是**工具结果顺序校验**：

- 都要求 tool output 严格对应上一轮 pending tool calls
- 都要在 converter 层完成 1:1 对齐检查

### 与 `GeminiClient`

最相似的点是**provider-native replay payload 必须保留**：

- Gemini 需要 `thoughtSignature`
- OpenAI Responses reasoning 需要保留 reasoning item，尤其是加密 reasoning 的情况

---

## 明确的 Phase 拆分

### Phase 1

目标：打通最小可用主链。

范围：

- `OpenAIResponsesClient`
- `OpenAIResponsesMessageConverter`
- `OpenAIResponsesStreamParser`
- text output
- custom function tool calling
- `function_call_output` replay
- `OpenAIResponsesReasoningBlock` 的捕获与 replay

不做：

- built-in tools
- MCP tools
- structured outputs 抽象化
- `previous_response_id`
- conversation object
- multimodal input

### Phase 2

目标：补齐可观测性和更多 OpenAI-native 能力。

范围：

- reasoning item 更细粒度事件支持
- annotations / citations 捕获策略评估
- `text.format` 与 structured outputs 设计收口
- `reasoning` / `service_tier` / `truncation` 等 option 收口

### Phase 3

目标：评估是否需要上升抽象层。

只在确实出现以下需求时再考虑：

- 上层开始需要消费 `previous_response_id`
- 上层需要显式区分 built-in tools 与 local functions
- 上层需要结构化输出作为一等能力

否则保持 `Completion.Abstractions` 不动。

---

## 测试建议

### 1. Converter 测试

至少覆盖：

- `ObservationMessage -> user message item`
- `ActionMessage(Text + ToolCall) -> assistant message + function_call`
- `ToolResultsMessage -> function_call_output`
- tool result 与 pending call 的 1:1 对齐校验
- `OpenAIResponsesReasoningBlock` replay

### 2. Stream parser 测试

至少覆盖：

- `response.output_text.delta` 连续拼接
- `response.function_call_arguments.delta` 聚合
- `response.function_call_arguments.done` finalize
- `response.output_item.done` 兜底 finalize
- early stop 丢弃半截 function call

### 3. End-to-end transport 测试

建议复用现有 record/replay pipeline：

- live record 一次真实 `v1/responses`
- 生成 JSONL exchange
- replay 同一份交换日志
- 验证最终 `CompletionResult.Message.Blocks`

### 4. 回归测试重点

最值得锁死的不是字段快照，而是行为契约：

- tool result 必须紧跟 pending `function_call`
- 未完成 arguments 不得产出 `RawToolCall`
- reasoning block 只能 replay 同源 payload
- `store=false + encrypted reasoning` 路径下，多轮 tool continuation 不能丢 reasoning item

---

## 关键决策总结

### 保持不变

- 不修改 `ICompletionClient`
- 不修改 `CompletionRequest`
- 不把 OpenAI Responses 特有字段上推到 Abstractions

### 新增

- 新 provider：`OpenAIResponsesClient`
- 新 converter / parser / api models
- 新 provider-specific reasoning block
- 少量 client-level options

### 明确不做

- 不在第一阶段引入 `previous_response_id`
- 不在第一阶段为 built-in tools 扩抽象
- 不把 `Responses` 和 `Chat Completions` 混成同一个 parser

---

## 推荐实施顺序

1. 先落 `ApiModels + ClientOptions + Client` 骨架
2. 再落 `MessageConverter`，先只支持 `Observation + Action.Text + ToolCall + ToolResults`
3. 再落 `StreamParser`，先打通 text delta + function call
4. 再补 `OpenAIResponsesReasoningBlock`
5. 最后做 record/replay 和多轮 tool continuation 测试

这个顺序最稳，因为：

- parser 和 converter 的复杂度彼此独立
- text + function calling 先通后，reasoning replay 的边界会更清楚
- 一旦 reasoning 先写，很容易在没有主链回归保护时把状态机写复杂

---

## 当前建议的文件命名

建议保持与现有 OpenAI Chat 路径对称：

```text
prototypes/Completion/OpenAI/
├─ OpenAIResponsesClient.cs
├─ OpenAIResponsesClientOptions.cs
├─ OpenAIResponsesApiModels.cs
├─ OpenAIResponsesMessageConverter.cs
├─ OpenAIResponsesStreamParser.cs
└─ OpenAIResponsesReasoningBlock.cs
```

这样后续在同目录下同时维护：

- `OpenAIChat*`
- `OpenAIResponses*`

会非常直观。

---

## 最终建议

这项设计最重要的不是“把 endpoint 改成 `/v1/responses`”，而是接受一个事实：

**Responses 的核心范式已经从“assistant message 增量”切到“typed item event stream”。**

因此最稳的实现不是在 `OpenAIChatClient` 上继续叠兼容层，而是新建一条并列主线，让：

- converter 真正产出 item
- parser 真正消费 `response.*` 事件
- reasoning 真正保留可 replay 的原生 item

这样既对得起 OpenAI 官方语义，也对得起 `prototypes/Completion` 当前已经形成的分层风格。

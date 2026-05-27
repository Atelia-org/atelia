# ToolResult Block 化重构方案

## 结论

这次重构在设计上是合理的，在实现上也是可行的。

理由很直接：

- `ActionMessage` 已经采用 `Blocks` 作为真相源，而 `ToolResult` 仍把结果压扁成单个 `string Result`，抽象层左右不对称。
- 当前主线里真正依赖 `ToolResult.Result` 的核心代码面很小，主要集中在三个 provider converter 和对应测试，改动半径可控。
- 现有三个 provider 都能接受“先 block 化、再降级为文本”的路径，因此可以先做一个行为等价的 phase 1，只引入扩展点，不急着承诺多模态传输。

这次重构最适合被理解为：

1. 先把 `ToolResult` 的真相源从单字符串升级为有序 block 列表。
2. 当前版本只定义 `Text` block。
3. 三个 converter 继续向各自 API 输出文本形式，保持现有行为基本不变。
4. 等未来真的出现图片、文件引用、artifact、结构化可视结果等需求时，再扩展 block kind 和 provider 降级策略。

## 为什么值得做

### 1. 当前抽象已经出现明显失衡

`ActionMessage` 的设计原则很清晰：

- `Blocks` 是真相源。
- `GetFlattenedText()` 是 lossy derived view。
- provider converter 负责把 provider-neutral block 语言投影到具体协议。

`ToolResult` 现在却反过来：

- `Result` 既是真相源，又是传输形态。
- 一旦未来工具结果不再是单段纯文本，就只能继续往字符串里塞约定，或者把 JSON 字符串再包进字符串。

这会把“工具真正产出了什么”和“当前 provider 暂时只收文本”混在一起。

### 2. 未来扩展点确实自然落在 tool result 上

`RawToolCall` 已经保留了原始 JSON 参数文本，说明这个抽象层并不排斥“先保真，再按需要解释”的思路。

同样地，工具执行结果未来很可能需要表达的不只是纯文本，比如：

- 图片或截图引用
- 文件或 artifact 引用
- 结构化表格/卡片的中间表示
- 可供 UI 单独展示的提示块
- 同一个工具结果里的多段内容，而不是一整坨字符串

如果不先把 `ToolResult` 从单字符串解开，后面每一种扩展都会逼着我们再做一次破坏性重构。

### 3. 当前是低成本窗口期

按当前主线代码来看，`ToolResult.Result` 的消费面主要是：

- `prototypes/Completion/Anthropic/AnthropicMessageConverter.cs`
- `prototypes/Completion/Gemini/GeminiMessageConverter.cs`
- `prototypes/Completion/OpenAI/OpenAIChatMessageConverter.cs`
- `tests/Completion.Tests/*` 中对应 converter / projection round-trip 测试

工具执行链本身目前停在 `ToolExecuteResult.Content : string`。这反而说明 phase 1 可以很收敛：

- 上游工具执行先不动。
- 在把 `ToolExecuteResult` 组装成 `ToolResult` 时，先包成单个 `Text` block。
- 等真的需要端到端多模态，再继续把 `ToolExecuteResult` 也升级掉。

这是一种很稳的“抽象先行，能力后补”路径。

## 设计判断

### 判断一：应该 block 化，但不要直接复用 `ActionBlock`

我认为应该新建独立的 `ToolResultBlock`，而不是强行复用 `ActionBlock`。

原因：

- `ActionBlock` 的语义是 assistant/action 内容语言，里面天然有 `ToolCall`、`ReasoningBlock` 这种只属于 assistant 的块。
- tool result 的语义边界不同，它表达的是“工具返回了什么”，不是“模型说了什么”。
- 如果复用 `ActionBlock`，会在类型层面暗示 tool result 也可能包含 `ToolCall` 或 thinking，这种信号是错的。

因此这次重构更合适的方向是：

- `ActionBlock` 继续服务于 assistant side。
- `ToolResultBlock` 单独服务于 tool-result side。
- 两者在设计风格上对齐，但不强行合并成一个大而泛的 `ContentBlock`。

### 判断二：phase 1 只支持 `Text` block 是对的

现在就把图片、artifact、json、file-ref 一次性设计全，很容易过度抽象。

更稳妥的做法是：

- 先把容器形状改对。
- 只落一个 `Text` block。
- 等真实需求出现时，再增加新的 block kind，并同步定义 provider 降级规则。

这样做的收益是：

- 当前行为可以几乎不变。
- API 语义已经转到“blocks 为真相源”。
- 未来新增 block kind 时，不需要再次推翻 `ToolResult` 的整体形状。

### 判断三：这次不应该顺手 block 化 `ToolResultsMessage.Content`

`ToolResultsMessage.Content` 当前承担的是“工具结果之外的附加观测文本”。

它和 `Results` 的职责并不相同：

- `Results` 是逐个 tool-call 对齐、带 `ToolCallId` 的结果集合。
- `Content` 是额外附带给模型的一段 observation 文本。

这次重构的真正扩展点是“单个工具结果的内部形状”，所以先只动 `ToolResult` 更合理。不要把范围顺手扩大到 `ToolResultsMessage.Content`，否则容易把一个清晰的抽象升级做成大杂烩。

## 建议的目标形状

下面是建议的 phase 1 目标 API。代码只是说明形状，不要求逐字照搬。

```csharp
namespace Atelia.Completion.Abstractions;

public abstract record ToolResultBlock {
    private protected ToolResultBlock() { }

    public abstract ToolResultBlockKind Kind { get; }

    public sealed record Text(string Content) : ToolResultBlock {
        public override ToolResultBlockKind Kind => ToolResultBlockKind.Text;
    }
}

public enum ToolResultBlockKind {
    Text,
}

public sealed record ToolResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    IReadOnlyList<ToolResultBlock> Blocks
) {
    public string GetFlattenedText() => string.Concat(
        Blocks.OfType<ToolResultBlock.Text>().Select(static block => block.Content)
    );

    public static ToolResult FromText(
        string toolName,
        string toolCallId,
        ToolExecutionStatus status,
        string content
    ) => new(
        toolName,
        toolCallId,
        status,
        new[] { new ToolResultBlock.Text(content) }
    );
}
```

这里最关键的是两个原则：

- `Blocks` 是唯一真相源。
- `GetFlattenedText()` 只是当前 provider 降级路径用的 derived view。

## 与当前执行链的关系

当前主线里，工具执行产物还是：

```csharp
public sealed record ToolExecuteResult(
    ToolExecutionStatus Status,
    string Content
)
```

这说明真正的多模态工具执行结果还没有打通到执行层。

但这不构成阻碍，反而给出了一条非常自然的分阶段路径：

### Phase 1

- `ToolExecuteResult` 维持 `string Content`
- `ToolResult` 升级为 `Blocks`
- 从执行结果组装 history 时，统一映射为单个 `ToolResultBlock.Text`

### Phase 2

- 如果出现真实的非文本工具结果需求，再把 `ToolExecuteResult.Content` 一并升级为 block 列表或更高层 artifact 结果
- 再决定工具实现接口是否直接返回 blocks / artifacts

这样做的好处是：

- phase 1 改动面小
- phase 1 就能把 history 抽象修正到位
- 不会为了“也许未来需要多模态”而强迫所有工具今天就改写执行接口

## 当前三条主线 provider 与 OpenAI Responses API 对照

刚需区分的一点是：**当前仓库里的 DTO / converter 形状，不等于上游 API 端点的真实能力边界。**

这次调查后，当前主线三条已接入端点，再加上后续可能接入的 OpenAI Responses API，可以概括为：

| Provider | 上游端点对工具返回值的能力 | 当前仓库实现 | 对本次重构的意义 |
|---|---|---|---|
| Anthropic Messages | `tool_result.content` 可为 `string`，也可为 block 数组；官方 SDK 中可见的 block 类型包括 `text`、`image`、`document`、`search_result`、`tool_reference` | 当前 `AnthropicToolResultBlock.Content` 被收窄为 `string` | Anthropic 其实是最适合保真映射 `ToolResultBlock[]` 的 provider |
| Gemini generateContent | `functionResponse` 同时支持 `response: JSON object` 与 `parts: FunctionResponsePart[]`；`FunctionResponsePart` 支持 `inline_data` / `file_data`，要求 IANA MIME type | 当前 `GeminiFunctionResponse` 只建模了 `response: JsonElement`，未建模 `parts` | Gemini 也支持多模态，但形状不是通用 content block 列表，而是“结构化响应 + 媒体附件” |
| OpenAI Chat Completions | `role="tool"` 的 `content` 本质上是文本通道；官方 SDK 允许 `string` 或 text parts，但不是通用多模态 parts | 当前 `OpenAIChatMessage.Content` 被收窄为单个 `string` | OpenAI Chat 是当前三家里能力最弱的一档，基本只能作为 text-lowering 下限 |
| OpenAI Responses API | `function_call_output.output` 与 `custom_tool_call_output.output` 可为 `string`，也可为 `input_text` / `input_image` / `input_file` 内容项数组；部分 built-in tool 还有各自专用 output item 形状 | 当前主线尚未接入 Responses API，因此没有对应 DTO / converter | 如果未来接入，它会明显强于 OpenAI Chat，不必把所有 tool result 都压回纯文本 |

因此，这次重构文档里应该把“phase 1 统一走文本降级”理解为**当前主线三条 provider 的可移植性基线**，而不是这些 API 上游规格本身都只能吃文本。

## Anthropic

Anthropic Messages 的真实上游规格，比当前仓库实现宽得多。

根据官方 SDK 类型，通用 `tool_result` 的 `content` 可以是：

- 单个字符串
- 或者 block 数组，其中已公开可见的 block 类型至少包括：
  - `text`
  - `image`
  - `document`
  - `search_result`
  - `tool_reference`

这意味着：

- 从“上游端点真实能力”看，Anthropic 已经天然支持多模态工具结果。
- 从“当前仓库实现”看，`AnthropicToolResultBlock.Content : string` 只是本地 DTO 的阶段性收窄，不是 Anthropic 规格的硬上限。

对本次重构的含义是：

- phase 1 仍然可以保守地做 `ToolResult.Blocks -> result.GetFlattenedText()`，以最小改动维持当前行为。
- 但中长期不应把 Anthropic 视为“只能文本”的 provider；相反，它是未来最适合做 richer projection 的优先落点。

换句话说，Anthropic 不是本次设计的约束项，而是未来能力扩展的上限参考。

## OpenAI Chat

OpenAI Chat Completions 的真实约束，基本就是文本。

官方 SDK 对 `ChatCompletionToolMessageParam` 的定义可归纳为：

- `content` 可以是 `string`
- 也可以是 text parts 数组
- 但不是 user message 那种通用多模态 input parts，也不是图片 / 音频 / 文件这类 tool message 内容

这意味着：

- OpenAI Chat 的 tool-return 通道，本质上仍是“文本通道”。
- 即便允许 text parts，本质收益也只是文本结构更细，不是多模态承载能力跃迁。

当前仓库实现进一步把它收窄为：

```csharp
new OpenAIChatMessage {
    Role = "tool",
    ToolCallId = result.ToolCallId,
    Content = BuildToolResultContent(result)
}
```

其中 `Content` 最终仍是字符串；当前实现里还会把 tool result 再包装成一个 JSON 字符串：

```json
{
  "tool_name": "search",
  "status": "success",
  "result": "ok"
}
```

因此，对 OpenAI Chat 路径的正确判断不是“也许以后能直接支持多模态”，而是：

- phase 1 继续输出同样的 JSON 字符串完全合理
- `result.Result` 改成 `result.GetFlattenedText()` 即可
- 如果未来出现非文本 `ToolResultBlock`，OpenAI Chat 需要显式定义 text-lowering、摘要化，或直接拒绝，而不是假设上游能原样承载

OpenAI Chat 才是当前三家里的**可移植性下限**。

## OpenAI Responses API

OpenAI Responses API 和 OpenAI Chat Completions 不能混为一谈。

在官方 SDK 类型里，自定义工具返回值至少有两条相关通道：

- `function_call_output.output`
- `custom_tool_call_output.output`

它们的形状都不是“只能字符串”，而是：

- `string`
- 或 `ResponseInputText[] / ResponseInputImage[] / ResponseInputFile[]` 这一类内容项数组

也就是说，就自定义 function tool / custom tool 而言，Responses API 原生支持：

- 文本输出
- 图片输出
- 文件输出

这比 OpenAI Chat Completions 的 `role="tool"` 文本通道宽得多。

除此之外，Responses API 里的 built-in tool 还经常有自己的专用 output item 形状。例如：

- `computer_call_output`：截图图像
- `shell_call_output`：结构化 stdout / stderr chunks
- `local_shell_call_output`：JSON 字符串

这说明 Responses API 的设计思路并不是“所有工具结果都塞进同一个字符串槽位”，而是：

- 对通用 function/custom tool，提供 text / image / file 三类通用输出项
- 对 built-in tools，则允许各自定义更专门的 output item 结构

对本次 `ToolResult` block 化重构的含义是：

- 如果未来接入 OpenAI Responses API，就不必沿用 OpenAI Chat 的纯文本下限。
- 至少在 provider-specific projection 上，可以自然映射 `Text` / `Image` / `File` 这几类结果。
- 但它的 shape 依然不同于 Anthropic 的统一 content block，也不同于 Gemini 的 `response + parts`；因此 Responses API 也需要单独的投影规则，而不是简单复用其他 provider 的编码方式。

需要强调的是：**Responses API 当前不在本仓库主线实现范围内。**

因此它影响的是中长期抽象设计判断，而不是当前 phase 1 的最小改造半径。

## Gemini

Gemini generateContent 的真实能力介于 Anthropic 与 OpenAI Chat 之间，而且 shape 明显不同。

根据官方 SDK 类型，`functionResponse` 不只是一个 JSON object，还包括另一条媒体通道：

- `response: dict[str, Any]`：结构化 JSON 响应
- `parts: FunctionResponsePart[]`：组成函数响应的附加 parts

而 `FunctionResponsePart` 至少支持：

- `inline_data`
- `file_data`

并要求使用 IANA MIME type 标注媒体类型。

这说明 Gemini 的工具返回值能力不是“只有一个 JSON object”，而是：

- 文本 / 结构化结果可以放进 `response`
- 二进制媒体或文件引用可以通过 `parts` 附带

当前仓库实现只保留了：

```csharp
new GeminiFunctionResponse {
    Name = result.ToolName,
    Id = result.ToolCallId,
    Response = JsonSerializer.SerializeToElement(...)
}
```

也就是说，本地实现目前只覆盖了 Gemini 规格中的 `response` 这一半，还没有覆盖 `parts` 这一半。

对本次重构的含义是：

- phase 1 仍然建议继续输出 `result: result.GetFlattenedText()`，维持三家 provider 的统一可移植路径
- 但未来如果真的引入图片 / 文件 / artifact 类工具结果，Gemini 可以考虑走“`response` + `parts`”的 provider-specific richer projection，而不是只能退回字符串

需要注意的是，Gemini 的 richer projection 形状**并不等同于** Anthropic 的 content block 列表。因此未来如果要让 `ToolResultBlock` 真正映射 Gemini，通常需要额外一层投影规则，而不能简单假设“一种 block 模型原样平移到所有 provider”。

## 这对 phase 1 的直接影响

调查结果反而强化了“phase 1 只引入 `ToolResultBlock.Text`”的正确性。

原因不是三家上游都只支持文本，而是：

- Anthropic 虽强，但当前实现尚未建模它的 richer `tool_result.content`
- Gemini 也支持 richer function response，但 shape 与 Anthropic 不同，当前实现也未建模
- OpenAI Chat 真实上限就是文本，因此它天然会成为跨 provider 共识的 portability floor
- Responses API 虽然支持 text / image / file 工具输出，但当前主线尚未接入，因此不构成这轮 phase 1 的实现约束

因此 phase 1 的最稳方案仍然是：

- Completion.Abstractions 先把 `ToolResult` 改成 block-first
- 当前只引入 `ToolResultBlock.Text`
- 三个 converter 统一先走 text-lowering
- 把 Anthropic / Gemini richer projection 作为 phase 2 的 provider-specific 能力扩展

这样做不会否认 Anthropic / Gemini 的上游能力，只是承认当前仓库主线还没有把这些能力接进来。

## 迁移建议

建议把这次重构拆成下面几个明确步骤。

### 第 1 步：在 Abstractions 层引入 `ToolResultBlock`

新增：

- `ToolResultBlock`
- `ToolResultBlockKind`

当前只实现：

- `ToolResultBlock.Text`

### 第 2 步：把 `ToolResult` 的真相源切换到 `Blocks`

建议方向：

- 去掉 `string Result`
- 改为 `IReadOnlyList<ToolResultBlock> Blocks`
- 提供 `GetFlattenedText()` 作为派生视图

如果想降低 call-site 噪音，可以保留一个 `FromText(...)` 工厂，但不要再让 `Result` 继续充当主字段。

### 第 3 步：更新三个 converter

三个 converter 都只需要做同一类修改：

- 不再直接读取 `result.Result`
- 改为读取 `result.GetFlattenedText()`

也就是说，phase 1 的 provider 行为仍然全部是 text-lowering。

这里特意强调：当前实现范围仍然只包括三条已接入 converter。OpenAI Responses API 还没有进入主线代码，因此不属于本轮改造对象。

### 第 4 步：更新测试与示例

需要同步修改的主要是：

- `tests/Completion.Tests/Anthropic/*`
- `tests/Completion.Tests/Gemini/*`
- `tests/Completion.Tests/OpenAI/*`
- `docs/Completion/quick-start.md` 中构造 `ToolResult` 的示例
- 任何直接 `new ToolResult(..., "ok")` 的调用点

这一步基本是机械迁移。

### 第 5 步：把 `ExecuteError` 的合成失败结果也统一成 block

当前三个 converter 在 pending tool call 缺失对应结果时，会用 `ExecuteError` 合成失败 `ToolResult`。

重构后建议统一成：

- `Status = Failed`
- `Blocks = [ new ToolResultBlock.Text(toolResults.ExecuteError) ]`

这样语义最整齐，不会保留字符串旁路。

### 第 6 步：把 `ToolExecuteResult` 的升级作为后续独立议题

不要把 phase 1 和“工具执行接口一并多模态化”绑死。

后者应该等真实需求出现后单独设计，届时再判断：

- 是返回 `ToolResultBlock[]`
- 还是返回更高层的 artifact/result object
- 还是把“模型上下文回灌内容”和“UI 展示内容”显式拆开

## 风险与边界

### 风险一：抽象提早，但能力尚未真正提升

这是事实，但不是问题。

这次 phase 1 的价值本来就不是“立刻支持多模态”，而是先把抽象从单字符串纠正成可扩展形态。

### 风险二：未来 block kind 设计可能和今天预想不同

所以当前不要预设太多种类。

只落 `Text`，其余留待真实需求驱动，是最稳的做法。

### 风险三：provider 投影策略天然会分叉

这也是正常的。

未来更可能出现的局面是：

- Anthropic 能直接吃 richer `ToolResultBlock[]`
- Gemini 需要把 richer result 投影成 `response + parts`
- OpenAI Chat 只能继续吃文本或文本摘要
- OpenAI Responses API 至少能吃 text / image / file 内容项，但需要独立于 OpenAI Chat 的投影逻辑

因此 phase 1 里统一走 text-lowering 是合理的，但 phase 2 不应再假设这些 provider 会长期保持同构。届时应该把差异显式写进 converter 规则，而不是继续让 `ToolResult` 退回字符串。

## 建议的验收标准

这次重构完成后，至少应满足下面几点：

- `ToolResult` 不再把单字符串当真相源
- 三个 converter 在 text-only 输入下行为与现在保持等价
- 所有现有 Completion.Tests 通过
- synthetic failure 路径不再绕开 block 抽象
- 文档与 quick-start 示例统一改成 blocks 构造方式或 `FromText(...)`
- 文档明确区分“上游端点真实能力”和“当前仓库实现已接入的能力”

## 最终建议

建议做，而且建议按“最小但方向正确”的方式做：

- 只重构 `ToolResult`
- 只新增 `ToolResultBlock.Text`
- 三个 provider 全部继续走文本降级
- 不顺手扩大到 `ToolResultsMessage.Content`
- 不把 `ToolExecuteResult` 一起拖进来

这会把 Completion history 抽象修到一个更一致的位置，同时把未来多模态工具结果的扩展点提前留出来，成本也处在当前可接受范围内。

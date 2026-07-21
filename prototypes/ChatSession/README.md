# Atelia.ChatSession - 快速上手（面向使用者）

> **读者**：要在自己的宿主程序里持久化多轮聊天历史，并复用 `Atelia.Completion` / `Atelia.Completion.Tools` 跑自动 tool loop 的上层应用作者。
> **不读这份**：要改 `StateJournal` 持久化形态、消息序列化细节或 compaction / rewind 内部算法的人。
> **配套阅读**：如何构造 `ICompletionClient`、`CompletionRequest` 与工具定义，请先看 [../Completion/quick-start.md](../Completion/quick-start.md) 和 [../Completion.Tools/README.md](../Completion.Tools/README.md)。

---

## 1. 这层库解决什么问题

`Atelia.ChatSession` 不是一个纯内存聊天包装器。它做的是：

- 把对话历史持久化到磁盘目录中
- 直接复用 `ICompletionClient` 发起多轮聊天
- 自动执行模型发出的 tool call，并把 `ToolResultsMessage` 回灌到下一轮
- 允许在进程重启后重新打开同一会话
- 提供“取出最近一轮重来”和“把旧前缀压缩成 recap”的宿主级能力

可以把它理解成：

```text
Completion client + ToolRegistry + durable history + tool loop
    => ChatSessionEngine
```

如果你不想自己手搓下面这条链：

- 追加 user message
- 保存历史
- 发起 completion
- 处理 tool calls
- 回灌 tool results
- 再次 completion
- 持久化中间状态

那 `ChatSessionEngine` 就是这层现成的执行壳。

---

## 2. 心智模型

对使用者来说，主角只有四个类型：

| 类型 | 作用 |
|---|---|
| `ChatSessionEngine` | 会话引擎。负责持久化 history、自动 tool loop、rewind、compaction |
| `ChatSessionCreateOptions` | 只在首次创建会话时使用，定义 `ModelId`、`SystemPrompt`、`CompletionSurfaceId`、分支名 |
| `ChatSessionRuntime` | 每次打开 / 创建时提供的运行时依赖：`ICompletionClient`、工具会话 `ToolSession` |
| `ChatSessionTurnResult` | 一次 `SendMessageAsync(...)` 的最终结果，含最终 assistant message、invocation、errors、执行过多少次工具 |

### 2.1 `ChatSessionRuntime` 是什么

```csharp
public sealed record ChatSessionRuntime(
    ICompletionClient CompletionClient,
    string CompletionSurfaceId,
    ToolSession ToolSession
);
```

这意味着：

- 聊天模型来自 `CompletionClient`
- 工具可见性与执行策略来自 `ToolSession`（由 `registry.CreateSession(...)` 产出）
- `CompletionSurfaceId` 是你给这条 prompt/tool surface 起的稳定身份标识

`CompletionSurfaceId` 不等于 `ApiSpecId`，也不由框架自动推导。它更像“我这条持久化会话对应哪套上层交互表面”的人工标签。创建和重开时都必须一致。

---

## 3. 三十秒上手

### 3.1 先构造 runtime

`Atelia.ChatSession` 自己不创建模型 client。你先用 `Atelia.Completion` 构造一个 `ICompletionClient`，再把它塞进 `ChatSessionRuntime`。

下面示例走本地 OpenAI-compatible 路径：

```csharp
using Atelia.ChatSession;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Tools;
using Atelia.Completion.Transport;

using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
    new Uri("http://localhost:8000/")
);

using var completionClient = new OpenAIChatClient(
    apiKey: null,
    httpClient: httpClient,
    dialect: OpenAIChatDialects.SgLangCompatible
);

var runtime = new ChatSessionRuntime(
    CompletionClient: completionClient,
    CompletionSurfaceId: "openai-chat/strict",
    ToolSession: new ToolRegistry(Array.Empty<ITool>()).CreateSession()
);
```

### 3.2 创建或重开会话

第一次运行时创建；以后直接重开同一个目录：

```csharp
using Atelia.ChatSession;

string sessionDir = Path.GetFullPath(".atelia/chat-sessions/demo");
Directory.CreateDirectory(sessionDir);

ChatSessionEngine engine = Directory.EnumerateFileSystemEntries(sessionDir).Any()
    ? await ChatSessionEngine.OpenAsync(sessionDir, runtime)
    : await ChatSessionEngine.CreateAsync(
        sessionDir,
        new ChatSessionCreateOptions(
            ModelId: "Qwen3.5-27b-GPTQ-Int4",
            SystemPrompt: "You are a helpful assistant.",
            CompletionSurfaceId: "openai-chat/strict"
        ),
        runtime
    );

using (engine) {
    var turn = await engine.SendMessageAsync("用一句话介绍自己。", CancellationToken.None);

    Console.WriteLine(turn.Message.GetFlattenedText());
    Console.WriteLine($"tool calls executed: {turn.ToolCallsExecuted}");
    Console.WriteLine($"history count: {engine.Context.Count}");
}
```

### 3.3 这一步到底帮你做了什么

`SendMessageAsync(...)` 内部已经自动完成：

1. 把你的输入追加为 `ObservationMessage`
2. 发起一次 `CompletionRequest`
3. 持久化 assistant 的 `ActionMessage`
4. 若 assistant 发出 tool calls，则执行工具并追加 `ToolResultsMessage`
5. 继续循环，直到 assistant 不再调用工具或达到硬上限

返回值 `ChatSessionTurnResult` 里的 `Message` 是**最后一条 assistant 消息**，不是整段历史。

整段历史请看 `engine.Context`。

---

## 4. 加上工具调用

`ChatSession` 的一个关键价值是：你只要把工具注册进 runtime，`SendMessageAsync(...)` 就会自动跑完整个 tool loop。

最自然的做法是配合 `Atelia.Completion.Tools.MethodToolWrapper`：

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

public sealed class DemoTools {
    [Tool("workspace.echo", "Echo text with session scope.")]
    public ValueTask<ToolExecuteResult> EchoAsync(
        EchoInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken
    ) {
        _ = cancellationToken;

        var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
            ? value as string
            : null;

        return ValueTask.FromResult(
            ToolExecuteResult.FromText(
                ToolExecutionStatus.Success,
                $"echo:{input.Text}|{scope}"
            )
        );
    }
}

[Description("Input for workspace.echo.")]
public sealed record class EchoInput(
    [property: Description("Text to echo back.")]
    [property: JsonPropertyName("text")]
    string Text
);

var toolHost = new DemoTools();
var echoTool = MethodToolWrapper.FromMethod(
    toolHost,
    typeof(DemoTools).GetMethod(nameof(DemoTools.EchoAsync))!
);

var runtime = new ChatSessionRuntime(
    CompletionClient: completionClient,
    CompletionSurfaceId: "openai-chat/strict",
    ToolSession: new ToolRegistry([echoTool]).CreateSession(
        items: new Dictionary<string, object?> { ["scope"] = "demo" }
    )
);

using var engine = await ChatSessionEngine.CreateAsync(
    sessionDir,
    new ChatSessionCreateOptions(
        "Qwen3.5-27b-GPTQ-Int4",
        "You are a coding assistant.",
        "openai-chat/strict"
    ),
    runtime
);

var turn = await engine.SendMessageAsync("调用 workspace.echo，把 hello 传进去。", CancellationToken.None);

Console.WriteLine(turn.Message.GetFlattenedText());
Console.WriteLine(turn.ToolCallsExecuted);
```

关键点：

- 你不需要手写 `RawToolCall -> ToolResult -> ToolResultsMessage` 的桥接
- 也不需要自己维护中间状态
- `ToolCallsExecuted` 会告诉你这次 turn 实际执行了多少次工具

---

## 5. 当前会话里能读到什么

### 5.1 `Context`

`engine.Context` 会把持久化消息投影回 `IHistoryMessage`：

- `ObservationMessage`
- `ActionMessage`
- `ToolResultsMessage`
- `RecapMessage`（compaction 后可能出现）

它是“下一轮 prompt 实际会看到什么”的近似真相源。

### 5.2 `GetStatistics()`

```csharp
var stats = engine.GetStatistics();
Console.WriteLine(stats.MessageCount);
Console.WriteLine(stats.EstimatedTokens);
```

这适合做：

- 是否该 compaction 的粗略判断
- 调试 history 长度
- 后台监控

`EstimatedTokens` 是启发式估算，不是 provider 精确 token 计数。

### 5.3 离线读取与 Markdown 导出

维护工具可以不创建 `ChatSessionEngine`，直接读取当前 branch HEAD 的 durable messages：

```csharp
var records = ChatSessionHistoryReader.ReadCurrent(sessionDir);
var markdown = ChatSessionMarkdownExporter.Export(records);
```

`ChatSessionHistoryRecord` 会保留 durable record index、原始 kind、timestamp、provider-facing `IHistoryMessage` 投影，以及 recap 的 `RecapSourceAnchor`。导出器支持 include / skip recap；旧 recap 没有 anchor 时会标记为 `unresolved-recap`，不会假装已经展开。

---

## 6. 重开、重来、压缩历史

### 6.1 重开会话

只要保留同一个目录，就可以重开：

```csharp
using var reopened = await ChatSessionEngine.OpenAsync(sessionDir, runtime);
Console.WriteLine(reopened.Context.Count);
```

注意：重开时真正生效的 `ModelId` / `SystemPrompt` 来自**已持久化会话**，不是 runtime。runtime 主要提供 client、surface 和 tools。

### 6.2 取出最近一轮，重跑一次

```csharp
if (engine.TryRemoveLatestCompletedTurn(out var removed)) {
    Console.WriteLine($"removed user message: {removed!.UserMessage}");
    Console.WriteLine($"removed records: {removed.RemovedMessageCount}");
}
```

这适合做“撤回上一轮并重试”。

如果最近一轮没有形成完整 assistant 输出，则不会删除。

### 6.3 Compaction

当历史太长时，可以把旧前缀压缩成 recap：

```csharp
var compaction = await engine.CompactAsync(
    summarizeSystemPrompt: "You compress old conversation history into a concise recap.",
    summarizePrompt: "请总结旧对话中后续继续聊天仍必须知道的信息。",
    ct: CancellationToken.None
);

Console.WriteLine(compaction.Applied);
Console.WriteLine(compaction.FailureReason);
Console.WriteLine(compaction.TokensBefore);
Console.WriteLine(compaction.TokensAfter);
```

`CompactAsync(...)` 会：

- 找一个合法 split point
- 用同一个 completion client 生成 summary
- 把旧前缀替换成一条 `RecapMessage`

当前它不会自动触发。触发时机由宿主自己决定。

---

## 7. 容易踩的坑

1. `CompletionSurfaceId` 不一致：`CreateAsync(...)` 的 options、`ChatSessionRuntime`、以及后续 `OpenAsync(...)` 的 runtime 必须对齐，否则会直接抛错。
2. 想在重开会话时偷偷换 `ModelId` / `SystemPrompt`：不行。它们已经持久化在 session repo 里。要换就新建会话目录，或者显式走新 branch。
3. 把 Gemini 和工具一起用：当前 v1 明确不支持 “Gemini + visible tools” 的 tool loop。
4. 忘记释放 engine：`ChatSessionEngine` 持有底层 repository，使用完要 `Dispose()`。
5. 共用脏目录：`ChatSession` 目录本质上是一个 `StateJournal` repository，应该给它单独的目录，不要和别的 durable graph 混用。
6. 期待无限工具递归：当前硬上限是 128 轮 tool loop；超过会抛 `InvalidOperationException`。

---

## 8. 什么时候该用，什么时候不该用

适合：

- 需要把聊天历史落到磁盘
- 需要自动 tool loop
- 需要在进程重启后继续同一会话
- 需要“撤回最近一轮重跑”或“摘要压缩旧历史”

不适合：

- 你只想临时发一次 `CompletionRequest`
- 你不需要持久化 history
- 你已经有自己的 durable conversation store，而且不想用 `StateJournal`

---

## 9. 可运行样例

本 README 里的最小路径已经落实成样例测试，后续 API 演化时可以帮忙发现文档漂移：

- [../../tests/FamilyChat.Server.Tests/ChatSessionQuickStartSamplesTests.cs](../../tests/FamilyChat.Server.Tests/ChatSessionQuickStartSamplesTests.cs)
- [../../tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs](../../tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs)

建议先跑这条：

```bash
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~ChatSession"
```

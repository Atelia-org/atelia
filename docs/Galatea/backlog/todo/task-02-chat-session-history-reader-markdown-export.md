# Task 02: ChatSession History Reader and Markdown Export

> 状态：Todo
> 建议执行者：适合做一个小型可复用库 + 可选 CLI 的实现会话
> 依赖：可先不依赖 Task 01；但 Task 01 完成后可支持 recap anchor 元数据。

## 背景

维护工具需要从 ChatSession repo 导出对话历史，典型目标格式是 Markdown。当前 `ChatSessionEngine.Context` 可以取到 provider-facing 的消息投影，但它不是专门为离线导出设计的：缺少 durable record index、timestamp、recap anchor、原始 kind 等元数据。

这个任务先抽象可复用库，上层再按具体需求做 CLI 或 Galatea 管理工具。

## 关键文件

- [`prototypes/ChatSession/ChatSessionEngine.State.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.State.cs)
- [`prototypes/ChatSession/MessageRecord.cs`](../../../../prototypes/ChatSession/MessageRecord.cs)
- [`prototypes/Completion.Abstractions/IHistoryMessage.cs`](../../../../prototypes/Completion.Abstractions/IHistoryMessage.cs)
- [`prototypes/Completion.Abstractions/ActionMessageSerialization.cs`](../../../../prototypes/Completion.Abstractions/ActionMessageSerialization.cs)
- [`docs/StateJournal/usage-guide.md`](../../../StateJournal/usage-guide.md)
- [`prototypes/ChatSession/README.md`](../../../../prototypes/ChatSession/README.md)

## 目标

新增 ChatSession 层的只读历史枚举 API，并提供 Markdown 导出器。

建议 API 形态可按实际代码调整：

```csharp
public sealed record ChatSessionHistoryRecord(
    int Index,
    string Kind,
    DateTimeOffset? TimestampUtc,
    IHistoryMessage Message,
    ChatSessionRecapSourceAnchor? RecapSource
);

public static class ChatSessionHistoryReader {
    public static IReadOnlyList<ChatSessionHistoryRecord> ReadCurrent(string repoDir, string branchName = "main");
}
```

Markdown 导出器建议支持：

- include recap：把 recap 当普通消息导出。
- skip recap：跳过 recap，仅导出当前 HEAD 中仍可达的普通消息。
- unresolved recap 标记：旧 recap 无 anchor 时明确标注。
- fence 策略：MVP 可固定六级 fence `~~~~~~`；后续可扫描正文中最长 fence 后自动加一级。

## Markdown 格式建议

每条消息使用顶层 code fence 作为信封，元数据放 fence 外，例如：

````markdown
## 00042 action

- kind: action
- timestampUtc: 2026-07-19T00:00:00.0000000Z

~~~~~~text
assistant visible text here
~~~~~~
````

ActionMessage 有多个 block 时，可以先导出可读文本，再在 metadata 或附加 fence 中保留 tool call JSON。MVP 不必完美复原 provider-native 结构，但不能静默丢失 tool call。

## 非目标

- 不要求本任务展开 recap 对应的旧历史范围。
- 不要求做 StateJournal commit 历史扫描。
- 不要求把导出工具接入 Galatea Web UI。
- 不要求修改 ChatSession 持久化 schema，除非只是读取 Task 01 新增字段。

## 验收

- 能打开一个 ChatSession repo，读取当前 `main` HEAD 的 messages。
- 能导出 observation/action/tool-results/recap/context-header。
- Markdown 中每条消息有稳定 index 和 kind。
- Tool call 不被完全丢弃：至少导出 tool name、tool call id、raw arguments。
- 对旧无 anchor recap 输出明确提示，而不是误称已展开。
- 有覆盖普通 turn、tool loop、compaction 后 recap 的测试。

## 风险点

- `MessageRecord` 当前是 `internal`。如果 reader 放在 `ChatSession` 项目内最简单；如果放在外部项目，会被迫复制 schema 常量，容易漂移。
- `ChatSessionEngine.OpenAsync(...)` 需要 runtime 依赖，而离线 reader 不应该要求真实 completion client；建议直接用 `Repository.Open` + `CheckoutBranch` + root schema 读取。
- `Repository.Open` 会拿 repo lock；导出运行时不能和 Galatea 服务同时打开同一个 repo。可对备份副本导出。

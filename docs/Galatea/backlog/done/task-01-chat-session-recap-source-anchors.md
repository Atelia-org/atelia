# Task 01: ChatSession Recap Source Anchors

> 状态：Done
> 建议执行者：熟悉 `prototypes/ChatSession` 与 StateJournal 基本用法的实现会话
> 优先级：高。它能让未来新数据天然可追溯。
> 结果摘要：[`../done/task-01-chat-session-recap-source-anchors-handoff.md`](../done/task-01-chat-session-recap-source-anchors-handoff.md)

## 背景

Galatea 通过 `Atelia.ChatSession` 保存对话历史。当前 `ChatSessionEngine.CompactAsync(...)` 会把旧消息前缀压缩成一条 `RecapMessage`，但 recap record 只保存摘要文本，没有保存它替代了原始消息序列中的哪个范围。

这导致后续离线导出、摘要质量审计、记忆重吸收、旧上下文回看都无法从当前 HEAD 精确回到 recap 对应的原始消息片段。

## 关键文件

- [`prototypes/ChatSession/ChatSessionEngine.Compaction.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.Compaction.cs)
- [`prototypes/ChatSession/MessageRecord.cs`](../../../../prototypes/ChatSession/MessageRecord.cs)
- [`prototypes/ChatSession/ChatSessionEngine.State.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.State.cs)
- [`prototypes/ChatSession/ChatSessionContracts.cs`](../../../../prototypes/ChatSession/ChatSessionContracts.cs)
- [`tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs`](../../../../tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs)
- [`docs/Galatea/backlog/todo/feature-request-recap-source-range-anchors.md`](feature-request-recap-source-range-anchors.md)

## 目标

在 compaction 生成 recap 时，把 recap 与原始消息范围的映射关系写入 recap record。

建议字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `sourceHeadBeforeCompaction` | string | compaction 开始前 branch HEAD 的 `CommitAddress.ToString()` |
| `sourceBranchName` | string | 当前 ChatSession branch，通常为 `main` |
| `sourceStartIndex` | int | 被替换范围起点，当前算法为 `0` |
| `sourceEndExclusive` | int | 被替换范围终点，通常等于 `splitIndex` |
| `sourceMessageCountBefore` | int | compaction 前完整消息数 |
| `compactionKind` | string | MVP 可固定为 `prefix-summary` |

如果现有 `timestampUtc` 已足够表达记录创建时间，不必额外加 `createdAtUtc`。

## 实施建议

1. 在 `MessageRecord` 中新增 recap source anchor 的 key 常量和轻量 DTO / record。
2. 扩展 `PrependRecap(...)`，允许传入可空 anchor；旧调用可显式传 null 或增加重载。
3. 在 `ExecuteCompactionCoreAsync(...)` 删除 prefix 之前捕获 `PersistedHeadAddress`、`_branchName`、`splitIndex`、`historyCountBefore`。
4. 生成 recap 时写入 anchor 字段。
5. 在读取 recap 时保留兼容：没有 anchor 的旧 recap 仍能读成普通 `RecapMessage`。
6. 如果需要公开 anchor 信息，优先新增 `RecapMessage` 的属性或新增派生/伴随 DTO；不要让上层重新解析 durable record。

## 非目标

- 不要求本任务实现递归展开 recap。
- 不要求修复旧会话中的无 anchor recap。
- 不要求新增 StateJournal commit 遍历 API。
- 不要求改变 compaction 的切分策略。

## 验收

- 新 compaction 后，当前 `engine.Context` 仍按原有语义返回 `RecapMessage + recent messages`。
- 单元测试能验证 recap source anchor 指向 compaction 前 HEAD。
- 单元测试能验证 source range 是 `[0, splitIndex)`，且 `sourceMessageCountBefore` 等于 compaction 前消息数。
- 旧 recap record 没有 anchor 时不会读取失败。
- `dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~ChatSession_CompactAsync"` 通过。

## 风险点

- `PersistedHeadAddress` 在尚未首次 commit 的 branch 上可能为 null；ChatSession 创建时已有初始 root commit，正常 compaction 应该不为空，但代码仍应防御。
- `ContextHeader` 在 compaction 后会被保留并重新 prepend；source range 对应的是 compaction 前完整 messages 的 prefix，不能用 compaction 后索引解释。
- `RecapMessage` 当前继承 `ObservationMessage`，如果要加 anchor 信息，注意不要破坏 provider-facing projection。

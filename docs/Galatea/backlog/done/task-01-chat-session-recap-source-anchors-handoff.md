# Task 01 Handoff: ChatSession Recap Source Anchors

> 完成日期：2026-07-19
> 对应任务：`docs/Galatea/backlog/todo/task-01-chat-session-recap-source-anchors.md`

## 实施结果

`Atelia.ChatSession` 的 compaction 现在会在新生成的 `RecapMessage` 上记录 source anchor，用于描述这条 recap 替代了 compaction 前消息序列中的哪个范围。

新增公开契约：

- `RecapSourceAnchor`
- `RecapMessage.SourceAnchor`

anchor 字段：

| 字段 | 含义 |
|---|---|
| `SourceHeadBeforeCompaction` | compaction 删除旧 prefix 前的 branch HEAD，来自 `CommitAddress.ToString()` |
| `SourceBranchName` | 当前 ChatSession branch 名，通常为 `main` |
| `SourceStartIndex` | 被替换范围起点，当前固定为 `0` |
| `SourceEndExclusive` | 被替换范围终点，等于本次 `CompactionResult.SplitIndex` |
| `SourceMessageCountBefore` | compaction 前完整 `engine.Context.Count` |
| `CompactionKind` | 当前固定为 `prefix-summary` |

## 关键实现位置

- `prototypes/ChatSession/ChatSessionContracts.cs`
  - 新增 `RecapSourceAnchor`。
  - `RecapMessage` 增加可空 `SourceAnchor`，默认 `null`，保持旧调用兼容。
- `prototypes/ChatSession/MessageRecord.cs`
  - 新增 durable record key 常量。
  - `PrependRecap(...)` 支持可空 anchor。
  - `BuildRecap(...)` 会读取 anchor；旧 record 缺少 anchor 字段时返回 `SourceAnchor == null`。
- `prototypes/ChatSession/ChatSessionEngine.Compaction.cs`
  - 在 compaction core 中捕获删除 prefix 前的 `PersistedHeadAddress`。
  - summary 成功后、删除 prefix 并 prepend recap 时写入 anchor。

## 语义约定

source range 的索引基于 compaction 前完整 `engine.Context` 序列，而不是 compaction 后的序列。

这意味着：

- 如果 compaction 前有 `ContextHeader`，它也属于索引空间。
- compaction 后 `ContextHeader` 会被重新 prepend 保留，因此 recap 不一定在 index `0`。
- `SourceStartIndex == 0` 和 `SourceEndExclusive == splitIndex` 描述的是被 recap 替换的旧 prefix。

## 测试覆盖

新增测试位于 `tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs`：

- `ChatSession_CompactAsync_WritesRecapSourceAnchor`
  - 验证 recap anchor 指向 compaction 前 HEAD。
  - 验证 source range 是 `[0, splitIndex)`。
  - 验证 `SourceMessageCountBefore` 等于 compaction 前消息数。
- `ChatSession_ReadRecapWithoutSourceAnchor_KeepsLegacyRecordCompatible`
  - 验证旧 recap record 没有 anchor 字段时仍能读取，且 `SourceAnchor == null`。

已运行验证：

```bash
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~SourceAnchor"
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~ChatSession_CompactAsync"
```

## 后续任务提示

关联任务如 Markdown export、history reader、legacy recap recovery 可以直接通过 `RecapMessage.SourceAnchor` 获取 source range，不需要重新解析 durable record。

若需要展开 recap，可从 `SourceHeadBeforeCompaction` 解析 `CommitAddress`，再用 StateJournal 的历史 commit 派生/checkout 能力读取旧 `messages[SourceStartIndex..SourceEndExclusive)`。当前任务没有实现展开逻辑，也没有迁移旧 recap。

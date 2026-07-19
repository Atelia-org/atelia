# Task 06: ChatSession Explicit Commit Kind Metadata

> 状态：Todo / Ready
> 建议执行者：熟悉 `prototypes/ChatSession` 写入路径与 StateJournal commit metadata 的实现会话
> 依赖：Task 04b / 04c 的 legacy attribution 结果

## 背景

Task 04b / 04c 已经能从旧 ChatSession repo 的历史 messages 与 root 状态中推断 commit attribution：`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save`。这为升级旧数据提供了关键支撑，但它仍然是事后推断。

新写入的数据不应继续依赖推断。ChatSession 应在每次提交时显式记录 commit purpose / commit kind metadata，使未来的 history export、sidecar、升级工具可以直接读取语义。

## 目标

为 `prototypes/ChatSession` 的提交路径增加显式 commit kind metadata：

- `model-turn`：`SendMessageAsync` 成功持久化一轮 observation/action/tool-results。
- `compaction`：`CompactAsync` 成功写入 recap 与 suffix。
- `revert-turn`：`TryRemoveLatestCompletedTurn` 成功移除最近完整 turn。
- `update-system-prompt`：`TrySyncSystemPrompt` 更新 root `systemPrompt`。
- `update-context-header`：`SetContextHeader` 改写 context header。
- `redundant-save`：保留给确实需要提交但业务状态未变化的防御性 commit；默认不应主动制造。

建议优先在 ChatSession 层封装提交入口，例如：

```csharp
private void Commit(ChatSessionCommitKind kind, string reason)
```

再由这个入口调用 StateJournal commit，并把 kind/reason 写入 commit metadata。若 StateJournal 当前 metadata API 不足，应先补最小必要能力，而不是把 kind 写入 messages。

## 建议字段

| 字段 | 类型 | 含义 |
|---|---|---|
| `commitKind` | string | kebab-case commit kind，例如 `model-turn` |
| `commitReason` | string? | 简短人类可读原因，可用于调试 |
| `chatSessionSchemaVersion` | number? | 可选，便于未来演进 |

## 非目标

- 不迁移旧 repo；旧数据仍由 Task 04c sidecar attribution 支撑。
- 不把 commit kind 写进每条 message record。
- 不要求一次性设计跨所有 StateJournal 消费者的通用 taxonomy；先满足 ChatSession。

## 验收

- 新创建并操作的 ChatSession repo，其 commit history 可直接读出 commit kind。
- `SendMessageAsync` 产生 `model-turn`。
- `CompactAsync` applied 时产生 `compaction`；未 applied 时不应产生新 commit。
- `TryRemoveLatestCompletedTurn` 成功时产生 `revert-turn`。
- `TrySyncSystemPrompt` 成功时产生 `update-system-prompt`；无变化时不产生新 commit。
- `SetContextHeader` 产生 `update-context-header`。
- 新 metadata 与 Task 04c sidecar attribution 的命名保持一致。
- 有测试覆盖 metadata 写入和读取；必要时同步更新 history/recovery reader 优先使用显式 metadata。

## 后续衔接

- Task 04c 的 legacy attribution 可作为旧数据 fallback。
- 新数据导出时应优先使用显式 commit metadata；只有 metadata 缺失时才运行 messages/root diff 推断。
- 未来可将 `update-system-prompt` / `update-context-header` 纳入 UI 或审计视图，解释为什么有些 commit 不改变 messages。

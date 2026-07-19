# Task 06: ChatSession Explicit Commit Kind Metadata

> 状态：Done
> 建议执行者：熟悉 `prototypes/ChatSession` 写入路径与 StateJournal commit metadata 的实现会话
> 依赖：Task 04b / 04c 的 legacy attribution 结果

## 实施结果

- 采用 `Repository.Commit(graphRoot, note)` 的 branch reflog `Note` 作为第一版 metadata 承载；不扩展 commit TailMeta v2 持久格式。
- ChatSession 新增 `ChatSessionCommitKind` 与 `ChatSessionCommitMetadata`，note 使用单行 JSON：`schema=atelia.chat-session.commit-note.v1`、`commitKind`、`commitReason`、`chatSessionSchemaVersion`。
- 新写入路径已记录 explicit kind：`initial-state`、`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`update-context-header`；`redundant-save` 已纳入编码/解析但当前默认不主动制造该提交。
- `RepositoryHistoryReader` 现在会从 branch reflog newHead 读取 note 并附到 effective commit address；`ChatSessionLegacyRecapRecovery` 优先解析 explicit metadata，缺失或解析失败时仍 fallback 到原 legacy attribution。
- `ChatSessionRecoverySidecarExporter` 已支持输出 `update-context-header`，并继续使用 recovery timeline 中的 attribution。
- 测试覆盖 explicit metadata timeline、未 applied compaction 不新增 commit、无变化 `TrySyncSystemPrompt` 不新增 commit，以及全部 commit kind 的 kebab-case roundtrip。

## 背景

Task 04b / 04c 已经能从旧 ChatSession repo 的历史 messages 与 root 状态中推断 commit attribution：`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save`。这为升级旧数据提供了关键支撑，但它仍然是事后推断。

新写入的数据不应继续依赖推断。ChatSession 应在每次提交时显式记录 commit purpose / commit kind metadata，使未来的 history export、sidecar、升级工具可以直接读取语义。

Task 04c 在 `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy` 上实跑后，已经把 77 个 effective commits 全部归因：

```text
initial-state: 1
model-turn: 71
compaction: 2
update-system-prompt: 3
```

其中 `update-system-prompt` 是通过“message signature 序列完全相同，但 root `systemPrompt` 发生变化”推断出的。这说明 commit kind 不是附属信息，而是后续升级旧数据、解释历史和调试 UI 行为的核心语义。

## 当前实现上下文

关键文件：

| 文件 | 作用 |
|---|---|
| `prototypes/ChatSession/ChatSessionEngine.State.cs` | `CreateAsync`、`SetContextHeader`、`TrySyncSystemPrompt`、`Commit()` 当前在这里 |
| `prototypes/ChatSession/ChatSessionEngine.cs` | `SendMessageAsync` 成功后持久化 turn 并 commit |
| `prototypes/ChatSession/ChatSessionEngine.Compaction.cs` | `CompactAsync` 成功 applied 后 commit |
| `prototypes/ChatSession/ChatSessionEngine.Rewind.cs` | `TryRemoveLatestCompletedTurn` 成功后 commit |
| `prototypes/ChatSession/ChatSessionLegacyRecapRecovery.cs` | Task 04b/04c legacy attribution 推断逻辑 |
| `prototypes/ChatSession/ChatSessionRecoverySidecarExporter.cs` | Task 04c JSON sidecar 输出 |
| `src/StateJournal/Repository.cs` | `Commit(DurableObject, string? note)` 当前公开入口 |
| `src/StateJournal/Repository.BranchRefs.cs` | branch ref / reflog 当前会保存 `LastNote` / `Note` |
| `src/StateJournal/RepositoryHistoryReader.cs` | 历史 commit address 枚举；若要读取 reflog note 可能需要扩展 |

已知现状：

- `Repository.Commit(graphRoot, note)` 已存在，`note` 会写入 branch ref 的 `LastNote` 和 branch reflog entry 的 `Note`。
- 当前 commit TailMeta v2 只保存 graph root、symbol table、parent address；不要为了本任务贸然扩展 tail meta 持久格式，除非确认这是必要且最小的改动。
- Task 06 的第一版可以把 ChatSession commit kind/reason 编码到 StateJournal `note`，并补一个读取/解析 helper；如果实现者认为 `note` 语义不足，应先在文档/报告中说明，再做最小 StateJournal API 扩展。
- 不要把 commit kind 写入 `MessageRecord`；commit kind 描述的是提交原因，不属于单条 message schema。

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

建议增加一个 ChatSession 层的轻量类型，例如：

```csharp
public enum ChatSessionCommitKind {
	InitialState,
	ModelTurn,
	Compaction,
	RevertTurn,
	UpdateSystemPrompt,
	UpdateContextHeader,
	RedundantSave,
}
```

注意：sidecar JSON 使用 kebab-case 字符串：`initial-state`、`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`update-context-header`、`redundant-save`。命名应与 Task 04c attribution 保持一致。

## 建议字段

| 字段 | 类型 | 含义 |
|---|---|---|
| `commitKind` | string | kebab-case commit kind，例如 `model-turn` |
| `commitReason` | string? | 简短人类可读原因，可用于调试 |
| `chatSessionSchemaVersion` | number? | 可选，便于未来演进 |

如果第一版复用 `Repository.Commit(..., note)`，建议 note 使用稳定、可解析、不会和普通人类 note 混淆的格式，例如 JSON 单行：

```json
{"schema":"atelia.chat-session.commit-note.v1","commitKind":"model-turn","commitReason":"persisted one user/assistant turn"}
```

实现者可以选择更合适的编码，但必须满足：

- 可从 reflog note 中稳定解析 `commitKind`。
- 缺失或解析失败时不破坏旧 repo 读取；reader 应 fallback 到 legacy attribution。
- 不输出绝对路径、时间戳等会导致无意义 diff 的信息。

## 建议实施步骤

1. 调查 StateJournal 当前 `note` 的写入/读取边界，决定是否复用 note 作为第一版 metadata 承载。
2. 在 ChatSession 层增加 commit kind 类型、note 编码/解析 helper、统一提交入口。
3. 替换当前写入路径：
	- `CreateAsync` 初始提交 → `initial-state`。
	- `SendMessageAsync` → `model-turn`。
	- `CompactAsync` applied → `compaction`。
	- `TryRemoveLatestCompletedTurn` → `revert-turn`。
	- `TrySyncSystemPrompt` → `update-system-prompt`。
	- `SetContextHeader` → `update-context-header`。
4. 扩展读取路径：新数据优先读取显式 metadata；旧数据缺失 metadata 时继续使用 Task 04b/04c 的 legacy attribution 推断。
5. 更新测试，确保每个写入路径的 commit kind 可被读出。

## 非目标

- 不迁移旧 repo；旧数据仍由 Task 04c sidecar attribution 支撑。
- 不把 commit kind 写进每条 message record。
- 不要求一次性设计跨所有 StateJournal 消费者的通用 taxonomy；先满足 ChatSession。

## 验收

- 新创建并操作的 ChatSession repo，其 commit history 可直接读出 commit kind。
- 若 commit note/metadata 缺失，旧 repo 仍可通过 legacy attribution 推断；不得破坏 Task 04b/04c 对旧数据的分析。
- `SendMessageAsync` 产生 `model-turn`。
- `CompactAsync` applied 时产生 `compaction`；未 applied 时不应产生新 commit。
- `TryRemoveLatestCompletedTurn` 成功时产生 `revert-turn`。
- `TrySyncSystemPrompt` 成功时产生 `update-system-prompt`；无变化时不产生新 commit。
- `SetContextHeader` 产生 `update-context-header`。
- `CreateAsync` 初始提交产生 `initial-state`。
- 新 metadata 与 Task 04c sidecar attribution 的命名保持一致。
- 有测试覆盖 metadata 写入和读取；必要时同步更新 history/recovery reader 优先使用显式 metadata。

## 测试建议

优先扩展 `tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs` 中已有 ChatSession recovery/sidecar 测试：

- 创建一个新 ChatSession repo，依次执行 create、send、sync prompt、compact、rewind、set context header，读取 effective timeline，断言显式 commit kind。
- 对没有 explicit metadata 的旧式测试 repo，断言 `ChatSessionLegacyRecapRecovery.Analyze(...)` 仍能 fallback 到 legacy attribution。
- 对 `CompactAsync` 未 applied 的场景，断言没有新增 `compaction` commit。

可运行的 focused validation：

```bash
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~ChatSessionRecoverySidecarExporter|FullyQualifiedName~ChatSessionLegacyRecapRecovery|FullyQualifiedName~ChatSessionHistoryReader|FullyQualifiedName~ChatSessionMarkdownExporter|FullyQualifiedName~ChatSession_CompactAsync|FullyQualifiedName~TrySyncSystemPrompt|FullyQualifiedName~TryRemoveLatestCompletedTurn"
```

完成后运行：

```bash
pwsh ./format.ps1 -Scope diff
git diff --check
```

## 后续衔接

- Task 04c 的 legacy attribution 可作为旧数据 fallback。
- 新数据导出时应优先使用显式 commit metadata；只有 metadata 缺失时才运行 messages/root diff 推断。
- 未来可将 `update-system-prompt` / `update-context-header` 纳入 UI 或审计视图，解释为什么有些 commit 不改变 messages。
- Task 06 完成后，再继续做旧数据升级导出：读取旧 repo + sidecar attribution，生成带 recap source anchor 和显式 commit kind metadata 的新版导出产物。

# ChatSession Legacy Memory Substrate 退役

> 后续收口（2026-07-27）：本文记录删除 legacy memory substrate 当时的边界。
> 随后原 `ChatSession.BacktestCli` 又拆成
> `ChatSession.LegacyExportCli` 与 `SessionJournal.Cli`；legacy pattern/LLM replay
> 已删除。现行状态见
> [CLI 拆分说明](legacy-export-and-sessionjournal-cli-split.md)。

> 状态：Design + Implemented
> 日期：2026-07-27
> 后继实现：
> [SessionMemoryContracts.cs](../../prototypes/SessionJournal/SessionMemoryContracts.cs)
> 与
> [SessionJournal.Maintainers](../../prototypes/SessionJournal.Maintainers/README.md)

## 1. 决策

删除旧 `Atelia.ChatSession` memory-maintainer substrate，让 legacy ChatSession
退回只保留朴素 `CompactAsync(...)` 的归档方向。

旧 substrate 没有进入 FamilyChat / Galatea 正式在线调用，也没有形成持久化
MemoryPack wire。它与 `Atelia.SessionJournal` 中已经投入后续主线的
`SessionMemoryContracts.cs` 同形但类型身份不同；继续保留只会制造两个可扩展真源。

本次不提供 compatibility wrapper，也不让旧 ChatSession 引用新
SessionJournal maintainer contracts。

## 2. 删除范围

- `prototypes/ChatSession/MemorySubstrate.cs`；
- `ChatSessionEngine.RunMemoryMaintainersAsync(...)`；
- `MemoryMaintenanceRequest` / `MemoryMaintenanceResult`；
- 只服务该入口的 history-slice helper 与 action-to-observation split 选项；
- FamilyChat.Tests 中针对旧 substrate / maintainer 入口的测试。

legacy `replay-pattern-count` 只需要保存上一 epoch 的 block 文本，因此改用普通
`string` 状态，不借用旧或新的 `MemoryPack` 类型。JSONL 中稳定的
maintainer id、target carrier 和 target block id 保持不变。

## 3. 保留范围

以下能力属于 legacy ChatSession 的正常 history / compaction 合同，不属于被删除的
maintainer substrate：

- `ContextHeader` 与 `SetContextHeader(...)`；
- `RecapMessage` / `RecapSourceAnchor`；
- `CompactionResult` / `CompactionFailureReason`；
- `ChatSessionEngine.CompactAsync(...)`；
- observation-to-action `HistoryWindowSplitPolicy`。

因此本次不是 ChatSession journal wire migration，不修改 MessageRecord key、commit
metadata、legacy recovery/export 或已有 recap persistence。

## 4. 新旧边界

```text
legacy ChatSession
  -> ContextHeader + recap + plain CompactAsync

SessionJournal
  -> memory contracts + replay/provenance + derived artifacts

SessionJournal.Maintainers
  -> concrete maintainer profiles/prompts/targets
```

依赖方向保持单向：SessionJournal raw core 不引用 concrete maintainers；legacy
ChatSession 也不引用 SessionJournal memory contracts。BacktestCli 只在 composition
root 同时读取 legacy export 与驱动新 SessionJournal replay。

## 5. 验收

- 仓库 production code 不再定义或调用旧 `Atelia.ChatSession.MemoryPack` /
  `IMemoryBlockMaintainer` / `RunMemoryMaintainersAsync(...)`；
- legacy `CompactAsync(...)` 测试继续通过；
- legacy pattern-count 输出 target identity 不变；
- 被删除旧测试中的仍有效 invariants 已迁入 `SessionJournal.Tests`；
- FamilyChat、SessionJournal、SessionJournal.Maintainers、BacktestCli 测试及 solution
  build 通过。

# CS-5-lite-B0 设计：SessionJournal Memory Substrate 上移

> 状态：Design + Implementation Plan
> 日期：2026-07-25
> 对应 brief：[CS-5-lite-B0: SessionJournal Memory Substrate 上移](cs-5-lite-B0-sessionjournal-memory-substrate.md)
>
> **后续改名（2026-07-27）**：本文保留 B0 当时的项目名、命名空间和非目标；
> concrete companion assembly 现为 `prototypes/SessionJournal.Maintainers`
> / `Atelia.SessionJournal.Maintainers`。

## 1. 结论

B0 第一版直接在 `prototypes/SessionJournal` 内建立新的 memory substrate 权威类型，命名空间使用
`Atelia.SessionJournal`。旧 `prototypes/ChatSession/MemorySubstrate.cs` 保留不动，短期允许新旧两套同形
不同身份的类型并存。

本分片不做兼容层，也不迁移 legacy `ChatSessionEngine` compaction 主链。它只为 B DerivedRecapStore、C runner
input abstraction、D artifact writing 准备 SessionJournal-owned API。

推荐新增文件：

```text
prototypes/SessionJournal/
  SessionMemoryContracts.cs
```

后续如果文件变大，可再拆 `SessionMemoryMaintainer.cs`；第一版单文件便于审阅复制来源和差异。

## 2. 上移类型

从旧 `Atelia.ChatSession` 复制并改为 `Atelia.SessionJournal` 的类型：

- `ContextHeaderSnapshot`
- `RecentHistorySlice`
- `IRecentHistoryAnalyzer`
- `RecentHistoryAnalysisContext`
- `MemoryPackCarrier`
- `MemoryPackCarrierTokens`
- `MemoryPackBlock`
- `MemoryPackBlockPath`
- `MemoryPack`
- `RenderedMemoryPack`
- `MemoryPackDraft`
- `IMemoryBlockMaintainer`
- `MemoryBlockMaintenanceRequest`
- `MemoryBlockMaintenanceResult`
- `MemoryRewriteProfile`
- `RewriteMemoryBlockMaintainer`
- `MemoryMaintenanceBatchResult`
- `MemoryMaintenanceOrchestrator`

新增 SessionJournal-owned header message：

```csharp
public sealed record SessionContextHeader(
    string? SystemPromptFragment,
    string? ObservationMessage,
    ActionMessage? ActionMessage
) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.ContextHeader;
}
```

`SessionContextHeader` 与旧 `Atelia.ChatSession.ContextHeader` 字段形状相同，但类型身份独立，后续可分别演化。

## 3. 与旧 ChatSession 的差异

- `ContextHeaderSnapshot.FromContextHeader(...)` 改为 `FromSessionContextHeader(...)`。
- `RenderedMemoryPack.ToContextHeader()` 改为 `ToSessionContextHeader()`。
- `RewriteMemoryBlockMaintainer` 对 `HistoryMessageKind.ContextHeader` 只识别新 `SessionContextHeader`；如果调用方把旧
  `ContextHeader` 传入新 maintainer，应先在边界展开或转换。
- `RewriteMemoryBlockMaintainer` completion 失败时抛 `SessionJournalTurnAbortedException`，不再抛旧
  `ChatSessionTurnAbortedException`。

第一版继续保留 `IHistoryMessage` / `ActionMessage` / `ObservationMessage` 等 Completion abstraction 类型作为共同
消息基底；这不是旧 ChatSession 依赖。

## 4. Concrete profiles 调整

`prototypes/ChatSession.Memory` 暂不改名、不移动 prompt resources，但它的具体 profile 改为引用新的
`Atelia.SessionJournal` memory substrate：

```text
prototypes/ChatSession.Memory
  -> prototypes/SessionJournal
```

保留命名空间 `Atelia.ChatSession.Memory`，因为项目目录和程序集本分片不重命名。它成为“具体 maintainer/profile
实现项目”，而不是旧 ChatSession substrate 的扩展项目。

受影响类型：

- `AutobiographicalRewriteProfiles`
- `WorldUnderstandingRewriteProfiles`
- `RolePlayMemoryBlockPaths`
- `EmbeddedMemoryRewriteProfileLoader`

## 5. BacktestCli 最小调整

`prototypes/ChatSession.BacktestCli` 目前同时有两个 replay：

- `replay-pattern-count`：legacy pattern analyzer，继续使用旧 `Atelia.ChatSession.MemoryPack`。
- `replay-rolling-summary`：LLM rewrite maintainer，应切到新 `Atelia.SessionJournal` substrate，因为它会承接 C/D。

因此只在 rolling summary 路径使用 namespace alias 或精确 using，避免 `MemoryPack` 等同名类型歧义。
`ChatSessionLegacyEventSourceProjection` 仍可产出 `IHistoryMessage`，不需要迁移。

## 6. 测试策略

新增 `tests/SessionJournal.Tests/SessionMemorySubstrateTests.cs` 覆盖：

- `RenderedMemoryPack.ToSessionContextHeader()` 不依赖旧 `ContextHeader`。
- `ContextHeaderSnapshot.FromSessionContextHeader(...)` 能读取三段 context。
- `MemoryMaintenanceOrchestrator.RunAsync(...)` 更新 block，并验证 maintainer id/target。
- `RewriteMemoryBlockMaintainer` 在 completion 失败时抛 `SessionJournalTurnAbortedException`。
- `RewriteMemoryBlockMaintainer` 对新 `SessionContextHeader` 展开后再调用 LLM。

更新 `tests/ChatSession.Memory.Tests`，让 concrete profiles 测试使用 `Atelia.SessionJournal` substrate 类型。

## 7. 后续分片

B0 不解决以下工作：

- 旧 `prototypes/ChatSession` 是否改为引用新 substrate。
- `prototypes/ChatSession.Memory` 项目目录/程序集改名。
- `RecentHistorySlice` 是否需要携带 raw address range。
- DerivedRecapStore 的 artifact schema 和 latest index。
- ContextPlanner / ArtifactSet。

这些应分别进入后续迁移或 B/C/D/E 分片。

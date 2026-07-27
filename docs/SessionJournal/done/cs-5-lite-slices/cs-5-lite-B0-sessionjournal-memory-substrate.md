# CS-5-lite-B0: SessionJournal Memory Substrate 上移

> 状态：Task Brief / Design + Implementation
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)
> 前置：[CS-5-lite-A Addressed Replay Cursor](cs-5-lite-A-addressed-replay-cursor.md)
> 后续：[CS-5-lite-B Derived Recap Store](cs-5-lite-B-derived-recap-store.md)
>
> **后续改名（2026-07-27）**：本文保留 B0 当时的项目名与“不在本分片改名”范围；
> 现行项目已改为 `prototypes/SessionJournal.Maintainers`，测试项目已改为
> `tests/SessionJournal.Maintainers.Tests`。
>
> **后续退役（2026-07-27）**：B0 当时短期保留的
> `prototypes/ChatSession/MemorySubstrate.cs` 已删除；本文正文保留当时迁移范围，
> 现行边界见 [ChatSession Legacy Memory Substrate 退役](../legacy-memory-substrate-retirement.md)。

## 1. 目标

在实现 `DerivedRecapStore` 之前，先把 memory / recap / compaction 后续会复用的基础抽象从旧
`prototypes/ChatSession` 的长期主干语义中解耦，放入新的 `prototypes/SessionJournal` 主干。

本分片的目标不是一次性淘汰旧 `ChatSession`，而是建立新的权威 substrate：

```text
SessionJournal raw events
-> addressed replay
-> SessionJournal-owned memory substrate
-> derived recap store
-> rolling maintainer artifact producer
```

这样 B/C/D 后续不需要围绕旧 `Atelia.ChatSession.MemoryPack` 设计正式 API。

## 2. 背景判断

`prototypes/SessionJournal` / `src/EventJournal` 是新的 LLM Session 基础设施方向。旧
`prototypes/ChatSession` 仍可保留简单/legacy 功能，但不应继续成为 memory substrate、recap store、
compaction framework 的长期定义位置。

当前 `prototypes/ChatSession/MemorySubstrate.cs` 中已有一批可复用抽象：

- `ContextHeaderSnapshot`
- `RecentHistorySlice`
- `MemoryPack` / `MemoryPackDraft`
- `MemoryPackCarrier` / `MemoryPackCarrierTokens`
- `MemoryPackBlock` / `MemoryPackBlockPath`
- `RenderedMemoryPack`
- `IMemoryBlockMaintainer`
- `MemoryBlockMaintenanceRequest` / `MemoryBlockMaintenanceResult`
- `MemoryRewriteProfile`
- `RewriteMemoryBlockMaintainer`
- `MemoryMaintenanceOrchestrator`

这些类型大多只依赖 `Completion.Abstractions`，适合上移。少数旧耦合点需要分裂或替换：

- 旧 `ContextHeader` 可在新旧项目中分裂成两个以后独立演化的类型，源码复制后改命名空间即可。
- `RenderedMemoryPack.ToContextHeader()` 不应强制返回旧 `Atelia.ChatSession.ContextHeader`。
- `RewriteMemoryBlockMaintainer` 当前抛 `ChatSessionTurnAbortedException`，上移后应改为
  `SessionJournalTurnAbortedException` 或新的 memory maintainer exception。
- 对 `HistoryMessageKind.ContextHeader` 的处理若依赖旧 `ContextHeader`，应在新主干中定义新
  `SessionContextHeader`，或改为只接受已展开的普通 history message。

## 3. 推荐设计方向

第一版推荐在 `prototypes/SessionJournal` 中新增 memory substrate 文件，例如：

```text
prototypes/SessionJournal/
  SessionMemoryContracts.cs
  SessionMemoryMaintainer.cs
```

命名空间建议保持：

```csharp
namespace Atelia.SessionJournal;
```

不建议第一版单独开 `Atelia.SessionJournal.Memory` 子命名空间，除非实现者发现类型数量明显需要分层。
当前 `SessionProjection`、`SessionHistoryReplay`、`SessionGoverningSetup` 已在同一命名空间；memory
substrate 作为 SessionJournal 主干 API 放在一起更易用。

## 4. 类型迁移策略

### 4.1 可源码复制并改命名空间的类型

以下类型可以从 `prototypes/ChatSession/MemorySubstrate.cs` 复制到 `prototypes/SessionJournal`，改为
`Atelia.SessionJournal` 命名空间，并作为后续新主干权威类型：

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
- `MemoryMaintenanceBatchResult`
- `MemoryMaintenanceOrchestrator`

旧 `prototypes/ChatSession` 可以短期继续保留同名旧类型，作为 legacy surface。后续再决定是引用新类型、
加 adapter、还是逐步删除。

### 4.2 需要分裂/调整的类型

如果新主干需要 context header message，推荐新增：

```csharp
public sealed record SessionContextHeader(
    string? SystemPromptFragment,
    string? ObservationMessage,
    ActionMessage? ActionMessage
) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.ContextHeader;
}
```

它可以与旧 `Atelia.ChatSession.ContextHeader` 字段形状相同，但命名空间和类型身份独立，后续可独立演化。

`ContextHeaderSnapshot` 在新主干中推荐提供：

```csharp
public static ContextHeaderSnapshot FromSessionContextHeader(SessionContextHeader? header);
public static ContextHeaderSnapshot FromRenderedMemoryPack(RenderedMemoryPack rendered);
```

不要依赖旧 `Atelia.ChatSession.ContextHeader`。

`RenderedMemoryPack` 在新主干中推荐提供：

```csharp
public SessionContextHeader ToSessionContextHeader();
```

不要提供返回旧 `ContextHeader` 的方法。

### 4.3 `RewriteMemoryBlockMaintainer`

`RewriteMemoryBlockMaintainer` 是通用 block rewrite executor，建议也上移到 `SessionJournal`，因为 B/C/D
需要复用它作为首批 artifact producer。

调整点：

- 依赖 `ICompletionClient` / `CompletionRequest` / `IHistoryMessage` 仍来自 `Completion.Abstractions`。
- completion 失败时不要抛旧 `ChatSessionTurnAbortedException`。
- 第一版可抛 `SessionJournalTurnAbortedException`，让调用侧沿用 SessionJournal 的 completion failure 语义。
- 若实现者认为 maintainer failure 不应绑定 turn failure，可新增更中性的
  `MemoryBlockMaintenanceAbortedException`；但不要为本分片引入复杂 failure hierarchy。

## 5. 项目引用方向

推荐完成后的依赖方向：

```text
prototypes/SessionJournal
  -> src/EventJournal
  -> prototypes/Completion.Abstractions
  -> prototypes/Completion.Tools

prototypes/ChatSession.Memory
  -> prototypes/SessionJournal

prototypes/ChatSession.BacktestCli
  -> prototypes/ChatSession            # legacy import/export/replay 仍需要
  -> prototypes/SessionJournal          # new replay + memory substrate
  -> prototypes/ChatSession.Memory      # concrete profiles, future rename
```

本分片不要求立即反转 `prototypes/ChatSession` 对 memory substrate 的使用。旧项目可继续使用旧类型，
避免把 B0 扩成大规模 legacy 迁移。

## 6. 非目标

- 不实现 `DerivedRecapStore`。
- 不改 `RollingSummaryReplayRunner` 到 SessionJournal 输入。
- 不迁移 legacy `ChatSessionEngine` compaction 行为。
- 不重命名 `prototypes/ChatSession.Memory`。
- 不移动 prompt resources。
- 不实现 ArtifactSet / ContextPlanner。
- 不要求删除旧 `prototypes/ChatSession/MemorySubstrate.cs`。

## 7. 验收

实现完成后至少证明：

- `prototypes/SessionJournal` 内存在新的 memory substrate 权威类型。
- 新类型不依赖旧 `prototypes/ChatSession`。
- `prototypes/ChatSession.Memory` 的具体 profile 可以引用新 `Atelia.SessionJournal` memory substrate 类型。
- `RewriteMemoryBlockMaintainer` / `MemoryMaintenanceOrchestrator` 在新命名空间下有最小测试覆盖。
- `SessionContextHeader` 或等价新类型不依赖旧 `ContextHeader`，但能表达同样的三段 context header 信息。
- 旧 `ChatSession` 测试不因本分片破坏；如果旧类型暂时保留，应明确这是 legacy 并存。

推荐验证命令：

```bash
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj
dotnet test tests/ChatSession.Memory.Tests/ChatSession.Memory.Tests.csproj
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj
```

如果 `FamilyChat.Server.Tests` 过慢，可至少运行与 memory substrate 相关的 targeted tests，并在最终说明中记录。

## 8. 后续影响

B0 完成后，B 分片应直接使用 `SessionJournal` 内的 `MemoryPack` / snapshot / block path 类型，不再定义
临时 `MemoryPackSnapshotDto` 作为长期主形态。

C/D 分片也应优先使用新 `Atelia.SessionJournal` memory substrate。只有 legacy export replay adapter 需要在旧
`Atelia.ChatSession` 类型和新类型之间做边界转换。

## 9. 残余风险

- 短期新旧项目可能存在字段形状相同但类型身份不同的 context header / memory 类型；这是允许的分裂，不是兼容层。
- 若旧 `ChatSession` 后续也改为引用新 substrate，需要另开迁移分片，避免本分片范围爆炸。
- `HistoryMessageKind.ContextHeader` 是 Completion abstraction 中的共享 enum；新旧 context header 类型都实现
  同一个 kind 时，消费方不能盲 cast 到旧类型，必须按自己所在主干识别对应类型或先展开。

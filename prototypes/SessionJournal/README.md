# Atelia.SessionJournal

`Atelia.SessionJournal` 是建立在 `Atelia.EventJournal` 之上的 raw、event-sourced
Agent session core。它负责：

- 追加不可变 session events，并用 `Parent` lineage 表达 branch-local 历史；
- 把 observation、completion request、attempt、action 与 tool execution 写成可恢复协议；
- 在崩溃后从 exact raw tail 判定 phase，并继续尚未完成的操作；
- 以 canonical request commitment证明 Prepared request 可重建；
- 为 context/recap实现提供 neutral、event-addressed 的读取与生命周期接口。

它不负责具体 Completion provider配置、DerivedRecap持久化、Recap planning policy或
具体 Maintainer。生产 Host通常把本项目与
[`SessionJournal.DerivedRecap.Store`](../SessionJournal.DerivedRecap.Store/README.md)、
[`SessionJournal.DerivedRecap.Planner`](../SessionJournal.DerivedRecap.Planner/README.md)和
[`SessionJournal.DerivedRecap.Maintainers`](../SessionJournal.DerivedRecap.Maintainers/README.md)
组合起来。

## 30 秒心智模型

```text
SessionJournal repo
  raw EventJournal events + refs       <- correctness source
       |
       +-- SessionJournalEngine
             |- Create / Open / OpenReadOnly
             |- InspectExecutionBoundary
             |- SendAsync / ResumeAsync
             |- exact lineage/setup/history planning reads
             `- neutral context candidate/lifecycle contracts

  config/recap-planner-config.json     <- Host/Planner intent，不是 raw event
  derived/recap/v4/...                 <- 可删除重建的 sidecar，不是 raw authority
```

一次正常 online turn大致产生：

```text
ObservationAccepted
  -> CompletionRequestPrepared
  -> CompletionAttemptStarted
  -> AgentActionProduced
```

若 action包含 tool call，则继续写入 `ToolExecutionStarted`、`ToolResultObserved`，再进入下一次
completion。provider failure写入 `CompletionAttemptFailed`；这些 durable phase允许 reopen 后
`ResumeAsync`继续，而不是猜测或重发整轮。

## 引用

```xml
<ProjectReference Include="../SessionJournal/SessionJournal.csproj" />
```

目标框架是 .NET 10 / C# 14。主要 namespace：

```csharp
using Atelia.SessionJournal;
```

调用方通常还需要：

```csharp
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
```

## 创建与只读打开

创建一个新的 raw repo不要求立刻提供 runtime：

```csharp
using var engine = SessionJournalEngine.Create(
    repositoryPath,
    new SessionCreateOptions(
        ModelId: "model-id",
        SystemPrompt: "You are an assistant.",
        CompletionSurfaceId: "responses",
        DerivedContextNthPrevious: 0
    )
);
```

`Create`只接受一个尚不存在的 repository path，并创建 `main` branch。初始化时依次提交
`RuntimeConfigSetup`、`SystemPromptSetup`和 `SessionCreated`。`SessionCreateOptions.Schema`
通常保持 `SessionJournalDefaults.Schema`。

只读检查使用：

```csharp
using var engine = SessionJournalEngine.OpenReadOnly(
    repositoryPath,
    SessionJournalDefaults.MainBranchName
);

SessionExecutionBoundaryInspection boundary =
    engine.InspectExecutionBoundary();
EventAddress? head = engine.ReadCurrentHead();
SessionCurrentLineagePrefix lineage =
    engine.ReadCurrentLineagePrefix(maxHeaderCount: 513);
```

`ReadCurrentLineagePrefix`最多读取给定数量的 header。未到 root时只返回显式
`Continuation.NextAddress`，不会自动翻页；`Lookup(anchor)`只有在已到 root时才能返回
`OffLineage`，否则返回带 `RequiredAnchor/CapturedHead/HeaderCount/NextAddress`的
`BeyondPrefix`证据。需要完整 head-to-root scan的离线工具才使用
`ReadCurrentLineageHeaders()`。

`OpenReadOnly`不会执行 tail recovery，也禁止 `UseRuntime`、`SendAsync`、`ResumeAsync`和 append。
离线审计优先使用
[`SessionJournal.Offline`](../SessionJournal.Offline/README.md)，不要在应用层重新实现 raw
state machine。

## Online runtime

`SessionRuntime`是 Host提供的进程内 capability，不会把 client、endpoint或 secret写进 raw
journal：

```csharp
var runtime = new SessionRuntime(
    CompletionClient: completionClient,
    CompletionTarget: new SessionCompletionTargetIdentity(
        ConnectionId: "primary",
        Kind: "openai-compatible",
        ConnectionFingerprint: connectionFingerprint,
        RequestAdapterFingerprint: requestAdapterFingerprint
    ),
    MaxTokens: 4096,
    ContextCandidateSource: candidateSource,
    ContextLifecycle: lifecycle,
    MaximumCanonicalRequestBytes: 200_000
);

using var engine = SessionJournalEngine.Open(
    repositoryPath,
    branchName
);
engine.UseRuntime(runtime);
```

关键约束：

- `CompletionTarget`是非 secret、稳定的 connection/adapter identity；Prepared recovery要求
  当前 runtime与 durable identity exact匹配。
- online completion必须提供 `ICoherentContextCandidateSource`。当前 production实现通常是
  `DerivedRecapOnlineLifecycleCoordinator`，它同时实现 candidate source和 lifecycle。
- 有 visible tools时还必须提供 `ToolSession`和 exact `SessionToolRuntimeIdentity`。
- `MaximumCanonicalRequestBytes`是最终 canonical request JSON的 UTF-8 byte guard，不是
  provider token limit。
- `SessionUncertainCompletionRecoveryPolicy.Refuse`是默认值。只有 Host明确接受潜在重复
  provider调用时才使用 `RestartWithNewAttempt`。

完整 DerivedRecap online装配见
[`SessionJournal.DerivedRecap.Planner` README](../SessionJournal.DerivedRecap.Planner/README.md)；
可运行参考实现位于
[`SessionJournal.Cli/OnlineTurnCommand.cs`](../SessionJournal.Cli/OnlineTurnCommand.cs)。

## Send 与 recovery

启动或重开时先检查最小 runtime recovery requirement：

```csharp
SessionRuntimeRecoveryRequirements requirement =
    engine.InspectRuntimeRecoveryRequirements(cancellationToken);
```

- `NoRuntimeRequired`表示当前没有 pending dispatch；`Idle`上的新Send由Host另行选择runtime，
  `TurnFailed`应先走exact abandon contract（G0B）。
- `NewRequestRequired`表示已接受Observation但尚未冻结completion target；Host提供与该head
  governing setup匹配的新runtime，不能在active turn中append setup。
- `FrozenCompletionRequired`携带Prepared/Started恢复所需的non-secret exact target、client/API、
  visible-tool-set hash及可选tool runtime identity。inspection会先走与`ResumeAsync`相同的Prepared v5
  full reconstruction、raw-range与commitment acceptance gate，再暴露这些identity；全过程不创建
  provider client、不会调用provider/tool，也不写raw。Host必须exact bind，不能fallback到default。
- `ToolContinuationRequired`只冻结tool runtime；它不会谎称旧completion connection也被冻结。

`FrozenCompletionRequired.DispatchState == StartedOutcomeUncertain`时，默认先返回给operator；不要
为了检查identity而提前创建provider client。`SessionVisibleToolSetFingerprint.ComputeSha256(...)`
可用于在调用`ResumeAsync`前验证Host提供的visible tool definitions。

完成runtime/Recap composition后，Host应把inspection的exact head带入bound overload：

```csharp
await engine.SendAsync(requirement.CapturedHead!.Value, message, cancellationToken);
await engine.ResumeAsync(requirement.CapturedHead!.Value, cancellationToken);
```

入口resolve发现head已经变化时抛出`SessionJournalExpectedHeadMismatchException`。入口检查之后
发生的竞争仍由既有lifecycle/head CAS fence拒绝，可能保留其原有operational exception类型；两种
情况都不会把先前组合的runtime用于后来tail。调用方应从inspection重新开始，而不是仅重试最后
一次方法调用。

规则：

- raw-core `SendAsync`当前允许 `Idle`或 `TurnFailed`；产品Host应在G0B完成后先abandon
  `TurnFailed`，避免失败Observation进入后续history。
- 非 idle phase必须先 `ResumeAsync`；不要为了“恢复”再次调用 `SendAsync`。
- `ResumeOutcome.Advanced == false`表示当前没有待恢复工作。
- `Prepared` / `Started` recovery使用 durable frozen request，不重新读取 active Recap config。
- `SessionJournalNotReadyException`表示 raw lineage合法，但 context/recap prerequisite当前不可用；
  `Reason`可用于 typed Host handling。
- `SessionJournalTurnAbortedException`表示 completion已形成 terminal abort；它携带 termination与
  errors。

不要根据 exception文本、文件是否存在或 head event名称自行发明恢复逻辑；Host以
`InspectRuntimeRecoveryRequirements`选择runtime，再进入`SendAsync`或`ResumeAsync`。

## Completed turn投影与exact retract

Recent UI只需要raw、已完成的visible turn，不应看见Prepared/Started/tool protocol或
DerivedRecap sidecar。core提供exact-head projector：

```csharp
SessionCompletedTurnsSnapshot snapshot =
    engine.ReadRecentCompletedTurns(maximumCount: 12);

SessionCompletedTurnsSnapshot historical =
    engine.ReadRecentCompletedTurnsAt(capturedHead, maximumCount: 12);
```

`Turns`按newest-first排序。每个`SessionCompletedTurnProjection`保留raw observation content、
Observation/terminal Action地址以及完整结构化`ActionMessage`；Host再负责user wrapper、inline
`<think>`和reasoning display normalization。tool loop无论包含多少轮，只投影一个turn，并且只把
最终`ToolCalls.Count == 0`的Action作为terminal Action。Imported Action遵守同一规则。

`ReadRecentCompletedTurnsAt`先验证exact captured tail。Observation、Prepared、Started、settled
tool result、TurnFailed和Idle/setup tail都完整forward-fold到captured head；只有
`AwaitingToolExecution`会cut到当前tool-calling Action的predecessor，再由tail resolver验证Action
及其active tool suffix。这样既能读取更早completed turns，又不会放宽history planning对
unresolved tool dependency的拒绝，也不会用bounded tail recovery冒充完整prefix validation。

两种写操作共享一个窄result union，但不暴露通用`MoveRef`：

```csharp
SessionTurnRetractionResult abandoned =
    engine.AbandonFailedTurn(expectedTurnFailedHead);

SessionTurnRetractionResult rewound =
    engine.RewindLatestCompletedTurn(expectedTerminalActionHead);
```

- `AbandonFailedTurn`只接受exact `CompletionAttemptFailed` / `TurnFailed` head；
- `RewindLatestCompletedTurn`只接受current head本身就是最新terminal no-tool Action；
- setup-only suffix、Prepared/Started、tool-active、错误operation与stale head都不会向后扫描；
- `Moved`返回`PreviousHead`、本次CAS的`NewHead`、被移出lineage的raw observation，以及
  completed rewind时的structured terminal Action；`NewHead`不是返回时freshness proof；
- `Unavailable`携带exact boundary，`Retryable`携带expected/observed head；corrupt raw继续fail fast；
- 成功只CAS移动selected branch ref到该turn Observation的predecessor，不删除raw event bytes，也不
  删除或重编号DerivedRecap sidecar。离开current lineage的sidecar由Store membership规则自然忽略。

产品Host应让send/resume/abandon/rewind共享同一个per-session writer lock。known failure或已知stop
只有在`AbandonFailedTurn`成功后，才能承诺失败Observation不会进入后续request；uncertain Started
不能伪装成known failure。

## Context / Recap 扩展点

raw core只定义 neutral contracts：

```csharp
public interface ICoherentContextCandidateSource {
    ValueTask<SessionContextCandidateSelection> SelectAsync(...);
    ValueTask<SessionContextCandidate> MaterializeAsync(...);
}

public interface ISessionContextLifecycleCoordinator {
    ValueTask<SessionContextLifecycleResult> PrepareAsync(...);
}
```

职责分离：

- candidate source按 governing `derivedContext.nthPrevious`选择 strict ordinal，并 materialize
  exact contribution；
- Core现已提供target-aware bounded lineage/window foundation与neutral `BeyondPrefix`状态；Store/
  Planner迁移到该foundation后，bounded lineage不足必须返回`BeyondPrefix`，online preflight/
  append/completion会把它视为`ContextCandidateUnavailable` backpressure，不得伪装成
  `EmptyLineage`并退回完整raw history；
- lifecycle可在新 request前执行一次 bounded maintenance/Restore；
- raw core负责验证 candidate descriptor、setup refs、contribution hashes与 raw suffix；
- Published exact slot损坏不能跳到更旧 slot，必须恢复同一 slot或返回 not-ready。

`DerivedRecapOnlineLifecycleCoordinator`同时实现这两个接口。它必须绑定到与 Store相同
repository path和 `RefId`的同一个 `SessionJournalEngine`实例。

## 面向 Planner / 离线工具的读取

常用 read API：

| API | 用途 |
|---|---|
| `ReadCurrentHead()` | 不投影 payload的 exact head读取 |
| `InspectExecutionBoundary()` | 当前 phase/head-kind |
| `ReadCurrentLineagePrefix(maxHeaderCount)` | current head上的header-only bounded prefix |
| `ReadLineagePrefixAt(head, maxHeaderCount)` | exact historical head上的header-only bounded prefix |
| `ReadCurrentLineageHeaders()` | 显式unbounded/offline的head-to-root snapshot |
| `ResolveGoverningSetup(head)` | exact head上的 runtime/system-prompt setup |
| `ReadHistoryPlanningWindowAtBounded(...)` | payload前证明raw interval上限；返回Available或BeyondPrefix |
| `ReadHistoryPlanningWindow(...)` | 显式unbounded/offline的dependency-closed planning window |
| `ReadHistoryPlanningWindowAt(...)` | 含seeded overload；均为显式unbounded/offline重放 |
| `ReadHistoryPlanningSeeds(...)` | 显式unbounded/offline地为多个cursor准备verified seeds |
| `ScanCheckedAuditEvents(...)` | read-only完整审计 scan；供 Offline companion使用 |

`SessionHistoryPlanningUnit`不是 raw event：多个 raw events可能闭合为一个 ToolResults unit，
Prepared/Started/Failed等 protocol events也可能不产生 HistoryUnit。Planner不得用 raw event
distance冒充 context/history长度。bounded planning的`maxRawEventCount = N`会先读取至多
`N + 1`个header来证明exact `startExclusive`；如果证明不足，返回`BeyondPrefix`且该次调用的
`PayloadReads == 0`，不会继续到root或materialize部分window。
EventJournal writer会拒绝missing Parent，而append-only address顺序不能构造指向未来event的
Parent cycle；corruption gate因此用截断的历史Parent frame与payload CRC锁定可达storage损坏，
并在internal authority shape测试中单独锁定cycle拒绝。

## Setup 变更

普通Host在新Send前应使用exact-head desired reconciliation：

```csharp
SessionRuntimeRecoveryRequirements requirement =
    engine.InspectRuntimeRecoveryRequirements();

var result = engine.ReconcileDesiredSetup(
    requirement.CapturedHead,
    new SessionDesiredSetup(
        ModelId: selectedConnection.ModelId,
        CompletionSurfaceId: selectedConnection.CompletionSurfaceId,
        SystemPrompt: desiredSystemPrompt
    )
);
```

该入口只允许exact `Idle`，自动保留governing `Schema`和`DerivedContext`，按runtime setup再system
prompt的顺序幂等追加。`TurnFailed`返回`FailedTurnMustBeAbandoned`；Prepared、Started、
AwaitingAgentAction和tool-active phase返回`ActiveTurn`且不写raw。`Retryable`表示captured head已经
变化，Host应重新inspect整条决策链。

如果第二次prompt append失败，前一条runtime intent不回滚；下次retry只补缺失prompt。这是raw
operator intent的自然幂等收敛，不需要setup transaction state machine。

更低层的受控工具仍可直接调用：

可写 engine提供：

```csharp
engine.AppendRuntimeConfigSetup(new SessionRuntimeConfiguration(
    ModelId: "new-model",
    CompletionSurfaceId: "responses",
    Schema: SessionJournalDefaults.Schema,
    DerivedContext: new SessionDerivedContextConfiguration(
        NthPrevious: 0
    )
));

engine.AppendSystemPromptSetup("new system prompt");
```

setup是 raw、branch-local、event-addressed facts。`ResolveGoverningSetup(exactHead)`沿 Parent
lineage解析最近的两种 setup；不要用 repo级 mutable config替代历史 setup authority。

## Low-level append API

`AppendObservation`、`AppendImportedAgentAction`等入口主要服务受控 import、测试和迁移流程。
普通 online Agent应使用 `SendAsync` / `ResumeAsync`，否则调用方必须自行证明 operational
legality、execution checkpoint和 context commitment，容易制造无法恢复的 tail。

Legacy迁移请优先使用
[`SessionJournal.Cli import-legacy-json`](../SessionJournal.Cli/README.md#import-legacy-json)。

## Current wire 快速检查

repo schema：

```text
atelia.session-journal.trunk.v1
```

current writer把 event payload编码为 JSON envelope：

```json
{"v":1,"body":{}}
```

`v`是每种 event kind自己的 body schema version，不等于 repo schema。所有current raw reader都对
envelope、body以及Action/reasoning/tool-result等nested discriminated object执行exact-shape decode：
unknown property、duplicate property、缺失required property与writer semantic domain之外的值一律
fail closed。当前 code-owned版本：

| Event kind | Body version |
|---|---:|
| `RuntimeConfigSetup` | 2 |
| `SystemPromptSetup` | 1 |
| `SessionCreated` | 2 |
| `ObservationAccepted` | 1 |
| `AgentActionProduced` / `ImportedAgentAction` | 1 |
| `ToolExecutionStarted` / `ToolResultObserved` | 1 |
| `CompletionRequestPrepared` | 5 |
| `CompletionAttemptStarted` | 1 |
| `CompletionAttemptFailed` | 2 |

`SessionEventCodec`与 `SessionRequestManifestCodec`是 internal wire authority。调用方不要手写
event JSON，也不要依赖 JSON property顺序以外的非合同细节。原型期 wire升级采用 direct cut；
旧实验 repo若不再被 current codec接受，应显式 import/rebuild，不增加 silent fallback。

Prepared v5的两个hash字段没有另带wire-visible codec id；其含义由body version隐式冻结：

- `plan.rawRangeSha256`固定使用`atelia.session-journal.raw-range.v1`；
- `plan.exactContextInputs[].contentSha256`固定使用
  `atelia.session-journal.artifact-context-snapshot.sha256.v1`。

改变任一算法/域分隔/字段framing必须升级`CompletionRequestPrepared` body version并同步golden，不能
在v5 reader中按环境猜测或增加fallback。

EventAddress显示格式统一使用 `EventAddressTextCodec`：

```csharp
string text = EventAddressTextCodec.Format(address);
EventAddress parsed = EventAddressTextCodec.Parse(text);
```

## 验证

```bash
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet test tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet run --project prototypes/SessionJournal.Cli -- \
  validate --input <repo-dir> --branch main \
  --report-json <path-outside-repo>
```

进一步设计背景：

- [Tail execution recovery](../../docs/SessionJournal/tail-execution-recovery-design.md)
- [Tail operational semantics simplification](../../docs/SessionJournal/done/tail-operational-semantics-simplification-plan.md)
- [Event-addressed Derived Recap concepts](../../docs/SessionJournal/event-addressed-derived-recap-concepts.md)

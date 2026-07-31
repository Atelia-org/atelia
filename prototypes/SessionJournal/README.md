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
SessionCurrentLineageSnapshot lineage =
    engine.ReadCurrentLineageHeaders();
```

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

启动或重开时先检查 phase：

```csharp
SessionExecutionBoundaryInspection boundary =
    engine.InspectExecutionBoundary();

if (boundary.Phase is SessionExecutionPhase.Idle
    or SessionExecutionPhase.TurnFailed) {
    TurnResult turn = await engine.SendAsync(
        "new observation",
        cancellationToken
    );
}
else {
    ResumeOutcome resumed =
        await engine.ResumeAsync(cancellationToken);
}
```

规则：

- `SendAsync`只允许 `Idle`或 `TurnFailed`，并且在 context/recap readiness通过后才提交新的
  observation。
- 非 idle phase必须先 `ResumeAsync`；不要为了“恢复”再次调用 `SendAsync`。
- `ResumeOutcome.Advanced == false`表示当前没有待恢复工作。
- `Prepared` / `Started` recovery使用 durable frozen request，不重新读取 active Recap config。
- `SessionJournalNotReadyException`表示 raw lineage合法，但 context/recap prerequisite当前不可用；
  `Reason`可用于 typed Host handling。
- `SessionJournalTurnAbortedException`表示 completion已形成 terminal abort；它携带 termination与
  errors。

不要根据 exception文本、文件是否存在或 head event名称自行发明恢复逻辑；以
`InspectExecutionBoundary`、`SendAsync`和 `ResumeAsync`为入口。

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
| `ReadCurrentLineageHeaders()` | header-only head-to-root lineage snapshot |
| `ResolveGoverningSetup(head)` | exact head上的 runtime/system-prompt setup |
| `ReadHistoryPlanningWindow(...)` | dependency-closed HistoryUnits与 replay-safe boundaries |
| `ReadHistoryPlanningWindowAt(...)` | 在 exact historical head重放 bounded planning window |
| `ReadHistoryPlanningSeeds(...)` | 为多个 bounded cursor准备 verified seeds |
| `ScanCheckedAuditEvents(...)` | read-only完整审计 scan；供 Offline companion使用 |

`SessionHistoryPlanningUnit`不是 raw event：多个 raw events可能闭合为一个 ToolResults unit，
Prepared/Started/Failed等 protocol events也可能不产生 HistoryUnit。Planner不得用 raw event
distance冒充 context/history长度。

## Setup 变更

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

`v`是每种 event kind自己的 body schema version，不等于 repo schema。Codec校验版本、required
shape与当前 kind定义的 exact properties；不要假设所有历史 kind具有相同的
unknown-property策略。当前 code-owned版本：

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

# SessionJournal.DerivedRecap.Planner

`Atelia.SessionJournal.DerivedRecap.Planner` 是 event-addressed DerivedRecap 的调度与执行层。
它读取 raw `SessionJournal` facts和 rebuildable `DerivedRecap.Store`，决定：

- 当前是否达到 rolling recap cadence；
- 新 set的 exact `SetAdmissionAnchor`；
- 每个 RecapBlock执行 `Maintain`还是 `Inherit`；
- 多 cursor block如何 bounded catch-up；
- 如何 Resume一个 frozen Building；
- 如何只修复 exact Published slot中缺失或损坏的部分；
- online request前如何维护/恢复 Recap并提供 coherent context candidate。

Planner不保存 active config，不拥有 concrete prompt/profile，也不改变 Published strict
ordinal。Store、Planner、Maintainers和 Host composition必须保持分离。

## 30 秒心智模型

```text
raw SessionJournal
  exact lineage + HistoryUnits + replay-safe boundaries
                    |
                    v
repo config -> Host resolves one immutable composition snapshot
                    |
                    v
        DerivedRecapPlannerExecutor
          raw safety -> HistoryLoad -> policy intent
                    |
                    v
        frozen Building in Store
          Resume uses frozen plan only
                    |
                    v
        Published exact ordinal
          Restore repairs same slot only
```

最重要的 authority规则：

- raw events是 history/provenance correctness source；
- active config只决定**新的** Building；
- Building一旦安装，Resume只服从 frozen manifest，不重新规划；
- Restore只服从 exact Published frozen state，不读取 active config/estimator；
- `NthPrevious`是 strict ordinal；损坏 slot不跳过；
- Recap Store是可删除重建的 sidecar，Planner不向 raw journal写 recap identity。

## 引用

```xml
<ProjectReference Include="../SessionJournal.DerivedRecap.Planner/SessionJournal.DerivedRecap.Planner.csproj" />
```

Planner会传递引用 raw SessionJournal与 Store contracts。若使用 built-in role-play
Maintainers，还需：

```xml
<ProjectReference Include="../SessionJournal.DerivedRecap.Maintainers/SessionJournal.DerivedRecap.Maintainers.csproj" />
```

常用 namespaces：

```csharp
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
```

## 选择入口

| 目标 | 使用类型 |
|---|---|
| 加载/初始化 repo config | `RecapPlannerConfigLoader` / `RecapPlannerConfigInitializer` |
| 解析 config planning authority | `RecapPlannerConfigSnapshot` + `RecapMaintainerCapabilitySnapshot` + `RecapPlannerConfigResolver` |
| Building-first operation preparation | `DerivedRecapOperationPreparer` + `PreparedRecapOperationAuthority` |
| lazy加载repo active config | `RepositoryRecapActivePlanningConfigurationSource` |
| 延迟构造完整Maintainer registry | `DeferredRecapBlockMaintainerRegistry` |
| 只测 HistoryLoad | `O200kBaseHistoryUnitLoadEstimator` + `RecapHistoryLoadProjector` |
| 新 planning，必要时直接执行并 Publish | `DerivedRecapPlannerExecutor` |
| Resume exact frozen Building | `DerivedRecapBuildingExecutor` |
| Restore exact Published slot | `DerivedRecapRestoreExecutor` |
| online maintenance + candidate source | `DerivedRecapOnlineLifecycleCoordinator` |
| 自定义 planning policy | `IRecapPlanningPolicy` |
| concrete maintainer lookup | Host-owned `IRecapBlockMaintainerRegistry` |

若只是操作 repository，优先使用
[`SessionJournal.Cli`](../SessionJournal.Cli/README.md)；它是当前经过真实验收的 composition
root。直接使用本库适合 Agent Host嵌入、专项测试或替换 policy/profile catalog。

## Repo-owned config

canonical path：

```text
<session-repo>/config/recap-planner-config.json
```

current schema：

```text
atelia.session-journal.recap-planner-config.v2
```

只有确认 durable phase需要 NewPlanning、且 current lineage没有 Building后，才加载 active
config。Resume、Restore、Prepared/Started recovery均跳过本节。

当前 canonical default：

```json
{
  "schema": "atelia.session-journal.recap-planner-config.v2",
  "planningPolicy": "bounded-maintain-all-v1",
  "cadence": {
    "historyUnitLoadEstimatorId": "atelia.history-load.o200k-base.history-unit-v1",
    "minimumRecentHistoryLoad": 18000,
    "recapBuildIntervalHistoryLoad": 21000
  },
  "catalog": [
    {
      "maintainerProfile": "world-understanding-rewrite",
      "maxContentUtf8Bytes": 32768
    },
    {
      "maintainerProfile": "autobiographical-rewrite",
      "maxContentUtf8Bytes": 32768
    }
  ],
  "limits": {
    "maxRawGrowthEventCount": 512,
    "maxRouteEndpointsPerBlock": 4,
    "maxMaintainerCallsPerBuild": 8,
    "maxRawEventsPerStep": 64,
    "maxRawEventsPerBuild": 512
  }
}
```

先加载typed result；下一段在创建snapshot时做exhaustive match：

```csharp
RecapPlannerConfigLoadResult loaded =
    RecapPlannerConfigLoader.Load(repositoryPath);
```

Loader负责 canonical path、safe bounded read、strict V2 JSON decode与 config hash。它不会：

- 打开 Recap Store；
- 选择 branch；
- 创建 Completion client；
- 把 `planningPolicy` / `maintainerProfile`解析成 concrete实现。

加载成功后，使用 public pure resolver把 document identity解析成 exact planning authority。
Host先把 concrete Maintainer catalog投影成metadata-only capability snapshot；prompt、Completion
client与factory不会进入 Planner：

```csharp
var concreteCapabilities = RecapMaintainerProfileCatalog.BuiltIn;
var planningCapabilities = new RecapMaintainerCapabilitySnapshot([
    .. concreteCapabilities.All.Select(profile =>
        new RecapProfilePlanningDescriptor(
            profile.ProfileName,
            new RecapBlockId(profile.RecapBlockIdValue),
            profile.Target,
            profile.MaintainerId
        )
    )
]);

RecapPlannerConfigSnapshot snapshot = loaded switch {
    RecapPlannerConfigLoadResult.Available available =>
        RecapPlannerConfigSnapshot.FromAvailable(available),
    RecapPlannerConfigLoadResult.Missing missing =>
        throw new InvalidOperationException(
            $"Missing planner config: {missing.Path}"
        ),
    RecapPlannerConfigLoadResult.Invalid invalid =>
        throw new InvalidDataException(string.Join(
            "; ",
            invalid.Defects.Select(d => $"{d.Code}: {d.Detail}")
        )),
    RecapPlannerConfigLoadResult.Unavailable unavailable =>
        throw new IOException(unavailable.Reason),
    _ => throw new InvalidOperationException(
        "Unknown config load result."
    )
};

ResolvedRecapPlanningConfiguration configuration =
    RecapPlannerConfigResolver.Resolve(
        snapshot,
        RecapPlannerConfigResolutionCatalog.BuiltIn,
        planningCapabilities
    ) switch {
        RecapPlannerConfigResolveResult.Resolved resolved =>
            resolved.Configuration,
        RecapPlannerConfigResolveResult.Invalid invalid =>
            throw new InvalidDataException(string.Join(
                "; ",
                invalid.Defects.Select(d =>
                    $"{d.Code}: {d.Detail}"
                )
            )),
        _ => throw new InvalidOperationException(
            "Unknown config resolution result."
        )
    };

RecapPlanningInputs inputs = configuration.PlanningInputs;
RecapPlanningLimits limits = configuration.PlanningLimits;
```

`RecapPlannerConfigResolutionCatalog.BuiltIn`只注册 baseline policy与o200k estimator。自定义Host
可构造自己的immutable catalog并显式注入。resolver只返回`Resolved`或`Invalid`：文件
`Missing/Invalid/Unavailable`仍属于loader层，concrete capability加载失败仍属于Host层。

同一次 operation只解析一次 snapshot，并复用同一个 immutable `inputs + limits`。`inputs.OrderedCatalog`
只包含active roster；execution registry必须覆盖完整capability catalog，使Resume/Restore仍能解析
旧frozen identity。不要从active roster构造execution registry，也不要在每个block、candidate
selection或Resume期间重新加载config。

concrete Maintainer registry只在后续prepared authority确实需要执行时，才从完整
`concreteCapabilities.All`延迟创建；不要在config resolution成功后立即创建Completion client、
logger或Maintainer。用public once-only helper延迟完整registry：

```csharp
var maintainers = new DeferredRecapBlockMaintainerRegistry(
    () => CreateCompleteMaintainerRegistry()
);
```

构造helper不会调用factory；第一次真实`TryResolve(MaintainerId, Target)`才用
`ExecutionAndPublication`激活一次。factory异常或返回null也会被缓存，不在同一个operation内重试。
helper不暴露activation状态、不拥有inner disposal，也不把custom inner升级成线程安全。

## Offline plan/build

Store必须由 operator或 Host显式创建；Planner不会 auto-create/reset：

```csharp
// 只在 repo/ref provisioning 时执行一次；已存在时会拒绝覆盖。
using var provisioningEngine = SessionJournalEngine.Open(
    repositoryPath,
    branchName
);
DerivedRecapStore newStore = DerivedRecapStore.Open(
    repositoryPath,
    provisioningEngine.BranchRefId
);
await newStore.CreateAsync(cancellationToken);
```

下面假设Store已经存在。operator同样先调用public preparer，不应手工复制Building selection、
latest Published catalog migration与raw-head fence：

```csharp
using var engine = SessionJournalEngine.Open(
    repositoryPath,
    branchName
);
DerivedRecapStore store = DerivedRecapStore.Open(
    repositoryPath,
    engine.BranchRefId
);

var source = new RepositoryRecapActivePlanningConfigurationSource(
    repositoryPath,
    planningCapabilities
);
var ready = AssertReady(await DerivedRecapOperationPreparer.PrepareAsync(
    engine,
    store,
    planningCapabilities,
    source,
    cancellationToken
));

DerivedRecapExecutionResult result;
DerivedRecapPlanningDiagnostics? diagnostics = null;
if (ready.Authority
    is PreparedRecapOperationAuthority.FrozenBuilding frozen) {
    var building = new DerivedRecapBuildingExecutor(
        engine,
        store,
        maintainers
    );
    result = await building.ResumeAsync(
        frozen.Descriptor,
        cancellationToken
    );
}
else {
    var planning =
        (PreparedRecapOperationAuthority.NewPlanning)ready.Authority;
    var planner = new DerivedRecapPlannerExecutor(
        engine,
        store,
        planning.Configuration.PlanningInputs,
        planning.Configuration.PlanningLimits,
        maintainers
    );
    result = await planner.RunAsync(
        planning.Baseline,
        cancellationToken
    );
    diagnostics = planner.LastPlanningDiagnostics;
}
```

`AssertReady`只是示意Host对`Ready / Retryable / Unavailable`做exhaustive mapping；不要把失败
折叠成无条件继续。preparer会把Available Building变成exact frozen authority；其他Building状态是
typed unavailable。Executor不会替Host把多个、stale或损坏Building猜成某个可继续的“最新”实例。

`RunAsync`只做一次 bounded operation。结果必须 exhaustive match：

```csharp
switch (result) {
    case DerivedRecapExecutionResult.Published published:
        // published.Descriptor 是 exact membership identity。
        break;
    case DerivedRecapExecutionResult.NoBuild noBuild:
        // cadence未达到，或仍等待 replay-safe admission。
        break;
    case DerivedRecapExecutionResult.BlockFailed failed:
        // Building已冻结；后续应 Resume同一 anchor。
        break;
    case DerivedRecapExecutionResult.Retryable retryable:
        // raw head/source/CAS改变；重新 capture readiness 后再尝试。
        break;
    case DerivedRecapExecutionResult.Unavailable unavailable:
        // stable config/store/frozen-plan/capability defect。
        break;
}
```

`LastPlanningDiagnostics`只有 new-planning attempt才存在：

- `RawSafetyRejected`：raw backpressure在 tokenizer前拒绝；
- `ExactSchedule`：记录 estimator、growth load、HistoryUnit/raw counts，以及可选 selected
  absorbed/recent load。

HistoryUnit/raw event counts是结构诊断，不是 scheduling authority。

## Resume frozen Building

先由 Store选择 current-lineage Building，再用 exact descriptor恢复：

```csharp
SessionCurrentLineageSnapshot lineage =
    engine.ReadCurrentLineageHeaders(cancellationToken);

CurrentLineageBuildingSelection selected =
    await store.SelectCurrentLineageBuildingAsync(
        lineage,
        cancellationToken
    );

if (selected
    is CurrentLineageBuildingSelection.Available available) {
    var executor = new DerivedRecapBuildingExecutor(
        engine,
        store,
        maintainers
    );
    DerivedRecapExecutionResult resumed =
        await executor.ResumeAsync(
            available.Snapshot.Descriptor,
            cancellationToken
        );
}
```

Resume不需要、也不应接收 `RecapPlanningInputs`或 `RecapPlanningLimits`。它使用 frozen route、
source、prior context、content ceiling与 code-owned `RecapProtocolHardCaps.V4`。healthy final
block直接复用；只补缺失或未完成工作。

`None`、`Multiple`、`Stale`、`Invalid`和 `StoreUnavailable`必须分别处理，不要按目录时间选择
“最新 Building”。

## Restore exact Published slot

```csharp
var restorer = new DerivedRecapRestoreExecutor(
    engine,
    store,
    maintainers
);

DerivedRecapRestoreResult restored =
    await restorer.RestoreAsync(
        setAdmissionAnchor,
        expectedRawHead,
        cancellationToken
    );
```

`expectedRawHead`是 optimistic fence。Restore：

- 不重新规划；
- 不改变 `SetAdmissionAnchor`或 Published membership；
- 不读取 repo config或 HistoryLoad estimator；
- 保留 healthy components；
- 只从 frozen input/checkpoint恢复缺失或损坏部分。

处理 `Restored`、`Unavailable`、`Retryable`和 `BlockFailed`四种 typed result。若 exact slot无法
恢复，应把 session报告为 not-ready；不得跳到 `NthPrevious + 1`。

## Online integration

Host必须先检查durable phase；只有确实要形成新request的phase才打开Store并调用preparer。
Prepared/Started recovery跳过下面整段，因此不会读取active config或Recap Store：

```csharp
using var engine = SessionJournalEngine.Open(
    repositoryPath,
    branchName
);
SessionExecutionBoundaryInspection boundary =
    engine.InspectExecutionBoundary(cancellationToken);
bool newRequestRequired = boundary.Phase
        is SessionExecutionPhase.Idle
            or SessionExecutionPhase.TurnFailed
    || boundary.Phase == SessionExecutionPhase.AwaitingAgentAction
        && boundary.HeadKind == SessionEventKind.ObservationAccepted;
if (!newRequestRequired) {
    // Prepared/Started等phase改走exact recovery path并立即return；
    // 不要继续打开Recap Store或构造activeConfiguration。
    return await ResumeExactRuntimeAsync(boundary, cancellationToken);
}

DerivedRecapStore store = DerivedRecapStore.Open(
    repositoryPath,
    engine.BranchRefId
);

// planningCapabilities是完整concrete capability catalog的
// metadata-only投影；source构造不读取repository。
var activeConfiguration =
    new RepositoryRecapActivePlanningConfigurationSource(
        repositoryPath,
        planningCapabilities
    );

DerivedRecapOperationPreparationResult prepared =
    await DerivedRecapOperationPreparer.PrepareAsync(
        engine,
        store,
        planningCapabilities,
        activeConfiguration,
        cancellationToken
    );

PreparedRecapOperationAuthority authority = prepared switch {
    DerivedRecapOperationPreparationResult.Ready ready =>
        ready.Authority,
    DerivedRecapOperationPreparationResult.Retryable retryable =>
        throw new InvalidOperationException(
            $"Retry preparation ({retryable.Kind}): "
            + retryable.Detail
        ),
    DerivedRecapOperationPreparationResult.Unavailable unavailable =>
        throw new InvalidOperationException(string.Join(
            "; ",
            unavailable.Defects.Select(
                defect => $"{defect.Code}: {defect.Detail}"
            )
        )),
    _ => throw new InvalidDataException(
        "Unknown DerivedRecap preparation result."
    )
};

DerivedRecapOnlineLifecycleCoordinator lifecycle =
    DerivedRecapOnlineLifecycleCoordinator.Create(
        engine,
        store,
        authority,
        maintainers
    );

engine.UseRuntime(baseRuntime with {
    ContextCandidateSource = lifecycle,
    ContextLifecycle = lifecycle
});
```

preparer内部顺序固定为：capture lineage → current-lineage Building selection →（仅在None时）
active source一次 → latest Published catalog → raw-head fence。

- `FrozenBuilding`绑定exact `BuildingDescriptor`，active source调用零次且没有config provenance；
- `NewPlanning`绑定同一个resolved config snapshot与captured-head baseline；
- 两种authority都内部绑定签发时的repository/RefId，不能拿到另一个repo/ref上创建lifecycle；
- `RawHeadChanged` / `SourceChanged`返回typed `Retryable`；
- config、Store、frozen capability或catalog shape问题返回typed `Unavailable`；
- lifecycle的public production surface只接受preparer签发的authority，不存在public unpinned
  constructor或descriptor-only绕行入口。

`planningCapabilities`必须来自完整execution capability catalog，而不是active roster；这样旧Building
使用的frozen `(MaintainerId, Target)`即使不再active，仍可被验证并恢复。concrete
`maintainers`也必须覆盖同一完整catalog，并应使用once-only deferred registry，使最终`NoBuild`时
连concrete Maintainer/maintenance logger都不创建。whole-registry factory由第一次exact binding
lookup触发；首版不做per-profile lazy。

若active source返回file-backed snapshot，其canonical path必须属于当前operation repo；resolved
active profiles也必须exact匹配传给preparer的同一份capability snapshot。自定义in-memory source可用
`CanonicalPath == null`的snapshot，但仍受capability一致性检查。

Coordinator在一次`PrepareAsync`中最多做bounded maintenance/Restore，并把同一bound Store暴露给
raw SessionJournal candidate contract。

Host的phase-first约束：

- Prepared / Started recovery不打开Store、加载config或构造Maintainer；
- current-lineage Building先于active config；
- 只有确实进入NewPlanning才加载一次repo snapshot；
- Completion client和call-log延迟到preparation/capability通过之后创建。

完整phase-first实现参考
[`OnlineTurnCommand.cs`](../SessionJournal.Cli/OnlineTurnCommand.cs)。

## HistoryLoad

current estimator：

```text
atelia.history-load.o200k-base.history-unit-v1
```

它对每个 dependency-closed `SessionHistoryPlanningUnit`独立 canonical rendering，再用
`o200k_base`计量。窗口 load是单元 load的 checked sum：

```text
R = MinimumRecentHistoryLoad
B = RecapBuildIntervalHistoryLoad
G = cadence baseline之后的 HistoryLoad

G < R + B  -> NoBuild
G >= R + B -> admission必须同时满足：
              absorbed >= B
              recent >= R
```

HistoryLoad不是当前推理模型的 token count，不用于 provider billing或 context preflight。
API failure/retry等未形成 HistoryUnit的 raw events不贡献 load，但仍受 raw safety ceilings约束。

单独测量：

```csharp
SessionHistoryPlanningWindow window =
    engine.ReadHistoryPlanningWindow();

var estimator = new O200kBaseHistoryUnitLoadEstimator();
RecapHistoryLoadMeasurement measurement =
    RecapHistoryLoadProjector.Measure(
        window,
        window.StartExclusive,
        estimator
    );
```

离线校准优先使用 CLI的 content-free
[`recap history-load inspect`](../SessionJournal.Cli/README.md#recap-history-load-inspect)。

## 常见误用

- 不要把 `HistoryUnitCount`或 raw event count重新用作 cadence trigger。
- 不要在 raw safety已经拒绝后仍运行 tokenizer。
- 不要让 policy自行发明 admission；evaluator会再次验证 exact candidate membership。
- 不要让 Store解析 planner config或 estimator identity。
- 不要让 Planner引用 concrete Maintainers assembly；由 Host composition root解析 profiles。
- 不要在 Resume/Restore读取 active config、重新选择 roster或重新计算 admission。
- 不要把 Building当作 Published，也不要用 filesystem timestamp实现 latest。
- 不要缓存 provider token count来替代 HistoryLoad。未来 load cache也只能是可删除重建的
  Planner/Host optimization。

## 测试与设计入口

```bash
dotnet test \
  tests/SessionJournal.DerivedRecap.Planner.Tests/SessionJournal.DerivedRecap.Planner.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet test \
  tests/SessionJournal.DerivedRecap.Store.Tests/SessionJournal.DerivedRecap.Store.Tests.csproj \
  -m:1 -nr:false --no-restore

dotnet test \
  tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj \
  -m:1 -nr:false --no-restore
```

建议按下面顺序阅读：

1. [Derived Recap core concepts](../../docs/SessionJournal/event-addressed-derived-recap-concepts.md)
2. [V4 target design](../../docs/SessionJournal/event-addressed-derived-recap-v4-target-design.md)
3. [HistoryLoad target design](../../docs/SessionJournal/derived-recap-history-load-target-design.md)
4. [Repo-owned planner config](../../docs/SessionJournal/recap-planner-config-repository-design.md)
5. [V4 implementation plan and evidence](../../docs/SessionJournal/event-addressed-derived-recap-v4-implementation-plan.md)

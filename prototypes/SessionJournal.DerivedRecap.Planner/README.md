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
        DerivedRecapPreparedExecutor
          (new planning or exact Building authority)
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

Contract 化简必须遵循
[canonical normalization gate](../../docs/SessionJournal/current/derived-recap/concepts.md#contract-normalization-gate)：
结构相似不代表 proof obligation 相同；不同 stage 的 typed result、authority boundary 与
fail-closed behavior 不能因此合并。

## B2 bounded online boundary

Planner当前通过 engine-lifetime-bound `SessionJournalReadView`与
`DerivedRecapLineageView`调用 Store selection、Building
admission、Publish和Restore，并把 Store的结构化 `BeyondPrefix` evidence逐层传递到 execution、
restore及online lifecycle结果；不会把它降级成普通字符串或扫描完整raw lineage来猜答案。

普通 `Prepare`、exact Building `Resume`、exact Published `Restore`与online lifecycle都只使用
bounded prefix、metadata proof和opaque write authority。需要的raw anchor/window无法在当前
prefix中证明时，会在读取recap payload、调用Maintainer或写Store之前返回stage-qualified
`BeyondPrefix`；不会退回full-lineage header/setup discovery。特别是当513-header prefix之外
可能存在prior Published baseline时，preflight只能返回`BeyondPrefix`，不能伪造exact
raw-growth count；只有baseline能在prefix内确定且配置limit更小时，才可能报告exact
`RawSafetyRejected`。

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
| 只读检查exact-head cadence progress | `DerivedRecapPlanningProgressInspector` |
| lazy加载repo active config | `RepositoryRecapActivePlanningConfigurationSource` |
| 延迟构造完整Maintainer registry | `DeferredRecapBlockMaintainerRegistry` |
| 只测 HistoryLoad | `O200kBaseHistoryUnitLoadEstimator` + `RecapHistoryLoadProjector` |
| 执行prepared new planning或exact Building | `DerivedRecapPreparedExecutor` |
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
            profile.MaintainerId,
            profile.CapabilityFingerprint
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
把 `IRecapPlanningPolicy` 与 `IHistoryUnitLoadEstimator` 实现直接交给 immutable catalog；catalog
在构造时冻结各实现当时的 `Id`，resolver 仍会拒绝后续 identity 漂移。resolver只返回`Resolved`或`Invalid`：文件
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

构造helper不会调用factory；第一次真实
`TryResolve(MaintainerId, Target, MaintainerCapabilityFingerprint)`才用
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
DerivedRecapOperationPreparationResult prepared =
    await DerivedRecapOperationPreparer.PrepareAsync(
        engine.ReadView,
        store,
        planningCapabilities,
        source,
        cancellationToken
    );

PreparedRecapOperationAuthority authority = prepared switch {
    DerivedRecapOperationPreparationResult.Ready ready =>
        ready.Authority,
    DerivedRecapOperationPreparationResult.Retryable retryable =>
        throw new InvalidOperationException(retryable.Detail),
    DerivedRecapOperationPreparationResult.Unavailable unavailable =>
        throw new InvalidDataException(string.Join(
            "; ",
            unavailable.Defects.Select(defect => defect.Detail)
        )),
    DerivedRecapOperationPreparationResult.BeyondPrefix beyond =>
        throw new InvalidOperationException(
            $"Preparation exceeded its bounded prefix at "
            + $"{beyond.Stage}: {beyond.Evidence.RequiredAnchor}"
        ),
    _ => throw new InvalidDataException(
        "Unknown DerivedRecap preparation result."
    )
};

var executor = new DerivedRecapPreparedExecutor(
    engine.ReadView,
    store,
    authority,
    maintainers
);
DerivedRecapExecutionResult result =
    await executor.ExecuteAsync(cancellationToken);
DerivedRecapPlanningDiagnostics? diagnostics =
    executor.LastPlanningDiagnostics;
```

Host必须对`Ready / Retryable / Unavailable / BeyondPrefix`做exhaustive mapping；不要把失败折叠成
无条件继续，也不要扩大bounded prefix或fallback full scan。preparer会把Available Building变成exact
frozen authority；其他Building状态是typed unavailable。`DerivedRecapPreparedExecutor`只执行这份
preparer签发的authority，不替Host猜测多个、stale或损坏Building中的“最新”实例。

`ExecuteAsync`只做一次 bounded operation。结果必须 exhaustive match：

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
    case DerivedRecapExecutionResult.BeyondPrefix beyond:
        // 保留stage/evidence；不得扩大prefix或fallback full scan。
        break;
}
```

`LastPlanningDiagnostics`只有 new-planning attempt才存在：

- `RawSafetyRejected`：raw backpressure在 tokenizer前拒绝；
- `ExactSchedule`：记录 estimator、growth load、HistoryUnit/raw counts，以及可选 selected
  absorbed/recent load。

HistoryUnit/raw event counts是结构诊断，不是 scheduling authority。

### Exact-head只读进度检查

Host需要向operator或UI展示当前cadence进度时，使用
`DerivedRecapPlanningProgressInspector`，不要直接拼接一次旧
`LastPlanningDiagnostics`与后来加载的active config：

```csharp
DerivedRecapPlanningProgressInspectionResult progress =
    await DerivedRecapPlanningProgressInspector.InspectAsync(
        engine.ReadView,
        store,
        capabilities,
        activeConfiguration,
        cancellationToken
    );
```

Inspector首先复用`DerivedRecapOperationPreparer.PrepareAsync`的Building-first顺序：current-lineage
Building存在时返回`FrozenBuilding(CapturedRawHead, Descriptor)`，并保持active config零读取；该只读
diagnostic result不会暴露后续读取Building content所需的`BuildingPlanHandle` capability。只有
`NewPlanning`才进入与production
executor共用的read-only schedule reader；该reader只完成fresh lineage/source fence、raw safety、bounded
planning window、HistoryLoad与`EvaluateSchedule`，不会调用policy、Maintainer、installer或publisher。

结果必须exhaustive处理：

- `BelowCadenceThreshold`：`RemainingHistoryLoad > 0`；
- `AwaitingReplaySafeAdmission`：load threshold已到，但尚无同时满足absorbed/recent约束的
  dependency-closed boundary；
- `CadenceReady`：cadence已有合法candidate，但这不承诺后续policy、budget、Maintainer或publication
  成功；
- `FrozenBuilding`、`RawSafetyRejected`、`Retryable`、`Unavailable`、`BeyondPrefix`保持各自typed语义；
  `Retryable.Kind`区分`RawHeadChanged`与`SourceChanged`，`Code`只是从Kind派生的字符串表示。

成功schedule的`DerivedRecapPlanningProgressSnapshot`同时携带exact `CapturedRawHead`、cadence
baseline/latest Published anchor、同一次immutable `RecapCadenceConfig`、
`RecapExactScheduleMeasurement`与checked `RemainingHistoryLoad`。它是operation-local observation，不是
durable authority；若当前raw head已不同，Host必须把它视为stale并重新inspect。HistoryUnit count与raw
event count只用于结构诊断；触发进度必须使用同一estimator identity下的HistoryLoad。
同步HistoryLoad projection前后均有cancellation fence；projection期间观察到的caller cancellation直接传播为
`OperationCanceledException`，不会被折叠为typed success或`Unavailable`。

## Resume frozen Building

显式恢复某个已知Building时，也必须先走public exact preparation，再交给统一prepared executor：

```csharp
DerivedRecapOperationPreparationResult exact =
    await DerivedRecapOperationPreparer.PrepareExactBuildingAsync(
        engine.ReadView,
        store,
        planningCapabilities,
        setAdmissionAnchor,
        cancellationToken
    );
PreparedRecapOperationAuthority exactAuthority = exact switch {
    DerivedRecapOperationPreparationResult.Ready ready =>
        ready.Authority,
    DerivedRecapOperationPreparationResult.Retryable retryable =>
        throw new InvalidOperationException(retryable.Detail),
    DerivedRecapOperationPreparationResult.Unavailable unavailable =>
        throw new InvalidDataException(string.Join(
            "; ",
            unavailable.Defects.Select(defect => defect.Detail)
        )),
    DerivedRecapOperationPreparationResult.BeyondPrefix beyond =>
        throw new InvalidOperationException(
            $"Exact Building preparation exceeded its bounded prefix "
            + $"at {beyond.Stage}: {beyond.Evidence.RequiredAnchor}"
        ),
    _ => throw new InvalidDataException(
        "Unknown exact Building preparation result."
    )
};
var exactExecutor = new DerivedRecapPreparedExecutor(
    engine.ReadView,
    store,
    exactAuthority,
    maintainers
);
DerivedRecapExecutionResult resumed =
    await exactExecutor.ExecuteAsync(cancellationToken);
```

Resume不需要、也不应接收 `RecapPlanningInputs`或 `RecapPlanningLimits`。它使用 frozen route、
source、set-level prior context、content ceiling与 code-owned `RecapProtocolHardCaps.V4`。schema v7
manifest中的 admission/source/replay boundary均冻结 exact governing setup refs；Resume验证
这些 refs并用它们构造 replay seed。healthy final block直接复用；只补缺失或未完成工作。

new planning与frozen-plan raw validation先生成bounded metadata proof，再物化exact planning
window。线上路径不调用full-lineage header/setup discovery；无法证明时返回typed、带stage的
`BeyondPrefix`。

`bounded-maintain-all-v1`在首轮使用显式Empty prior context。已有Published source时，Planner在
exact envelope double-read所得的同一个`PublishedRecapSourceSnapshot`上，按其frozen plan顺序把
全部frozen block inputs投影成一个`ContextHeaderPack`。Evaluator不允许policy在per-block decision中
回显或改写prior；它从authoritative shared prior派生`EffectivePriorContext`。只要plan含Maintain，
manifest根级冻结一次该exact snapshot及其canonical digest，每个Maintain plan只冻结相同digest；
all-Inherit则冻结Empty。snapshot包含每个Maintainer自己的上一版和其他blocks的上一版，但不读取
当前Building的partial output；因此block执行顺序和crash/reopen不会改变输入。`OldBlock`仍作为
neutral request contract交付，但current rewrite只消费上述shared prior context，避免当前block正文
重复进入prompt。

Resume与Restore都从authoritative manifest读取同一份root prior，并把同一个runtime snapshot共享给
所有pending Maintainer steps。它们不从build-local inputs重新render，也不读取live Published source。
Planner/Store在任何对应durable write前分别检查完整manifest/publication canonical encoded bytes不超过
2 MiB/3 MiB；JSON escaping与ordered plans同样计入，不以snapshot正文的单独UTF-8 guard替代wire cap。

`None`、`Multiple`、`Stale`、`Invalid`和 `StoreUnavailable`必须分别处理，不要按目录时间选择
“最新 Building”。

## Restore exact Published slot

```csharp
var restorer = new DerivedRecapRestoreExecutor(
    engine.ReadView,
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

Restore在读取Published component之前完成所有可能产生`BeyondPrefix`的metadata/window proof。
component inspection为每块签发不可构造的`PublishedBlockWriteAuthority`；checkpoint/final写成功后
返回刷新authority。最后由Store把同一exact restore handle的完整block authority roster聚合为
`PublishedEnvelopeCommitAuthority`。公开commit API不接受caller-supplied state-token map，且
envelope commit阶段只复核raw-head fence与exact component identity，不再产生`BeyondPrefix`。

处理 `Restored`、`BeyondPrefix`、`Unavailable`、`Retryable`和 `BlockFailed`五种 typed result。
若 exact slot无法恢复，应把 session报告为 not-ready；不得跳到 `NthPrevious + 1`。

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
        == SessionExecutionPhase.Idle
    || boundary.Phase == SessionExecutionPhase.AwaitingAgentAction
        && boundary.HeadKind == SessionEventKind.ObservationAccepted;
if (!newRequestRequired) {
    // Prepared/Started等phase改走exact recovery path并立即return；
    // 不要继续打开Recap Store或构造activeConfiguration。
    return await ResumeExactRuntimeAsync(boundary, cancellationToken);
}

// TurnFailed 不属于 lifecycle 支持面。Host 必须先通过
// InspectRuntimeRecoveryRequirements() 取得
// FailedTurnMustBeAbandoned 的 exact FailedHead，成功执行
// AbandonFailedTurn(FailedHead)，重新检查为 Idle 后，才可打开 Store、
// preparer 或 maintainer。

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
        engine.ReadView,
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
    DerivedRecapOperationPreparationResult.BeyondPrefix beyond =>
        throw new InvalidOperationException(
            $"Preparation exceeded its bounded prefix at "
            + $"{beyond.Stage}: {beyond.Evidence.RequiredAnchor}"
        ),
    _ => throw new InvalidDataException(
        "Unknown DerivedRecap preparation result."
    )
};

DerivedRecapOnlineLifecycleCoordinator lifecycle =
    DerivedRecapOnlineLifecycleCoordinator.Create(
        engine.ReadView,
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
使用的frozen
`(MaintainerId, Target, MaintainerCapabilityFingerprint)`即使不再active，仍可被验证并恢复。concrete
`maintainers`也必须覆盖同一完整catalog，并应使用once-only deferred registry，使最终`NoBuild`时
连concrete Maintainer/maintenance logger都不创建。whole-registry factory由第一次exact binding
lookup触发；首版不做per-profile lazy。

每个Maintain plan都冻结上述exact triple；Resume/Restore只做exact lookup，不从当前profile或
`MaintainerId`推断fingerprint。fingerprint格式固定为`sha256:<64 lowercase hex>`，但Planner不解释
preimage；具体Maintainers assembly拥有canonical计算规则。active catalog shape比较仍只覆盖
`RecapBlockId / Target / MaxContentUtf8Bytes`：它约束set形状，不把执行identity升级误判成roster
shape变化。

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

## API 费用校准工具

[`tools/recap-cadence-cost-calculator.html`](tools/recap-cadence-cost-calculator.html)
是一个无外部依赖的静态求解器，用于在质量约束已经给定
`MinimumRecentHistoryLoad = R`后，估算经济上合适的
`RecapBuildIntervalHistoryLoad = B`。直接在浏览器打开即可；纯计算核心在
[`recap-cadence-cost-model.js`](tools/recap-cadence-cost-model.js)，可用 Node focused test复核：

```bash
node --test \
  prototypes/SessionJournal.DerivedRecap.Planner/tools/recap-cadence-cost-model.test.js
```

首版使用 steady-state fluid approximation：recent suffix在一个build周期内大致从`R`增长到
`R + B`，平均增长量为`B / 2`；rewrite固定输入、recap输出和build后的prompt-cache refresh则按
`B`摊薄。归一化为每一百万新增HistoryLoad的费用后：

```text
C(B) = C0 + aB + K/B
B* = sqrt(K/a)
d²C/dB² = 2K/B³ > 0
```

因此`a > 0 && K > 0`时有唯一连续全局最小值，不需要least-squares或通用优化库。页面会应用operator
提供的可行区间、选择相邻最优整数、显示当前config对比、成本分解、5%近优区间，并生成候选cadence
JSON fragment。

页面默认价格在2026-08-06按Claude Platform官方表校准：Opus 5与Opus 4.6均为base input
`$5/MTok`、5分钟cache write `$6.25/MTok`、cache hit `$0.50/MTok`、output
`$25/MTok`；价格字段可编辑。当前价格仍应以
[Anthropic pricing](https://platform.claude.com/docs/en/about-claude/pricing)为准。

当前`RewriteRecapBlockMaintainer`在每次completion dispatch显式传递
`PromptCacheReuseHint.NoReuseExpected`。对支持禁用显式prompt cache的Anthropic，这与计算器默认
`rewriteInputPrice = $5/MTok`的base-input假设对齐，而不会继承Galatea connection的一小时TTL。
该hint只影响provider运行成本，不进入capability fingerprint，也不改变cadence公式或`HistoryLoad`
边界；对无法表达no-reuse的provider，operator仍须以真实usage覆盖页面价格输入。

这个工具不是新的scheduling authority：

- `HistoryLoad`不是provider token；`providerTokensPerHistoryLoad`与每次request新增HistoryLoad必须用
  真实provider usage和session telemetry校准；
- 纯费用最小化会把`R`、recap长度和质量推到零，因此`R`与recap内容下界必须先由任务质量约束确定；
- 模型假设固定recap输出长度、稳定有效单价和固定source pass数；若`B`跨越bounded step后增加
  Maintainer call数，真实成本是分段函数，应按真实候选interval枚举；
- 达到`R + B`仍可能等待replay-safe admission；求解结果不承诺在exact threshold立即build；
- config更新只影响未来NewPlanning；frozen Building Resume与Published Restore仍不读取active config。

## 常见误用

- 不要把 `HistoryUnitCount`或 raw event count重新用作 cadence trigger。
- 不要在 raw safety已经拒绝后仍运行 tokenizer。
- 不要让 policy自行发明 admission；evaluator会再次验证 exact candidate membership。
- 不要让 Store解析 planner config或 estimator identity。
- 不要让 Planner引用 concrete Maintainers assembly；由 Host composition root解析 profiles。
- 不要在 Resume/Restore读取 active config、重新选择 roster或重新计算 admission。
- 不要从当前Building的checkpoint/final block拼装prior context；它只能来自planning时捕获并冻结的
  previous Published source，首轮则显式Empty。
- 不要让policy输出per-block prior context；Evaluator拥有`EffectivePriorContext`，durable Maintain plan
  只提交manifest根级prior的canonical digest。
- 不要在Resume/Restore从frozen inputs重新render prior；exact snapshot已由schema v7 manifest冻结。
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

1. [Derived Recap core concepts](../../docs/SessionJournal/current/derived-recap/concepts.md)
2. [V4 target design](../../docs/SessionJournal/current/derived-recap/durable-target.md)
3. [HistoryLoad target design](../../docs/SessionJournal/current/derived-recap/history-load.md)
4. [Repo-owned planner config](../../docs/SessionJournal/current/derived-recap/planner-config.md)
5. [V4 implementation plan and evidence](../../docs/SessionJournal/archive/completed-plans/event-addressed-derived-recap-v4-implementation-plan.md)

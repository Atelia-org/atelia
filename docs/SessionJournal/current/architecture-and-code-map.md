# SessionJournal 当前架构与代码地图

> **用途**：Coding Agent 的 current implementation 发现入口  
> **实现基线**：`564c18f6137c5e9bd3145bbfb0704ac8d28a8039`  
> **基线含义**：这是本文提交前的 exact code HEAD；后续 HEAD 不自动继承本文判断  
> **非 authority 声明**：本文不是 API/wire 规范、兼容性承诺、验收结果或未来目标设计

本文只回答“当前代码在哪里、组件如何连接、改动时先看哪些测试”。精确 schema、canonical bytes、
reader rejection、durability 与 Beta support 边界，应继续核对 owning code、fixtures/tests，以及
[Beta contract snapshot](contracts/session-journal-beta-contract-snapshot.md)。EADR 术语与不可合并的 proof
obligation 见 [核心概念](derived-recap/concepts.md)。

## 30 秒心智模型

```text
raw EventJournal events + selected RefId Parent lineage
             |                         correctness authority
             v
     SessionJournalEngine
       Send / Resume / setup / bounded read
             |
             +---- Prepared event freezes exact completion request
             |
             v
repo-owned active config --NewPlanning only--> DerivedRecap Planner
                                                |
                                                v
                                     frozen Building plan
                                                |
                                                v
                                     strict Published ordinal

derived/recap/v4 is a disposable, rebuildable sidecar; it is not a raw fact.
```

四条先决判断：

- raw event 与真实 `Parent` lineage 决定 branch-local history、setup 与 recovery；目录名和数值地址不决定。
- `CompletionRequestPrepared` 冻结恢复所需的 exact request；Prepared/Started recovery 不重新读取 active recap config。
- active Planner config 只决定新的 Building；Building Resume 与 Published Restore 服从各自的 frozen authority。
- Store、Planner、Maintainers 与 Host 的类型即使结构相似，也只有在合法状态和 proof obligation 相同时才可合并。

## Assembly 依赖与所有权

依赖方向直接来自各项目 `.csproj`：

```text
DerivedRecap.Abstractions -> SessionJournal Core + Completion.Abstractions
DerivedRecap.Store        -> SessionJournal Core
DerivedRecap.Planner      -> Store + DerivedRecap.Abstractions
DerivedRecap.Maintainers  -> SessionJournal Core + DerivedRecap.Abstractions
                             + Completion.Abstractions
DerivedRecap.Runtime      -> DerivedRecap.Abstractions + Maintainers + Completion

SessionJournal.Offline -> Core + EventJournal
SessionJournal.Cli     -> Core + Store + Planner + Maintainers + Runtime + Offline + Completion
Galatea.Server         -> Core + Store + Planner + Maintainers + Runtime + Completion
```

| 组件 | 当前职责 | 明确不负责 |
|---|---|---|
| Core | raw event/wire、Parent lineage、setup、Send/Resume、Prepared recovery、context-header data contracts | Recap filesystem、Maintainer execution contract、active planning policy、concrete prompt/profile |
| DerivedRecap Abstractions | neutral epoch input、closed success union、opaque runtime-group affinity、`IRecapBlockMaintainer`与exact/deferred registry | Store wire、planning policy、concrete prompt、provider dispatch |
| Store | Building/Published IO、selection、materialization、publication、exact-slot Restore primitives | cadence、active config、Maintainer prompt、raw mutation |
| Planner | NewPlanning cadence、frozen Building execution、Published Restore orchestration、online lifecycle | durable membership、concrete Maintainer、Host secrets/provider |
| Maintainers | concrete family/member/output protocol、prompts、stable identity/fingerprint | Store/Planner workflow、raw journal、client/model/dispatch |
| Runtime | shared lane、reference-identity runtime group、bound executable Maintainer、per-call logging attribution | Store wire、planning、parallel scheduling、provider cache policy |
| CLI / Galatea | composition、phase ordering、Completion connection、operator surface | 重新定义 Core/Store authority |
| Offline | checked forward fold与只读 validation | tail repair、online recovery、derived publication |

项目入口：[Core csproj](../../../prototypes/SessionJournal/SessionJournal.csproj)、
[Store csproj](../../../prototypes/SessionJournal.DerivedRecap.Store/SessionJournal.DerivedRecap.Store.csproj)、
[Abstractions csproj](../../../prototypes/SessionJournal.DerivedRecap.Abstractions/SessionJournal.DerivedRecap.Abstractions.csproj)、
[Planner csproj](../../../prototypes/SessionJournal.DerivedRecap.Planner/SessionJournal.DerivedRecap.Planner.csproj)、
[Maintainers csproj](../../../prototypes/SessionJournal.DerivedRecap.Maintainers/SessionJournal.DerivedRecap.Maintainers.csproj)、
[Runtime csproj](../../../prototypes/SessionJournal.DerivedRecap.Runtime/SessionJournal.DerivedRecap.Runtime.csproj)、
[Offline csproj](../../../prototypes/SessionJournal.Offline/SessionJournal.Offline.csproj)、
[CLI csproj](../../../prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj)、
[Galatea csproj](../../../prototypes/Galatea/Galatea.Server.csproj)。

## 源码地图

### Raw Core

| Concern | 首读代码 | Focused tests |
|---|---|---|
| event kinds、phase、runtime contracts | [`SessionJournalContracts.cs`](../../../prototypes/SessionJournal/SessionJournalContracts.cs) | [`SessionEventBodySchemaVersionTests.cs`](../../../tests/SessionJournal.Tests/SessionEventBodySchemaVersionTests.cs) |
| strict event wire | [`SessionEventCodec.cs`](../../../prototypes/SessionJournal/SessionEventCodec.cs) | [`SessionEventCodecStrictnessTests.cs`](../../../tests/SessionJournal.Tests/SessionEventCodecStrictnessTests.cs) |
| Engine lifecycle、Send/Resume | [`SessionJournalEngine.cs`](../../../prototypes/SessionJournal/SessionJournalEngine.cs) | [`SessionJournalEngineTests.cs`](../../../tests/SessionJournal.Tests/SessionJournalEngineTests.cs) |
| exact-head mutation gate | [`SessionJournalMutationContracts.cs`](../../../prototypes/SessionJournal/SessionJournalMutationContracts.cs) | [`SessionJournalMutationGateTests.cs`](../../../tests/SessionJournal.Tests/SessionJournalMutationGateTests.cs) |
| Prepared manifest/canonical wire | [`SessionRequestManifestCodec.cs`](../../../prototypes/SessionJournal/SessionRequestManifestCodec.cs) | [`SessionRequestManifestCodecTests.cs`](../../../tests/SessionJournal.Tests/SessionRequestManifestCodecTests.cs) |
| Prepared reconstruction | [`SessionPreparedRequestReconstructor.cs`](../../../prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs) | [`SessionPreparedRequestReconstructorTests.cs`](../../../tests/SessionJournal.Tests/SessionPreparedRequestReconstructorTests.cs) |
| tail phase recovery | [`SessionExecutionTailResolver.cs`](../../../prototypes/SessionJournal/SessionExecutionTailResolver.cs) | [`SessionExecutionTailResolverTests.cs`](../../../tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs) |
| runtime recovery inspection/execution | [`SessionJournalEngine.RuntimeRecovery.cs`](../../../prototypes/SessionJournal/SessionJournalEngine.RuntimeRecovery.cs) | [`SessionRuntimeRecoveryRequirementsTests.cs`](../../../tests/SessionJournal.Tests/SessionRuntimeRecoveryRequirementsTests.cs)、[`SessionPreparedCompletionRecoveryEngineTests.cs`](../../../tests/SessionJournal.Tests/SessionPreparedCompletionRecoveryEngineTests.cs) |
| Parent lineage与 bounded proof | [`SessionHistoryPlanning.cs`](../../../prototypes/SessionJournal/SessionHistoryPlanning.cs) | [`SessionBoundedLineageTests.cs`](../../../tests/SessionJournal.Tests/SessionBoundedLineageTests.cs) |
| engine-bound read surface | [`SessionJournalReadView.cs`](../../../prototypes/SessionJournal/SessionJournalReadView.cs) | [`SessionJournalReadViewTests.cs`](../../../tests/SessionJournal.Tests/SessionJournalReadViewTests.cs) |
| governing setup | [`SessionAuthoritativeGoverningSetupResolver.cs`](../../../prototypes/SessionJournal/SessionAuthoritativeGoverningSetupResolver.cs) | [`SessionDesiredSetupReconciliationTests.cs`](../../../tests/SessionJournal.Tests/SessionDesiredSetupReconciliationTests.cs) |

详细使用入口是 [Core README](../../../prototypes/SessionJournal/README.md)，但 wire 或 recovery 改动不能只读 README。

### DerivedRecap Store

| Concern | 首读代码 | Focused tests |
|---|---|---|
| durable/result contracts | [`DerivedRecapContracts.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapContracts.cs) | [`DerivedRecapAuthorityBoundaryTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapAuthorityBoundaryTests.cs) |
| schemas、strict codecs | [`DerivedRecapCodec.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapCodec.cs) | [`DerivedRecapCodecTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapCodecTests.cs) |
| Store open/selection/materialize | [`DerivedRecapStore.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapStore.cs) | [`DerivedRecapStoreAcceptanceTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapStoreAcceptanceTests.cs) |
| engine-bound lineage | [`DerivedRecapLineageView.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapLineageView.cs) | [`DerivedRecapCurrentLineageBuildingTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapCurrentLineageBuildingTests.cs) |
| Building install | [`DerivedRecapBuildingInstaller.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapBuildingInstaller.cs) | [`DerivedRecapAuthorityBoundaryTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapAuthorityBoundaryTests.cs) |
| atomic publish | [`DerivedRecapPublisher.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapPublisher.cs) | [`DerivedRecapPublisherTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapPublisherTests.cs) |
| exact-slot restore primitive | [`DerivedRecapRestorer.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapRestorer.cs) | [`DerivedRecapPublishedRestoreInspectionTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapPublishedRestoreInspectionTests.cs)、[`DerivedRecapPublishedRestoreWriteTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapPublishedRestoreWriteTests.cs) |
| filesystem durability | [`RecapDurableFileSystem.cs`](../../../prototypes/SessionJournal.DerivedRecap.Store/RecapDurableFileSystem.cs) | [`DerivedRecapCrashRecoveryTests.cs`](../../../tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapCrashRecoveryTests.cs) |

使用与 durability 边界见 [Store README](../../../prototypes/SessionJournal.DerivedRecap.Store/README.md)。

### DerivedRecap Planner 与 Maintainers

| Concern | 首读代码 | Focused tests |
|---|---|---|
| config document/codec/repository | [`RecapPlannerConfigRepository.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerConfigRepository.cs) | [`RecapPlannerConfigRepositoryTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/RecapPlannerConfigRepositoryTests.cs) |
| pure config resolution | [`RecapPlannerConfigResolution.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerConfigResolution.cs) | [`RecapPlannerConfigResolverTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/RecapPlannerConfigResolverTests.cs) |
| HistoryLoad measure/project | [`O200kBaseHistoryUnitLoadEstimator.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/O200kBaseHistoryUnitLoadEstimator.cs)、[`RecapHistoryLoadProjector.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/RecapHistoryLoadProjector.cs) | [`RecapHistoryLoadProjectorTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/RecapHistoryLoadProjectorTests.cs) |
| cadence/policy evaluation | [`RecapPlanEvaluator.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/RecapPlanEvaluator.cs) | [`RecapPlanEvaluatorTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/RecapPlanEvaluatorTests.cs) |
| phase-first authority preparation | [`DerivedRecapOperationPreparer.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapOperationPreparer.cs) | [`DerivedRecapOperationPreparerTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/DerivedRecapOperationPreparerTests.cs) |
| NewPlanning/Building execution | [`DerivedRecapPreparedExecutor.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapPreparedExecutor.cs) | [`DerivedRecapPlannerExecutorTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/DerivedRecapPlannerExecutorTests.cs) |
| exact Published Restore | [`DerivedRecapRestoreExecutor.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapRestoreExecutor.cs) | [`DerivedRecapRestoreExecutorTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/DerivedRecapRestoreExecutorTests.cs) |
| online candidate/lifecycle | [`DerivedRecapOnlineLifecycleCoordinator.cs`](../../../prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapOnlineLifecycleCoordinator.cs) | [`DerivedRecapOnlineLifecycleCoordinatorTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/DerivedRecapOnlineLifecycleCoordinatorTests.cs) |
| neutral epoch/success/registry contracts | [`RecapMaintenanceContracts.cs`](../../../prototypes/SessionJournal.DerivedRecap.Abstractions/RecapMaintenanceContracts.cs) | [`DeferredRecapBlockMaintainerRegistryTests.cs`](../../../tests/SessionJournal.DerivedRecap.Planner.Tests/DeferredRecapBlockMaintainerRegistryTests.cs) |
| family/member/fingerprint catalog | [`RecapMaintainerDefinitions.cs`](../../../prototypes/SessionJournal.DerivedRecap.Maintainers/RecapMaintainerDefinitions.cs)、[`RecapMaintainerProfileCatalog.cs`](../../../prototypes/SessionJournal.DerivedRecap.Maintainers/RecapMaintainerProfileCatalog.cs) | [`RecapMaintainerFamilyContractTests.cs`](../../../tests/SessionJournal.DerivedRecap.Maintainers.Tests/RecapMaintainerFamilyContractTests.cs) |
| lane/group/bound dispatch | [`RecapExecutionLane.cs`](../../../prototypes/SessionJournal.DerivedRecap.Runtime/RecapExecutionLane.cs)、[`BoundRecapBlockMaintainer.cs`](../../../prototypes/SessionJournal.DerivedRecap.Runtime/BoundRecapBlockMaintainer.cs) | [`RecapRuntimeBindingTests.cs`](../../../tests/SessionJournal.DerivedRecap.Maintainers.Tests/RecapRuntimeBindingTests.cs)、[`BoundRecapBlockMaintainerTests.cs`](../../../tests/SessionJournal.DerivedRecap.Maintainers.Tests/BoundRecapBlockMaintainerTests.cs) |
| structured output protocol | [`RecapMaintainerDefinitions.cs`](../../../prototypes/SessionJournal.DerivedRecap.Maintainers/RecapMaintainerDefinitions.cs) | [`StructuredRecapMaintainerOutputProtocolTests.cs`](../../../tests/SessionJournal.DerivedRecap.Maintainers.Tests/StructuredRecapMaintainerOutputProtocolTests.cs) |

按需细读 [Planner README](../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) 与
[Maintainers README](../../../prototypes/SessionJournal.DerivedRecap.Maintainers/README.md)。可运行 composition
参考 [`OnlineTurnCommand.cs`](../../../prototypes/SessionJournal.Cli/OnlineTurnCommand.cs) 和
[`RecapCliComposition.cs`](../../../prototypes/SessionJournal.Cli/RecapCliComposition.cs)；Galatea composition
入口是 [`GalateaServices.cs`](../../../prototypes/Galatea/GalateaServices.cs)。Planner 的 public execution
路径是 `DerivedRecapOperationPreparer.PrepareAsync/PrepareExactBuildingAsync`签发
`PreparedRecapOperationAuthority`，再由 `DerivedRecapPreparedExecutor.ExecuteAsync`消费；不要把 internal
workflow executor 当成 Host entry。

### Offline、Host 与操作验收

| Concern | Current entry | Focused tests / procedure |
|---|---|---|
| read-only full audit | [`SessionJournalOfflineValidator.cs`](../../../prototypes/SessionJournal.Offline/SessionJournalOfflineValidator.cs)、[Offline README](../../../prototypes/SessionJournal.Offline/README.md) | [`SessionJournalOfflineValidatorTests.cs`](../../../tests/SessionJournal.Offline.Tests/SessionJournalOfflineValidatorTests.cs) |
| CLI real-data recap/import gate | [`Program.cs`](../../../prototypes/SessionJournal.Cli/Program.cs)、[CLI README](../../../prototypes/SessionJournal.Cli/README.md) | [`DerivedRecapRealDataAcceptanceTests.cs`](../../../tests/SessionJournal.Cli.Tests/DerivedRecapRealDataAcceptanceTests.cs) |
| Galatea G2A Host acceptance | [`GalateaServices.cs`](../../../prototypes/Galatea/GalateaServices.cs) | [`GalateaG2AStagingHostAcceptanceTests.cs`](../../../tests/Galatea.Server.Tests/GalateaG2AStagingHostAcceptanceTests.cs) |
| G2A disposable clone safety | [G2A runbook](../operations/galatea-g2a-staging-acceptance.md) | [`GalateaG2AStagingCloneSafetyTests.cs`](../../../tests/Galatea.Server.Tests/GalateaG2AStagingCloneSafetyTests.cs) |

G2A runbook只定义repeatable procedure，不是当前 HEAD 的 `Passed` evidence。Host acceptance与real-data
acceptance都是显式environment-gated；普通test run中的skip不能被报告成通过external gate。Clone-safety
tests可独立验证本地安全性质，但也不能代替真实export、provider或staging执行。

## 三类运行流程

### Core `SendAsync` / `ResumeAsync`

1. Host 先调用 `InspectRuntimeRecoveryRequirements`并捕获 exact head；setup变化先走 desired reconciliation。
2. `SendAsync(expectedHead, ...)`只从 Idle 开新 turn，持有单实例 mutation guard，并写 Observation。
3. 新 request 经 context lifecycle/candidate materialization，canonicalize 后写 Prepared，再写 Started并调用 provider。
4. `ResumeAsync(expectedHead, ...)`按 durable phase继续；Prepared/Started使用重建后的 frozen request。
5. Started outcome uncertain默认 `Refuse`；显式 `RestartWithNewAttempt`可能重复 provider side effect。
6. tool continuation还要 exact匹配 durable tool runtime identity；不要由 exception文本猜 phase。

### Planner NewPlanning / frozen Building Resume

1. Host 先看 raw phase；Prepared/Started recovery立即走 Core Resume，不打开 Store或加载 config。
2. `DerivedRecapOperationPreparer`先 capture bounded lineage，再查 current-lineage Building。
3. 只有没有 Building才加载一次 active config，读取 latest Published baseline并签发 NewPlanning authority。
4. NewPlanning先过 raw safety，再做 HistoryLoad与 policy；合法 plan由 installer冻结成 Building。
5. frozen Building Resume exact匹配 manifest中的 capability/profile identity，不读取 active config或重新规划。
6. executor完成 blocks后由 Publisher以 captured lineage和最终 raw-head fence原子提升为 Published。

### Exact Published Restore

1. strict ordinal先选择 exact Published slot；metadata或payload损坏都不允许跳到邻近 slot。
2. Store inspection签发 exact-handle、per-block write authority；Planner只修复缺失/损坏 component。
3. Restore使用 frozen input/checkpoint和完整 capability registry，不加载 config、不重算 admission/HistoryLoad。
4. 完整 authority roster才能签发 envelope commit authority；最终仍检查 raw head和component identity。

## Authority 与安全升级条件

- `SessionJournalEngine`是普通 consumer 的 raw mutation入口；同一实例的 outer mutations必须由 Host串行化。
- `SessionJournalReadView`及其派生 lineage/prepared authority绑定 owning Engine lifetime，dispose后不得复用。
- caller不能用普通 descriptor、路径存在性或自造token替代 Store/Planner签发的 opaque authority。
- bounded proof不足必须传播 typed `BeyondPrefix`，不得 hidden-page或fallback full lineage。
- Store final-head reread 是 fence，不是跨同一 Engine caller的原子 CAS；不要据此放松 Host serialization。
- Maintainers 的 embedded prompt `LogicalName`有意保留旧 `Atelia.SessionJournal.Maintainers.Prompts.*`
  resource identity。`LogicalName`本身不进入fingerprint preimage；prompt fingerprint由schema与两段prompt
  文本计算，capability fingerprint再绑定implementation、maintainer、target与prompt fingerprint。

遇到以下改动时，本地图不够，必须继续读 owning codec/contracts、focused tests和fixtures：

- event kind/body、Prepared manifest、canonical JSON/hash、unknown-field与schema rejection；
- Parent traversal、setup authority、bounded prefix/window、exact-head mutation与 rewind/abandon；
- Building/Published membership、strict ordinal、corruption、Restore、authority issuance；
- path normalization、symlink/reparse、lock、fsync、same-directory temporary与atomic rename；
- import/migration、真实数据、external provider、secret/call-log与staging promotion。

接受 wire/contract变化时要建立独立 candidate并重跑对应 gate；“测试绿”不能替代 canonical bytes、
reader language、crash/recovery和authority边界审阅。

## 当前真实开放边界

- **R-PLAN-01 — Planner static reachability guard**：`RecapPlannerConfigResolver`当前校验单字段、catalog与protocol hard caps，
  尚未用跨字段规则拒绝“在 `maxRawGrowthEventCount`内不可能达到 `R + B` HistoryLoad”的配置。
- **Provider Started outcome uncertain**：详见[current safety contract](recovery/uncertain-external-effects.md)。
  Completion provider path目前只有默认 `Refuse`与显式
  `RestartWithNewAttempt`；provider result lookup/reconciliation尚未实现，restart可能重复provider side effect。
- **Tool continuation capability boundary**：这不由provider policy控制，完整边界见
  [current safety contract](recovery/uncertain-external-effects.md)。`ToolExecutionStarted`冻结tool runtime
  identity、operation id与execution sequence，Resume用同一reservation继续。只有天然幂等或Host能按operation id
  自行去重/查询结果的工具适合自动恢复；非幂等且结果不可查询的工具不得自动恢复。当前Core尚未提供按该
  side-effect capability自动选择resume/pause的策略层。
- **G3 warm-up**：Galatea当前没有post-response DerivedRecap warm-up路径；在真实延迟证据、独立设计与验收前，
  不应把它加入 response-critical workflow。
- **External acceptance**：deterministic tests不等于当前HEAD已通过真实provider/staging gate；每轮都要生成新evidence。

## 更新与验证

当 assembly引用、owner文件、public Host流程、authority边界或上述开放项改变时：

1. 从 current `.csproj`、code与tests重核本文；不要从归档计划推导 current事实。
2. 把“实现已存在”和“目标仍开放”分开；记录新的 exact implementation baseline。
3. 验证所有链接目标与表中关键symbol仍存在。
4. 先跑受影响的focused tests；跨assembly authority或wire变化再扩大到对应project suites。
5. 运行文档结构检查和diff检查：

```bash
python scripts/check_session_journal_docs.py --all-tracked
git diff --check
```

若实现变化但地图尚未重核，应把本文视为 stale navigation hint，并直接从 `.csproj`、code和tests重新定位。

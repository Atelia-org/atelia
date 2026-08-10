# DerivedRecap Grid Rewrite Migration Ledger

标识：`DRGRID-CUTOVER`

状态：Open inventory；本文件是旧实现到Grid rewrite的单一迁移/删除账本，不表示任何replacement已经完成。

上游：[`Grid target`](derived-recap-grid-target-design.md) ·
[`Master plan`](derived-recap-grid-rewrite-master-plan.md) ·
[`WP-00`](derived-recap-grid-wp00-baseline-and-walking-skeleton.md)

## 1. Ledger rules

每项`Disposition`只能取以下六值之一：

```text
Preserve | Move | Rewrite | Delete | Retarget | Keep-connected-until-WP08
```

- `Preserve`：语义与owner都应保留；允许只做必要的neutral seam调整。
- `Move`：语义和golden保留，canonical owner迁移，旧owner最终删除。
- `Rewrite`：保留列出的behavior/invariant，以新语义和新owner重写；旧symbol不保留兼容层。
- `Delete`：旧artifact/layout/owner本身不进入目标系统；只有明确列出的行为可由其他ledger项承接。
- `Retarget`：用户或operator surface保留，但数据源、报告语义或命令实现切到新owner。
- `Keep-connected-until-WP08`：current production caller在WP-08 atomic cut之前保持可编译可运行；不得提前注释、断线或加长期flag。

所有条目初始为`Open`。只有对应gate取得exact test/scan evidence后才能改为`Closed`。`Closed` proof必须记录
commit、验证命令和结果；“replacement文件已经存在”不是关闭证据。

## 2. Exact baseline

Fresh inventory基线：

```text
main                               5e1ba46eb84f784a6fa481829a0cabc14b73781f
archive/derived-recap-pre-grid     5e1ba46eb84f784a6fa481829a0cabc14b73781f
feature/derived-recap-grid-rewrite 5e1ba46eb84f784a6fa481829a0cabc14b73781f
```

调查开始时`main`位于上述HEAD且worktree clean；随后WP-00在同一HEAD创建/切到archive与feature branch。
本ledger的current facts以该commit为准；后续每个WP仍须重新盘点exact HEAD和dirty writers。

Baseline evidence：

```bash
git rev-parse main
git rev-parse archive/derived-recap-pre-grid
git rev-parse feature/derived-recap-grid-rewrite
git status --porcelain=v1
dotnet sln Atelia.sln list
```

## 3. Production call roots

baseline上concrete DerivedRecap production consumers只有Galatea与`SessionJournal.Cli`。`SessionJournal.csproj`
只引用Diagnostics、EventJournal、Completion.Abstractions和Completion.Tools；它通过neutral candidate/lifecycle/raw-read
contracts与derived subsystem交互，不引用concrete DerivedRecap project。

| Root | Exact evidence | Current behavior | Cutover owner |
|---|---|---|---|
| Galatea read-only progress | `GalateaServices.cs:238-244` -> `GalateaRecapComposition.InspectPlanningAsync` at `GalateaRecapComposition.cs:40-100` | 读取v8 Building/raw-head cadence snapshot | WP-07B Retarget |
| Galatea fresh send | `GalateaServices.cs:549-665`，尤其`PrepareAsync`/`CreateLifecycle` at `:604-653` | 创建/验证v8 Store，驱动old online lifecycle并提供candidate | WP-07B candidate，WP-08 cut |
| Galatea NewRequest recovery | `GalateaServices.cs:668-742` | 只有`NewRequestRequired`创建old recap lifecycle | WP-07B candidate，WP-08 cut |
| Galatea Prepared/Started recovery | `GalateaServices.cs:744-809` | 按frozen completion identity `BindExact`；不创建DerivedRecap lifecycle | Preserve through WP-08 |
| CLI operator root | `SessionJournal.Cli/Program.cs:28-39` -> `RecapStoreCommands.cs:13-52` | planner-config/history-load/create/inspect/materialize/run/rebuild/reset | WP-07A Retarget/candidate，WP-08 cut |
| CLI second online Host | `SessionJournal.Cli/Program.cs:55-60`；fresh composition `OnlineTurnCommand.cs:146-213`；frozen bind `:102-127` | fresh/new-request使用old Store/Planner；Prepared/Started不创建old recap | WP-07B candidate，WP-08 cut |

Project-reference evidence：

```text
prototypes/Galatea/Galatea.Server.csproj:13-16
  -> Store, Planner, Maintainers, Runtime
prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj:11-14
  -> Maintainers, Runtime, Planner, Store
```

Repo-wide root reproduction：

```bash
rg -n "SessionJournal\.DerivedRecap|DerivedRecap" prototypes src \
  -g '*.cs' -g '*.csproj' \
  --glob '!prototypes/SessionJournal.DerivedRecap.*/*'
```

除Galatea、CLI外，baseline只命中`SessionJournal/Properties/AssemblyInfo.cs:5-6`中的旧test assembly
`InternalsVisibleTo`，它们不是production caller，但必须随旧test projects在WP-08清理。

## 4. Durable, config and operational roots

这些root也是可关闭的ledger entries；`Governed by`指向下方symbol ledger，不建立第二套迁移authority。

| ID | Root | Baseline status/authority | Exact evidence | Disposition | Governed by | Target WP | Closure gate | Status |
|---|---|---|---|---|---|---|---|---|
| D01 | `<repo>/derived/recap/v8/` | current production derived sidecar；raw不是这里的正文authority；仅offline exact-confirm archive/delete | `DerivedRecapEpochStore.cs:29-66` | Delete | L06/L07 | WP-03/05/08 | new runtime不scan/fallback/auto-delete；offline procedure exact-confirm | Open |
| D02 | `v8/locks/<ref>.lock`、`v8/refs/<ref>/{store.json,building/,published/}`、`v8/quarantine/` | v8 Store lifecycle、staging/reset recovery | `DerivedRecapEpochStore.cs:58-66,1515-1668` | Delete | L06/L07 | WP-03/05/08 | new schema/crash/reset gates green；old grammar production scan zero | Open |
| D03 | `<repo>/derived/recap/rebuild/v1/` | explicit full-rebuild execution aid，不是raw/recap authority | `DerivedRecapRebuildSpoolStore.cs:45-57` | Delete | L09 | WP-04/07A/08 | restart/full rebuild不依赖spool；offline cleanup覆盖root | Open |
| D04 | `<repo>/config/recap-planner-config.json` | optional active v3 Planner intent；只管old NewPlanning | `RecapEpochConfigDocument.cs:369-445` | Delete | L11 | WP-02/07A/07B/08 | 单一Control carrier落地；两个Hosts不再读取old config | Open |
| D05 | Galatea `.atelia/galatea/config.json`与sibling `connections.json` | Host/user/Completion registry；generic connection registry保留，只重定向recap-specific fields | `Galatea/Program.cs:10-18`、`GalateaServices.cs:1184-1249` | Retarget | L17/L22 | WP-06/07B/08 | frozen exact registry保留；active recap route切family/runtime key | Open |
| D06 | `connections.json.recapMaintainerConnections` | optional static `MaintainerId -> connectionId`映射；缺失时old behavior跟随agent connection | `GalateaConfig.cs:19-40`、`GalateaServices.cs:1272-1354` | Retarget | L22 | WP-06/07B/08 | dynamic column按allow-listed family key exact解析；无fallback | Open |
| D07 | configured/default call-log directories | operational evidence，不是cell/raw/recovery identity | `GalateaCompletionLogging.cs:12-58`、`OnlineTurnCommand.cs:13-41`、`RecapExecutionCommands.cs:12-60` | Retarget | L14/L19/L20/L22 | WP-06/07A/07B | new runtime记录route/cache/call outcomes，且不进入cell identity | Open |
| D08 | `derived/recap/v4`至`v7` | baseline runtime已无reader；仅由offline exact-confirm procedure处理 | `DerivedRecap.Store/README.md:3-5`、`GalateaFreshReadinessVerticalTests.cs:55-101` | Delete | L25 | WP-08 | normal runtime持续inert；offline procedure测试/演练 | Open |

`derived/recap/v9`与v9 wire在baseline source、tests和current docs中均不存在。它是negative inventory guard，
不是带Disposition的迁移对象：后续不得为它新增reader、fallback或cleanup state machine。

Generation evidence：

```bash
rg -n "DerivedRecap.*v9|derived/recap/v9|SchemaV9|derived-recap.*v9" \
  prototypes tests docs/SessionJournal/current
# baseline expected: zero matches

rg -n "derived/recap/v4|old v4|v4-v7" \
  prototypes tests docs/SessionJournal/current
# baseline evidence: v4-v7 are already inert; production owner is v8
```

New Grid `reset/rebuild`只处理new Grid artifacts。它不得扫描、解释、fallback、auto-delete或声称已经处理上述old roots。

## 5. Source and production ledger

| ID | Legacy symbol/path | Current callers/evidence | Behavior/invariant worth preserving | Target owner | Disposition | Target WP | Closure/deletion gate | Status |
|---|---|---|---|---|---|---|---|---|
| L01 | `SessionHistoryPlanning*`、`SessionCurrentLineagePrefix`、bounded window/setup proofs | `SessionHistoryPlanning.cs:9-724`；`SessionJournalReadView.cs:52-172`；old Planner campaign/rebuild consumers | raw selected Parent lineage、bounded planning window、setup proof、typed BeyondPrefix | SessionJournal raw API + HistoryTimeline consumer | Preserve | WP-01A/B | 原raw/lineage/setup tests green；Timeline不复制raw authority | Open |
| L02 | `ICoherentContextCandidateSource`、`ISessionContextLifecycleCoordinator`、candidate/contribution contracts | `SessionContextCandidateContracts.cs:12-229`；SessionJournal Engine与两个Hosts | neutral selection/materialization、strict status、bounded contributions、SessionJournal-owned raw-tail fold | SessionJournal core + neutral Grid adapter | Preserve | WP-05 | new adapter通过neutral contract；SessionJournal csproj无concrete Grid reference | Open |
| L03 | Prepared/request reconstruction：`SessionCoherentRequestRecipe`、`SessionPreparedRequestReconstructor`、snapshot/request hashing、runtime recovery | SessionJournal Engine/audit/recovery；Galatea/CLI frozen branches | Prepared bytes自足；Started Refuse；frozen completion identity exact bind；不重新读取active derived state | SessionJournal core + Host registry | Preserve | WP-05/07B/08 | Prepared/Started matrix byte-identical、零active Grid/control/current-route read | Open |
| L04 | `HistoryLoadUnit`、`IHistoryUnitLoadEstimator`、`RecapHistoryLoadProjector`、`O200kBaseHistoryUnitLoadEstimator` | `HistoryLoadContracts.cs`、`RecapHistoryLoadProjector.cs`、`O200kBaseHistoryUnitLoadEstimator.cs`；Planner/CLI/Galatea defaults | provider-neutral HistoryLoad identity、framing、bounds、goldens；不等同provider token | HistoryTimeline | Move | WP-01A | estimator/projector goldens equivalent；old Planner owner/reference zero | Open |
| L05 | old `DerivedRecap.Abstractions`: `RecapMaintenanceEpochInput`、`IRecapBlockMaintainer*`、group/call-control/registry | `RecapMaintenanceContracts.cs:9-260`；Planner kernel、Runtime、Host registries | `Updated`/`KeepUnchanged` distinction、lazy binding、runtime identity不进durable identity | RecapGrid.Abstractions + Manager batch seam | Rewrite | WP-02/04/06 | canonical definitions、`FrozenRowBatch`、closed per-item outcomes green；old namespace production zero | Open |
| L06 | v8 wire/layout：`DerivedRecapV8Contracts`、`DerivedRecapV8Codec`、`RecapEpoch*` wire types、`EventAddressFileNameCodec` | Store/Planner/CLI/tests | strict canonical codec、hash/bounds/fail-closed作为行为参考；epoch/layout本身不保留 | RecapGrid.Store canonical artifacts | Delete | WP-03/05 then WP-08 | new schema/strict/corruption/materialization tests green；old schema/type/path scan zero | Open |
| L07 | `DerivedRecapEpochStore`、repair/reseal/write authorities、`RecapDurableFileSystem` | Galatea/CLI、Planner、Store tests；`DerivedRecapEpochStore.cs:19-77` | immutable commit、atomic winner、crash safety、read bounds；Building/Published/repair语义不保留 | SQLite RecapGrid.Store | Delete | WP-03/04/05 then WP-08 | SQLite crash/contention/reset、missing-only resume、strict Getter green；old owner/tests removed | Open |
| L08 | `DerivedRecapContextCandidateSource` | Store project；`DerivedRecapOnlineLifecycleCoordinator.cs:68-82`；CLI materialize inspect | exact selected lineage、strict ordinal、select/materialize fence、不fallback | RecapGrid.Getter + neutral ContextComposer adapter | Rewrite | WP-05 | branch/Nth/head/promotion/raw-tail-owner matrix green；无latest/逐列/fallback旧source | Open |
| L09 | `DerivedRecapRebuildSpool*`与`derived/recap/rebuild/v1` | CLI rebuild、Planner full rebuild preparer/executor、spool tests | explicit full rebuild权限与raw binding；不保留durable campaign/spool状态机 | GridBuildRecipe + Manager missing query + CLI authority | Delete | WP-04/07A then WP-08 | restart/full rebuild E2E不依赖spool；offline cleanup覆盖root | Open |
| L10 | complete-roster Planner：`MaintainCompleteRosterEpochPolicy`、`DerivedRecapEpochCampaignExecutor`、`DerivedRecapSerialEpochKernel`、explicit rebuild、online lifecycle | Galatea/CLI composition；Planner tests | whole predictable budget preflight、started sibling drain、missing-only recovery、安全lifecycle；complete roster/epoch不保留 | RecapGrid.Manager + batch executor | Rewrite | WP-04/05/06 | overlay/full/A-B/wavefront/budget/restart/lifecycle matrix green；old symbols zero | Open |
| L11 | Planner v3 config types/codec/loader及`config/recap-planner-config.json` | Galatea/CLI分别加载或构造defaults | strict config读取、active只影响new work、frozen work不重解释；old schema/path不保留 | MaintainerControlPlane chosen carrier + Host runtime routes | Delete | WP-02/07A/07B then WP-08 | 单一carrier落地；两个Hosts不读旧config；old file仅inert/offline cleanup | Open |
| L12 | `RecapMaintainerFamilyDefinition`、`RecapMaintainerDefinition`、`RecapMaintainerProfileCatalog`、`RolePlayRecapBlockPaths`、built-ins | Maintainers project；Galatea/CLI defaults、route validation | family-owned system/tools/output；member-ownedtopic/task；provider route不进semantic identity | content-addressed Family/Maintainer definitions | Rewrite | WP-02/06 | canonical hash、allowlist、dynamic topic、renderer/parser gates green | Open |
| L13 | 被嵌入的family/autobiographical/world-understanding rewrite prompt assets | `SessionJournal.DerivedRecap.Maintainers.csproj:16-35` | 仍被target built-ins采用的prompt正文应显式进入canonical definition | Control/definition asset owner chosen in WP-02 | Move | WP-02/06 | canonical bytes和resource ownership明确；旧logical resource names零引用 | Open |
| L14 | `RecapExecutionLane*`、`RecapRuntimeGroup*`、`BoundRecapBlockMaintainer` | Runtime project；Galatea/CLI composition | family/lane affinity、leader/follower、parallel cap、logging/cache与semantic identity分离 | unique real `IRecapCellBatchExecutor` | Rewrite | WP-06 | scheduler/cache/cancel/drain/renderer tests green；Manager无第二scheduler | Open |
| L15 | Galatea read-only recap progress root与`RecapPlanningSnapshotDto`旧epoch fields | `GalateaServices.cs:238-300`、`GalateaConfig.cs:117-136` | read-only/no-create/no-secret、same-head/stale fencing；HistoryLoad仍是cadence单位 | Galatea Grid progress projection | Retarget | WP-07B | new Timeline/Grid progress tests与UI green；无v8/epoch字段或文案 | Open |
| L16 | Galatea fresh/NewRequest `GalateaPreparedRecap`、`PrepareAsync`、`CreateLifecycle` chain | `GalateaServices.cs:604-653,691-742`；`GalateaRecapComposition.cs:14-224` | fresh/new-request在safe boundary驱动maintenance并提供context | Galatea new composition root | Keep-connected-until-WP08 | WP-07B/08 | candidate vertical green后atomic cut；old lifecycle production zero | Open |
| L17 | Galatea `FrozenCompletionRequired` branch | `GalateaServices.cs:744-809` | exact `BindExact`、Started Refuse before client、restart frozen bytes；不创建recap lifecycle | Galatea Host recovery | Preserve | WP-07B/08 | Prepared/Started tests green且throwing derived collaborators未触碰 | Open |
| L18 | CLI `recap history-load`与`materialize-inspect` | `RecapHistoryLoadCommands.cs:50-78`、`RecapMaterializationInspectionCommands.cs:27-65` | read-only diagnostics、HistoryLoad calibration、strict Nth、bounded report | Timeline CLI + Grid Getter inspect | Retarget | WP-01A/05/07A | new reports/tests green；old report schema/Store source removed | Open |
| L19 | CLI `recap planner-config/create/inspect/run/rebuild/reset` | `Program.cs:28-39`、`RecapStoreCommands.cs:13-52`、`RecapExecutionCommands.cs:75-170` | confirmation/scope/no-secret/read-only gates；命令语义切新authority | Timeline/Control/Grid/Manager operator CLI | Keep-connected-until-WP08 | WP-07A/08 | candidate CLI matrix green；WP-08一次切换并删除old commands/schema | Open |
| L20 | CLI `run-online-turn` fresh/new-request branch及`--connections`/`--connection` active route | `Program.cs:55-60`、`OnlineTurnCommand.cs:146-213` | 第二Host必须与Galatea共享Manager/Composer；old implicit agent-route改为exact allow-listed family/runtime key | CLI online Host composition + Host route registry | Keep-connected-until-WP08 | WP-06/07B/08 | direct second-Host E2E；dynamic column exact route；无implicit agent-route fallback；old Store/Planner refs zero | Open |
| L21 | CLI `run-online-turn` frozen recovery branch | `OnlineTurnCommand.cs:102-127`；recap只在`:146-189` fresh branch创建 | frozen exact bind、Started Refuse、Prepared不打开active derived state | CLI Host recovery | Preserve | WP-07B/08 | Prepared/Started/frozen registry tests green | Open |
| L22 | Galatea `connections.json.recapMaintainerConnections`与CLI active `--connections`/`--connection` route | `GalateaConfig.cs:19-40`、`GalateaServices.cs:1272-1354`、`OnlineTurnCommand.cs:13-41,146-213` | exact allowlisted route、无default fallback、provider route不进durable identity；static every-column/implicit agent-route不保留 | Host family/runtime route registry | Retarget | WP-06/07B | 两Host dynamic column按family key解析；missing exact route拒绝；frozen completion registry保留 | Open |
| L23 | 五个old `SessionJournal.DerivedRecap.*` projects、solution/project refs、旧test IVT | `Atelia.sln:88-126`；Galatea csproj`:13-16`；CLI csproj`:11-14`；SessionJournal AssemblyInfo`:5-6` | 只保留真正provider-generic/raw primitives，迁准确owner | new Timeline/RecapGrid projects | Delete | WP-08 | 若复用项目名只删epoch-era owners；否则project/solution/ref/IVT全部清理 | Open |
| L24 | current DerivedRecap docs、SessionJournal/Galatea/CLI README、old active shared-epoch plan | `docs/SessionJournal/current/derived-recap/*`、architecture map、host integration、`derived-recap-shared-epoch-parallel-maintainer-refactor-plan.md` | raw authority、recovery与operator safety保留；旧architecture/layout文案不保留 | current Grid docs + archive evidence | Rewrite | WP-08 | current docs只描述Grid；历史snapshot/old plan归档；docs checker green | Open |
| L25 | 已inert `derived/recap/v4`至`v7` roots；baseline无v9 implementation | Store README`:3-5`、Galatea v4 inert test、negative v9 scan | normal runtime不得scan/mutate/fallback；cleanup必须explicit exact-confirm | offline legacy-root procedure | Delete | WP-08 | procedure测试/演练；new Grid reset不触碰old roots | Open |

## 6. Test migration ledger

| ID | Current tests/project | Preserved behavior or disposition reason | Disposition | Target WP | Closure gate | Status |
|---|---|---|---|---|---|---|
| T01 | `SessionHistoryPlanningTests`、`SessionBoundedLineageTests`、`SessionSelectedLineageAuditTests`、`SessionRawRangeHasherTests`、`SessionHistorySemanticCommitmentTests`、`SessionTailContextProjectionTests`、`SessionPrepared*`、candidate contract/route tests | raw lineage/setup/range commitment、semantic identity、SessionJournal-owned tail fold、neutral candidate、Prepared/Started contracts | Preserve | WP-01/05/07B/08 | 持续green；无concrete Grid dependency进入SessionJournal | Open |
| T02 | `DerivedRecapPreparedRecoveryIntegrationTests` | 保留“Prepared后derived Store删除仍exact resume”，替换v8 fixture | Rewrite | WP-07B/08 | Grid/control删除与throwing collaborators版本green；移除old Store/Planner project refs | Open |
| T03 | `DerivedRecapV8CodecCandidateTests` | v8 wire/layout-specific | Delete | WP-08 | WP-02/03 new canonical codec/schema tests先green | Open |
| T04 | `DerivedRecapEpochStoreCandidateTests`、`DerivedRecapEpochCrashRecoveryTests` | immutable/crash/reset/strict read行为迁到SQLite/Grid；Building/Published mechanics不保留 | Rewrite | WP-03/05 | new Store crash/contention/corruption/Getter tests green | Open |
| T05 | `DerivedRecapRebuildSpoolTests`与old CrashHarness v8/spool modes | durable spool/campaign被删除 | Delete | WP-03/07A/08 | new DB crash harness + full rebuild restart E2E green | Open |
| T06 | `O200kBaseHistoryUnitLoadEstimatorTests`、`RecapHistoryLoadProjectorTests` | canonical HistoryLoad goldens | Move | WP-01A | goldens equivalent且old Planner test owner removed | Open |
| T07 | Planner campaign/kernel/rebuild/lifecycle/config tests | budget、drain、missing-only、安全lifecycle重写；epoch/config mechanics删除 | Rewrite | WP-04/06/07 | fake/real batch与lifecycle/operator matrices green | Open |
| T08 | Maintainer family/output/runtime binding tests | family semantics、strict output、lane/group behavior重写到canonical definitions/runtime | Rewrite | WP-02/06 | definition hash/renderer/parser/scheduler tests green | Open |
| T09 | `ProgramRecapHistoryLoadCommandTests` | operator surface保留，数据源切Timeline | Retarget | WP-01A/07A | deterministic read-only report tests green | Open |
| T10 | `ProgramRecapV8CommandTests` | v8 commands改为Timeline/Control/Grid commands | Rewrite | WP-07A | confirmation/read-only/reset/build/activate E2E green | Open |
| T11 | `RecapCutoverArchitectureBoundaryTests` | current test正向要求old v8 owners存在，必须改为新zero-ledger | Rewrite | WP-08 | production source/config/current-doc scan按new denylist green | Open |
| T12 | Galatea recap composition/config/progress/fresh-readiness tests | 两Host routes、progress、fresh lifecycle改用Grid | Rewrite | WP-07B | disposable Galatea vertical + route/progress/raw gates green | Open |
| T13 | `GalateaDurableRecoveryVerticalTests` | frozen recovery invariant保留，derived deletion fixture改为Grid/control | Preserve | WP-07B/08 | Prepared byte-identical、Started Refuse、restart exact tests green | Open |
| T14 | CLI `run-online-turn` direct tests | baseline `rg -n 'run-online-turn|OnlineTurnCommand' tests`为零；第二Host coverage缺口 | Rewrite | WP-07B | fresh/NewRequest/Prepared/Started/head-drift direct vertical新增并green | Open |

Test project/ref inventory：

```text
SessionJournal.DerivedRecap.Store.Tests
SessionJournal.DerivedRecap.Store.CrashHarness
SessionJournal.DerivedRecap.Planner.Tests
SessionJournal.DerivedRecap.Maintainers.Tests
SessionJournal.Cli.Tests
Galatea.Server.Tests
SessionJournal.Tests (baseline仍直接引用old Planner/Store for integration fixture)
```

Exact project evidence位于：

```text
tests/SessionJournal.DerivedRecap.Store.Tests/SessionJournal.DerivedRecap.Store.Tests.csproj:18-19
tests/SessionJournal.DerivedRecap.Store.CrashHarness/SessionJournal.DerivedRecap.Store.CrashHarness.csproj:11-12
tests/SessionJournal.DerivedRecap.Planner.Tests/SessionJournal.DerivedRecap.Planner.Tests.csproj:18-19
tests/SessionJournal.DerivedRecap.Maintainers.Tests/SessionJournal.DerivedRecap.Maintainers.Tests.csproj:18-19
tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj:18-25
tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj:21-26
tests/SessionJournal.Tests/SessionJournal.Tests.csproj:18-22
```

## 7. WP-00 known gaps and handoff constraints

1. `Disposition`必须始终使用本ledger的六值集合；不得让Master/WP摘要中的四值速记收窄真实账本。
2. baseline的old `DerivedRecap.Abstractions.csproj`引用`Completion.Abstractions`。新项目即使名称相近也不得机械复用；
   WP-00锁定Timeline/Abstractions executable gate及future direct-reference allowlist；Control/Store/Manager在各自项目创建WP补
   同等级executable architecture gate。
3. current generation事实是“production v8；v4-v7已inert；无v9 source evidence”。WP-08的old-root procedure按exact
   inventory工作，不建立任何legacy reader。
4. CLI `run-online-turn`是第二production Host但没有direct test；T14必须在WP-07B关闭。
5. Galatea与CLI各自有一套old built-in roster/default composition：
   `GalateaRecapComposition.CreateDefaultDocument`与`BuiltInRecapPlannerConfig.Document`。WP-08必须同时移除，不能只切一个Host。
6. `RecapCutoverArchitectureBoundaryTests`目前证明的是old direct shared-epoch cut；T11必须改写成new Grid zero-ledger，不能只追加
   一条新test而保留旧正向断言。

## 8. WP-08 zero-ledger reproduction

WP-08开始时必须用fresh exact names扩充下列scan；archive/evidence与本ledger自身明确排除：

```bash
rg -n \
  'DerivedRecapEpoch|RecapEpoch|DerivedRecapV8|DerivedRecapRebuildSpool|MaintainCompleteRosterEpochPolicy|DerivedRecapSerialEpochKernel|DerivedRecapOnlineLifecycleCoordinator|IRecapBlockMaintainer|RecapExecutionLane|RecapRuntimeGroup' \
  prototypes src tests docs/SessionJournal/current \
  -g '*.cs' -g '*.csproj' -g '*.md' -g '*.json'

rg -n \
  'derived/recap/v8|derived/recap/rebuild/v1|recap-planner-config|recapMaintainerConnections' \
  prototypes src tests docs/SessionJournal/current \
  -g '*.cs' -g '*.csproj' -g '*.md' -g '*.json'

rg -n 'run-online-turn|OnlineTurnCommand' tests
```

前两项在production source/config/current docs的最终期望是零，除非某个generic primitive已经迁到准确owner且不再引用legacy
types。第三项最终必须非零并指向WP-07B second-Host tests。

# DerivedRecap Grid Rewrite Migration Ledger

标识：`DRGRID-CUTOVER`

状态：Open inventory；本文件是旧实现到Grid rewrite的单一迁移/删除账本。只有标为`Closed`的entry表示其窄gate已完成；
ledger整体仍不表示production cutover已经完成。

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
| Galatea read-only progress | production仍由`GalateaRecapComposition.InspectPlanningAsync`读取v8；internal candidate在`GalateaServices.RefreshRecentTurnsAsync`走Grid raw-head projection且不触碰v8 | candidate不读old planner；完整Grid progress DTO待cut | WP-07B candidate，WP-07C后由WP-08 cut |
| Galatea fresh send | `GalateaServices.cs:549-665`，尤其`PrepareAsync`/`CreateLifecycle` at `:604-653` | 创建/验证v8 Store，驱动old online lifecycle并提供candidate | WP-07B candidate，WP-07C后由WP-08 cut |
| Galatea NewRequest recovery | `GalateaServices.cs:668-742` | 只有`NewRequestRequired`创建old recap lifecycle | WP-07B candidate，WP-07C后由WP-08 cut |
| Galatea Prepared/Started recovery | `GalateaServices.cs:744-809` | 按frozen completion identity `BindExact`；不创建DerivedRecap lifecycle | Preserve through WP-08 |
| CLI operator root | `SessionJournal.Cli/Program.cs:28-39` -> `RecapStoreCommands.cs:13-52` | planner-config/history-load/create/inspect/materialize/run/rebuild/reset | WP-07A Retarget/candidate，WP-08 cut |
| CLI second online Host | production `run-online-turn`仍旧；candidate `recap-grid candidate run-online-turn`位于`RecapGridCandidateOnlineTurn.cs` | candidate fresh/NewRequest复用Online+Hosting；Prepared/Started frozen | WP-07B candidate，WP-07C后由WP-08 cut |

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
| D09 | `<repo>/control/recap-grid/v1/refs/<ref>/timelines/<timeline>/{control.json,lifetime.lock,writer.lock}` | WP-02 complete的单一Control authority；bounded canonical whole-state、双lock、exact Timeline binding；不在可reset的`derived` root | `SessionJournal.RecapGrid.Control/ControlRuntime.cs`、`ControlDurableFiles.cs` | Keep | L05/L11/L12 | WP-02/07A/07B/08 | 两个Hosts只读该carrier且old config零reader；backup/export/temp永不参与normal discovery | Open — WP-02 carrier/canonical/CAS/admission/backup/restore/crash/public surface与WP-07B candidate Hosts均已通过两路independent GO和final serial gates；仅WP-08 production cut仍Open |
| D10 | `<repo>/derived/recap-grid/v1/{grid.sqlite,lifetime.lock}` | WP-03 complete的唯一Grid artifact/ref authority；SQLite canonical BLOB + validated locators/members/FK；Grid-only reset | `SessionJournal.RecapGrid.Store/SchemaV1.sql`、`StoreRuntime.cs`、`StoreMaintenance.cs` | Keep | L06/L07 | WP-03/04/05/07A/08 | Manager/Getter/Hosts只经public Store API消费；normal不扫描temp/orphan/old roots，reset不触碰raw/Timeline/Control | Open — WP-03 Store、WP-04 Manager、WP-05 Getter、WP-07A operator与WP-07B candidate Hosts均complete；CLI init按domain显式创建且stable store-only命令不变，仅WP-08 production cut仍Open |

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

WP-01C新增的target root是`<repo>/derived/history-timeline/v1/`，exact inventory只有
`locks/<ref>.lock`、`refs/<ref>/locator.json`与`refs/<ref>/timelines/<timeline>.sqlite`。它不是old-root migration对象：normal
runtime只跟随canonical locator，不扫描old recap roots、orphan Timeline DB或backup；Grid reset也不处理它。single SQLite backend、
backup/restore/abandon与public factory/Reader已完成并取得两路independent review GO；final serial evidence为Timeline 156/156、raw 19/19、
walking 13/13、public surface 2/2、solution build 0 warning / 0 error、docs 15/0与diff clean。包含本次变更的containing commit作为
commit evidence，不在提交前虚构hash。current production仍未cutover，因此不提前关闭任何WP-08 deletion entry。
这里的exact inventory只列normal canonical slots：crash可留下unreferenced Timeline SQLite、dot-temp或exact SQLite rollback journal；它们
不参与normal discovery/latest选择，只能由SQLite recovery或后续explicit inventory/retention action处理。V1 durable lease/fsync为
Linux-only，其他platform返回typed unsupported，不提供弱durability fallback。

## 5. Source and production ledger

| ID | Legacy symbol/path | Current callers/evidence | Behavior/invariant worth preserving | Target owner | Disposition | Target WP | Closure/deletion gate | Status |
|---|---|---|---|---|---|---|---|---|
| L01 | `SessionHistoryPlanning*`、`SessionCurrentLineagePrefix`、bounded window/setup proofs | `SessionHistoryPlanning.cs:9-724`；`SessionJournalReadView.cs:52-172`；old Planner campaign/rebuild consumers | raw selected Parent lineage、bounded planning window、setup proof、typed BeyondPrefix | SessionJournal raw API + HistoryTimeline consumer | Preserve | WP-01A/B | 原raw/lineage/setup tests green；Timeline不复制raw authority | Closed — WP-01 complete：Timeline 156/156、raw audit 19/19、walking architecture 13/13、public surface 2/2、solution 0W/0E、docs 15/0、diff clean、两路independent GO；containing commit为commit evidence |
| L02 | `ICoherentContextCandidateSource`、`ISessionContextLifecycleCoordinator`、candidate/contribution contracts | `SessionContextCandidateContracts.cs:12-229`；SessionJournal Engine与两个Hosts | neutral selection/materialization、strict status、bounded contributions、SessionJournal-owned raw-tail fold | SessionJournal core + neutral Grid adapter | Preserve | WP-05 | new adapter通过neutral contract；SessionJournal csproj无concrete Grid reference | Open — WP-05 Getter与WP-07B Online complete；同一Getter source + composite lifecycle锁定Idle seal、ObservationAccepted只readiness与OfflineValidator raw-tail完整性。WP-08 production cutover仍Open |
| L03 | Prepared/request reconstruction：`SessionCoherentRequestRecipe`、`SessionPreparedRequestReconstructor`、snapshot/request hashing、runtime recovery | SessionJournal Engine/audit/recovery；Galatea/CLI frozen branches | Prepared bytes自足；Started Refuse；frozen completion identity exact bind；不重新读取active derived state | SessionJournal core + Host registry | Preserve | WP-05/07B/08 | Prepared/Started matrix byte-identical、零active Grid/control/current-route read | Open — WP-07B complete的CLI与actual Galatea service均证明Prepared exact bind不打开Online/route、Started Refuse零client/derived；production cutover仍属WP-08 |
| L04 | `HistoryLoadUnit`、`IHistoryUnitLoadEstimator`、`RecapHistoryLoadProjector`、`O200kBaseHistoryUnitLoadEstimator` | baseline：old Planner三文件及Planner/CLI/Galatea defaults；WP-01A：`SessionJournal.HistoryTimeline/HistoryLoadContracts.cs`、`HistoryLoadProjector.cs`、`O200kBaseHistoryUnitLoadEstimator.cs` | provider-neutral HistoryLoad identity、framing、bounds、goldens；不等同provider token | HistoryTimeline | Move | WP-01A | estimator/projector 25-case baseline与new Timeline tests green；old Planner declaration/path与duplicate `EstimatorId` scan zero | Closed — final tail Timeline 54/54、Planner 42/42、solution build 0/0，independent gate P0/P1=0 |
| L05 | old `DerivedRecap.Abstractions`: `RecapMaintenanceEpochInput`、`IRecapBlockMaintainer*`、group/call-control/registry | `RecapMaintenanceContracts.cs:9-260`；Planner kernel、Runtime、Host registries | `Updated`/`KeepUnchanged` distinction、lazy binding、runtime identity不进durable identity | RecapGrid.Abstractions + Manager batch seam | Rewrite | WP-02/04/06 | canonical definitions、`FrozenRowBatch`、closed per-item outcomes green；old namespace production zero | Open — WP-04与WP-06 complete，唯一real executor、strict Updated/Keep parser与started accounting已取得两路independent GO；WP-08仍负责old namespace deletion |
| L06 | v8 wire/layout：`DerivedRecapV8Contracts`、`DerivedRecapV8Codec`、`RecapEpoch*` wire types、`EventAddressFileNameCodec` | Store/Planner/CLI/tests | strict canonical codec、hash/bounds、fail-closed作为行为参考；epoch/layout本身不保留 | RecapGrid.Store canonical artifacts | Delete | WP-03/05 then WP-08 | new schema/strict/corruption/materialization tests green；old schema/type/path scan zero | Open — WP-03 canonical/schema与WP-05 complete Getter strict materialization均已落且不读v8；旧wire/path owner deletion仍只属WP-08，current production未切换 |
| L07 | `DerivedRecapEpochStore`、repair/reseal/write authorities、`RecapDurableFileSystem` | Galatea/CLI、Planner、Store tests；`DerivedRecapEpochStore.cs:19-77` | immutable commit、atomic winner、crash safety、read bounds；Building/Published/repair语义不保留 | SQLite RecapGrid.Store | Delete | WP-03/04/05 then WP-08 | SQLite crash/contention/reset、missing-only resume、strict Getter green；old owner/tests removed | Open — WP-03 SQLite Store、WP-04 Manager与WP-05 strict Getter均已完成各自旁路；旧owner/tests与production cutover仍属WP-08 |
| L08 | `DerivedRecapContextCandidateSource` | Store project；`DerivedRecapOnlineLifecycleCoordinator.cs:68-82`；CLI materialize inspect | exact selected lineage、strict ordinal、select/materialize fence、不fallback | RecapGrid.Getter + neutral ContextComposer adapter | Rewrite | WP-05 | branch/Nth/head/promotion/raw-tail-owner matrix green；无latest/逐列/fallback旧source | Open — new pure-read Getter WP-05 slice complete：current exact fulfilled key、Timeline/RowView predecessor双链、process-local owner nonce、same Store ReaderHandle/identity、original witness复验、whole T/C/raw semantic-terminal fences、explicit neutral UnsupportedSchema mapping与shared bounded provenance均有Getter 21/21及两路independent GO证据；旧source删除仍属WP-08 |
| L09 | `DerivedRecapRebuildSpool*`与`derived/recap/rebuild/v1` | CLI rebuild、Planner full rebuild preparer/executor、spool tests | explicit full rebuild权限与raw binding；不保留durable campaign/spool状态机 | GridBuildRecipe + Manager missing query + CLI authority | Delete | WP-04/07A then WP-08 | restart/full rebuild E2E不依赖spool；offline cleanup覆盖root | Open — WP-04 complete已证明immutable missing query restart/re-entry且没有durable campaign；WP-07A complete的`build/progress/promote`直接依赖recipe+missing query且不读写old spool；old spool删除/offline cleanup仍属WP-08 |
| L10 | complete-roster Planner：`MaintainCompleteRosterEpochPolicy`、`DerivedRecapEpochCampaignExecutor`、`DerivedRecapSerialEpochKernel`、explicit rebuild、online lifecycle | Galatea/CLI composition；Planner tests | whole predictable budget preflight、started sibling drain、missing-only recovery、安全lifecycle；complete roster/epoch不保留 | RecapGrid.Manager + batch executor | Rewrite | WP-04/05/06 | overlay/full/A-B/wavefront/budget/restart/lifecycle matrix green；old symbols zero | Open — WP-04 complete Manager、WP-06 real executor与WP-07B candidate Host lifecycle均取得两路independent GO；仅production rewrite/old owner deletion仍属WP-08 |
| L11 | Planner v3 config types/codec/loader及`config/recap-planner-config.json` | Galatea/CLI分别加载或构造defaults | strict config读取、active只影响new work、frozen work不重解释；old schema/path不保留 | MaintainerControlPlane chosen carrier + Host runtime routes | Delete | WP-02/07A/07B then WP-08 | 单一carrier落地；两个Hosts不读旧config；old file仅inert/offline cleanup | Open — WP-02 single-carrier与WP-07B candidate Hosts已通过两路independent GO；仅WP-08 production cut和old file cleanup仍Open |
| L12 | `RecapMaintainerFamilyDefinition`、`RecapMaintainerDefinition`、`RecapMaintainerProfileCatalog`、`RolePlayRecapBlockPaths`、built-ins | Maintainers project；Galatea/CLI defaults、route validation | family-owned system/tools/output；member-ownedtopic/task；provider route不进semantic identity | content-addressed Family/Maintainer definitions | Rewrite | WP-02/06 | canonical hash、allowlist、dynamic topic、renderer/parser gates green | Open — WP-02 formal owners、WP-06 code-owned V1 protocol与WP-07B candidate Host composition均complete并取得independent GO；built-in genesis属WP-07C |
| L13 | 被嵌入的family/autobiographical/world-understanding rewrite prompt assets | `SessionJournal.DerivedRecap.Maintainers.csproj:16-35` | 仍被target built-ins采用的prompt正文应显式进入canonical definition | Control/definition asset owner chosen in WP-02 | Move | WP-02/06 | canonical bytes和resource ownership明确；旧logical resource names零引用 | Open |
| L14 | `RecapExecutionLane*`、`RecapRuntimeGroup*`、`BoundRecapBlockMaintainer` | Runtime project；Galatea/CLI composition | family/lane affinity、leader/follower、parallel cap、logging/cache与semantic identity分离 | unique real `IRecapCellBatchExecutor` | Rewrite | WP-06 | scheduler/cache/cancel/drain/renderer tests green；Manager无第二scheduler | Open — WP-06 runtime与WP-07B single-registry Host composition均complete并取得两路independent GO；old owner删除仍属WP-08 |
| L15 | Galatea read-only recap progress root与`RecapPlanningSnapshotDto`旧epoch fields | `GalateaServices.cs:238-300`、`GalateaConfig.cs:117-136` | read-only/no-create/no-secret、same-head/stale fencing；HistoryLoad仍是cadence单位 | Galatea Grid progress projection | Retarget | WP-07B | new Timeline/Grid progress tests与UI green；无v8/epoch字段或文案 | Open — internal candidate projection已不读old v8 planner，返回exact raw-head candidate state；完整UI progress字段切换仍属WP-08 |
| L16 | Galatea fresh/NewRequest `GalateaPreparedRecap`、`PrepareAsync`、`CreateLifecycle` chain | old production保持；internal `GalateaRecapGridCandidateComposition`/`RecapGridOnlineContextHandle`为旁路 | fresh/new-request在safe boundary驱动maintenance并提供context | Galatea new composition root | Keep-connected-until-WP08 | WP-07B/08 | candidate vertical green后atomic cut；old lifecycle production zero | Open — WP-07B complete：actual Galatea service Fresh/NewRequest、missing-work real route与CLI/Galatea exact derived equivalence green；真实main-agent request锁coherent derived authority与一个raw-tail observation，Galatea只允许显式user envelope差异；public/default仍old |
| L17 | Galatea `FrozenCompletionRequired` branch | old production保持；candidate `RunCandidateRecoveryAsync`只在NewRequest创建Online，Frozen沿outer request | exact `BindExact`、Started Refuse before client、restart frozen bytes；不创建recap lifecycle | Galatea Host recovery | Preserve | WP-07B/08 | Prepared/Started tests green且throwing derived collaborators未触碰 | Open — WP-07B complete的Prepared/Started actual service gate green；WP-08 production cut仍Open |
| L18 | CLI `recap history-load`与`materialize-inspect` | `RecapHistoryLoadCommands.cs:50-78`、`RecapMaterializationInspectionCommands.cs:27-65`；WP-01A已把HistoryLoad types/import/project ref迁到Timeline owner | read-only diagnostics、HistoryLoad calibration、strict Nth、bounded report | Timeline CLI + Grid Getter inspect | Retarget | WP-01A/05/07A | new reports/tests green；old report schema/Store source removed | Open — HistoryLoad owner迁移完成；WP-07A candidate `materialize`已用Getter strict Nth并通过promotion后exact fixture，旧production command/data source仍留到WP-08原子切换 |
| L19 | CLI `recap planner-config/create/inspect/run/rebuild/reset` | `Program.cs:28-39`、`RecapStoreCommands.cs:13-52`、`RecapExecutionCommands.cs:75-170` | confirmation/scope/no-secret/read-only gates；命令语义切新authority | Timeline/Control/Grid/Manager operator CLI | Keep-connected-until-WP08 | WP-07A/08 | candidate CLI matrix green；WP-08一次切换并删除old commands/schema | Open — WP-07A complete：明确`recap-grid candidate`子树、strict admission/confirmation、online/offline cap与raw drift、real Runtime build、pure-read progress、zero-call promotion、materialization及真实四域maintenance byte isolation 8-case E2E均green并取得两路GO；WP-08原子production switch仍Open |
| L20 | CLI `run-online-turn` fresh/new-request branch及`--connections`/`--connection` active route | old command保持；candidate `RecapGridCandidateOnlineTurn.cs`复用Online+Hosting | 第二Host必须与Galatea共享Manager/Composer；old implicit agent-route改为exact allow-listed family/runtime key | CLI online Host composition + Host route registry | Keep-connected-until-WP08 | WP-06/07B/08 | direct second-Host E2E；dynamic column exact route；无implicit agent-route fallback；old Store/Planner refs zero | Open — WP-07B complete的candidate Fresh/NewRequest与real Runtime route green，并与Galatea从同clone逐字节比较Timeline/Store/Getter；仅WP-08 production cut仍Open |
| L21 | CLI `run-online-turn` frozen recovery branch | candidate Started先于manifest/client；Prepared exact bind且不构造Online | frozen exact bind、Started Refuse、Prepared不打开active derived state | CLI Host recovery | Preserve | WP-07B/08 | Prepared/Started/frozen registry tests green | Open — WP-07B candidate focused与independent review complete；old production command待WP-08切 |
| L22 | Galatea `connections.json.recapMaintainerConnections`与CLI active route | WP-07B candidate两Host共用`RecapGridCompletionHost`；old production mapping仍保留 | exact allowlisted route、无default fallback、provider route不进durable identity | Host family/runtime route registry | Retarget | WP-06/07B | 两Host dynamic column按family key解析；missing exact route拒绝；frozen completion registry保留 | Open — WP-07B candidate复用single registry/exact lazy route且no-work不load并取得两路independent GO；old mapping删除仍属WP-08 |
| L23 | 五个old `SessionJournal.DerivedRecap.*` projects、solution/project refs、旧test IVT | `Atelia.sln:88-126`；Galatea csproj`:13-16`；CLI csproj`:11-14`；SessionJournal AssemblyInfo`:5-6` | 只保留真正provider-generic/raw primitives，迁准确owner | new Timeline/RecapGrid projects | Delete | WP-08 | 若复用项目名只删epoch-era owners；否则project/solution/ref/IVT全部清理 | Open |
| L24 | current DerivedRecap docs、SessionJournal/Galatea/CLI README、old active shared-epoch plan | `docs/SessionJournal/current/derived-recap/*`、architecture map、host integration、`derived-recap-shared-epoch-parallel-maintainer-refactor-plan.md` | raw authority、recovery与operator safety保留；旧architecture/layout文案不保留 | current Grid docs + archive evidence | Rewrite | WP-08 | current docs只描述Grid；历史snapshot/old plan归档；docs checker green | Open |
| L25 | 已inert `derived/recap/v4`至`v7` roots；baseline无v9 implementation | Store README`:3-5`、Galatea v4 inert test、negative v9 scan | normal runtime不得scan/mutate/fallback；cleanup必须explicit exact-confirm | offline legacy-root procedure | Delete | WP-08 | procedure测试/演练；new Grid reset不触碰old roots | Open |
| L26 | Agent-facing recap control tools、built-in genesis与ToolResult/ToolContinuation | WP-07B operator fixture可provision但normal Host不auto-create；current Galatea tools仍old/unsupported | admission-bound canonical control mutation、operation ID recovery、safe post-tool lifecycle | Control tool + WP-07B Online composition | Rewrite | WP-07C/08 | tool replay/idempotency/unauthorized、built-in provision、ToolResult recovery E2E green | Open — 明确拆至WP-07C；WP-07B candidate不冒充Agent tool |

## 6. Test migration ledger

| ID | Current tests/project | Preserved behavior or disposition reason | Disposition | Target WP | Closure gate | Status |
|---|---|---|---|---|---|---|
| T01 | `SessionHistoryPlanningTests`、`SessionBoundedLineageTests`、`SessionSelectedLineageAuditTests`、`SessionRawRangeHasherTests`、`SessionHistorySemanticCommitmentTests`、`SessionTailContextProjectionTests`、`SessionPrepared*`、candidate contract/route tests | raw lineage/setup/range commitment、semantic identity、SessionJournal-owned tail fold、neutral candidate、Prepared/Started contracts | Preserve | WP-01/05/07B/08 | 持续green；无concrete Grid dependency进入SessionJournal | Open — WP-07B只给SessionJournal增加owner-bound lifecycle audit seam，core仍无Grid ref；raw audit原矩阵与new lifecycle scope/cap/drift、OfflineValidator request reconstruction均green并取得两路independent GO，production切换仍属WP-08 |
| T02 | `DerivedRecapPreparedRecoveryIntegrationTests` | 保留“Prepared后derived Store删除仍exact resume”，替换v8 fixture | Rewrite | WP-07B/08 | Grid/control删除与throwing collaborators版本green；移除old Store/Planner project refs | Open |
| T03 | `DerivedRecapV8CodecCandidateTests` | v8 wire/layout-specific | Delete | WP-08 | WP-02/03 new canonical codec/schema tests先green | Open |
| T04 | `DerivedRecapEpochStoreCandidateTests`、`DerivedRecapEpochCrashRecoveryTests` | immutable/crash/reset/strict read行为迁到SQLite/Grid；Building/Published mechanics不保留 | Rewrite | WP-03/05 | new Store crash/contention/corruption/Getter tests green | Open — WP-03 durable matrix与WP-05 Getter slice均complete；Getter 21/21新增missing/corrupt cell/view/previous、identity reset lease、owner-bound selection/witness、whole-head terminal drift、neutral unsupported-schema与no-fallback fixtures，并取得两路independent GO；旧tests删除属WP-08 |
| T05 | `DerivedRecapRebuildSpoolTests`与old CrashHarness v8/spool modes | durable spool/campaign被删除 | Delete | WP-03/07A/08 | new DB crash harness + full rebuild restart E2E green | Open — new Store crash harness已落；full rebuild restart与old spool删除仍属WP-04/07A/08 |
| T06 | baseline `O200kBaseHistoryUnitLoadEstimatorTests`、`RecapHistoryLoadProjectorTests`；current `SessionJournal.HistoryTimeline.Tests` | canonical HistoryLoad goldens | Move | WP-01A | 25-case baseline保留并green；old Planner test owner removed；新增partition/contracts/codec strict matrix | Closed — Timeline 54/54、skeleton 12/12、Planner 42/42、docs 15/0，independent gate P0/P1=0；CLI baseline-equivalent，Galatea保留既有debt与route timeout flaky |
| T07 | Planner campaign/kernel/rebuild/lifecycle/config tests | budget、drain、missing-only、安全lifecycle重写；epoch/config mechanics删除 | Rewrite | WP-04/06/07 | fake/real batch与lifecycle/operator matrices green | Open — WP-07B real Idle/Observation lifecycle已green；ToolResult/Agent control拆WP-07C，old test deletion属WP-08 |
| T08 | Maintainer family/output/runtime binding tests | family semantics、strict output、lane/group behavior重写到canonical definitions/runtime | Rewrite | WP-02/06 | definition hash/renderer/parser/scheduler tests green | Open |
| T09 | `ProgramRecapHistoryLoadCommandTests` | operator surface保留，数据源切Timeline；WP-01A只完成type/import/project-ref owner迁移 | Retarget | WP-01A/07A | deterministic read-only report tests green，且command数据源/旧report schema在WP-07A完成retarget | Open — partial owner migration only |
| T10 | `ProgramRecapV8CommandTests` | v8 commands改为Timeline/Control/Grid commands | Rewrite | WP-07A | confirmation/read-only/reset/build/activate E2E green | Open — WP-07A complete的8-case CLI vertical覆盖confirmation/no-provider、online/offline sync、fork isolation、真实Runtime build、provider-free progress、zero-call promotion、Getter materialize、typed maintenance与四域byte isolation；旧v8 command tests只在WP-08切换时删除 |
| T11 | `RecapCutoverArchitectureBoundaryTests` | current test正向要求old v8 owners存在，必须改为新zero-ledger | Rewrite | WP-08 | production source/config/current-doc scan按new denylist green | Open |
| T12 | Galatea recap composition/config/progress/fresh-readiness tests | 两Host routes、progress、fresh lifecycle改用Grid | Rewrite | WP-07B | disposable Galatea vertical + route/progress/raw gates green | Open — WP-07B complete：actual service candidate 7/7（Fresh/NewRequest、real missing-work route、Prepared、Started、CLI equivalence、session/candidate cleanup fatal taxonomy）、Online 21/21、Hosting 19/19与CLI candidate 10/10 green；production caller切换仍属WP-08 |
| T13 | `GalateaDurableRecoveryVerticalTests` | frozen recovery invariant保留，derived deletion fixture改为Grid/control | Preserve | WP-07B/08 | Prepared byte-identical、Started Refuse、restart exact tests green | Open — candidate Prepared resumes with corrupt derived sentinel；Started Refuse零client/derived；production test retarget属WP-08 |
| T14 | CLI `run-online-turn` direct tests | baseline缺口由candidate command tests补齐；old production command仍保留 | Rewrite | WP-07B | fresh/NewRequest/Prepared/Started/head-drift direct vertical新增并green | Open — WP-07B complete的candidate direct vertical已与Galatea exact derived/request-boundary等价；Fresh/NewRequest保持corrupt old-v8 sentinel byte-exact inert，candidate source architecture gate不引用old DerivedRecap；production cut仍属WP-08 |

Test project/ref inventory：

```text
SessionJournal.HistoryTimeline.Tests (WP-01A/B/C HistoryLoad/partition/raw/durable/crash owner；in-memory ledger仅在此test assembly)
SessionJournal.HistoryTimeline.PublicSurface.Tests (无IVT external Create/Open/Reader/Dispose gate)
SessionJournal.HistoryTimeline.CrashHarness (WP-01C child-process transaction/locator/backup/restore crash gate)
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

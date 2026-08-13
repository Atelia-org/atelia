# DerivedRecap Grid Rewrite Migration Ledger

标识：`DRGRID-CUTOVER`

状态：WP-08 source implementation Complete，independent closure Closed；旧production callers/projects已切除，
全部durable/source/test entries均已Closed。containing commit仍是commit identity evidence；real-provider canary与
actual cyber activation保持外部`NotRun`，不改变ledger source closure。

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

WP-08完成后formal RecapGrid production consumers仍只有Galatea与`SessionJournal.Cli`。
`SessionJournal.csproj`继续只通过neutral candidate/lifecycle/raw-read contracts与Grid交互，不引用concrete
RecapGrid product。

| Root | Exact evidence | Current behavior | Cutover owner |
|---|---|---|---|
| Galatea read-only progress | `GalateaRecapGridReadiness.cs` | Getter Resolve；仅Unfulfilled时Manager InspectProgress；provider/build/write零触达 | formal WP-08 owner |
| Galatea fresh/NewRequest | `GalateaRecapGridComposition.cs` + `GalateaServices.cs` | per-turn Online + host-wide single `RecapGridCompletionHost` | formal WP-08 owner |
| Galatea Prepared/Started/ToolContinuation | `GalateaRecapGridComposition.cs` | frozen tool → current completion → Online；Prepared lazy零derived、Started先拒绝 | Preserve/formalize |
| CLI operator root | `Program.cs` -> `RecapGridCommands.cs` | stable Store + Timeline/Control/Cadence/build/progress/materialize/legacy-root | formal WP-08 owner + cadence A3 |
| CLI online Host | top-level `run-online-turn` -> `RecapGridOnlineTurnCommand.cs` | Fresh/NewRequest formal Online；Prepared/Started frozen gates | formal WP-08 owner |

Project-reference evidence：

```text
prototypes/Galatea/Galatea.Server.csproj
  -> formal HistoryTimeline/Control/Store/Manager/Getter/Runtime/Hosting/Online/AgentControl graph
prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj
  -> same formal public products; no old DerivedRecap project
```

### 3.1 Post-cutover cadence closure

| Slice | Durable/runtime disposition | Status |
|---|---|---|
| A0 | 新`SessionJournal.RecapGrid.Cadence`持有per-Ref canonical policy；`control/recap-grid/v1/refs/<ref>/cadence/`独立Linux durability、mutable-owner CAS、reader no-create | Closed — `0af28eea` + authority/durability fixes `397f2ab8`/`b0bce3b3` |
| A1 | B=60,000 partition保持first replay-safe；R=24,000由Cadence seal proof强制；Online/offline/CLI无public B-only bypass | Closed — `1e8ea927` |
| A2 | Getter/build-read以同一Cadence/Timeline authority选择latest R-eligible healthy fulfillment；typed `ReserveBootstrapRawOnly`；统一262,144 operation cap | Closed — `bac31986` |
| A3 | `recap-grid cadence inspect|set-reserve`；pure-read inspect与只改R的exact CAS，不提供修改B入口 | Source candidate — CLI product 0W/0E，新dense 1/1、CLI full 92/92 |

该post-cutover closure不重开旧migration entries，也不宣称C2 rolling built-in、C3 incremental capacity、C4 rollover/GC
或C5 actual cyber activation完成；这些边界继续由cadence/capacity audit跟踪。

Repo-wide root reproduction：

```bash
rg -n "SessionJournal\.DerivedRecap|DerivedRecap" prototypes src \
  -g '*.cs' -g '*.csproj' \
  --glob '!prototypes/SessionJournal.DerivedRecap.*/*'
```

当前production `.cs/.csproj` old namespace scan为零；Walking另锁old project/ref/IVT absence。

## 4. Durable, config and operational roots

这些root也是可关闭的ledger entries；`Governed by`指向下方symbol ledger，不建立第二套迁移authority。

| ID | Root | Baseline status/authority | Exact evidence | Disposition | Governed by | Target WP | Closure gate | Status |
|---|---|---|---|---|---|---|---|---|
| D01 | old `<repo>/derived/recap/v8/` | inert legacy slot | legacy-root manifest | Delete | L06/L07/L25 | WP-08 | normal零reader；explicit archive/delete | Closed — owner-bound legacy-root 11/11与deterministic disposable import canary green |
| D02 | old v8 locks/refs/building/published/quarantine grammar | retired layout | frozen baseline | Delete | L06/L07 | WP-08 | old grammar source/project scan zero | Closed — old product/tests deleted |
| D03 | old `<repo>/derived/recap/rebuild/v1/` | inert legacy slot | Manager restart test | Delete | L09/L25 | WP-08 | missing-only restart不创建spool；explicit archive/delete | Closed — restart 1/1 |
| D04 | old `<repo>/config/recap-planner-config.json` | inert legacy file | legacy-root manifest | Delete | L11/L25 | WP-08 | Hosts零reader；explicit archive/delete | Closed — formal config/Control paths only |
| D05 | Galatea `config.json` + `connections.json` | strict RecapGrid config + shared Completion registry | Galatea config/composition tests | Retarget | L17/L22 | WP-08 | one host owner；frozen exact registry | Closed — composition/readiness 9/9 |
| D06 | old `recapMaintainerConnections` | retired no-fallback mapping | strict route manifest | Delete | L22 | WP-08 | source/config scan zero | Closed — field/loader removed |
| D07 | configured call-log directory | unified agent+recap v9 operational evidence | `GalateaCompletionLogging.cs` | Retarget | L14/L19/L20/L22 | WP-08 | one decorator，not durable identity | Closed — single completion host owner |
| D08 | old `derived/recap/v4`至`v7` | inert legacy slots | legacy-root manifest | Delete | L25 | WP-08 | normal inert；explicit archive/delete | Closed — owner-bound legacy-root 11/11与deterministic disposable import canary green |
| D09 | `<repo>/control/recap-grid/v1/refs/<ref>/timelines/<timeline>/{control.json,lifetime.lock,writer.lock}` | WP-02 complete的单一Control authority；bounded canonical whole-state、双lock、exact Timeline binding；不在可reset的`derived` root | `SessionJournal.RecapGrid.Control/ControlRuntime.cs`、`ControlDurableFiles.cs` | Keep | L05/L11/L12 | WP-02/07A/07B/08 | 两个Hosts只读该carrier且old config零reader；backup/export/temp永不参与normal discovery | Closed — Control carrier与formal Hosts通过final solution 4658/4658及两路independent closure |
| D10 | `<repo>/derived/recap-grid/v1/{grid.sqlite,lifetime.lock}` | WP-03 complete的唯一Grid artifact/ref authority；SQLite canonical BLOB + validated locators/members/FK；Grid-only reset | `SessionJournal.RecapGrid.Store/SchemaV1.sql`、`StoreRuntime.cs`、`StoreMaintenance.cs` | Keep | L06/L07 | WP-03/04/05/07A/08 | Manager/Getter/Hosts只经public Store API消费；normal不扫描temp/orphan/old roots，reset不触碰raw/Timeline/Control | Closed — Store/Manager/Getter/Hosts与stable CLI通过final solution 4658/4658及两路independent closure |

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

WP-01C经C3D hard cut后的target root是`<repo>/derived/history-timeline/v2/`，exact inventory只有
`locks/<ref>.lock`、`refs/<ref>/locator.json`与`refs/<ref>/timelines/<timeline>.sqlite`。它不是old-root migration对象：normal
runtime只跟随canonical locator，不扫描old recap roots、orphan Timeline DB或backup；Grid reset也不处理它。single SQLite backend、
backup/restore/abandon与public factory/Reader已完成；C3D把selected path改为head-bound count/root commitment与O(log N)
append/truncate，删除累计row/trie/database byte lifetime caps。旧`derived/history-timeline/v1`bytes明确inert，normal path不读取、
fallback或迁移。V2 deployment必须将Cadence、Timeline、Control、Store作为四个durability domains显式重新provision；不得混用
V1 Timeline与current companion state。whole-generation rollback只允许在首次new raw write前；之后只能replay或forward-fix。
包含本次变更的containing commit作为commit evidence，不在提交前虚构hash。WP-08 source cutover已完成并关闭deletion entries；
actual cyber activation仍由外部gate决定。
这里的exact inventory只列normal canonical slots：crash可留下unreferenced Timeline SQLite、dot-temp或exact SQLite rollback journal；它们
不参与normal discovery/latest选择，只能由SQLite recovery或后续explicit inventory/retention action处理。V2 durable lease/fsync为
Linux-only，其他platform返回typed unsupported，不提供弱durability fallback。

## 5. Source and production ledger

| ID | Legacy symbol/path | Current callers/evidence | Behavior/invariant worth preserving | Target owner | Disposition | Target WP | Closure/deletion gate | Status |
|---|---|---|---|---|---|---|---|---|
| L01 | `SessionHistoryPlanning*`、`SessionCurrentLineagePrefix`、bounded window/setup proofs | `SessionHistoryPlanning.cs:9-724`；`SessionJournalReadView.cs:52-172`；old Planner campaign/rebuild consumers | raw selected Parent lineage、bounded planning window、setup proof、typed BeyondPrefix | SessionJournal raw API + HistoryTimeline consumer | Preserve | WP-01A/B | 原raw/lineage/setup tests green；Timeline不复制raw authority | Closed — WP-01 complete：Timeline 156/156、raw audit 19/19、walking architecture 13/13、public surface 2/2、solution 0W/0E、docs 15/0、diff clean、两路independent GO；containing commit为commit evidence |
| L02 | `ICoherentContextCandidateSource`、`ISessionContextLifecycleCoordinator`、candidate/contribution contracts | `SessionContextCandidateContracts.cs`；Engine与两个Hosts | neutral selection/materialization、strict status、bounded contributions、SessionJournal-owned raw-tail fold | SessionJournal core + neutral Grid adapter | Preserve | WP-05/07B/08 | formal adapter/lifecycle通过neutral contract；SessionJournal无concrete Grid ref | Closed — CLI/Galatea formal Online使用同一Getter + composite lifecycle；Walking 25/25 |
| L03 | Prepared/request reconstruction：`SessionCoherentRequestRecipe`、`SessionPreparedRequestReconstructor`、snapshot/request hashing、runtime recovery | SessionJournal Engine/audit/recovery；Galatea/CLI frozen branches | Prepared bytes自足；Started Refuse；frozen completion identity exact bind；不重新读取active derived state | SessionJournal core + Host registry | Preserve | WP-05/07B/08 | Prepared/Started matrix byte-identical、零active Grid/control/current-route read | Closed — formal CLI/Galatea保持outer frozen gates，old derived composition已删除 |
| L04 | `HistoryLoadUnit`、`IHistoryUnitLoadEstimator`、`RecapHistoryLoadProjector`、`O200kBaseHistoryUnitLoadEstimator` | baseline：old Planner三文件及Planner/CLI/Galatea defaults；WP-01A：`SessionJournal.HistoryTimeline/HistoryLoadContracts.cs`、`HistoryLoadProjector.cs`、`O200kBaseHistoryUnitLoadEstimator.cs` | provider-neutral HistoryLoad identity、framing、bounds、goldens；不等同provider token | HistoryTimeline | Move | WP-01A | estimator/projector 25-case baseline与new Timeline tests green；old Planner declaration/path与duplicate `EstimatorId` scan zero | Closed — final tail Timeline 54/54、Planner 42/42、solution build 0/0，independent gate P0/P1=0 |
| L05 | old `DerivedRecap.Abstractions` epoch contracts | frozen baseline | Updated/Keep、lazy binding、runtime identity不进durable identity | RecapGrid.Abstractions + Manager batch seam | Rewrite | WP-02/04/06/08 | formal contracts green；old namespace production zero | Closed — old project deleted，formal Manager/Runtime only |
| L06 | v8 wire/layout | frozen baseline | strict canonical/hash/bounds作为behavior参考；epoch/layout不保留 | RecapGrid.Store | Delete | WP-03/05/08 | old schema/type/path source zero | Closed — old Store/tests deleted；formal Store/Getter retained |
| L07 | old epoch Store/repair/reseal/filesystem | frozen baseline | immutable commit/crash/read bounds保留，Building/Published语义删除 | SQLite RecapGrid.Store | Delete | WP-03/04/05/08 | formal Store/Manager/Getter green；old owner/tests removed | Closed — deletion complete；final solution 4658/4658 green |
| L08 | old context candidate source | frozen baseline | exact lineage/ordinal/fences/no fallback | RecapGrid.Getter + neutral adapter | Rewrite | WP-05/08 | formal branch/Nth/head/raw-tail matrix；old source zero | Closed — old Store project deleted；formal Getter caller active |
| L09 | old rebuild spool/root | frozen baseline | explicit rebuild authority；无durable campaign | recipe + Manager missing query + CLI | Delete | WP-04/07A/08 | restart E2E zero spool；legacy archive/delete | Closed — Manager restart 1/1，old project/tests deleted |
| L10 | old complete-roster Planner/kernel/lifecycle | frozen baseline | budget/drain/missing-only/safe lifecycle | Manager + Runtime + Online | Rewrite | WP-04/05/06/08 | formal matrices green；old symbols zero | Closed — old Planner/Runtime projects deleted |
| L11 | old Planner v3 config/path | frozen baseline | strict config、active/frozen separation | Control + Hosting manifests | Delete | WP-02/07A/07B/08 | Hosts zero old reader；legacy file inert | Closed — old loader/callers deleted |
| L12 | old family/maintainer catalog/built-ins | frozen baseline | family/member semantics；route不进identity | canonical Family/Definition + AgentControl assets | Rewrite | WP-02/06/07C/08 | formal hash/renderer/parser/operator gates | Closed — old Maintainers project deleted |
| L13 | 被嵌入的family/autobiographical/world-understanding rewrite prompt assets | historical Maintainers embedded resources | 不建立implicit default roster；需要的built-in必须显式canonical asset/operator provision | AgentControl built-in catalog + Control definitions | Delete | WP-02/06/07C/08 | old resources/logical names零引用；normal Host不auto-provision | Closed — old project/resources已删除；formal built-in只经explicit operator/tool path |
| L14 | old execution lane/runtime group/bound maintainer | frozen baseline | affinity/scheduler/logging与semantic identity分离 | unique RecapGrid.Runtime executor | Rewrite | WP-06/08 | formal runtime green；old owner zero | Closed — old Runtime project deleted；one formal runtime retained |
| L15 | Galatea old epoch progress DTO | frozen baseline | read-only/no-create/no-secret、same-head/stale fences | formal `RecapGridReadiness` | Retarget | WP-07B/08 | formal DTO/UI no old fields | Closed — Getter/Manager-backed readiness active；12/12 |
| L16 | Galatea fresh/NewRequest old composition chain | frozen baseline | safe-boundary maintenance + context | formal `GalateaRecapGridComposition` | Keep-connected-until-WP08 | WP-07B/08 | public/default formal and old lifecycle zero | Closed — formal single owner active；old composition/candidate seam deleted |
| L17 | Galatea frozen completion branch | formal composition | exact frozen bind、Started pre-client refusal | Galatea Host recovery | Preserve | WP-07B/08 | Prepared/Started tests green，zero derived open | Closed — formal phase order retained |
| L18 | old CLI history-load/materialize commands | frozen baseline | read-only HistoryLoad、strict Nth、bounded report | Timeline CLI + Getter | Retarget | WP-01A/05/07A/08 | formal command/tests green；old source removed | Closed — HistoryLoad 3/3；formal materialize in CLI 13/13 |
| L19 | old CLI recap command tree | frozen baseline | confirmation/scope/read-only/operator safety | formal `recap-grid` tree | Keep-connected-until-WP08 | WP-07A/08 | old commands deleted；formal matrix green | Closed — nested candidate/old command files deleted；CLI 13/13 |
| L20 | CLI old online Host | frozen baseline | second Host shares Manager/Getter/Hosting exact routes | formal top-level `run-online-turn` | Keep-connected-until-WP08 | WP-06/07B/08 | formal Fresh/NewRequest/frozen gates；old refs zero | Closed — `RecapGridOnlineTurnCommand` active |
| L21 | CLI frozen recovery branch | formal online command | frozen exact bind、Started refusal、Prepared zero active read | CLI Host recovery | Preserve | WP-07B/08 | Prepared/Started tests green | Closed — formal direct command retained |
| L22 | old recapMaintainerConnections/default route | frozen baseline | exact route、no fallback、route不进identity | `RecapGridCompletionHost` route registry | Retarget | WP-06/07B/08 | strict manifest；old field/loader zero | Closed — one registry owner，old mapping deleted |
| L23 | 五个old `SessionJournal.DerivedRecap.*` projects、solution/project refs、旧test IVT | frozen baseline/archive branch | 只保留真正provider-generic/raw primitives，迁准确owner | new Timeline/RecapGrid projects | Delete | WP-08 | projects/solution/refs/IVTs清理且zero-ledger gate green | Closed — 五products、四test/harness、solution/project refs与IVTs已删除；Walking 25/25；final solution 4658/4658且independent closure green |
| L24 | current DerivedRecap docs、SessionJournal/Galatea/CLI README、old active shared-epoch plan | current docs + archive evidence | raw authority、recovery与operator safety保留；旧architecture/layout文案不保留 | current Grid docs + archive evidence | Rewrite | WP-08 | current docs只描述Grid；历史snapshot/old plan归档；docs checker green | Closed — current docs与READMEs已formalize，old plan归档；scoped docs checker green；all-tracked与fresh clone留作provisional containing commit后的identity evidence |
| L25 | inert seven slots：`derived/recap/v4`至`v8`、`rebuild/v1`、old config；v9 forbidden | formal legacy-root command/tests | normal runtime不得scan/mutate/fallback；cleanup必须explicit exact-confirm | `recap-grid legacy-root inspect|archive|delete` | Delete | WP-08 | mutable owner、Idle、whole raw authority、wrong/drift/symlink/v9/FIFO/crash/partial retry与source unchanged green | Closed — formal legacy-root 11/11；V2 archive提交branch/ref/raw，8 GiB累计cap在hash前检查，foreign/stale authority零mutation；deterministic disposable import canary green |
| L26 | Agent-facing recap control tools、built-in genesis与ToolResult/ToolContinuation | formal AgentControl + CLI/Galatea | admission-bound canonical control mutation、operation receipt settlement、safe post-tool lifecycle | AgentControl + Control V2 receipts + Online composition | Rewrite | WP-07C/08 | production callers用formal binding且candidate seam零 | Closed — CLI/Galatea callers已formalize并移除candidate naming/branch；final solution与independent closure green |

## 6. Test migration ledger

| ID | Current tests/project | Preserved behavior or disposition reason | Disposition | Target WP | Closure gate | Status |
|---|---|---|---|---|---|---|
| T01 | SessionJournal raw/planning/audit/tail/Prepared/neutral tests | raw lineage/setup/range commitment、tail fold与frozen contracts | Preserve | WP-01/05/07B/08 | 持续green；SessionJournal无concrete Grid dependency | Closed — formal Hosts只经neutral/lifecycle seams；Walking 25/25 |
| T02 | historical `DerivedRecapPreparedRecoveryIntegrationTests` | 保留“Prepared后derived state不可用仍exact frozen bind”，替换v8 fixture | Rewrite | WP-07B/08 | formal CLI/Galatea Prepared/Started gates green；old test/ref删除 | Closed — old test删除，formal Host recovery tests保留；final solution green |
| T03 | historical v8 codec tests | v8 wire/layout-specific | Delete | WP-08 | WP-02/03 canonical codec/schema tests先green | Closed — old project/tests删除；new canonical owners保留 |
| T04 | old epoch Store/crash tests | immutable/crash/reset/strict read迁到SQLite/Grid；Building/Published删除 | Rewrite | WP-03/05/08 | formal Store/Getter tests green；old tests removed | Closed — old project/tests deleted |
| T05 | historical rebuild spool tests与old CrashHarness modes | durable spool/campaign被删除 | Delete | WP-03/07A/08 | new DB crash harness + full rebuild restart E2E green | Closed — old tests/harness删除；Manager partial→dispose→reopen仅补missing rows且zero legacy spool 1/1 |
| T06 | baseline `O200kBaseHistoryUnitLoadEstimatorTests`、`RecapHistoryLoadProjectorTests`；current `SessionJournal.HistoryTimeline.Tests` | canonical HistoryLoad goldens | Move | WP-01A | 25-case baseline保留并green；old Planner test owner removed；新增partition/contracts/codec strict matrix | Closed — Timeline 54/54、skeleton 12/12、Planner 42/42、docs 15/0，independent gate P0/P1=0；CLI baseline-equivalent，Galatea保留既有debt与route timeout flaky |
| T07 | old Planner campaign/kernel/rebuild/lifecycle/config tests | budget、drain、missing-only、安全lifecycle重写；epoch mechanics删除 | Rewrite | WP-04/06/07/08 | formal Manager/Runtime/Online matrices；old tests removed | Closed — restart missing-only 1/1补充WP-08 evidence |
| T08 | old Maintainer family/output/runtime binding tests | semantics/parser/lane行为迁formal definitions/runtime | Rewrite | WP-02/06/08 | formal definition/renderer/parser/scheduler tests | Closed — old project/tests deleted |
| T09 | `ProgramRecapHistoryLoadCommandTests` | operator surface迁到Timeline子树，数据源为Timeline owner | Retarget | WP-01A/07A/08 | `recap-grid timeline history-load inspect` deterministic green | Closed — HistoryLoad CLI 3/3；tools迁HistoryTimeline且node 6/6 |
| T10 | historical v8 command tests | 改为formal Timeline/Control/Grid commands | Rewrite | WP-07A/08 | confirmation/read-only/reset/build/activate E2E green | Closed — old tests删除；formal CLI 13/13、legacy-root 11/11 |
| T11 | historical cutover boundary test | old-owner positive gate改formal zero-ledger | Rewrite | WP-08 | source/config/current-doc denylist green | Closed — Walking formal absence/graph gates 25/25 |
| T12 | Galatea composition/config/progress/fresh-readiness tests | routes、progress、fresh lifecycle改formal Grid | Rewrite | WP-07B/08 | formal Galatea vertical + readiness gates | Closed — formal composition/readiness 9/9；old composition tests deleted |
| T13 | Galatea durable recovery vertical | frozen recovery invariant保留，derived fixture改Grid/control | Preserve | WP-07B/08 | Prepared byte-identical、Started Refuse、restart exact | Closed — formal frozen gates retained；final solution green |
| T14 | CLI `run-online-turn` direct tests | formal production command保留Fresh/NewRequest/Prepared/Started/head-drift behavior | Rewrite | WP-07B/08 | top-level formal command与Galatea exact derived/request-boundary等价 | Closed — candidate nesting已删除；formal CLI 13/13与final solution green |

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

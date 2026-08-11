# DerivedRecap Grid WP-08：Atomic Production Cutover 与 Legacy Deletion

状态：Complete / independent closure Closed；production caller、formal operator surface与legacy owner deletion均已完成。
real-provider canary与actual cyber repository activation仍是外部`NotRun` gate，不属于source closure证据。

只需加载：目标设计、总计划、WP-07B handoff、WP-07C handoff、本文、current architecture map与migration ledger。

## Intent

在一个原子工作包中将Galatea/CLI/current docs切到Grid链，并删除旧DerivedRecap complete-roster epoch实现。旧roots
对新runtime保持inert，只能由offline exact-confirm procedure归档/删除；reset/rebuild只针对new Grid。不保留reader、
migration、feature flag或compatibility layer。

## Preconditions

- WP-01..07C commits与handoffs完整；
- WP-05 Getter/neutral phase2 contract取得independent GO，且old candidate source只在本包原子切换时删除；
- frozen pre-grid branch/tag存在且clean；
- migration/deletion ledger无Unknown；
- WP-03 Store V1 Linux-only contract、exact Grid-only reset root与Busy/Invalid/Disposed/CommitIndeterminate mapping已被WP-04..07消费者保持；
- target/current config与operator actions已审阅；
- disposable vertical candidate通过；
- WP-07A Hosting strict bounded route/connections、lazy bounded evidence与Runtime-before-registry drain合同已取得independent GO；
- WP-07B已证明两个candidate Hosts复用同一owner且没有旧unbounded config/fallback旁路；WP-07C已补齐Agent-facing
  Control、built-in provision与ToolResult/ToolContinuation，并取得两路independent GO；
- exact cutover HEAD重新盘点，无并行uncommitted writer。

WP-04..07C的旁路已作为本包输入。WP-08已把CLI/Galatea caller切到formal products并删除legacy
owners；source implementation与independent closure均已完成。是否对actual cyber repository执行operator
activation仍由外部部署gate决定，不能由source tests替代。

## In scope

- normal Galatea/CLI composition切新Timeline/Grid/ControlPlane/Manager/Composer；
- normal composition只经`HistoryTimelineFactory.Create/Open(SessionJournalReadView, ...)`与public Reader/Coordinator capability；
  不引用SQLite/internal ledger、不注入backend selector，并在Host lifecycle结束时dispose handle；
- normal Control composition只经`RecapGridControlFactory.Create/Open/OpenReader(repositoryPath, RefId, ...)`；不得注入Timeline Reader、
  backend selector或扫描old config。Control canonical root位于`<repo>/control/recap-grid/v1/`，与Grid reset root分离；
- strict new config与operator messages；
- 已交付的stable store-only `recap-grid inspect|export|verify|reset --prepare/reset`命令名在cutover后继续保留；WP-08只接入完整
  composition与删除legacy命令，不重命名或另造第二组Grid maintenance surface；
- Prepared/Started fast path保持先于DerivedRecap active composition；Prepared按frozen identity exact bind Host registry，Started
  Refuse先于client creation；
- old derived roots检测策略：新production不读取；旧roots只由offline exact-confirm procedure归档/删除，operator只对new
  Timeline/Control/Grid执行其各自明确的restore/abandon/reset/rebuild；
- 删除old Store/Planner/Runtime/Maintainers **legacy owners/symbols**；若WP-06复用了同名project，只删除epoch-era代码并以
  architecture gate证明新runtime无legacy dependency，不能按项目名误删；
- 新owner的exact项目名是`SessionJournal.RecapGrid.Runtime`；WP-08删除的是old
  `SessionJournal.DerivedRecap.Runtime`及epoch-era callers，不得误删新product。cutover后仍只能保留唯一Manager scheduler与exact
  deferred route resolver，不得留下old/new runtime switch或implicit agent-route fallback；
-删除old v8/v9 wire/codecs/paths/repair/reseal/spool/config/tests/docs；
-更新solution、README、current architecture/concepts/config/host docs；
- archive已完成target/plan/handoff evidence。
- SessionJournal Prepared codec、`SessionCoherentRequestRecipe`、snapshot hashing、raw request reconstruction、uncertain completion
  policy与tool continuation明确out of scope/zero-diff，只有其derived-candidate adapter允许替换。

## Cutover ordering

1. fresh inventory与stage plan；
2. Prepared/Started recovery gate先锁green；
3. composition/config/CLI一次切换；
4. migration ledger逐项删除legacy callers/owners/tests；
5. architecture zero-ledger扫描；
6. focused -> affected full -> solution -> disposable vertical -> fresh checkout；
7. docs/current/archive同步；
8. 单一atomic commit或内部临时commits最终squash为可审阅cut。

### Exact caller switch/delete map

| Host | Current production caller | 已验证candidate caller | WP-08原子切换 | 切换后删除 |
|---|---|---|---|---|
| CLI | historical `OnlineTurnCommand`与old `recap`子树（已删除） | WP-07A/07C candidate command（已formalize） | `Program.cs`唯一顶层`run-online-turn`直达`RecapGridCommands.RunOnlineTurnAsync`；`recap-grid`统一Store/Timeline/Control/build/progress/materialize/legacy-root | old callers、old `recap` owners和nested `candidate`层均已删除；formal Hosting/Online/AgentControl保留 |
| Galatea | historical `GalateaRecapComposition`与direct connection-registry DI（已删除） | WP-07B/07C internal candidate composition（已formalize） | `Program`只构造一个`RecapGridCompletionHost`，`GalateaHostService`唯一拥有`GalateaRecapGridComposition`；frozen tool → current completion → Online顺序保留 | old composition、candidate naming/branch与双registry均已删除 |

删除顺序必须是“production caller已切且同一behavior矩阵green”之后再删candidate shim/legacy owner，不能先删测试旁路再猜测正式composition。

## Legacy zero-ledger

最终production命中必须为零（以WP-00 fresh inventory实际名称扩充）：

- old `DerivedRecapEpoch*`/complete-roster/Published repair/reseal types；
- old Store/Planner/Runtime/Maintainers composition APIs；
- old durable root/schema/handle/config字段；
- old `recap run/rebuild`语义与layout-specific tests；
- compatibility decoder、fallback、auto-migrate、dual-write；
- candidate-only WP-07A/07B entry/feature switch。

允许保留的只有真正provider-generic Completion/HistoryLoad/raw-lineage primitives，且必须迁到准确owner并证明无legacy type
依赖；不能仅改名逃过zero-ledger。

architecture scan覆盖production `.cs`、`.csproj`、config、`docs/SessionJournal/current`、CLI/Galatea READMEs；明确排除
archive/evidence与migration ledger自身。current `host-integration`若与exact源码不符只算documentation drift，不能当保留API证据。

## Validation matrix

### Safety/authority

- Grid inspect/reset/rebuild这些derived-only窗口中，raw selected lineage和non-derived files exact不变；普通Galatea turn与最终
  control carrier按正式契约写各自authority，不套用错误的全E2E raw不变断言；
- Timeline/control survive Grid reset；
- old sidecar bytes inert，不被normal path扫描/修改；
- abandoned Timeline DB、backup与orphan同样inert；normal path只跟随exact per-Ref locator，不按mtime/目录枚举找latest；
- wrong confirmation/reset target零mutation；
- Prepared/Started删除DerivedRecap active config/Grid后仍按frozen identity恢复成功。
- old v8 sidecar即使corrupt也inert，normal new path不scan/mutate；
- old `derived/recap/v8`、rebuild spool与old config不由new Grid reset处理；cutover保留为inert bytes，并提供独立offline
  exact-confirm archive/delete procedure。normal path与Grid commands不得auto-delete或把它们当fallback；

### Behavior

- normal row fill、overlay、full rebuild、A/B、mystery analysis；
- context exact view + raw tail；
- parallel family/prefix/cache；
- restart/missing resume、whole-grid invalid/reset/rebuild；
- branch/rewind/head drift；
- strict `NthPrevious` exact predecessor，不跳missing/damaged slot；
- fresh Idle、AwaitingAgentAction、Prepared exact resume、Started Refuse、Started explicit restart、ToolExecutionStarted边界；
- CLI/Galatea reports与operator actions。

### Build/repo

- affected project tests与solution build 0 warnings/errors；
- crash harness与disposable Galatea vertical；
- docs checker scoped 0 diagnostics；最终 `--all-tracked` 必须为0；未提交移动产生的
  临时source-missing只能记录为中间态，不能算Done证据；
- `git diff --check`；
- fresh checkout重跑核心gate；
- production source/config/docs old-symbol/path `rg`为零。
- current architecture map、concepts、durable-target、history-load、planner-config、host-integration、operations staging、
  SessionJournal.Cli/Galatea READMEs全部更新；old shared-epoch active plan归档。

## No-Go

- 为赶cutover保留old/new runtime switch；
- legacy reader或automatic migration；
- old tests先删而新behavioral invariants尚未覆盖；
- current docs称新production但default composition仍旧；
- full suite失败被focused green掩盖；
- fresh clone依赖本地untracked DB/config/secret。

## Done when

- normal production只有新Grid链；
- migration/deletion ledger全部Closed；
- old implementation与candidate entry删除；
- old roots有明确且测试过的offline archive/delete procedure；new Grid有reset/rebuild operator flow；
- all gates green或外部canary明确environment-blocked；
- current docs成为唯一导航，计划与旧设计材料按治理规则归档；
- worktree clean，cutover commit与rollback baseline可定位。

## Implementation completion record（2026-08-12）

本轮以`archive/pre-wp08-cutover-20260811 = 804bc551`为rollback baseline，已完成：

该baseline只提供source rollback定位：在cutover后首次新raw event写入前，可整体回退
code/derived state；一旦已有新raw event，禁止把repository raw authority回滚到旧副本，
只能保留raw并用forward fix/明确兼容审计恢复。任何rollback都不得用旧derived sidecar覆盖raw。

- CLI stable surface formalize：`recap-grid`统一scaffold/Store/Timeline/Control/build/progress/materialize/legacy-root，
  顶层唯一`run-online-turn`走formal Online；HistoryLoad迁到`recap-grid timeline history-load inspect`；
- Galatea single-owner cut：strict RecapGrid config、one `RecapGridCompletionHost`、formal readiness DTO与
  Prepared/Started/ToolContinuation phase order；
- provider-free create-only `scaffold`（Control admission + AgentControl profile + Hosting route manifest）、
  `control compose-full-recipe`与owner-bound seven-slot legacy-root
  inspect/archive/delete；archive V2 manifest提交selected branch/RefId/raw head，mutation要求
  Idle mutable owner与fresh whole authority；normal path对old roots inert；
- 删除五个`SessionJournal.DerivedRecap.*` products、四个old test/harness projects、old callers、refs、IVTs与
  solution entries；HistoryLoad tooling迁到HistoryTimeline；
- current architecture/concepts/durable/config/HistoryLoad/host docs改为formal Timeline/Control/Store链；
- restart evidence新增：partial build后dispose全部Manager handle，reopen仅补exact missing rows，第三次0 calls，
  全程不创建legacy rebuild spool。

Focused evidence：Walking 25/25；CLI formal 13/13；legacy-root 11/11（与Walking独立计数，含四个child crash窗口、
8 GiB hash-before-cap、Busy/ref/raw/non-Idle/drift、foreign branch、FIFO/symlink、publication settlement与partial retry）；Galatea strict config
7/7、readiness direct 2/2、ready→fulfillment-missing→reset→frontier vertical 1/1；HistoryLoad CLI 3/3；
Galatea composition/readiness 9/9；scaffold CLI 4/4、Galatea strict scaffold load并入config 8/8；Manager restart 1/1；HistoryTimeline tool node fixtures 6/6；scoped docs
checker 14/0。`--all-tracked`在未commit source move期间允许报告待提交的source-missing，
但containing commit后必须重跑为0；不能把中间诊断列作最终允许残留。historical missing-target为0。

Final source evidence：`Atelia.sln`串行build为0 warning / 0 error；第二轮solution test完整退出0，36个test projects
合计4658/4658 green；package vulnerability audit退出0且所有项目无已知漏洞；两路independent closure最终均为
P0=0/P1=0。deterministic disposable legacy-import canary已完成，未调用真实provider，也未写入actual cyber repository。

外部`NotRun` gate保持不变：real-provider authenticated canary与actual cyber repository的provision/compose/activate
均未执行。它们不得被上述deterministic证据冒充；同样不得改变`archive/pre-wp08-cutover-20260811 = 804bc551`
的rollback边界：首次new raw write前才允许整体source/derived rollback，之后只能保留raw并forward-fix。

fresh no-local checkout已在provisional containing source tree `913fd8fa`上完成：全新restore成功，Release
solution build为0 warning / 0 error，36个test projects合计4658/4658 green，package vulnerability audit零命中，
scoped docs 14/0、all-tracked docs 71/0，checkout status clean。最终containing commit只在该source tree上回写本段
fresh evidence；没有再改变product/test source。

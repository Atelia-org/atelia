# DerivedRecap Grid WP-08：Atomic Production Cutover 与 Legacy Deletion

状态：Planned；只有 WP-07A/07B Go 后才可开始

只需加载：目标设计、总计划、WP-07B handoff、本文、current architecture map与migration ledger。

## Intent

在一个原子工作包中将Galatea/CLI/current docs切到Grid链，并删除旧DerivedRecap complete-roster epoch实现。旧roots
对新runtime保持inert，只能由offline exact-confirm procedure归档/删除；reset/rebuild只针对new Grid。不保留reader、
migration、feature flag或compatibility layer。

## Preconditions

- WP-01..07B commits与handoffs完整；
- frozen pre-grid branch/tag存在且clean；
- migration/deletion ledger无Unknown；
- WP-03 Store V1 Linux-only contract、exact Grid-only reset root与Busy/Invalid/Disposed/CommitIndeterminate mapping已被WP-04..07消费者保持；
- target/current config与operator actions已审阅；
- disposable vertical candidate通过；
- exact cutover HEAD重新盘点，无并行uncommitted writer。

WP-04 Manager旁路实现已complete，但旧Planner/Runtime/Maintainers、Galatea与CLI callers均未切换。它必须与旧
production并存至WP-07 vertical Go；WP-08才在同一atomic cut中切composition并删除legacy owners，
不得提前把candidate presence解释为current production cutover。

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
- docs checker scoped 0 diagnostics；all-tracked仅允许另有owner的known archive findings；
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

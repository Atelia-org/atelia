# DerivedRecap Grid WP-07B：Galatea 与 Online Hosts Vertical Candidate

状态：Complete；两路independent closure均GO，final serial gates green；current production未切换

只需加载：目标设计、Master、WP-07A handoff、本文与WP-08摘要。

WP-06 complete handoff已把真实Completion执行限定在唯一`IRecapCellBatchExecutor`实现中。两个Host只能在composition root提供exact deferred
route resolver与owned provider-neutral invoker；动态column按Family/Runtime/Semantic三元key解析，missing/null semantic均无fallback。
Prepared/Started frozen recovery仍必须早于resolver/client construction，fresh lifecycle由Manager call budget与Runtime started-call accounting
共同证明，没有warmup或retry。Host registry必须显式区分Owned/Borrowed client；关闭顺序固定为先drain/dispose Runtime，再释放由
registry持有的borrowed clients，不能由route隐式猜测ownership。

WP-07A candidate已建立独立`SessionJournal.RecapGrid.Hosting`作为这段composition的唯一owner：strict bounded route manifest提交
exact `(FamilyDigest, RuntimeProtocolId, SemanticModelId?)`，resolver只`TryGet/GetClient`且没有default/fallback，Runtime route借用
registry-owned client；Completion connections同样由Hosting strict bounded reader冻结，旧unbounded `LoadFile`不得成为active Host旁路。
Galatea与CLI `run-online-turn`都必须直接复用该Hosting owner；不得复制manifest、resolver、telemetry或
Runtime lifetime。关闭顺序固定为Runtime drain/dispose在前、registry distinct clients best-effort dispose/rethrow在后；两个Host只读取
Hosting的bounded operational evidence snapshot，no-work不materialize collector，也不另建scheduler/logger。WP-07A真实blocking-client
integration已证明in-flight Runtime operation settle前Host与registry client均不会提前完成/dispose；cleanup fatal taxonomy必须继续与Runtime一致，
不能把OOM/SO/AV包装成Aggregate或继续执行后续cleanup。

## Intent

以明确candidate composition验证Galatea及CLI `run-online-turn`两个Host的fresh/new-request lifecycle、主线context、动态
Maintainer与frozen completion recovery；仍不切production default。

WP-04 complete Manager只证明同一frozen request重复进入的幂等性；本包在两个Host上验证真实
Idle/pre-observation、ObservationAccepted与NewRequest lifecycle。ToolResultObserved/ToolContinuation及Agent-facing Control由
WP-07C承接，不能用WP-04 fake/runtime fixture或本包operator fixture冒充。Mystery candidate已经证明base active、
`XSuspicion` overlay bootstrap reuse、future normal-row interaction与candidate build不提前切active；本包仍须用真实lifecycle和
explicit promotion重演该行为。

## In scope

- fresh/NewRequestRequired必须由Host组合成一个明确的composite lifecycle：`Timeline reconcile/seal -> Manager fulfill -> Getter
  readiness/candidate -> SessionJournal raw-tail composition`。Getter只是pure-read readiness与neutral candidate source，不驱动Timeline、
  不执行Manager build，也不把自身的lifecycle adapter误称为完整maintenance lifecycle；
- composition root经`RecapGridContextFactory.Open(SessionJournalReadView)`取得同一owned Getter handle，并直接把它注册为neutral candidate
  source；Host只注册包含Timeline reconcile/seal、Manager fulfill和Getter pure-read readiness调用的composite coordinator作为lifecycle。
  Getter handle的readiness面只是该composite内部一步，不能单独冒充Host lifecycle；raw-only路径不得预开Store，Selected路径不得在
  select/materialize间reopen Store；
  raw-only同时锁两条规则：no-active不论Timeline empty/nonempty均raw-only；Timeline empty即使active也raw-only以允许首row seal。
  Timeline nonempty + active missing fulfillment必须保持NotReady/Unfulfilled；
- Prepared/Started：在active composition之前走frozen path，零Timeline/Grid/DerivedRecap active/control/current route config读取；
  Prepared仍按frozen completion identity从Host registry exact bind；Started Refuse在binding/client creation前返回；
- C3C把lifecycle收口为Grid-first one-row pass：只有`PreObservation`在无既存Grid debt时可seal一条Timeline row；
  `ObservationAccepted`与`ToolResultObserved`只恢复既存Grid debt、绝不seal，必须保留SessionJournal-owned raw tail。两个Host在同一外部
  请求内只对typed `MaintenanceContinuation`做bounded catch-up，Ready之前不构造main-agent client；
- built-in/operator genesis本包只复用WP-07A canonical provision fixture与正式Control factories；没有声称Agent tool已经完成，也不在normal
  Host缺state时auto-create；
- route按allow-listed family key，dynamic column无需每列静态connection mapping；
- 两Host progress与operator messages。

## Acceptance matrix

1. built-in genesis、normal multi-row fill；NoBuild/missing-free零maintainer client；
2. same row multiple family leaders bounded overlap，共享prefix family内1 Leader + followers；
3. `XSuspicion` overlay追平前不live，激活后future rows影响`CulpritHypothesis`；
4. full rebuild从Row0 wavefront传播，partial row/view不泄漏；
5. restart missing-only、sibling failure/cancel/drain、budget zero-overrun；
6. fresh Idle、AwaitingAgentAction无Prepared与ObservationAccepted recovery幂等；ToolResult/ToolExecutionStarted属于WP-07C；
7. Prepared删除SQLite Grid、改变active recipe/删除DerivedRecap config后byte-identical resume，frozen connection仍exact bind；
8. Started Refuse零client/零derived write；explicit restart从frozen bytes建立new attempt；
9. ToolExecutionStarted existing operation/sequence语义本包零改；Galatea若仍unsupported必须显式保持，其
   Grid lifecycle接续验收属于WP-07C；
10. strict NthPrevious、off-lineage rewind、select/materialize head/promotion drift；
11. old v8 sidecar present/corrupt完全inert；
12. derived-only Grid rebuild/reset窗口证明raw selected lineage和all non-derived files不变。普通Galatea turn/control action按其
    正式carrier允许写raw/control，不能用错误的“整个E2E raw不变”断言。

## No-Go

- Prepared/Started先打开active Store/config；
- Galatea和CLI online使用不同Manager/Composer语义；
- Main context自行拼raw tail，绕过SessionJournal candidate/materialization contract；
- candidate composition与current default通过长期flag共存。

## Done when

两个Host disposable vertical、missing-work build、recovery、route/cache、raw authority gates green；reviewer批准本包
handoff给WP-07C。WP-08必须继续等待WP-07C GO。

## Implementation record（2026-08-11）

- 新增provider-neutral `SessionJournal.RecapGrid.Online`：factory绑定mutable `SessionJournalEngine` exact read view，eager owned
  Timeline+Getter、active-unfulfilled时才lazy Manager，borrowed executor；同一handle直接提供Getter candidate source和composite lifecycle，
  Dispose drain等待in-flight build后再释放Store/Control/Timeline leases；
- mutable owner新增lifecycle-scope-only `CaptureSelectedLineageAuditSnapshot`。现有read-only `BeginSelectedLineageAudit`规则不变；capture
  与mutation owner、exact head、single capture、cap/cap+1、cancel/failure后的exhaustion均由typed tests锁定；online
  `OfflineBootstrapRequired`只经该一次性bounded audit完成，不scan orphan或复制raw reducer/hash。`AuditContext`拥有共享snapshot，
  每个owner-bound cursor只释放自己的enumerator/lease，因此同一capture可先offline reconcile、再从共同ancestor继续bounded suffix build；
  ordinary read-only offline cursor仍保留原先的独占snapshot ownership；C3C移除旧的Online可配row-count surface，改为每pass最多
  seal一row，并以零提交terminal probe区分Ready与typed continuation；
- Hosting新增单一`RecapGridCompletionHost`，main agent和Recap Runtime共用同一strict registry；agent exact inspect/bind与Prepared exact bind
  不经default fallback，route manifest只在首个recap work加载，关闭顺序固定Online/request handles -> Runtime drain -> registry distinct clients；
- CLI新增可删除candidate入口`recap-grid candidate run-online-turn`；Started/Refuse在connection/route/client之前终止，Prepared只按frozen
  identity bind，fresh/NewRequest才打开Online；旧top-level `run-online-turn`与`recap`production行为未改；
- Galatea只增加internal/test-only candidate constructor/composition，public constructor和Program DI/default仍exact旧production。
  Fresh/NewRequest每turn持有Online；Frozen Prepared沿最外层frozen request，不打开Online/route；Started/Refuse保持零client/零derived。
  candidate recent-turn projection不再读取old v8 planner，old v8 corrupt sentinel byte-exact inert；
- 两Host exact-equivalence gate从同一closed canonical raw/Timeline/Control/recipe/Store fixture复制两份，分别走真实CLI和
  `GalateaHostService`，逐字节比较Timeline head/descriptor、Store content export、fulfilled view与Getter contributions；二者recap
  call count相等，并捕获真实main-agent `CompletionRequest`：model/system/contract/boundary相同，raw-tail各恰有一个provider-visible
  observation，CLI保留原文、Galatea只允许其既有显式user-message envelope差异。Galatea actual service另覆盖missing-work real Runtime
  route、Fresh/ObservationAccepted、Prepared与Started；CLI Fresh/NewRequest与Galatea均证明corrupt old-v8 sentinel byte-exact inert，
  candidate CLI source gate不引用old DerivedRecap，Galatea legacy registry client creation为0；
- caller switch map保持为可删candidate旁路：`recap-grid candidate run-online-turn`的Fresh/NewRequest调用Online+Hosting，
  Galatea仅internal/test constructor调用同一Online+Hosting；top-level `run-online-turn`、Galatea public constructor/Program DI和old
  `recap`命令仍精确走legacy production。WP-08只能在WP-07C GO后一次性切换这些caller并删除candidate入口；
- progress语义本包不冒充完整UI迁移：Online只在active+unfulfilled时内部调用Manager
  `InspectBuildProgress`决定是否build，不公开第二个progress owner；Galatea candidate recent-turn DTO仅报告exact
  raw-head-bound `recap-grid-candidate`状态且不读v8 planner。完整Grid progress DTO/UI caller switch仍属于WP-08；
- tail把SessionJournal lifecycle callback之后的raw-head fence提升为所有typed result统一执行；Backpressure/Unavailable也不能在raw漂移后
  冒充稳定terminal。Online disposal按Manager -> Getter -> Timeline best-effort聚合nonfatal、fatal立即停止，reentrant/unawaited drain fault可由后续
  Dispose观察；host-wide Completion composition真实等待in-flight Runtime后才exact-once释放client；Galatea session loop同样聚合nonfatal并让
  OOM/SO/AV立即透传且不继续candidate cleanup；
- 最终候选冻结前的串行evidence：Hosting 19/19、Hosting public surface 2/2、Online 21/21、Online public
  surface 1/1、Galatea actual candidate 7/7、CLI candidate 10/10、SessionJournal raw audit 19/19、Walking architecture
  22/22；Galatea与CLI affected product builds均为0 warning / 0 error，`Atelia.sln` build 0 warning / 0 error，包漏洞扫描零命中，
  scoped docs checker 15/0，`git diff --check`无whitespace error，Online三项project已在`Atelia.sln`注册；两路independent
  closure均GO（P0=0，P1=0）。这些证据不表示production cutover。

明确延后：Agent-facing Control tool、code-owned built-in genesis command/asset、ToolResultObserved/ToolContinuation、operation-idempotent
control carrier属于[`WP-07C`](derived-recap-grid-wp07c-agent-control-and-tool-continuation.md)。

## Handoff to WP-08

先交付给WP-07C exact candidate composition graph与Host lifetime；WP-07C关闭Agent control/tool continuation后，二者共同向WP-08交付
production caller switch list、behavior tests迁移表与环境阻塞的provider canary证据。

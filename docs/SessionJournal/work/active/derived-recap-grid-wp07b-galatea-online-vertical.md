# DerivedRecap Grid WP-07B：Galatea 与 Online Hosts Vertical Candidate

状态：Ready；WP-07A complete并取得两路独立closure GO

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

WP-04 complete Manager只证明同一frozen request重复进入的幂等性；真实Idle/pre-observation、ObservationAccepted与
ToolResultObserved lifecycle仍由本包在两个Host上验证，不能用WP-04 fake/runtime fixture替代。Mystery candidate已经证明base active、
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
- Manager lifecycle可在Idle/pre-observation、ObservationAccepted和每个ToolResultObserved多次幂等进入；
- Agent声明式创建专题Maintainer、overlay/full/A-B activate；
- Agent创建必须穿过最终选定的真实control capability/command，验证allowlist/scope/budget与operation-idempotency，不能让fixture
  直接`PutDefinition`冒充；若carrier/tool使用SessionJournal operation，沿用ToolExecutionStarted operationId恢复边界；
- route按allow-listed family key，dynamic column无需每列静态connection mapping；
- 两Host progress与operator messages。

## Acceptance matrix

1. built-in genesis、normal multi-row fill；NoBuild/missing-free零maintainer client；
2. same row multiple family leaders bounded overlap，共享prefix family内1 Leader + followers；
3. `XSuspicion` overlay追平前不live，激活后future rows影响`CulpritHypothesis`；
4. full rebuild从Row0 wavefront传播，partial row/view不泄漏；
5. restart missing-only、sibling failure/cancel/drain、budget zero-overrun；
6. fresh Idle、AwaitingAgentAction无Prepared、Observation/ToolResult多次lifecycle均幂等；
7. Prepared删除SQLite Grid、改变active recipe/删除DerivedRecap config后byte-identical resume，frozen connection仍exact bind；
8. Started Refuse零client/零derived write；explicit restart从frozen bytes建立new attempt；
9. ToolExecutionStarted existing operation/sequence语义不改；Galatea若仍unsupported必须显式保持；
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

两个Host disposable vertical、mystery analysis、recovery、route/cache、raw authority gates green；reviewer批准WP-08 direct cut。

## Handoff to WP-08

交付exact candidate composition graph、production caller switch list、behavior tests迁移表与环境阻塞的provider canary证据。

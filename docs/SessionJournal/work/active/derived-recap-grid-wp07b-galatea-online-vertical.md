# DerivedRecap Grid WP-07B：Galatea 与 Online Hosts Vertical Candidate

状态：Planned；依赖 WP-07A complete

只需加载：目标设计、Master、WP-07A handoff、本文与WP-08摘要。

## Intent

以明确candidate composition验证Galatea及CLI `run-online-turn`两个Host的fresh/new-request lifecycle、主线context、动态
Maintainer与frozen completion recovery；仍不切production default。

## In scope

- fresh/NewRequestRequired：Timeline reconcile/seal -> Manager fulfill -> Grid candidate -> SessionJournal raw-tail composition；
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

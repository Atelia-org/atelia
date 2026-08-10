# DerivedRecap Grid WP-01C：Single Durable Timeline Ledger

状态：Planned；依赖 WP-01B complete

只需加载：Grid target、Master、WP-01 overview、WP-01B handoff与本文。

## Intent

选择并实现唯一Timeline durable backend，分别提供immutable policy put、policy whole-head CAS、atomic row insert + append-head
CAS，以及backup/restore与bounded operator surface。

## Backend decision gate

Directory+canonical files与独立SQLite ledger使用同一fixture比较：row insert/head CAS、two-writer、crash before/after commit、
canonical corruption、branch/path query、backup/restore、file/state/API budget。选择winner后删除loser code/dependency/tests；禁止双写、
fallback或configurable dual backend。Timeline与Grid即使用同种技术也必须是独立durability domains，禁止跨库transaction。

## In scope

- V1 create/open strict schema；以下三种transaction不得合并成隐式“最新状态”写入：
  - `PutPolicy(canonicalPolicy)`：strict decode/canonical equality后content-addressed idempotent insert，绝不改head；
  - `CompareExchangePolicy(expectedWholeHead, nextPolicyDigest)`：CAS比较完整`TimelineHeadRef`，只替换active policy并推进
    generation；next policy必须已存在且属于同Timeline；即使empty head仍保持row/raw fence为null；
  - `CommitRow(proposal)`：raw authority内部重读proposal绑定Ref的current selected head并匹配outer captured head，在同一
    transaction完成immutable row insert与完整head CAS；new head把row设为proposal row、raw fence设为outer captured head、
    generation加一并保留expected active policy，不顺带切policy；
- whole-head CAS比较TimelineId/RefId/head row/active policy/selected raw fence/generation全部字段；caller传入的
  `BoundHistorySegmentRange`只是typed evidence，不代替raw authority；
- Timeline durable owner独占`TimelineHeadRef`与`ActiveTimelineLocator` strict canonical codec：strict UTF-8、duplicate/unknown/
  null/case/order/whitespace拒绝并要求canonical byte equality；其他层不得自写JSON locator；
- create/open/restore在读取或分配前执行code-owned hard caps：policy/descriptor沿用4 KiB/16 KiB，HeadRef/locator canonical bytes
  各不超过4 KiB；backend/backup总bytes、entry/row count、path page与restore copy上限必须在backend plan lock给出exact常量和
  exact-cap/cap+1 tests，不得由输入或config放宽；
- `inspect/export/verify` read-only/no-create/bounded；
- verified backup + restore演练；
- canonical `ActiveTimelineLocator` per Ref；`abandon --confirm <RefId,TimelineId,locator-generation>`在Host关闭时CAS到
  explicit initial policy创建的new TimelineId；旧ledger/backup永久inert，不提供普通in-place reset；
- corruption/unknown schema fail closed，不自动重新分段。

## Crash/concurrency matrix

1. `PutPolicy` crash只见absent或完整canonical policy；不改变head；
2. `CompareExchangePolicy` crash只见old或完整new whole head，row set不变；empty generation 0可变为empty generation 1+；
3. `CommitRow` crash只见old head，或完整row + new head；new head保留expected active policy；
4. raw authority重读current selected head与proposal的outer captured head不等时，row insert与head mutation都必须为零；
5. 从same expected whole head竞争的policy CAS与row append只能一个胜出，loser零mutation；同类two-writer亦然，且只匹配
   generation但其他head field不同必须失败；
6. crash/reopen重新计算strict canonical whole-head digest，只见old或完整new head；
7. backup manifest提交TimelineId/RefId/schema/generation、canonical whole-head digest与whole-backup digest；restore先复算head
   digest与全部hard caps，仅在Host关闭、expected active scope/version exact且backup包含active head时atomic old-or-new替换；
   更旧backup只能走abandon；
8. corrupt descriptor/head/locator/index或digest mismatch使ledger Invalid，normal path零mutation；
9. abandon错误确认零mutation；正确确认的locator crash只见old或new，旧bytes不改且旧Grid/control scope自然失配；
10. inspect absent/existing均不create、不加载Maintainer/provider/secret；
11. bounded path pagination，不把全History/全DAG物化进内存；open/restore每个hard cap做exact-cap接受、cap+1拒绝。

## No-Go

- StateJournal/EventJournal复用迫使Timeline引入不需要的process-exclusive/second-protocol complexity；
- 损坏时静默reset/repartition；
- losing backend或migration runner留在production；
- Agent/operator需要手改live store。

## Done when

single backend、operator action、crash/backup/branch tests、affected build/docs/diff与independent review全部green。

## Handoff to WP-02

交付stable reader/witness、backend diagnostics、Timeline scope identity和abandon后Grid/control必须重建的规则。

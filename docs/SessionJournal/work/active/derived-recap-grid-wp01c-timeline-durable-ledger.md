# DerivedRecap Grid WP-01C：Single Durable Timeline Ledger

状态：Planned；依赖 WP-01B complete

只需加载：Grid target、Master、WP-01 overview、WP-01B handoff与本文。

## Intent

选择并实现唯一Timeline durable backend，提供atomic row insert + head/policy CAS、backup/restore与bounded operator surface。

## Backend decision gate

Directory+canonical files与独立SQLite ledger使用同一fixture比较：row insert/head CAS、two-writer、crash before/after commit、
canonical corruption、branch/path query、backup/restore、file/state/API budget。选择winner后删除loser code/dependency/tests；禁止双写、
fallback或configurable dual backend。Timeline与Grid即使用同种技术也必须是独立durability domains，禁止跨库transaction。

## In scope

- V1 create/open strict schema；atomic row/head/policy operations；
- `inspect/export/verify` read-only/no-create/bounded；
- verified backup + restore演练；
- canonical `ActiveTimelineLocator` per Ref；`abandon --confirm <RefId,TimelineId,locator-generation>`在Host关闭时CAS到
  explicit initial policy创建的new TimelineId；旧ledger/backup永久inert，不提供普通in-place reset；
- corruption/unknown schema fail closed，不自动重新分段。

## Crash/concurrency matrix

1. row write、head CAS、policy switch before/after commit；
2. same expected generation two writers只有一个胜者；
3. crash/reopen只见old或完整new head；
4. backup manifest提交TimelineId/RefId/schema/generation/head digest/whole-backup digest；restore仅在Host关闭、expected
   active scope/version exact且backup包含active head时atomic old-or-new替换；更旧backup只能走abandon；
5. corrupt descriptor/head/index使ledger Invalid，normal path零mutation；
6. abandon错误确认零mutation；正确确认的locator crash只见old或new，旧bytes不改且旧Grid/control scope自然失配；
7. inspect absent/existing均不create、不加载Maintainer/provider/secret；
8. bounded path pagination，不把全History/全DAG物化进内存。

## No-Go

- StateJournal/EventJournal复用迫使Timeline引入不需要的process-exclusive/second-protocol complexity；
- 损坏时静默reset/repartition；
- losing backend或migration runner留在production；
- Agent/operator需要手改live store。

## Done when

single backend、operator action、crash/backup/branch tests、affected build/docs/diff与independent review全部green。

## Handoff to WP-02

交付stable reader/witness、backend diagnostics、Timeline scope identity和abandon后Grid/control必须重建的规则。

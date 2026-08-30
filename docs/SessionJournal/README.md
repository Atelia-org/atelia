# SessionJournal 文档入口

状态：Current discovery router

本文只负责帮助 Coding Agent 找到当前入口，不是 API、wire、recovery 或 implementation authority。
事实必须以 current code、tests、fixtures/goldens，以及 raw events 与 selected `RefId` Parent lineage
为准；snapshot 或 evidence 只认证其记录的 exact candidate，不自动认证当前 HEAD。

首次接触 SessionJournal，或要定位 assembly ownership、owner code、focused tests 与已知开放边界时，
先读[当前架构与代码地图](current/architecture-and-code-map.md)，再按任务补读下列入口。正常任务不需要
先读 `archive/`。

## 按任务阅读

| 任务 | 首读入口 | 必要时再读 |
|---|---|---|
| Core API、raw wire、Prepared/Resume 与 crash recovery | [Beta contract snapshot](current/contracts/session-journal-beta-contract-snapshot.md)、[Core guide](../../prototypes/SessionJournal/README.md) | [Uncertain external effects contract](current/recovery/uncertain-external-effects.md) 与 current codec/recovery tests |
| Timeline、partition、HistoryLoad 与 branch reconcile | [HistoryLoad](current/derived-recap/history-load.md)、[HistoryTimeline code](../../prototypes/SessionJournal.HistoryTimeline/)、[HistoryTimeline tests](../../tests/SessionJournal.HistoryTimeline.Tests/) | [Durable authority](current/derived-recap/durable-target.md) |
| 修改 Cadence recent reserve或恢复丢失receipt | [Cadence set-reserve approved receipt contract](current/contracts/cadence-set-reserve-receipt.md)、[CLI guide](../../prototypes/SessionJournal.Cli/README.md) | exact command-local status/detail/exit与fresh-inspect矩阵由immutable surface set 5 tag锚定；不要自动retry或把receipt当Cadence authority |
| 生成或消费 HistoryLoad calibration report | [HistoryLoad report V2 approved top-level contract](current/contracts/history-load-report-v2.md)、[activation runbook](operations/galatea-g2a-staging-acceptance.md#2-import-and-raw-baseline) | exact 11-field/types/meanings、V1字段删除与read-only retry由immutable v4 tag锚定；full planning window仍unbounded/offline、无final byte cap/oversize contract |
| 生成或消费 SessionJournal offline validation report | [Offline validation report V3 approved contract](current/contracts/offline-validation-report-v3.md)、[surface set 6 addendum](evidence/contract-freeze-r2-approval-surface-set-6.md)、[Offline owner guide](../../prototypes/SessionJournal.Offline/README.md)、[activation runbook](operations/galatea-g2a-staging-acceptance.md#2-import-and-raw-baseline) | exact 25-field/current nested/7 phase/11 kind与read-only/publication/retry/privacy/resource boundary由immutable v6 tag锚定；不属于v5，post-tag docs review PASS |
| Grid Store、Control、Manager、Getter 与 Runtime | [Grid concepts](current/derived-recap/concepts.md)、[target design](work/active/derived-recap-grid-target-design.md) | [Store SQLite V2 approved logical-schema appendix](current/contracts/recap-grid-store-sqlite-v2.md)与owning product/tests；logical schema/persistent pragmas/operator mapping由surface-set-2 tag锚定，physical SQLite不在批准范围 |
| 审阅 Galatea root `config.json` | [Root config V6 current contract](current/contracts/galatea-root-config-v6.md)、[Galatea guide](../../prototypes/Galatea/README.md) | current product已hard-cut V6：保留V5 prompt ownership并新增required `characterMemoryStateDir`与total storage topology；[V5](current/contracts/galatea-root-config-v5.md)、[V4](current/contracts/galatea-root-config-v4.md)、[V3](current/contracts/galatea-root-config-v3.md)、[V2](current/contracts/galatea-root-config-v2.md)与[V1 appendix](current/contracts/galatea-root-config-v1.md)仅保留prior history，旧tag不认证V2–V6 delta |
| 消费或恢复 desired-setup reconciliation report | [Desired setup report V2 approved contract](current/contracts/desired-setup-reconciliation-report-v2.md)、[activation runbook](operations/galatea-g2a-staging-acceptance.md#9-actual-activation-after-a-passed-disposable-candidate) | producer-only exact 10-field gate；raw mutation先于report publication，失败后必须重新inspect exact head/Idle/governing setup；surface set 3已通过unified gates并由immutable v3 tag锚定 |
| 审阅 approved public API / wire-format surface与R2停止边界 | [Contract R2 anchored surfaces and intentional Defer map](current/contracts/session-journal-contract-r2.md)、[R2 closure evidence](evidence/contract-freeze-r2-closure.md) | immutable v1-v6 tags分别锚定surface sets 1-6；closure记录exact tag map、Stop-after-V6理由、intentional remaining matrix与fresh-candidate reopen triggers；remaining Defer/non-promises不是active backlog |
| 审阅 post-cutover cadence、recent reserve、长期容量或 cyber 激活边界 | [Cadence/capacity audit](work/active/derived-recap-grid-cadence-capacity-and-activation-audit.md) | A0-A2已实现24k target reserve；C2/C5 activation完成，C4仍Open |
| 实现 Galatea 自传/world-understanding rolling maintainers，或审阅未来 Editor/ExperienceRefiner 边界 | [C2 Galatea rolling maintainers](work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md) | shared Family、runtime-configurable model、operator asset assembly与验收矩阵 |
| 设计或实现面向具体事务细节的动态外置记忆 | [MemoPod目标设计与施工计划](work/active/memo-pod-target-design-and-implementation-plan.md) | MemoPod与RecapGrid互补；首版只做单Pod、Editable/Frozen、aggregate document与ID-only recall；Galatea接入另读下项 |
| 重新设计MemoPod未来upper-consumer integration | [MemoPod Galatea / SessionJournal integration plan](work/active/memo-pod-galatea-integration-plan.md) | Design Reopened；先关闭query timing、Pod动态生命周期、Indexer、empty-query cache renewal、main-thread injection与跨turn reference continuity；当前没有active B1/B2 authorization |
| 审计已撤回的Prepared v6 candidate | [withdrawn Tier-A amendment](archive/superseded/completion-request-prepared-v6-tier-a-amendment.md)、[historical rolled-back evidence](evidence/completion-request-prepared-v6-candidate.md) | historical `83477c06` reviews仍为事实；用户撤回且`1d8c33bb`已回滚；Gate B/B2 never authorized，current approved+code均为v5/v1/count `0..128` |
| CLI operator 或 Galatea integration | [CLI guide](../../prototypes/SessionJournal.Cli/README.md)、[Galatea guide](../../prototypes/Galatea/README.md)、[Host integration](current/host-integration/derived-recap-host-integration.md) | current composition/code/tests |
| 重跑 Galatea G2A staging acceptance | [G2A runbook](operations/galatea-g2a-staging-acceptance.md) | 本轮新生成的 acceptance evidence；runbook 存在不等于本轮 Passed |
| 审计历史 candidate、review 或交付 | [Evidence index](evidence/README.md)、[`archive/`](archive/) | [冻结的旧 router 与 claim ledger](archive/superseded/session-journal-router-and-claim-ledger-2026-08-04.md) 只用于 cut-time 审计 |

## 目录语义

- `current/`：当前 Shape、Rule 与代码导航入口；仍须 against code/tests 核对实现事实。
- `operations/`：可重复 procedure，不是执行成功证明。
- `evidence/`：exact run、candidate 或 review 的记录，不随 HEAD 自动续期。
- `archive/`：已完成、被替代或历史材料；正常实现任务不得从这里推导 current API。
- 计划入口（含为链接稳定保留原路径的closed plan）：
  [DerivedRecap Sparse Versioned Grid 目标设计](work/active/derived-recap-grid-target-design.md)
  记录Timeline rows、analysis columns、content-addressed immutable cells与Control/Store/Manager/Getter的current Rule/Shape。
  [DerivedRecap Grid Rewrite 总施工计划](work/active/derived-recap-grid-rewrite-master-plan.md)
  记录WP-00至WP-08的implementation/review evidence；WP-08负责正式caller cutover与旧owner删除。
  [C2 Galatea rolling maintainers](work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md)
  记录首个production recipe、runtime model policy、prompt/asset owner与未来refiner扩展边界。
  [MemoPod动态外置记忆目标设计与施工计划](work/active/memo-pod-target-design-and-implementation-plan.md)
  记录与RecapGrid互补的细粒度事务记忆边界、Editable/Frozen状态机、单文档持久化、ID-only recall与WP-00至WP-07实施门禁；
  WP-00–WP-06、Track C1/C2与MemoPod core不受SessionJournal rollback影响；MemoPod当前没有product upper consumer，
  production显式依赖仅`Completion.Abstractions`。WP-07现为Design Reopened，不是implementation authority。
  [MemoPod Galatea / SessionJournal integration plan](work/active/memo-pod-galatea-integration-plan.md)
  active authority仅是Design Reopened状态与六项未决设计闸；旧WP-07A/B全文只作historical input。六项关闭并获fresh
  user design authorization前，不得设计或实现SessionJournal interface/wire、Galatea adapter或main-thread injection。
  [Withdrawn CompletionRequestPrepared v6 Tier-A amendment](archive/superseded/completion-request-prepared-v6-tier-a-amendment.md)
  与[historical evidence](evidence/completion-request-prepared-v6-candidate.md)保留旧implementation/review/audit事实。用户已撤回，
  `1d8c33bb`已回滚；Gate B canceled/never granted，promotion never started，旧B2 canceled/never authorized。
  已关闭但为避免link/archive churn而保留原路径的
  [SessionJournal Contract Freeze R2](work/active/session-journal-contract-freeze-r2.md)
  记录候选direct cut与分阶段freeze gates；该`work/active/`路径名不表示R2仍active。
  [Contract R2](current/contracts/session-journal-contract-r2.md)汇总approved exact support-role/wire与intentional Defer边界；
  surface set 1已由immutable v1 tag锚定，additive surface set 2
  已获用户批准、通过pre-tag gates并由immutable v2 tag锚定；additive surface set 3也已通过unified gates并由immutable
  v3 tag锚定；additive surface set 4只新增HistoryLoad V2 exact top-level/read-only contract，也已通过unified gates与
  independent review并由immutable v4 tag锚定；additive surface set 5只新增Cadence set-reserve command-local
  ledger/recovery，已通过unified gates与final pre-tag review并由immutable v5 tag锚定；本post-tag docs commit不移动tag、
  不续期证据或扩大scope，对`845539c5`与actual v5 tag的independent post-tag review已PASS；未列出的surface继续按Defer边界推进。
  Offline validation report V3是其后的producer-only surface set 6；fresh gates/rebuild与final pre-tag review已完成，
  annotated v6 tag object `acc73dab`已锚定reviewed ledger `14b570cb`。它不反向扩大surface set 5，也不把full
  audit/rebuild误写为bounded/content-free/physical-byte promise，或把public serialization metadata hard cut误写成CLR
  compatibility承诺。对post-tag review object `bbfd7823`与actual tag的independent review已PASS；本tail不移动tag、不续期
  证据或扩大scope。[R2 closure evidence](evidence/contract-freeze-r2-closure.md)记录v1-v6 exact anchors、
  Stop-after-V6理由、remaining matrix与reopen triggers；剩余项不是待继续穷举的缺陷。
  精确事实仍以owning code/tests与`current/`文档为准。

目录、标题、日期、`README.md` 或 `public` 均不自动授予 authority。

## Safety escalation

遇到以下主题时，不要停在 router、snapshot 或 target design：

- wire/schema/codec/canonical bytes，以及 Prepared/Resume/Restore/tool continuation；
- raw Parent lineage、bounded proof、exact-head mutation、strict ordinal、repair/corruption；
- migration/import/replay，以及 path/lock/fsync/crash/atomic publication。

必须继续定位 current code owner、focused tests 与 fixtures/goldens，并确认 target 没有被当成 checkout
事实。若接受 contract 变化，应建立独立 candidate 与 verification gate。

## 结构检查

检查 current explicit scope：

```bash
python3 scripts/check_session_journal_docs.py
```

观察全部 tracked SessionJournal 文档：

```bash
python3 scripts/check_session_journal_docs.py --all-tracked --report-only
```

默认路径见 [`session-journal-doc-check-scope.txt`](session-journal-doc-check-scope.txt)。checker 只做
tracked scope、UTF-8/regular-file、local link、path case、repo escape、worktree 与 ancestor symlink
等机械检查；它不判断正文真伪、claim ownership、anchor 或网络目标，也不写入或修复文件。

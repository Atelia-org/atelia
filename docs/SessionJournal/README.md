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
| Grid Store、Control、Manager、Getter 与 Runtime | [Grid concepts](current/derived-recap/concepts.md)、[target design](work/active/derived-recap-grid-target-design.md) | [Store SQLite V2 approved logical-schema appendix](current/contracts/recap-grid-store-sqlite-v2.md)与owning product/tests；logical schema/persistent pragmas/operator mapping由surface-set-2 tag锚定，physical SQLite不在批准范围 |
| 审阅 Galatea root `config.json` V1 | [Root config V1 approved appendix](current/contracts/galatea-root-config-v1.md)、[Galatea guide](../../prototypes/Galatea/README.md) | exact field/path/bounds/bootstrap policy由surface-set-2 tag锚定；不要将批准扩张到connections/Route/Profile owner contract、secret/deployment或appendix non-promises |
| 消费或恢复 desired-setup reconciliation report | [Desired setup report V2 approved contract](current/contracts/desired-setup-reconciliation-report-v2.md)、[activation runbook](operations/galatea-g2a-staging-acceptance.md#9-actual-activation-after-a-passed-disposable-candidate) | producer-only exact 10-field gate；raw mutation先于report publication，失败后必须重新inspect exact head/Idle/governing setup；surface set 3已通过unified gates并由immutable v3 tag锚定 |
| 审阅 approved public API / wire-format surface与remaining candidate | [Contract R2 approved surfaces and candidate map](current/contracts/session-journal-contract-r2.md) | immutable v1/v2/v3 tags分别锚定surface sets 1/2/3；[surface set 2 addendum](evidence/contract-freeze-r2-approval-surface-set-2.md)与[surface set 3 addendum](evidence/contract-freeze-r2-approval-surface-set-3.md)记录各自exact tag object/target；[plan](work/active/session-journal-contract-freeze-r2.md)保留其余Defer边界 |
| 审阅 post-cutover cadence、recent reserve、长期容量或 cyber 激活边界 | [Cadence/capacity audit](work/active/derived-recap-grid-cadence-capacity-and-activation-audit.md) | A0-A2已实现24k target reserve；C2/C5 activation完成，C4仍Open |
| 实现 Galatea 自传/world-understanding rolling maintainers，或审阅未来 Editor/ExperienceRefiner 边界 | [C2 Galatea rolling maintainers](work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md) | shared Family、runtime-configurable model、operator asset assembly与验收矩阵 |
| CLI operator 或 Galatea integration | [CLI guide](../../prototypes/SessionJournal.Cli/README.md)、[Galatea guide](../../prototypes/Galatea/README.md)、[Host integration](current/host-integration/derived-recap-host-integration.md) | current composition/code/tests |
| 重跑 Galatea G2A staging acceptance | [G2A runbook](operations/galatea-g2a-staging-acceptance.md) | 本轮新生成的 acceptance evidence；runbook 存在不等于本轮 Passed |
| 审计历史 candidate、review 或交付 | [Evidence index](evidence/README.md)、[`archive/`](archive/) | [冻结的旧 router 与 claim ledger](archive/superseded/session-journal-router-and-claim-ledger-2026-08-04.md) 只用于 cut-time 审计 |

## 目录语义

- `current/`：当前 Shape、Rule 与代码导航入口；仍须 against code/tests 核对实现事实。
- `operations/`：可重复 procedure，不是执行成功证明。
- `evidence/`：exact run、candidate 或 review 的记录，不随 HEAD 自动续期。
- `archive/`：已完成、被替代或历史材料；正常实现任务不得从这里推导 current API。
- 当前 active plans：
  [DerivedRecap Sparse Versioned Grid 目标设计](work/active/derived-recap-grid-target-design.md)
  记录Timeline rows、analysis columns、content-addressed immutable cells与Control/Store/Manager/Getter的current Rule/Shape。
  [DerivedRecap Grid Rewrite 总施工计划](work/active/derived-recap-grid-rewrite-master-plan.md)
  记录WP-00至WP-08的implementation/review evidence；WP-08负责正式caller cutover与旧owner删除。
  [C2 Galatea rolling maintainers](work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md)
  记录首个production recipe、runtime model policy、prompt/asset owner与未来refiner扩展边界。
  [SessionJournal Contract Freeze R2](work/active/session-journal-contract-freeze-r2.md)
  记录候选direct cut与分阶段freeze gates；[Contract R2 candidate](current/contracts/session-journal-contract-r2.md)
  汇总approved exact support-role/wire与remaining candidate；surface set 1已由immutable v1 tag锚定，additive surface set 2
  已获用户批准、通过pre-tag gates并由immutable v2 tag锚定；additive surface set 3也已通过unified gates并由immutable
  v3 tag锚定；未列出的surface继续按Defer边界推进。
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

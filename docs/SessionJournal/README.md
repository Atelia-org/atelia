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
| EADR 术语、Store selection/publication/materialization/Restore | [EADR concepts](current/derived-recap/concepts.md)、[Store guide](../../prototypes/SessionJournal.DerivedRecap.Store/README.md) | [Durable target](current/derived-recap/durable-target.md) 与 Store code/tests |
| Planner config、cadence、HistoryLoad、NewPlanning/Resume/Restore | [Planner guide](../../prototypes/SessionJournal.DerivedRecap.Planner/README.md)、[Planner config](current/derived-recap/planner-config.md) | [HistoryLoad](current/derived-recap/history-load.md) 与 Planner code/tests |
| Maintainers、Host composition 或 Galatea integration | [Maintainers guide](../../prototypes/SessionJournal.DerivedRecap.Maintainers/README.md)、[Host integration](current/host-integration/derived-recap-host-integration.md) | current Galatea composition/code/tests |
| 重跑 Galatea G2A staging acceptance | [G2A runbook](operations/galatea-g2a-staging-acceptance.md) | 本轮新生成的 acceptance evidence；runbook 存在不等于本轮 Passed |
| 审计历史 candidate、review 或交付 | [Evidence index](evidence/README.md)、[`archive/`](archive/) | [冻结的旧 router 与 claim ledger](archive/superseded/session-journal-router-and-claim-ledger-2026-08-04.md) 只用于 cut-time 审计 |

## 目录语义

- `current/`：当前 Shape、Rule 与代码导航入口；仍须 against code/tests 核对实现事实。
- `operations/`：可重复 procedure，不是执行成功证明。
- `evidence/`：exact run、candidate 或 review 的记录，不随 HEAD 自动续期。
- `archive/`：已完成、被替代或历史材料；正常实现任务不得从这里推导 current API。
- 当前 active plans：
  [DerivedRecap Sparse Versioned Grid 目标设计](work/active/derived-recap-grid-target-design.md)
  记录下一代Timeline rows、Maintainer analysis columns、content-addressed immutable cells与minimal control plane的理想
  Shape/Rule；尚未实施，也不描述current production。
  [DerivedRecap Shared Epoch / Maintainer Family 并行重构计划](work/active/derived-recap-shared-epoch-parallel-maintainer-refactor-plan.md)。
  R3 shared-epoch v8、R4 runtime-group并行调度、R5 cache boundary/usage telemetry与R6
  Galatea/CLI production composition已进入current code；R7 real-provider cache/economic proof仍为
  `Environment-blocked`。
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
python scripts/check_session_journal_docs.py
```

观察全部 tracked SessionJournal 文档：

```bash
python scripts/check_session_journal_docs.py --all-tracked --report-only
```

默认路径见 [`session-journal-doc-check-scope.txt`](session-journal-doc-check-scope.txt)。checker 只做
tracked scope、UTF-8/regular-file、local link、path case、repo escape、worktree 与 ancestor symlink
等机械检查；它不判断正文真伪、claim ownership、anchor 或网络目标，也不写入或修复文件。

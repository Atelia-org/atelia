# SessionJournal Evidence 索引

状态：Audit-only evidence router

本目录保留 exact candidate、一次性审阅和校准运行的可复核记录。这里的文档不认证 current HEAD，
不拥有 current API、wire、recovery、implementation status 或 operator procedure，也不能因为历史结论为
`Passed` / `GO` 就自动续期。理解当前实现应先读
[SessionJournal 文档入口](../README.md)、[当前架构与代码地图](../current/architecture-and-code-map.md)
以及 owning code/tests。

| 记录 | Evidence 范围 | Exact boundary | 何时读取 |
|---|---|---|---|
| [HistoryLoad Galatea calibration](history-load-galatea-calibration.md) | 一次 fresh-import 的 content-free HistoryLoad分布与当时cache取舍依据 | 2026-07-31 single-fixture run；只证明记录内的fixed export identity、estimator、captured head与baseline，且未记录code commit；不代表current repo分布 | 调整estimator、R/B或重做真实历史校准时，用来理解旧阈值来源并设计新run |
| [DG1 pilot](document-governance-dg1-pilot.md) | 12个窄claim的Accept/Modify/frozen/closed裁决和portable verification pointers | verification baseline `cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；candidate evidence仍限于各自exact candidate | 审计router claim为何这样分层，或重新核验某一claim时；它不是第二份active ledger |
| [First-production readiness review](first-production-readiness-review.md) | 首次生产前findings、修复/残余边界与R4 acceptance记录 | review baseline `2ccd67150373360a1230dd22c195b4ec100ac0bb`；Beta GO只属于candidate `681fc02bb9f1e4a45cd012aa7feadefe3f33fa9e` | 追溯早期Beta blocker、accepted risk或首次candidate gate时，不用于证明后来HEAD可发布 |
| [Contract normalization review](contract-normalization-review.md) | N0～N5 candidate ledger、Adopt/Retain/Reject/Defer理由与contract-preservation evidence | execution baseline `cd804c39cf96499167c80e5d046fb21e4d3b8c7d`；implementation candidate `49ebb4634e5b4136032db983dd92a9a4560b33eb`；`81a1fa24`只增加tests/runbook，本轮provider calls为0且staging evidence是scripted | 再提contract合并/删除proof redundancy时，复核已裁决方案与重新开启条件 |
| [Contract Freeze R2 R0 inventory](contract-freeze-r2-r0.md) | S/T/O/C/G/H public construction、raw/durable companion wire、config/CLI/HTTP/SSE current inventory与R1 draft ledger | exact source/inventory baseline `380df30fc069d2dfbc3c71fe1923e0442389ecd8`；R0只读调查，不认证后续HEAD，也未批准或实施candidate | 在收窄current API、给wire加版本或进入R1/R2前，复核current finding、Retain理由、evidence gaps与gates |
| [Contract Freeze R2 R1 priority review](contract-freeze-r2-r1-priority-review.md) | `CF-A-01` construction、`CF-D-01` connections language、`CF-D-04` CLI envelope双视角review与原子裁决 | source baseline `677e94c9d931cfabc8137a24bf5163b3b494331f`；只读R1，不含production/test修改或动态验证；Adopt仍须R2 lock | 在实施三个优先candidate、迁移connections operator config或继续HTTP/SSE/config review前，复核最小cut、复杂性tripwire与gates |
| [Contract Freeze R2 priority implementation](contract-freeze-r2-r2-priority-implementation.md) | `CF-A-01`、`CF-D-01`、`CF-D-04` plan lock、原子实现、API/wire delta、独立review与R4 code gates | implementation baseline `5ca08be9`；code candidate `58d8ae06` + test-only `87079eaa`；operator cutover后来完成，但本文仍不认证tier freeze | 复核priority cuts为何落地/停止扩张，或继续CF-D-02/03、CF-B/CF-C前使用；actual operator证据转到相邻HTTP/SSE plan-lock记录 |
| [Contract Freeze R2 HTTP/SSE plan lock](contract-freeze-r2-http-sse-plan-lock.md) | CF-D-01 actual operator cutover evidence；CF-D-02a HTTP与02b SSE accepted-language、consumer、state-machine、candidate ledger、P0与stream-bound blockers | source baseline `e1d785f0`；operator ignored V1 manifest已迁移但不受Git跟踪；原始HTTP/SSE调查只读，后续candidate/R4转到相邻implementation evidence；numeric budgets仍为Prototype | 复核D02 cut-time风险、设计裁决与拒绝项；current implementation事实、commits和gates以相邻D02 R4 evidence为准 |
| [Contract Freeze R2 D02 R4 implementation](contract-freeze-r2-d02-r4-implementation.md) | D02-P0 bounded recent、HTTP V1、SSE V1的commit chain、最终candidate shape、public inventory、combined R4与复杂性复核 | product lock `66dd87fc`；source candidate `0f441f90`；各test/build结果按package closure commit分列，不是一次final rerun；仍为Prototype locked candidate | 复核current D02 candidate为何bounded、如何处理pop indeterminate与fatal SSE EOF，或进入R5前核对exact commits、gates与remaining boundary |
| [Contract Freeze R2 D03 / targeted CF-B / CF-C-01 implementation](contract-freeze-r2-d03-cfb-cfc01-implementation.md) | root config exact V1/no-BOM、三项support-role cut、Control future-schema classification与empty golden的commit map、operator content-free evidence及分时gates | source candidate `8f72cb66`；operator manifest是Git外content-free observation；仍为Prototype candidate，未完成R5 | 复核D03 hard cut、CF-B停止点、CF-C-01分类边界，或开始CF-C-02与R5 preparation前使用 |
| [Contract Freeze R2 CF-C-02 implementation](contract-freeze-r2-cfc02-implementation.md) | History/Store/Rewriter independent goldens与fingerprints、Store identity/classification修复、repeat-init readiness及legacy disposable rebuild | source candidate `3599c510`；rebuild仅在`/tmp`且provider calls为0；实际Galatea repo未改；仍为Prototype candidate | 复核companion durable proof为何Retain、Store malformed/future分类、可重建边界，或进入readiness tails与R5 preparation前使用 |
| [Contract Freeze R2 R5 candidate](contract-freeze-r2-r5-candidate.md) | RT/SC readiness tails、current S/T/O/C/G/H inventory、unified support/wire/upgrade map、candidate commit map与final gate ledger | source candidate `a77ed16c`；code/rebuild gates complete，docs closure与用户tier approval仍Pending；ignored operator config/provider均NotRun；未创建tag | 审阅R5 candidate边界、完成docs closure或准备用户逐tierapproval时使用；不得把draft当成freeze声明 |

Galatea 2026-08-01 exact G2A旧证据仍在
[cutover completion record](../archive/completed-plans/galatea-session-journal-cutover-plan.md#g2arepeatable-staging-acceptancedone2026-08-01)；
该completion record现已位于`archive/`，这里的链接只用于历史审计。要针对current candidate重跑G2A，
必须使用[operations runbook](../operations/galatea-g2a-staging-acceptance.md)并生成本轮新的
`Passed / Failed / NotRun` evidence，不能复制旧结果或只更新日期。

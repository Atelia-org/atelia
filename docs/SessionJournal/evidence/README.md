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

Galatea 2026-08-01 exact G2A旧证据仍在
[cutover completion record](../archive/completed-plans/galatea-session-journal-cutover-plan.md#g2arepeatable-staging-acceptancedone2026-08-01)；
该completion record现已位于`archive/`，这里的链接只用于历史审计。要针对current candidate重跑G2A，
必须使用[operations runbook](../operations/galatea-g2a-staging-acceptance.md)并生成本轮新的
`Passed / Failed / NotRun` evidence，不能复制旧结果或只更新日期。

# SessionJournal 文档入口

状态：Active discovery ledger / DG2

本文只负责按任务发现 SessionJournal 文档，不是 raw、wire、recovery 或 implementation authority。
current implementation/API/wire claim 已在 exact commit
`cf3c77d524abdf24352400c221e0c42f0c9cb2fe` 上核验；核验 scope、focused tests 与裁决见
[DG1 pilot closing record](session-journal-document-governance-dg1-pilot-report.md)。该 SHA 是
verification baseline，不表示后续 HEAD 自动通过相同 gate。代码变化后必须重跑对应 scope，不能机械更新 SHA。

从下表与任务最接近的一行开始，通常先读 2～4 份文档。遇到本文末尾的 safety trigger 时，立即继续读取
current code、tests 与 fixtures，不受默认阅读预算限制。

## 按任务阅读

| 任务 | 首读入口 | 再读入口 | 边界 / escalation |
|---|---|---|---|
| 选择 Beta-supported consumer entry path | [Beta snapshot](session-journal-beta-contract-snapshot.md) §1 与 §2 role table/general boundary | DG1 的 `sj.beta.supported-roles` | current claim 不覆盖 §2 后续 candidate-specific Store/Galatea implementation detail，也不把所有 `public` surface 视为 supported |
| 修改 raw event kind/body、Prepared request 或 strict reader | [Beta snapshot](session-journal-beta-contract-snapshot.md) §3 | DG1 的 `sj.raw-wire.beta-summary`，再读 current codec/tests | 只发布 A-level 摘要；canonical bytes、rejection language 与 recovery wire 必须 against code/fixtures 核对 |
| 理解 EADR 术语、authority、cadence 与 ownership | [EADR concepts](event-addressed-derived-recap-concepts.md) | [Store guide](../../prototypes/SessionJournal.DerivedRecap.Store/README.md) 或 [Planner guide](../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) | concepts 拥有术语与不变量，不拥有全部 current API、wire 或 status |
| 修改 Store selection、publication、materialization 或 Restore | [Store guide](../../prototypes/SessionJournal.DerivedRecap.Store/README.md) | [EADR concepts](event-addressed-derived-recap-concepts.md) | wire、authority、atomic publication、strict ordinal 与 corruption 必须继续读 Store code/tests |
| 修改 Planner cadence、NewPlanning、Resume 或 Restore | [Planner guide](../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) | [EADR concepts](event-addressed-derived-recap-concepts.md)，必要时再读 Store guide | active config 与 frozen execution分离；bounded proof 不能退化为 full scan |
| 评估 EADR durable Shape / Rule | [V4 target design](event-addressed-derived-recap-v4-target-design.md) | EADR concepts 与相关 component guide | target 是 accepted normative intent，不是 current codec、API 或 implementation-status owner |
| 提议合并同构 contract 或删除 proof redundancy | [EADR concepts](event-addressed-derived-recap-concepts.md) 的 `Contract normalization gate` | [normalization closeout](session-journal-semantic-preserving-contract-normalization-review-report.md) | 先比较合法状态/行为、authority、proof obligation 与 durable reader language |
| 审计历史交付或 exact candidate acceptance | [implementation completion record](event-addressed-derived-recap-v4-implementation-plan.md) 或 normalization closeout | Beta snapshot §7 与 DG1 record | 这些是 closed/frozen evidence，不自动认证 current HEAD |

## Current verified claim ledger

以下七项是 against DG1 baseline 核验过的窄 implementation/API/wire claim。owner 文档仍只是入口；
`verified_against` 指向的 code/tests/fixtures 才是复核依据。

| `claim_id` | 窄 claim / owner | role · lifecycle | `verified_against` | `read_when` |
|---|---|---|---|---|
| `sj.beta.supported-roles` | Online Host、read-only/offline、Derived consumer、migration 与 composition-root role allowlist；[Beta snapshot](session-journal-beta-contract-snapshot.md) §1 + §2 role table/general boundary | `canonical-contract` · `current` | `api`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；Engine/ReadView public authority + mutation-gate tests；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 选择 consumer entry、收窄 public API 或判断 low-level seam 是否受支持 |
| `sj.raw-wire.beta-summary` | A-level event IDs/body versions、frozen identifiers、strict rejection 与 Prepared bounds 摘要；[Beta snapshot](session-journal-beta-contract-snapshot.md) §3 | `canonical-contract` · `current` | `wire`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；SessionEventCodec、Prepared manifest/reconstruction + strictness fixtures；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 修改 event kind/body、Prepared、strict reader、canonical bytes 或 recovery wire |
| `eadr.vocabulary.core` | Recap/Memory/Context、Parent lineage、anchor/cursor、Building/Published 与 fail-closed predicates；[EADR concepts](event-addressed-derived-recap-concepts.md) | `concept` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；Core context、Store/Planner contracts + component guides；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 理解术语、authority model、durable phase 或讨论 normalization |
| `eadr.cadence.current` | NewPlanning 使用 HistoryLoad config V2；HistoryUnit count 只承担 structure/baseline/raw-safety；[EADR concepts](event-addressed-derived-recap-concepts.md) | `concept`, `component-guide` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；config V2 resolver、HistoryLoad projector、plan evaluator + Resume/Restore tests；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 修改 trigger、reserve、rolling interval、estimator identity 或 frozen-config boundary |
| `eadr.ownership.store-planner-maintainers` | Store persistence/selection/materialization、Planner scheduling/frozen execution、Maintainers profile/prompt、composition-root assembly boundary；[EADR concepts](event-addressed-derived-recap-concepts.md) | `concept` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；component project references/guides + public authority tests；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 新增跨 assembly reference、移动 authority 或合并 component contract |
| `eadr.store.current-usage` | engine-bound LineageView、metadata-issued authority、bounded selection、exact materialization/Restore 与 codec入口；Store guide | `component-guide` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；Store contracts/codec/lineage/publisher/installer + authority/codec tests；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 调用 Store 或修改 publication、selection、Restore、authority issuance、codec |
| `eadr.planner.current-usage` | Building-first、NewPlanning config V2、frozen Resume/Restore 与 typed bounded-prefix execution；Planner guide | `component-guide` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；config/evaluator/coordinator/frozen barrier + focused tests；[DG1](session-journal-document-governance-dg1-pilot-report.md) | 调用 Planner 或修改 config、cadence、frozen execution、runtime authority |

## Normative、frozen 与 closed entries

Normative entry 规定当前采用的 Shape/Rule 或变更判据，但不伪造 implementation `verified_against`。
Frozen/closed entry 只用于审计其 exact candidate 或 delivery/review closeout。

| `claim_id` | role · lifecycle | 窄边界 | 入口 |
|---|---|---|---|
| `eadr.target.durable-shape` | `target-design` · `current` | normative durable directory/phase、atomic publication、strict ordinal、exact-slot recovery 与 accepted schema cutover；不拥有 current API/status | [V4 target design](event-addressed-derived-recap-v4-target-design.md)；裁决见 [DG1](session-journal-document-governance-dg1-pilot-report.md) |
| `eadr.normalization.gate` | `canonical-contract` · `current` | normative：合并同构类型前比较合法状态/行为、authority、proof/verification obligation 与 durable reader language | [EADR concepts](event-addressed-derived-recap-concepts.md) 的 `Contract normalization gate` |
| `sj.beta.candidate-49ebb463` | `canonical-contract`, `evidence` · `frozen` | 只认证 exact candidate `49ebb4634e5b4136032db983dd92a9a4560b33eb` 的 Beta acceptance 与 §7 evidence boundary | [Beta snapshot](session-journal-beta-contract-snapshot.md) §7 |
| `eadr.implementation.r0-r3-ch-closeout` | `completion-record`, `historical` · `closed` | R0～R3、C0～C3、H0～H2 的工作分解、交付顺序、commit/evidence map 与当时验收边界 | [implementation completion record](event-addressed-derived-recap-v4-implementation-plan.md) |
| `eadr.normalization.decision-49ebb463` | `review`, `completion-record`, `evidence` · `closed` | `cd804c39..49ebb463` candidate ledger、adopt/reject/defer decision、commit map 与 residual risks | [normalization closeout](session-journal-semantic-preserving-contract-normalization-review-report.md) |

## Safety escalation

遇到以下主题时，不要停在本 router、snapshot 或设计文档：

- wire/schema/codec/canonical bytes、Prepared/Resume/Restore/tool continuation；
- raw Parent lineage、bounded proof、exact-head mutation、strict ordinal、repair/corruption；
- migration/import/replay、path/lock/fsync/crash/atomic publication。

必须继续定位 current code owner、focused tests 与 fixtures/goldens；检查 target 没有被当作 checkout 事实。
若接受 contract 变化，建立独立 candidate 与 verification gate。

## 维护本 router

分类、claim ownership、review close 与验证更新规则见
[SessionJournal 文档治理计划](session-journal-document-governance-plan.md)。DG1 report 是 closed
decision/evidence record，不是第二份 active ledger。只有 DG1 后续工作包实际裁决并核验的 claim 才能加入这里；
目录、日期、标题、`README.md` 或 `public` 均不自动授予 authority。

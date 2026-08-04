# SessionJournal 文档入口

状态：Frozen / Superseded discovery router and claim ledger

Source snapshot：`bb3d99b3ac8f170363373ceec120735dba630b2a`（2026-08-04）

本文完整保留被替代的 task router 与 claim ledger，供审计当时的路由、baseline 和裁决边界。
表中的 `current` 只描述 source snapshot，不认证后续 HEAD，也不得继续更新 SHA 或 test totals。
当前入口是 [SessionJournal 文档入口](../../README.md)，代码定位从
[当前架构与代码地图](../../current/architecture-and-code-map.md) 开始。下文相对链接仅因归档迁移而
机械改写，不改变冻结内容的语义。

本文只负责按任务发现 SessionJournal 文档，不是 raw、wire、recovery 或 implementation authority。
DG1 claim 在 exact commit `cf3c77d524abdf24352400c221e0c42f0c9cb2fe` 上核验；本页后来发布的
Core/Recovery 与 Recap/Host claim 各自在表内记录自己的 exact baseline、scope 与 evidence。
这些 SHA 都只是 verification baseline，不表示后续 HEAD 自动通过相同 gate。代码变化后必须重跑
对应 scope，不能机械更新 SHA。

从下表与任务最接近的一行开始，通常先读 2～4 份文档。遇到本文末尾的 safety trigger 时，立即继续读取
current code、tests 与 fixtures，不受默认阅读预算限制。

首次接触 SessionJournal、定位 current assembly ownership、owner code 或 focused tests 时，先读
[当前架构与代码地图](../../current/architecture-and-code-map.md)。该地图只是 agent-first navigation，
不取代下表的 canonical contract、normative design、current code/tests 或 acceptance evidence。

## 按任务阅读

| 任务 | 首读入口 | 再读入口 | 边界 / escalation |
|---|---|---|---|
| 选择 Beta-supported consumer entry path | [Beta snapshot](../../current/contracts/session-journal-beta-contract-snapshot.md) §1 与 §2 role table/general boundary | DG1 的 `sj.beta.supported-roles` | current claim 不覆盖 §2 后续 candidate-specific Store/Galatea implementation detail，也不把所有 `public` surface 视为 supported |
| 修改 raw event kind/body、Prepared request 或 strict reader | [Beta snapshot](../../current/contracts/session-journal-beta-contract-snapshot.md) §3 | DG1 的 `sj.raw-wire.beta-summary`，再读 current codec/tests | 只发布 A-level 摘要；canonical bytes、rejection language 与 recovery wire 必须 against code/fixtures 核对 |
| 理解 raw Parent lineage、branch-local authority 或 as-of setup | [Core guide](../../../../prototypes/SessionJournal/README.md#30-秒心智模型) §30 秒心智模型与 [§Setup 变更](../../../../prototypes/SessionJournal/README.md#setup-变更) | [Roadmap](../studies/event-sourced-session-architecture-roadmap.md#3-核心决策) §3 与 §5.1 | 默认入口是 current Core guide/code/tests；不要从历史 trunk/tail 长文推断 current API |
| 修改 Prepared/Started、Core `SendAsync` / `ResumeAsync` 或 crash recovery | [Core guide §Send 与 recovery](../../../../prototypes/SessionJournal/README.md#send-与-recovery) | [Uncertain external effects contract](../../current/recovery/uncertain-external-effects.md)，再读 current reconstructor/tail resolver/tests | Core `ResumeAsync`恢复 raw execution phase；Prepared/Started使用 frozen request，不重新读取 active Recap config |
| 决定 provider/tool uncertain external effect 是否可自动恢复 | [Uncertain external effects contract](../../current/recovery/uncertain-external-effects.md) | Core runtime recovery code/tests；历史理由才读 [Roadmap §8.4](../studies/event-sourced-session-architecture-roadmap.md#84-future-hardeninguncertain-与-capability-aware-recovery) | provider restart是可能重复的新attempt；tool continuation要求Host证明幂等或可按operation id去重/查询 |
| 修改 bounded lineage/window/setup proof 或 `BeyondPrefix` | [Core guide §面向 Planner / 离线工具的读取](../../../../prototypes/SessionJournal/README.md#面向-planner--离线工具的读取) | current `SessionHistoryPlanning`、`SessionJournalReadView` 与 bounded tests | `BeyondPrefix`不是 `OffLineage`；numeric bound不足时不 hidden-page、不读 payload、不回退 full lineage |
| 修改 runtime/system-prompt setup authority 或 desired reconciliation | [Core guide §Setup 变更](../../../../prototypes/SessionJournal/README.md#setup-变更) | current resolver/reconciliation code/tests；历史理由再读 [Configuration Notes](../studies/session-configuration-access-notes.md) 顶部与 §2 | exact-head `ResolveGoverningSetup`可能走到 root；它与 numeric bounded proof不是同一合同 |
| 区分 Core Resume、Planner Resume 与 Planner Restore | [Core guide §Send 与 recovery](../../../../prototypes/SessionJournal/README.md#send-与-recovery)；[Planner guide §Resume frozen Building](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md#resume-frozen-building)；[§Restore exact Published slot](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md#restore-exact-published-slot) | Core recovery tests或 Planner frozen-execution tests，按实际改动选择 | 三者证明义务不同：raw phase recovery、frozen Building execution、exact Published slot repair，不能因名称相近而合并 |
| 理解 EADR 术语、authority、cadence 与 ownership | [EADR concepts](../../current/derived-recap/concepts.md) | [Store guide](../../../../prototypes/SessionJournal.DerivedRecap.Store/README.md) 或 [Planner guide](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) | concepts 拥有术语与不变量，不拥有全部 current API、wire 或 status |
| 修改 Store selection、publication、materialization 或 Restore | [Store guide](../../../../prototypes/SessionJournal.DerivedRecap.Store/README.md) | [EADR concepts](../../current/derived-recap/concepts.md) | wire、authority、atomic publication、strict ordinal 与 corruption 必须继续读 Store code/tests |
| 修改 Planner cadence、NewPlanning、Resume 或 Restore | [Planner guide](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) | [EADR concepts](../../current/derived-recap/concepts.md)，必要时再读 Store guide | active config 与 frozen execution分离；bounded proof 不能退化为 full scan |
| 修改 config V2、repo loader、active roster 或 planning limits | [Config design §0～§2、§4～§8](../../current/derived-recap/planner-config.md) | [Planner guide §Repo-owned config](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md#repo-owned-config) 与 current codec/resolver tests | Config design §3、§9只是 V1 / delivery history；config validation当前不证明 cadence在raw ceiling前静态可达 |
| 修改 HistoryLoad estimator、framing、projection 或 threshold | [HistoryLoad design §0～§7](../../current/derived-recap/history-load.md) | [Planner guide §HistoryLoad](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md#historyload) 与 evaluator/projector tests | HistoryLoad design §8与calibration是closed evidence；不要把历史提交/test total当current verification |
| 修改 Planner frozen Building、Resume/Restore 或 Host preparation order | [Planner guide §Offline plan/build](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md#offline-planbuild) 与 [§Resume](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md#resume-frozen-building) | [Host Integration §3～§4](../../current/host-integration/derived-recap-host-integration.md#3-public-neutral-contracts) | active roster不是完整execution capability registry；Prepared/Started recovery跳过Store/config |
| 修改 Galatea SessionJournal/DerivedRecap composition | [Host Integration §2～§7](../../current/host-integration/derived-recap-host-integration.md#2-所有权与依赖) | [Galatea cutover closeout](../completed-plans/galatea-session-journal-cutover-plan.md) 只用于历史交付/决策追溯 | current实现以Galatea code/tests为准；cutover totals冻结在2026-08-01，不自动续期 |
| 重跑 Galatea G2A staging acceptance | [G2A runbook](../../operations/galatea-g2a-staging-acceptance.md) | current CLI/Galatea command surfaces、path-safety tests与本轮新生成 evidence | runbook是procedure，不是Passed结果；external staging/provider gates必须每轮实际执行 |
| 评估 EADR durable Shape / Rule | [V4 target design](../../current/derived-recap/durable-target.md) | EADR concepts 与相关 component guide | target 是 accepted normative intent，不是 current codec、API 或 implementation-status owner |
| 提议合并同构 contract 或删除 proof redundancy | [EADR concepts](../../current/derived-recap/concepts.md) 的 `Contract normalization gate` | [normalization closeout](../../evidence/contract-normalization-review.md) | 先比较合法状态/行为、authority、proof obligation 与 durable reader language |
| 审计历史交付或 exact candidate acceptance | [implementation completion record](../completed-plans/event-addressed-derived-recap-v4-implementation-plan.md) 或 normalization closeout | Beta snapshot §7 与 DG1 record | 这些是 closed/frozen evidence，不自动认证 current HEAD |

## Current verified claim ledger

以下是分批against各自baseline核验过的窄 implementation/API/wire claim。owner 文档仍只是入口；
`verified_against` 指向的 code/tests/fixtures 才是复核依据。后加入的细化claim只收窄read route，
不夺取DG1 concept/component claim的既有ownership。

| `claim_id` | 窄 claim / owner | role · lifecycle | `verified_against` | `read_when` |
|---|---|---|---|---|
| `sj.beta.supported-roles` | Online Host、read-only/offline、Derived consumer、migration 与 composition-root role allowlist；[Beta snapshot](../../current/contracts/session-journal-beta-contract-snapshot.md) §1 + §2 role table/general boundary | `canonical-contract` · `current` | `api`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；Engine/ReadView public authority + mutation-gate tests；[DG1](../../evidence/document-governance-dg1-pilot.md) | 选择 consumer entry、收窄 public API 或判断 low-level seam 是否受支持 |
| `sj.raw-wire.beta-summary` | A-level event IDs/body versions、frozen identifiers、strict rejection 与 Prepared bounds 摘要；[Beta snapshot](../../current/contracts/session-journal-beta-contract-snapshot.md) §3 | `canonical-contract` · `current` | `wire`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；SessionEventCodec、Prepared manifest/reconstruction + strictness fixtures；[DG1](../../evidence/document-governance-dg1-pilot.md) | 修改 event kind/body、Prepared、strict reader、canonical bytes 或 recovery wire |
| `sj.core.parent-lineage-authority` | raw events与真实 `Parent` lineage是 branch-local correctness/setup authority；Core guide §30秒模型/§Setup | `component-guide`, `canonical-contract` · `current` | `implementation/api`；`714cf6080717ea24cdd7360fdc796d106258dba7`；`SessionJournalEventReader`、authoritative setup resolver、tail resolver、Engine lineage APIs；bounded/tail/provider-route gate 78/78 | 修改 Parent traversal、branch/as-of setup、checkpoint trust或 authority boundary |
| `sj.recovery.prepared-attempt-current` | Prepared是 frozen request真源；Started event address是 attempt identity；Core inspection与 expected-head-bound Resume恢复 exact phase；uncertain external effect边界由 [current safety contract](../../current/recovery/uncertain-external-effects.md) 拥有 | `component-guide`, `canonical-contract` · `current` | `implementation/api`；`714cf6080717ea24cdd7360fdc796d106258dba7`；manifest codec/reconstructor、tail resolver、runtime recovery与 Engine driver；recovery/tail gate 53/53 + Prepared/codec gate 36/36；safety contract另记exact baseline | 修改 Prepared/Started topology、inspection、Send/Resume或 uncertain policy |
| `sj.core.bounded-proof-contract` | bounded lineage/window/setup proof只在 numeric prefix内判定；不足返回typed `BeyondPrefix`，无 hidden paging或 proof-before-payload 旁路；Core guide §面向 Planner / 离线工具的读取 | `component-guide`, `canonical-contract` · `current` | `implementation/api`；`714cf6080717ea24cdd7360fdc796d106258dba7`；`SessionHistoryPlanning`、`SessionJournalReadView`与 Engine bounded proof/materialization；bounded/readview/setup/provider-route gate 78/78 | 修改 prefix、proof token、continuation evidence、payload materialization或 backpressure |
| `sj.core.setup-authority` | runtime config与system prompt是独立 raw setup；exact-head resolver沿 Parent逐字段解析并可用 validated Prepared checkpoint补缺；Core guide §Setup | `component-guide`, `canonical-contract` · `current` | `implementation/api`；`714cf6080717ea24cdd7360fdc796d106258dba7`；authoritative setup resolver、desired reconciliation、runtime-config v2 codec；bounded/readview/setup/provider-route gate 78/78 | 修改 setup schema、resolver、checkpoint refs、reconciliation或 bounded setup proof |
| `eadr.vocabulary.core` | Recap/Memory/Context、Parent lineage、anchor/cursor、Building/Published 与 fail-closed predicates；[EADR concepts](../../current/derived-recap/concepts.md) | `concept` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；Core context、Store/Planner contracts + component guides；[DG1](../../evidence/document-governance-dg1-pilot.md) | 理解术语、authority model、durable phase 或讨论 normalization |
| `eadr.cadence.current` | NewPlanning 使用 HistoryLoad config V2；HistoryUnit count 只承担 structure/baseline/diagnostics，raw event count 只承担 resource safety/backpressure；[EADR concepts](../../current/derived-recap/concepts.md) | `concept`, `component-guide` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；config V2 resolver、HistoryLoad projector、plan evaluator + Resume/Restore tests；[DG1](../../evidence/document-governance-dg1-pilot.md) | 修改 trigger、reserve、rolling interval、estimator identity 或 frozen-config boundary |
| `eadr.ownership.store-planner-maintainers` | Store persistence/selection/materialization、Planner scheduling/frozen execution、Maintainers profile/prompt、composition-root assembly boundary；[EADR concepts](../../current/derived-recap/concepts.md) | `concept` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；component project references/guides + public authority tests；[DG1](../../evidence/document-governance-dg1-pilot.md) | 新增跨 assembly reference、移动 authority 或合并 component contract |
| `eadr.store.current-usage` | engine-bound LineageView、metadata-issued authority、bounded selection、exact materialization/Restore 与 codec入口；[Store guide](../../../../prototypes/SessionJournal.DerivedRecap.Store/README.md) | `component-guide` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；Store contracts/codec/lineage/publisher/installer + authority/codec tests；[DG1](../../evidence/document-governance-dg1-pilot.md) | 调用 Store 或修改 publication、selection、Restore、authority issuance、codec |
| `eadr.planner.current-usage` | Building-first、NewPlanning config V2、frozen Resume/Restore 与 typed bounded-prefix execution；[Planner guide](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) | `component-guide` · `current` | `implementation`；`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`；config/evaluator/coordinator/frozen barrier + focused tests；[DG1](../../evidence/document-governance-dg1-pilot.md) | 调用 Planner 或修改 config、cadence、frozen execution、runtime authority |
| `eadr.planner.config-v2-repository-current` | Config V2 repo path、strict load/resolve、single snapshot、active roster与planning ceilings；[Config design §0～§2、§4～§8](../../current/derived-recap/planner-config.md) | `target-design`, `component-guide` · `current` | `implementation/api`；`eda5ee979b5df1ab383fddf20d0691bb891a00d1`；config codec/loader/resolver/repository source；Planner focused gate 110/110 | 修改config wire/path、resolver、catalog、limits或NewPlanning config provenance；这是 `eadr.planner.current-usage` 的config细化，不重定义其总边界 |
| `eadr.planner.history-load-cadence-current` | `o200k_base` estimator identity、per-unit framing、baseline projection与HistoryLoad trigger/admission；[HistoryLoad design §0～§7](../../current/derived-recap/history-load.md) | `target-design`, `component-guide` · `current` | `implementation`；`eda5ee979b5df1ab383fddf20d0691bb891a00d1`；HistoryLoad contracts/estimator/projector/evaluator；Planner focused gate 110/110 | 修改HistoryLoad数值identity、framing、R/B、cache或admission；这是concept claim `eadr.cadence.current` 的implementation owner |
| `eadr.planner.frozen-execution-current` | Building-first preparation签发closed authority；Frozen Building、Resume与Restore不读取active config，exact lookup完整capability registry；[Planner guide](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) | `component-guide`, `canonical-contract` · `current` | `implementation/api`；`eda5ee979b5df1ab383fddf20d0691bb891a00d1`；operation preparer/prepared executor/restore/deferred registry；Planner focused gate 110/110 | 修改preparation order、authority union、Resume/Restore输入或Maintainer activation；不与Core raw `ResumeAsync`合并 |
| `eadr.ownership.composition-current` | Store→Core、Planner→Store、Maintainers→Core+Completion.Abstractions；CLI/Galatea拥有concrete composition，Planner不引用Maintainers；[Host Integration §2](../../current/host-integration/derived-recap-host-integration.md#2-所有权与依赖) | `target-design`, `component-guide` · `current` | `implementation`；`eda5ee979b5df1ab383fddf20d0691bb891a00d1`；三个component project references与guides；Maintainers focused gate 22/22 | 新增project reference、移动config/prompt/phase ownership或考虑Hosting assembly；这是既有ownership concept的composition细化 |
| `galatea.derived-recap.integration-current` | Galatea已使用SessionJournal + Store/Planner/Maintainers public kernel，并保留Host-owned phase/connection/logging/UI；[Host Integration §2～§7](../../current/host-integration/derived-recap-host-integration.md#2-所有权与依赖) | `target-design`, `component-guide` · `current` | `implementation`；`eda5ee979b5df1ab383fddf20d0691bb891a00d1`；Galatea project refs/composition/session host；deterministic Galatea gate 14/14 | 修改Galatea recap composition、recovery binding、readiness、maintenance或UI projection；历史cutover文档不拥有current code |
| `galatea.g2a.runbook-current-procedure` | fresh staging、raw invariant、disposable clone、content-free evidence与no-promotion步骤；[G2A runbook](../../operations/galatea-g2a-staging-acceptance.md) | `runbook` · `current` | `operational-procedure`；`eda5ee979b5df1ab383fddf20d0691bb891a00d1`；current CLI command surface + deterministic Galatea gate 14/14；external staging/provider gate未形成current HEAD Passed evidence | 执行或修改G2A流程；每轮必须重新生成Passed/Failed/NotRun evidence，不能从runbook存在推断成功 |

## Normative、frozen 与 closed entries

Normative entry 规定当前采用的 Shape/Rule 或变更判据，但不伪造 implementation `verified_against`。
Frozen/closed entry 只用于审计其 exact candidate 或 delivery/review closeout。

| `claim_id` | role · lifecycle | 窄边界 | 入口 |
|---|---|---|---|
| `eadr.target.durable-shape` | `target-design` · `current` | normative durable directory/phase、atomic publication、strict ordinal、exact-slot recovery 与 accepted schema cutover；不拥有 current API/status | [V4 target design](../../current/derived-recap/durable-target.md)；裁决见 [DG1](../../evidence/document-governance-dg1-pilot.md) |
| `eadr.normalization.gate` | `canonical-contract` · `current` | normative：合并同构类型前比较合法状态/行为、authority、proof/verification obligation 与 durable reader language | [EADR concepts](../../current/derived-recap/concepts.md) 的 `Contract normalization gate` |
| `sj.recovery.uncertain-hardening-target` | `target-design` · `current` | normative future：provider result lookup/reconcile、capability-aware retry与非幂等工具 paused/uncertain；不声称 current runtime 已实现 | [Uncertain external effects contract §未实现的 future target](../../current/recovery/uncertain-external-effects.md#未实现的-future-target)；归档Roadmap只保留historical rationale |
| `sj.beta.candidate-49ebb463` | `canonical-contract`, `evidence` · `frozen` | 只认证 exact candidate `49ebb4634e5b4136032db983dd92a9a4560b33eb` 的 Beta acceptance 与 §7 evidence boundary | [Beta snapshot](../../current/contracts/session-journal-beta-contract-snapshot.md) §7 |
| `sj.recovery.cs3d-history` | `completion-record`, `historical` · `closed` | CS-3A～CS-3D7 tail recovery、configuration checkpoint与后续化简的 cut-time design/decision/evidence；不拥有 current API | [Tail recovery completion record](../completed-plans/tail-execution-recovery-design.md)、[Configuration Notes](../studies/session-configuration-access-notes.md)、[simplification study](../studies/tail-execution-recovery-simplification-study.md) |
| `eadr.implementation.r0-r3-ch-closeout` | `completion-record`, `historical` · `closed` | R0～R3、C0～C3、H0～H2 的工作分解、交付顺序、commit/evidence map 与当时验收边界 | [implementation completion record](../completed-plans/event-addressed-derived-recap-v4-implementation-plan.md) |
| `eadr.normalization.decision-49ebb463` | `review`, `completion-record`, `evidence` · `closed` | `cd804c39..49ebb463` candidate ledger、adopt/reject/defer decision、commit map 与 residual risks | [normalization closeout](../../evidence/contract-normalization-review.md) |
| `eadr.history-load.h0-h2-c3-closeout` | `completion-record`, `evidence` · `historical`, `closed` | H0～H2/C3 delivery order、2026-07-31 calibration与当时real-repo evidence；不拥有current implementation status | [HistoryLoad design §8](../../current/derived-recap/history-load.md#8-实施-gates) 与 [Galatea calibration](../../evidence/history-load-galatea-calibration.md) |
| `galatea.sessionjournal-cutover-2026-08-01` | `completion-record` · `historical`, `closed` | H0～H2、G0A～G2B 的交付决策、commit/test/evidence map与activation边界 | [Galatea cutover closeout](../completed-plans/galatea-session-journal-cutover-plan.md) |
| `galatea.g2a.acceptance-2026-08-01` | `evidence` · `frozen`, `closed` | 只记录2026-08-01 exact export/staging/provider/Host acceptance；不认证current HEAD或下一轮run | [Cutover G2A closeout](../completed-plans/galatea-session-journal-cutover-plan.md#g2arepeatable-staging-acceptancedone2026-08-01) |
| `galatea.g3.warmup-target` | `target-design` · `deferred` | 可选post-response warm-up；当前未实现，需真实延迟证据与独立设计/验收后才能进入implementation | [Cutover G3](../completed-plans/galatea-session-journal-cutover-plan.md#g3post-response-recap-warm-up可选deferred) |

## Safety escalation

遇到以下主题时，不要停在本 router、snapshot 或设计文档：

- wire/schema/codec/canonical bytes、Prepared/Resume/Restore/tool continuation；
- raw Parent lineage、bounded proof、exact-head mutation、strict ordinal、repair/corruption；
- migration/import/replay、path/lock/fsync/crash/atomic publication。

必须继续定位 current code owner、focused tests 与 fixtures/goldens；检查 target 没有被当作 checkout 事实。
若接受 contract 变化，建立独立 candidate 与 verification gate。

## 维护本 router

分类、claim ownership、review close 与验证更新规则的历史决策见
[SessionJournal 文档治理计划](../completed-plans/session-journal-document-governance-plan.md)。DG1 report 是 closed
decision/evidence record，不是第二份 active ledger。只有实际裁决并核验的 claim 才能加入这里；
目录、日期、标题、`README.md` 或 `public` 均不自动授予 authority。当前 contract/design集中在
`current/`，可重复操作流程集中在`operations/`；物理目录仍不替代claim级authority判断。

结构检查使用tracked explicit scope：

```bash
python scripts/check_session_journal_docs.py
```

默认路径列表见
[`session-journal-doc-check-scope.txt`](../../session-journal-doc-check-scope.txt)。checker先用`git ls-files`
确认scope与每个输入均已tracked，再读取Markdown；因此未纳入版本库的review/report不会被隐式读取。
它只检查local target的tracked membership、worktree存在性、逐ancestor symlink安全、path case/repo
escape，以及两张ledger的exact section/header/claim结构；不访问网络、不写report、不修复文件，首版也
不校验anchor/GitHub slug。

需要连同closed/historical corpus一起检查链接安全时运行：

```bash
python scripts/check_session_journal_docs.py --all-tracked --report-only
```

`--report-only`即使发现diagnostic也返回0，只供治理盘点；它不是CI gate，也不能把历史噪声解释成
current文档失败。移除`--report-only`后，任何diagnostic都会返回1。文档物理迁移后，default scope与
all-tracked audit都必须保持0项diagnostic。

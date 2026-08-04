# SessionJournal 文档治理 DG1：Pilot claim 核验报告

> **状态**：Closed review / evidence artifact  
> **核验基线**：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`  
> **范围**：EADR concepts、Beta snapshot、V4 target/completion record、最近一次 tracked
> normalization review、Store/Planner component guide  
> **非 authority 边界**：本文记录一次性的 claim 裁决和核验证据，不是持续更新的 active ledger，
> 也不替代 code、wire codec、component README 或后续 domain router。

本文关闭
[SessionJournal 文档治理计划](../archive/completed-plans/session-journal-document-governance-plan.md) 的 DG1。核验发生在上面的
exact full commit；本包只修正文档，未把基线机械提升到修正文档后的 commit。DG2 只能发布本报告中
`Accept` 或已经落实文字修正的 `Modify` entry，不能把未核验的文档标题或整篇文档提升为 current
authority。

## 1. Candidate 与 current checkout 边界

[`SessionJournal Beta contract snapshot`](../current/contracts/session-journal-beta-contract-snapshot.md) 冻结的 product
candidate 是 `49ebb4634e5b4136032db983dd92a9a4560b33eb`；它的 candidate-specific clone、build、test 与
real-data/staging evidence 只证明该 exact candidate，不能自动转移到 DG1 基线
`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`。

从 `49ebb463` 到 DG1 基线，raw event kind/body version 与 A-level codec reader language没有发生变化，
但 Store/Planner/Core authority、bounded lineage 与 mutation gate 的实现和 focused tests已经继续演进。
因此本报告分别处理两类 claim：

- candidate acceptance 与 normalization decision 保持 `frozen` / `closed`，exact 指向 `49ebb463`；
- current roles、raw-wire summary、Store/Planner usage 则重新 against DG1 基线的 code/tests 核验，
  不借用 candidate test totals 续期。

`gitignore/session-journal/...` 下的 run-specific inventory、blind-review 原文和可能包含环境信息的输出
只可作为本地辅助；它们不是 tracked、portable 的 contract authority，也不是以下任何结论的唯一证据。

## 2. Pilot decision summary

| `claim_id` | 裁决 | DG2 可发布的窄结论 |
|---|---|---|
| `sj.beta.supported-roles` | **Modify → current** | 发布 role allowlist 与 authority boundary；不把所有 `public` surface 视为 supported。 |
| `sj.beta.candidate-49ebb463` | **Accept → frozen** | 只发布 exact candidate 的 Beta acceptance 与其 evidence boundary。 |
| `sj.raw-wire.beta-summary` | **Modify → current** | 发布当前 A-level kind/version/strict-reader 摘要；candidate test totals仍保持 frozen。 |
| `eadr.vocabulary.core` | **Modify → current** | concepts拥有术语/不变量；completion record不再是唯一 current 状态源。 |
| `eadr.cadence.current` | **Modify → current** | NewPlanning使用HistoryLoad config V2；count只承担结构与safety职责。 |
| `eadr.ownership.store-planner-maintainers` | **Accept → current** | 保持 Store / Planner / Maintainers / composition root 的不同 proof obligations。 |
| `eadr.target.durable-shape` | **Modify → current normative** | target只拥有 durable invariant/accepted target，不拥有 current API/status。 |
| `eadr.implementation.r0-r3-ch-closeout` | **Modify → closed** | implementation plan改为 completion record / mixed historical。 |
| `eadr.store.current-usage` | **Accept → current** | Store README 是 current usage/code map，不是 wire本身。 |
| `eadr.planner.current-usage` | **Accept → current** | Planner README 是 current scheduling/frozen execution usage/code map。 |
| `eadr.normalization.gate` | **Accept → current normative** | 同构 contract 合并前比较合法状态、authority与proof obligation。 |
| `eadr.normalization.decision-49ebb463` | **Accept → closed** | normalization report只拥有该轮 candidate ledger/decision/evidence map。 |

## 3. Claim registry

### `sj.beta.supported-roles`

- `document`：`docs/SessionJournal/session-journal-beta-contract-snapshot.md`
- `doc_role`：`canonical-contract`
- `lifecycle`：`current`
- `owner`：SessionJournal Beta support boundary
- `canonical_for`：Online Host、read-only/offline、Derived consumer、migration 与 composition-root
  的 supported entry roles；不覆盖所有 public 类型
- `read_when`：选择 consumer entry path、收窄 public API 或判断某个 low-level seam 是否受 Beta 支持时
- `decision`：**Modify → current**；采用 snapshot §1～§2 的窄 role/authority claim，并以 DG1
  checkout 重新核验；不继承 §7 的 candidate-only test totals
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：`prototypes/SessionJournal/SessionJournalEngine.cs`、
  `prototypes/SessionJournal/SessionJournalReadView.cs`、public authority tests 与 mutation-gate tests
- `verified_against.kind`：`api`
- `verified_against.evidence`：tracked snapshot §1～§2；
  `tests/SessionJournal.Tests/SessionJournalPublicAuthorityTests.cs`；
  `tests/SessionJournal.Tests/SessionJournalMutationGateTests.cs`；§4 的 Core focused command

### `sj.beta.candidate-49ebb463`

- `document`：`docs/SessionJournal/session-journal-beta-contract-snapshot.md`
- `doc_role`：`canonical-contract`, `evidence`
- `lifecycle`：`frozen`
- `owner`：Beta candidate acceptance record
- `canonical_for`：exact product candidate `49ebb4634e5b4136032db983dd92a9a4560b33eb`
  的 Beta-supported acceptance 与 verification boundary
- `read_when`：审计首个 Beta candidate、candidate-specific clone/gate 或区分历史 acceptance 与 current
  checkout 时
- `decision`：**Accept → frozen**；不将该 candidate 的 build/test/real-data totals解释为 DG1 HEAD 的
  verification
- `verified_against.full_commit`：`49ebb4634e5b4136032db983dd92a9a4560b33eb`
- `verified_against.scope`：snapshot §7 记录的 candidate-specific Clone A/B、default tests、real-data 与
  staging acceptance；tracked normalization closeout
- `verified_against.kind`：`operational-evidence`
- `verified_against.evidence`：tracked snapshot §7；
  `docs/SessionJournal/evidence/contract-normalization-review.md` §7～§8；
  `docs/SessionJournal/galatea-g2a-staging-acceptance-runbook.md`

### `sj.raw-wire.beta-summary`

- `document`：`docs/SessionJournal/session-journal-beta-contract-snapshot.md`
- `doc_role`：`canonical-contract`
- `lifecycle`：`current`
- `owner`：SessionJournal raw/recovery codec contract
- `canonical_for`：A-level event kind numeric IDs、body versions、frozen identifiers、strict rejection 与
  Prepared exact-input bounds 的摘要
- `read_when`：修改 event kind/body、Prepared request、strict reader、canonical bytes 或 recovery wire 时
- `decision`：**Modify → current**；保留 snapshot §3 的 wire摘要，并以 current codec/tests重新核验；
  删除“candidate `49ebb463`无 wire delta”之外的 candidate-status含义
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：`prototypes/SessionJournal/SessionEventCodec.cs`、raw event contracts、Prepared manifest
  codec/reconstruction 与 strictness fixtures
- `verified_against.kind`：`wire`
- `verified_against.evidence`：snapshot §3；
  `tests/SessionJournal.Tests/SessionEventCodecStrictnessTests.cs`；
  `tests/SessionJournal.Tests/SessionRequestManifestCodecTests.cs`；§4 的 Core focused command

### `eadr.vocabulary.core`

- `document`：`docs/SessionJournal/event-addressed-derived-recap-concepts.md`
- `doc_role`：`concept`
- `lifecycle`：`current`
- `owner`：EADR domain vocabulary and invariants
- `canonical_for`：Recap/Memory/Context、raw Parent lineage、anchor/cursor、Building/Published 与
  fail-closed predicates 的 current vocabulary
- `read_when`：理解 EADR 术语、authority model、durable phase 或讨论 contract normalization 时
- `decision`：**Modify → current**；已删除 implementation plan 是“唯一 current状态源”的漂移，
  concepts不再冒充 API/status owner
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：Core context contracts、Store contracts/codec、Planner contracts 与三个
  component README
- `verified_against.kind`：`implementation`
- `verified_against.evidence`：本文档 §0～§3；
  `prototypes/SessionJournal.DerivedRecap.Store/README.md`；
  `prototypes/SessionJournal.DerivedRecap.Planner/README.md`

### `eadr.cadence.current`

- `document`：`docs/SessionJournal/event-addressed-derived-recap-concepts.md`
- `doc_role`：`concept`, `component-guide`
- `lifecycle`：`current`
- `owner`：DerivedRecap Planner scheduling boundary
- `canonical_for`：NewPlanning 的 HistoryLoad config V2 cadence，以及 HistoryUnit count 的
  structure/baseline/raw-safety非触发职责
- `read_when`：修改 trigger、recent reserve、rolling interval、estimator identity 或 Resume/Restore config
  boundary 时
- `decision`：**Modify → current**；已删除“当前仍按 HistoryUnit count 调度”的过时描述
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：Planner config V2 codec/resolver、HistoryLoad contracts/projector、plan evaluator、
  NewPlanning 与 Resume/Restore tests
- `verified_against.kind`：`implementation`
- `verified_against.evidence`：`prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerConfigCodec.cs`；
  `prototypes/SessionJournal.DerivedRecap.Planner/RecapPlanEvaluator.cs`；
  `tests/SessionJournal.DerivedRecap.Planner.Tests/RecapHistoryLoadProjectorTests.cs`；§4 Planner command

### `eadr.ownership.store-planner-maintainers`

- `document`：`docs/SessionJournal/event-addressed-derived-recap-concepts.md`
- `doc_role`：`concept`
- `lifecycle`：`current`
- `owner`：EADR assembly and authority boundaries
- `canonical_for`：Store persistence/selection/materialization、Planner scheduling/frozen execution、
  Maintainers profile/prompt 与 composition-root ownership 分工
- `read_when`：新增跨 assembly reference、移动 authority、合并 component contract 或设计 Host composition 时
- `decision`：**Accept → current**；结构相似不消除各 stage 的 proof obligation
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：Core/Store/Planner/Maintainers project references、component README 与
  public authority tests
- `verified_against.kind`：`implementation`
- `verified_against.evidence`：各 component `.csproj`；
  `prototypes/SessionJournal.DerivedRecap.Store/README.md`；
  `prototypes/SessionJournal.DerivedRecap.Planner/README.md`

### `eadr.target.durable-shape`

- `document`：`docs/SessionJournal/event-addressed-derived-recap-v4-target-design.md`
- `doc_role`：`target-design`
- `lifecycle`：`current`
- `owner`：EADR normative durable Shape / Rule
- `canonical_for`：durable directory/phase、atomic publication、strict ordinal、exact-slot recovery 与
  accepted schema cutover的 normative invariants；不覆盖 current API/status
- `read_when`：改变 durable shape、publication/recovery semantics 或评估实现是否偏离设计时
- `decision`：**Modify → current normative**；顶部删除 current-status authority，§12 对齐 accepted
  manifest/publication v6、frozen-input v5、Store/block v4
- `verified_against`：不适用；这是 normative target claim，不伪装成 implementation verification。
  exact current codec/reader claim由 `eadr.store.current-usage` 与 code/tests承担

### `eadr.implementation.r0-r3-ch-closeout`

- `document`：`docs/SessionJournal/event-addressed-derived-recap-v4-implementation-plan.md`
- `doc_role`：`completion-record`, `historical`
- `lifecycle`：`closed`
- `owner`：EADR R0～R3、C0～C3、H0～H2 delivery record
- `canonical_for`：已关闭工作包的分解、顺序、commit/evidence map 与当时验收边界
- `read_when`：审计 EADR 如何交付、定位历史 work package/commit 或理解 rollout 顺序时
- `decision`：**Modify → closed**；顶部已明确 completion record / mixed historical，不再承担 current
  API、wire 或 implementation-status authority
- `decision_or_closing_record`：本文档 §0.2 及各 package closeout；本 DG1 报告

### `eadr.store.current-usage`

- `document`：`prototypes/SessionJournal.DerivedRecap.Store/README.md`
- `doc_role`：`component-guide`
- `lifecycle`：`current`
- `owner`：DerivedRecap Store implementation usage and code map
- `canonical_for`：engine-bound LineageView、metadata-issued authority、bounded fail-closed selection、exact
  materialization/restore 与 current codec入口
- `read_when`：调用 Store、修改 publication/selection/restore、authority issuance 或 durable codec 时
- `decision`：**Accept → current**；README是贴近实现的 usage/code map，不取代 codec reader language
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：Store contracts/codec/lineage view/publisher/installer 与 authority/codec tests
- `verified_against.kind`：`implementation`
- `verified_against.evidence`：`prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapCodec.cs`；
  `prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapLineageView.cs`；
  `tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapAuthorityBoundaryTests.cs`；§4 Store command

### `eadr.planner.current-usage`

- `document`：`prototypes/SessionJournal.DerivedRecap.Planner/README.md`
- `doc_role`：`component-guide`
- `lifecycle`：`current`
- `owner`：DerivedRecap Planner implementation usage and code map
- `canonical_for`：Building-first lifecycle、NewPlanning config V2、frozen Resume/Restore、typed bounded-prefix
  planning与execution
- `read_when`：调用 Planner、修改 config resolution、planning cadence、frozen execution 或 runtime
  authority 时
- `decision`：**Accept → current**
- `verified_against.full_commit`：`cf3c77d524abdf24352400c221e0c42f0c9cb2fe`
- `verified_against.scope`：Planner config/resolution/evaluator/online coordinator/frozen barrier 与 focused tests
- `verified_against.kind`：`implementation`
- `verified_against.evidence`：`prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerConfigResolution.cs`；
  `prototypes/SessionJournal.DerivedRecap.Planner/DerivedRecapOnlineLifecycleCoordinator.cs`；
  `tests/SessionJournal.DerivedRecap.Planner.Tests/RecapRuntimeAuthorityTests.cs`；§4 Planner command

### `eadr.normalization.gate`

- `document`：`docs/SessionJournal/event-addressed-derived-recap-concepts.md`
- `doc_role`：`canonical-contract`
- `lifecycle`：`current`
- `owner`：EADR contract-change review rule
- `canonical_for`：合并同构类型前比较合法状态/行为、authority、proof/verification obligation 与 durable
  reader language 的 normative gate
- `read_when`：提议合并 result、health、phase、state machine、opaque authority 或删去 proof redundancy 时
- `decision`：**Accept → current normative**
- `verified_against`：不适用；这是 contract-change rule，不声称某个实现已被自动证明

### `eadr.normalization.decision-49ebb463`

- `document`：`docs/SessionJournal/evidence/contract-normalization-review.md`
- `doc_role`：`review`, `completion-record`, `evidence`
- `lifecycle`：`closed`
- `owner`：N0～N5 normalization review closeout
- `canonical_for`：`cd804c39..49ebb463` normalization candidate ledger、adopt/reject/defer决策、commit map
  与 residual risks
- `read_when`：审计为何删除/保留某个 contract、复核 candidate `49ebb463` 或避免重复提出已裁决化简时
- `decision`：**Accept → closed**；不承担 DG1 HEAD 的 current contract
- `verified_against.full_commit`：`49ebb4634e5b4136032db983dd92a9a4560b33eb`
- `verified_against.scope`：normalization report 的 candidate ledger、authority graph、wire matrix、commit map 与
  candidate-specific gates
- `verified_against.kind`：`operational-evidence`
- `verified_against.evidence`：tracked review report；Beta snapshot §7；tracked test/runbook pointers
- `decision_or_closing_record`：
  `docs/SessionJournal/evidence/contract-normalization-review.md`

## 4. Portable verification evidence

下列命令在 DG1 baseline 串行执行，均使用 existing restore state，没有并行 MSBuild node：

```bash
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj \
  -m:1 -nr:false --no-restore \
  --filter "FullyQualifiedName~SessionJournalPublicAuthorityTests|FullyQualifiedName~SessionEventCodecStrictnessTests|FullyQualifiedName~SessionRequestManifestCodecTests|FullyQualifiedName~SessionJournalMutationGateTests"
# 37 passed, 0 failed, 0 skipped

dotnet test tests/SessionJournal.DerivedRecap.Store.Tests/SessionJournal.DerivedRecap.Store.Tests.csproj \
  -m:1 -nr:false --no-restore \
  --filter "FullyQualifiedName~DerivedRecapAuthorityBoundaryTests|FullyQualifiedName~DerivedRecapCodecTests"
# 51 passed, 0 failed, 0 skipped

dotnet test tests/SessionJournal.DerivedRecap.Planner.Tests/SessionJournal.DerivedRecap.Planner.Tests.csproj \
  -m:1 -nr:false --no-restore \
  --filter "FullyQualifiedName~RecapPlanEvaluatorTests|FullyQualifiedName~RecapHistoryLoadProjectorTests|FullyQualifiedName~RecapPlannerConfigRepositoryTests|FullyQualifiedName~RecapRuntimeAuthorityTests"
# 77 passed, 0 failed, 0 skipped
```

Portable evidence manifest：

- raw/API：`prototypes/SessionJournal/` 与 `tests/SessionJournal.Tests/` 中上列四个 focused suites；
- Store：`prototypes/SessionJournal.DerivedRecap.Store/`、其 README 与 Store authority/codec suites；
- Planner：`prototypes/SessionJournal.DerivedRecap.Planner/`、其 README 与 evaluator/HistoryLoad/config/runtime
  authority suites；
- candidate closeout：tracked Beta snapshot、normalization report 与
  `docs/SessionJournal/galatea-g2a-staging-acceptance-runbook.md`；
- exact schema constants：`DerivedRecapCodec.StoreSchema`/`ManifestSchema`/`FrozenInputSchema`/
  `BlockSchema`/`PublicationSchema` = v4/v6/v5/v4/v6。

这些 tracked pointers足以重新执行 focused核验；ignored、temporary、secret-bearing 或 run-specific artifacts
至多补充 provenance，不是 closing claim 的唯一证据。

## 5. Conflict / drift closure

| Drift | 处置 |
|---|---|
| concepts称 implementation plan 是唯一 current 状态/证据源 | 已改为 concepts拥有 vocabulary；component README/code/tests拥有 current implementation/API/wire核验入口。 |
| concepts称 current cadence仍按 HistoryUnit count | 已改为 HistoryLoad config V2 trigger；count只保留结构/baseline/safety职责。 |
| target顶部持续发布具体完成状态 | 已改为 normative target边界；交付状态下沉到 closed completion record。 |
| target §12仍写 manifest/publication v5、frozen input v4 | 已改为 accepted v6/v5 direct cut，并指向 Store codec/README/tests核验 current reader。 |
| implementation plan看似 active/current状态源 | 已改为 Closed Completion Record / Mixed Historical，并明确不拥有 current API/wire/status。 |

本轮没有创建 domain/root README、frontmatter、backlink、checker或目录移动，也没有读取、修改、暂存用户的
untracked Claude review report。

## 6. DG2 publication gate

DG2 可以把 §2 中 12 个已裁决 entry 发布到 `docs/SessionJournal/README.md`，但必须遵守：

1. router只保留 task-first、窄 `canonical_for` / `read_when` 与本 closing record指针；不得复制本报告；
2. `sj.beta.candidate-49ebb463` 与 `eadr.normalization.decision-49ebb463` 明示 frozen/closed，不能伪装成
   current HEAD verification；
3. `eadr.target.durable-shape` 与 `eadr.normalization.gate` 是 normative claim，不伪造 implementation
   `verified_against`；
4. current implementation/API/wire entries继续绑定 full baseline
   `cf3c77d524abdf24352400c221e0c42f0c9cb2fe`，代码变化后必须重跑 scope 才能更新；
5. 同一 `claim_id` 只能发布一个 current owner；未经过本报告裁决的 claim 留待 DG3～DG4。

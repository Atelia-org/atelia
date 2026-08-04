# SessionJournal 语义保持型 Contract Normalization 审阅计划

状态：Review plan，2026-08-03  
Context baseline：`b7f9fa37ed9dcd5150077983e428b8d6646c64d9`  
已验证的上一轮 product candidate：`681fc02bb9f1e4a45cd012aa7feadefe3f33fa9e`

本文是 SessionJournal 首次生产运行前审阅的后续计划。上一轮已经证明当前 candidate 达到 Beta GO；本轮不以
寻找更多 bug、优化性能或压缩代码行为主要目标，而是审阅能否用更少的独立概念、authority path 和 durable
representation，表达相同的功能、恢复能力与鲁棒性。

这里的“化简”不是简单删功能、合并类型或放宽 validator，而是 **semantic-preserving normalization**：

- 对外能力保持完整；
- raw/recovery authority 与 crash/corruption safety 不弱化；
- wire 接受语言保持不变或变得更严格，不接受以前会拒绝的非法输入；
- 减少可独立变化、可互相矛盾、可被 caller 伪造或需要跨层同步的概念。

“没有找到值得实施的化简机会”是合法成功结果。不得为了让本轮看起来有产出而移动已经通过 Beta gate 的
contract。

## 1. 新会话快速入口

新会话应先按顺序读取：

1. 本文；
2. [`first-production-readiness-review.md`](../../evidence/first-production-readiness-review.md)：
   上一轮 findings、修复 commit map 与 R4 证据；
3. [`session-journal-beta-contract-snapshot.md`](../../current/contracts/session-journal-beta-contract-snapshot.md)：当前 Beta support surface、
   A/B/C wire 与 residual risks；
4. [`session-journal-first-production-readiness-review-plan.md`](session-journal-first-production-readiness-review-plan.md)：
   五包审阅方法、finding 格式与稳定性分级；
5. 各工作包列出的 current README、实现与测试。

开始执行前重新记录：

```bash
git status --short
git rev-parse HEAD
dotnet --info
findmnt -T .
free -h
swapon --show
```

本文的 context baseline 只表示撰写计划时的 checkout。新会话不得把它当作未来 execution baseline，也不得把
上一轮 API inventory 数量或测试计数当作当前事实；checkout 变化后必须重新生成。

环境约束：

- 所有 `dotnet build/test` 严格串行，使用 `-m:1 -nr:false`；先 restore，再尽量使用 `--no-restore` /
  `--no-build`，禁止多个 agent 并行运行测试；
- 永久 Galatea session repo
  `/repos/focus/atelia/prototypes/Galatea/.atelia/galatea/sessions/cyber-session-journal`
  不得打开、fingerprint、复制或写入；需要真实 workflow 时只使用 disposable clone；
- 真实 provider 只能在 plan lock 后显式授权有界调用预算，call logs 必须位于 session repo 外；
- run-specific 原始报告放在 `gitignore/session-journal/reviews/<run-id>/`，不自动成为 contract authority。

## 2. 当前已经成立的上下文

上一轮从 review baseline `2ccd6715` 开始，经十份独立盲审、九个 confirmed blocker、逐包修复和独立尾审，
形成 product candidate `681fc02b`。两个 `--no-local` fresh clone 合计通过 2080 tests、10 expected skips、
0 failures，并完成真实数据 import/validate/recap/NoBuild、provider canary、reopen/Undo 和 raw/source invariants。

当前结论是“值得首次 Beta 生产运行”，不是“当前每个 `public` 类型、每一层 wrapper、每个 durable 字段都值得
永久稳定”。上一轮为关闭 authority、bounds 与 recovery gaps 增加了必要规则；本轮要判断这些规则能否由更小、
更正交的 contract 表达。

当前稳定性分级继续有效：

| Tier | 产物 | 本轮默认态度 |
|---|---|---|
| A | raw events、Parent lineage、Prepared commitment、setup/tool recovery | 最高门槛；默认保留，只有高杠杆且完整证明的 normalization 才能 direct cut |
| B | repo-owned `config/recap-planner-config.json` | 严格 schema；允许显式版本切换或重新初始化，不允许 silent migration |
| C | `derived/recap/v4` sidecar | 可重建；可以比 A/B 更积极化简，但必须保留 strict ordinal、atomic publication 与 typed recovery |
| D | public .NET API | Beta 前最适合积极收窄；只冻结明确 support roles，不冻结所有 exported surface |
| E | CLI JSON report、call log | 可以化简 operational evidence，但不得承担 invocation/raw/recovery authority |

任何被接受的 API 或 wire 改动都会产生新的 product candidate；上一轮 R4 GO 不能自动转移到新 commit。

## 3. “语义保持”的判定标准

候选方案必须同时说明以下六类等价性。仅证明单元测试仍通过不够。

### 3.1 Capability equivalence

- 当前 Beta-supported role 仍能完成相同的 Online Host、read-only/offline、Derived consumer、migration 和
  composition workflow；
- 不把“不好表达”当成“可以删除”，也不通过 implicit default 替代显式能力；
- 若删除 overload/type，必须展示唯一推荐路径足以完成原有合法操作。

### 3.2 Authority equivalence

- raw events 与 selected `RefId` Parent lineage 仍是唯一会话事实与恢复 authority；
- Store/Planner/Maintainers/Host ownership 不合并成第二真源；
- caller 不能构造或转抄本应由 Engine/Store 颁发、重建或验证的 authority token/descriptor；
- active config 仍只决定 NewPlanning，Resume/Restore 仍只服从 frozen plan。

### 3.3 State-machine equivalence

- Empty、Idle、Prepared/Started、tool continuation、failed turn、completed/retracted 等 durable phase 的合法
  transition 与 typed recovery outcome 不被吞并；
- 合并 result/phase 只能减少表示重复，不能把不同 operator action 需求折叠成一个模糊状态；
- exact-head、writer lifetime 与 Host serialization 约束不弱化。

### 3.4 Wire-language equivalence

- semantic capability 不变；canonical writer 只有一个；
- reader 接受的 wire 集合保持不变或变窄，不能因字段减少、default 或 tolerant parser 而变宽；
- schema-defined nullable 与 optional 必须逐字段保留，不得用全局“宽松 null”替代；
- canonical bytes 若变化，必须升级相应 schema/version/codec id，并在首次生产前做显式 direct cut；禁止在旧
  version 下静默换算法或解释。

### 3.5 Recovery and robustness equivalence

- unknown/old/truncated/duplicate/wrong-type/out-of-range/hash mismatch 继续 fail closed 或 typed unavailable；
- strict ordinal 不跳过坏 slot，不 fallback 到 full raw 或更旧 Published set；
- missing-only repair、bounded lineage、size/count caps、path safety、atomic publication、fsync/rename 边界不弱化；
- provider exactly-once 仍不是承诺，日志缺失仍不能作为未调用证明。

### 3.6 Operational equivalence

- CLI/Host 不复制 Planner/Store/raw reducer，也不读取不属于当前 phase 的 config/client/Store；
- call log/report 仍是 content-free 或低 authority operational evidence；
- cancellation、fatal exception、writer gate 和 post-commit best-effort failure 不改变 primary durable outcome。

## 4. 应减少什么，不应机械减少什么

本轮关注的是 **独立语义自由度**，不是 LOC、文件数、类型数或 JSON 字段数本身。

优先寻找：

- 表达同一 authoritative mutation 的多个 public 入口；
- 两个可独立写入、但必须永远相等的字段或 artifact；
- caller 可伪造、copy 或跨 Store/Engine lifetime 误用的 descriptor/handle；
- 只镜像内部步骤、没有形成不变量的 public intermediate type；
- 多层 result union 只改变包装、不改变下一步 operator action；
- 重复且可能漂移的 schema/codec/path/type mapping；
- CLI 与 Host 中重复的 config resolution、phase routing、selection 或 recovery；
- 为旧实验 generation 或未支持 consumer 保留的 compatibility path。

不得机械删除：

- 用于 corruption detection 的 hash、length、setup reference、canonical commitment；
- 为 frozen recovery 提供独立证据的 snapshot；
- 把 active authority 与 frozen authority 分开的字段或类型；
- 用于 closed union exhaustive match 的不同 operator action；
- size/count/depth bounds、lineage proof、exact-head fence、path/lock/fsync guard；
- 有明确职责边界的 Store、Planner、Maintainers 分层。

关键区分：

> **authority redundancy 应优先消除；verification redundancy 只有在证明不承担检测、冻结或恢复职责后才能删除。**

若同一事实的两份表达发生冲突：

- 如果两边都可能被独立 writer 改写，通常是双真源；
- 如果一边是 authority、另一边是 commitment/checksum，冲突时明确 fail closed，通常是 intentional proof
  redundancy；
- 如果没有明确 owner、冲突规则和测试，先报告 contract gap，不能直接删任一边。

## 5. 不可协商的不变量与禁区

1. raw events + selected Parent lineage 是唯一 raw authority；
2. DerivedRecap 可删除重建，不向 raw 写 recap identity；
3. Store 管 durable structure/membership/ordinal/publication，Planner 管 schedule/frozen execution，Maintainers
   管 concrete profile/prompt；
4. active config 只用于 NewPlanning；Resume/Restore 不读取 active config/default connection/current roster；
5. `NthPrevious` 保持 strict ordinal，坏 slot 不跳过、不重编号；
6. Published 健康 component 复用，缺失/损坏 component missing-only rebuild；
7. Prepared/Started/tool/failed recovery 保持 exact、typed、fail-closed；
8. bounded prefix 不分页、不自动扩界、不 fallback full scan；
9. Host 串行驱动同一 writable Engine；final reread 是 fence，不伪称 CAS；
10. call log/report 不成为 provider invocation、raw 或 recovery authority；
11. 不增加 compatibility layer、silent fallback、auto reset、猜测迁移或第二套状态机；
12. 不把 accepted risk（例如 hostile concurrent same-directory writer）伪写成已解决。

以下不是本轮目标：

- 性能优化、allocation tuning、缓存策略或 token estimator 改进；
- Galatea tool-capable runtime、background recap scheduler、multi-process service；
- provider exactly-once、remote/distributed Store、backup/replication/full scrub；
- 跨平台真实断电认证；
- 纯命名、格式化、局部代码去重或“让文件更短”的重构；
- 通过删除 typed failure、bounds、hash 或 tests 制造表面简洁。

## 6. 关键文件与 authority 路由

### 6.1 当前 contract 与架构入口

- `docs/SessionJournal/session-journal-beta-contract-snapshot.md`：当前 Beta support/wire authority；
- `docs/SessionJournal/evidence/first-production-readiness-review.md`：上一轮 findings 与验证证据；
- `prototypes/SessionJournal/README.md`：Host、recovery、completed turn、Planner/offline read、setup 与 wire quick check；
- `prototypes/SessionJournal.DerivedRecap.Store/README.md`：frozen capability、publication authority、durability；
- `prototypes/SessionJournal.DerivedRecap.Planner/README.md`：entry selection、config、resume/restore/online integration；
- `prototypes/SessionJournal.DerivedRecap.Maintainers/README.md`：concrete maintainer ownership；
- `prototypes/SessionJournal.Cli/README.md`、`prototypes/Galatea/README.md`：两个 composition roots。

target-design 文档用于解释 intent，不自动覆盖 current code/README/snapshot：

- `docs/SessionJournal/event-addressed-derived-recap-v4-target-design.md`；
- `docs/SessionJournal/derived-recap-cadence-target-design.md`；
- `docs/SessionJournal/derived-recap-history-load-target-design.md`；
- `docs/SessionJournal/recap-planner-config-repository-design.md`；
- `docs/SessionJournal/tail-execution-recovery-design.md`。

### 6.2 Core API 与 A-level wire

重点文件：

- `prototypes/SessionJournal/SessionJournalEngine.cs`；
- `SessionJournalEngine.RuntimeRecovery.cs`、`SessionJournalEngine.CompletedTurns.cs`、
  `SessionJournalEngine.DesiredSetup.cs`；
- `SessionJournalReadView.cs`、`SessionJournalLegacyImportWriter.cs`；
- `SessionJournalContracts.cs`、`SessionExpectedHeadContracts.cs`、`SessionRuntimeRecoveryRequirements.cs`；
- `SessionEventCodec.cs`、`SessionRequestManifest.cs`、`SessionRequestManifestCodec.cs`；
- `SessionRawRangeHasher.cs`、`SessionArtifactContextSnapshotHasher.cs`、
  `SessionContextContributionContract.cs`、`EventAddressTextCodec.cs`；
- `prototypes/SessionJournal.Offline/`。

关键测试：

- `tests/SessionJournal.Tests/SessionJournalPublicAuthorityTests.cs`；
- `SessionEventCodecStrictnessTests.cs`、`SessionEventBodySchemaVersionTests.cs`；
- `SessionRequestManifestCodecTests.cs`、`SessionExecutionRecoveryContractTests.cs`；
- `SessionPreparedCompletionRecoveryEngineTests.cs`、`SessionBoundedLineageTests.cs`；
- `tests/SessionJournal.Offline.Tests/`。

### 6.3 Store filesystem contract

重点文件：

- `prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapContracts.cs`；
- `DerivedRecapCodec.cs`、`DerivedRecapPathCodecs.cs`；
- `DerivedRecapStore.cs`、`DerivedRecapBuildingInstaller.cs`；
- `DerivedRecapPublisher.cs`、`DerivedRecapRestorer.cs`；
- `DerivedRecapLineageView.cs`、`RecapDurableFileSystem.cs`。

关键测试：

- `tests/SessionJournal.DerivedRecap.Store.Tests/DerivedRecapCodecTests.cs`；
- `DerivedRecapAuthorityBoundaryTests.cs`、`DerivedRecapReadOnlySurfaceTests.cs`；
- `DerivedRecapPublishedMembershipInspectionTests.cs`、`DerivedRecapPublishedPlanReadTests.cs`；
- `DerivedRecapPublishedRestoreInspectionTests.cs`、`DerivedRecapPublishedRestoreWriteTests.cs`；
- `DerivedRecapCrashRecoveryTests.cs` 与
  `tests/SessionJournal.DerivedRecap.Store.CrashHarness/`。

### 6.4 Planner config 与 frozen execution

重点文件：

- `prototypes/SessionJournal.DerivedRecap.Planner/RecapPlannerConfigDocument.cs`；
- `RecapPlannerConfigCodec.cs`、`RecapPlannerConfigRepository.cs`、`RecapPlannerConfigResolution.cs`；
- `RecapPlannerContracts.cs`、`DerivedRecapExecutionContracts.cs`；
- `DerivedRecapOperationPreparer.cs`、`DerivedRecapPreparedExecutor.cs`；
- `DerivedRecapPlannerExecutor.cs`、`DerivedRecapRestoreExecutor.cs`；
- `RecapFrozenPlanBarrier.cs`、`RecapFrozenPlanRawValidator.cs`；
- `DerivedRecapOnlineLifecycleCoordinator.cs`、`RecapCatalogShape.cs`。

关键测试：

- `tests/SessionJournal.DerivedRecap.Planner.Tests/DerivedRecapOperationPreparerTests.cs`；
- `DerivedRecapPlannerExecutorTests.cs`、`DerivedRecapRestoreExecutorTests.cs`；
- `RecapRuntimeAuthorityTests.cs`、`DerivedRecapOnlineLifecycleCoordinatorTests.cs`；
- `RecapPlannerConfigRepositoryTests.cs`、`RecapPlannerConfigResolverTests.cs`；
- `RecapCatalogShapeTests.cs`、`DerivedRecapAcceptanceTests.cs`。

### 6.5 Composition seams

- `prototypes/SessionJournal.Cli/Program.cs` 与 `tests/SessionJournal.Cli.Tests/`；
- `tests/SessionJournal.Cli.Tests/RecapCutoverArchitectureBoundaryTests.cs`；
- `prototypes/Galatea/Program.cs`、`GalateaServices.cs` 与 `tests/Galatea.Server.Tests/`；
- `GalateaDurableRecoveryVerticalTests.cs`、`GalateaRecentRewindHostTests.cs`、
  `GalateaG2AStagingHostAcceptanceTests.cs`；
- `prototypes/SessionJournal.DerivedRecap.Maintainers/` 与对应 tests。

## 7. 工作包

首轮全部只读。每包至少安排两个相互独立的视角：一个 **normalizer/simplifier**，一个
**robustness/authority defender**。后一个 reviewer 不先看前一个结论。

### N-A：Public API 与第二 Host 最小完整面

**Intent**：让一个普通 Host 只有一条难以误用的推荐路径，同时保持 Offline、Planner 与 migration 的必要能力。

重点问题：

- Create/Open/OpenReadOnly、runtime binding、Send/Resume、setup、retract 是否有语义重复 overload；
- expected-head、read view、recovery requirement 是否存在只改变包装、不改变 operator action 的层次；
- 哪些 exported records/unions/handles 是真正 support surface，哪些只是 first-party cross-assembly mechanics；
- 是否仍有 caller 可 copy/forge 的 authority-bearing descriptor；
- 第二个 minimal Host 能否只依赖 allowlist 完成正确 composition。

候选化简必须提供 before/after consumer 示例、compile-positive/negative fixture 与 public reflection diff。

### N-B：Raw/Prepared wire algebra

**Intent**：寻找 A-level wire 中重复的语义表达、version/codec mapping 或不必要的独立变量；默认不改 wire。

重点问题：

- 每个 event kind 的 field 是否属于 event fact、frozen commitment、cross-check 还是可重新推导的 convenience；
- Prepared v5 的 raw range、artifact snapshot、setup refs、context contributions、runtime/tool identity 是否存在双
  authority，还是有意的 dependency-closed proof；
- schema/version/recipe/codec id 是否一一映射，是否存在两个 registry 或 default source；
- encode、decode semantic validation、Prepared reconstruction、Offline audit 是否共享同一 contract source；
- setup/reference duplication 能否减少而不削弱 off-lineage、drift 与 corruption detection。

任何提议都必须展示 literal canonical bytes、accepted/rejected wire-language delta、mutation corpus 与全 phase reopen
matrix。不能只凭“字段可推导”建议删除。

### N-C：DerivedRecap Store filesystem normalization

**Intent**：减少 Store public surface 和 durable artifacts 之间不必要的一致性关系，同时保留可重建 sidecar 的完整
proof、strict ordinal 和 durability。

重点问题：

- `store.json`、manifest、frozen input、block、publication 中重复事实的 owner 与冲突规则；
- descriptor/handle/result union 是否能让非法 store/ref/generation/lifetime 组合进入 API；
- Building/Published/membership/health/materialization/restore 是否有重复状态或 validator；
- path codec、directory shape、schema registry 是否有多个定义源；
- missing-only repair 所需 proof 与纯 convenience metadata 能否清晰分开。

不得以 sidecar“可重建”为理由删除 atomic publication、no-replace、fsync、bounds、canonical health 或 exact-slot
Restore。

### N-D：Planner config、catalog 与 frozen execution normalization

**Intent**：让 active config、resolved capability、frozen plan 和 execution registry 各自只有一个明确角色。

重点问题：

- config document、resolver、catalog shape、protocol hard caps 是否重复表达同一 identity/bound；
- policy/profile/maintainer capability fingerprint 的 mapping 是否有多个 registry；
- NewPlanning、Building Resume、Published Restore 是否共享不该共享的 active dependency，或重复包装同一 authority；
- preparer/executor/coordinator result 是否可减少而不合并不同 operator action；
- deterministic plan 所需字段与 runtime convenience 是否被混在同一 wire/contract 中。

候选必须通过 config literal/mutation、culture/enumeration determinism、resource-access spy、frozen drift、bounded raw
和 missing-only restore tests。

### N-E：CLI/Galatea composition 与 contract 可读性

**Intent**：确认两个 composition root 只组合同一 public contracts，并把最终 contract 变成人类可快速审阅的形状。

重点问题：

- CLI/Galatea 是否复制 phase routing、config resolution、selection/restore 或 recent/recovery projection；
- Host 为正确使用 API 所需的 glue 是否说明 API 仍过宽或缺少一个更小的行为保持型前置切口；
- CLI report、call log、HTTP DTO 与 durable contracts 是否发生无意耦合；
- README sample、CLI docs、Beta snapshot 与实际推荐路径是否一致；
- 是否可以用一张 authority graph 和一张 wire fact-ownership matrix 取代跨多个文档的重复解释。

本包不把 Galatea UI、provider adapter 或 maintenance feature 纳入重构。

## 8. 执行轮次

### N0：Current inventory 与语义图

主线程在 exact execution baseline 上生成：

- exported API inventory，以及按 Online/Offline/Derived/Migration/Composition/Unsupported 分类的 support map；
- public mutation path 与 authority-token construction graph；
- A/B/C wire inventory：schema、field、owner、writer、reader、validator、golden、bounds、recovery purpose；
- durable fact-ownership matrix，逐个重复字段标注 `Authority` / `Frozen input` / `Commitment` /
  `Verification` / `Convenience`；
- state/result/operator-action matrix；
- CLI/Galatea call graph与重复 composition inventory；
- 当前 tests/goldens/mutations/crash/real-data/provider coverage。

N0 只记录事实。type/member/field 数量是线索，不是“越少越好”的目标。

### N1：独立盲审

- N-A～N-D 可并行，每包至少两个视角；N-E 在前四包报告完成前仍保持盲审；
- reviewer 只读，不修改 code/test/doc；
- simplifier 主动寻找等价设计；defender 主动证明冗余承担的 authority/proof/recovery 作用；
- 不向后一个 reviewer 展示前一个结论；
- 测试通过不能替代 contract 分析，target design 不能替代 checkout 实现。

### N2：Candidate ledger 与 plan lock

主线程去重、复现并为每个候选建立 ledger：

```text
ID: <package>-<ordinal>
Current concepts: 当前有哪些独立概念/入口/字段
Proposed normalization: 建议收成什么
Normalization leverage: High | Medium | Low
Safety severity: P0 | P1 | P2 | P3 | none
Semantic capability proof: 哪些合法 workflow 保持不变
Authority proof: authority owner 与冲突规则如何保持
Wire-language delta: accepted/rejected/canonical bytes 如何变化
Intentional redundancy analysis: 删除的是双真源还是 proof
Recovery/robustness proof: phase、crash、bounds、path、failure 如何保持
Minimum implementation: 最小切口
Tests required: 具体 fixture/matrix/golden
Cross-package impact: 受影响包与顺序
Decision: Adopt | Retain-intentional | Reject-not-equivalent | Prototype | Defer
```

Normalization leverage：

- **High**：删除一条 authority path、一个独立真源、一个 durable consistency relation 或一组 forgeable states；
- **Medium**：删除有意义的 contract concept/variant/registry，但不改变 authority graph；
- **Low**：只减少 boilerplate、文件、命名或局部 wrapper。

默认只让 High/明确 Medium 进入实施。Low 不应阻断首次生产。当前设计中的真实 safety defect 仍用 P0–P3
处理，不要把“可更漂亮”标成 P1/P2。

若两位 reviewer 意见相反，主线程回到 fact ownership、canonical bytes、operator action 与最小反例裁决，不投票。

### N3：有界实施闭环

每个 Adopt candidate 或同根候选组形成独立工作包：

1. explorer 再审视是否存在更小的行为保持型前置切口；
2. 主线程锁定 `Intent / In scope / Out of scope / Write scope / Validation / Done when`；
3. worker 修改代码、最小 tests/docs，并形成独立 commit；
4. 未参与实施的 reviewer 做 code review；
5. 主线程裁决 tail findings，关闭后再进入依赖包。

默认实施顺序：

1. D-level public API/authority path；
2. C-level Store surface/filesystem wire；
3. B-level config/frozen Planner contract；
4. A-level raw wire（只有确有高杠杆候选时）；
5. CLI/Galatea migration、docs 与跨层尾修。

若某个 A-level change 是其他包的前提，主线程应按 dependency graph 前置，而不是机械遵循上述顺序。不同
worker 不得并行修改同一 contract surface；所有重负载测试保持串行。

### N4：新 candidate gate

每包先跑 focused validation；形成新 candidate 后按变更 tier 决定 gate：

- **仅 D/E 且不影响 runtime/wire**：solution build、全部相关 suites、public reflection/second Host compile、
  docs/sample 与 clean fresh clone；
- **触及 C/B、Engine lifecycle、Host recovery 或 composition authority**：完整重复上一轮 R4，包括所有 suites、
  real-data、fresh recap/NoBuild、disposable Host、reopen/Undo、raw/source invariants；
- **触及 A-level raw/Prepared wire**：除完整 R4 外，必须对新旧 canonical fixtures、mutation corpus、所有 phase
  reopen、importer/Offline validator 一致性做显式 direct-cut 证明；
- 触及 provider request construction 时才需要真实 provider；调用预算在运行前锁定，不把正文/耗时作为 golden。

任何 A/B/C 或 Host authority 改动，默认在两个独立 `--no-local` fresh clone 上重复最终 gate。新 candidate 未通过前，
文档只能写 `gate incomplete`，不能沿用 `681fc02b` 的 GO。

### N5：最终产物与人工审阅面

tracked 产物至少包括：

1. 综合 normalization report：所有 Adopt/Retain/Reject/Prototype/Defer 候选及理由；
2. 更新后的 Beta contract snapshot；
3. before/after authority graph；
4. before/after public support map 与 wire fact-ownership matrix；
5. “哪些非法状态从此不可表示”清单；
6. exact commit map、验证矩阵与 residual risks。

原始 agent 报告、API dumps、TRX、call logs 与大体积 diff 放在 run-specific ignored evidence。最终报告应让人类
不阅读全部实现提交，也能判断“减少了哪些独立概念、保留了哪些 proof redundancy、为什么没有削弱鲁棒性”。

## 9. Candidate 接受与停止规则

接受一个化简候选，至少需要：

- 删除的不是 feature 或 failure distinction，而是重复表达/入口/invalid state；
- before/after authority graph 更小或相同，绝不增加 owner；
- wire fact-ownership 更清晰，冲突行为显式；
- required tests 能证明正常、拒绝、恢复和 crash/bounds 边界；
- benefit 足以支付重新移动 candidate 与重跑 gate 的成本。

立即 Reject 或升级用户决策：

- 需要放宽 parser、增加 default/fallback 或猜测 migration 才能成立；
- 需要删除 hash/bound/exact-head/path/fsync/typed failure 才显得更简单；
- 改变用户可见能力、operator action、recovery policy 或 accepted residual risk；
- 把 Store/Planner/Maintainers 合并为一个职责不清的 orchestrator；
- 只能证明代码更短，不能证明 independent semantic concepts 更少；
- 实施 blast radius 或证明成本明显大于可获得的 contract reduction。

若候选只是实现层重排、性能优化或美学偏好，应记录后 Defer，不延迟首次生产。

## 10. 成功标准

本轮完成时必须满足：

- 每个当前 public support role 与 A/B/C wire fact 都有明确 owner；
- 每个被保留的重复字段/类型/phase 都注明它承担的 proof、freeze 或 operator-action 作用；
- 每个 Adopt candidate 都有语义、authority、wire-language、recovery 四类证明和独立 reviewer；
- 没有通过 feature deletion、robustness weakening、tolerant read 或 compatibility shim 换取“化简”；
- public mutation path、独立 durable consistency relation、forgeable state 或 duplicate registry 至少一项有实质减少；
  若没有高置信度机会，则明确给出“retain current contract”的结论；
- docs、README、validator、runtime、tests 与 Beta snapshot 一致；
- 任何新 product candidate 完成与其风险相称的 fresh-clone gate；
- worktree clean，所有实施与尾修按阶段形成可独立审阅的 commits。

## 11. Reviewer 派单骨架

```text
你是只读 reviewer，不要修改代码、测试或文档。

任务：SessionJournal 语义保持型 Contract Normalization，工作包 <N-A...N-E>。
Baseline: <exact commit>，worktree <clean/dirty>
总计划：docs/SessionJournal/session-journal-semantic-preserving-contract-normalization-review-plan.md
当前 contract：docs/SessionJournal/session-journal-beta-contract-snapshot.md

目标不是减少功能、测试或鲁棒性，而是减少独立 authority、重复 durable fact、可伪造状态和等价 public path。

必须：
- 先画出本包 authority/fact ownership/operator-action
- 区分 authority redundancy 与 intentional verification redundancy
- 对每个候选说明 capability、wire-language、recovery、bounds 是否等价
- 给出最小实现与具体测试；无法证明时建议 Retain/Reject

明确不做：
- 不修改文件
- 不因 LOC/type/field 数量多就自动建议删除
- 不增加 tolerant reader、fallback、compatibility shim 或第二状态机
- 不把 target design 当作 checkout 实现证据

输出：
1. Normalization candidates，按 leverage 排序
2. Intentional redundancies that must remain
3. Current safety findings（若有，另按 P0-P3）
4. 实际读取的关键文件与命令
5. Residual risks；若无候选，明确写“retain current contract”
```

## 12. 最自然的开工步骤

下一会话不应立即改代码。建议第一天只完成：

1. 捕获 exact baseline、环境与 dirty status；
2. 重新生成 exported API inventory；
3. 建立 A/B/C wire fact-ownership matrix；
4. 建立 state → operator action matrix；
5. 按 N-A～N-E 发出相互独立的只读审阅；
6. 主线程汇总 candidate ledger，在第一个 implementation commit 前形成 plan lock。

这样可以避免“先看到一处 wrapper 就开始删”的局部优化，也能把三十小时修复积累出的安全规则压缩成一份人类
可检查的 contract map，再决定哪些复杂度是真正必要的。

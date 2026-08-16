# SessionJournal Contract Freeze R2 计划

状态：Active；priority R2/R3 code complete，R4 code gates passed；operator migration / remaining R1 pending  
计划基线：`13ca21f7106fbbec6e18e461360419ebeff952cc`  
启动日期：2026-08-16

## 1. 目标与裁决边界

SessionJournal、HistoryTimeline 与 current RecapGrid 已能支撑 first-party production candidate，
但“当前可运行”不等于“.NET public API 与 wire-format 已冻结”。本轮在更广泛投入使用前，优先寻找并
有选择地实施会改变 public API 或 wire-format、但能减少下列长期负担的 direct cut：

- 没有明确 support role 的 exported type/member；
- 本应由 owner 签发、却允许 caller 伪造的 result、proof、authority 或 observation；
- 表达同一 authoritative fact 的重复 public entry path、registry 或 wire field；
- 没有明确 compatibility policy、却可能被误当作稳定协议的 config/report/HTTP surface；
- 可由单一 owner 无歧义重建、且删除后不扩大 reader acceptance 或恢复状态空间的 wire fact。

“化简”以减少独立 semantic concept、authority path、forgeable state 和兼容承诺为准，不以减少代码行数、
合并名称相似的 result family、移除验证或降低功能为目标。

本计划允许 pre-release direct cut，不自动增加 compatibility shim、tolerant reader、silent migration 或
fallback。任何被接受的 wire/API 变化都会产生新的 product candidate；历史 Beta/activation 证据不能自动
认证新 candidate。

## 2. 当前起点

### 2.1 已完成的前置收口

- 旧一轮 semantic-preserving normalization 已删除重复 runtime factory、任意 raw payload public escape、
  可伪造的 historical Store-issued result construction 与重复 catalog wrapper；见
  [审阅证据](../../evidence/contract-normalization-review.md)。
- SessionJournal/RecapGrid product assembly 已从旧多项目图收敛为 S/T/C/G/H/O 边界，并完成一轮低风险
  `public -> internal`；保存的
  M2 G-assembly inventory 为 415 exported types / 4,429 members，但 R0 必须重新生成 current inventory，
  不把该历史数字当作 HEAD 事实。
- S4-D 已在 D0 证明不存在值得抽取的 non-trivial exact-equivalent durable-file seam，并以
  owner-local Retain 归档；见
  [DurableIO study](../../archive/studies/session-journal-durable-io-safety-convergence.md)。
- 跨 owner generic result family 已判定 Reject：相似叶子名称不等于相同合法状态、payload authority 或
  operator action。

### 2.2 当前文档缺口

[historical Beta snapshot](../../current/contracts/session-journal-beta-contract-snapshot.md) 明确不描述
current RecapGrid wire、owner 或 production caller；[durable target](../../current/derived-recap/durable-target.md)
只给出 top-level current rules，不是逐 field/version/reader/writer inventory。因此 current RecapGrid 在 freeze
前需要新的 exact evidence，而不是沿用 historical v7/v8 结论。

## 3. 稳定性分层

| Tier | Surface | 默认变更政策 | Freeze 前最低证据 |
|:--|:--|:--|:--|
| A | raw SessionJournal event/recovery wire | 最高风险；只有高杠杆、明确冗余才 direct cut | literal canonical fixtures、accepted/rejected language、全 phase replay/reopen/recovery |
| B | T/C/Control/Store durable companion wire | 可 pre-release hard cut，但必须显式 reprovision/upgrade policy | field owner、codec/schema、bounds、goldens、crash/reopen、maintenance action |
| C | route/connections/profile config、CLI report、Galatea HTTP/SSE | 先决定是否承诺兼容，再决定 version/direct cut | producer/consumer、secret boundary、version、unknown/missing policy、client fixture |
| D | S/T/C/G/H/O public .NET API | 最适合积极收窄；只冻结明确 support roles | exact exported inventory、production consumers、constructor authority、positive/negative compile/reflection gate |

严格 versioning 或拒绝旧版本只说明 hard-cut policy 清晰，不等于 backward compatibility。

## 4. 不可协商的不变量

除非单独接受 contract change，并在 candidate ledger 中明确标注，否则不得改变：

1. raw events + selected `RefId` Parent lineage 的 authority；
2. strict unknown/missing/duplicate/wrong-type rejection 与 canonical byte规则；
3. Prepared/frozen execution 的 setup、origin、target、tool/runtime 与 exact input proof；
4. exact-head fences、typed stale/busy/indeterminate operator action；
5. digest、commitment、physical witness、bounds、strict ordinal 与 path/slot identity 的验证职责；
6. atomic publication、SQLite transaction、fsync/rename 与 crash old-or-new boundary；
7. active config 只治理 NewPlanning，Resume/Restore 服从 frozen authority；
8. report、telemetry 与 call log 不成为 raw、provider invocation 或 recovery authority；
9. reader acceptance 只能保持或收窄，不能由 default/tolerant parse/silent fallback 变宽；
10. direct cut 后不保留同义 compatibility wrapper、双 parser、双 registry 或双 truth。

## 5. 候选假设

以下只是 R0/R1 要验证的 hypotheses，不是预先批准的实施清单。

### CF-A — Owner-issued construction authority（最高优先）

调查 public readable、但生产构造点只存在于 owning module 的 result/proof/evidence/progress 类型。候选包括：

- Manager `RecapGridBuildProgressAuthority/Metrics/MissingAssignmentProgress/RecipeRowWork`；
- Getter `RecapGridReserveBootstrapEvidence`、`RecapGridContextProvenance`；
- Online `RecapGridOnlineMaintenanceEvidence`；
- owner factory/open/read/maintenance result variants 与 public `init`/copy/`with` surface；
- S/T/C/G/H/O 中其他 output-only authority、observation 与 maintenance token。

期望形状是“type 与 public getters 保持可见，constructor/copy/init 由 owner 控制”。必须保留 caller input/spec、
operator confirmation token 的可构造性，也必须保留 external implementer contract（例如 executor/provider seam）
所需的 result construction。

Focused R1 已把该 umbrella 收缩为 output construction API hygiene：七类没有 production trusted input，
不能叙述为 security authority。Manager reference records、Getter evidence可进入原子cut；metrics struct保持
intentional default；Online需保留三项assembly-internal `init` 供现有 `with` 路径使用；完整result-family
封闭因收益不足而Defer。见
[R1 priority review](../../evidence/contract-freeze-r2-r1-priority-review.md)。

### CF-B — Public support-role cut

逐 exported symbol 分类：

1. consumer input/spec；
2. owner-issued readable token/result；
3. external implementer contract；
4. first-party composition implementation；
5. diagnostic/test/operational shape。

只有前三类可默认进入稳定 public support map；4/5 需要具体外部 consumer 或兼容承诺。候选动作包括
`public -> internal`、收窄 constructor/setter/init、删除同义 overload，不能只为降低 count 删除有用能力。

### CF-C — Current RecapGrid wire fact normalization

逐字段审阅 HistoryTimeline locator/SQLite V2、Cadence JSON V1、Control JSON/Schema V2、Store SQLite V2 与
`text-runtime-v3` / `atelia.recap.output.v3`。重点寻找：

- 两个独立 writer 能改写同一事实；
- version/schema/identity 在多个位置重复且冲突规则不清；
- canonical payload 与 SQLite indexed columns 的重复究竟是 query index、corruption proof 还是双 authority；
- public token 与 durable field 的 construction/parse authority 是否一致；
- maintenance/export/backup/restore 是否暴露不必要的 wire-shaped public DTO。

未证明前，head/digest/schema/anchor/descriptor/commitment/physical witness 均按 intentional proof Retain。

### CF-D — Operational wire support boundary

清点 route manifest、completion-connections manifest、AgentControl profile、CLI JSON、Galatea HTTP/SSE DTO。
优先决定“正式 versioned wire / first-party operational output / internal diagnostic”三分法，再考虑字段化简。
policy routing 与 secret/client construction 若分属不同 authority，必须保留分层，不能因字段相似合并。

### CF-E — Raw/Prepared wire（默认 Retain）

只有 R0/R1 提供新的 concrete redundancy 或 illegal-state finding 才进入。旧 normalization 已证明 Prepared
setup/version/model、raw range、exact inputs、origin/execution、tool/runtime/target 等承担不同 proof；不得重复
发起“字段多所以删除”的审阅。

## 6. 明确不重开

1. 跨 owner generic/superset result union；
2. S4-D shared durable-file framework；
3. 把 domain-specific digest/head/id wrapper 合并成 string/generic digest；
4. 用 exception/string code 取代 expected typed result；
5. 删除 hash、bound、exact-head、strict ordinal、fsync/path guard 来减少行数；
6. 为旧格式增加 silent migration、fallback、dual reader 或 compatibility wrapper；
7. 仅移动 namespace/文件、但不减少 semantic concept 或 authority path 的重排。

## 7. 执行阶段

### R0 — Fresh current inventory（只读）

基线必须记录 HEAD、worktree、SDK/platform 与 inventory command；不修改 production/tests。产出：

- S/T/C/G/H/O exact exported type/member/constructor inventory；
- public symbol production/test consumers 与 support-role 初分；
- owner-issued construction graph、copy/with/init/reflection/serializer consumer；
- A/B durable wire artifact、schema/version、path、writer、reader、validator、bounds、golden、recovery action；
- C-level config/CLI/HTTP/SSE producer/consumer/version/compatibility inventory；
- current public support map、wire fact-ownership matrix 与 candidate ledger draft。

R0 只记录事实和 hypotheses。没有完整 consumer/serializer/recovery 证据的候选不得标 Adopt。

### R1 — Independent simplifier / authority defender review

对 CF-A～CF-D 分别进行至少一个 simplifier 与一个 robustness/authority 视角审阅。每个 finding 必须包含：

- exact symbol/field/path 与 current consumer；
- capability、authority、state-machine、wire-language、recovery、operator-action delta；
- 最小 direct cut；
- red mutation/negative/public-surface/golden/crash evidence；
- `Adopt | Retain-intentional | Reject-not-equivalent | Prototype | Defer` 建议。

### R2 — Candidate ledger / plan lock

主线程只批准同时满足下列条件的 candidate：

- 至少减少一个 public mutation path、forgeable owner-issued state、independent durable fact、duplicate registry
  或真实 compatibility promise；
- public consumer replacement 更窄且可解释；
- wire accepted language不变或明确收窄；
- recovery/operator action不含糊；
- 没有新增 compatibility layer 或第二 truth；
- blast radius、red/green gates 与 rollback boundary 已锁定。

### R3 — Bounded implementation loops

Focused R1 后的优先顺序调整为：小型 `CF-D-04` outer-envelope cut → `CF-A-01-G/M/O` 原子API包 →
完成operator/Prepared preflight后的 `CF-D-01` atomic language cut → 拆分后的 `CF-D-02/03` → CF-C companion
evidence → CF-E raw wire。一个 candidate/semantic unit 一个提交，执行 explorer → plan lock → worker →
independent reviewer → tail-fix 闭环。若 wire candidate 与 API candidate 可独立，禁止捆绑。

### R4 — New candidate gate

- API-only：fresh builds、全部相关 tests、public reflection inventory、positive/negative consumer fixtures；
- companion wire：另加 canonical goldens、old/new rejection or explicit upgrade、maintenance、reopen/crash；
- operational wire：另加 CLI/HTTP/config exact fixtures 与 unknown/missing/version tests；
- raw/recovery wire：完整 solution + recovery/offline/strict decode/goldens + disposable clone real-data gate；
- 所有重测试串行运行 `-m:1 -nr:false`，CrashHarness 使用 isolated checkout 的标准 repo-local output。

### R5 — Freeze closure

发布 current support-role map、wire inventory、compatibility/upgrade policy、candidate commit map 与 tagged candidate。
只有 R4 通过后，才可把“current production candidate”提升为具体 tier 的 stable/frozen 声明。

## 8. Progress ledger

| Stage | Status | Evidence / next gate |
|:--|:--|:--|
| Plan | Complete | 本文；baseline `13ca21f7` |
| R0-A public/constructor inventory | Complete | S/T/O/C/G/H exact metadata inventory + compiled construction graph |
| R0-B durable wire inventory | Complete | raw、T/C/Control/Store/Rewriter field-owner/proof/recovery matrix |
| R0-C operational wire inventory | Complete | connections/config/CLI/HTTP/SSE support-boundary map |
| R0 synthesis | Complete | [R0 current inventory](../../evidence/contract-freeze-r2-r0.md) |
| R1 priority reviews | Complete | [CF-A-01 / CF-D-01 / CF-D-04 evidence](../../evidence/contract-freeze-r2-r1-priority-review.md) |
| R1 remaining reviews | Pending | CF-D-02拆HTTP/SSE；CF-D-03；CF-C-01/02；不重开raw/durable字段删除 |
| R2 priority plan lock | Complete | [priority implementation evidence](../../evidence/contract-freeze-r2-r2-priority-implementation.md)；仅批准CF-A-01 G/M/O、CF-D-01、CF-D-04 |
| R3 priority implementation | Complete | 七个原子commits + test-only `87079eaa`；未增加compatibility/framework层 |
| R4 priority code gates | Complete | solution + owner/PublicSurface/CLI/wire/nonfriend gates；CF-D-01 operator migration仍Pending |
| R5 freeze closure | Pending | 分 tier 发布稳定性声明 |

## 9. R0 完成标准

R0 只有在以下条件同时满足时完成：

1. inventory 来自本轮 HEAD，而非 M2 或 historical Beta 数字；
2. 每个候选有 production consumer 与 construction/serialization/reflection 证据；
3. 每个 durable/config/HTTP wire 有 writer、reader、version、bounds、strictness 与 recovery/operator action；
4. 明确区分 authority redundancy、verification redundancy 与 intentional denormalized index；
5. draft ledger 不含仅凭命名/行数提出的合并；
6. independent reviewer 对漏项、错误 owner 与 acceptance-widening finding 完成复核；
7. 计划 progress ledger 与 exact evidence 路径同步更新。

## 10. Focused R1 decision与复杂性门槛

本轮三项优先review已完成R2 plan lock与R3 bounded implementation：

- `CF-A-01`：Manager/Getter/Online已分别收窄；Metrics Retain，完整result family封闭因76 variants与合法
  external implementer contract而升级为Reject-overreach；
- `CF-D-01`：Completion-owned唯一数值`v:1` strict byte language已落地，owner-local path guards保留；
  tracked writer/readers/CLI/docs已atomic cut，ignored operator manifest仍是明确deployment gate；
- `CF-D-04`：outer envelope已统一；可执行反例要求Store page cap由4 MiB hard-cut到2 MiB，未抽typed
  result/DTO framework。

任何实施若开始需要新shared assembly、generic parser options、dual reader、compatibility hierarchy、
跨owner result union或cursor-aware通用printer，应暂停并重新证明收益；这些结构会重新引入本计划要删除的
第二truth或compatibility promise。

## 11. Priority implementation后的路线调整

- `CF-D-02` 下一步拆为HTTP core与SSE event两个独立language candidate；先锁explicit version、closed
  event/error DTO与browser fixture，不做通用JSON framework。
- `CF-D-03` root config继续独立，不把users/routes/secrets/runtime policy并入connections superset。
- broad `CF-B` 排在HTTP/SSE之后；不为降低inventory count继续改写output-only result algebra。
- `CF-C` 继续补companion wire goldens/classification；不重开已证明承担corruption/query/CAS职责的
  head/digest/schema/index proof。
- 所有outer wire bound新增composed encoded-byte relation gate；内层payload cap不能作为外层安全上限的证明。
- CF-D-01实际operator migration完成前，code与旧manifest不得交叉运行；回滚必须code+manifest成对执行。

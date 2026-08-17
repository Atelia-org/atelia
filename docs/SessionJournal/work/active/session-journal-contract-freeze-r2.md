# SessionJournal Contract Freeze R2 计划

状态：Active；approved surface set 1 anchored；remaining Defer packages按新candidate继续  
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
完成operator/Prepared preflight后的 `CF-D-01` atomic language cut → `CF-D-02-P0` bounded recent →
`CF-D-02b-A0` stream-bound decisions → 拆分后的 `CF-D-02a/02b` implementation → CF-D-03 → CF-C companion
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
| R1 CF-D-03 / targeted CF-B / CF-C-01 | Complete | [commit-pinned implementation evidence](../../evidence/contract-freeze-r2-d03-cfb-cfc01-implementation.md)；保持Prototype candidate |
| CF-C-02 companion evidence | Complete | [commit-pinned implementation evidence](../../evidence/contract-freeze-r2-cfc02-implementation.md)；History/Store/Rewriter independent evidence + disposable rebuild |
| Post-CF-C-02 readiness tails | Complete | RT-01 `c00df3d8` same-lease existing validation；SC-01 `a77ed16c` single DDL source，Schema V2不变 |
| R2 priority plan lock | Complete | [priority implementation evidence](../../evidence/contract-freeze-r2-r2-priority-implementation.md) + [HTTP/SSE plan lock](../../evidence/contract-freeze-r2-http-sse-plan-lock.md) |
| R3 priority implementation | Complete | 七个原子commits + test-only `87079eaa`；未增加compatibility/framework层 |
| CF-D-01 operator cutover | Complete；historical cut-time | 当时的ignored V1 manifest、Idle/Prepared=0与actual-env provider-free load；不构成R5 current deployment gate |
| CF-D-02a/02b review + plan lock | Complete | HTTP/SSE split；Adopt/Retain/Prototype/Reject与P0 blocker已锁；该阶段只读，后续R3/R4另列 |
| CF-D-02-P0 / D02b-A0 decisions | Complete | 4,096 headers / 16 MiB payload / 4 MiB recent；256 KiB pop source / 2 MiB receipt；4+5=9 MiB SSE；preview suppression |
| CF-D-02 R3 implementation | Complete | `66dd87fc` → `0f441f90`；bounded recent、minimal pop receipt、HTTP/SSE atomic server+browser cuts及P0 API hygiene tail |
| CF-D-02 combined R4 | Complete | [commit-pinned implementation evidence](../../evidence/contract-freeze-r2-d02-r4-implementation.md)；candidate/Prototype locked，未作tier freeze |
| CF-D-03 root config V1 | Complete；historical cut-time | `23392263` + no-BOM tail `8f72cb66`；当时的ignored operator manifest停服迁移/provider-free load不构成R5 current deployment gate |
| Targeted CF-B | Complete / stop | Galatea file DTO、History owner-local assembly、Hosting snapshot-only telemetry；不为inventory count继续扩大cut |
| CF-C-01 Control classification | Complete | `8a2186f8`；future schema typed Unsupported与empty whole-state independent golden |
| R4 priority code gates | Complete | solution + owner/PublicSurface/CLI/wire/nonfriend gates；D02与post-D02分时R4 evidence分别记录 |
| R5 current inventory | Complete | `a77ed16c` S/T/O/C/G/H = 901 types / 9,419 rows / 2,123 construction lines；[R5 candidate evidence](../../evidence/contract-freeze-r2-r5-candidate.md) |
| Historical R5 support/wire/upgrade docs | Complete / historical candidate | `a77ed16c`当时的contract map ready for approval；current contract已由后续AP promotion取代 |
| R5 final code gates | Complete | `a77ed16c` solution 37 projects / 4,629 passed / 0 failed / 0 skipped；build 0W/0E；11 PublicSurface、Walking 27、Galatea.RG 7与HTTP/SSE Node均green |
| R5 disposable rebuild | Complete | `a77ed16c` two fresh imports、offline/raw equality、four-owner gates、repeat-init与standalone Timeline create 13-file snapshots exact；禁网/provider artifacts 0 |
| Historical R5 docs review / approval / tag | Complete / historical Pending | `3e9a4b8d`关闭两位initial reviewer findings；在`a77ed16c`检查点approval/tag仍Pending，后续由AP promotion完成 |
| AP approval review | Complete | [approval review](../../evidence/contract-freeze-r2-approval-review.md)；Tier A/B/C/D拆分Go/Defer，发现raw literal、Route/Profile与named-role closure缺口 |
| AP-A / AP-C1 / AP-D1 closure | Complete | `e9b966e7`→`cd966fc7`；independent review findings由`02be1510`、`5e458238`、`d8517bac`、`c50356c0`、`cd966fc7`关闭 |
| AP renewal solution/Node gates | Complete | source candidate `cd966fc7`；38 projects / 4,677 passed；12 PublicSurface 40/40；build 0W/0E；HTTP/SSE Node各1/1 |
| AP renewal inventory | Complete | `cd966fc7` isolated S/T/O/C/G/H仍为901 / 9,419 / 2,123；逐assembly counts/hashes与R5 byte-identical；两层byte stability PASS |
| AP renewal disposable rebuild | Complete | `cd966fc7` two fresh imports/validations、raw/assets exact、four-owner gates、repeat-init与standalone Timeline create；exact 128-byte mixed route贯穿scaffold；禁网/provider artifacts 0 |
| AP approval docs review | Complete | `3575bf30` initial docs + `8585d889` scope/fact tail；三位independent tail re-review均PASS；scoped docs 18/0 |
| Tier approval与tag | Complete | user approved exact surface set 1；annotated tag `session-journal-contract-r2-approved-surfaces-v1`锚定promotion docs commit与validated source `cd966fc7` |
| STORE-SCHEMA-A1 | Candidate complete / approval Defer | post-tag [SQLite V2 logical-schema appendix](../../current/contracts/recap-grid-store-sqlite-v2.md) + independent persistent pragma/fingerprint gate；不属于surface-set-1 tag，等待后续显式approval |
| ROOT-CONFIG-PATH-A0 | Candidate implementation complete / approval Defer | post-tag `0f0afb2c`；relative `sessionDir`以config directory为base、absolute target保持、template为`sessions/*`；root完整field language仍Defer |
| ROOT-CONFIG-A1 | Candidate complete / approval Defer | post-tag [root config V1 appendix](../../current/contracts/galatea-root-config-v1.md)；`0515083f` + `8c450bf0`锁field language/root JSON classification，`6c5d3d50`锁full-loader profile registry conflict；不属于surface-set-1 tag |

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
  tracked writer/readers/CLI/docs已atomic cut；ignored operator manifest随后已在停服preflight后完成cutover；
- `CF-D-04`：outer envelope已统一；可执行反例要求Store page cap由4 MiB hard-cut到2 MiB，未抽typed
  result/DTO framework。

任何实施若开始需要新shared assembly、generic parser options、dual reader、compatibility hierarchy、
跨owner result union或cursor-aware通用printer，应暂停并重新证明收益；这些结构会重新引入本计划要删除的
第二truth或compatibility promise。

## 11. Priority implementation后的路线调整

- `CF-D-02a/02b` 已按shared `/api/v1` direct cut、strict endpoint DTO/error与closed SSE event language实施并完成
  combined R4；D02-P0先于wire cut关闭unbounded recent blocker，未引入通用JSON framework。
- `CF-D-03` 已独立direct cut到root exact `v:1`并关闭production bootstrap BOM blocker；没有把users、routes、
  secrets或runtime policy并入connections superset，也没有versionless/dual reader。
- targeted `CF-B` 已完成Galatea file DTO、History owner-local proof/factory与Hosting snapshot-only telemetry三项
  高置信cut，本轮到此停止；不为降低inventory count继续改写output result algebra或抽跨owner hierarchy。
- `CF-C-01` 已稳定Control future-schema operator classification并增加empty whole-state independent golden；
  `CF-C-02`也已补齐History/Store/Rewriter independent evidence，关闭Store metadata/version classification与
  repeat-init false-ready，未重开承担corruption/query/CAS职责的head/digest/schema/index proof。
- 所有outer wire bound新增composed encoded-byte relation gate；内层payload cap不能作为外层安全上限的证明。
- CF-D-01 operator migration已完成；未来回滚仍必须停服并让code+manifest成对执行。

## 12. CF-D-02 candidate与combined R4 closure

原始HTTP/SSE accepted-language调查、方案比较和历史blocker保留在
[D02 plan lock](../../evidence/contract-freeze-r2-http-sse-plan-lock.md)；实际提交链、最终shape与分时验证见
[D02 R4 implementation evidence](../../evidence/contract-freeze-r2-d02-r4-implementation.md)。当前结论是：

- `CF-D-02-P0` 已把旧whole-lineage recent replay替换为同operation 4,096 header / 16 MiB decoded payload的
  seeded bounded fold；latest 6 turns经production serializer另受4 MiB final JSON cap。closed result保留
  limit、unsupported schema与corruption的owner语义，没有root-wide fallback或第二turn reducer。
- `0f441f90`把P0的12个closed result records收为plain classes，只删除120个自动生成的clone/equality/print
  API rows。最终S inventory为162 types / 1,358 rows，相对R0净增14 / 19；construction inventory与R0
  byte-identical，没有新增public construction/copy authority，也没有wire变化。
- pop只返回预编码exact `{poppedUserText}`；source / receipt上限为256 KiB / 2 MiB，所有fallible projection、
  encoding与stale snapshot准备均在CAS前。browser持有token-bound provisional draft，任何indeterminate outcome
  只做current/recent reconciliation，不自动重发mutation。
- HTTP server与cache-busted browser已直接cut到`/api/v1`；旧route 404。request body / original-normalized message
  上限为1 MiB / 64 KiB UTF-8；strict endpoint DTO、application-owned 413、minimal error/busy与endpoint-local
  validators共同收窄accepted language。
- SSE只保留status、reasoning-delta、text-delta、done与error；strict UTF-8/LF、exact terminal与linear replay
  由typed frame owner执行。nonterminal / terminal / whole replay为4 / 5 / 9 MiB，最多16,384 events；subscriber
  channel为256 frame refs；browser为9 MiB connection / 5 MiB raw frame。
- cap hit不停provider、不改durable outcome，只进入internal `PreviewSuppressed`并丢弃后续preview；durable完成但
  bounded view不可得时仍发`done {recent:null}`。fatal EOF必须查询current并有限重试，不能当success。

combined R4已完成，但这里只形成commit-pinned candidate。数值仍为`Prototype locked`；在该阶段R5尚未完成，本计划
没有批准stable/frozen tier，也没有引入pagination、cursor、truncation、Last-Event-ID、ack、dual grammar、
generic schema framework或新的public authority。

## 13. Post-D02 package closure与下一检查点

[D03 / targeted CF-B / CF-C-01 implementation evidence](../../evidence/contract-freeze-r2-d03-cfb-cfc01-implementation.md)
记录了后续原子提交、public inventory、root config/operator cutover、Control classification与分时验证。结论是：

- root config exact V1/no-BOM、三项targeted support-role cut与Control classification均已形成package-local R4
  complete的Prototype candidate；
- broad CF-B停止，避免为了surface count进入result-family overreach；
- [CF-C-02 implementation evidence](../../evidence/contract-freeze-r2-cfc02-implementation.md)已锁定
  History/Store/Rewriter independent golden/fingerprint、Store typed classification与legacy disposable rebuild；
- standalone Timeline create existing readiness已由`c00df3d8`关闭：同一exclusive lease内read-only验证schema、
  head与active policy，失败不迁移、不repair、不误报`ready`；
- History schema-source去重由`a77ed16c`以单一12-entry DDL列表完成，净减53行；没有SQL parser/framework，
  independent fingerprint与Schema V2 accepted language不变；
- current support map、wire inventory、upgrade policy、candidate commit map与fresh compiled inventory已进入
  [R5 candidate evidence](../../evidence/contract-freeze-r2-r5-candidate.md)；final gates与independent docs review均已通过；
- 在该historical R5检查点candidate已ready for approval，但当时本文尚未批准stable/frozen tier；后续批准见§15。

## 14. R5 candidate preparation

historical R5 source candidate为`a77ed16c1ddef949dc519811fde56600db38316e`。当时的contract map按Tier A-D
记录支持角色、raw/companion/operational wire、compatibility/reprovision与non-promise；current
[Contract R2 candidate](../../current/contracts/session-journal-contract-r2.md)已经前进到AP renewal source，
不能用该current链接反向描述historical R5 source；
[R5 candidate evidence](../../evidence/contract-freeze-r2-r5-candidate.md)记录完整commit map、inventory hashes与
final gate ledger。

fresh isolated inventory为901 effective-public types、9,419 logical API rows与2,123 construction rows；相对R0
分别`+10 / -17 / -48`。这些数字识别candidate，不表示所有export都已获得stable support promise。

同一historical source candidate的final solution/owner/PublicSurface、HTTP/SSE Node、provider-free disposable rebuild、
docs checker、source diff与independent review均已complete；它在当时是`ready for approval`且approval/tag仍Pending。
ignored operator config和real provider明确`NotRun`。后续批准事实只由§15 AP promotion拥有，不能反向改写该历史检查点。

## 15. AP approval closure与renewal candidate

[approval review](../../evidence/contract-freeze-r2-approval-review.md)把blanket Tier批准拆成逐surface承诺。三项小型
closure已经完成：

- AP-A登记raw ID `11/12`均retired，并为全部unique raw body shapes增加test-owned exact literals；
- AP-C1补齐Route/Profile/Admission accepted-language gates，并修复Route JSON-escaped/source-UTF8计数漂移、
  Control Admission超过64 KiB时producer/reader不闭合与culture-sensitive排序；
- AP-D1新增无IVT的SessionJournal PublicSurface project，把external source/lifecycle、estimator、executor、
  runtime/hosting composition改为named-role source oracle，同时排除owner-issued output construction。

current source candidate为`cd966fc7fddfa6acbda6f80431cf9b588177d969`。它没有public type/member、raw event、
durable schema version、HTTP/SSE grammar或CLI envelope delta，但有production implementation/accepted-language修正，
因此已重新运行inventory、solution/Node与provider-free disposable rebuild。所有renewal gates与independent docs
review均通过；用户已批准exact surface set 1并授权annotated tag。未批准surface继续candidate/Defer；后续工作不得
移动该tag或用新HEAD反向改写已批准基线。

## 16. Post-tag STORE-SCHEMA-A1 candidate

[Store SQLite V2 logical-schema appendix](../../current/contracts/recap-grid-store-sqlite-v2.md)针对同一validated product
source `cd966fc7`补齐了Tier B Store的approval-grade审阅入口，并在test-owned independent fingerprint旁另锁
`page_size=4096`与`journal_mode=delete`。本包不改production、Schema V2、public API、wire或tag；appendix及其
post-tag test evidence也不属于immutable surface set 1。下一步只能是独立review后由用户显式批准或继续Defer，
不能用文档合入或green gate自动宣布stable/frozen。

## 17. Post-tag ROOT-CONFIG-PATH-A0 candidate

`0f0afb2c`把root config file DTO中的relative `sessionDir`以`config.json`所在目录为base解析，并只向runtime
交付absolute path；absolute配置保持同一target。bootstrap template同步从旧CWD-oriented
`.atelia/galatea/sessions/*`改为`sessions/*`，避免默认config目录下出现double prefix。

该direct cut没有增加schema/version、public record、Host constructor、CWD/existence fallback、`chdir`、auto
move/create或generic path framework；absolute与`..`仍合法，也不承诺config-directory confinement或新增no-follow
filesystem边界。新增focused 6/6、config 20/20、Galatea full 150/150与solution build 0W/0E均通过；ignored operator
config未由本包修改。root config完整field language继续candidate/Defer，且不属于immutable surface-set-1 tag。

## 18. Post-tag ROOT-CONFIG-A1 candidate

[Galatea root config V1 appendix](../../current/contracts/galatea-root-config-v1.md)在`0f0afb2c` path cut与`319bd425`
初始记录之上，用`0515083f` handwritten field-language tests和`8c450bf0` root JSON materialization
classification tail锁定whole root `config.json` candidate。范围包括required/optional/count、prompt file precedence、config-directory paths、
eager profile与deferred route dependency、root/prompt/profile bounds，以及bootstrap no-BOM/single-LF writer事实。

该appendix明确bootstrap不是canonical writer，且不承诺password-at-rest protection、file permissions、Kestrel对
opaque `listenUrls`的解释、diagnostic/provider/deployment、whitespace/property order或auto rewrite。完整root V1
仍是post-tag candidate/approval Defer，不属于immutable surface-set-1 tag；green tests与文档不能替代后续显式approval。

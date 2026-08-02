# SessionJournal 首次生产运行前审阅计划

状态：Review plan，2026-08-02

适用范围：

- `prototypes/SessionJournal`；
- `prototypes/SessionJournal.DerivedRecap.Store`；
- `prototypes/SessionJournal.DerivedRecap.Planner`；
- 为证明上述三者能够正确组合而必需的 `SessionJournal.Cli`、Maintainers与Galatea integration seam。

本文不是新功能计划。目标是在首个长期运行的真实SessionJournal开始积累不可随意丢弃的raw history之前，
利用尚无外部用户的窗口，审阅并收紧public API、durable wire、repo-owned config和恢复语义。Galatea当前
已足以进入E2E试运行；本轮关注的是支撑它的库与格式是否值得从Technology Review推进到Beta。

## 1. 目标与成功标准

本轮要回答四个问题：

1. public API是否表达单一、最小且难以误用的authority模型；
2. raw payload、Prepared commitment、Recap sidecar和Planner config的schema、canonical bytes与拒绝规则
   是否明确且一致；
3. crash、corruption、reopen、concurrency和phase recovery下，系统是否保持raw authority、strict ordinal与
   missing-only repair语义；
4. CLI与Host是否只组合public contracts，没有复制Planner/Store/raw reducer或创建第二真源。

完成时必须满足：

- 没有未处理的P0/P1 finding；
- 每个P2 finding都有“本轮修复”或“明确接受并说明为何不阻断Beta”的书面结论；
- public API inventory、wire inventory、测试矩阵与current文档相互一致；
- 所有修复都经过“实施者之外的独立reviewer”复核；
- 在fresh clone上重复完成import、validate、Recap publish/materialize/NoBuild、Prepared reopen recovery与
  Galatea disposable canary；
- 审阅不以增加compatibility layer、silent fallback、auto reset或第二套状态机来换取通过。

## 2. 稳定性不是同一个等级

不同产物应承担不同稳定性承诺，审阅时不得把它们混成一个“wire compatibility”问题。

| 等级 | 产物 | 首个Beta后的预期承诺 |
|---|---|---|
| A | raw SessionJournal event payload、Parent lineage、Prepared request commitment、setup与tool execution recovery | 最强；改变必须升级明确版本并提供显式import/rebuild决策，禁止缺省推断与silent fallback |
| B | repo-owned `config/recap-planner-config.json` | operator-authored长期配置；严格schema、bounded read、canonical writer，升级需显式迁移或重新初始化 |
| C | `derived/recap/v4` Building/Published sidecar | 可从raw重建；要求严格拒绝、原子publication、可诊断恢复，但不承诺为旧实验generation保留reader |
| D | public .NET API | Beta前允许直接瘦身；Beta后按明确support surface演进，避免把diagnostic/internal workflow永久冻结为public |
| E | CLI JSON report、call log | versioned operational evidence；不得成为raw或recovery authority，兼容强度低于A/B |

阶段建议：

- **TR**：允许direct cut；以找到并消除错误抽象、双真源和误用入口为目标；
- **Beta**：冻结首个support surface和A/B级schema；后续breaking change必须显式版本化；
- **Public**：只有经过真实长期运行、恢复演练和至少一次upgrade rehearsal后再讨论SemVer或跨版本读取承诺。

“没有外部用户”应被用来删除不值得稳定的表面，而不是提前承诺当前所有`public`类型。

## 3. 核心不变量

所有reviewer共享以下基线。若认为其中一条不成立，应把它作为设计finding提出，而不是私自改写前提。

1. raw events与selected `RefId`的Parent lineage是会话事实和恢复的唯一authority；
2. DerivedRecap是可删除、可重建的sidecar，不向raw写入Recap identity；
3. Store管durable structure、point read/write、membership、strict ordinal、atomic publication和exact
   materialization；
4. Planner管cadence、HistoryLoad、Maintain/Inherit、bounded catch-up、Building Resume和exact-slot
   Restore；
5. Maintainers管concrete profile/prompt；CLI/Host是composition root；
6. active Planner config只决定新的Building；Resume/Restore只服从frozen plan；
7. `NthPrevious`是strict ordinal，损坏slot不能跳过或重编号；
8. Published健康文件复用，缺失或损坏component只恢复该部分；不追求provider exactly-once；
9. Prepared/Started recovery不读取active Planner config，也不fallback到current default connection；
10. 未知、旧版、截断、重复字段、越界值与不一致hash必须fail closed或返回typed unavailable；
11. low-level append、diagnostic和trusted seam不能被普通Host误当作online workflow；
12. canonical bytes、hash codec id、schema id、event-address text/file-name codec各自只有一个定义源。

## 4. 审阅组织方式

### 4.1 先盲审，后汇总

第一轮让不同模型独立审阅，不向后一个reviewer展示前一个reviewer的结论。每个工作包至少安排两个视角：

- **contract lawyer**：检查public contract、schema、版本、拒绝规则与文档；
- **adversarial operator**：检查crash、corruption、concurrency、recovery和误操作；
- 可选第三视角为 **library consumer / simplifier**：从第二个Host的使用体验寻找API过宽、样板代码、
  错误默认值与可删除表面。

模型多样性之外，还要保持视角多样性。所有reviewer使用同一个事实基线和输出格式，但不要要求它们先达成
一致意见。

### 4.2 审阅阶段全部只读

首轮reviewer不得修改代码、测试或文档。只有主协调者完成finding去重、证据复核与优先级判定后，才把
确认的问题拆成独立修复包。这样可以避免早到的局部方案污染后续审阅，也避免多个Agent在同一表面并行写入。

### 4.3 固定review baseline

每轮开始记录：

- exact git commit、dirty status和.NET SDK；
- OS/filesystem，以及`renameat2`、directory fsync等平台假设；
- 所审阅的assembly、schema与测试项目清单；
- 是否包含真实provider测试、其connection id与有界调用预算。

报告只对该baseline成立。baseline变化后，高风险finding的关闭必须在新commit上复核。

## 5. 工作包

最低阅读范围：

| 包 | Product code | Current contract docs | Tests / evidence |
|---|---|---|---|
| A | `prototypes/SessionJournal` public contracts与`SessionJournalEngine*` | `prototypes/SessionJournal/README.md`、`tail-execution-recovery-design.md` | `SessionJournal.Tests`、`SessionJournal.Offline.Tests` |
| B | `SessionEventCodec`、request manifest/canonicalizer、tail resolver、audit/history hash | 同上，以及`event-addressed-derived-recap-concepts.md`中raw/derived边界 | codec/golden/reconstructor/recovery/offline-validator tests |
| C | `prototypes/SessionJournal.DerivedRecap.Store` | Store README、`event-addressed-derived-recap-v4-target-design.md` | Store tests与CrashHarness |
| D | `prototypes/SessionJournal.DerivedRecap.Planner` | Planner README、cadence/history-load/config repository designs | Planner tests |
| E | `SessionJournal.Cli`、Galatea composition、Maintainers catalog | CLI/Galatea/Maintainers README与cutover plan | CLI tests、Galatea tests、G2A runbook与run-specific evidence |

若目标设计、README与实现冲突，reviewer必须报告drift并指出哪一个应该成为current authority；不得为了让
结论看起来一致而只选其中一份阅读。

### A：SessionJournal public API与authority边界

**Intent**：判断raw core是否暴露了一个小而完整的Host/Offline/Planner API，并找出应该在Beta前internalize、
合并或重命名的表面。

**In scope**：

- `SessionJournalEngine`的Create/Open/OpenReadOnly、Send/Resume、setup reconciliation、completed-turn、
  history planning、audit与exact-head API；
- `SessionRuntime`、recovery requirement unions、context candidate/lifecycle contracts、exception/result类型；
- low-level append API、`InternalsVisibleTo`、XML docs与README示例；
- public enum新增值、record positional参数、default参数和cancellation语义。

**重点问题**：

- 一个普通Host能否只走一条推荐路径，还是容易绕过phase/authority检查；
- `public`是否混入只为CLI/tests存在的workflow中间类型；
- result union是否可exhaustive match，unknown/future case是否会被调用方误判为success；
- read-only、writer lock、expected-head和disposed lifetime是否可从API形状直接理解；
- README sample是否编译，并使用与Galatea相同的public path。

**Out of scope**：新增Galatea tool runtime、provider adapter或UI功能。

**Validation候选**：public API reflection snapshot、README compile tests、null/bounds/cancellation matrix、第二个
minimal Host compile fixture、architecture dependency tests。

### B：SessionJournal raw wire与recovery protocol

**Intent**：把A等级wire当作协议审阅，而不是只看round-trip测试。

**In scope**：

- 每个`SessionEventKind`的body version与exact JSON shape；
- canonical UTF-8 encoding、property order、number/string/enum encoding、Unicode与required escaping；
- Runtime/System setup、SessionCreated、Observation/Action、tool events、Prepared v5、attempt started/failed；
- raw-range、history semantic、context contribution、request commitment等hash/codec ids；
- reopen recovery、tail resolver、dependency-closed fold seed、offline validator与importer输出。

**必测负向矩阵**：

- unknown kind/version/property；missing/duplicate/null/wrong-type property；
- integer overflow、negative checkpoint、invalid EventAddress与off-lineage reference；
- truncated/corrupt payload、hash mismatch、wrong Parent、setup cursor drift；
- Observation/Prepared/Started/tool/failed各phase的process-death reopen；
- direct encode bytes、decode后语义与Prepared reconstruct canonical bytes三者一致。

**产物**：一张current wire inventory，逐kind记录schema version、writer、reader、validator、golden与拒绝测试。

### C：DerivedRecap.Store API、filesystem wire与durability

**Intent**：证明Store只表达storage authority，并判断当前较大的public contract surface是否能够在Beta前收窄。

**In scope**：

- store/manifest/frozen-input/block/publication schema与path codec；
- Building install、checkpoint/final write、publication、selection、materialization、restore、quarantine/reset；
- `SetAdmissionAnchor`、`AbsorbedThrough`、source/prior context、strict ordinal和current-lineage规则；
- per-Ref lock、temp file、flush/fsync、rename-no-replace、reopen与symlink/reparse防护；
- public handles/descriptors/result unions的forgeability与lifetime。

**重点攻击**：

- half-written JSON、valid JSON但hash/shape错误、block与manifest交叉替换；
- sealed Building伪装Published、publication先于block durability、同anchor双publisher；
- newer exact slot损坏、stale/off-lineage Building、reset与publish竞争；
- handle跨store/ref/reopen误用、caller伪造lineage snapshot或publication authority；
- Linux crash-harness声明是否与实际fsync边界一致，非Linux行为是否明确为unsupported或降级。

**Validation候选**：literal canonical goldens、mutation tests、property-based path/codec tests、多进程race、现有
crash harness扩展、raw events/refs before/after fingerprint。

### D：DerivedRecap.Planner API、config wire与determinism

**Intent**：证明Planner是纯调度/执行层，active config、frozen plan与Host capability不会形成三套authority。

**In scope**：

- `recap-planner-config.v2` canonical codec、loader、initializer、resolver与capability snapshot；
- HistoryUnit estimator、cadence evaluator、baseline policy、limits与bounded catch-up；
- preparer、new planning、Building resume、Published restore、online lifecycle与deferred registry；
- phase-specific laziness、client/LLM调用时机与typed diagnostics。

**重点问题**：

- config filename/schema/type mapping是否唯一，unknown/duplicate/missing/overflow字段是否strict reject；
- 同一次operation是否只加载一个immutable snapshot；
- Building存在时是否完全跳过active config；Prepared/Started/Restore是否zero-touch config/estimator；
- estimator/policy/active roster/execution registry identity是否可能漂移；
- 相同raw + Store + config是否产生相同schedule/manifest，文化区、线程与枚举顺序是否影响结果；
- raw safety是否早于tokenization/client，NoBuild是否0 provider call；
- 多cursor catch-up、Maintain/Inherit与limits是否可能无进展、越界或产生非replay-safe anchor。

**Validation候选**：config literal goldens与mutation corpus、determinism repeat test、phase/resource-access spy matrix、
boundary/property tests、frozen config drift、missing-only restore、large-history bounded-cost tests。

### E：跨层组合与第二Host可用性

**Intent**：寻找单个项目测试看不见的缝隙；不把CLI/Galatea升级成被审对象的第二实现。

**In scope**：

- raw engine、Store、Planner、Maintainers、Completion registry在CLI和Galatea中的依赖方向；
- exact repo path/RefId/engine instance binding；
- import → setup reconciliation → config/Store provisioning → Recap → materialize → Send/Resume；
- call log、report和provider operational config是否误入durable fingerprint；
- permanent repo只读验收与disposable clone写入验收。

**必须证明**：

- CLI与Galatea不复制config resolution、Building selection、restore或raw phase state machine；
- Maintainer/profile roster与frozen
  `MaintainerId + Target + MaintainerCapabilityFingerprint`能够exact bind；
- Recap sidecar mutation不改变raw events/refs；
- Prepared reopen能在active config删除/改变后重建同一canonical request；
- strict ordinal损坏只Restore同一slot，不能fallback到full raw或更旧set；
- importer输出的semantic commitment、setup和history在后续Recap/Send后仍可审计。

## 6. Finding格式与严重度

每条finding必须包含：

```text
ID: <package>-<ordinal>
Severity: P0 | P1 | P2 | P3
Claim: 一句话说明哪个承诺不成立
Evidence: 文件/符号/测试/最小复现；禁止只给主观偏好
Impact: 会造成数据丢失、错误恢复、wire漂移、API误用还是维护成本
Minimal correction: 最小修复方向；不要直接扩成大重构
Tests required: 能阻止回归的具体测试
Cross-package impact: none 或受影响工作包
```

严重度：

- **P0**：可能静默丢失/伪造raw authority、执行错误或不可逆副作用；
- **P1**：常见crash/recovery/corruption路径失真，或public/wire存在双真源；Beta blocker；
- **P2**：API明显易误用、schema/文档/validator不一致、关键边界缺测试；默认本轮处理；
- **P3**：局部清晰度、低概率diagnostic或不削弱当前设计主张的改进。

自然语言exception message、代码样式和纯命名偏好不能单独升级为compatibility finding。若reviewer无实质性
finding，必须明确写“无 findings”，并列出仍未覆盖的residual risks。

## 7. 执行轮次

### R0：Inventory与可复现baseline

主协调者生成并人工确认：

- public API inventory：assembly、namespace、type/member、visibility、XML docs；
- wire inventory：schema/codec id、path、writer、reader、validator、golden；
- test inventory：unit、golden、mutation、crash、integration、real-data、real-provider；
- dependency graph：SessionJournal ← Store ← Planner，Maintainers与Host composition边界。

R0只建立事实，不先判断“现状就是目标”。建议把reflection API snapshot和wire fixtures变成测试输入，但在
API瘦身决策完成前不要把整个现有public surface误锁成永久golden。

### R1：多模型独立盲审

- A～D可并行，每包至少两个模型；
- E由一个Host integrator和一个adversarial recovery reviewer分别执行；
- 只读，不修改；每个报告必须注明实际读过的文件和跑过的命令；
- 不把测试通过当作无finding，也不把目标设计文档当作实现证据。

### R2：主线程综合与plan lock

主协调者：

1. 合并同根finding，保留不同证据；
2. 亲自复现P0/P1和影响跨包的P2；
3. 将意见分为bug、contract gap、test gap、documentation drift、optional simplification；
4. 先决定support surface与wire rule，再拆修复包；
5. 发布一份审阅结论，明确accepted/rejected/deferred及理由。

如果两个reviewer结论相反，不做票数表决；回到authority、wire bytes、最小复现和用户可见承诺裁决。

### R3：有界修复闭环

每个确认finding或同根finding组形成独立工作包：

1. explorer再审视是否有更小切口；
2. 主协调者锁定Intent/In scope/Out of scope/Write scope/Validation/Done when；
3. worker实施、补最小测试并提交独立commit；
4. 未参与实施的reviewer复核；
5. P0/P1/P2尾修关闭后再进入下一包。

优先顺序：raw wire/authority → Store durable shape → Planner config/frozen authority → public API瘦身与Host
ergonomics。若API瘦身会改变wire authority，应与对应wire包一起处理，不能留下名义统一、实际双路径。

### R4：Beta candidate gate

在fresh working clone串行执行：

1. 所有相关unit/integration/crash suites；
2. canonical golden与mutation corpus；
3. 目标legacy JSON fresh import及strict offline validate；
4. repo config初始化/检查、Store create、Recap Published、materialization与immediate NoBuild；
5. Prepared/Started/tool phase reopen matrix，其中不支持的Host能力必须typed拒绝；
6. disposable Galatea clone真实provider canary、reopen和Undo；
7. raw events/refs与source export invariants；
8. clean checkout重复一次，以排除测试顺序、缓存和本地ignored配置依赖。

Beta candidate不能依赖永久production repo上的破坏性canary。真实provider正文与耗时不做跨run golden；固定
的是route identity、request commitment、raw/import facts、set membership、admission/absorption与typed结果。

## 8. 建议的测试增强清单

审阅后按finding决定是否实施，不应为了数量机械添加。

- reflection API baseline + intentional allowlist，防止意外新增public surface；
- README/sample compile tests，覆盖第二Host的最短正确composition；
- literal JSON/UTF-8 byte goldens，而不只是encode-decode round trip；
- schema mutation corpus：delete/duplicate/rename/reorder/wrong-type/overflow/unknown；
- deterministic repeated planning：同一输入在不同culture、process和catalog enumeration下字节一致；
- model-based phase transition test，对比append/reopen后的reducer与runtime requirements；
- multi-process publish/reset/restore races和crash failpoints；
- corpus/fuzz decoder test，要求bounded failure、无repo mutation、无provider access；
- resource-access spies，证明各phase不读取不应读取的config/Store/client；
- real-data import→recap→Prepared canonical request commitment端到端fixture；
- package dependency/namespace test，阻止raw core反向引用Store/Planner/Maintainers。

## 9. 可直接派发给Reviewer的提示骨架

```text
你是只读reviewer，不要修改代码或文档。当前任务是首次生产运行前的<工作包名称>审阅。

Baseline:
- commit: <exact commit>
- repo: /repos/focus/atelia
- 规范: docs/SessionJournal/session-journal-first-production-readiness-review-plan.md

必须先读:
- <本工作包源文件>
- <对应README/target design>
- <对应测试>

审阅目标:
- 验证public API、wire、validator、runtime和tests是否表达同一个authority模型
- 主动寻找双真源、silent fallback、可伪造输入、crash/recovery缺口和过宽public surface

明确不做:
- 不改文件
- 不实现新功能
- 不因测试通过而跳过contract review
- 不把旧superseded文档当作current contract

输出:
1. Findings，按P0-P3排序，每条使用计划规定的finding格式
2. API/wire/test coverage gaps
3. 你实际读取的关键文件与执行的命令
4. Residual risks；若无finding请明确写“无 findings”
```

为增加反馈多样性，可在共同骨架末尾分别追加：

- “以第一次接入该库的Host作者视角，优先找API误用和不必要样板”；
- “以filesystem/crash recovery工程师视角，假设任意IO边界进程死亡”；
- “以协议实现者视角，把每个JSON字段、版本和hash当成不可信输入”；
- “以简化设计视角，优先找可以删除的public类型、重复descriptor和双层validator”；
- “以攻击性测试作者视角，提出最小mutation/property/model-based测试”。

## 10. 明确不在本轮扩张的事项

- Galatea tool-capable runtime与tool recovery；
- post-response Recap warm-up、background scheduler或多进程service；
- provider token accounting、动态模型窗口或新的HistoryLoad estimator；
- DerivedRecap跨generation compatibility reader；
- 云端分布式Store、remote locking或exactly-once LLM调用；
- 为让旧实验repo继续打开而增加缺省字段、猜测迁移或full-raw fallback。

这些事项可以在Beta之后单独设计，但不能用来回避本轮发现的现有contract缺陷。

## 11. 最终审阅产物

建议保留三类产物：

1. 每个模型的原始只读报告，保存到run-specific、repo外目录，例如
   `gitignore/session-journal/reviews/<run-id>/`；
2. tracked综合报告，记录confirmed/rejected/deferred findings和修复commit；
3. Beta contract snapshot：支持的public surface、A/B/C级wire inventory、测试结果与明确的residual risks。

只有第2、3类是项目结论。单个Agent报告是可审查输入，不自动成为设计真理。

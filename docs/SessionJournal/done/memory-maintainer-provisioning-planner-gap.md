# MemoryMaintainer Provisioning / Planner 功能缺口备忘

> **状态**：Implemented / Superseded Historical Gap（DM-5～DM-8，2026-07-28）。
> 本文保留提出问题时的历史快照，正文中的“当前缺口”“尚未实现”不再描述 current trunk。
> current contracts、实现落点与验收以
> [DerivedMemory 可替换子系统与 Shared Epoch 实施方案](derived-memory-subsystem-implementation-plan.md)
> 为准。
>
> 日期：2026-07-27。
> 目的：记录 SessionJournal raw core 之上的 Agent application layer 仍缺少的 MemoryMaintainer
> provisioning、调度、coherent publication 与生命周期管理能力，供后续 Coding Agent 建立上下文和
> 拆分实施切片。本文不是已采纳的详细 API 设计。
> **已采纳实施顺序**：
> [DerivedMemory 可替换子系统与 Shared Epoch 实施方案](derived-memory-subsystem-implementation-plan.md)。

## 1. 一句话结论

当前代码已经具备“一个 maintainer 维护一个 `MemoryPack` 文本块”、同 snapshot 并行执行的
substrate，以及将多个 exact derived artifacts 发布为 derived-only coherent ArtifactSet 的底层机制；但尚未具备
一个通用的 Agent/Session 上层 provisioner/planner，去声明一类 Session 需要哪些 memory roles、何时运行
哪些 maintainer、如何先生成所有 maintainer 共享的 history coverage epoch、如何恢复各自 lineage、
如何并行生成同一 epoch 的结果，以及何时把完整结果集原子发布为可供 online completion 使用的
coherent ArtifactSet。

候选 C / DM-4 已将 coherent ArtifactSet definition/publication 移入 derived sidecar；raw
SessionJournal 不再引用 derived set 或 artifact id。详见
[`tail-execution-recovery-simplification-study.md`](../tail-execution-recovery-simplification-study.md)。

Derived ArtifactSet 也不应只是“仍放在 SessionJournal 程序集里的另一种 store”。其具体维护、存储、
lineage、indexes、provisioning 和 candidate discovery 应进入独立、可替换的 DerivedMemory 子系统；
SessionJournal 只定义并消费 store-neutral 的 coherent context candidate contract。Host/composition
root 同时引用两边并负责注入具体实现。

目前 `autobiography` 与 `world-understanding` 的成功链路仍主要依靠
`SessionJournal.Cli run-memory-maintainer` 分别运行两个 profile，
再由操作者显式执行 artifact-set checkpoint。它是可靠的验收管线，不是最终的在线编排实现。

另一个已确认的缺口是：当前每个 CLI runner 独立解释 `--threshold-tokens` 并计算 split。相同 raw
输入、token estimator 和阈值通常会偶然得到相同 anchor，但分块没有 durable shared identity；
prompt-tuning 改阈值、改变 replay 起点或升级 estimator 后，各 role artifact 会落入不同 coverage，
无法直接组成同步的 DerivedArtifactSet。因此 shared epoch planning 必须早于通用并行
MemoryMaintainer execution。

## 2. 所属层次与命名

该功能属于 **SessionJournal raw core 之上的 Agent/Session 应用编排层**，不属于 raw execution
reducer/codec。

本文暂称其为 **Memory Maintenance Provisioner / Planner**，以避免和 request-time `Context Planner`
混为一谈：

- **Memory Maintenance Provisioner / Planner**：决定哪些 memory roles 存在、由哪个 maintainer
  维护，并调用 shared history epoch planner 后调度同一 epoch 的 producer，最终形成 coherent
  ArtifactSet candidate。
- **Derived Artifact Epoch Planner**：只负责“哪些 dependency-safe recent-history 即将滑出工作
  context”以及如何把它们切成共享 coverage epochs；它不运行 maintainer，也不决定 provider request。
- **Context Planner**：在一次 completion 前，从可用 ArtifactSet、raw suffix 和其他 recall
  candidates 中选择 exact request context。
- **SessionJournal raw core**：保存并验证 raw execution、raw lineage、setup、Prepared manifest 和
  reopen 合同；不引用 derived ids，也不解释应用层 role 的业务含义。

两类 planner 通过 DerivedMemory 子系统内部的 ArtifactSet candidate 和面向 SessionJournal 的
store-neutral context candidate 交接；不会把 concrete artifact/set reference 交给 raw core。职责也
不能合并成一个同时负责 LLM producer 生命周期和 request recovery 的“大 planner”。

### 2.1 程序集与依赖方向

长期目标依赖图：

```text
Agent Host / SessionJournal CLI / composition root
├── Atelia.SessionJournal
│   ├── raw event / tail execution recovery
│   ├── coherent context candidate contracts
│   ├── dependency-closed suffix materialization
│   └── Prepared exact reopen
└── Atelia.SessionJournal.DerivedMemory（暂名）
    ├── MemoryMaintainer provisioning / orchestration
    ├── shared history epoch planner / plan ledger
    ├── artifact / ArtifactSet persistence
    ├── derived lineage / indexes
    └── context candidate discovery / selection
         └── 单向引用 Atelia.SessionJournal contracts
```

`Atelia.SessionJournal` 不引用 concrete DerivedMemory 程序集。第一阶段允许 provider interface 和
store-neutral DTO 继续定义在 SessionJournal 内；只有出现多个 consumer/implementation、或 contracts
稳定后确有收益时，才进一步抽取 `SessionJournal.Abstractions`。

边界接口应按能力命名，例如 `ICoherentContextCandidateSource`，而不是把
`IDerivedArtifactSetStore` 暴露给 core。SessionJournal 所需的返回形状只包括：

- `RawStartExclusive` 与对应 paired setup refs；
- 证明 current-lineage/coverage 所需的最小 raw provenance；
- 规范化的 `{ carrier, blockKey, exactText }` contributions；
- 可选 cost estimate 与 opaque diagnostics。

artifact/set id、文件路径、latest index、profile/maintainer 实现和 store schema 都留在 derived
subsystem。若要审计某次 Prepared 使用了哪个 set，可在 derived usage index 记录
`preparedAddress -> derivedSetId`，不能反向写入 raw。

Derived subsystem 决定应用级 required roles、coherence 和 producer compatibility；SessionJournal
仍需独立验证 selected candidate 的 raw ancestor、replay-safe boundary、setup refs、target uniqueness
与 exact-head CAS，并负责 fold suffix、deterministic rendering、canonicalization 和 Prepared
commit。provider 不能直接返回 ready-made `CompletionRequest` 绕过这些校验。

这里的“同步”是共享 immutable `epochId + raw coverage`，不是要求所有 maintainer 必须在同一个进程或
同一时刻运行。日常 online 运行可以在 epoch plan durable 后并行启动整个 coherence group；开发期则可
让单个 maintainer 针对同一 epoch 独立重跑、比较多个 prompt/model candidate，而不重新切分 history。

## 3. 当前已经具备的基础

### 3.1 通用 Memory substrate

`prototypes/SessionJournal/SessionMemoryContracts.cs` 当前提供：

- `MemoryPack`、`MemoryPackDraft`、`RenderedMemoryPack`；
- `MemoryPackCarrier.System / Observation / Action`；
- `IMemoryBlockMaintainer`：稳定 `Id`、唯一 `Target`、`MaintainAsync()`；
- `RewriteMemoryBlockMaintainer`：以 profile 驱动的通用 LLM 文本块重写器；
- `MemoryMaintenanceOrchestrator`：从同一个 `MemoryPack` / `RecentHistorySlice` snapshot 启动多个
  maintainer，通过 `Task.WhenAll` 收集结果，并在校验 id/target 后形成新的 `MemoryPack`。

这已经表达了最初概念中的关键隔离：每个 maintainer 只拥有自己的 target block；多个 maintainer
不能写同一 target；结果通过统一 `MemoryPack` 形状汇合。

### 3.2 应用层内容 profiles

`prototypes/SessionJournal.Maintainers/` 当前作为依赖 SessionJournal contracts 的
concrete MemoryMaintainer companion assembly，提供首批 Role-Play 内容策略：

- `AutobiographicalRewriteProfiles`；
- `WorldUnderstandingRewriteProfiles`；
- `RolePlayMemoryBlockPaths`。

业务角色、prompt 和 block path 没有写入 SessionJournal wire。后续增加新的 memory role，原则上不应
要求修改 SessionJournal event codec、tail resolver 或 reducer。

### 3.3 Derived artifact 与 coherent publication

`DerivedRecapStore` 已能把 maintainer 结果作为带 provenance 的 append-only sidecar artifact 保存。每个
artifact 固定自己的：

- profile / producer identity；
- source raw head、实际覆盖范围与 anchor；
- governing runtime config / system prompt setup；
- previous/input artifact lineage；
- target block、content hash、invocation。

DM-3B 已把上述历史 interim activation 路径替换为独立
`Atelia.SessionJournal.DerivedMemory`：derived-only `DerivedArtifactSetStore` 发布 exact set，
capability-oriented provider 返回 neutral candidate，raw core authoritative validation 后拼接
dependency-closed suffix。Prepared 只保留实际进入 request 的 exact snapshots、raw range/hash 和
raw setup refs；Prepared 后即使整个 `derived/` 删除也可 exact reopen。raw kind 12 的剩余
codec/read-only legality 将在 DM-4 删除。

## 4. 当前实际链路

DM-3B 后的正式原型链路是：

```text
import raw SessionJournal
  -> 单独运行 autobiographical-rewrite
  -> 写 autobiography artifact
  -> 单独运行 world-understanding-rewrite
  -> 写 world-understanding artifact
  -> host/composition code 以 strict anchor setup refs 发布 derived-only ArtifactSet
  -> 注入 DerivedArtifactSetContextCandidateSource
  -> online SendAsync / ResumeAsync 可用
```

这个链路有以下限制：

1. SessionJournal CLI 一次只创建一个 maintainer；DM-3C 尚未提供 derived set publish/list command，
   也没有实际使用 orchestrator 的多 maintainer 并行能力。
2. SessionJournal artifact runner 当前从 raw root 和空 `MemoryPack` 开始 full replay，并要求目标
   lineage 为空；不能从 latest artifact anchor 增量续跑。
3. 每个 runner 都用自己的 `--threshold-tokens` 和 active-history buffer 决定 split；两个 producer
   是否使用相同 source block/snapshot、最终是否形成完整 set，只由参数巧合、操作者和验收脚本保证。
4. 当前 orchestrator 以整体 `Task.WhenAll` 返回 batch；任一 maintainer 抛错时，上层拿不到结构化的
   partial-success 结算结果，也没有把“producer 成功”和“artifact 已持久化”纳入同一 batch 状态机。
5. role 到 maintainer/profile/target 的映射没有通用 catalog。
6. 哪些 role 是 required、optional 或属于同一个 coherence group，没有可执行合同。
7. 没有自动触发、重试、过期结果处理、branch/rewind 处理或 bootstrap policy。
8. `ArtifactSetCommitted` 把 derived-set definition、active/latest、setup hint 和 Prepared provenance
   混入 raw Parent chain；候选 C 已将“删除 raw activation、改为 derived -> raw 单向引用”确定为目标
   方向，尚待具体设计和实施。

## 5. 功能缺口

### 5.1 Provisioning contract

需要由上层声明一类 Session 的 memory topology。概念上至少要能表达：

- role id；
- maintainer/profile/factory identity；
- target `MemoryPackBlockPath`；
- required / optional；
- coherence group；
- trigger / freshness policy；
- producer fingerprint policy；
- failure、retry 与降级策略；
- 是否允许读取其他 memory artifacts 作为输入。

不应急于把这些字段全部固化成一个 raw wire record。第一版可以是进程内、显式注册的 typed
configuration 或 rebuildable derived policy。只有实际进入外部 request、影响 exact execution replay 的
内容才由 Prepared 提升为 raw fact。

### 5.2 Maintainer discovery 与 construction

需要一个稳定的构造边界，把 provisioning entry 解析为 `IMemoryBlockMaintainer`。第一版不必实现 DLL
扫描或通用插件市场；显式 registry/factory 足以验证架构：

```text
role/profile id
  -> maintainer factory
  -> runtime dependencies
  -> IMemoryBlockMaintainer
```

SessionJournal core 不应引用 `SessionJournal.Maintainers`，也不应硬编码 `autobiography`、
`world-understanding` 或未来的 indexer/analyzer 类型。

### 5.3 Incremental lineage recovery

每个 maintainer 应从自己 current-lineage 上的 latest usable artifact 恢复：

```text
latest artifact
  -> materialize old target block
  -> previous anchor / source cursor
  -> 只读取尚未吸收的 dependency-safe raw range
```

不能在长 Session 上每次从 root full replay。derived latest index 只是可重建加速信息；artifact
provenance 和 raw Parent lineage 仍是正确性来源。另一个 branch 上“更新”的 artifact 不得使当前 branch
跳过未吸收 raw events。

### 5.4 Maintenance epoch planning

history partition 是所有同一 coherence group maintainer 的公共输入，不能由 maintainer/profile
各自决定。Derived Artifact Epoch Planner 需要先针对一个 observed raw head 产生 immutable、
bounded、可审计的 epoch plan：

```text
DerivedArtifactEpochPlan {
  epochId / schema,
  coherenceGroup / topologyVersion,
  previousEpoch?,
  plannedAtRawHead,
  sourceStartExclusive,
  sourceEndInclusive,        // 也是新的 common anchor
  rawStartSetups,
  splitPolicyFingerprint,
  tokenEstimatorId,
  measuredCost,
  inputSet?,
  planning diagnostics
}
```

`epochId` 应由 immutable plan identity 确定，不由 wall-clock 或某个 maintainer run id 决定。plan
必须在任何 LLM producer 调用前写入 derived repository；所有 artifacts 显式声明同一 `epochId` 和
coverage。一个 required role 的多次 prompt-tuning 运行可以产生多个 candidate artifacts，但不能推进
epoch cursor 或改变 source range。

#### 5.4.1 Config 只定义 policy，epoch ledger 固定实际分块

在 DerivedMemory repository 中保存 planner config 是合理的，但**仅有 config 文件不够**。配置回答
“以后怎样切”，epoch ledger 回答“历史实际上怎样切了”。建议区分：

```text
planner config snapshot
  tokenEstimatorId
  minimumRecentTokens
  epochTriggerTokens
  dependencyBoundaryPolicyId
  coherenceGroup/topologyVersion
  scheduling/headroom policy

immutable epoch plans
  exact raw range + anchor + setup refs + config fingerprint
```

可以有一个 repo-local `planner-config.json` 指向 current immutable config snapshot；旧 epoch 必须保存
其 config fingerprint，配置更新只能影响未来 epoch，不能重新解释过去分块。config/plan 都属于
DerivedMemory subsystem，不进入 raw SessionJournal event sequence，也不参与 Prepared reopen
correctness。删除 derived repository 后 raw session 仍可打开；要重建相同 epoch geometry，则调用方需
重新提供相同 config snapshot。

这些对象更适合由整个 `DerivedMemoryRepository` 拥有，而不是塞进只负责 immutable set records 的
`DerivedArtifactSetStore`。

#### 5.4.2 “保留最小 recent history，再按阈值滑出”的触发规则

第一版可以采用如下确定性策略：

```text
latest usable ArtifactSet anchor
        │
        ├── eligible old prefix ──> 下一 maintenance epoch
        └── newest dependency-closed suffix，至少保留 minimumRecentTokens

if Cost(eligible old prefix) >= epochTriggerTokens:
    persist one epoch plan
    run registered maintainers for that coherence group
```

cut 必须落在 replay-safe / dependency-safe raw boundary。boundary 对齐会使每个 epoch 大小不同，这是
正常现象；同步要求是全部成员共享 exact epoch，而不是每块 token 数相等。`threshold` 应保留足够
headroom，使旧 ArtifactSet + 持续增长 suffix 在新 set 生成期间仍不超过 request budget；达到 hard
limit 而 required set 尚未完成时，应 backpressure/not-ready，不能静默 full replay。

同步域应是 `coherenceGroup`，不必把未来所有动态 maintainer 强制塞进一个全球 epoch。Role-Play 的
core-memory group 可包含 autobiography + world-understanding；按客户动态创建的 specialized memory
可以使用自己的 group/topology/schedule，再由 Context Planner 组合候选。

#### 5.4.3 日常运行与 prompt-tuning 共用同一计划

- online：planner 创建一个 epoch 后，针对固定 input snapshot 并行启动该 group 的所有 registered
  required maintainers；
- retry/restart：只重开同一 epoch 中未结算的 role，不重新计算 split；
- prompt-tuning：未来 CLI 以 `--epoch <id> --profile <name>` 读取既有 plan，允许覆盖 prompt/model 并
  写出 alternative candidate；
- evaluation/publication：明确选择每个 required role 在该 epoch 的一个 exact candidate，全部满足后
  才发布 DerivedArtifactSet；
- 若确需研究另一种 partition policy，应显式创建另一条 derived plan lineage/实验 repository，而不是
  在同一 set lineage 上悄悄用不同 `--threshold-tokens`。

不同 coherence groups 可以有不同频率；但同一 group 内不得用“较旧 role artifact + 本 epoch 新
artifact”偶然拼 set。若 maintainer 判断无需改文，也应产生显式 no-change/identity result 来结算该
epoch，而不是沿用旧成员冒充同步完成。

### 5.5 并行执行与结果结算

同一个 epoch 中没有显式依赖的 maintainers 应基于同一 immutable input snapshot 并行执行。结算需要
区分：

- shared epoch plan 已 durable；
- producer completion 成功；
- artifact sidecar 持久化成功；
- whole-set policy 满足；
- derived ArtifactSet record 原子发布成功；
- 若随后发起 completion，Prepared exact-head CAS 成功。

单个 producer 成功不等于新 derived set 已可选。若 required member 失败：

- 已写出的单个 artifact 可以保留用于诊断或后续复用；
- 旧 usable coherent set 保持可选；
- 不得发布半套新 derived set；
- retry 不得伪造 source head 或覆盖原 artifact。

### 5.6 Coherent set selection 与 publication

上层必须验证应用语义，例如 Role-Play ChatSession 的最低可用 set 是否要求：

```text
autobiography
world-understanding
```

SessionJournal raw core 不验证这个集合，也不保存它的 id。上层将 coherent set 作为 immutable derived
record 原子写入 sidecar，并验证：

- members 全部声明同一 exact epochId / coverage plan；
- exact members 与上层 required/optional role policy；
- common anchor / source ranges 位于 current raw Parent lineage；
- coverage setup refs 与 raw authoritative setup stream 一致；
- contribution snapshots 可确定性 materialize。

Context Planner 可以选择同一 derived set lineage 上较新或较旧的 set，以调节 raw suffix 长度。选择结果
进入 Prepared 时，只把 exact context snapshots、raw start/range/hash 与 raw setup refs提升为 execution
fact；不把 derived set/artifact ids写入 raw。

derived set 的 default/latest index 只是可重建选择加速。若需要记录某次 Prepared 来自哪个 set，使用
`preparedAddress -> derivedSetId` 的 derived usage index，不反向污染 raw。

### 5.7 Online lifecycle integration

最终需要明确 Session 创建、恢复和正常运行时的入口：

- 新 Session 在没有 artifact 时如何 bootstrap；
- `SendAsync()` 遇到 not-ready 时由谁触发 maintenance；
- maintenance 是否阻塞本次回复，还是后台生成供后续 request 使用；
- crash 后如何识别未结算 epoch；
- setup mutation、rewind、fork 后哪些 artifacts/set 仍可用；
- 多个 engine/process 竞争 maintenance/derived publication 时如何以 sidecar atomic
  compare-and-publish 收口。

这些决策不应破坏已有 Prepared/Started reopen 合同。Provider attempt 一旦 Prepared/Started，恢复必须
使用 committed manifest，而不能重新运行 memory planner。

## 6. 核心不变量

后续具体设计应至少保持：

1. raw SessionJournal events 是 execution/history 正确性来源；derived artifacts/indexes 可删除重建。
2. SessionJournal raw core 不解释应用 role 名称，不依赖 `SessionJournal.Maintainers` 或 concrete
   DerivedMemory 程序集；依赖方向只能是 concrete derived implementation -> SessionJournal contracts。
3. 每个 maintainer 只能更新声明的唯一 target block。
4. history partition 由 coherence-group epoch planner 统一决定，不能由单个 maintainer 的
   `--threshold-tokens` 或 prompt/profile 决定。
5. epoch plan 必须先于 producer call durable；同一 group 的并行、retry 与 prompt-tuning maintainers
   必须从同一 fixed epoch/input snapshot 读取，不能重新切分或互相观察未提交结果。
6. artifact 必须记录 exact epochId、实际 source range、anchor、setup 和 producer fingerprint。
7. derived ArtifactSet 只能引用同一 exact epoch、current-lineage、policy-compatible members 和 raw
   provenance。
8. raw SessionJournal 不引用 derived plan/artifact/set ids；derived records 可以引用 raw addresses。
9. required member 失败时不得发布半套新 set；旧 coherent set 保持可用。
10. Prepared 自包含实际 request context 与 raw provenance；planner/renderer 或 sidecar 变化不影响 exact
   reopen。
11. 恢复与增量维护的 raw payload reads 必须由 operational tail/未吸收范围决定，而不是全历史长度决定。
12. MemoryPack 是 selected artifacts 的 materialized context view，不是覆盖 raw history 的第二份 SSOT。

## 7. 非目标

第一阶段不应顺带实施：

- 在 SessionJournal core 硬编码 autobiography/world-understanding；
- 让 SessionJournal 直接打开 concrete derived store、解释 artifact id/index/path，或让 execution tail
  resolver 依赖 candidate provider；
- 通用外部程序集扫描、热插拔或插件市场；
- 把 derived artifact 变成 raw truth；
- 把 planner config/epoch ledger 写入 raw SessionJournal，或把 mutable current config 当作解释旧 epoch
  的权威；
- 为简化 planner 合并 runtime-config/system-prompt 两条 setup stream；
- 让 Prepared/Started reopen 重新依赖 derived sidecar；
- 同时完成通用 retrieval/ranking/learning planner；
- 为旧实验 journal 增加隐式兼容猜测；
- 通过恢复 full conversation context 来运行 maintainer。

## 8. 建议的后续实施切片

以下只是可调度的工作包边界，不是锁死的类/API 方案。

### MMP-0：Candidate contract 与程序集依赖倒置

目标：

- 在 SessionJournal 定义 store-neutral 的 context candidate request/result contract；
- 用 fake provider 建立 raw ancestor/setup/replay-safe/duplicate-target 与 exact-head CAS 的 core tests；
- 先给 current `DerivedRecapStore` 增加 adapter，使 Engine/materializer 不再接触 concrete
  `DerivedRecapArtifact`；
- 建立独立 DerivedMemory 项目并迁移 store/set/index/provider，由 SessionJournal CLI/Agent host 注入；
- 保持 raw-only `Open`、tail recovery、audit 与 Prepared reopen 在 provider 缺失时可用。

第一步只做依赖倒置和等价适配，不同时删除 raw kind 12 或迁移全部 maintainer substrate。后续 Prepared
self-contained wire cut、raw activation 删除和 producer 迁移应各自 review/验收。

### MMP-A：Shared history epoch planner 与 plan ledger

目标：

- 定义 versioned planner config snapshot、policy fingerprint 和 immutable epoch record；
- 从 previous coherent-set anchor 增量读取 raw tail，计算 dependency-safe eligible prefix；
- 实现 `minimumRecentTokens + epochTriggerTokens` 与 headroom/hard-limit 合同；
- 在 producer 前原子写入 epoch plan，并支持 restart/branch/rewind 校验；
- CLI 能先只生成/列出 epoch plans，不调用 LLM。

这一步先固定共享 history geometry，是后续独立 prompt-tuning 和并行 production 的共同输入。

### MMP-B：Role catalog 与 epoch-bound independent runner

目标：

- 定义最小 typed provisioning contract；
- 用显式 registry/factory 构造 maintainer；
- 在 Agent/Session 应用层声明 Role-Play 的 required roles；
- 让单 maintainer runner 通过 exact `epochId` 恢复 old block/input set，只消费该 epoch 的 raw range；
- 同一 role 可用不同 prompt/model 对同一 epoch 生成多个 candidate，不移动 epoch cursor；
- 证明新增第三个测试 role不修改 SessionJournal core。

重复 substrate 调查已于 2026-07-27 收口：旧
`prototypes/ChatSession/MemorySubstrate.cs` 及其 session-level maintainer API 已删除，
`prototypes/SessionJournal/SessionMemoryContracts.cs` 是唯一继续演化的实现。详见
[ChatSession Legacy Memory Substrate 退役](../../ChatSession/legacy-memory-substrate-retirement.md)。
若未来抽取独立 substrate 项目，仍应作为有意识的 dependency refactor，而不是重新复制一份实现。

### MMP-C：coherence-group epoch 与并行 producer

目标：

- 读取一个已经 durable 的 epoch plan 和 fixed input snapshot；
- 通过 orchestrator 并行运行无依赖 maintainers；
- 独立写入每个成功 artifact；
- 支持 crash 后只重开未结算 roles，并保留 prompt-tuning alternatives；
- required set 全部成功后原子写入一个 immutable derived ArtifactSet record；
- 不向 raw Parent chain 追加 ArtifactSet activation；
- 任一 required member 失败时不发布半套结果。

### MMP-D：自动 readiness / lifecycle integration

目标：

- 把 bootstrap、not-ready 和 freshness 触发接入 Agent/Session online lifecycle；
- crash/restart 后确定性重开尚未结算的 maintenance 工作；
- 对 setup mutation、branch、rewind 和 CAS race 建立明确行为；
- 增加观测：epoch、role、source range、artifact、derived set publication、失败阶段。

### MMP-E：Context Planner 与 derived ArtifactSet selection

目标：

- 根据 token budget/staleness 选择 exact coherent set 与 raw suffix；
- 支持 latest / `NthPrevious(n)` 与后续 budgeted candidate comparison；
- 保证 exact context snapshots 和 raw provenance 进入 Prepared，reopen 不重新规划或读取 sidecar；
- 再考虑 optional roles、不同更新频率、retrieval/ranking。

## 9. 建议验收场景

后续每个切片都应有 focused tests；整体至少覆盖：

1. `SessionJournal.csproj` 不引用 concrete DerivedMemory 项目；host/composition root 才同时引用并注入。
2. SessionJournal core 只用 fake candidate source 即可验证 planning contract；provider 缺失时 raw-only
   Open/audit/tail recovery/Prepared reopen 仍可用。
3. 对一条 raw history 只运行 epoch planner，得到确定性的 exact ranges；boundary 对齐允许各块大小不同。
4. autobiography 与 world-understanding 使用不同进程、不同 prompt override 运行时，按同一 epochId
   读取完全相同的 source range，不再各自解释 threshold。
5. 修改 current planner config 只影响未来 epoch，旧 epoch 的 config fingerprint/range 不变。
6. 注册两个 required roles 和一个 optional role，不修改 SessionJournal。
7. 两个无依赖 maintainer 确实从同一 epoch/input snapshot 并行运行，各自只能写自己的 target。
8. 两个成功 artifacts 具有同一 epochId 和可审计的 lineage/setup/coverage，并原子发布一个 derived
   coherent set；
   raw event count 不因此变化。
9. 一个 required maintainer 失败时，另一个 artifact 可保留，但不发布半套 set；restart 只重跑缺失
   role，不重新规划 epoch。
10. 进程重启后从 latest epoch/set anchor 增量续跑，不从 root 重放。
11. 10k+ 冷历史前缀不增加 maintenance 恢复的 payload reads。
12. fork/rewind 后拒绝复用 divergent branch epoch/artifact/cursor；恶意/损坏 provider 返回错误
   anchor/setup/duplicate target 时由 SessionJournal 在 Prepared/provider call 前拒绝。
13. setup 在 artifact coverage 后变化时，derived set coverage 与 Prepared raw-start/current setup
   语义仍可验证。
14. sidecar 在 Prepared 前缺失时 fail-fast/重新规划；Prepared 后缺失仍可 exact reopen。
15. 使用真实 autobiography + world-understanding profiles 完成一次端到端 shared-plan、并行
    production、artifact
    publication、online completion 和 restart。

真实 LLM 验收可以继续使用
`prototypes/Galatea/.atelia/galatea/connections.json` 中的 `dsv4p`，但源码文档只记录非敏感的
artifact id、anchor、计数、readiness 和测试结果。

## 10. 关键代码与文档路径

当前实现入口：

- `prototypes/SessionJournal/SessionMemoryContracts.cs`
- `prototypes/SessionJournal/DerivedRecapStore.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `prototypes/SessionJournal/SessionTailContextProjection.cs`
- `prototypes/SessionJournal/SessionCoherentRequestRecipe.cs`
- `prototypes/SessionJournal.DerivedMemory/`（目标新程序集；暂名，当前不存在）
- `prototypes/SessionJournal.DerivedMemory/DerivedArtifactSetStore.cs`（目标 concrete store，当前不存在）
- `prototypes/SessionJournal.Maintainers/`
- `prototypes/SessionJournal.Cli/MemoryMaintainerRun.cs`
- `prototypes/SessionJournal.Cli/MemoryMaintainerArtifactWriting.cs`
- `prototypes/SessionJournal.Cli/Program.cs`
- `docs/ChatSession/legacy-memory-substrate-retirement.md`（legacy 重复 substrate 的退役决策）

相关设计背景：

- `docs/SessionJournal/event-sourced-session-architecture-roadmap.md`
- `docs/Galatea/memory-content-implementation-notes.md`
- `docs/SessionJournal/tail-execution-recovery-design.md`
- `docs/SessionJournal/tail-execution-recovery-simplification-study.md`
- `docs/SessionJournal/done/coherent-request-manifest-simplification-plan.md`

## 11. 进入具体设计前必须回答的问题

1. provisioning 是静态 Session 类型配置，还是允许成为可变的 durable session state？
2. required role set 是按产品、persona、session template，还是按每次 request 定义？
3. planner config 的 user-authored source 与 repo-local current pointer 如何更新/并发发布？
4. 不同 `coherenceGroup` 的 epoch schedules 如何在一次 ContextPlan 中组合？
5. 一个 maintainer 是否允许读取其他 role 的上一 epoch artifact；若允许，依赖图如何固定和审计？
6. no-change result 是写轻量 identity artifact，还是 set member 显式引用 previous content hash？
7. bootstrap 时没有旧 artifact，是否允许基于 bounded raw prefix 生成 genesis set？
8. derived set/epoch lineage index 如何重建；`NthPrevious(n)` 如何在 branch/rewind 后保持确定性？
9. generic Memory substrate 最终应留在 SessionJournal，还是抽到独立项目供 SessionJournal、
   ChatSession 与内容插件共同引用？

这些问题需要在实施对应切片时逐项做 evidence-backed design。当前备忘的目标是保存功能缺口、边界和
意图，避免后续把“已有并行 orchestrator”误认为“通用上层编排已经完成”。

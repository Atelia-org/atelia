# SessionJournal Tail Execution Recovery 后续化简候选

> **状态**：Current Research / CS-3D7 后候选集
> **日期**：2026-07-27
> **当前基线**：[Tail-only Execution Recovery Design](tail-execution-recovery-design.md)
> **已完成计划**：
> [CS-3D6：Coherent-only Request Manifest 化简计划](done/coherent-request-manifest-simplification-plan.md)
> [CS-3D7：Prepared / Provider Attempt 对称化](done/prepared-provider-attempt-symmetry-design.md)
> **后续实施计划**：
> [DerivedMemory 可替换子系统与 Shared Epoch 实施方案](derived-memory-subsystem-implementation-plan.md)
> **目标**：只保留在 current trunk 上仍成立的化简候选；不以牺牲 crash recovery、raw provenance、
> exact reopen 或 bounded reads 换取表面简洁。

## 0. 当前结论

CS-3D6 已经完成 request manifest 的主要收口；CS-3D7 又将 Prepared 与 provider attempt 分离：
online 只有 coherent artifact-tail，`CompletionRequestPrepared` 只有 v3，legacy reader/writer 已删除，并已用真实 legacy import、两个
maintainer artifact 和一次 exact checkpoint 验收。已实施内容不在本文重复，详见
[归档计划](done/coherent-request-manifest-simplification-plan.md)。

当前仍值得研究的化简候选只有三组：

1. **Request snapshot spike**：比较 current recipe-authoritative reopen 与 exact canonical request
   snapshot；先测 stored bytes 和恢复开销，不先改 wire。
2. **ArtifactSet 从 raw activation 与 raw-core 程序集解耦**：ArtifactSet 保持 derived、只单向引用
   raw；具体维护/存储/选择实现迁入可替换子系统，SessionJournal 只消费中性的 coherent context
   candidate；同一 coherence group 由 shared epoch planner 先固定 history coverage，再运行各
   maintainer；Prepared 只固化实际进入 request 的精确 context snapshot 与 raw provenance。
3. **共享正向 operational semantics**：减少 full reducer、suffix fold 和 validator 的规则复述，
   但保留独立 reverse tail collector。

`SessionJournalEngine` 的一般职责拆分仍应等语义收口后再做；但候选 C 所要求的程序集依赖倒置不是普通
“移动代码”，而是 raw/derived 正确性边界的一部分，应随候选 C 一起冻结并分阶段落地。

以下方向不再是本文候选：

- 合并 `RuntimeConfigSetup` / `SystemPromptSetup`：此前已决定保留两条 sticky stream。
- 恢复 `full-raw`、`explicit-artifact-tail` 或 bounded `bootstrap-raw` policy：这会倒退已经确立的
  coherent ArtifactSet readiness 合同。
- 为旧 Prepared 增加 compatibility decoder、缺省字段推断或 root replay fallback。
- 再规划一次覆盖所有 event kind 的“大一统 wire v2”：current codec 已支持 per-kind body schema
  version，后续只升级真正发生变化的 kind。

## 1. Current trunk 基线

### 1.1 当前实现事实

request preparation 与 reopen 现在只有一个 wire shape：

```text
exact coherent ArtifactSet
+ dependency-closed raw suffix
+ paired governing setup refs
+ visible tool/runtime identity
+ dispatch target
-> CompletionRequestPrepared v3
-> CompletionAttemptStarted
```

具体事实：

- online completion 必须先有 current-lineage 上可用的 `ArtifactSetCommitted`；没有 artifact set 时明确
  not-ready，不回退到 full history。
- Prepared 内联 exact artifact context snapshots、tool definitions 和 identity/provenance；raw suffix
  仍以 address/hash + deterministic recipe 重建。
- Prepared 后即使 derived sidecar 被删除，`SessionPreparedRequestReconstructor` 仍能 exact reopen。
- `RuntimeConfigSetup` 与 `SystemPromptSetup` 保持两条独立 sticky stream，Prepared pin 两条 exact refs。
- `SessionExecutionTailResolver` 独立反向收集 operational dependencies；online routing 不调用
  `Project()`。
- `SessionReducer` 继续提供 root-to-head full audit/oracle；`SessionTailContextProjection.FoldSuffix`
  是 seeded request-context fold；offline validator 仍对不可信 raw 做完整只读验证。
- `CompletionRequestPrepared` 只保存 request origin；每次 dispatch 由严格空 body 的
  `CompletionAttemptStarted` 表示，其 event address 是内部 attempt identity。
- `ArtifactSetCommitted` 同时扮演 coherent set definition 和 active/latest checkpoint；current Engine
  从最近 raw activation 或更近的 Prepared-pinned reference 恢复它，而 latest-only validators 使两者在
  合法历史上语义相同。

### 1.2 当前规模

截至本次刷新，恢复主链十个核心文件约 **7,622 LOC**：

| 区域 | LOC |
| --- | ---: |
| `SessionJournalEngine` | 1,972 |
| `SessionExecutionTailResolver` | 942 |
| `SessionTailContextProjection` | 751 |
| `SessionPreparedRequestReconstructor` | 543 |
| `SessionReducer` | 617 |
| `SessionJournalOfflineValidator` | 605 |
| event + manifest codecs | 1,709 |
| contracts + manifest records | 483 |

`SessionJournal.Tests` 约 **10,713 LOC**。这些数字不是删代码 KPI，只说明 request reconstruction、
attempt chain、ArtifactSet activation 与 operational legality 仍有足够大的维护面，值得继续寻找语义级
化简。

### 1.3 不可删除的复杂度

以下属于问题本身：

1. raw Parent lineage 是 execution correctness source。
2. online reopen / routing 不得静默 root replay。
3. Prepared 后不得重新规划；provider-facing request 必须 exact reopen。
4. provider/tool 外部调用前必须有 durable identity / reservation。
5. tool result 必须按 Action 声明顺序与 durable sequence join。
6. ArtifactSet coherence 与 per-request selection 必须可审计。
7. sidecar/index 丢失不能改写 committed request。
8. branch/rewind 只能沿 current Parent lineage 解析。
9. full audit projection 与 bounded online recovery 必须继续分离。
10. offline untrusted import 必须 strict、read-only、O(raw inventory) 验证。

因此仍应否决：

- 找不到 checkpoint 时自动 full replay；
- 固定 N events/turns 的 tail window；
- 删除 `ToolExecutionStarted` 并把外部调用与 Result 合并；
- 让 Artifact/MemoryPack 决定 pending execution；
- 缓存完整 `SessionProjection`；
- 为了复用，把 reverse tail resolver 改成 forward full fold。

## 2. 候选 A：Exact request snapshot spike

### 2.1 当前问题

Prepared v3 已经只有一个 coherent recipe，但 exact reopen 仍需要：

1. 读取 paired setup payload；
2. 读取并验证 exact raw range；
3. 读取 referenced `ArtifactSetCommitted`；
4. seed 并运行 dependency-closed suffix fold；
5. 聚合 inline artifact snapshots；
6. 重新执行 canonical recipe；
7. 对比 commitment。

这条路径正确且 bounded，但 renderer、suffix fold 和 reconstructor 仍处在 online correctness core。
`SessionPreparedRequestReconstructor` 的 543 LOC 不是 legacy policy 分支，而是 current recipe 本身的
证明成本。

### 2.2 两种权威形态

| 方案 | Online reopen | 主要代价 |
| --- | --- | --- |
| R：current recipe-authoritative | 重新读取 refs、fold、render，再校验 commitment | renderer 与 fold 版本长期属于恢复合同 |
| S：snapshot-authoritative | 读取 snapshot、校验 hash、decode request | 每个 Prepared 在 raw 中复制 bounded request bytes |

S 的候选形状：

```text
CompletionRequestPrepared {
  canonicalRequestCodecId,
  canonicalRequestBytes,
  canonicalRequestSha256,
  provenance: {
    governingSetupRefs,
    rawRange,
    artifactSetRef,
    contributionHashes
  },
  executionAndDispatchIdentity
}
```

online reopen 只信 committed snapshot + commitment；offline audit 可以选择重新运行 current recipe 并
对比 snapshot，但不能把昂贵 rematerialization 继续当成 online 前置条件。

### 2.3 只做 spike，不先采纳

必须在真实 autobiography + world-understanding、Observation completion 与 multi-tool continuation 上
比较：

- canonical request logical bytes；
- current Prepared logical/stored bytes；
- snapshot Prepared logical/stored bytes；
- 100 / 1,000 requests 的累计 raw amplification；
- reopen header visits、payload reads、decoded bytes 与 peak live bytes；
- EventJournal 压缩后重复 system prompt、artifact snapshot、tools 和 suffix 的实际增量。

决策不应只看倍数：还要设单 request 和长期 session 的绝对存储预算。若 snapshot 的存储成本不可接受，
继续保留 current recipe 是完全合法的结论；这时应转向候选 D，减少 recipe 各实现间的规则复述。

### 2.4 不应混入 spike 的改动

- 不同时修改 Attempt state machine。
- 不同时改变 ArtifactSet latest/selection 语义。
- 不恢复第二种 bootstrap/full-raw Prepared shape。
- 不因 snapshot 可 reopen 就删除 provenance；raw range、setup 与 artifact selection 仍需审计。

## 3. 已采纳 B：Prepared 与 provider attempt 对称化

该候选已由 CS-3D7 实施并从研究列表移除。current truth table、wire cut、recovery policy 与验收
证据见 [Prepared / Provider Attempt 对称化设计](done/prepared-provider-attempt-symmetry-design.md)。

## 4. 候选 C：ArtifactSet 从 raw activation 解耦

### 4.1 修订后的边界判断

`ArtifactSetCommitted` 当前是 SessionJournal 主 Parent 链上的 raw kind 12。它把 derived sidecar
members 的 exact ids 写回 raw event，并同时承担：

1. coherent member set definition；
2. session active/default selection；
3. governing setup 的近头 checkpoint；
4. online completion readiness；
5. Prepared 必须引用的 membership/provenance assertion。

这与 ArtifactSet 的长期定位不一致。Autobiography、world-understanding、按客户或任务动态创建的
specialized memory blocks 都是可重建、可替换的派生认知。prompt、producer、role topology、内容划分或
召回策略变化，都可能生成新的 artifact/set；这些变化不应不断向主 execution sequence 追加“当前派生
视图”事件，也不应让 raw validity 依赖某个 sidecar id 仍然存在。

因此采用更强的单向引用原则：

```text
raw SessionJournal event sequence
        ^
        | raw addresses / ranges / setup refs
        |
derived artifacts + derived ArtifactSet records
```

- raw event 只能以 raw address 引用其他 raw event；
- derived artifact/set 可以引用 raw anchor、source range 和 setup events；
- raw execution history 不引用 derived artifact id、set id 或 derived latest index；
- 删除全部 derived memory 后，raw SessionJournal 仍应可独立验证 execution legality，并能 exact
  reopen 已经 Prepared 的 provider request。

这里的 ArtifactSet 是多个 artifacts（例如 autobiography + world-understanding）的 coherent
组合；单个 autobiography 并不是 ArtifactSet。

### 4.2 Prepared 是必要而合法的“派生内容晋升”

单向引用不意味着 raw 永远不能包含由派生系统产生的文本。一旦某些 memory blocks 实际进入一次 LLM
request，它们就从“可替换候选”变成了“这次外部调用实际看到了什么”的 execution fact。

`CompletionRequestPrepared` 必须自包含地固定：

```text
exact materialized context snapshots / canonical request bytes
exact RawStartExclusive + raw range hash
governing setups at RawStartExclusive
governing setups at request boundary
tool/runtime/target identity
request commitment
```

但它不需要引用 `ArtifactSetCommitted` 或 derived set id。Prepared 前由 planner 校验 derived
ArtifactSet coherence；Prepared 后 exact reopen 只依赖 raw manifest，不重新打开 derived store，也不
重新运行 planner。

当前 manifest 已内联 artifact context snapshots，却仍保存 `ActiveArtifactSet` ref，并从 raw
activation 取得 anchor coverage setup seed。移除 raw activation 时，Prepared 必须直接保存
`RawStartExclusive` 对应的 paired setup refs（可命名为 `RawStartSetups` / `CoverageSetups`），使
reconstructor 能从该 seed fold raw suffix。它们只引用 raw setup events，符合单向边界。

若需要审计“哪一个 derived set 产生了这次 Prepared”，应写入 rebuildable derived usage index：

```text
derived usage record {
  preparedAddress -> derivedSetId
}
```

方向仍是 derived -> raw；它不能参与 raw reopen correctness。

### 4.3 可替换的 Derived Context 子系统

Derived ArtifactSet 的具体维护、持久化、lineage、indexes 和 candidate discovery 不应继续位于
`Atelia.SessionJournal` 程序集。目标依赖图是：

```text
Agent Host / SessionJournal.Cli / composition root
├── Atelia.SessionJournal
│   ├── raw event / execution recovery
│   ├── coherent context candidate contracts
│   ├── raw suffix materialization
│   └── Prepared exact reopen
└── Atelia.SessionJournal.DerivedMemory（暂名）
    ├── artifact / ArtifactSet store
    ├── maintainer provisioning / orchestration
    ├── derived lineage / indexes
    └── candidate discovery / selection
         └── 单向引用 Atelia.SessionJournal contracts
```

`Atelia.SessionJournal` 不引用具体 DerivedMemory 程序集。composition root 同时引用两者、构造具体
provider，并将接口实例注入 Engine/request coordinator。第一阶段接口可以继续由 SessionJournal
程序集定义；不必仅为依赖倒置立即增加第三个 `SessionJournal.Abstractions` 程序集。将来出现多个实现、
contracts 足够稳定或需要更小依赖面时，再抽取独立 abstractions assembly。

SessionJournal 依赖的应是能力合同，而不是存储合同。推荐概念名
`ICoherentContextCandidateSource` / `ISessionContextCandidateProvider`，而不是
`IDerivedArtifactSetStore`。一个最小调用形状概念上为：

```csharp
ValueTask<SessionContextCandidate?> SelectAsync(
    SessionContextSelectionRequest request,
    CancellationToken cancellationToken);
```

返回值是 store-neutral 的 materialization candidate，至少包含：

```text
RawStartExclusive
RawStartSetups
minimal raw coverage/source provenance
Contributions: [{ carrier, blockKey, exactText }]
optional estimated cost / opaque diagnostics
```

它不向 SessionJournal 暴露 artifact 文件路径、latest index、store schema、maintainer/profile 类型，也
不要求 SessionJournal 持久化 derived artifact/set id。若返回一个 ready-made
`CompletionRequest`，具体实现就能绕过 raw suffix、setup、tool 和 canonicalization 校验，因此也不
属于合法合同。

职责边界如下：

- derived subsystem 验证 member/set 的应用级 coherence、required roles、producer fingerprint、
  derived lineage 与存储完整性；
- SessionJournal 对 provider 结果重新验证 raw-facing invariants：anchor 是 current completion
  boundary 的严格祖先、boundary replay-safe、source/setup refs 属于真实 Parent lineage、
  contribution target 结构合法且唯一；
- SessionJournal 确定 contribution 稳定排序，fold dependency-closed raw suffix，构造 canonical
  request，并以 exact-head CAS 提交 Prepared；
- provider 的 derived selection token 只能进入 derived usage/diagnostics，不能成为 raw reopen
  correctness 的输入。

provider 为高效 branch-aware selection 可使用 SessionJournal 提供的窄只读 lineage capability，或
返回有序 candidates 交给 core 逐个验证；不要把 concrete Engine/EventJournal ownership 暴露给它。
无论采用哪一种，core 都必须对最终 selected candidate 再验证，不能把 raw correctness 外包给
可替换实现。

### 4.4 Derived ArtifactSet store

`ArtifactSetCommitted` 不应改名后继续留在 raw；目标是将 coherent set definition 移入 derived
sidecar store。一个 immutable derived set record 概念上需要：

```text
DerivedArtifactSet {
  setId / schema / policy fingerprint,
  epochId / epochPlanFingerprint,
  commonAnchor,
  coverageSetups,
  members: [{ roleId, artifactId, target, contributionHash }],
  previousSet?, coherenceGroup?,
  producer/planner provenance
}
```

该记录与其 indexes 都可删除重建。正确性检查发生在 Prepared **之前**：

- required members 属于同一个 immutable history coverage epoch，而不是各自按 maintainer-local
  threshold 偶然切到相同 anchor；
- members exact、role/target 唯一且满足上层 provisioning policy；
- artifacts 的 source ranges / anchors 位于 current raw Parent lineage；
- common anchor 是 replay-safe boundary；
- coverage setup refs 与 raw authoritative setup stream 一致；
- contribution snapshots 能从 exact members 确定性 materialize。

`previousSet` 或等价 lineage 不能用文件时间、物理地址或全局 latest 代替。branch/rewind 后，planner
只能选择其 raw provenance 可达的 derived sets。

ArtifactSet store 之外还需要同属 DerivedMemory repository 的 shared epoch planner/config/ledger。
config 保存 token estimator、`minimumRecentTokens`、`epochTriggerTokens`、dependency-safe boundary
policy 与 coherence topology；immutable epoch plan 保存实际 raw range、anchor、setup refs 和 config
fingerprint。config 决定未来怎样切，plan ledger 固定历史实际怎样切，二者不能合并成一个可变
`threshold` 字段。具体设计见
[MemoryMaintainer Provisioning / Planner 功能缺口备忘 §5.4](memory-maintainer-provisioning-planner-gap.md)。

### 4.5 Selection：recent history 长短属于 request planning

取消 raw activation 后，Context Planner 可以在 derived candidates 中选择：

```text
较新的 coherent set + 较短 raw suffix
较旧的 coherent set + 较长 raw suffix
```

第一版可以提供可解释的 `NthPrevious(n)`：

- `n = 0`：当前 derived set lineage 的最新 usable coherent set；
- `n > 0`：沿同一 derived set lineage 选择更早 set；
- exact `n` 不可用时 fail-fast，不静默移动 ordinal；
- 不允许分别按 role 选择“第 n 个 artifact”后偶然拼 set。

但 set ordinal 不天然等价于 context cost。新 set 的 anchor 未必严格单调，role topology 也可能变化；
最终 planner 应按 exact common anchor、raw suffix token/byte cost、required roles、freshness 与 budget
比较 candidates。online read 成本应为：

```text
O(current completion boundary 到 selected commonAnchor)
```

selected anchor 之前增加 10k+ cold prefix 不得增加 payload reads；选择更旧 anchor 主动带来的更长
suffix reads 则是预期成本，不应被性能测试误判为回归。

### 4.6 Staged restart，不把 derived subsystem 塞进 execution resolver

“重启需要 raw + ArtifactSet”应按阶段解释：

1. `SessionExecutionTailResolver` 仅靠 raw Parent chain 重建 execution phase/checkpoint；
2. head 已有 Prepared/Started 时，仅靠 raw manifest exact reopen，不读取 derived memory；
3. head 是尚未 Prepared 的 `AwaitingAgentAction` 时，resume planning 才加载 derived ArtifactSet；
4. idle session 可以 raw-only 打开；新 Send 的 memory readiness 在 mutation/provider call 前检查。

这样 derived subsystem 缺失会使“规划下一次 context”not-ready，但不会把合法 raw journal 判成
corruption。
若希望打开 session 时同时报告 memory readiness，可以在 Engine 上层组合两份只读 projection；不要让
execution tail DFA 依赖 sidecar。

这里也要澄清“coherent-tail reducer”的边界：`SessionExecutionTailResolver` 和 full
`SessionReducer` 不依赖 candidate provider；只有尚未 Prepared 的 request-context
planning/materialization 路径依赖它。Prepared/Started 之后只读取 raw manifest。

### 4.7 由此产生的化简

移除 raw `ArtifactSetCommitted` 后，可以删除或收缩：

- `SessionEventKind.ArtifactSetCommitted` 及其 codec/body；
- reducer / tail resolver 中该 event 的 idle-boundary 分支；
- `ResolveActiveArtifactSet()` 的 raw latest/Prepared 继承混合语义；
- offline validator 对 raw activation、latest-equals-selected 和 historical sidecar assertion 的验证；
- Prepared reconstructor 对 selected activation 必须位于 raw range、必须为 latest 的验证；
- `SessionRequestManifestDefaults.ActiveArtifactSetPolicy*`；
- raw validator/report 中 `ActiveArtifactSet` readiness。

需要新增或迁移：

- SessionJournal 中 store-neutral 的 coherent context candidate contracts；
- 独立 DerivedMemory 程序集中的 rebuildable `DerivedArtifactSetStore`、provider adapter 与
  branch-aware selection；
- host/composition root 对 concrete provider 的构造与注入；
- Prepared 的 `RawStartSetups`；
- Prepared context inputs 与 derived artifact ids 解耦；
- pre-Prepared derived readiness/coherence validator；
- derived usage/audit index（若确有审计需求）；
- governing-setup 的替代近头 hint。

最后一项不能遗漏：当前 `ArtifactSetCommitted.CurrentSetups` 也是
`ResolveGoverningSetup()` 的 early-exit checkpoint。移除 raw event 后，近头 Prepared 仍可提供 setup
hint；首次 Prepared 前的长 import/session 可使用可重建 derived hint，并以 raw header walk
authoritative fallback。不能为了删除 ArtifactSet event 而重新引入隐式 root replay。

当前 `SessionMemoryContracts.cs` 中的 `RewriteMemoryBlockMaintainer`、
`MemoryMaintenanceOrchestrator` 和 mutable `MemoryPack` 也不是 raw execution core 的长期职责。
第一切片不必把它们与 store 一次性全部迁走；先以 adapter 切断 Engine 对 concrete
`DerivedRecapStore` / `DerivedRecapArtifact` 的依赖，再按 provisioning 方案迁移 producer substrate，
可以降低 wire cut 与物理搬迁同时发生的风险。

### 4.8 与应用层 role 的边界

SessionJournal raw core 不硬编码 `autobiography` 或 `world-understanding`，也不验证 derived set 是否满足
某个 Agent 的最低 memory topology。role catalog、required/optional、dynamic maintainer 与 coherent
set policy 属于上层 provisioning/planner，见
[MemoryMaintainer Provisioning / Planner 功能缺口备忘](memory-maintainer-provisioning-planner-gap.md)。

### 4.9 Migration boundary

这是明确的 breaking raw/manifest upgrade：

- 旧实验 journal 中的 raw kind 12 不做 compatibility decode 或语义猜测；
- 通过原始 legacy export 重新 import，或使用显式 offline migration；
- Prepared 新版本必须完全自包含 raw-start setup seed 和 exact context；
- migration 后删除全部 derived memory，raw validator 与 Prepared reopen 仍应通过；
- 随后重跑 maintainers，重建 derived ArtifactSet collection 和 readiness。

## 5. 候选 D：共享正向 operational semantics

### 5.1 仍存在的复述

同一组 `SessionEventKind` 仍被多条路径解释：

- `SessionReducer`：full root-to-head audit fold；
- `SessionExecutionTailResolver`：reverse dependency collection；
- `SessionTailContextProjection.FoldSuffix`：seeded forward context fold；
- `SessionPreparedRequestReconstructor`：request boundary 与 exact reopen validation；
- `SessionJournalOfflineValidator`：full chronology/provenance validation。

它们不能被粗暴合成“万能 reducer”。reverse collector 决定 online read complexity；full/suffix fold
决定正向 legality；offline validator 面向不可信 raw。真正可共享的是无 IO、无 collection policy 的
局部规则。

### 5.2 建议边界

#### `SessionEventSemantics`

集中纯 helper/validator，例如：

- header/body 与 direct-parent legality；
- completion boundary、correlation 与 reason；
- action/tool call identity；
- setup exact reference；
- current trunk 的 ArtifactSet membership assertion；候选 C 后收缩为 store-neutral candidate 的
  raw-facing anchor/setup/target validation；
- reserved tool sequence/runtime identity。

#### `SessionOperationalFold`

可 seed 的正向 transition kernel：

- `FromEmpty()`：供 full reducer；
- `FromDependencyClosed(seed)`：供 suffix projection；
- `Apply(event)`：统一正向 execution legality；
- 输出 `SessionExecutionState` 和可选 conversation effects。

wrapper 决定是否积累完整 context、suffix context，或只消费 execution state。

明确不合并：

- `SessionExecutionTailResolver` 的 reverse dependency collection；
- reconstructor 的 request/provenance 编排；
- offline validator 的全库 inventory 与 untrusted-boundary 职责。

### 5.3 更小的先行切口

`FoldSuffix` 当前仍接受 nullable execution seed，并用 `InferSeedPhase` 扩大输入合同。可以先引入显式
`DependencyClosedSeed`：

- seed 必须来自 validated tail recovery；
- 明确允许的 anchor phase/head kind；
- mid-tool/open-action seed fail-fast；
- setup 与 tool execution checkpoint 一起进入 seed。

这个切口不改变 wire，也不要求立即统一 full reducer，适合先验证 shared kernel 的真实收益。

### 5.4 防止“共享”破坏 oracle

- 保留 full-vs-tail differential tests。
- reverse collector 不直接调用 full fold。
- shared helper 不读取 journal、不决定 walk/stop boundary。
- mutation corpus 应逐字段破坏 Parent、attempt、setup、tool sequence 与 artifact provenance。
- 不以减少异常消息数量作为成功标准；目标是让同一 legality rule 只有一个权威实现。

## 6. 候选 E：Engine 职责拆分

`SessionJournalEngine` 当前约 1,972 LOC，同时承担 public facade、exact-head CAS、setup mutation、
artifact readiness、request preparation、provider dispatch、tool loop 与 recovery routing。

长期内部职责可以接近：

```text
SessionJournalEngine             public facade / exact-head CAS
SessionRecoveryService           reverse tail collection
SessionRequestCoordinator        prepare, commit, dispatch, reopen
SessionToolLoopDriver             reserve, execute, observe
SessionRequestContextPlanner      consume candidate provider / validate / materialize
SessionAuditProjector             full replay + offline oracle
```

这不是要求每项都引入 interface。优先使用 internal sealed class + 明确输入输出，保留 Engine 作为
transaction/CAS owner。

拆分时机：

- request coordinator 最好在 snapshot/recipe 决策之后稳定；
- provider driver 最好在 Attempt 对称化之后稳定；
- request context planner 最好在 candidate provider 边界、raw/derived 单向引用与 Prepared
  self-contained 合同明确之后稳定；
- 与这些候选无关的纯 setup/readiness helper 可以提前移动，但每次移动都应证明行为与计数器不变。

因此本项是结构性收口，不应成为独立“大搬家”。

## 7. 建议研究与实施顺序

### P0：Request snapshot stored-byte spike

只增加 benchmark/报告，不改 wire。产出明确的 storage/read/memory 数据与采用/否决结论。

### P1：Dependency-closed seed 与 pure semantics 小切口

先收紧 `FoldSuffix` 输入合同，再提取少量已被三个以上 consumer 复述的纯 validator。每个切口保持
full-vs-tail differential 与 1-vs-10001 指标。

### P2：Attempt symmetry ADR 与实现

先冻结 crash-state truth table、provider policy 和 failpoint matrix，再做单-kind/相关-kind wire cut。
它与 request snapshot 可以独立决策，不应打包成一次大迁移。

### P3：ArtifactSet raw 解耦

先冻结单向引用与 staged restart 合同，再按独立切片实施：

1. 在 SessionJournal 定义 store-neutral candidate contract；用 fake provider 建立 core contract tests；
2. 用 current `DerivedRecapStore` adapter 把 Engine/materializer 从 concrete artifact 类型切到接口，
   保持行为不变；
3. 建立独立 DerivedMemory 程序集，迁移 derived store/set/index 与 branch-aware provider；由 host
   composition root 注入；
4. 先实现 shared history epoch config/ledger，使独立 maintainer 与 online parallel run 消费同一
   coverage plan；
5. Prepared 自包含 `RawStartSetups` 和 exact context inputs；
6. reconstructor/offline validator 切断 raw activation 与 concrete derived subsystem 依赖；
7. 删除 raw kind 12 及 Engine/reducer/tail-resolver 分支；
8. 补 governing-setup derived hint、migration、real acceptance，再迁移 maintainer/orchestrator
   substrate。

durable default activation 不是当前目标。只有未来产品明确需要“跨 request 改变默认 derived set”时，才在
derived planner state 中设计 default；不要重新把它放回 raw execution sequence。

### P4：Shared operational fold 与 Engine 收口

根据 P1 的实际净收益决定是否继续形成完整 kernel。随后按已经稳定的 request、attempt、ArtifactSet
边界拆 Engine。

这条顺序刻意避免再次制造一个同时修改 Configuration、Prepared、Attempt 与 ArtifactSet 的整体 wire
迁移。current per-kind versioning 允许每个候选独立设计、独立 review、独立重建实验 journal。

## 8. 共同验收闸门

所有候选实施时都必须保持：

- `TailResolver(head).State == FullFold(head).ExecutionState`；
- branch/rewind/divergent refs fail-fast；
- Prepared 后 sidecar 删除仍 exact reopen；
- tool operation id + reserved sequence 在所有 failpoint 后稳定；
- 1 vs 10001 cold prefix 的 online header/payload/decoded bytes 不增长；
- online `ChronologicalChainReadCount == 0`；
- online `FullProjectionInvocationCount == 0`；
- offline validator 严格只读，坏 tail 不修复原 repo；
- SessionJournal 不依赖 `Agent.Core`；
- real legacy export 可导入全新 repo，并能建立 coherent ArtifactSet；
- worktree 中不重新出现 legacy policy alias、compat decoder 或 silent full replay。

若采用 request snapshot，额外验收：

- snapshot SHA 与 canonical bytes exact；
- provider-facing request 与 prepare 时逐字节相同；
- stored-byte benchmark 达到 P0 设定的绝对预算；
- snapshot decode 不依赖 current renderer/planner；
- selection provenance 仍可离线审计。

若采用 Attempt 对称化，额外验收：

- Prepared-only head 与 AttemptStarted head 的恢复 phase 明确不同；
- 首次与 retry 使用同一 attempt event 和 driver；
- Started 前/后所有 failpoint 的 refuse/lookup/restart 行为有确定测试；
- exact-head CAS 与 attempt identity 在 crash/reopen 后稳定。

若调整 ArtifactSet 语义，额外验收：

- `SessionJournal.csproj` 不引用 concrete DerivedMemory 项目；host/composition root 才同时引用并注入；
- SessionJournal core tests 使用 fake candidate source 即可覆盖 request planning，具体 store/integration
  tests 属于 DerivedMemory 项目；
- raw event inventory 不再包含 `ArtifactSetCommitted` 或其他 derived-set activation；
- 删除全部 derived memory 后，raw validator 和已有 Prepared exact reopen 仍通过；
- planner 可选的 derived set 必须在 current Parent lineage 上且 coverage/setup coherence 可验证；
- 同一 set 的 required members 必须共享 exact epochId/coverage plan；单 maintainer prompt-tuning 不得
  重新解释 threshold 或推进 epoch cursor；
- Prepared 自包含 exact context、raw range/hash、raw-start/current setup refs，不引用 derived ids；
- Prepared 前缺 member明确 derived-not-ready，不能借未来 Prepared snapshot 自证；
- head 为 Prepared/Started 时 recovery 不读取 derived subsystem；
- head 为未 Prepared 的 `AwaitingAgentAction` 时 derived 缺失只阻止 planning，不污染 raw execution state；
- provider 返回 divergent anchor、错误 setup、重复 target 或越界 contribution 时由 SessionJournal 在
  Prepared/provider call 前拒绝；
- 选择更旧 set 时 reads 只随 selected anchor 到 boundary 的距离增长，anchor 前 cold prefix 不增长。

## 9. 给后续 Coding Agent 的决策原则

不要把“文件变小”当成协议已经化简。下一阶段优先回答三个可证伪的问题：

1. exact request snapshot 的真实存储成本，是否值得换掉 recipe reconstruction correctness core？
2. Prepared 去除 derived refs 后，最小 self-contained context/raw-start seed 是否足以保持 exact reopen？
3. 哪些 legality rule 确实在三个以上路径复述，提取后又不会损害 full-vs-tail oracle 独立性？

Attempt 对称化的问题则更明确：它能澄清 durable request 与 uncertain provider call 的状态边界，但不能
替代 provider idempotency/reconcile。只有在这条边界的 driver policy 也被写清楚后，wire 化简才是
完整的。

当前 trunk 已经证明 tail-only recovery 与 coherent artifact context 能工作。后续化简应以“小实验、
单语义切片、per-kind wire cut”为单位推进，不再为已删除的 legacy 表面付费，也不再把多个尚未决策的
协议一次性绑在一起。

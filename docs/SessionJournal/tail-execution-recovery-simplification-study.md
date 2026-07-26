# SessionJournal Tail Execution Recovery 后续化简候选

> **状态**：Current Research / CS-3D6 后候选集
> **日期**：2026-07-27
> **当前基线**：[Tail-only Execution Recovery Design](tail-execution-recovery-design.md)
> **已完成计划**：
> [CS-3D6：Coherent-only Request Manifest 化简计划](done/coherent-request-manifest-simplification-plan.md)
> **目标**：只保留在 current trunk 上仍成立的化简候选；不以牺牲 crash recovery、raw provenance、
> exact reopen 或 bounded reads 换取表面简洁。

## 0. 当前结论

CS-3D6 已经完成 request manifest 的主要收口：online 只有 coherent artifact-tail、
`CompletionRequestPrepared` 只有 v2、legacy reader/writer 已删除，并已用真实 legacy import、两个
maintainer artifact 和一次 exact checkpoint 验收。已实施内容不在本文重复，详见
[归档计划](done/coherent-request-manifest-simplification-plan.md)。

当前仍值得研究的化简候选只有四组：

1. **Request snapshot spike**：比较 current recipe-authoritative reopen 与 exact canonical request
   snapshot；先测 stored bytes 和恢复开销，不先改 wire。
2. **Attempt 对称化**：把“request 已 durable”和“某次 provider attempt 已开始”拆成不同事件，
   统一首次调用与 retry。
3. **ArtifactSet definition / activation 解耦**：先确认 active/default set 是否真是长期 session
   state，再决定是否取消 current latest-equals-selected 约束。
4. **共享正向 operational semantics**：减少 full reducer、suffix fold 和 validator 的规则复述，
   但保留独立 reverse tail collector。

`SessionJournalEngine` 的职责拆分仍有价值，但应作为上述语义收口的结构性后续，而不是先移动代码。

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
-> CompletionRequestPrepared v2
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
- `CompletionRequestPrepared` 同时建立首次 attempt identity；retry 由
  `CompletionAttemptRestarted` 表示。
- `ArtifactSetCommitted` 同时扮演 coherent set definition 和 active/latest checkpoint；current Engine
  选择 completion boundary 最近的合法 activation。

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

Prepared v2 已经只有一个 coherent recipe，但 exact reopen 仍需要：

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

## 3. 候选 B：Prepared 与 provider attempt 对称化

### 3.1 当前不对称

current event flow：

```text
completion boundary
-> CompletionRequestPrepared       // request durable，同时是首次 active attempt
-> AgentActionProduced | CompletionAttemptFailed

CompletionRequestPrepared
-> CompletionAttemptRestarted      // 第二次及以后 attempt
-> AgentActionProduced | CompletionAttemptFailed
```

Prepared commit 后、provider 实际发送前发生崩溃，与“请求已发送但结果未知”共享同一个恢复状态。首次
attempt 和 restart 也由两种 body、两套 chain validation/failpoint 分支表达。

### 3.2 候选目标

```text
completion boundary
-> CompletionRequestPrepared       // request durable，尚未声明调用 provider
-> CompletionAttemptStarted        // 每一次调用前都写
-> AgentActionProduced | CompletionAttemptFailed
```

retry 继续引用 exact source Prepared，但与首次调用使用同一种 `CompletionAttemptStarted`。

潜在收益：

- Prepared head 可明确表示 `ReadyToDispatch`；
- AttemptStarted head 才进入 uncertain external-call window；
- 首次与 retry 共用同一个 routing、identity chain 和 validator；
- 可删除 `CompletionAttemptRestarted`、`ReplacesAttemptId` 与 initial/restart 两套状态分支；
- attempt identity 可考虑直接使用 Started event address，减少另造字符串 identity。

### 3.3 不能夸大的收益

`CompletionAttemptStarted` 仍不能原子化“raw event durable”与“网络请求已经被 provider 接收”。它只把
安全的 Prepared 状态与 uncertain attempt 状态分开。真正的 exactly-once 仍需要 provider idempotency
key、result lookup 或显式 reconcile policy。

实施前必须决定：

- Started event address 是否足以作为内部 attempt identity；
- provider idempotency key 如何从 durable identity 派生；
- Prepared 状态 reopen 后是自动 dispatch，还是由显式 driver policy 决定；
- Started 后无 terminal result 时的 refuse/lookup/restart 合同；
- v1 kind 的实验 journal 是重建还是离线迁移；不得加入 silent compatibility。

### 3.4 建议切片

1. 先写 event-sequence truth table 与 failpoint matrix，不改 production。
2. 为首次/retry 建立同一 oracle tests。
3. 对涉及的 event kind 单独升级 body schema；不发动全局 wire cut。
4. 切换 Engine、tail resolver、full reducer、offline validator。
5. 删除旧 Restarted kind/path 与重复 fixtures。
6. 重跑 exact-head CAS、provider failure/reopen 和 1-vs-10001 gates。

## 4. 候选 C：ArtifactSet definition 与 active/default selection 解耦

### 4.1 当前混责

`ArtifactSetCommitted` 当前同时表达：

1. coherent member set 的 durable definition；
2. session 当前 active/default set；
3. activation Parent 时的 governing setup checkpoint；
4. online planner 必须选择的 nearest/latest set。

Engine、reconstructor 与 offline validator 都会验证 Prepared 引用 raw range 中最新的合法 activation。
这在没有 Context Planner 时提供了清晰、bounded 的 default，但会阻止未来 planner 选择：

```text
更早但仍 coherent 的 ArtifactSet + 更长但仍在预算内的 raw suffix
```

### 4.2 先做产品语义决定

在改 wire 前先回答：

> active/default ArtifactSet 是否是用户可观察、需要跨 request 持久化的 session state？

- **若不是**：`ArtifactSetCommitted` 只需定义 immutable candidate，本次选择完全由 Prepared
  provenance 表达。
- **若是**：definition 与 activation 应拆成不同概念；默认 activation 可以影响 planner 默认值，但不应
  自动等价于“本次 request 唯一合法选择”。

不要在问题尚未回答时仅为减少字段而改 body。

### 4.3 可能的目标形状

```text
ArtifactSetCommitted {
  commonAnchor,
  coverageSetups,
  members: [{ roleId, artifactId }]
}

ArtifactSetActivated / ContextDefaultsChanged   // 仅在产品确实需要时

CompletionRequestPrepared {
  exact selected ArtifactSet ref,
  exact raw suffix provenance,
  ...
}
```

候选瘦身项包括：

- 将 activation-current setups 从 immutable definition 移出；
- 评估 member 的 artifact kind、target、content hash 是否能由 exact artifact identity 验证；
- 删除值相同的 policy id + fingerprint 二元组。

这些删除必须逐项证明仍能在 Prepared **之前**验证 coherence、branch lineage 和 sidecar member；
不能依赖 Prepared 后的 inline snapshot 倒推 activation 合法性。

### 4.4 与应用层 role 的边界

SessionJournal core 继续只验证 role/member 的结构性 coherence，不硬编码 `autobiography` 或
`world-understanding`。哪些 role 构成 ChatSession 的最低可用 set，仍属于上层 provisioning/planner
合同。

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
- ArtifactSet membership assertion；
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
SessionArtifactSetResolver        readiness / selection validation
SessionAuditProjector             full replay + offline oracle
```

这不是要求每项都引入 interface。优先使用 internal sealed class + 明确输入输出，保留 Engine 作为
transaction/CAS owner。

拆分时机：

- request coordinator 最好在 snapshot/recipe 决策之后稳定；
- provider driver 最好在 Attempt 对称化之后稳定；
- ArtifactSet resolver 最好在 definition/activation 语义明确之后稳定；
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

### P3：ArtifactSet 产品语义决定

等 Context Planner/default-selection 需求足够具体后，决定：

- immutable definitions only；或
- definition + explicit default activation。

只有决定完成后才设计瘦身 body 和 selection validator。

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

- definition、default activation 与 per-request selection 三者不再混用；
- planner 可选的 set 必须在 current Parent lineage 上且 coverage/setup coherence 可验证；
- Prepared 始终 pin exact selected set；
- Prepared 前缺 member 明确 not-ready，不能借 Prepared snapshot 自证。

## 9. 给后续 Coding Agent 的决策原则

不要把“文件变小”当成协议已经化简。下一阶段优先回答三个可证伪的问题：

1. exact request snapshot 的真实存储成本，是否值得换掉 recipe reconstruction correctness core？
2. active/default ArtifactSet 是否真是产品状态，还是 current planner 尚未出现时的临时选择策略？
3. 哪些 legality rule 确实在三个以上路径复述，提取后又不会损害 full-vs-tail oracle 独立性？

Attempt 对称化的问题则更明确：它能澄清 durable request 与 uncertain provider call 的状态边界，但不能
替代 provider idempotency/reconcile。只有在这条边界的 driver policy 也被写清楚后，wire 化简才是
完整的。

当前 trunk 已经证明 tail-only recovery 与 coherent artifact context 能工作。后续化简应以“小实验、
单语义切片、per-kind wire cut”为单位推进，不再为已删除的 legacy 表面付费，也不再把多个尚未决策的
协议一次性绑在一起。

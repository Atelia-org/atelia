# SessionJournal Tail Execution Recovery 化简调研

> **状态**：Research Report / 尚未实施
> **日期**：2026-07-27
> **调研对象**：[Tail-only Execution Recovery Design](tail-execution-recovery-design.md)
> **目标**：在继续 Context Planner、provider capability 与 tool reconcile 之前，识别能够减少协议、
> 实现和测试复杂度的重构机会；不以牺牲 crash recovery、raw provenance 或 bounded reads 换取表面简洁。

## 0. 结论摘要

当前实现不是“基本路线错误”。以下复杂度属于问题本身，不能删：

- provider request 与 tool side effect 的 crash window；
- 多 tool call 的 dependency closure；
- exact request reopen；
- raw Parent lineage、setup 与 artifact provenance；
- full audit 与 bounded online recovery 的分离。

但 CS-3A～D5 分片逐层推进后，确实沉积了四类可以化简的偶然复杂度：

1. `CompletionRequestPrepared` 同时充当 request snapshot 与首次 attempt，后续 attempt 却由
   `CompletionAttemptRestarted` 表达，状态机不对称。
2. `full-raw`、`explicit-artifact-tail`、`coherent-artifact-tail` 三套 manifest policy 仍共存，
   其中前两套已是 legacy 表面。
3. `ArtifactSetCommitted` 同时承担 coherent set definition、latest-active selection 与 setup
   checkpoint；这会妨碍未来 planner 选择更早 ArtifactSet。
4. full reducer、artifact suffix fold、tail resolver、reconstructor 与 offline validator 多次复述
   operational legality。

本报告建议：**停止在 v1 上继续加字段，先规划一次 SessionJournal wire v2 归一化。**

推荐目标不是推倒 tail-only 架构，而是把它收束为：

```text
一个完整 SessionConfiguration fact
+ 一个 immutable coherent ArtifactSet definition
+ 一个每次 request 的 exact Prepared snapshot / provenance
+ 一个对称的 CompletionAttemptStarted
+ 一个共享的 forward operational transition kernel
+ 一个仍然独立的 reverse dependency collector
```

其中最高杠杆但需要先实验的选择是：

> 让 `CompletionRequestPrepared` 直接持久化压缩后的 exact canonical request bytes，并把
> ContextPlan 降为 provenance/audit；online reopen 不再重新运行 renderer 和 suffix fold。

这会以可测量的 raw storage 增长换取显著更小的恢复协议。由于 SessionJournal request 本来受 LLM
context window 约束，且 EventJournal 默认使用 zlib，这个方向值得优先做 stored-byte spike；但在得到
真实数据前，不应把它直接定为新 wire。

## 1. 调研范围与证据

### 1.1 当前规模

恢复主链的十个核心文件约 **7,961 LOC**：

| 区域 | 当前 LOC |
| --- | ---: |
| `SessionJournalEngine` | 1,988 |
| `SessionExecutionTailResolver` | 956 |
| `SessionTailContextProjection` | 787 |
| `SessionPreparedRequestReconstructor` | 598 |
| `SessionReducer` | 622 |
| `SessionJournalOfflineValidator` | 626 |
| event + manifest codecs | 1,875 |
| contracts + manifest records | 509 |

`SessionJournal.Tests` 约 **9,866 LOC**。大测试面本身不是坏事，但相当一部分矩阵在重复证明三套
request policy 和多套 operational DFA。

### 1.2 语义复述

同一组 12 个 `SessionEventKind` 被以下路径分别解释：

- `SessionReducer`：full root-to-head fold；
- `SessionExecutionTailResolver`：reverse dependency collection；
- `SessionTailContextProjection.FoldSuffix`：seeded forward fold；
- `SessionPreparedRequestReconstructor`：request boundary/replay validation；
- `SessionJournalOfflineValidator`：full chronology/provenance validation。

不能把它们粗暴合成一个“万能 reducer”：reverse collector 决定读取复杂度，full/suffix fold 决定正向
legality，它们职责不同。但正向 transition 和纯 validator 不应继续复述。

### 1.3 Legacy 表面

当前 online writer 实际只有：

- 默认 `RequireActiveArtifactSet` → `coherent-artifact-tail`；
- 显式 `LegacyFullRaw` → `full-raw`。

`explicit-artifact-tail` 已没有 writer，只为旧 committed Prepared reopen 保留。仓库内也没有真实
production caller 选择 `LegacyFullRaw`。本项目尚未发布、旧实验 journal 可重建，因此继续维护这两套
协议的收益低于其认知和测试成本。

### 1.4 Manifest 冗余

当前 manifest 至少有 13 个硬编码、互相等值或尚无 producer 的字段：

- `PlannerFingerprint`、`RenderingProfileId`、renderer id/fingerprint 都由 policy 固定映射；
- `ModelProfileId == Parameters.ModelId`；
- `Plan.Reason == Attempt.Reason`，而 reason 又可由 raw boundary kind 推导；
- `RecalledInputs` 永远为空；
- `EstimatedInputTokens` 只是 `byteLength / 4`；
- `Rendering.ToolCodecId == ToolSet.CodecId`，且 codec id 是全局常量；
- canonical codec / reasoning codec fingerprints 是全局常量；
- commitment algorithm 固定为 SHA-256；
- `Target.CompletionSurfaceId` 重复 governing runtime config。

这些字段看似为未来扩展留槽，实际却让当前 codec 必须维护 hard-coded identity tuple。真正的
Context Planner、retriever 或多 renderer 出现时再设计对应 schema，比提前持久化空槽更简单。

## 2. 哪些复杂度必须保留

化简不能破坏以下不变量：

1. raw SessionJournal 仍是 execution correctness source。
2. online reopen / routing 不得静默 root replay。
3. Prepared 后不能重新运行 planner；必须 exact reopen 当时 request。
4. provider/tool 外部调用前必须先有 durable identity / reservation。
5. tool result 必须按 Action 声明顺序 join，不能只看 append 顺序。
6. ArtifactSet coherence 与 per-request selection 必须可审计。
7. sidecar/index 丢失不能改写已提交 request 的内容。
8. full `Project()` / `ReplayHistory()` 继续保留审计语义。
9. offline untrusted import 继续 strict、read-only、O(raw inventory) 验证。
10. branch/rewind 只能沿 current Parent lineage 解析。

因此以下“化简”仍应否决：

- 恢复找不到 checkpoint 时自动 full replay；
- 固定 N events 的 tail window；
- 删除 `ToolExecutionStarted`，把外部调用与 Result 合并；
- 让 Artifact/MemoryPack 决定 pending execution；
- 缓存完整 `SessionProjection`；
- 让 reverse tail resolver 为了复用而改成 forward full fold。

## 3. 推荐的目标协议

### 3.1 Configuration：两条 sticky stream 收成一个完整 fact

当前 bootstrap 是：

```text
RuntimeConfigSetup
-> SystemPromptSetup
-> SessionCreated
```

每个 Prepared、ArtifactSet 与 setup resolver 都要携带或收集一对 setup refs。建议 v2 改为：

```text
SessionCreated {
  configuration: {
    runtime: { modelId, completionSurfaceId, schema },
    systemPrompt
  }
}

SessionConfigurationChanged {
  configuration: complete snapshot
}
```

任何 runtime 或 prompt mutation 都写一份完整 snapshot，不保存 patch。

收益：

- root 不变量变成真正的 `SessionCreated`；
- governing configuration 从“两次定位 + 两次读取”变成一个 exact reference；
- 删除 bootstrap 三事件特判；
- Prepared、ArtifactSet、reconstructor、offline validator 的 paired setup fields/validators 减半；
- 不再需要分别维护 prompt/config cursor 地址。

代价是单独修改 runtime config 时会重复 system prompt bytes。setup mutation 相对 request/turn 很少，
且 EventJournal 默认 zlib；这里应优先选择简单、完整的 state fact。

### 3.2 Prepared 与 Attempt 必须对称

当前 `CompletionRequestPrepared` 同时表示：

- request 已 materialize；
- 首次 provider attempt 可能已经开始。

而后续 retry 才写 `CompletionAttemptRestarted`。Prepared 后、provider 调用前崩溃会被误归入
uncertain window。

建议 v2：

```text
completion boundary
-> CompletionRequestPrepared       // request durable，尚未调用 provider
-> CompletionAttemptStarted        // 每次调用前都写；自己的 address 是 attempt identity
-> AgentActionProduced | CompletionAttemptFailed
```

retry 也写同一种 `CompletionAttemptStarted`，并 exact-reference 原 Prepared。

由此可以删除：

- `CompletionAttemptRestarted`；
- `ReplacesAttemptId`；
- Prepared 中永远为 null 的 replacement 字段；
- Failure 中重复的 attempt id；
- restart attempt-id uniqueness chain；
- initial/restart 两套 provider routing 与 failpoint 分支。

Prepared head 是安全的 `ReadyToDispatch`；AttemptStarted head 才是 uncertain external call。仍须承认：
durable Started 与真实网络发送不能原子化，provider idempotency/result lookup/reconcile 仍是最终解。

Attempt identity 建议直接使用 `CompletionAttemptStarted` 的 `EventAddress`。若 provider 需要字符串
idempotency key，可在 append 成功后 canonical format 该地址。每个 Started 可携带 exact Prepared ref，
保持 O(1) request lookup，不必沿 retry chain 回溯。

### 3.3 Prepared：把“恢复事实”与“选择解释”分层

当前 `SessionContextPlan` 同时是：

- planner audit；
- renderer recipe；
- request reconstruction input；
- active ArtifactSet assertion；
- 部分 attempt metadata。

v2 应至少拆成两个概念：

```text
SelectionAudit
  planner/policy identity
  chosen ArtifactSet / raw range / recalled items
  budget and explanation

PreparedRequest
  exact governing configuration ref
  exact request payload or exact reconstruction recipe
  tool runtime identity
  dispatch target identity
  request commitment
  execution checkpoint
```

没有真实 producer 的 recall、budget、estimator 字段不要先进入 wire；等 Context Planner 实施时再加入
`SelectionAudit` 的版本化 schema。

这里要区分 **selection strategy** 与 **Prepared wire shape**。删除 legacy `full-raw` codec 不代表
新会话在首个 ArtifactSet 产生前不能请求 LLM。planner 仍需有一个严格受预算约束的
`bootstrap-raw` 选择：

- 仅当从 governing configuration / session root 到 boundary 的 raw suffix 未超过明确预算时可选；
- 超预算且尚无 coherent ArtifactSet 时，返回“需要先建 artifact”的 liveness 结果，不能静默 full replay；
- 若采用 snapshot-authoritative，bootstrap 与 artifact-tail 最终都编码成同一种 Prepared snapshot，
  差异只留在 `SelectionAudit`；
- 若保留 recipe-authoritative，则 v2 至少需要 bounded bootstrap recipe 与 coherent artifact recipe，
  但两者应共享统一 envelope，不能复活三套 manifest/reconstructor。

#### 推荐先做的 stored-byte spike

比较两种权威形态：

| 方案 | 恢复 | 主要代价 |
| --- | --- | --- |
| R：引用式 recipe | 读取 config/raw/artifact refs 并重新 fold/render | reconstructor 与 renderer version 长期进入 correctness core |
| S：exact canonical snapshot | 读取 Prepared、校验 hash、decode request | 每次 request 在 raw 中复制 bounded context bytes |

本报告倾向 **S：snapshot-authoritative + provenance-auditable**：

- Prepared 保存 canonical request bytes、codec id 与 SHA-256；
- Plan 仍记录 raw range、ArtifactSet、contribution hashes 等 provenance；
- online reopen 只读取 snapshot，不重新运行 renderer；
- offline validator 可选择重新 materialize 并对比 snapshot，作为昂贵审计，而不是 online correctness
  前置条件。

理由：

- request 大小已经受模型 context window 上限约束；
- 当前 manifest 已内联 artifact contribution 与 tool definitions；
- 新增的主要重复只是 bounded raw suffix；
- EventJournal 默认 zlib，长文本和重复 context 通常可压缩；
- 可直接删除三 policy reconstructor dispatch 和 renderer fingerprint tuple。

但这会改变 roadmap 早期“引用式优先”的选择，必须先在真实 autobiography/world-understanding +
multi-tool request 上测量：

- logical request bytes；
- 当前 manifest stored bytes；
- snapshot manifest stored bytes；
- 100 / 1,000 requests 的累计 raw amplification；
- reopen payload reads、decoded bytes 与 peak live bytes。

建议门槛：若 snapshot stored bytes 增长处于可接受的固定倍数/绝对预算，选择 S；否则保留 R，但仍只支持
一个 v2 coherent recipe，删除 legacy policy 与冗余 identity fields。

### 3.4 ArtifactSet：保留 definition，拆掉 latest-active 混责

coherent ArtifactSet 是长期概念，不建议直接删除。但当前 `ArtifactSetCommitted` 同时表示：

1. immutable set definition；
2. session 当前 active/default set；
3. activation parent 的 setup checkpoint；
4. 下一次 request 必须选择 latest activation。

第 4 条与 roadmap 的 planner 候选策略冲突：planner 应允许选择“更早 ArtifactSet + 更长 suffix”。

建议目标：

```text
ArtifactSetCommitted
  = immutable coherent candidate definition

ContextPlan / SelectionAudit
  = 本次 request 实际选择哪个 exact set

ArtifactSetActivated / ContextDefaultsChanged
  = 只有产品真的需要“session 默认 set”时才引入
```

不要在 generic SessionJournal core 中把 nearest/latest set 当成必选 set。

当前 body 也可瘦身。由于 `ArtifactId` 已 commitment 整个 artifact identity，候选形状可以接近：

```text
ArtifactSetCommitted {
  policySchemaId,
  commonAnchor,
  coverageConfigurationRef,
  members: [{ roleId, artifactId }]
}
```

可删除或迁出：

- activation-current setups：属于 recovery/default activation，不属于 set definition；
- member 的 artifact kind、target、contribution hash：可由 exact artifact identity 验证；
- 值相同的 policy id + fingerprint 二元组。

Prepared 后的 exact reopen 继续由 request snapshot 或 inline per-role contribution 保证；Prepared 前
sidecar 删除仍是 planner liveness failure，除非未来把 artifact content 提升到 durable Artifact Journal。

### 3.5 Operational semantics：一个 forward kernel，保留 reverse collector

建议提取两个内部组件，而不是引入庞大接口层：

#### `SessionEventSemantics`

统一纯 helper/validator：

- header/body validation；
- completion boundary/correlation；
- action/tool call identity；
- setup/config exact reference；
- ArtifactSet membership assertion；
- reserved tool sequence/runtime identity。

#### `SessionOperationalFold`

可 seed 的正向 transition kernel：

- `FromEmpty()`：供 full reducer；
- `FromDependencyClosed(seed)`：供 suffix projection；
- `Apply(event)`：唯一正向 legality transition；
- 输出完整 `SessionExecutionState` 和可选 conversation effects。

wrapper 决定累积 full context、suffix context 或只取 execution state。

明确不合并：

- `SessionExecutionTailResolver` 的 reverse dependency collection；
- `SessionPreparedRequestReconstructor` / offline validator 的编排职责。

tail resolver 可以复用纯 validators，但应保留独立 collection 和 checkpoint cut，避免破坏 bounded reads，
也保留一定的 full-vs-tail differential 独立性。

当前 `FoldSuffix` 的 nullable seed / `InferSeedPhase` API 比真实合同更宽，应改为显式
`DependencyClosedSeed`；mid-tool seed 必须拒绝。

## 4. Legacy 与 manifest 收口

### 4.1 直接删除的候选

建议不再为旧实验 journal 保留 runtime/wire compatibility：

- 删除 `SessionRequestContextPolicy.LegacyFullRaw` 与 live full-raw materializer；
- 删除无 writer 的 `explicit-artifact-tail`；
- v2 不 decode 旧 full-raw/explicit Prepared；
- 删除指向 full-raw 的 `SessionRequestManifestDefaults` alias；
- `Project()` / `ReplayHistory()` 保留 full semantics，但不再用于 live request。

删除的是“无界 root-to-boundary materialization 与 legacy wire”，不是新会话 bootstrap 能力。S0/S2
必须同时确定上一节的 bounded `bootstrap-raw` selection；否则默认要求 active ArtifactSet 的 engine
无法服务刚创建的 session。

真实 legacy upgrade export 不包含这些新式 Prepared；它仍可通过 `import-session-journal` 写入 v2。
包含旧 Prepared 的实验 repo 应重建，不应给 v2 留 compatibility branches。

### 4.2 v2 manifest 至少应删除

- `Attempt.ReplacesAttemptId`；
- duplicated plan/attempt reason；
- `Plan.ModelProfileId`；
- placeholder `RecalledInputs`；
- fake `EstimatedInputTokens`；
- hard-coded planner/rendering identity tuple；
- 重复的 tool codec id；
- fixed commitment algorithm；
- 重复的 completion surface。

应保留：

- exact request bytes或单一 recipe；
- governing configuration ref；
- raw range/hash 与 exact ArtifactSet/provenance；
- tool definitions 或 snapshot 中的 tools；
- tool runtime implementation/capability identity；
- dispatch connection/adapter/client identity；
- request codec id + SHA-256；
- last-issued tool execution sequence；
- completion boundary identity。

## 5. 实现结构上的次级机会

协议收口后再拆 `SessionJournalEngine`。在 wire 未稳定前机械分文件只会移动复杂度。

建议最终内部职责：

```text
SessionJournalEngine             public facade / exact-head CAS
SessionRecoveryService           reverse tail collection
SessionRequestCoordinator        plan, prepare, dispatch, reopen
SessionToolLoopDriver             reserve, execute, observe
SessionConfigurationResolver     one sticky configuration fact
SessionAuditProjector            full replay + offline oracle
```

不要求为每项引入 interface；内部 sealed class + 明确输入输出即可。

测试应从大量 hand-built policy fixture 迁向：

- 一个 current-wire coherent fixture builder；
- legal trace generator；
- deterministic one-field mutation corpus；
- full fold / suffix fold / tail resolver state differential；
- 少量 canonical wire goldens；
- 1 vs 10k/10001 complexity invariants。

这能删除 legacy fixture 复制，同时比冻结异常整句文本更稳。

## 6. 两条可选路线

### 路线 A：保守、行为保持

1. 抽 `SessionEventSemantics`。
2. shadow 实现 `SessionOperationalFold`。
3. 迁移 `FoldSuffix` 与 `SessionReducer`。
4. 统一 exact-reference helpers。
5. 拆 Engine 文件。

预期净删约 250～400 production LOC，主要收益是减少语义漂移；wire 与旧 request policies 不变。

局限：最重的 manifest/reconstructor/attempt/activation 复杂度仍在。

### 路线 B：一次 wire v2 归一化（推荐）

先做 stored-byte spike 与 ArtifactSet semantic decision，然后：

1. 删除 live full-raw 与 explicit legacy，测试迁到 current coherent fixture。
2. 用完整 `SessionConfiguration` 取代双 setup stream。
3. 拆 `Prepared` / `AttemptStarted`，首次与 retry 对称。
4. 采用 snapshot-authoritative，或单一 coherent recipe 的 Prepared v2。
5. 把 ArtifactSet 收成 immutable candidate，取消 latest-only selection。
6. wire 稳定后提取 forward operational kernel。
7. 最后拆 Engine、合并测试矩阵并重跑真实 import。

预期可以删除：

- legacy live materializer 约 120～140 LOC；
- legacy policy codec/reconstructor 约 250～400 LOC；
- 约 500～800 LOC policy-specific tests/fixtures；
- request snapshot 若采用，可进一步大幅缩小 598 LOC reconstructor；
- shared kernel 再净删约 250～400 LOC。

这些是方向性估算，目标不是追逐 LOC，而是把“需要同时理解的协议”从三套收成一套。

## 7. 推荐实施顺序

### S0：三项决策实验

- **S0a Request snapshot storage spike**：真实 artifact-tail + multi-tool requests，比较 recipe/snapshot
  stored bytes 与 reopen reads。
- **S0b ArtifactSet semantics**：确认 `active/default set` 是否为用户可见的长期 session state。
  - 若不是：只保留 immutable set + per-request plan。
  - 若是：definition 与 activation 分事件，不再 latest-equals-selected。
- **S0c Bootstrap semantics**：确定首个 coherent ArtifactSet 前允许的 raw budget、超预算 liveness
  结果，以及它如何归一到同一个 Prepared wire。

产出：wire v2 ADR，尚不改 production protocol。

### S1：Current-wire test foundation

- 建立 coherent fixture builder；
- 把通用 engine/reopen/failpoint tests 从 full-raw fixture 迁出；
- 保留当前 bytes，先证明迁移不改变行为。

### S2：Request/Attempt v2

- 删除 legacy policies；
- 引入 Prepared-only + AttemptStarted；
- 采用 S0 选定的 snapshot 或 single-recipe manifest；
- 删除 duplicate fields 与 attempt-id replacement chain；
- 不保留 v1 decode compatibility。

### S3：Configuration v2

- `SessionCreated` 内嵌完整 configuration；
- 增加 full-snapshot `SessionConfigurationChanged`；
- paired setup refs/cursor/validators 收成一个。

### S4：ArtifactSet role split

- slim immutable set definition；
- planner 按 request 选择 exact set；
- 只有确认需要时才新增 default activation；
- offline validator 不再要求 Prepared 引用 raw range 中“最后一个”set。

### S5：Operational kernel

- 先 shared semantics；
- 再 shadow kernel；
- 迁移 suffix fold 与 full reducer；
- tail resolver 只 selective reuse validators；
- 删除旧 switches。

### S6：结构与迁移收口

- 拆 Engine；
- 删除 legacy tests/helpers；
- 重跑 legacy export → v2 import；
- 重建 DerivedRecap / ArtifactSet；
- strict offline validate；
- 更新 roadmap 与主干设计。

S2～S4 是同一个 wire v2 cutover 的三个实现工作包，不应把中间形态宣传为可长期保留的兼容合同。
可以按小 commit 逐步实现和 review，但只在三者全部完成后重建实验 journal、生成新 golden 并宣布 v2
baseline，避免先造“新 request + 旧双 setup + 旧 latest activation”后又为它维护迁移路径。

## 8. 验收闸门

每个切片至少保持：

- `TailResolver(head).State == FullFold(head).ExecutionState`；
- branch/rewind/divergent refs fail-fast；
- Prepared 后 sidecar 删除仍 exact reopen；
- Prepared head 明确 safe-to-dispatch，AttemptStarted head 明确 uncertain；
- tool operation id + reserved sequence 在所有 failpoint 后稳定；
- 1 vs 10001 cold prefix 的 online header/payload/decoded bytes 不增长；
- online `ChronologicalChainReadCount == 0`、`FullProjectionInvocationCount == 0`；
- offline validator 严格只读，坏 tail 不修复原 repo；
- SessionJournal 不依赖 Agent.Core；
- real legacy export 能导入新 repo 并通过 validator；
- fresh session 在预算内可 bootstrap，超预算且无 ArtifactSet 时显式停止而非 root replay；
- worktree 中不存在旧 policy alias、compat decoder 或 silent full replay。

若采用 request snapshot，额外验收：

- snapshot SHA 与 canonical bytes exact；
- provider-facing request 与 prepare 时逐字节相同；
- stored-byte benchmark 达到 S0 设定门槛；
- snapshot decode 不依赖当前 renderer/planner；
- selection provenance 仍可离线审计。

## 9. 最终建议

不要立即从 `SessionJournalEngine` 抽一批 helper 就宣布“复杂度已经解决”。那只能改善文件形状，无法减少
协议数量。

当前最合适的路线是：

1. 先做 request snapshot storage spike；
2. 明确 ArtifactSet definition 与 activation 的产品语义；
3. 冻结一个 single-policy、symmetric-attempt、single-configuration 的 wire v2；
4. 利用项目未发布、journal 可重建的窗口一次性切断 legacy；
5. wire 稳定后再抽共享 operational kernel。

D5 的价值没有被否定：它证明了 bounded recovery 所需的事实边界。现在要做的是把这些已验证事实重新
排列成更少、更正交的协议，而不是继续在过渡形状上叠加 Context Planner 和 provider capability。

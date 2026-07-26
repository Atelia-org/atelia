# CS-3D6：Coherent-only Request Manifest 化简计划

> **状态**：Implementation in progress / D6A、D6B、D6C1 已实施
> **日期**：2026-07-27
> **前置基线**：[Tail-only Execution Recovery Design](tail-execution-recovery-design.md)
> **来源调研**：[Tail Execution Recovery 化简调研](tail-execution-recovery-simplification-study.md)
> **目标读者**：负责逐切片具体设计、实施、review 与验收的 Coding Agent

## 0. 决策摘要

本计划采纳以下近期收束方向：

1. **保留** `RuntimeConfigSetup` 与 `SystemPromptSetup` 两条 sticky stream；不实施“合并成一个完整
   configuration fact”。
2. online request 只保留一种有意义的 materialization：
   `coherent-artifact-tail = exact coherent ArtifactSet + dependency-closed raw suffix`。
3. `full-raw` 与 `explicit-artifact-tail` 均视为可删除的实验期 legacy：
   - 停止产生新的 full-raw Prepared；
   - 不继续读取旧 full-raw / explicit Prepared；
   - 不增加兼容 decoder、缺省字段推断或 root replay fallback。
4. 先做行为保持的测试与 codec 前置切口，再通过**一次**
   `CompletionRequestPrepared v2` cutover 删除 legacy reader 和 manifest 冗余。
5. 本轮仍采用 recipe-authoritative exact reopen，不引入 canonical request snapshot，也不改
   Attempt、ArtifactSet activation 或 operational fold 的总体协议。

这条路线合理且可行。它删除的是 request materialization 的历史分叉，与 governing setup 的表示、
tool reservation、provider crash window 和 tail dependency collection 基本正交。最大的实际成本不是
production branch 本身，而是：

- 很多通用 recovery/failpoint 测试仍借 full-raw fixture 搭建；
- 当前 `SessionEventCodec` 对所有 kind 共用一个 body schema version；
- exact-shape manifest 删除字段属于真实 wire break。

因此“逐步实施”不应理解成逐字段反复破坏 wire，而应理解成：

```text
先迁 oracle / fixture
-> 停止 legacy writer
-> 建立 per-kind schema version 能力（bytes 不变）
-> 一次 Prepared v2 原子切换
-> 重建实验 journal 并做真实验收
```

## 1. 为什么 single coherent policy 成立

### 1.1 当前生产事实

当前 `SessionJournalEngine` 已经只有 coherent artifact-tail online writer；它会：

1. 从 completion boundary 解析 exact governing setup；
2. 找到 durable `ArtifactSetCommitted`；
3. 物化多个 exact artifact contribution；
4. 从 common anchor 到 boundary fold dependency-closed raw suffix；
5. 将 artifact snapshots、raw range、tools、setup 与 target 写入 Prepared；
6. Prepared 后即使 sidecar 被删除，也能从 committed manifest exact reopen。

`explicit-artifact-tail` 与 `full-raw` 都已没有 online writer，只剩 D6D 前
codec/reconstructor 的旧请求读取分支。coherent 路径覆盖 Observation completion、ToolResult
continuation、visible tools、Prepared reopen 与长冷前缀 bounded-read 验收。

### 1.2 ArtifactSet 是运行前置条件，不是可选优化

本计划接受一个明确的产品事实：

> 长寿命 session 的完整 raw history 不保证能装进 LLM context window；没有 coherent recap/artifact
> 时，不存在可靠的 online completion fallback。

所以删除 full-raw 后：

- fresh session；
- 仅完成 raw import、尚未生成/checkpoint artifact 的 session；
- rewind 到 ArtifactSet activation 之前的 branch；
- exact sidecar members 在 Prepared **之前**缺失；

都必须返回明确的 “ArtifactSet required / not ready” 结果或错误。不得：

- 自动 root-to-head `Project()`；
- 猜测使用 stale/旁支 ArtifactSet；
- 把“当前历史看起来还短”当成隐式 full-raw 许可；
- 在请求时临时调用 maintainer 改写 raw execution 状态。

这里约束的是 online orchestration，不是禁止构建尚未 ready 的 raw session：

- `SendAsync()` 必须在追加 Observation 前检查 readiness；
- `ResumeAsync()` 在已有 `AwaitingAgentAction` 但无可用 set 时，不追加 Prepared、不调用 provider；
- 低层 `AppendObservation()` 与 importer 仍允许写 raw，供离线 maintainer 后续生成 artifacts。

离线 maintainer 与 checkpoint 工作流负责建立 readiness：

```text
import raw
-> 运行 autobiographical-rewrite
-> 运行 world-understanding-rewrite
-> checkpoint exact coherent ArtifactSet
-> validate
-> online Send/Resume
```

这不会形成 SessionJournal online LLM 的循环依赖：`ChatSession.BacktestCli` maintainer 使用独立
Completion client 从 addressed raw replay 生成 sidecar artifact。但 fresh/empty session 如何产生首组
artifact 仍应作为上层 lifecycle 问题显式处理；本切片不发明 synthetic empty recap。

setup mutation 本身不必自动废弃已有 ArtifactSet：只要 common anchor 仍在 current lineage，tail fold
可以吸收 anchor 之后的 runtime/system-prompt setup 事件，而新 Prepared 会 pin boundary 上最新的两条
setup refs。必须保留 runtime-only、prompt-only mutation 后的 coherent materialization/reopen 测试。
`ArtifactSetCommitted.CurrentSetups` 只证明 activation Parent 当时的 governing setup，不要求等于未来
completion boundary 的 setup；后者由新 Prepared 的 paired setup refs 固定。

### 1.3 “single policy”不等于“永远选择 latest set”

single policy 定义的是唯一 request **形状**：

```text
one exact coherent ArtifactSet + one dependency-closed raw suffix
```

它不应把未来 planner 永久约束为“必须选择最近一次 activation”。当前 Engine 可以继续选择 active/latest
set，本计划也不修改 `ArtifactSetCommitted` wire；但 Prepared 必须保存 exact set reference，而不是只保存
“latest”断言。未来 planner 选择更早合法 set 时，不需要重新引入第二套 manifest policy。

### 1.4 coherent membership 的分层职责

`SessionJournal` 是通用执行与持久化底座，不应硬编码某个应用的 memory maintainer 名称。
core 只负责结构性 coherence：

- 至少两个唯一 role/member；
- member id 与 exact sidecar 一致；
- common anchor、coverage setup、lineage、target 与 contribution hash 一致；
- online completion 前所有 exact member 都可读且可验证。

`autobiography`、`world-understanding` 是当前 ChatSession 应用的最低上下文策略，应由上层
provisioning / planner 在提交前检查，并由应用层测试固定。其他应用可以使用不同 role vocabulary，
而不需要修改 SessionJournal core。Prepared 继续 exact assertion 实际被选中的整个 set，但不解释
role 的业务含义。

## 2. 本轮目标与非目标

### 2.1 目标

完成后应满足：

- `SessionRuntime` 不再暴露 request-context policy selector；
- online Engine 只有 coherent artifact-tail materializer；
- Prepared codec/reconstructor 只支持 coherent recipe；
- 旧 full-raw / explicit policy ids、aliases、writer、reader、fixtures 被删除；
- Prepared v2 不再携带空槽、固定常量副本和可由 authoritative field 推导的值；
- paired setup exact refs、artifact provenance、tool/dispatch identity 与 commitment 保持完整；
- online full-projection counters 保持为零；
- 旧实验 repo 通过显式重建而不是 compatibility layer 迁移。

### 2.2 明确非目标

本轮不做：

- 合并 `RuntimeConfigSetup` / `SystemPromptSetup`；
- 修改 `SessionGoverningSetupReferences`；
- 把 `CompletionRequestPrepared` 与首次 attempt 拆开；
- 删除或重塑 `CompletionAttemptRestarted`；
- 改变 `ArtifactSetCommitted` definition/activation/latest 语义；
- 引入 request snapshot-authoritative 存储；
- 合并 full reducer、suffix fold 与 reverse tail resolver；
- 自动运行 MemoryMaintainer；
- 删除 public `Project()` / `ReplayHistory()` 的 full audit semantics；
- 让 SessionJournal 依赖 `Agent.Core`。

后续 Coding Agent 不应借 manifest 化简顺手实施上述重构。

## 3. 必须保留的 correctness 信息

Prepared v2 仍是 exact reopen 与 side-effect routing 的 durable fact。至少保留：

| 类别 | 必须保留的信息 | 原因 |
| --- | --- | --- |
| Attempt | attempt id、correlation id、单一 reason | reopen、retry chain、completion boundary 验证 |
| Execution | last-issued tool execution sequence | durable tool reservation 延续 |
| Raw provenance | non-null raw start exclusive、raw range SHA-256 | dependency closure 与 addressed provenance |
| Artifact provenance | exact ArtifactSet ref、artifact id/kind/hash、inline context snapshot | coherence；Prepared 后 sidecar 删除仍可重建 |
| Setup | runtime config + system prompt exact refs（address/version/payload hash） | governing setup 真源 |
| Request parameters | model id、max tokens | canonical request recipe |
| Tools | definitions、tool-set SHA、runtime implementation/capability identity | model-visible contract 与执行安全 |
| Dispatch | connection identity、request-adapter fingerprint、client name、API spec | reopen 后只能路由到兼容 provider |
| Commitment | canonical byte length + SHA-256 | 验证 reconstructed request exact |
| Recipe version | 一个能够固定 coherent renderer 语义的 version id | 避免新代码静默重解释旧 Prepared |
| Request codec | canonical request codec id | 固定 commitment 所对应的 byte encoding |

Prepared event header `Parent` 继续是 authoritative completion boundary，不在 body 中复制。

## 4. Prepared v2 的瘦身边界

### 4.1 删除项

以下字段可在 v2 一次删除：

| 当前字段/类型 | 删除理由 | 替代真源 |
| --- | --- | --- |
| `Attempt.ReplacesAttemptId` | Prepared 中被强制为 null | retry 的 `CompletionAttemptRestartedBody.ReplacesAttemptId` |
| `Plan.SelectionPolicyId` | 只剩一个合法 request shape | v2 schema + coherent recipe id |
| `Plan.PlannerFingerprint` | 当前只是 policy 常量，没有真实 planner producer | 将来真实 SelectionAudit |
| `Plan.RecalledInputs` / `SessionRequestRecalledInput` | writer 永远写空，codec 强制空 | 将来真实 SelectionAudit |
| `Plan.RenderingProfileId` | 与 renderer/policy 固定绑定 | coherent recipe id |
| `Plan.ModelProfileId` | 恒等于 `Parameters.ModelId` | `Parameters.ModelId` |
| `Plan.EstimatedInputTokens` | byteLength/4 伪估算且无 consumer | 将来真实 budget audit |
| `Plan.Reason` | 恒等于 `Attempt.Reason` | `Attempt.Reason` |
| `Rendering.ContextRendererId/Fingerprint` | 多 policy identity tuple 的组成部分 | 一个 versioned coherent recipe id |
| `Rendering.ToolCodecId` | 恒等于 `ToolSet.CodecId` | `ToolSet.CodecId` |
| `Rendering.ReasoningCodecSetFingerprint` | 当前只是 canonical codec 的固定子版本 | canonical request codec 合同 |
| `Target.CompletionSurfaceId` | 恒等于 pinned RuntimeConfigSetup | exact runtime setup ref |
| `Commitment.Algorithm` | 唯一合法值是 SHA-256 | v2 字段名/codec 固定算法 |

本计划推荐把 `SessionRequestRendering` 明确收成一个小的 recipe/codec 描述（具体 record 名可在
实现时定稿，但语义字段只保留这两个）：

```text
recipeId
canonicalRequestCodecId
```

不要保留旧五元 policy/renderer tuple，也不要让 renderer identity 完全消失。

两者的 version ownership 必须明确：

- `recipeId` 负责 artifact contribution 的组合顺序、role 到 system/observation/action carrier 的映射、
  suffix context 的拼接规则；
- `canonicalRequestCodecId` 负责最终 `CompletionRequest` 的 JSON bytes，包括 reasoning blocks、
  Action/ToolResult content shape 与 inline tool definitions。

任一语义变化都必须 bump 对应 identity（并在本项目不保留旧 reader 时触发显式 wire/rebuild
决策），不能只改当前实现代码。

### 4.2 暂时保留项

以下字段即使存在一定重复，本轮也不删：

- `Parameters.ModelId` / `MaxTokens`；
- `ToolSet.CodecId`、`Sha256`、definitions、runtime identity；
- `Commitment.ByteLength` / `Sha256`；
- `Target.Connection`、`ClientName`、`ApiSpecId`；
- paired setup refs；
- artifact input 的 kind/hash/snapshot；
- active ArtifactSet exact ref；
- raw range/hash；
- execution checkpoint。

这里采用“先删明显无价值的字段，不顺手重写 authority model”的原则。尤其不能因为整体 request
commitment 已存在，就顺手删掉 tool runtime identity 或 inline artifact snapshot。

### 4.3 Schema version 规则

当前 `SessionEventCodec` 用一个全局 `BodySchemaVersion = 1` 验证所有 event kind。Prepared 字段变化不能：

- 在仍标记 `v:1` 时偷偷改变 exact body shape；
- 为了 Prepared 一个 kind 把所有旧 setup/action/tool event 一起 bump 到 v2；
- 同时接受 v1/v2 并用缺省值猜测旧字段。

应先建立 per-kind expected body version：

```text
RuntimeConfigSetup              v1
SystemPromptSetup               v1
...
ArtifactSetCommitted            v1
CompletionRequestPrepared       v2
```

行为保持切片先让所有 kind 仍写/读 v1并证明 bytes 不变；正式 cutover 时只把
`CompletionRequestPrepared` 切到 v2。当前产品不保留 Prepared v1 decoder；遇到旧 Prepared 应明确报告
unsupported schema，而不是普通 corruption 或 full replay fallback。

## 5. 可逐步实施的工作包

这些工作包按依赖顺序调度。每个包都应独立 review、验证并提交；只有 **D6D** 是 wire cutover。

### CS-3D6A：Scope freeze 与 readiness contract

**目标**

- 把本计划的范围写成 tests/contract；
- 明确没有 ArtifactSet 时的行为；
- 清理最容易误导新代码的 full-raw aliases。

**实施内容**

1. 冻结以下非目标：paired setup、Attempt、ArtifactSet wire 均不改。
2. 为 `SendAsync` / completion routing 增加或收紧 readiness 测试：
   - 无 activation 时在 provider 调用、full projection 和 raw append 前 fail-fast；
     这里的 raw append 指 `SendAsync` 自己的 Observation；低层 append/import 不受禁止；
   - `ResumeAsync` 在已有 AwaitingAgentAction 且无 set 时不 append Prepared；
   - rewind 到 activation 之前同样 fail-fast；
   - Prepared 前 member 丢失 fail-fast；
   - setup mutation 后仍能用 anchor-to-boundary suffix 得到最新 governing setup。
3. 增加 public typed not-ready exception/reason code，并在 `SendAsync` 与
   AwaitingAgentAction completion routing 复用同一 preflight：
   - current lineage 上不存在 activation 时报告 `ActiveArtifactSetRequired`；
   - exact sidecar member 缺失或不可用时报告 member unavailable；
   - sidecar 的 id/kind/target/common anchor/coverage setup/contribution hash 与 activation
     不一致时报告 member mismatch；
   - raw codec、Parent、lineage 与 setup reference corruption 继续走原有 corruption
     exception，不伪装成 readiness。
4. `SessionJournal` core 不检查 `autobiography` / `world-understanding` 等应用 role；
   required-role policy 由 ChatSession provisioning / planner 负责。
5. 删除仅指向 full-raw 的模糊 aliases：
   - `SessionRequestManifestDefaults.SelectionPolicyId`
   - `PlannerFingerprint`
   - `RenderingProfileId`
   - `ContextRendererId`
   - `ContextRendererFingerprint`
6. 将少量仍使用 alias 的测试改成显式 current policy constant。

**fresh-session gate**

如果产品要求空白新 session 立刻对话，调度者必须在 D6C 前另开一个 lifecycle slice，提供由上层显式
调用的 deterministic coherent genesis/provisioning：

- 在合法 anchor 上创建 autobiography/world-understanding 初始 artifacts；
- commit exact ArtifactSet；
- derived 写失败不得污染 raw execution chain。

若当前产品只从已有历史/import 启动，则可以暂不实现 genesis，但必须接受
`ActiveArtifactSetRequired` 是正式 readiness reason，不能用 full-raw 恢复可用性。

**验收**

- 不改变任何 committed event bytes；
- 无 ArtifactSet 的错误发生前 `FullProjectionInvocationCount == 0`；
- setup mutation 用例通过；
- production 不再存在会默认解析成 full-raw 的 alias。

### CS-3D6B：Coherent test foundation

**目标**

在删除 legacy production 分支之前，把通用执行 oracle 从 full-raw fixture 迁到 coherent fixture。

**实施内容**

1. 建立共享 test helper：

   ```text
   Create session
   -> write two exact common-anchor artifacts
   -> CommitArtifactSetAsync
   -> return coherent runtime + artifact identities
   ```

2. 优先迁移以下通用测试：
   - Engine send/resume；
   - Prepared failpoints 与 restart；
   - provider failure；
   - no-tools / visible-tools；
   - single/multi-tool continuation；
   - runtime implementation/capability mismatch；
   - branch/rewind；
   - full reducer vs tail resolver differential。
3. 保留独立 oracle：
   - expected execution state 不由被测 manifest validator 自己生成；
   - public `Project()` 继续作为 full audit oracle；
   - malformed fixtures 继续能单字段变异。
4. legacy-specific test 明确标记，避免与待保留的通用 crash/recovery coverage 一起删除。

**关键测试文件**

- `tests/SessionJournal.Tests/SessionJournalEngineTests.cs`
- `tests/SessionJournal.Tests/SessionPreparedCompletionRecoveryEngineTests.cs`
- `tests/SessionJournal.Tests/SessionExecutionRecoveryContractTests.cs`
- `tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs`
- `tests/SessionJournal.Tests/SessionCompletionAttemptRestartedTests.cs`
- `tests/SessionJournal.Tests/SessionPreparedRequestReconstructorTests.cs`

**验收**

- 通用 engine/recovery tests 不再创建 `LegacyFullRaw` runtime；
- coherent tests 覆盖 Observation 与 ToolResult boundary；
- Prepared 后删除 sidecar仍 exact reopen；
- failpoint、CAS 与 operation id/sequence 断言保持；
- 1 vs 10001 cold prefix diagnostics 不退化。

**实施记录（2026-07-27）**

- 新增共享 `CoherentArtifactSetTestFixture`：只负责在当前 replay-safe idle head 写入两个 exact
  sidecar artifacts、提交 `ArtifactSetCommitted`，以及从 provider request 中区分固定 artifact
  prefix 与待断言 raw suffix；它不生成 execution-state oracle、Prepared commitment 或预期 manifest。
- `SessionJournalEngineTests` 的 online send/resume、tool loop、provider failure 与 failpoint 场景已改为
  coherent activation；online request 断言显式跳过两个 artifact contribution 后检查 dependency-closed
  raw suffix，且 `FullProjectionInvocationCount` 在请求期间保持不变。
- `SessionPreparedCompletionRecoveryEngineTests` 的通用 Prepared/restart/dispatch/tool identity 场景不再
  借 `CreateFullRawPreparedAsync` 建立状态；Prepared 后删除 sidecar 的 exact reopen 覆盖继续保留。
- `SessionExecutionRecoveryContractTests` 的 runtime-driven Prepared refusal diagnostics 已改用 coherent
  activation。仅服务于 reducer/tail-resolver 独立 oracle 的 hand-built full-raw manifest body 暂留，
  与 legacy codec/reconstructor fixtures 一并交由 D6D 原子 wire cutover 清理。
- 本包只修改测试与本计划文档；production、event schema 与 canonical bytes 均未改变。

### CS-3D6C：停止 live full-raw，并建立 per-kind schema 基础

这是两个互相独立、均为行为保持/单向收口的小切口，可以分成两个 commit。

#### D6C1：online writer coherent-only

删除：

- `SessionRequestContextPolicy.LegacyFullRaw`；
- 单选项已无意义的 `SessionRequestContextPolicy` enum 与 `SessionRuntime.RequestContextPolicy`；
- `CompleteAwaitingAgentActionAsync()` 的 full-raw routing；
- `MaterializeFullRawRequestContext()`；
- `FullRawRequestContext`；
- Engine 的 full-raw materialization identity plumbing。

同一 commit 删除或迁移所有依赖 `SessionRequestContextPolicy` / live full-raw writer 的测试；不能把编译
清理留到 D6E。已经在 D6B 迁为 coherent 的通用测试继续保留。

暂时保留旧 full-raw/explicit decoder 和 reconstructor，仅作为 D6D 前的过渡只读能力；任何新 writer
均不得产生它们。这个过渡状态只持续到 D6D wire cutover，文档不得把它描述为长期 compatibility
承诺；D6C2 只建立 per-kind schema plumbing，不扩大或续期 legacy reader。

保留：

- public `Project()` / `ReplayHistory()`；
- `SessionReducer` full semantics；
- OfflineValidator 的 full oracle；
- full replay CLI/maintainer 能力。

**D6C1 实施记录（2026-07-27）**

- `SessionRuntime` 已删除 request-context policy selector；online `SendAsync()` 与
  AwaitingAgentAction completion routing 现在无条件走 active ArtifactSet readiness 与 coherent
  artifact-tail materialization。
- live full-raw writer、`MaterializeFullRawRequestContext()`、其 request-context record 和仅服务于
  该 writer 的 raw-range hasher 已删除；public `Project()` / `ReplayHistory()` 不变。
- 通用 Prepared recovery、offline historical Prepared corruption 与 failed-boundary causality
  fixtures 已改为现场提交 coherent activation；Prepared exact reopen 仍只依赖 committed manifest
  的 inline contribution，Prepared 后删除 sidecar 继续可恢复。
- D6D 前暂留 full-raw / explicit policy constants、codec 与 reconstructor reader branches，明确只是
  historical Prepared 的过渡只读面；本切片未改 event schema、Prepared shape 或 canonical bytes。
- 验证证据：相关 recovery/offline/tail focused tests `45/45`、`SessionJournal.Tests`
  `206/206`、`ChatSession.BacktestCli.Tests` `35/35`；Backtest CLI build 为 0 warning。

#### D6C2：per-kind body schema plumbing

重构 `SessionEventCodec`：

- encode/decode 根据 `SessionEventKind` 取得 expected body version；
- 初始状态下所有 kind 仍为 v1；
- canonical bytes/goldens 完全不变；
- unsupported version 错误包含 kind、actual、expected。

D6D 将只修改 `CompletionRequestPrepared` 的 expected version。

**验收**

- 全量 `SessionJournal.Tests` 通过；
- 当前所有 event canonical bytes不变；
- online request path 不再有 `Project()` materialization 分支；
- production code 已不能写 full-raw，但旧 reader 尚可在本包内通过原有 golden。

### CS-3D6D：Prepared v2 原子 cutover

**目标**

通过一次明确的 wire break，同时完成 single-policy reader 与 manifest 瘦身。这个包不能拆成多个会被
长期保留的中间 wire。

**同一切换中必须完成**

1. `CompletionRequestPrepared` body schema 从 v1 切到 v2；其他 event kind 仍为 v1。
2. 删除：
   - full-raw / explicit / coherent 三套 selection policy ids 与 fingerprints；
   - full-raw / explicit codec validation branches；
   - `ReconstructFullRaw()`；
   - `ReconstructExplicitArtifactTail()`；
   - 所有旧 defaults alias；
   - §4.1 列出的 manifest 冗余字段与类型。
   与这些类型/字段绑定的 legacy codec/reconstructor tests、hand-built manifest helpers 必须在同一
   cutover 中删除或改成 “Prepared v1 unsupported” 测试，保证本提交自身可编译、可验证。
3. 将 coherent validation 从 switch 分支提升为 Prepared v2 的唯一合同：
   - `RawStartExclusive` 非 null；
   - exact active ArtifactSet ref 非 null；
   - required artifact roles/member assertions；
   - inline contributions 合法且 exact hashes 匹配；
   - raw range/setup/tool/target/commitment 继续严格验证。
4. 保留一个 versioned coherent `recipeId` 和 canonical request codec id；删除五元
   policy/planner/renderer tuple。
5. 将所有 consumer 一次改到 v2：
   - Engine writer；
   - manifest codec；
   - reconstructor；
   - full reducer；
   - tail resolver；
   - suffix fold；
   - offline validator。
6. 明确拒绝 Prepared v1；不双写、不双读、不用缺省字段补旧 body。

**特别注意**

- `CompletionAttemptRestarted` 及其 `ReplacesAttemptId` 保留；
- Prepared 仅删除自己永远为 null 的 replacement；
- `Plan.Reason` 删除后，所有 boundary validation 统一读取 `Attempt.Reason`；
- `Target.CompletionSurfaceId` 删除后，仍从 exact RuntimeConfigSetup 校验 runtime surface；
- active ArtifactSet 与 inline artifact snapshots 都必须保留；
- 不把 current/latest selection 规则写入 recipe id。

**验收**

- canonical Prepared v2 golden；
- 包含 opaque reasoning payload、tool call 与 ToolResult 的 exact-reopen golden；
- strict decode 拒绝 unknown/missing property；
- Prepared v1 报 unsupported schema；
- policy strings 在 production 与 current-wire tests 中归零；
- reconstructed canonical request commitment exact；
- Prepared 后删除 sidecar仍 exact reopen；
- tool definitions/runtime identity、target identity mismatch 仍 fail-fast；
- reducer/tail resolver 对所有 durable heads 状态一致。

### CS-3D6E：Legacy fixture 与验证收口

**目标**

删除 wire cutover 后不再有价值的测试/文档表面，并用真实 repo 工作流证明没有依赖 legacy request
policy。

**实施内容**

1. 确认 D6C/D6D 已同步删除或迁移所有无法编译的 legacy-only tests/helpers；D6E 只清理残余名称、
   archived docs 与不再引用的测试数据，不推迟前序包的编译修复。
2. 保留已经迁到 coherent fixture 的通用：
   - restart/failpoint；
   - malformed event；
   - full-vs-tail differential；
   - performance diagnostics；
   - offline read-only validation。
3. 更新：
   - `tail-execution-recovery-design.md`
   - `session-configuration-access-notes.md`
   - `session-journal-trunk-design.md`
   - `ChatSession.BacktestCli/README.md`
   - 本计划状态与完成证据。
4. 搜索并清零陈旧名称：

   ```text
   LegacyFullRaw
   full-raw
   explicit-artifact-tail
   RequireActiveArtifactSet
   SessionRequestContextPolicy
   FullRaw*
   ExplicitArtifactTail*
   ```

文档中的历史说明可保留，但必须明确标记为 archived history，不能描述为当前可调用路径。

## 6. 真实迁移与验收流程

旧 legacy upgrade export 不含新式 Prepared；`import-session-journal` 只生成 setup、SessionCreated、
Observation 与 ImportedAction，因此不依赖被删除的 request policy。正式验收使用一个全新 repo：

```text
1. import-session-journal
2. validate-session-journal
   -> needs-artifact-set-checkpoint
3. replay-rolling-summary-session-journal
   -> autobiographical-rewrite
4. replay-rolling-summary-session-journal
   -> world-understanding-rewrite
5. checkpoint-artifact-set-session-journal
   -> autobiography=<id>
   -> world-understanding=<id>
6. validate-session-journal
   -> active-coherent
7. 用 SessionJournalEngine 执行 Observation completion
8. 执行至少一次 multi-tool continuation / reopen
```

关键输入：

- legacy export：
  `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json`
- real Completion connection：
  `prototypes/Galatea/.atelia/galatea/connections.json` 中的 `dsv4p`
- CLI 文档：
  `prototypes/ChatSession.BacktestCli/README.md`

旧 SessionJournal 若已经包含 full-raw/explicit/Prepared-v1：

- 不原地改写 immutable events；
- 不通过默认字段猜测；
- 可替换实验数据直接重新 import + regenerate artifacts；
- 若数据包含不可重复的真实外部 side effect，先 inventory 并人工决定保留/归档，不能自动宣称可丢弃。

## 7. 每包共同验收闸门

- SessionJournal 不依赖 `prototypes/Agent.Core`；
- raw Parent lineage 仍是 correctness source；
- paired setup refs 不变且仍 header-walk authoritative；
- online `ChronologicalChainReadCount == 0`；
- online `FullProjectionInvocationCount == 0`；
- 1 vs 10001 cold prefix 不增加 online payload reads/decoded bytes；
- Prepared 后 sidecar 删除仍 exact reopen；
- Prepared 前缺 artifact/member 明确 fail-fast；
- branch/rewind 不复用 divergent ArtifactSet；
- tool operation id、sequence、implementation/capability identity 稳定；
- provider/tool failpoint 与 exact-head CAS 行为不退化；
- offline validator 保持 strict、read-only；
- public full audit APIs 继续工作；
- production 没有 silent full replay 或 legacy policy fallback。

## 8. Coding Agent 调度建议

每个工作包都按：

```text
再审视
-> 定稿本包数据形状/删除边界
-> 实施与 focused tests
-> reviewer 反证
-> 尾修
-> 独立 commit
```

推荐调度顺序：

1. D6A：合同与 readiness；
2. D6B：测试迁移；
3. D6C1：停 legacy writer；
4. D6C2：per-kind version，bytes 不变；
5. D6D：唯一一次 Prepared wire cut；
6. D6E：真实迁移、全量验收与文档收口。

不建议并行实施 D6B 与 D6D：二者会同时大量修改 hand-built manifest fixtures。D6C1 与 D6C2
职责独立，但为了避免共享 `SessionJournalEngine` / codec 测试冲突，也以串行小 commit 更易 review。

## 9. 完成定义

当且仅当以下条件同时满足，CS-3D6 才算完成：

1. runtime、writer、codec、reconstructor 只有 coherent artifact-tail request shape；
2. Prepared v2 比 v1 显著更瘦，且每个保留字段都有 execution/reopen/provenance consumer；
3. 不存在旧 policy alias、compat decoder、默认值推断或 root replay fallback；
4. 双 setup stream 保持原样；
5. 通用 recovery 覆盖已经迁到 coherent fixtures；
6. real import → 两 maintainer → checkpoint → validate → online completion 流程通过；
7. long-prefix bounded-read 与 sidecar-deletion exact-reopen 验收通过；
8. 文档明确 ArtifactSet readiness 是 online completion 的正式前置条件。

本计划完成后，才重新评估 Attempt 对称化、ArtifactSet selection/activation 解耦、request snapshot 与共享
operational kernel。它们不应阻塞本次低成本 legacy 收口。

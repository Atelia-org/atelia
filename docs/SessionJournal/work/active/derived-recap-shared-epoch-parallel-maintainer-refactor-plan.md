# DerivedRecap Shared Epoch / Maintainer Family 并行重构计划

状态：Active / Implementation in progress  
Context baseline：`6a80afdda7f12d6c9ce432066dfb51d1b3326762`  
目标阶段：首次发布前 direct-cut refactor；现有 DerivedRecap sidecar 将由 operator reset 后从 raw history rebuild  
本文性质：Shape / Rule / Plan authority candidate，不描述 current implementation 已经具备的行为

### Implementation progress

| Package | State | Evidence |
|---|---|---|
| R0 | Complete | fresh inventory at `359bdfc0`; related Completion/Maintainers/Planner/Store/Galatea test projects built with 0 warnings/errors; Completion 438/438 and Maintainers 29/29 baseline passed |
| R1 | Complete | `6d273515` + tail fix `e846ea14`; immutable typed prefix/output/tail, provider projection, call-log v6 and Prepared fail-closed boundary; independent tail review found no P0/P1 |
| R2A | Next | introduce DerivedRecap Abstractions and family/member/output-protocol single source |
| R2B–R7 | Pending | follow the package gates in §11; do not infer implementation from this target document |

R1 deterministic evidence: Completion 456 non-live tests, SessionJournal 409, Agent.Core 117, Galatea 94 passed / 4
environment-skipped, solution build 0 warnings/errors. The existing full CLI baseline still has 16 recap fakes that reject the
already-production `NoReuseExpected` options overload and one stale connection-fingerprint golden; these were independently
shown not to originate in R1 and remain visible gates for the R2/R6 test migration rather than accepted final-state failures.

## 1. 新会话快速入口

新会话、上下文压缩后的继续执行，或 subagent 接手某个工作包时，按顺序读取：

1. 本文；
2. [`docs/SessionJournal/README.md`](../../README.md) 与
   [`current architecture and code map`](../../current/architecture-and-code-map.md)；
3. [`current DerivedRecap concepts`](../../current/derived-recap/concepts.md)，只用于识别重构前事实；
4. [`Planner README`](../../../../prototypes/SessionJournal.DerivedRecap.Planner/README.md) 与
   [`Maintainers README`](../../../../prototypes/SessionJournal.DerivedRecap.Maintainers/README.md)；
5. 本文对应工作包列出的 owning code/tests。

开始实施前重新记录：

```bash
git status --short
git rev-parse HEAD
```

本文 baseline 只认证撰写时的 checkout。以后不得把本文的 current-code 描述、schema 号、测试数或文件行号
当作新 HEAD 的事实；应重新核对 code/tests。本文采用的目标不变量则应持续有效，除非开发者显式修改本计划。

## 2. 用户已经锁定的目标

以下不是待选候选，而是本轮重构的设计输入：

1. **多 recap maintainer 并行是 DerivedRecap 的核心目标。** 当前 production 全局串行是历史积累造成的
   偏离，不应为了迁就现状而保留。
2. **一次 recap build 是一次共享蒸馏。** 所有本轮参与者面对同一个 previous recap pack 和同一份
   即将滑出 online context 的 recent-history slab；多个 maintainer 是对同一输入的分治，而不是各自拥有
   独立 replay window 的 producer。
3. **同一 family 的公共 prompt prefix 必须由类型结构保证。** family 成员不能分别配置自己的
   system prompt 或 tool schema；它们必须引用同一个 immutable `{system prompt + ordered tools + output
   protocol}` aggregate instance。
4. **任务差异只存在于 member-specific 尾部 instruction。** 当前两个 built-in maintainer 只是机制占位；
   本计划不设计它们最终维护哪些信息，也不对实际 prompt 内容做质量优化。
5. **connection/model 是 Host-owned runtime supply。** 它参与一次运行的 dispatch/cache domain，不进入 raw
   events、Store manifest、maintainer durable identity 或 capability fingerprint。
6. **同 cache group 采用 `first-real then parallel followers`。** leader 完整返回后再释放 followers；不追求
   response-start cache-ready，不采用通用 `max_tokens=0` warm-up。
7. **现有 recap 无需兼容。** 实施后执行显式 reset/rebuild；不保留旧 sidecar schema reader、旧 prompt
   descriptor 或迁移兼容层。
8. **必要时允许大幅重写外围基础设施。** 评价标准是目标机制是否清晰、可恢复、可测试，而不是最少改动
   当前代码。

## 3. 当前 production 与目标的差距

在 baseline 上已经确认：

- `DerivedRecapBuildingExecutor` 按 frozen manifest block 顺序逐个 `await EnsureBlockAsync`；
- `DerivedRecapRestoreExecutor` 按 prepared action 顺序逐个执行；
- 每个 `MaintainRecapBlockPlan` 拥有自己的 source cursor、`CatchUpBoundaries[]` 和 materialized windows；
- 当前 `Inherit` exact-copy frozen input，同时保持 per-block `AbsorbedThrough`，后续该 block 可以从旧 cursor
  单独 catch up；
- `RecapMaintainerStepRunner` 每个 step 新建一份 `RecentHistorySlice`，neutral request 仍携带 `OldBlock`；
- current rewrite 不读取 `OldBlock`，而是读取 manifest-root frozen `PriorContext`；
- `RewriteRecapBlockMaintainer` 把 profile-owned `SystemPrompt` 与 `UserPrompt` 拼入请求，`Tools = []`，并固定
  `PromptCacheReuseHint.NoReuseExpected`；
- `IRecapBlockMaintainerRegistry` 只返回 neutral maintainer，Galatea composition 中已知的 connection/model
  runtime binding 在进入 Planner 前被擦除；
- `RecapMaintenanceOrchestrator.Task.WhenAll` 仍存在，但只由 legacy/unit-test 路径使用，不是 production
  Building/Restore scheduler；
- `RecapMaintainerProfileDescriptor` 的 prompt fingerprint 覆盖 member 自己的 system+user 文本，没有
  family/tool contract；
- Completion result/call log 尚未提供结构化 cache-read/cache-write token telemetry。

这不是只缺少 `Task.WhenAll`。当前 per-block cursor/catch-up、profile-owned system prompt、registry 形状、
execution failure semantics 和 cache boundary 都与目标模型不一致。

### 3.1 Baseline owning-code map

| Concern | Current owner | Focused tests |
|---|---|---|
| neutral request / maintainer contract | `prototypes/SessionJournal/SessionContextAndRecapContracts.cs` | `SessionRecapContextContractsTests.cs` |
| durable plan/input/final/publication types | `prototypes/SessionJournal.DerivedRecap.Store/DerivedRecapContracts.cs` | `DerivedRecapAuthorityBoundaryTests.cs` |
| strict sidecar codec | `DerivedRecap.Store/DerivedRecapCodec.cs` | `DerivedRecapCodecTests.cs` |
| Store install/final/publish/restore | `DerivedRecapBuildingInstaller.cs`、`DerivedRecapStore.cs`、`DerivedRecapPublisher.cs`、`DerivedRecapRestorer.cs` | Store authority/publisher/restore/crash suites |
| policy/evaluator/shared raw planning window | `BoundedMaintainAllRecapPlanningPolicy.cs`、`RecapPlanEvaluator.cs`、`RecapPendingWindowPreparer.cs` | corresponding Planner tests |
| Building execution | `DerivedRecapPlannerExecutor.cs`、`RecapMaintainerStepRunner.cs` | `DerivedRecapPlannerExecutorTests.cs` |
| Published Restore execution | `DerivedRecapRestoreExecutor.cs` | `DerivedRecapRestoreExecutorTests.cs` |
| runtime registry | `DerivedRecapExecutionContracts.cs` | Planner registry/operation tests |
| concrete family/profile/rewrite placeholder | `RecapMaintainerProfileCatalog.cs`、`RewriteRecapBlockMaintainer.cs` | `MaintainerIdentityTests.cs`、rewrite tests |
| legacy parallel helper | `RecapMaintenanceOrchestrator.cs` | `SessionRecapContextContractsTests.cs` |
| Completion request/cache hint/result | `Completion.Abstractions/CompletionRequest.cs`、`CompletionInvocationOptions.cs`、`CompletionResult.cs` | `Completion.Tests` provider acceptance/wire tests |
| provider prefix projection | `Completion/Anthropic/AnthropicMessageConverter.cs` 与 OpenAI/Gemini converters | provider request serialization tests |
| Galatea runtime routing | `Galatea/GalateaRecapComposition.cs` | `GalateaRecapCompositionRoutingTests.cs` |
| CLI runtime routing | `SessionJournal.Cli/RecapCliComposition.cs`、`RecapExecutionCommands.cs` | CLI composition/E2E tests |

工作包开始时必须从 `rg` fresh inventory 重新生成此表的实际 symbol/call-site 列表；本表只是 baseline 路由，
不是未来 API 约束。

## 4. 目标心智模型

```text
raw append-only history + previous Published RecapPack
                         |
                         v
              freeze one RecapDistillationEpoch
              - one PriorRecapPack snapshot
              - one exact RecentHistorySlab
              - one target AdmissionAnchor
                         |
                         |
              complete roster of Maintain work items
                         |
                 resolve runtime bindings
                                      |
                          group by reference identity:
                    (ExecutionLane instance, Family instance)
                                      |
                         leaders across groups in parallel
                                      |
                       each leader settles completely
                                      |
                         its followers run in parallel
                                      |
                        validate and persist each result
                                      |
                         publish only the complete set
```

所有 `Maintain` request 的公共部分严格是：

```text
Family.SystemPrompt
Family.OutputProtocol.RequestContract  // ordered tools + tool-choice/output constraints
Epoch.PriorRecapContext
Epoch.RecentHistorySlab
------------------------------ explicit cache boundary
Member.Target + Member.TaskInstruction
```

同一 family 的成员没有 API 可以替换前四项中的 family-owned 部分；同一 build 的成员也没有 API 可以替换
epoch-owned prior/history。

## 5. 不可协商的不变量

### 5.1 Shared Epoch

每个 Building 只冻结一个 `RecapDistillationEpoch`：

- `ReplayStartExclusive`；
- `SetAdmissionAnchor` / slab end；
- exact governing setup references / bounded lineage proof；
- previous Published set identity，首轮则显式 Empty；
- set-level frozen prior recap context；
- 可从 raw append-only lineage 精确重建的同一 ordered history slab。

执行时只 materialize 一次 immutable `RecapMaintenanceEpochInput`。本轮所有 remote calls 必须引用同一个
epoch object；不得为不同 block 重切、扩张、过滤或重新 materialize history。

正常增量状态必须满足：

```text
previousSet.SetAdmissionAnchor == epoch.ReplayStartExclusive
everyPublishedBlock.CoveredThrough == epoch.SetAdmissionAnchor
```

第一轮从显式 bootstrap start 开始。若 backlog 大于单次安全 slab，Planner 应生成并依次发布多个 shared
epochs，而不是在一个 set 内给不同 block 设计不同 catch-up route。

一个 frozen Building 严格只代表一个 epoch。backlog catch-up 由 lifecycle/CLI 依次执行多个独立 Building，
每个 epoch 完整 publish 后才规划下一个，所以下一轮总能把刚发布的完整 pack 当作 prior。不得把多个 epochs
塞入一个 manifest，避免恢复时同时存在多个 prior、多个 publication fence 或跨 epoch partial output。
online Host 可使用有界 epoch loop；operator reset/rebuild 则循环到 `NoBuild`。两者都要有明确的
`MaxEpochsPerOperation` / `MaxMaintainerCallsPerOperation` hard caps与typed `MoreWorkPending`，不能
无界追赶。这两个operation caps与per-epoch roster cap分别计数，每个新epoch安装/发起前用checked
arithmetic验证剩余预算；预算不足时不发起该epoch的任何remote call。
config必须满足`0 < MaxEpochsPerOperation`和
`0 < MaxMaintainerCallsPerEpoch <= MaxMaintainerCallsPerOperation`，且active roster不超过per-epoch cap，
保证一次operation至少能完成一个完整pending epoch。若首个epoch就无法放入
operation budget，preflight返回typed configuration/limit failure，不得返回会无限重试的
`MoreWorkPending`；后者只表示本operation已取得durable进展后仍有backlog。

这里不能复用current one-shot bounded head-to-oldest authority假装支持任意backlog：baseline与raw head之间的
总growth超过`MaxRawGrowthEventCount`时，current preparer会在policy前拒绝，而且bounded prefix无法定位最老
待处理slab。目标明确分成两条authority路径：

- normal online path只处理当前bounded proof可覆盖的一个或少量epochs；超界时typed返回
  `FullRebuildRequired`，不得fallback full scan；
- explicit reset/rebuild path取得独立`RecapFullRebuildAuthority`，在exact selected RefId/Parent lineage上做
  有界分页的完整审计，生成可删除的forward epoch spool/index，再从最老slab开始逐epoch install/publish。

rebuild spool只是当前raw lineage的execution aid，不是新事实源；每个epoch仍要冻结自己的raw range/setup
commitments并过raw-head/lineage fence。新会话实施R3A前必须先设计该authority的生命周期、分页proof、crash
resume与raw-head变化行为；不能只在CLI里写一个while loop绕开Planner safety gate。

### 5.2 `NoBuild` 与 identity maintenance 严格分开

目标模型删除旧 `Inherit` 以及任何 per-block “本轮不调用” planning mode：

- `NoBuild`：本轮不创建epoch，不调用任何maintainer；
- `Build`：冻结一个shared epoch，并为完整active roster中的每个member安排一个logical invocation；
- `KeepUnchanged`：maintainer已经看过本轮shared prior+slab，显式返回无编辑/恒等维护；
- `Updated`：maintainer已经看过同一输入并返回新正文。

因此“有些block较少变化”只是实际结果中更常出现`KeepUnchanged`，不是不同调用频率、lagging cursor或
per-block skip policy。唯一合法的不调用情形是whole-set `NoBuild`，以及Resume/Restore发现该block已有exact
healthy final后的恢复复用；后者不是planning语义。

### 5.3 Maintainer 返回 union

remote maintainer 的合法**成功**结果是 closed union：

```text
Updated(new block content)
KeepUnchanged
```

transport/provider/termination/output-validation failure是executor的typed failure，不伪装成模型可返回的第三种
成功outcome；caller cancellation继续通过独立control flow传播。

`KeepUnchanged` 表示 maintainer 确实看过本轮 prior+slab，决定正文不变；Planner 从 frozen previous input
复制正文并把 coverage 推进到 epoch admission。首轮或 target 尚不存在时不能 `KeepUnchanged`。

neutral `RecapBlockMaintenanceRequest` 删除 `OldBlock`。旧状态只有一个真源：shared epoch 中的完整 frozen
prior recap pack。这样当前 block 不会在 prompt 中重复出现，所有 members 同时看到 peers 的上一轮状态。

### 5.4 Family 是共享对象，不是相同字符串约定

候选形状：

```csharp
public sealed class RecapMaintainerFamilyDefinition {
    public string SystemPrompt { get; }
    public RecapMaintainerOutputProtocol OutputProtocol { get; }
    public string SemanticFingerprint { get; }
}

public abstract class RecapMaintainerOutputProtocol {
    public CompletionOutputContract RequestContract { get; }
    public string SemanticFingerprint { get; }
    public abstract RecapMaintenanceSuccess ParseAndValidate(
        CompletionResult result);
}

public sealed class RecapMaintainerDefinition {
    public string MaintainerId { get; }
    public ContextHeaderBlockPath Target { get; }
    public RecapMaintainerFamilyDefinition Family { get; }
    public string TaskInstruction { get; }
    public string CapabilityFingerprint { get; }
}
```

约束：

- member constructor 没有 `systemPrompt` / `tools` 参数，也没有 override；
- family aggregate 完全 immutable，tools 使用 frozen ordered collection；
- shared `OutputProtocol`是`CompletionOutputContract`（ordered tools + tool-choice/output constraints）、
  result parser/validator和protocol fingerprint的唯一owner；family只转发它，member不得私有
  request constraint、解析或替换parser；
- catalog 只注册 member definition，member 必须持有 family object；
- 一次 process/composition 中，同一 family 的所有 members 必须 `ReferenceEquals(member.Family, family)`；
- catalog 拒绝“不同 family objects 却拥有相同 semantic fingerprint”，防止不小心复制出两个逻辑相同但
  不能共享调度/cache 的实例；
- runtime scheduler 按 family **对象引用**分组，不按人工字符串 ID 或“恰好相等”的文本分组；
- family fingerprint 用于 durable exact capability/recovery，不能替代 runtime reference identity；
- family fingerprint canonical preimage 至少覆盖 system prompt、ordered canonical tool schemas、request
  projection schema 与 output protocol schema；
- member capability fingerprint 覆盖 implementation、maintainer id、target、family fingerprint 和 member
  task instruction；connection/model/cache TTL 不进入；
- family/member fingerprints必须从实际发送使用的immutable object graph内部计算，不接受caller传入一个
  “声称匹配”的fingerprint constructor参数。

可有 diagnostic family name，但它不拥有分组 authority。

### 5.5 Runtime lane 也是共享对象

Host composition 为每个 exact connection/model/API-surface/request-adapter binding 建立一个shared
`RecapMaintenanceExecutionLane` instance。同一 runtime route 的 members 引用同一 lane object；不同 route
不得共享 lane，即使字符串配置看起来相似。lane及其concurrency gate按Host/composition lifetime共享，不是
每个group或operation各建一个semaphore。

lane 是实际 dispatch 的唯一 owner：它持有 exact `ICompletionClient + ModelId + adapter/runtime policy`，并提供
唯一的 recap completion send 入口。concrete maintainer 不再私有注入、保存或公开另一份
`ICompletionClient` / `ModelId`，也不能绕过 lane 自行 dispatch。

composition还必须按exact `(Lane instance, Family instance)` intern一个sealed
`RecapMaintainerRuntimeGroup`。registry不再只返回unbound maintainer，而是返回由受控factory创建的executable
runtime binding：

```text
exact durable maintainer identity
  -> BoundMaintainer
       -> Member definition
            -> shared Family definition instance (only prefix producer)
       -> shared RuntimeGroup instance
            -> same Family definition instance
            -> shared ExecutionLane instance (only client/model/send owner)
```

受控factory/registry construction验证`ReferenceEquals(Member.Family, RuntimeGroup.Family)`；
`BoundMaintainer.MaintainAsync`只能经group中的family+lane执行，不能接受per-call override。Planner只验证frozen
`(MaintainerId, Target, CapabilityFingerprint)`，随后用显式reference comparer按opaque `RuntimeGroup` reference
分组；它不读取或持久化family fingerprint、connection id、model id、system prompt或tool schema。member
capability已经提交family semantic fingerprint，不在manifest再造第二字段。

### 5.6 Prefix structural identity

family instance 保证 static prefix，epoch instance 保证 dynamic prefix。实际 request builder 必须由这两个
shared aggregates 构造：

- system/tools 只能从 `Family` 取得；
- tool-choice/output constraint只能从`Family.OutputProtocol.RequestContract`取得；
- prior/history 只能从 `EpochInput` 取得；
- target/task instruction 只能追加在 cache boundary 之后；
- member 不得插入 boundary 前消息；
- actual send 只能经 `BoundMaintainer.RuntimeGroup.ExecutionLane`；
- provider adapter 不得重排 tools/system/shared messages/task suffix。

每个 `(RuntimeGroup instance, EpochInput instance)` 只构造一个 immutable `CompletionPromptPrefix` object；
该组所有members的requests引用这个同一prefix object，只各自追加tail。同一family若被Host路由到不同lanes，
仍共享同一static Family definition，但因provider cache domain不同，会在各RuntimeGroup内各建一个prefix。
Resume/Restore从frozen member capability和epoch authority重建新的operation-local sharedprefix，不要求跨process
对象identity稳定。

fingerprint 用于验证、日志和 durable binding；调度不需要重新 hash 60K dynamic content，也不依赖 hash
碰撞作为正确性边界。

## 6. Durable target：direct-cut sidecar

现有 sidecar 将 reset/rebuild，因此实施采用一个新的 strict Store generation。manifest/publication预期
为`v8`，Store header/directory、frozen input和final block也各自升级到下一个generation；若实施时HEAD已推进
则整体再前进。所有受影响component在同一个candidate中一次性切换到唯一canonical shape，直接拒绝旧
generation，不提供reader/migrator，也不在同一schema号下分阶段改变字段语义。

目标 manifest 以 set-level epoch 为中心：

```text
DerivedRecapSetManifest
  Epoch
    StartBoundary(address + setups)
    AdmissionBoundary(address + setups)
    exact shared history range commitment
    Previous
      Empty(bootstrap)
      | PriorRecapPackSnapshot
          source descriptor(anchor + envelope hash)
          projection schema id
          ordered blocks[block id, target, exact content, hashes]
    EpochPayloadSha256
  Blocks[]
    RecapBlockId + Target + MaintainerId
    MaintainerCapabilityFingerprint
    content ceiling + ordinal
  ManifestPayloadSha256

DerivedRecapFinalBlock
  RecapBlockId + Target
  EpochBlockExecutionSha256
  content + content/payload hashes
```

删除或收口：

- per-block `CatchUpBoundaries[]`；
- per-block replay source choice；
- 可落后于 set anchor 的 final `AbsorbedThrough`；
- current `Inherit` mode；
- `OldBlock` request payload；
- 任意 per-block no-call planning mode；
- rolling checkpoint、`work/`、`ResumeSuffix`与单endpoint已不需要的checkpoint install/codec API。

保留：

- 一个ordered structured `PriorRecapPackSnapshot`作为旧内容唯一真源；它既按block提供`KeepUnchanged`正文，
  又按冻结的projection schema生成shared prompt prefix，不再同时持久化flattened prior context和另一套per-block
  frozen inputs；
- block content/capability commitments；
- strict ordinal、atomic publication、size/count limits、path/lock/fsync guards；
- raw-head fence、bounded lineage与setup authority；
- Resume/Restore 只服从 frozen plan，不读取 active config或重新规划；
- Building install前exact验证previous source；install后Building与Published Restore完全self-contained，不再要求
  previous publication仍存在、仍healthy或envelope保持live值。

新的final/publication wire不再包含per-block coverage字段；materialization生成neutral
`SessionContextContribution`时统一从root epoch admission填入coverage。corruption detection所需的block
plan/content commitments继续保留，但不得复制root coverage形成第二表达。

删除per-block coverage后，final仍必须提交exact epoch，不能只hash稳定的member plan。canonical
`EpochBlockExecutionSha256`至少绑定`ManifestPayloadSha256 + block ordinal + canonical block definition`；
publication commitment也提交该值。把epoch N的final复制到epoch N+1必须fail closed。`KeepUnchanged`即使正文
字节相同，也会生成属于新epoch的不同execution identity。

Store/Planner还要引入`MaxTotalRecapPackUtf8Bytes`与actual canonical encoded-size gates，保证一个合法Published
pack一定能作为下一epoch的prior被冻结和编码；不能只限制单block，最后发布出下一轮无法rolling的aggregate。

## 7. Planning target

Planner 每次只选择一个 replay-safe shared slab：

```text
previous epoch boundary -> next admission boundary
```

policy只返回whole-set `NoBuild(reason)`或`Build(SetAdmissionAnchor)`；frozen `Blocks[]`本身就是ordered active
catalog的完整maintenance roster，不再包一层`Maintain | ...`plan union。policy不选择per-block
mode/source/window/boundaries，也不提供prior context。Evaluator负责：

- 所有 blocks 完整覆盖 catalog，target 唯一；
- epoch 起止和 cadence 合法；
- 每个roster entry的member capability在execution preflight时可exact解析；
- `MaxMaintainerCallsPerEpoch`对fresh execution等于完整roster size，对Resume/Restore等于pending roster size；
  它与lane-owned `MaxConcurrentCalls`是两个独立caps；
- output size、aggregate pack size和catalog count受hard caps约束。

动态添加 block/member 不是普通增量 epoch：新 block 没有 previous content，不能假装与已有 blocks拥有相同
coverage。第一阶段要求 topology 变化后显式 reset/rebuild全部 recap。未来若需要 online dynamic onboarding，
必须设计独立 bootstrap transaction，再让新 block 从某个完整 shared epoch加入；不得重新引入常态 per-block
lagging cursor。

若bounded online campaign因`MaxEpochsPerOperation`停止并返回`MoreWorkPending`，即使中间epochs已经合法
Published，也不得向本次online request返回`Ready` candidate；Host必须继续有界追赶或显式转入full rebuild。
中间publication保留为下一次工作的durable进度，但不是“已经足够新”的隐式降级结果。

## 8. Production parallel execution state machine

Building Resume 与 Published Restore 必须复用同一个internal **scheduling** kernel，避免形成两套并发/失败
语义；两者stage-specific proof、write authority、outer typed result与publication gate继续分开，由各stage向
kernel提供execute/install delegate，不能为代码复用而合并authority。

### 8.1 Resolve phase

1. 读取 frozen epoch 与 ordered block plans；
2. 跳过已经拥有 healthy final 的 block；
3. 为每个 pending roster entry exact resolve runtime binding；
4. 验证 resolved binding 的 durable maintainer identity、target和capability；family/runtime-group reference
   一致性已由sealed binding factory/registry construction保证；
5. 所有 pending calls共享同一个 materialized `RecapMaintenanceEpochInput`；
6. 用显式reference-equality comparer（例如`ReferenceEqualityComparer.Instance`）按opaque `RuntimeGroup`
   reference分组，组内保持frozen manifest order；不得依赖group类型当前的默认`Equals`实现；
7. 每个 `(RuntimeGroup, EpochInput)` 只创建一个shared prefix。

任何binding、shared-prefix projection或roster preflight失败都在发出第一个remote call前返回typed unavailable，
避免半执行后才发现catalog缺口。

### 8.2 First-real then followers

- 各 group 的第一项是 leader；所有 groups 的 leaders并行启动。因此不同connection天然并行，同connection
  的不同families也可并行。
- 若同一lane的groups数量超过lane concurrency cap，lane admission必须让已排队的leaders优先于
  已解锁的followers，先为每个family建立cache seed，避免早期family的followers推迟未启动leaders；
- 一个 group 的 followers 等 leader 的 `MaintainAsync`完整 settle 后并行启动。
- “settle”指成功返回或任何non-caller-cancellation terminal已确定；一旦leader进入dispatch，任何这类terminal
  都释放followers，以完成full roster。若leader未能写入cache，followers只是退化成普通并行调用，
  correctness不变。
- caller cancellation发生时不再启动queued followers，并取消且drain已启动工作；provider timeout或其抛出的
  `TaskCanceledException`只有在caller token确实已取消时才按全局cancellation处理，否则是普通indexed failure。
- 第一阶段不做 response-start release、zero-output warm-up、leader重选或投机双发。

“并行”仍必须有边界：protocol hard caps限制一个epoch的总calls/catalog size；runtime lane可再提供
shared `MaxConcurrentCalls` semaphore，约束该lane上的所有families/operations，而不是每group各自计数；不得把
默认值设成1来伪装恢复并行。scheduler第一阶段不做自动业务retry，recap lane也必须禁用
provider client/SDK内的隐式transport retry，因而一个logical invocation对应一个remote attempt。未来若引入
retry，每个actual attempt必须先从operation-owned remote-attempt budget获取许可，同时进入
logging/telemetry；不得把隐式retry藏在logical-call caps之外。

leader只影响经济性与延迟，不影响durable输出，因此无需把leader identity写入manifest。为测试和日志稳定，
默认选择frozen manifest中的组内第一项。

### 8.3 Result、persistence 与 failure

- fresh epoch中每个member有一个logical invocation；组内followers并行但单个call没有旧multi-endpoint loop。
  这不是crash-safe provider exactly-once：provider返回而final尚未落盘时崩溃，Resume允许重复调用；若final已经
  healthy则不得重复调用。
- `Updated`经过target/id/content limit/UTF-8/output-protocol校验后写final；
- `KeepUnchanged`从frozen source复制正文，但final coverage推进到epoch admission；
- 成功sibling的final即使另一block失败也保留，Resume/Restore不得重做healthy final；
- batch kernel把每个task包装成manifest-indexed outcome，不依赖`Task.WhenAll`的first-exception传播；它等待
  全部已启动工作settle/drain，只有完整final roster才publish/commit envelope；
- 多失败按frozen manifest顺序选择对外primary failure，completion顺序只进入diagnostics；
- Store短写可继续在global write lock内串行；remote wait不得持有Store lock；
- crash/reopen从已有healthy finals恢复，只重新执行缺失/损坏block。

若当前Store API无法安全支持并发完成后的独立final install，应重写该API或增加operation-scoped write
authority；不得因此把remote calls重新串行化。

## 9. Completion / cache contract

本重构需要一个明确的provider-neutral prefix boundary，而不是让adapter猜“最后一个相同message”。候选是把
逻辑请求拆成不可变两段：

```text
CompletionPromptPrefix
  SystemPrompt
  CompletionOutputContract    // ordered tools + family-owned tool-choice/output constraints
  SharedContextMessages

CompletionRequest
  ModelId                     // generic Completion surface only
  PromptPrefix
  TailMessages
```

对recap family：`SharedContextMessages = PriorRecapContext + RecentHistorySlab`，`TailMessages`只含member task。
若不采用上述通用type，也必须提供等价的typed boundary；不接受裸message index、人工cache key或adapter内
字符串搜索。

generic `CompletionRequest`可以继续携带`ModelId`等provider-neutral字段，但family member不能直接构造或发送
它。`BoundMaintainer`只能调用lane的窄入口：

```text
ExecutionLane.SendAsync(
    shared CompletionPromptPrefix,
    member TailMessages,
    RecapCallContext,
    cancellationToken)
```

lane注入并校验实际`ModelId`、max output、reasoning/reuse options和raw client；prefix中的
`CompletionOutputContract`必须来自`RuntimeGroup.Family.OutputProtocol`，lane/member都不得替换其
tools、tool-choice或output constraint。member没有上述任何参数的override。
`RecapCallContext`只提供maintainer/target/epoch/leader-follower等logging diagnostics，不改变wire语义。

provider adapter负责：

- Anthropic：在shared context末尾建立显式cache breakpoint，并使用短期reuse policy；
- OpenAI：映射其exact-prefix/cache-key/breakpoint能力；缺少可靠能力时允许best-effort，但不得伪报hit；
- Gemini：implicit与explicit CachedContent作为不同capability，不伪装成同一warm-up协议；
- OpenAI-compatible第三方surface：默认unknown/no guarantee，除非该adapter有独立verified contract。

prefix boundary必须携带provider投影provenance。尤其Anthropic converter会合并相邻同role messages；即使
shared prefix最后一条和member tail都是user/Observation，merge/normalize后也必须把`cache_control`落在
prefix最后一个content block，而不是移动到tail末尾。wire tests必须覆盖这个同role合并场景。

第一阶段调度只依赖“leader完整settle后followers可启动”，不依赖provider暴露cache-ready event。

Completion result/logging必须能携带provider报告的normalized optional telemetry：

- uncached input tokens；
- cache creation input tokens；
- cache read/cached input tokens；
- output tokens；
- cache `Requested / Supported / Observed / Unknown`正交状态与可选provider diagnostics；cache write/read可同时
  非零，不能压成单值`Hit/Miss` enum，也不能假设provider返回cache key；
- scheduler queue/gate wait与provider call total elapsed。

各stream parser必须定义usage merge规则：Anthropic可能在`message_start`与`message_delta`分段报告，OpenAI
Responses在terminal response报告，Gemini `usageMetadata`可能只在后续/final chunk出现；重复或累积事件不能
double-count。第一阶段不承诺TTFT/response-start latency，除非每个client都显式采样first provider event。

call-log schema同步升级：记录member/target/epoch/runtime-group/lane diagnostics、leader/follower、normalized
usage与完整canonical tool-contract fingerprint。只记录tool name/description不足以解释schema变化造成的cache
miss。上述字段都是observability，不进入durable recap identity。

telemetry是经济性证据，不进入recap正文、capability或durable recovery authority。

## 10. 代码所有权目标

| Concern | Owner |
|---|---|
| neutral epoch/request/success/binding/family-affinity contracts | 新的`SessionJournal.DerivedRecap.Abstractions` |
| durable epoch manifest/publication/finals | DerivedRecap.Store |
| cadence、shared slab、whole-roster Maintain plan、batch execution、Resume/Restore | DerivedRecap.Planner |
| concrete family/member definitions、request builder、output interpreter | DerivedRecap.Maintainers |
| prompt-prefix/cache boundary与provider mapping/usage telemetry | Completion.Abstractions + concrete Completion adapters |
| recap execution lane implementation | Completion或Maintainers中的recap-only runtime layer；它是raw client/model的唯一owner |
| member→lane composition、runtime singleton sharing、operator reset | Galatea / CLI composition roots |

Planner不得引用concrete Maintainers assembly；Store不得认识connection/model/family runtime对象；Host不得复制
Planner state machine或Store publication logic。

依赖方向锁定为（`A <- B`表示B引用A）：

```text
SessionJournal <- DerivedRecap.Store <- DerivedRecap.Planner
SessionJournal                 <- SessionJournal.DerivedRecap.Abstractions
Completion.Abstractions        <- SessionJournal.DerivedRecap.Abstractions
DerivedRecap.Abstractions      <- DerivedRecap.Maintainers
DerivedRecap.Abstractions      <- DerivedRecap.Planner
DerivedRecap.Store             <- DerivedRecap.Planner

Galatea / CLI
  -> Store + Planner + Maintainers + Completion
```

Store不引用runtime abstractions；Planner不引用concrete Maintainers；新的Abstractions不拥有prompt内容、provider
client或Store wire。current raw core中的recap-specific runtime contracts直接迁出，不留forwarding compatibility。

## 11. 工作包与实施顺序

### R0 — Rebaseline 与 executable invariants

Intent：把本计划的目标语义变成少量先行contract tests和fresh inventory。  
In scope：current call graph、schema inventory、测试地图；新增target-shape tests可先red。  
Out of scope：production行为修改。  
Done when：明确所有待删除的per-block cursor/catch-up/Inherit/OldBlock入口，以及所有Building/Restore串行循环。

### R1 — Provider-neutral Completion Prefix / Tail contract

Intent：先建立后续family与scheduler共同依赖的typed shared-prefix boundary。  
In scope：`CompletionPromptPrefix`、immutable `CompletionOutputContract`（ordered tools + tool-choice/output
constraints）、`CompletionRequest`prefix/tail shape、所有现有caller和provider converter的机械迁移；非recap
callers使用明确的default/auto constraints，保持当前wire语义。  
Out of scope：启用provider cache、usage telemetry、recap family。  
Done when：caller不能用裸message index表达boundary；tools与tool-choice/output constraints只经同一
`CompletionOutputContract`进入wire；converter在同role merge后仍保留prefix provenance；
所有Completion suites证明迁移前后的非recap wire等价。

### R2A — DerivedRecap Abstractions + Family / Member 单一真源

Intent：建立不可被member override的shared family aggregate、新capability fingerprint和唯一bound dispatch
形状。  
In scope：新建窄`SessionJournal.DerivedRecap.Abstractions`，从raw core移出
recap-specific neutral request/result/maintainer contracts；Maintainers definitions/catalog、canonical tool schema
fingerprint、`Updated | KeepUnchanged`成功union、shared output protocol（复用R1的
`CompletionOutputContract`）、placeholder built-ins；删除member私有
system/tools/parser注入。  
Out of scope：优化真实prompt内容、provider cache、并行。  
Done when：同family members只能引用同一family instance；catalog拒绝duplicate semantic family instances；
member API不存在system/tools/parser override；family能用shared epoch创建唯一prefix；tool或
output-protocol变化会改变所有member capability；connection/model不进入fingerprint；failure不成为第三种可
持久化success outcome。

### R2B — ExecutionLane / RuntimeGroup / BoundMaintainer 串行 wiring

Intent：在引入并行前，先让production只有一个不可绕过的family+lane dispatch path并保持可编译。  
In scope：lane唯一拥有client/model/options/send，sealedRuntimeGroup/BoundMaintainer受控factory，registry返回
binding；Galatea/CLI最小串行composition；lane-owned logging + per-call RecapCallContext；删除concrete
maintainer私有client/model。  
Out of scope：group scheduler、cache mapping、reset/rebuild。  
Done when：production request只能经binding group的family+lane发出；same route共享lane/raw client但日志仍按
member归属；Family/Group mismatch构造失败；NoBuild/all-healthy recovery不创建binding/client。

### R3A — Full-rebuild raw authority / forward spool

Intent：让reset/rebuild在超过normal raw-growth hard cap时仍有独立、可审计的authority。  
In scope：paged selected-lineage audit、forward epoch spool/index、raw-head变化、crash resume、typed
`FullRebuildRequired` / `MoreWorkPending`。  
Out of scope：Store vNext wire、maintainer dispatch、并行。  
Done when：normal path不full-scan fallback；explicit rebuild能从超cap lineage定位最老slab；spool可删且不成为
事实源；分页proof/raw-head变化fail closed。

### R3B — Shared Epoch vNext Store

Intent：以single epoch、structured prior pack、direct final替代per-blockcursor/catch-up/checkpoint wire。  
In scope：Store contracts/codec/installer/publisher/restore inspection；所有受影响schemas direct cut；删除
`Inherit`、per-block source/coverage、checkpoint/work目录；epoch-bound final与aggregate size gate。  
Out of scope：Planner cadence/execution、provider cache。  
Done when：Building install后self-contained；prior pack是旧内容唯一真源；cross-epoch final substitution拒绝；
旧schemas全部拒绝；direct final/Restore保留stage-specificauthority。

### R3C — Serial Shared Epoch Planner / lifecycle

Intent：先在无并发变量下证明full-roster single-epoch语义与多epochcampaign。  
In scope：Planner intent/evaluator/window preparation、serial execution、`NoBuild | Build(full roster)`、
`Updated | KeepUnchanged` persistence、normal bounded path和rebuild spool consumer、online candidate gating。  
Out of scope：parallel scheduler与provider cache。  
Done when：每个Building只有一份prior+history；fresh build每member一个logical invocation；NoBuild零调用；首轮
keep拒绝；operation cap返回MoreWorkPending且绝不暴露中间Ready candidate；backlog逐epoch无gap/overlap。

本包同时direct-cut Planner config/caps：用`MaxMaintainerCallsPerEpoch`替代旧按route endpoints计数的
`MaxMaintainerCallsPerBuild`，删除不再有意义的`MaxRouteEndpointsPerBlock`，并加入
`MaxEpochsPerOperation`、`MaxMaintainerCallsPerOperation`、catalog/aggregate limits。operation loop在安装
下一epoch前用checked counter保证完整pending roster不会越界；第一阶段recap lane禁用隐式
transport retry。config schema随语义变化升级，不在旧字段名下换解释。

R3B与R3C可以分别设计、审阅和准备候选补丁，但它们共用一个direct-cut integration gate：
不得在主线留下“Store已切vNext wire、Planner仍发送旧shape”的不可编译/不可运行中间提交，
也不得为跨包过渡增加compatibility reader。实施时要么先在不接入production的candidate types中完成
R3B，再与R3C原子切换；要么将两包的production cut合并为一个可编译、可恢复的独立提交。

### R4 — 并行 scheduling kernel

Intent：恢复DerivedRecap的多maintainer并行核心能力。  
In scope：operation-local `(RuntimeGroup, Epoch) -> CompletionPromptPrefix` intern、Building/Restore共享
scheduling kernel、并发
failure/cancellation/partial-final semantics。  
Out of scope：provider-specificcache boundary。  
Done when：不同lane/group leaders重叠；same group leader settle后followers重叠；block failure不丢successful
siblings；Resume/Restore共用语义；final publication order deterministic。

### R5 — Provider cache mapping 与 usage telemetry

Intent：让shared family+epoch结构在provider wire上形成真实可复用prefix。  
In scope：Anthropic/OpenAI/Gemini capability mapping、reuse options、usage telemetry、logging。  
Out of scope：`max_tokens=0`warm-up、response-start release、通用agent traffic scheduler。  
Done when：wire tests证明boundary在member task之前；unsupported provider明确no guarantee；日志能区分cache
write/read/miss/unknown。

### R6 — Galatea/CLI composition 与 reset/rebuild acceptance

Intent：按exactconnection/model建立sharedlane，完成production wiring和operator cutover。  
In scope：per-member runtime routing、per-lane singleton、per-binding lazy、explicit recap reset/rebuild、disposable
real-session acceptance。  
Out of scope：online dynamic topology与真实业务prompt设计。  
Done when：NoBuild不创建client；同route共享lane、不同route隔离；production确实并行；旧sidecar显式reset后
从raw重建；raw events不变。

### R7 — Provider canary 与经济性决策

Intent：用实际usage与latency决定默认cache策略。  
In scope：有界真实provider调用、call logs、cache hit/write token和latency对照。  
Out of scope：根据单次样本自动调参、provider价格长期承诺。  
Done when：至少一个production provider证明leader写入、followers读取；若未命中，报告真实原因并保持
correctness，不用估算值冒充证据。

## 12. 最小 regression matrix

### Family / request shape

- member不能构造私有system/tools；
- two members `ReferenceEquals(Family)`且`ReferenceEquals(OutputProtocol)`，system/tools/parser只能经该family访问；
- task instruction不同，但boundary前request object graph相同，且同RuntimeGroup内引用同一prefix；
- family/tool/output schema变化导致member capability变化；
- family-owned tool-choice/output constraint进入shared prefix并到达provider wire，member/lane无override通道；
- fingerprint从实际immutable object graph计算，caller不能注入伪值；
- 相同family fingerprint对应两个不同family references时catalog拒绝；
- Member.Family与RuntimeGroup.Family不同则binding构造拒绝；
- connection/model/cache TTL变化不改变durable capability。

### Shared epoch

- first build：Empty prior + one slab，所有maintainers收到同一epoch实例；
- second build：previous pack + one unrelated slab，所有maintainers看到相同previous pack和相同messages；
- 一个member不能要求更旧cursor或不同endpoint；
- `KeepUnchanged`正文不变、coverage推进；
- first build unchanged拒绝；
- NoBuild零调用；fresh Build完整roster每个member有一个logical invocation；
- oversized backlog产生多个sequential shared epochs，不产生per-block route；每个Building仍只有一个epoch；
- multi-epoch slabs连续且无gap/overlap，每轮prior恰好来自上一完整publication；
- bounded online loop达到operation cap时返回typed `MoreWorkPending`，已Published epochs保持有效，但本次online
  candidate不得返回Ready；
- normal bounded authority无法覆盖baseline时返回`FullRebuildRequired`，不full-scan fallback；
- explicit rebuild在超过raw-growth hard cap的fixture上仍能从最老slab逐epoch完成，并检测分页proof/raw-head变化；

### Scheduler

- two connections：leaders并发；
- same lane、different families：leaders可重叠但共同服从lane-owned concurrency cap；
- lane cap小于family数时，queued leaders优先于已解锁followers获得admission；
- same lane、same family：followers在leader完整settle前不启动，之后并发；
- same Family、different lanes：各自成为leader并拥有独立RuntimeGroup/prefix；
- leader任何non-caller-cancellation terminal后followers仍启动；caller cancellation不启动queued followers并
  drain started calls；provider timeout不得误判成caller cancellation；
- leader同步抛异常不悬挂gate；single-member group只执行leader；
- 两个`Equals == true`但非同一reference的fake RuntimeGroups不得合组；
- multiple failures等待全部started work后按manifest顺序选择primary；
- operation在下一epoch前超出`MaxMaintainerCallsPerOperation`时零dispatch该epoch并返回
  `MoreWorkPending`；checked arithmetic溢出fail closed；
- operation budget连首个完整pending epoch都无法容纳时返回typed configuration/limit failure，
  而不是`MoreWorkPending`；
- recap lane不发生隐式transport retry；未来显式retry的每个attempt都消耗operation budget；
- successful sibling final在失败后可被Resume复用；
- 原leader已healthy时，组内第一个pending member成为新leader；all-healthy不resolve binding/client；
- final write失败不阻止其他completed siblings尝试持久化；
- Building Resume与Published Restore共用scheduling behavior，但保留各自stage authority/results。

### Persistence/recovery

- Store/manifest/input/block/publication旧schemas全部direct reject，canonical writer只有新shape；
- manifest只含一个epoch prior/history authority和一个structured PriorRecapPack旧内容真源；
- final/publication wire无per-block coverage，materialization从root admission合成；
- epoch N final放入epoch N+1因execution binding不同而拒绝；
- `KeepUnchanged`正文相同仍生成新epoch-bound final identity；
- Building安装后previous publication损坏/变化，Resume/Restore仍只用build-local prior pack；
- 新schema不创建或读取`work/`checkpoint；provider返回后final写前crash允许重调，final healthy后不重调；
- aggregate prior pack超限在发布不可继续rolling的set之前fail closed；
- corrupt/missing one block只修复该block，healthy sibling不重做；
- raw head变化、setup mismatch、lineage不足继续typed fail closed；
- publication只在完整roster后发生。

### Provider/cache

- exact wire prefix在task tail之前；
- Anthropic cache breakpoint位于shared history末尾；prefix末尾与tail同为user role时仍落在prefix content block；
- OpenAI/Gemini unsupported or best-effort状态不会伪报hit；
- multi-event usage merge不double-count，unknown/unsupported与zero tokens可区分；
- cache usage telemetry进入Completion result/log，不进入recap durable identity；
- same route共享lane/raw client时，logging仍按member/target/epoch归属，并记录canonical tool-contract fingerprint。

## 13. 明确非目标

- 设计autobiographical/world-understanding或未来主题blocks的实际内容；
- prompt wording、语言版本与业务质量调优；
- 自动发现插件、在线动态添加block、跨拓扑无reset迁移；
- 高动态检索、working memory、vector recall；
- provider-neutralzero-tokenwarm-up；
- 首response字节到达即释放followers；
- distributed scheduler、多进程cache coordinator、remote Store；
- 把cache命中当作correctness requirement；
- 为旧sidecar、旧promptfingerprint或旧registry保留compatibility layer。

## 14. Review gate

每个实现工作包遵循：re-review → plan lock → implementation → independent review → tail fix。高风险finding
必须在当前包内解决，包括：

- family member仍能override prefix；
- bound maintainer仍能绕过其RuntimeGroup，使用私有client/model/prompt发送；
- per-block replay window/cursor以新名字残留；
- Building与Restore出现两套scheduling kernel，或为共享kernel合并了stage-specificauthority；
- connection/model进入durable identity；
- parallel failure使successful sibling丢失或publication不完整；
- scheduler依赖RuntimeGroup默认`Equals`而不是显式reference equality；
- final未提交exactepoch，允许cross-epoch substitution；
- flattened prior与structured/per-blockold content并存为两个持久化真源；
- provider adapter把task-specific tail错误纳入shared prefix；
- test只比较字符串相等，却没有证明shared object ownership；
- 文档把target design误写成current implemented fact。

所有代码与文档提交至少运行受影响focused tests、project build、
`python scripts/check_session_journal_docs.py --all-tracked`与`git diff --check`。真实provider/staging gate必须
单独报告是否实际执行，不能用deterministic tests替代。

## 15. 完成定义

本重构完成时，production必须能证明：

1. 一次Build只冻结和materialize一份structured previous pack + recent-history slab；
2. `NoBuild`零调用；fresh Build为完整roster每个member安排一个logical invocation，成功结果只有
   `Updated | KeepUnchanged`；
3. 所有members是对该shared epoch的分治，正常wire没有per-block cursor、catch-up route或checkpoint；
4. family成员结构上共享同一system/tool/output contract aggregate，bound execution没有private dispatch旁路；
5. 不同connection/family RuntimeGroups并行，同group执行first-real then parallel followers，并服从shared lane
   concurrency cap；
6. block结果独立验证/持久化并绑定exact epoch，完整set才publish，Resume/Restore self-contained且保持exact；
7. shared prefix在provider wire上有typed boundary和可观测cache usage；
8. operator已显式reset旧sidecar，并能通过verified full-rebuild authority从超cap raw history重建；
9. raw events与selected `RefId` Parent lineage仍是唯一事实authority。

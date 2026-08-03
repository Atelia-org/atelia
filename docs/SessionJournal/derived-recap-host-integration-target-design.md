# SessionJournal DerivedRecap Host Integration：短目标设计

> **状态**：Target Shape / Rule
> **日期**：2026-07-31
> **核心实现背景**：
> [EADR V4 目标设计](event-addressed-derived-recap-v4-target-design.md)、
> [Repo-owned RecapPlannerConfig](recap-planner-config-repository-design.md)
> **首个 library consumer**：
> [Galatea SessionJournal cutover plan](galatea-session-journal-cutover-plan.md)

## 0. 决策

将当前只存在于 `SessionJournal.Cli` internal composition root 的两段 correctness-sensitive
逻辑提升为 `SessionJournal.DerivedRecap.Planner` public API：

1. **pure config resolver**：把一个 immutable repo config snapshot与 Host提供的
   metadata-only capability snapshot解析成 `RecapPlanningInputs + RecapPlanningLimits`；
2. **Building-first preparation kernel**：在不创建 Completion client或 concrete Maintainer的
   前提下，选择 frozen Building或生成 pinned NewPlanning authority。

online lifecycle随后只接受 preparation产生的 authority。CLI和Galatea仍各自是 Host
composition root，负责 durable phase gate、Completion connection、logging、ToolSession、
application result与UI映射。

本设计不直接公开 CLI internal类型，不让 Planner引用 concrete Maintainers，也不创建包办
SessionRuntime的万能 Host framework。

> **实施进度（2026-08-01）**：H0 public config resolution、H1 Building-first preparation与H2
> concrete runtime laziness均已落地。Planner现已提供neutral capability/config snapshots、
> Host-injected immutable resolution catalog、construction-zero-touch repository source、public
> preparer/authority、authority-only lifecycle与once-only deferred Maintainer registry；CLI已迁为
> thin concrete/report adapter并通过目标Galatea export的deterministic real-repo gate。

第二个 Host已经明确是 `prototypes/Galatea`。因此这不是为假想消费者预留扩展点，而是Galatea
替换 `ChatSessionEngine`存储层和状态机前的必要 API hardening。

## 1. 实施前基线与剩余缺口

public API已经包含：

- repo config document、codec、loader与initializer；
- policy、HistoryLoad estimator与registry；
- Store selection/read/write；
- Planner、Building Resume、Published Restore executors；
- online lifecycle与context candidate source。

H0实施前，安全的端到端装配分散在：

- `SessionJournal.Cli/RecapPlannerComposition.cs`：config → policy/estimator/profile/catalog/limits；
- `SessionJournal.Cli/RecapOperationReadiness.cs`：Building-first、catalog migration、baseline pin与
  raw-head fence。

复制这些 internal逻辑的 Host很容易产生下列错误：

- 在 frozen Building前读取active config；
- 把active roster误当完整 execution capability registry；
- 漏掉duplicate block/target或protocol hard-cap检查；
- 一次operation多次读取config；
- NewPlanning没有绑定readiness时捕获的baseline；
- latest Published catalog不兼容时仍继续规划。

H0已把第一项config resolution关闭为可复用public contract；H1关闭第二项Building-first
readiness/preparation与authority-only lifecycle；H2又关闭lazy concrete runtime。至此Galatea
不再需要复制CLI internal config/preparation/laziness算法，剩余前置缺口是cutover plan中的
recovery binding、desired setup、completed-turn projection/rewind与Host行为，而不是Recap
composition API。

## 2. 所有权与依赖

```text
SessionJournal
  raw protocol + recovery + neutral context contracts
       ^
       |
DerivedRecap.Store
       ^
       |
DerivedRecap.Planner
  config loader + pure resolver
  Building-first preparer
  Planner / Resume / Restore / online lifecycle

DerivedRecap.Maintainers
  concrete profiles + prompts + factories

CLI / Galatea
  phase gate + capability projection
  connection/model/logging/tools
  SessionRuntime + application/UI mapping
```

约束：

- Planner不得引用 `SessionJournal.DerivedRecap.Maintainers`或 `Atelia.Completion` concrete
  provider；
- Store不获得config、policy或Maintainer；
- Host从同一个immutable concrete catalog投影planning metadata，并保留原catalog用于执行；
- raw events始终是correctness source；Recap config和Store都是repo-owned companion，不进入
  raw event chain。

## 3. Public neutral contracts

下列名称是目标shape，不冻结最终C#拼写。

### 3.1 Capability snapshot

```text
RecapProfilePlanningDescriptor
  ProfileName
  RecapBlockId
  Target
  MaintainerId
  MaintainerCapabilityFingerprint

RecapMaintainerCapabilitySnapshot
  ordered/immutable descriptors
  ResolveActiveProfile(ProfileName)
  SupportsFrozen(MaintainerId, Target, MaintainerCapabilityFingerprint)
```

snapshot在构造时复制输入并使用ordinal comparison。它是metadata-only：

- 不包含prompt正文；
- capability fingerprint是opaque `sha256:<64 lowercase hex>` token；其canonical preimage由
  concrete Maintainers assembly拥有；
- 不包含Completion client、connection、secret或factory；
- 不创建目录或call log；
- 不因active config删除profile而删除仍受Host支持的frozen capability。

构造规则：

- `ProfileName`必须ordinal unique；
- frozen execution key
  `(MaintainerId, Target, MaintainerCapabilityFingerprint)`必须unique；同一ID与Target可保留多个
  fingerprint，以便恢复旧frozen plan；
- capability catalog允许多个profile映射到同一个`RecapBlockId/Target`，以支持producer/profile
  换代；
- 只有resolved active roster必须拒绝重复`RecapBlockId`或Target。

active roster只决定新的Building。Resume/Restore必须使用完整capability snapshot验证frozen
`MaintainerId + Target + MaintainerCapabilityFingerprint`，再由Host的完整maintainer registry执行。

### 3.2 Pure config resolution

```text
RecapPlannerConfigResolver.Resolve(
  config snapshot,
  immutable policy catalog,
  immutable estimator catalog,
  capability snapshot
)
  -> ResolvedRecapPlanningConfiguration
  |  Invalid(defects)
```

resolved result至少包含：

- config `Document + CanonicalBytes + Path + ConfigSha256`；
- ordered active profile bindings；
- exact `RecapPlanningInputs`；
- exact `RecapPlanningLimits`。

resolver统一负责：

- policy、estimator与profile ID解析；
- estimator registry key与`Estimator.Id`一致性；
- cadence、ordered catalog、inputs与limits构造；
- duplicate resolved block/target拒绝；
- `RecapProtocolHardCaps.V4.ValidatePlanningAuthority`。

pure resolver不打开文件、Store或raw repo，不创建Maintainer。文件的Missing/Invalid/Unavailable
仍由loader表达；resolver只表达领域解析成功或Invalid。

不提供 `LoadAndResolve(repositoryRoot)`捷径。Host必须先完成phase gate；preparer也必须能延迟
调用active composition source。

Planner提供construction-zero-touch的repository-backed source：

```text
IRecapActivePlanningConfigurationSource.Load()
  -> Available(ResolvedRecapPlanningConfiguration)
  |  Missing(path)
  |  Invalid(defects, optional config provenance)
  |  Unavailable(path, reason)
```

source持有repo path、immutable policy/estimator catalogs与capability snapshot，但构造时不打开文件。
只有preparer确认current-lineage Building为`None`后才调用它一次。这样两个Host不需要分别复制
loader-result → resolver-result → readiness-result stitching。

### 3.3 Prepared authority

```text
DerivedRecapOperationPreparer.PrepareAsync(
  engine,
  store,
  capability snapshot,
  lazy active-composition source
)
  -> Ready(FrozenBuilding)
  |  Ready(NewPlanning)
  |  Retryable
  |  Unavailable
```

`FrozenBuilding`：

- exact `BuildingDescriptor`；
- captured lineage；
- preparer签发时的repository/RefId binding（不必作为public数据暴露）；
- frozen capability已经通过验证；
- config provenance必须为空。

`NewPlanning`：

- resolved immutable composition；
- captured lineage；
- preparer签发时的repository/RefId binding；
- `DerivedRecapPlanningBaseline`；
- config provenance来自同一个snapshot。

preparer的强制顺序：

```text
capture current lineage
  -> select current-lineage Building
     -> Available: validate frozen capability; never call config source
     -> Multiple/Stale/Invalid/StoreUnavailable: typed unavailable
     -> None:
          call config source exactly once
          resolve one immutable composition
          read latest Published frozen catalog
          require exact active/frozen catalog shape
          recheck raw head
          return pinned NewPlanning
```

`RawHeadChanged`和`SourceChanged`是typed Retryable，不依赖Host解析字符串code。preparer不接收
`ICompletionClient`、logging path或concrete maintainer factory。

preparer同时验证四者是同一个operation authority：engine、Store、prepared authority的
repository/RefId，以及file-backed snapshot的canonical repo path；resolved active profiles还必须
exact属于本次传入的完整capability snapshot。anonymous/in-memory snapshot的path允许为空，供测试
与显式custom Host使用。

Building Resume和Published Restore继续是两个独立frozen-only executor；不并入preparer。

### 3.4 Lifecycle binding与lazy execution

online lifecycle增加authority-only factory：

```text
DerivedRecapOnlineLifecycleCoordinator.Create(
  engine,
  store,
  prepared authority,
  maintainer registry
)
```

- `NewPlanning`只能生成pinned-baseline lifecycle；
- `FrozenBuilding`只能生成exact-descriptor lifecycle；
- 当前没有baseline的online constructor直接降为internal；public production入口只接受prepared
  authority，不保留兼容层。

Planner提供Maintainers-neutral、具有thread-safe once-only activation的
`DeferredRecapBlockMaintainerRegistry(Func<IRecapBlockMaintainerRegistry>)`：

- 第一次真实`TryResolve`才激活inner registry；
- 只激活一次；
- readiness failure和NoBuild时factory调用零次；
- Host factory必须从完整capability catalog构造inner registry，而不是active roster。

Host可以在preparation成功后创建agent Completion client；concrete recap Maintainer与其logger则由
deferred registry延迟创建。

阶段归属：prepared authority、lazy active-composition source与authority-only lifecycle factory已在
H1完成；`DeferredRecapBlockMaintainerRegistry`及CLI lazy wiring已在H2完成。whole-registry
factory只在第一次exact
`(MaintainerId, Target, MaintainerCapabilityFingerprint)` lookup时激活；不增加per-binding state machine、
activation状态或retry/reset API。

## 4. Host operation order

phase-first gate仍属于Host，因为判断Prepared/Started时不应先构造Store、capability snapshot或
config source：

| durable phase | Host行为 | Recap访问 |
|---|---|---|
| `Idle` | 准备新Observation | 调用preparer |
| exact `TurnFailed` | 先`AbandonFailedTurn(FailedHead)`并reinspect为`Idle` | zero-touch |
| `AwaitingAgentAction`且需要形成新request | `ResumeAsync` | 调用preparer |
| `AwaitingCompletionDispatch` | exact frozen request recovery | zero-touch |
| `AwaitingCompletion` | exact attempt recovery | zero-touch |
| tool continuation | 由raw phase与ToolSession决定 | 仅在下一request确实要prepare时进入lifecycle |

对于Galatea这类允许每轮切换connection的Host，selected connection只在`Idle`上与governing
`RuntimeConfigSetup`协调：ModelId或CompletionSurfaceId改变时，Host显式append新的setup，然后
preparer从变化后的raw head捕获authority。`TurnFailed`必须先exact abandon；system prompt同步
遵守同一规则。
Prepared/Started期间禁止append setup。

Host顺序：

```text
open raw SessionJournal
  -> inspect durable phase
  -> recovery-only:
       InspectRuntimeRecoveryRequirements
       bind exact matching frozen runtime
       ResumeAsync
  -> new-request:
       resolve selected connection metadata
       reconcile runtime/system-prompt setup at a stable boundary
       open exact branch Store
       metadata-only capability snapshot
       preparer
       create selected agent client
       create deferred maintainer registry
       authority-only lifecycle
       bind SessionRuntime
       SendAsync / ResumeAsync
```

Prepared/Started recovery不得因为repo config或整个Recap Store缺失而失败。新request时Store缺失或
config无效必须在Observation、client/call-log和任何Host辅助LLM（包括input normalizer）调用前失败。

raw SessionJournal提供只读、store-neutral的runtime requirements inspection：

```text
InspectRuntimeRecoveryRequirements()
  -> NoRuntimeRequired
  |  FailedTurnMustBeAbandoned(FailedHead)
  |  NewRequestRequired
  |  FrozenCompletionRequired(
       SessionCompletionTargetIdentity,
       optional SessionToolRuntimeIdentity,
       uncertainty kind
     )
```

它验证当前tail并只暴露Host绑定runtime所需的non-secret durable identities，不返回Prepared request
正文。Prepared可进入safe exact dispatch；Started默认`Refuse`并保持零provider/零mutation，只有
显式授权`RestartWithNewAttempt`才允许潜在重复调用。
pre-Beta direct cut不扫描旧`TurnFailed + setup/Observation suffix`来恢复failure authority；这类tail
fail-closed并要求显式迁移或重建。

## 5. Galatea作为第二个Host

Galatea证明需要公共kernel，但不改变职责边界：

- 每个账号继续绑定一个session repo；
- 每个新turn可以选择不同connection；
- durable Prepared/Started recovery必须按frozen completion target选择匹配connection，不能使用
  页面当前选择覆盖它；
- raw SessionJournal需要提供non-secret recovery runtime requirements读取面；Host不能靠尝试不同
  client或解码internal Prepared payload猜测frozen identity；
- streaming observer与用户stop继续透传给`SendAsync/ResumeAsync`；
- 新消息入口遇到未完成durable turn时必须返回recovery-required，不能丢弃新消息后静默Resume；
- recent-turn UI从raw replay投影，不把DerivedRecap伪装成raw聊天event；
- 旧的post-generation `CompactAsync`与`compaction*`配置退出production authority；
- system prompt改变通过explicit raw `SystemPromptSetup`表达，且只能在稳定可写boundary同步；
- SessionJournal已知失败不会提交partial assistant Action，但会保留Observation与failure
  provenance；若Galatea继续承诺“失败/用户停止不进入后续对话”，必须通过high-level、
  CAS-protected failed-turn abandon/rewind表达；
- “撤销上一轮”同样必须使用SessionJournal high-level turn rewind API；Galatea不得直接调用
  EventJournal `MoveRef`。

Galatea的具体迁移顺序、fixture和产品验收见配套cutover plan。Host cutover不要求更改raw wire、
Recap Store schema或planner config schema。

## 6. Assembly决定

第一阶段不新建 `SessionJournal.DerivedRecap.Hosting`：

- correctness-sensitive、Maintainers-neutral的resolver/preparer天然属于Planner；
- phase、connection、logging与UI天然属于各Host；
- 仅为十几行concrete catalog projection增加assembly不能降低系统复杂度。

H2复核后仍不新建Hosting assembly：CLI当前只剩concrete profile projection、prompt fingerprint与
log context；Galatea尚未形成第二份稳定重复实现。等Galatea真正切到public kernel后，只有重复代码
形成清晰且不包含CLI/UI policy的共同闭包时，才提取薄Hosting companion；不得因此把
SessionRuntime或online turn状态机搬出raw SessionJournal。

## 7. 验收

- resolver可由不引用CLI的测试/Host直接使用；
- CLI和Galatea不再复制policy/estimator/profile/limits领域解析；
- Building存在且config missing/invalid/throwing时，config source调用零次；
- no Building时config source恰好调用一次；
- active roster与完整frozen capability registry有明确分离测试；
- catalog mismatch、raw-head drift与Published envelope drift均为typed result；
- NewPlanning report与authority使用同一个config hash；
- FrozenBuilding、Resume、Restore没有active config provenance；
- NoBuild不构造concrete Maintainer或maintenance logger；operator-only `recap run`还保证整个
  call-log目录不存在，online turn则仍会产生必要的agent call log，但没有maintenance log；
- Prepared删除config和Recap Store后仍可safe exact dispatch；Started仍可在zero-touch下返回typed
  Refuse，并只在显式授权后restart；
- CLI现有wire/report行为保持；Galatea通过配套real-session gate。

## 8. 非目标

- DI framework、plugin discovery、global mutable registry或hot reload；
- config fallback、environment override或第二份Host defaults；
- provider token estimator或connection config进入planner config；
- 自动Store create/reset；
- 后台scrub、recursive repair或exactly-once provider workflow；
- 把Galatea UI、账号认证或SSE协议做成通用Hosting库；
- 本设计内直接实现turn rewind、recent-turn projector或Galatea cutover。

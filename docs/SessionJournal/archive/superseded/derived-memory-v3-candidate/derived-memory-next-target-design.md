# SessionJournal DerivedMemory Next：目标设计

> **状态**：Target Shape / Rule，指导新 generation 实现
> **日期**：2026-07-29
> **目标成品程序集**：`Atelia.SessionJournal.DerivedMemory`
> **目标存储 generation**：`derived/memory/v3/`
> **配套计划**：
> [DerivedMemory Next 实现与替换计划](derived-memory-next-implementation-plan.md)
> **前置决策**：
> [SessionJournal 恢复与 DerivedMemory 化简](../../completed-plans/session-journal-recovery-and-derived-memory-simplification-plan.md)

## 0. 文档目的

本文只定义新一代 DerivedMemory 的最终外观、领域实体、持久化事实和故障语义，不描述从旧实现迁移
代码的中间步骤。

目标不是把 current `JobFingerprint`、`TransactionId`、`CandidateId`、`AttemptId` 换一组名字，
而是重新建立一套更直接的 maintenance 模型：

```text
shared immutable epoch
  -> stable maintenance job
  -> independent per-role attempts
  -> durable per-role settlements
  -> durable closure finalization
  -> atomic coherent-domain ArtifactSet publication
  -> exact publication-view + NthPrevious selection
```

完成后的实现必须让人类维护者能从上述主线理解系统。安全写入、raw authority、validation 和
rebuild 都服务于这条主线，不再各自形成一套平行领域模型。

这条主线只负责**有界、可恢复的结构化记忆维护**。它不是完整的长期检索系统，也不把
`NthPrevious` 冒充为语义召回。新 generation 必须先让 Galatea 这样的 Role-Play Agent 在
压缩、崩溃和重启后维持基本人格连续性，同时允许低价值细节随反复重写而消退。

## 1. 产品承诺

新一代 DerivedMemory 必须同时兑现以下承诺。

1. raw SessionJournal 始终是 history、setup 与 execution correctness source。
2. DerivedMemory 完全可删除、可重建；raw event 不引用 derived identity。
3. 同一 epoch stream 上的 MemoryMaintainer 消费同一个 immutable epoch；首版只启用一个
   core coherence domain。
4. 每个 role 独立执行、独立失败、独立 settlement。
5. role 一旦形成 durable settlement，进程崩溃或重启后不得再次调用该 role 的昂贵 producer。
6. 未 settlement 的 missing、failed 或 uncertain role 可以产生后继 attempt，不改变 job identity，
   也不使其他 role 的 settlement 失效。
7. 同一 coherence domain 的全部 declared roles 未闭合时不发布半套 memory；旧 usable set
   继续在线可用。
8. domain roles 闭合后先 durable finalization，再原子发布唯一预期 ArtifactSet。
9. finalization 后重启不得运行 producer，只能完成或验证同一个 set publication。
10. `NthPrevious(n)` 只表示一个已选 publication lineage 上的时间深度；`n = 0` 即 latest，
    不做 budgeted fallback。
11. publication selection 与 request-time projection 是两条不同的轴；首版只实现一个静态
    core view，但接口不得把未来的 work/private/client projection 封死。
12. 每个 role 有 stable content budget；超过上限不得 settlement。正常遗忘通过 latest artifact
    不再携带旧细节发生，不靠破坏 immutable history；稳定超限的 frozen job 可以被显式
    reprovision，不得永久卡死 pending-first。
13. maintainer 在吸收旧区间时可以读取同一 planning snapshot 中保留的 recent suffix 作为
    reabsorption lookahead，但只能把 `SourceEndInclusive` 之前的区间声明为已吸收。
14. policy、prompt、model 或 role roster revision 不得自动切断同一 stable coherence domain
    的 ArtifactSet lineage。
15. 已 Prepared 的 completion request 自包含 exact context snapshot；Prepared reopen 不依赖
    DerivedMemory。

### 1.1 Galatea MVP baseline

首个真实 consumer 不是抽象的 artifact reader，而是当时由machine-local/ignored path
`prototypes/Galatea/.atelia/galatea/prompts/cyber.md` 定义的 Galatea；该路径不纳入tracked authority。
V3 的最小 baseline 固定为一个静态 core coherence domain，包含两个 concrete required
memory slots：

- `autobiography` →
  `roleplay.first-person-autobiography`：承载第一人称自我认识、重要经历、关系、承诺、计划和
  未完成话题；
- `world-understanding` →
  `roleplay.world-understanding`：承载外部事实、人物与世界模型，并保持“自我经历”和
  “对外界的理解”之间的认知边界。

DerivedMemory 不规定这两个 block 的内部 Markdown schema；内容规则和 voice 继续由
`SessionJournal.Maintainers` 的 concrete profiles 拥有。但端到端验收必须证明：

- maintenance、reopen 与 request materialization 后，Galatea 仍能延续“我是谁”、重要关系、
  长期计划与尚未完成的话题；
- recent scene、当前动作、天气和可互动物体主要由 dependency-closed raw suffix 保真，不要求
  长期 memory 永久保存；
- Galatea 输出流中的 Hint 仍是 raw history 的一部分，由 concrete maintainer prompt 解释并影响
  重写；V3 保证 Hint 位于正确 absorbed/lookahead input 中，但不为 Hint 另建 durable table 或
  workflow；
- 允许普通细节随时间淡出；超限 result 不可发布，但系统必须提供显式 reprovision
  路径，不能让不可完成的 frozen job 永久占据 pending-first；
- DerivedMemory 只保证输入、provenance、容量与原子 publication，不声称能以形式化测试证明
  LLM 输出的文学质量或主体性真实性。

## 2. 程序集与依赖边界

最终依赖方向固定为：

```text
Atelia.SessionJournal.DerivedMemory
        |
        v
Atelia.SessionJournal

Atelia.SessionJournal.Maintainers
        |
        v
Atelia.SessionJournal contracts

SessionJournal.Cli / Agent Host
        |
        +--> SessionJournal
        +--> SessionJournal.DerivedMemory
        +--> SessionJournal.Maintainers
```

约束：

- raw `Atelia.SessionJournal` 不引用 concrete DerivedMemory 或 Maintainers；
- DerivedMemory 不引用旧 `Atelia.SessionJournal.DerivedMemory` generation；
- Maintainers 提供 concrete producer/profile，不拥有 epoch、job、attempt、settlement 或 set；
- CLI/Agent Host 是 composition root，负责把 Engine、DerivedMemory 和 concrete maintainers 绑定；
- 不通过 production `InternalsVisibleTo` 打穿程序集边界；
- 若现有 store-neutral SessionJournal 接口不足，可以修改接口，但接口只能暴露 raw-facing
  capability，不能泄漏 derived store identity。

### 2.1 SessionJournal-facing online ports

默认继续使用：

- `ICoherentContextCandidateSource`：two-phase select/materialize；
- `ISessionMemoryLifecycleCoordinator`：在安全 raw boundary 推进 maintenance。

新实现可以收窄参数或增加一个更明确的 raw-planning port，但必须保持：

- candidate descriptor content-free；
- materialized candidate 只包含 exact context contributions 与 raw-facing assertions；
- SessionJournal 对 anchor/setup/source assertions 做 authoritative validation；
- `NthPrevious` 只从 RuntimeConfig/request 表达时间深度；
- publication view/projection 由 DerivedMemory online adapter 在 Prepared 前显式决定；
- Prepared 保存最终 materialized exact contributions，不在 reopen 时重新运行 selector；
- DerivedMemory 不得修改 raw journal。

### 2.2 DerivedMemory public facade

新程序集不再把各个 JSON Store 当作主要 public API。目标 public surface 以少量 facade 为主：

```text
DerivedMemoryWorkspace.Open(sessionRepositoryPath)
  -> Bind(SessionJournalEngine, MaintenanceProvisioning)
      -> ConfigurePlannerAsync(...)
      -> PlanNextEpochAsync(...)
      -> RunMaintenanceAsync(...)
      -> ValidateAsync(...)
      -> RebuildIndexesAsync(...)
      -> CreateOnlineAdapter(StaticPublicationView)
```

概念名称可以在实现设计时微调，但以下边界必须成立：

- branch-local 操作只能通过已绑定 Engine 得到 exact `RefId` scope；
- CLI 不直接拼 transaction、attempt、settlement 或 finalization DTO；
- low-level stores、wire DTO、hash DTO 默认 internal；
- 首版 `StaticPublicationView` 只选择 core coherence domain 的全部成员；
- facade 可以为未来的显式 view selector/composer 保留窄注入点，但不得在 V3 首切中引入
  semantic classifier、ranking 或 query DSL；
- inventory/report 是面向运维的窄投影，不回传完整 producer prompt、raw arguments 或敏感调用内容。

### 2.3 配置 authority

DerivedMemory 可以保存 immutable planner/job provisioning snapshot，但它不是 Galatea 自主决定
“创建、暂停或退休哪个 maintainer”的最终 authority。

- 当前 provisioning 由 Agent Host、raw setup/configuration 或另一个 durable control plane 提供；
- 删除整个 v3 后，调用方必须能从该 authority 重新提供 active configuration；
- v3 内的 current pointers 只缓存已 provisioned immutable facts，不得凭 wall-clock 或目录顺序猜测
  用户当前想要的 topology；
- 未来若 Galatea 自主创建/退休 maintainer，该决定必须先进入可重放的 control plane，再由
  DerivedMemory 执行；不能只写入可整体删除的 derived state。

## 3. 核心领域实体

### 3.1 稳定 identity 层级

必须先区分三类 identity，避免把一次执行配置误当成 Galatea 的长期记忆身份。

| 层级 | identity | 语义 |
|---|---|---|
| stable | `BranchRefId + EpochStreamId` | 一条共享 raw planning clock |
| stable | `BranchRefId + CoherenceDomainId` | 必须原子发布的最小 memory domain |
| stable | `MemorySlotId + Target` | 跨 job、prompt/model upgrade 保持连续的逻辑 memory slot |
| epoch/job-local | planner config、epoch、maintenance policy、RoleSpecs、job/attempt/set | 某次规划和执行 revision |
| request-local | publication view、每条 lineage 的 `NthPrevious`、composition order | 本次请求实际看见什么 |

首版只有：

```text
EpochStreamId      = roleplay-memory
CoherenceDomainId  = roleplay-core
MemorySlotId       = autobiography | world-understanding
PublicationView    = roleplay-core/full
```

这些默认值是首个 product profile，不应硬编码进 raw SessionJournal core。未来增加客户或任务专属
memory 时，可以增加新的 slot/domain/view，而不改写历史 job；本 generation 不实现动态 registry。

### 3.2 PlannerConfig

`PlannerConfig` 是 immutable、content-addressed shared epoch scheduling policy，至少固定：

- `BranchRefId + EpochStreamId` key；
- token estimator/version；
- trigger/hard-limit scheduling policy；
- dependency-safe boundary policy；
- reabsorption lookahead 的 bounded materialization policy。

它**不固定 active role roster、prompt/model 或 publication view**。这些属于 job provisioning
或 request-local selection，不能改变共享 epoch clock。

mutable current-config pointer 只能指向已由 configuration authority provisioned 的 immutable
config。只要 v3 facts 尚在，pointer 可以从唯一 config lineage tip rebuild；删除整个 v3 后必须由
外部 authority 重新 provision，不能猜测。

### 3.3 Epoch

`Epoch` 是同一 epoch stream 上所有 role/domain 可共享的 immutable raw input：

```text
Epoch {
  EpochId
  PlannerConfigId
  BranchRefId
  EpochStreamId
  PreviousEpochId
  SourceStartExclusive
  SourceEndInclusive
  PlannedAtRawHead
  RawStartSetups
  MeasuredAbsorbedCost
  MeasuredLookaheadCost
}
```

其中：

- `(SourceStartExclusive, SourceEndInclusive]` 是本 epoch 真正被吸收、会推进 memory anchor 的区间；
- `(SourceEndInclusive, PlannedAtRawHead]` 是仍留在 online raw suffix 中的 retained recent
  lookahead；
- lookahead 只帮助 maintainer 用“现在已经知道什么”重新判断旧信息价值，不能被 artifact 声称为
  已吸收 source；
- 两段 history、setup 与 hash assertions 都必须由 SessionJournal bounded API 可复现；
- non-genesis epoch 必须 exact join previous epoch：

  ```text
  Current.BranchRefId == Previous.BranchRefId
  Current.EpochStreamId == Previous.EpochStreamId
  Current.SourceStartExclusive == Previous.SourceEndInclusive
  Current.RawStartSetups == authoritative setups at Previous.SourceEndInclusive
  ```

- `SourceEndInclusive` 与 `PlannedAtRawHead` 必须都在 captured selected branch lineage 上，且顺序合法；
- planner 可以独立保存 shared epoch facts，但 online lifecycle 只有在 active core domain 已发布
  `CommonAnchor == PreviousEpoch.SourceEndInclusive` 的 input set 后，才为 successor epoch 创建 job；
- epoch 不引用 domain-specific `InputSetId`，不固定 topology，不运行 producer，也不发布 set；
- epoch latest pointer 是同一 stream 内的 rebuildable index。

### 3.4 MaintenanceJob

`MaintenanceJob` 表示“一个 coherence domain 在 exact epoch 上，按一套 immutable slot
specification 生产下一份 coherent set”的 durable 工作定义。

```text
MaintenanceJob {
  MaintenanceJobId
  EpochId
  CoherenceDomainId
  InputSetId?
  MaintenancePolicyId
  MaintenancePolicyFingerprint
  RoleSpecs[]
}
```

`MaintenanceJobId` 是唯一 job identity，同时承担 content commitment、文件名和 foreign key。
不再同时持久化 `JobFingerprint + TransactionId`。

一个 job 可以由 root `MaintenanceJob` fact 定义，也可以作为 `JobReprovision` 中内嵌的
replacement job 定义；两种 carrier 使用同一 canonical job identity 和 validation，不形成两套
领域实体。

canonical job identity 至少包含：

- exact epoch identity；
- stable coherence domain；
- exact previous domain set，genesis 时为 null；
- maintenance policy id/fingerprint；
- canonical ordered stable `RoleSpec[]`。

branch/ref、epoch stream 与 raw interval 从 immutable Epoch strict join 取得，不在 job 中影子复制。
`InputSetId` 属于 domain-local dependency，因此保存在 job 中；non-genesis job 必须满足：

```text
InputSet.CoherenceDomainId == Job.CoherenceDomainId
InputSet.CommonAnchor == Epoch.SourceStartExclusive
InputSet.AnchorSetups == Epoch.RawStartSetups
InputSet.OriginEpoch.BranchRefId == Epoch.BranchRefId
InputSet.OriginEpoch.EpochStreamId == Epoch.EpochStreamId
```

此外，InputSet 必须是 exact `BranchRefId + CoherenceDomainId` lineage 上的 immutable node；job
创建时它必须是该 domain 在 `Epoch.SourceStartExclusive` 的 current unique tip。上述完整 join
必须在任何 producer 调用前验证，不能等到 publication 时才发现 prior memory 来自错误 branch、
setup 或 epoch stream。

同一 `EpochId + CoherenceDomainId + InputSetId` 可以因显式 reprovision 形成一条 chain，但任一
时刻只能有一个 active leaf；未由原子 `JobReprovision` 连接的 competing root jobs 是 concurrency
error，不能按文件名或创建时间任选。

明确不包含：

- physical attempt id/ordinal；
- attempt start time、failure、call log；
- produced artifact/content；
- settlement/finalization/publication 状态；
- request-local publication view 或 `NthPrevious`。

### 3.5 RoleSpec 与 stable MemorySlotId

`RoleSpec` 固定一个 logical memory slot 在该 job 中应如何被生产：

```text
RoleSpec {
  MemorySlotId
  ProfileId
  Target
  ProducerId
  ProducerFingerprint
  PromptFingerprint
  ModelFingerprint
  MaxContentUtf8Bytes
}
```

规则：

- `MemorySlotId + Target` 跨 maintenance policy、profile、prompt 和 model revision 保持稳定；
- 同一 domain lineage 内，某个 `MemorySlotId` 一旦出现就永久绑定同一 `Target`；换 Target 必须
  使用新的 `MemorySlotId`，remove 后 re-add 也不得绕过该约束；
- profile/prompt/model 改变会产生新 job/spec revision，但不会产生新的 domain lineage；
- 同一 job/domain 中不得有重复 `MemorySlotId` 或重复 `Target`；
- 首版一个 job 中声明的全部 slots 都是该 coherence domain 的 required closure；不实现 optional
  omission；
- `MaxContentUtf8Bytes` 进入 job identity，producer result 在 artifact/settlement 前按 exact UTF-8
  bytes 验证；
- previous input set 中同 `MemorySlotId` 的 block 是该 role 的 prior state；新增 slot 从空 block
  或 concrete profile 定义的显式 seed 开始，移除 slot 只影响未来 set，不改写历史；
- 首切不实现 `VariantId`、`SelectExisting`、跨 job artifact reuse 或 arbitrary candidate identity；
  unchanged content 仍是一次合法 producer success，不需要另一套 identity 模型。

### 3.6 RoleAttempt

`RoleAttempt` 表示一次真实 producer invocation，不参与 `MaintenanceJobId`。

一个 attempt 至少由两份 immutable fact 表达：

```text
RoleAttemptIntent {
  AttemptId
  MaintenanceJobId
  MemorySlotId
  Ordinal
  PreviousAttemptId?
  RecoveryReason
  ProducerExecutionIdentity
}

RoleAttemptOutcome {
  AttemptId
  Status = Succeeded | Failed | Canceled
  ArtifactId?
  SanitizedFailure?
  InvocationProvenance?
}
```

合法状态真值表：

| Status | ArtifactId | SanitizedFailure | InvocationProvenance |
|---|---|---|---|
| `Succeeded` | required | absent | required |
| `Failed` | absent | required stable reason code，可附 sanitized detail | optional/required 由 failure point 决定 |
| `Canceled` | absent | absent 或 stable cancellation detail | optional |

`Uncertain` 不可持久化成 outcome status；它严格表示 intent 已 durable、outcome 仍 absent。
`ContentBudgetExceeded` 是一种 stable failed reason：writer 不创建 artifact、不 settlement，允许新
attempt 或显式 reprovision。

规则：

- `AttemptId` 由 repository 分配，调用方/CLI 不提供 arbitrary `--attempt-id`；
- identity 至少绑定 `MaintenanceJobId + MemorySlotId + Ordinal`；
- ordinal 通过 immutable intent 的 atomic create 分配；并发创建冲突者改用下一 ordinal，不覆盖；
- attempt intent 必须在调用 producer 之前 durable；
- producer 调用不持有 repository write lock；
- outcome 只能写一次；
- failed/canceled attempt 没有 settlement；
- intent 存在但 outcome 不存在表示 `Uncertain`，不是伪造的 success/failure；
- unsettled role 可以创建引用 uncertain predecessor 的后继 attempt；
- concurrent attempts 可能造成额外 LLM 成本，但 settlement CAS 只能选择一个 winner；
- 本 generation 不承诺 provider-side exactly-once。

### 3.7 Artifact

`Artifact` 是一次成功 attempt 的 immutable、content-addressed 结果：

```text
Artifact {
  ArtifactId
  OriginMaintenanceJobId
  OriginMemorySlotId
  OriginAttemptId
  Target
  Content
  ContentIdentity
  AbsorbedRawRangeAndSetupProvenance
  LookaheadRawRangeAndSetupProvenance
  Producer/InvocationProvenance
  Outcome
}
```

规则：

- artifact identity 包含 exact content 和 provenance；
- artifact 的 epoch/input/policy authority 通过 `OriginMaintenanceJob -> Epoch` join 取得，不保存
  一套可漂移的 shadow identity；
- artifact 必须回指其 origin job 下一个 durable attempt intent；
- artifact 的 absorbed range 必须 exact 结束于 `Epoch.SourceEndInclusive`；lookahead provenance
  可以结束于 `Epoch.PlannedAtRawHead`，但不推进 artifact/set anchor；
- `Content` 的 UTF-8 bytes 不得超过 exact RoleSpec `MaxContentUtf8Bytes`；
- successful attempt outcome exact 指向 artifact；
- crash 在 artifact 写入后、outcome/settlement 前发生时，恢复流程应能发现并验证唯一 artifact，
  补写 outcome/settlement，而不是重跑 producer；
- artifact 按 job/role/attempt 分区保存；同一 attempt 出现多个 success artifact 属于 ambiguous
  corruption，不能任意挑选；
- settlement 选择的 artifact origin 必须是当前 job/slot attempt；不允许跨 branch、跨 epoch、
  跨 input dependency 偷用内容。

### 3.8 RoleSettlement

`RoleSettlement` 是“该 job 的该 role 已成功闭合”的 durable 事实：

```text
RoleSettlement {
  MaintenanceJobId
  MemorySlotId
  AttemptId
  ArtifactId
}
```

规则：

- 每个 `job + memory slot` 至多一个 settlement；
- settlement 必须 exact join 当前 RoleSpec 与 successful attempt outcome；artifact 再按
  origin/content-budget/provenance 规则验证；
- settlement 使用 atomic create/CAS；
- settlement 后不得再为该 job/role 调用 producer；
- 并发 loser artifact 可以保留为 orphan diagnostic fact，但不能改写 settlement 或进入 set。

### 3.9 JobReprovision

普通 failure/uncertain recovery 必须在同一 job 内产生 successor attempt；不能用新 job 逃避
settlement 复用。只有 frozen provisioning 被证明不可完成，例如稳定的 content-budget failure
需要修改 prompt/model/budget 时，才允许显式 reprovision：

```text
JobReprovision {
  SupersededJobId
  ReplacementJob { ...full canonical MaintenanceJob... }
  ReasonCode
}
```

规则：

- 一个 atomic immutable file 同时 commitment old→new edge 与完整 replacement job；不存在
  “replacement durable、edge 缺失”的中间状态，也不另写 standalone replacement job fact；
- replacement 必须 exact 使用同一 `EpochId + CoherenceDomainId + InputSetId`；
- replacement 必须保持 exact ordered `MemorySlotId + Target` roster；首切只允许修改
  profile/producer/prompt/model/content-budget/maintenance-policy 等 execution provisioning；
- replacement provisioning 必须与 old job 不同，不能制造等价 job；
- reprovision 只能发生在 old job finalization 前；finalized/published job 不可 reprovision；
- reprovision 与 finalization 在同一短 repository lock/CAS boundary 下互斥；两者同时存在属于
  corruption；
- reprovision durable 后，old job 禁止新 attempt、settlement 与 finalization，pending-first
  转向 replacement；
- old job 已 durable 的 attempts/artifacts/settlements 保留用于 validation，但不自动移植到
  replacement；显式 reprovision 可能重跑已成功 producer，这是修改 frozen job definition 的代价，
  不违反“同一 job settled slot 不重跑”；
- 同一 job 至多一个 `JobReprovision`，replacement chain 必须 acyclic、最终只有一个 active leaf；
- 文件以 `SupersededJobId` 定位；重试相同 replacement 是 idempotent，不同 replacement 是
  concurrency error；
- 这是解除不可完成 frozen job 的窄 recovery primitive，不是动态 maintainer registry、
  pause/retire 或通用 workflow engine。

### 3.10 Finalization

`Finalization` 是 job closure intent，不重复 job 可推导字段：

```text
Finalization {
  MaintenanceJobId
  AnchorSetups
  IncludedRoles[]  // role + artifact；attempt/outcome 由 immutable settlement/artifact 推导
  ExpectedSetId
}
```

`InputSetId/expected previous set`、domain 与 policy 从 immutable job 取得；branch、epoch stream、
raw interval 从 `job -> immutable Epoch` 取得。

规则：

- `IncludedRoles` 必须 exact 覆盖 job 的全部 declared RoleSpecs；
- included role 必须 exact join durable settlement；
- superseded job 不得 finalization；
- finalization immutable、canonical、唯一；
- finalization 写入后禁止创建新 attempt 或 settlement；
- `ExpectedSetId` 必须能由 job + finalization 重建验证；
- finalization 后的 reopen 只走 publication completion。

### 3.11 ArtifactSet

`ArtifactSet` 是 online 可见的唯一 coherent publication unit：

```text
ArtifactSet {
  SetId
  MaintenanceJobId
  CoherenceDomainId
  PreviousSetId
  CommonAnchor
  AnchorSetups
  Members[]  // canonical role + artifact references
}
```

规则：

- `SetId` 是 job、previous set、anchor/setup 与 canonical members 的 identity hash；
- `PreviousSetId == Job.InputSetId`，genesis 时为 null；
- `CommonAnchor == Job.Epoch.SourceEndInclusive`，AnchorSetups 必须等于该 exact epoch end 的
  authoritative paired setup；
- members 必须来自 finalization included roles；
- set 不重复 branch/policy/epoch、完整 RoleSpec、attempt 或 artifact content metadata；strict validation
  通过 `set -> job -> epoch` 与 `set -> artifact` joins 取得；
- set lineage key 固定为 stable `BranchRefId + CoherenceDomainId`；maintenance policy、
  prompt/model 与 membership revision 可以在相邻 nodes 间改变，不切断 lineage；
- 同一 domain lineage 的所有 nodes 必须经 `set -> job -> epoch` join 到同一
  `BranchRefId + EpochStreamId`；首个 set 隐式绑定 stream，后继 set 不得迁移到另一 stream；
- policy/topology 不兼容且确实需要新历史时，必须显式创建新的 `CoherenceDomainId`，不能用
  fingerprint 隐式 fork；
- lineage key 从 job/epoch authority 推导，latest index 可以缓存但不能取代 join validation；
- publication 前再次验证 current selected raw lineage authority；
- immutable set 文件先写入，latest pointer 后 CAS；
- pointer 更新前旧 latest 继续可用；
- partial settlements/finalization 永不被 candidate provider 暴露；
- latest pointer 丢失可从 immutable set lineage 重建；
- divergent/cycle/missing parent fail-fast，不跳过损坏 set。

## 4. Orchestration 状态机

```text
EpochPlanned
  -> JobCreated
  -> SlotUnsettled
       -> AttemptIntentDurable
       -> ProducerRunning
       -> AttemptFailed --------------------+
       -> AttemptUncertain -----------------+-> NewSuccessorAttempt
       -> ArtifactDurable
       -> AttemptSucceeded
       -> SlotSettlementDurable
       -> ProvisioningUnrecoverable
            -> JobReprovisionDurable -------+-> Replacement JobCreated
  -> DomainSlotsClosed
  -> FinalizationDurable
  -> SetFileDurable
  -> LatestPointerCAS
  -> Published
```

关键单调性：

- attempt facts append-only；
- settlement 只从 absent 变成 present；
- reprovision 只从 absent 变成 present，并使 old job terminal；
- finalization 只从 absent 变成 present；
- set immutable；
- latest pointer 可变但可重建；
- finalization 是 producer execution 与 publication completion 的不可逆分界线。

## 5. Crash 与并发语义

| crash/failure point | reopen 行为 |
|---|---|
| attempt intent 前 | slot 仍 missing，可创建首个 attempt |
| intent 后、producer 未知 | 旧 attempt 为 uncertain；允许显式 successor attempt |
| producer 抛错且 failure outcome durable | slot 仍 unsettled；创建下一 ordinal attempt |
| LLM 已返回、artifact 尚未 durable | 可能重复调用；不宣称 exactly-once |
| artifact durable、success outcome/settlement 前 | 发现并验证 artifact，补 outcome/settlement，不重跑 producer |
| settlement durable 后 | 永不再调用该 slot producer |
| 部分 slots settlement | 只运行其余 unsettled slots；不发布该 domain set |
| `JobReprovision` atomic create 前 | old job 仍 active |
| `JobReprovision` durable | old job terminal；embedded replacement 成为 active leaf |
| finalization durable、set 缺失 | 不运行 producer；重建并发布同一 `ExpectedSetId` |
| set durable、pointer 缺失 | 验证 set 后 rebuild/CAS pointer |
| latest 已是该 set 或同 key 后代 | short-circuit success，不回退 pointer |
| latest divergent | concurrency error，fail-fast |

并发承诺：

- repository write lock 只保护短时 immutable file creation、settlement/finalization 与 pointer CAS；
- producer/LLM 调用不持锁；
- generation 内保证 settlement/finalization/publication 的 single-winner correctness；
- 不保证昂贵 producer 调用在多进程竞争或 uncertain crash 下物理上 exactly once；
- 若未来需要 provider-level exactly-once，必须引入 provider idempotency/query contract，不能仅靠本地 lease
  冒充确定性。

### 5.1 topology revision 与未来 lifecycle seam

V3 首切只运行静态 `roleplay-core` roster，但当前 schema 必须允许相邻 job/set 使用不同
RoleSpecs，而保持同一 stable domain lineage：

- add slot：新 job 声明新 `MemorySlotId`，以空 block/显式 seed 启动；
- remove slot：新 job 不再声明该 slot；旧 set/artifact 仍可验证；
- change profile/prompt/model/budget：stable slot 不变，job revision 改变；
- `NthPrevious` 可以跨上述 revision 继续沿 `PreviousSetId` 回看。

动态控制 API、pause/resume/retire 与跨 domain composition 不在首切实现。首切只实现上一节用于
修正不可完成 frozen provisioning 的窄 `JobReprovision`。
未来语义至少遵守：

- pause 只停止未来 scheduling；
- retire/tombstone 不删除历史事实；
- 已 finalization job 必须完成 publication；
- 不同 coherence domain 的 pending job 不得永久阻塞 core domain；
- 若未来要无 replacement 地放弃未 finalization job，必须新增 append-only disposition，不能复用
  `JobReprovision` 或删除文件伪造“从未发生”。

这些是 extension seam，不是要求 N1～N9 顺手实现完整动态 registry。

## 6. 存储外观

目标根目录：

```text
derived/memory/v3/
  manifest.json
  planner/
    configs/
    current/
    epochs/
    latest/
  jobs/
    roots/
    reprovisions/  # keyed by superseded job id; embeds replacement job
  attempts/
    <job-id>/
      <slot-key>/
        <attempt-id>.intent.json
        <attempt-id>.outcome.json
  artifacts/
    <job-id>/
      <slot-key>/
        <attempt-id>/
          <artifact-id>.json
  settlements/
    <job-id>/
      <slot-key>.json
  finalizations/
    <job-id>.json
  sets/
  indexes/
    latest-sets/
```

规则：

- v3 不读取 `derived/memory/v2/`；
- schema/domain hash 必须显式版本化；
- point read 在 `File.Exists` 前做 safe-descendant/symlink/reparse guard；
- immutable write 使用同目录 temporary file + atomic move/create；
- 文件大小在 deserialize 前检查，writer 使用同一 UTF-8 byte limit；
- persisted DTO 使用 strict unknown-member rejection；
- identity file 必须验证 filename、schema、canonical hash 与 dependency closure；
- epoch/set latest indexes 全部可从 immutable facts rebuild；current provisioning pointer 只能从
  已 provisioned config lineage 或外部 configuration authority 恢复；
- wall-clock timestamp 仅用于诊断，不参与 logical identity。

## 7. Publication version selection 与 request projection

published set lineage 继续使用：

```text
ArtifactSet -> PreviousSetId
ArtifactSet -> CommonAnchor(raw EventAddress)
```

selection 分两层：

```text
StaticPublicationView
  -> select exact coherence domain lineage(s)
  -> apply NthPrevious(n) to each selected lineage
  -> validate compatible common anchor
  -> materialize/compose exact contributions
```

首版 contract：

1. `StaticPublicationView = roleplay-core/full`，只选择 core domain 全部 members；
2. 按 exact `BranchRefId + CoherenceDomainId` 找 published lineage tip；
3. `NthPrevious(0)` 选择 tip；
4. `NthPrevious(n)` 严格沿 `PreviousSetId` 走 n 次，且可以跨 maintenance policy/topology revision；
5. 返回 content-free descriptor；
6. materialization 才读取 exact member artifacts；
7. SessionJournal 复核 anchor/setup/source assertions；
8. lineage 太短返回 `OrdinalUnavailable`；
9. 空 lineage 只返回 `EmptyLineage`，bootstrap eligibility 仍由 raw topology 决定；
10. 损坏、cycle、missing parent 不得伪装成 empty/short lineage；
11. 不实现 Latest/Budgeted 并列策略或 request-size fallback。

未来 extension seam 可以让显式 request context 选择 work/private/client domains 或 member subset，
但必须遵守：

- `NthPrevious` 永远只表示时间深度，不承载 domain/filter 语义；
- selector 只读取已发布 immutable sets，不参与 maintenance transaction；
- 多 domain composition 只有在 common anchor/setup exact compatible 时才可 materialize，否则
  显式 not-ready；
- Prepared 保存最终实际选择的 exact contributions，reopen 不重新分类；
- V3 不提供自动情境分类、semantic ranking、vector/graph retrieval 或隐式 budget fallback。

### 7.1 遗忘与历史回看

- latest artifact 中不再出现的信息，视为正常 online semantic forgetting；
- immutable old artifacts/sets 可以继续存在，用于 validation、audit 与显式 `NthPrevious(n > 0)`；
- `n > 0` 是 Agent 有意回看旧认知，可能重新暴露最新 memory 已淡忘的信息；
- 单 slot 物理 purge、隐私擦除和 derived GC 不在本 generation 承诺内。

## 8. Validation 与运维

strict validation 必须覆盖：

- generation manifest、schema、filename、canonical identity；
- planner config/epoch lineage；
- epoch absorbed interval、retained lookahead 与 exact setup/hash authority；
- job identity、stable domain/slot identity 与 exact RoleSpec；
- attempt ordinal/parent chain、intent/outcome；
- artifact 与 attempt/job/epoch/input dependency、content budget、absorbed/lookahead provenance；
- settlement 与 exact attempt/artifact；
- job reprovision exact roster/replacement compatibility、single active leaf 与 acyclic chain；
- finalization exact slot closure；
- set 与 finalization/job/members；
- 跨 maintenance policy/topology revision 的 set lineage、latest pointer 与 selected raw branch；
- static publication view、`NthPrevious` 与 materialized exact contributions；
- unexpected files、size limit、symlink/reparse、hash collision。

运维 facade 至少提供：

- validate exact bound branch；
- validate all active refs；
- rebuild planner pointers；
- rebuild latest set indexes；
- list domains/jobs/attempts/settlements/reprovisions/finalizations/sets 的窄报告；
- 显式 reprovision 不可完成 frozen job，并报告可能重跑的 settled slots；
- 删除整个 v3 后，从 raw history 加 configuration authority 重新 provision、规划和维护。

validation report 不输出完整 prompt、memory content、raw arguments 或 provider secrets。

## 9. 明确非目标

本 generation 不承担：

- 修改 raw SessionJournal event wire 来保存 derived id；
- 为旧 `derived/memory/v2` 提供 migration/compatibility reader；
- provider-side exactly-once；
- 任意跨 branch artifact/set 复用；
- 自动 budgeted context fallback；
- vector/graph retrieval；
- 自动 work/private/client 分类、semantic ranking 或通用查询 DSL；
- 完整动态 maintainer registry、pause/resume/retire UI 与跨 domain orchestration；
- 单 slot immutable history 物理擦除、隐私 purge 或 GC；
- `VariantId`、`SelectExisting` 与跨 job alternative artifact adoption；
- 把 full raw replay 放回 online request path；
- 把 concrete maintainer profile 放进 raw core；
- 通过永久保留 `DerivedMemory2` 形成两套 production implementation。

## 10. 最终验收

目标成品只有在以下条件全部成立时完成：

- 最终 solution 中只有正式 `SessionJournal.DerivedMemory`，没有 `.Next`/`DerivedMemory2`；
- production 不引用旧 DerivedMemory project/namespace/root/schema；
- `MaintenanceJobId` 是唯一 stable job identity；
- stable `MemorySlotId` 在 prompt/model/policy revision 后保持不变；
- physical attempt 不参与 job identity，CLI 不接收 job-level `AttemptId`；
- failed role 新 attempt 不改变 job id，其他 role settlement 保持有效；
- settled role crash/reopen 不调用 producer；
- artifact-durable/settlement-missing crash 可补 settlement 而不调用 producer；
- stable content-budget failure 可通过 durable job reprovision 切换到同 epoch/domain/input 的
  replacement provisioning，old job 不再阻塞 pending-first；
- domain slot closure 前无 set publication；
- finalization 后无 producer path；
- atomic set/latest publication 保持旧 set 可用；
- policy/prompt/model/roster revision 产生新 job/set，但不断开 stable domain lineage；
- `NthPrevious` 可跨上述 revision exact traversal，且只表示时间深度；
- static core publication view 与 raw authority validation 通过；
- maintainer 能读取 bounded retained recent lookahead，同时 artifact/set anchor 只推进到
  `SourceEndInclusive`；
- 每个 role 的 content budget 被 settlement 前强制执行，多 epoch 长跑不会因 block 无界增长而
  永久 not-ready；
- 基于 Galatea 代表性轨迹的 maintenance → reopen → materialize 行为测试能延续基本身份、
  重要关系、长期计划和未完成话题；
- 删除 `derived/memory/v3/` 后 raw audit/Prepared reopen 不受影响；
- architecture、corruption、failpoint、long-history、CLI E2E 和 solution build 全部通过。

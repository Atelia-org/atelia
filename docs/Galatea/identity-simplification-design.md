# Galatea / RecapGrid 身份与恢复校验简化设计

> 状态：§5 已实现并完成唯一 Dev 实例 E2E；§6.2 [Control 回执简化](control-receipt-simplification-plan.md)与[前置输入对象简化](recap-prior-input-simplification-plan.md)均已实现并通过本地验证，尚未部署。前置输入代码见 `258125fd`，验证见实施记录；后续采用“丢弃旧 Recap，全部重构完成后统一重建”；[Store 简化](recap-store-simplification-plan.md)已实现，25 个项目的 1,771 项本地测试通过，尚未部署。首轮代码见 §10，首轮 E2E 见 §11。
>
> 日期：2026-09-14。首轮代码基线：`277baeea`；Store 规划基线：`4f718d87`；下一切片规划基线：`6d87a4c1`。本文区分目标设计、当前实现和历史验证；[Timeline 单一行身份计划](timeline-row-identity-simplification-plan.md)已完成辩证审查、待实施。本轮只维护计划，不执行清库、部署或真实模型重建。

## 1. 目标与最小模型

用少量明确的身份表达：**哪段历史、哪种摘要规则、哪个已落盘结果、哪次操作**。以构建位置复用已提交摘要，保留必要的持久恢复内容，取消把正常修复和升级变成恢复失败的手工 adapter 指纹门槛。

目标模型：

```text
HistoryRow：不可变历史区间 + 前驱
Definition / Recipe：不可变规则快照 + 构建关系
CellSlot：(RecipeDigest, HistoryRowId, LogicalColumnId)，普通结构坐标
Cell / RowResult：Store 分配的普通随机 ID + UNIQUE / FK
Head：实例身份 + 单调 generation
OperationReceipt：稳定操作 ID + 命令匹配依据 + 结果
Prepared：已准备的逻辑请求输入 + 必要调用目标 + 一处最终内容校验
```

原始历史、不可变规则和已生效操作是保留对象；旧 Recap 物化结果可整体丢弃。所有重构完成后，最后统一调用 LLM 重建，不要求新旧正文相同。正常运行时，同一 CellSlot 仍沿用首个已提交结果；不再承诺跨 recipe 的同输入或同正文自动共享。Overlay 的显式 base-cell 复用保留。

这些是业务概念，不要求新建 framework/API。hash 不再默认成为每层对象的公开身份、持久格式和恢复兼容合同。

## 2. 需求台账与证据等级

| 编号 | 要求或判断 | 来源 |
|---|---|---|
| U1 | 个人自用、未发布、无下游，优先简单、及时重构 | 用户与根 AGENTS.md，明确 |
| U2 | 不需要应用层防恶意篡改；质疑 hash/fingerprint 的数量与价值 | 本次用户，明确 |
| U3 | 设计经 dialectical-simplification 完善后已获采纳；授权带领 subagents 实施、维护文档并按需提交 | 2026-09-14 用户后续指令，明确 |
| U4 | 允许清空旧 Recap 内容；全部重构代码完成后最后统一 LLM 重建，不保旧正文或旧缓存命中 | 用户最新明确决定，替代先前的旧摘要迁移要求 |
| B1 | 原始会话、规则、工具回执、冻结请求保留；新 Store 正常重启仍保已提交结果 | 当前恢复消费者；U4 只放弃重构切换时的旧摘要缓存 |
| B2 | 摘要不可混用历史区间、规则和前驱；同一构建位置的重试沿用首个结果，Overlay 显式复用保留 | Store/Manager 当前调用链；缓存粒度按 U4 简化 |
| B3 | 操作已生效、会话结果尚未提交时，恢复不能重复应用副作用 | Control receipt 与 SessionJournal 工具执行链 |
| B4 | 不同用户/仓库不能误绑定；真实工具权限和原生 provider 载荷解析边界仍有效 | 当前执行与隔离消费者；U2 不等于取消业务权限 |
| D1 | 重试沿用原模型与已准备 prompt/history/tools，允许当前修好的 adapter 执行；不承诺旧 HTTP wire 逐字不变 | 本设计建议，已随用户采纳整体方案而确认 |
| D2 | 撤销跨来源同正文自动复用要求；以 recipe/历史行/列为构建位置，删除独立求值 hash 链 | 用户接受需求精简；本次源码审查确认不可变规则与唯一前驱足以确定输入 |

B 项说明真实需求，不把现有测试对每一个字段的断言升级成不可修改的产品律。实施采用 D1 的恢复语义。

故障模型：进程崩溃、重启、正常部署修改、重复回调/重试、并发构建及跨持久组件提交窗口。事务、文件发布和输入关系校验继续承担这些故障。没有引入攻击者同时修改内容与 hash 的防护需求，也没有承诺外部模型/工具 exactly-once。

## 3. 基线与已完成工作

- `e8b8a43b`：Anthropic Models API 404/405/501 回退；用户确认恢复可用。一次不必要的 adapter fingerprint 升级曾阻塞旧 Prepared，最终提交保留原 identity。
- `7ab556d6`：route manifest 的 operator JSON 已改用宽容排版的 `ParseJson`；空白、字段顺序不影响配置加载，schema/重复字段仍校验。**不再把这件事列入待办。**
- `277baeea` 及其前序委派提交：已实现有限恢复、已知未发送与结果不明的区分。见[委派恢复方案](codex-delegation-recovery-refactor-plan.md)。本方案不修改恢复预算、不自动重发结果不明的委派，不把该方案的阶段授权移用到这里。

实施前基线证据入口（`277baeea`；§5 涉及的旧符号已在实施中删除）：

| 机制 | 代码与消费者 | 结论强度 |
|---|---|---|
| Adapter 指纹 | [CompletionDispatchIdentity](../../src/Completion/CompletionDispatchIdentity.cs)，[Registry.BindExact](../../src/Completion/CompletionConnections.cs)，[Galatea BindPrepared](../../prototypes/Galatea/GalateaRecapGridComposition.cs) | 已确认：hash 的是手工 mapping 标签，不是实际 adapter 代码/HTTP 请求 |
| 恢复二次比对 | [SessionJournalEngine](../../prototypes/SessionJournal/SessionJournalEngine.cs) `ValidateRecoveryRuntimeCompatibility`、[RuntimeRecovery](../../prototypes/SessionJournal/SessionJournalEngine.RuntimeRecovery.cs) `CreateFrozenCompletionRequirement` | 已确认：不只 Registry 一处检查；只绕过 Host 门槛不足以正确完成变更 |
| 同 body 双身份 | [HistoryTimelineCanonicalCodec](../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelineCanonicalCodec.cs) `RowIdDomain` / `DescriptorDomain` | 已确认：同 descriptor body 换 domain 计算两遍 |
| 结果身份与唯一性 | 基线 `Store/SchemaV2.sql`（已退役；当前为 [SchemaV3.sql](../../prototypes/SessionJournal.RecapGrid/Store/SchemaV3.sql)）、[SqliteRecapGridStore](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs) | 已确认：cell 的 evaluation key 唯一；row 的 `(ref,timeline,recipe,row)` 唯一 |
| 摘要缓存输入 | [ManagerRowBuild](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs)、[BuildContracts](../../prototypes/SessionJournal.RecapGrid/Abstractions/BuildContracts.cs) | 已确认：缓存区分规则、历史和前置摘要内容 |
| 操作结果摘要 | [ControlOperationCanonicalizer](../../prototypes/SessionJournal.RecapGrid/Control/ControlOperationCanonicalizer.cs)、[ControlRuntime](../../prototypes/SessionJournal.RecapGrid/Control/ControlRuntime.cs) `TryReplay` | 已确认：ResultIdentity 不参与操作判重；receipt 参与 |
| Prepared 内容证明 | [Manifest](../../prototypes/SessionJournal/SessionRequestManifest.cs)、[Reconstructor](../../prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs) | 已确认：局部 hash 与最终 commitment 并存；最终检查可发现重构变化，但该故障场景目前是结构推论 |

## 4. 决策总表

| 对象 | 判定 | 最小机制与边界 |
|---|---|---|
| RequestAdapterFingerprint | delete | 删除执行硬门槛及当前身份字段，不用另一个手工标签或常量 hash 替代 |
| ConnectionFingerprint | defer | 第一切片保持当前行为；改变 endpoint/reasoning 后如何恢复是另一项产品选择 |
| HistoryRowId + DescriptorDigest | merge | 保留一个不可变 HistoryRowId，暂沿用现有 RowId 算法；无需顺便换 UUID |
| CellDigest / RowViewDigest | simplify，已完成 | 改普通 CellId/RowResultId，保留唯一约束、前驱及成员 FK |
| Content / Projection / Evaluation 三层摘要 | delete | 用普通 CellSlot 关联工作与首个结果，不计算新的缓存 hash；前驱和规则由不可变关系确定并校验 |
| Control ResultIdentity | delete | 回执返回稳定操作/回执 ID，保留命令匹配与已应用结果 |
| ControlStateDigest | defer | 可从在线 CAS 移除；需确认所有写入都递增 generation，再独立处理 |
| Prepared ContextSnapshot 的重复 ContentSha256 | simplify | 后续删同份快照的重复证明；保留最终逻辑请求 commitment |
| Prepared raw/setup/tool 局部 hash | defer | 执行元数据不全在最终 prompt 内，不能据“最终也有 hash”全部删除 |
| 整 catalog / ToolRuntime fingerprint | defer | 有历史 pending Action 消费者，保留真实命令版本/权限/receipt；不以 fresh 路径没有该工具作为删除证明 |
| operator JSON canonical 排版限制 | completed | `7ab556d6` 已修；内部缓存编码稳定性不等于要求用户写 canonical JSON |

不把所有 hash 清零作为验收目标。每项删除必须减少真实概念/重复状态/检查路径，不能仅把 `Digest` 重命名成 `Id` 后保留全部派生层。

## 5. 第一实施切片：取消 adapter 标签的恢复否决权

### 5.1 行为合同

依据已采纳的 D1：旧 adapter 标签差异不再构成 Prepared 恢复的拒绝理由。模型、prompt、history、可见工具定义及最后执行序号仍来自持久输入；能力查询和 provider request 转换使用当前代码。真正不兼容的升级仍可能被其余绑定或载荷检查拒绝，不承诺任意升级都能恢复。

- connectionId 必须精确查找，不回落默认连接。
- 第一切片保留 ConnectionFingerprint、client name、API spec、原生 reasoning Origin/type 和工具权限检查。**不顺便允许切换模型、endpoint、reasoning 或工具实现。**
- `StartedOutcomeUncertain` 仍须既有的明确重试授权；删 fingerprint 不产生自动重试权。
- capability 404 回退等 adapter 修复不再因手工版本标签阻塞恢复。
- 其余绑定与载荷解析保持既有错误行为；只清理已删除的 mismatch reason 映射，不新增通用兼容协商或异常分类体系。
- 保留最终 provider-neutral request commitment，不把它宣称成 HTTP wire 一致性证明。

### 5.2 当前类型与持久格式

目标从 `CompletionDispatchIdentity`、`SessionCompletionTargetIdentity` 移除 `RequestAdapterFingerprint`，同时删除 `ComputeRequestAdapterFingerprint` 及只为它存在的 reasoning/output/projection mapping 标签。保留实际 provider 配置与载荷转换逻辑。

新 Prepared writer 使用下一可用 body schema（以当前基线为 v8），target 不写 adapter fingerprint；逻辑请求 canonical codec 不因删除 target 元数据而升级。实施前若另一会话已占用该 schema，顺延编号并更新本文。

版本边界：v5/v7/v8 可读与审计；只有 v7/v8 可进入当前执行路径。v5 继续沿现有只读审计边界，不借机新增其可执行恢复。

旧布局分支只存在于 codec：v5/v7 共用带 adapter 字段的 target 读取逻辑，校验字段形状后立即丢弃该值；v8 读取新布局。v7/v8 都产生同一个当前 `CompletionRequestPreparedBody`，不新增 `HistoricalPreparedV7Body`、独立重建器或逐版迁移框架。v5 的既有历史 body 保留 legacy max_tokens 审计用途。

旧事件原始字节、地址、commitment 不改写；保留真实来源 `BodySchemaVersion` 供分派与诊断，不能把读入的 v7 冒充 v8。v7 与新格式共用逻辑请求重构；旧 adapter 字段不进入运行身份或相等判断。

这不是保留双执行协议：只有一个当前执行模型；兼容读取仅因已有真实 v7 会话存在。旧格式读取不能伪造“当前代码具有旧 fingerprint”，不能把 runtime 的 identity 强行替换为 required identity 以骗过 record 相等。

### 5.3 必须走通的改动链

```text
Completion identity / BindExact
  → Galatea 与 CLI 的 Prepared target 映射
  → SessionEventCodec + manifest target 布局读取 + ManifestView
  → Prepared 重构 + runtime recovery snapshot + 历史审计分派
  → ValidateRecoveryRuntimeCompatibility
  → 原生 provider 请求与 tool runtime 的既有检查
```

代码入口包括 `GalateaServices.CreateRecapGridRuntime` 的 target 构建、`GalateaRecapGridComposition.BindPrepared`、`SessionJournal.Cli/CompletionTargetIdentityFactory`、`RecapGridOnlineTurnCommand`、`GalateaSseProtocol` 中旧 mismatch reason 映射。实施时按符号搜索核对实际函数名；不能只修本次触发错误的 Registry。

版本相关必查点：`SessionEventCodec` 的可读版本分派、`SessionPreparedManifestView.FromDecoded` 的 version/body 匹配、`SessionJournalEngine.RuntimeRecovery` 的可执行版本限制，以及 [SessionPreparedRequestAuditVerifier](../../prototypes/SessionJournal/SessionPreparedRequestV5HistoricalVerifier.cs) 的审计分派。其消费者包括 `SessionJournalAuditScanner` 和 `SelectedLineageAudit`。现有 [v5 codec](../../prototypes/SessionJournal/SessionRequestManifestV5HistoricalCodec.cs) 共用 target 读取器，改 v8 布局时必须明确让 v5/v7 使用旧布局投影。

Record 相等比较仍可用于归一化后的当前 target。Manifest 自身与其 recovery snapshot 的一致性检查继续存在，但双方必须经同一格式投影，避免“已删字段仍在另一层恢复失败”。

### 5.4 验收与边界反例

| 场景 | 预期 |
|---|---|
| 旧 v7 Prepared，只改变旧 adapter 标签；显式恢复 | 到达当前 Client，不被标签拒绝；逻辑请求 canonical bytes 不变 |
| 同上，Models API 404 | 使用已实现的回退，成功产生 Action |
| 已准备但未发送 / 已 Started 两种 crash 点 | 保留各自既有授权行为；不能把 Started 自动当未发送 |
| 删除连接或改变 ConnectionFingerprint 覆盖字段 | 仍在调用前明确失败，不使用默认连接 |
| 原生 reasoning 的 provider/API/model Origin 不兼容 | 仍拒绝非法载荷重放 |
| pending tool Action 或不同 tool runtime | 既有工具绑定与 receipt 语义不被放宽 |
| 新格式写入、冷重开并继续下一轮 | 新 writer 不再产生 adapter 字段；历史输入、结果和执行序号可读 |
| 已完成 v7 与新 v8 混合 lineage | 冷重开与完整历史审计均成功，不只验证 pending 恢复 |
| 历史 v5 样本 | 仍可审计，仍不能进入 provider 执行路径 |
| 真实非空 RecapGrid 的冻结恢复 | 不重新规划/生成摘要，重建逻辑请求与既有 Prepared 一致 |

至少一条测试必须走实际 Galatea Host → Registry → SessionJournal → 假 provider → Action 的生产链，并使用旧格式持久样本。只构造 identity record 或只断言异常消失不够。成功样本与负面样本都不使用真实账号。

## 6. 后续路线：丢弃旧缓存，完成重构后统一重建

已完成的前置输入切片见[实施记录](recap-prior-input-simplification-plan.md)，它在当时保持了旧 digest 和持久格式。其后 Store 切片已按新的可丢弃缓存要求完成；下文分别标明已完成部分与后继目标。

### 6.1 Timeline 行身份：下一实施切片

详细设计见 [Timeline 单一行身份计划](timeline-row-identity-simplification-plan.md)。保留原 HistoryRowId 算法及其 body 编码，删除同一 descriptor 的第二个 DescriptorDigest；外层格式升级不改变行 ID，不重新划分历史或改变 row frontier。

源码复查收窄了原先的迁移范围：Cadence 持久文件没有 DescriptorDigest，Recipe 正文本身也只引用 BootstrapThroughRowId。它们的格式和内容键保持；变化位于 Timeline 行记录、Control bootstrap 附加记录与 registration 命令、Store/构建及显示消费者。

Timeline 提供限定的离线格式升级，保留全部行（含非当前路径行）、原 head、policy 和 selected-path 结构；当前 reader 只读新格式。Control 复用既有旧格式 codec 投影，保留源 Head/bytes，下一次真实 mutation 才写新布局，不另建 Control/backup 转换器。旧 Recap Store 仍只在最终整体 Reset，不做结果图转换。

registration 的 domain 和整套工具 runtime 保持；只删 Recipe DTO 的重复字段，因此 Recipes 非空的命令摘要变化，family/definition-only 和 promotion 命令不变。最终切换前正常收敛受影响的 recipe registration，以及原有 Store 清空前要求收敛的 promotion；不改写 receipt 或冻结工具结果。精确语料、非空分叉旧样本和验收工作包见该计划。

### 6.2 Control 操作回执：已完成

`0e9524d6` 已删除派生 ResultIdentity，返回已有 OperationKey；保留 command/runtime/sequence、首次应用坐标和 receipt 原子发布。writer v3、旧 v2 codec 投影与 1,793 项本地验证见[实施记录](control-receipt-simplification-plan.md)。

这部分生效事实和规则图不属于可丢弃的 Recap 内容。清空 Store 不授权清空 Control、改变命令匹配或改写 Journal 工具结果。

### 6.3 Store 与构建位置：已完成

详细实现与验证见[Store 简化计划](recap-store-simplification-plan.md)。已落地的模型为：

```text
CellSlot = (RecipeDigest, HistoryRowId, LogicalColumnId)
同 Slot 的重试 → 已存 Cell
RowResult = 同 recipe / history row 的有序成员 + 前驱
CellId / RowResultId = Store 分配的普通随机 128-bit ID
```

recipe 固定列与不可变 Definition；HistoryRow 固定历史区间与前驱；同 recipe/row 的已发布结果唯一且不可变。三者已确定求值输入，无需再持久保存 EvaluationKey、ContentDigest 或 PriorInputProjectionDigest，也不另存可推导的 cell 前驱字段。规则与 recipe 的既有内容键暂留，不新增版本注册器。

Cell 按 Slot 唯一：竞争者正文不同也返回首个已存结果。Row 按 assignment 唯一：同成员与前驱返回已存 row，不能把候选随机 ID 当成冲突；真实业务差异仍拒绝。SQL 列和成员关系成为唯一持久表示，删除 cell/row/fulfilled 的整对象 canonical 副本及重复对账。

Overlay 的 Reuse 引用原 base cell，保留它的源 Slot；不是把它复制到 candidate Slot。Getter 改用来源关系诊断，不为保留旧“同正文即对齐”的指标重建 hash 层。

### 6.4 暂缓项目

ConnectionFingerprint、Prepared 局部 hash、ControlStateDigest、tool catalog/runtime identity 各自仍有真实消费者，本次 Store 切片不顺带删除。最终逻辑请求 commitment 保留；已冻结请求使用其已保存正文，不用新 Recap 重渲染。

所有计划中的重构完成并验证后，再统一执行真实数据处置与 LLM 重建；中间代码切片只使用隔离测试库，不为每个中间 schema 重建真实摘要。

## 7. 保留范围与最终重建边界

| 数据 | 后续处置 |
|---|---|
| Grid Store 的旧 cells、rows、members、fulfilled 标记及旧缓存命中 | 整体丢弃，不做 old→new 转换、prior 逆向映射或孤立结果保留 |
| Journal 原始历史、Prepared 正文、raw tool result、执行序号 | 保持原字节与既有恢复语义 |
| Control 规则、recipe、操作回执，相关配置 | 保留；格式改动另按实际消费者处理 |
| Timeline 行、Cadence、前驱与分区关系 | Store 切片保持；下一 Timeline 切片保留原行身份与关系，仅离线升级行表示，Cadence 原字节保持 |

最终步骤是：所有代码完成 → 在旧状态仍可读时收敛必要的 pending 操作 → 停服并保留完整匹配快照 → 在完整隔离副本完成所需 Timeline 格式升级并验证 → 初始化新 Store → 最后统一 LLM 重建 → 检查后恢复正常使用。当前 Store 位于 `derived/recap-grid/v1/grid.sqlite`，目录版本不等于数据库 schema；优先复用现有离线 Reset/锁与实例更换机制，不新增常驻迁移服务。

pending 前提按实际消费者限定：当前 `AgentControlTool.PromoteAsync` 在查 receipt 前先要求 Store 的 candidate 构建证明；下一 Timeline 切片还会改变 Recipes 非空的 registration 命令摘要。最终切换前，所有仍支持继续执行的分支中这两类待执行/重放调用，须通过正常工具续行到对应 ToolResultObserved 或更后状态，包括 Action 内尚未轮到的调用。未收敛则暂缓该数据集，不能把失败当成功、重写 command digest 或跳过 receipt。family/definition-only registration 不受这次编码变化影响；不默认增加“重建全部 inactive candidate 后再重放”第二套路径。

普通启动遇到旧或不支持的 Store schema，明确返回既有 unsupported 结果，不自动清库或调用 LLM。新 Store 运行期间保留事务、首个结果、missing-only 恢复与调用预算；整体 Reset 更换 StoreInstanceId，关闭旧 handle。不支持为本次简化增加局部删 winner、跨实例拼表或运行中替换前驱。

Prepared 保存的 ContextSnapshot 正文没有 Cell/Row/Store ID。旧冻结请求必须在旧 Store 已不可用时仍保持 canonical request、commitment 和 ExactContextInputs，并且零 recap 调用。只有最后重建后的新请求才使用新摘要。

回退采用匹配代码与完整数据快照；本次 Store 实施没有清库、调用真实模型或执行部署。

## 8. 实施入口、验证与完成定义

下一实施入口为 [Timeline 单一行身份计划](timeline-row-identity-simplification-plan.md)，当前只完成规划。[Store 简化计划](recap-store-simplification-plan.md)的 A/B/C 已完成，实际提交与验证见其 §7。其他已完成切片的代码与验证分别见 §10、[Control 实施记录](control-receipt-simplification-plan.md)和[前置输入实施记录](recap-prior-input-simplification-plan.md)。下面首轮验证命令保留作历史回归参考，不是新切片工作清单；历史 E2E 见 §11。

开始前检查 `git status` 和 `git log`，重新确认本文列出的关键类型与 schema，保留并行会话已提交修复。以当前生产消费者划范围，不把全部公共类型快照测试当成设计保留理由。

第一切片验证项目：

```bash
dotnet test tests/Completion.Tests/Completion.Tests.csproj --no-restore -m:1 -nr:false
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj --no-restore -m:1 -nr:false
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~CharacterNoteTranscriptionLiveTests&FullyQualifiedName!~GalateaCodexDelegationLiveTests&FullyQualifiedName!~GalateaScenarioLabLiveTests' -- xUnit.MaxParallelThreads=4
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -m:1 -nr:false
dotnet build prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj --no-restore -m:1 -nr:false
```

串行执行；restore 未准备时先恢复依赖。场景与项目过滤须检查当时测试命名，live opt-in 环境变量不得意外开启。后续切片增加受影响的 Timeline/RecapGrid Control/Store/Manager/Runtime/Getter/Cadence 测试，重点验证构建位置 first-winner、显式 Overlay 复用、清空后重建、旧操作 receipt 边界和非空冻结请求恢复；不再验收旧 Store 转换或跨来源正文相等复用。

完成定义：目标门槛/重复身份确实从当前代码与新写入数据消失；没有常量 hash 或伪造旧身份；保留范围内的实际数据可读可恢复，旧 Recap 可重建；负面绑定与副作用边界仍成立；文档说清当前格式及回退限制。验证次数、测试通过数和 live 证据只记录实际执行结果。

## 9. 辩证审查记录（首轮）

首轮已进行三个独立视角的完整阅读、源码核对和交叉质询。以下保留当时决策来源；涉及旧 Store 转换与缓存保留的三项已被最新用户决定替代，当前裁决见[Store 简化计划](recap-store-simplification-plan.md)。

| 初稿问题 | 反例或成本 | 修订结果 |
|---|---|---|
| “adapter 升级后可以恢复”过宽 | 真正非法的原生载荷仍不能执行，易扩成兼容协商工程 | 只删除标签否决权，其他检查维持 |
| v7 兼容可能复制完整历史模型 | 同一逻辑请求只因 target 多一字段 | codec 边界投影，v7/v8 同一 body 与重构器 |
| 只关注未决请求 | 已完成 v7 的 audit 分派、v5 的共享 target reader 也会被新布局破坏 | 明确可读/可执行版本及混合 lineage 审计验收 |
| 当时要求 Timeline 与结果主键合并迁移 | 旧 hash 引用图会被重编码两次 | 已替代：旧 Store 丢弃，代码可分片，全部完成后统一重建 |
| “保留 receipt 就可恢复”不充分 | 改 command 编码可能冲突；promotion 还会先读 Store proof | 保留针对实际受影响操作的收敛边界，不再把全部 pending 都纳入迁移前提 |
| 当时需从旧结果生成新缓存键 | 旧 prior 只有 digest，没有正文表 | 已撤销：不转换旧摘要，不建映射，也不保旧缓存命中 |
| 为 receipt 再保存全文命令 | 与现有 command digest 形成重复状态 | 只删结果派生 hash，命令匹配暂不动 |

用户已采纳 D1；以后允许哪些 connection 改配继续重试仍待独立决定，不阻塞 §5。整体 hash 清零、逐字 HTTP 回放和所有缓存命中保留都不是本设计的完成条件。

## 10. 第一轮实施记录（2026-09-14）

按 §8 的首轮范围完成 §5，工作包经过源码再审视、分工实施与独立审阅：

| 工作包 | 实际改动与证据 |
|---|---|
| Completion 路由身份（`b5fd03db`） | 删除 adapter 字段、mapping 标签、计算函数和 mismatch reason；精确 connection/client/API 绑定保留；822 通过、1 跳过 |
| SessionJournal 持久恢复（`3ff8080b`） | writer v8；v5/v7 旧布局在 codec 读取丢弃 adapter；v7/v8 共用 body/重构/audit，保留实际 source version；517 通过 |
| Galatea / CLI 集成 | 映射与运行时错误出口同步收缩；旧 v7 经真实 Anthropic Client 与模拟 Models 404 的生产链验证、Codex 恢复、非空 RecapGrid 零重算验证通过；Galatea（`087c5e87`）非 Live 942 通过，CLI（`0ebfd6cd`）139 通过 |
| 相邻测试迁移（`1755c884`） | Cadence、Getter、Manager、Online 的目标构造同步删第四参数；对应测试分别 29、29、78、33 通过；没有扩大到 RecapGrid 的键或持久 schema |

独立审阅提出固定旧 v7 golden 的建议已落实：样本取自实施前的完整字面量，不随当前 writer 自动变化。
旧格式生产链夹具只在停止的合成测试目录追加替代 lineage，不改写已有事件，更不操作真实会话。

验证统一串行使用 `--no-restore -m:1 -nr:false`；除 §8 所列项目，补跑 `SessionJournal.Cli.Tests` 及上述四个 RecapGrid 测试项目。
首轮 Galatea 使用 `FullyQualifiedName!~Live`，且进程环境移除了 Note、Lab、Codex delegation 的 live opt-in 开关。后续 E2E 收尾发现此子串过滤也会误排除 `Delivery` 测试；§8 已改为只排除三个真实 Live 类，扩大后的补验见 §11。本节保留首轮实际执行数字，不把它冒充完整非 Live 集合。
最终八个项目合计 2,589 通过、1 跳过（Windows 专用测试），0 失败；文档检查 33 文件、0 diagnostics，`git diff --check` 通过。
Galatea Server 与 SessionJournal CLI 的独立 build 均为 0 warnings、0 errors。

首轮代码阶段没有部署或重启长期实例，没有转换 Timeline/Control/Store。§6.2 的独立格式升级方案已补入设计，
当时的 §6.1/§6.3 整图迁移路线已被本次“清旧 Recap、最后统一重建”决定替代；首轮实施与验证事实保持。

## 11. 唯一 Dev 实例 E2E（2026-09-14）

下次执行可复用的流程与踩坑经验已整理到 [E2E 操作指南](e2e-testing.md)；本节保留本次实际证据。

用户明确授权使用 `prototypes/Galatea/.atelia` 的两个测试账号进行真实 E2E，允许撤销测试叙事并保留外部 Codex/邮件影响。执行前确认实例停止，完整备份 `.atelia` 到 `/mnt/e/bak/atelia-full-identity-e2e-20260913T201405Z.7z`，通过 `7z t` 并保存校验文件；没有只备份其中一个用户。

初始两个活动命名 `main` 分支均通过 full audit 与 selected-lineage audit：`cyber` 为 AwaitingCompletion（228 events，Prepared v5=14/v7=5），`gpt` 为 Idle（252 events，v5=21/v7=40）。这是活动命名分支盘点，不宣称扫描全部历史物理 Ref/reflog。

真实 Chrome 登录后发现并修复了单元夹具未覆盖的旧状态问题：`gpt` 的已 Delivered Note 回执使用旧版“原文”文案，当前 renderer 改为“内容”文案后，`ValidateReceiptDeliveryRows` 重渲染并全文比较旧 body，使 `/api/v1/chat/turns/current` 返回 500。`351095b5` 删除这项重复门槛，按 Applied 事务中已冻结的 `notice_body` 读取；保留严格 UTF-8、非空、预算、来源/修订及 Bound Observation 检查。新增旧文案三种状态冷重开、append-before-ack 恢复和真实 Host 读取测试，focused 85/85 通过，独立审阅无阻断 findings。真实数据库未执行 SQL 修复，原 412-byte Delivered 回执及修订号保持不变。

实际调用与浏览器验证：

- `cyber` 的旧 v7 Started 请求经页面明确授权恢复成功，继续使用原中转站与 `claude-opus-4-6`；Models API 404 实际触发 `max_tokens=128000` 回退。
- 两个用户各完成两条测试输入，主连接分别为 Opus 4.6 与 Codex backend 的 `gpt-6-astra`。第一轮后真实停服、重新启动，两用户页面中的对应输入/回答逐字保持，current 为 Idle；第二轮验证重开后仍可继续发送。
- 共完成 5 次主线调用（1 次旧请求恢复、4 次 fresh），均有真实 SSE `done` 与正常页面收尾。Completion 日志另记录 20 次成功的 normalizer/Codex helper 调用。
- 首次 Anthropic 恢复在 Models GET 上发生 TLS EOF，未到 Messages POST；之后独立 GET 确认 404、小 Messages 请求成功，再经明确授权完成上述恢复。记录保留这次失败，不把重试包装成首次成功，也未增加自动重发生成请求。
- 私有浏览器脚本两处假设已纠正：SSE 按完整 `done/error` 帧结束观察，不等待 HTTP EOF；normalizer 将测试时间戳规范化为等价 ISO 拼法时不误判消息丢失。第二轮 cyber 的原失败报告保留，随后只读定向检查确认实际对应回合已显示，没有为补验重发消息。

四条测试输入都通过真实页面 Undo 撤销，且每次先确认最新用户卡片匹配本次测试标记。清理后两用户均 Idle，活动 lineage 不再包含 v8 测试轮次；验证期间已经证明 v5/v7/v8 共存可读。原始测试事件与 ref 移动历史保留，未冒充目录回滚。`cyber` 保留旧请求恢复产生的两个 Started 和 Action（231 events）；`gpt` 保留正常配置同步的 SystemPromptSetup（253 events）。一封原有 gpt Ready 回信已按正常流程转为 Consumed，邮件记录保留；没有新增 outbound mail。

清理后 full/selected audit 再次通过，7 个活动 SQLite 库 `quick_check=ok`，旧 gpt 回执字段对比不变。私有逐次元数据证据位于 `gitignore/galatea-identity-e2e-20260913T201405Z/`；原故事、Note、凭据和完整调用日志不进入版本库。

撤销后再次冷启动并通过真实 Chrome 检查：两用户 current=Idle，页面没有 console/page/API 错误；cyber Recap 为 exact/ready，gpt 为 exact/raw-only，均无错误 code。最终停止服务，没有把 Dev 实例留在后台运行。

最后停服的 full/selected audit 确认此次冷重开没有改变清理后的两个 head，旧 gpt 回执仍原样保留。验证后完整快照为 `/mnt/e/bak/atelia-full-identity-e2e-20260913T201405Z-verified.7z`，与升级前备份分别保留；两份均通过归档完整性检查。

回归补验：原 `!~Live` 子串过滤漏掉了 `Delivery` 测试，已改成只排除三个 Live 类；此前满并行出现的一个 delegation 短时限测试隔离 2/2 通过，随后使用 `xUnit.MaxParallelThreads=4` 的完整非 Live 集合 **983/983 通过**。Server 独立 build 为 0 warnings、0 errors；文档检查 33 文件、0 diagnostics。未因测试时序抖动放宽生产 deadline。

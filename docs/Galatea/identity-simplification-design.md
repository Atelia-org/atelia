# Galatea / RecapGrid 身份与恢复校验简化设计

> 状态：§5 已实现，并在唯一 Dev 实例完成真实调用与冷重开验证；§6 仍为后续设计。代码阶段见 §10，实例 E2E 与额外修复见 §11。
>
> 日期：2026-09-14。代码基线：`277baeea`。本文区分目标设计、当前实现和历史验证；不继承其他工作单的实施授权。

## 1. 目标与最小模型

用少量明确的身份表达：**哪段历史、哪种摘要规则、哪个已落盘结果、哪次操作**。保留一个内部摘要缓存键和必要的持久恢复内容，取消把正常修复和升级变成恢复失败的手工 adapter 指纹门槛。

目标模型：

```text
HistoryRow：不可变历史区间 + 前驱
Definition / Recipe：不可变规则快照 + 构建关系
EvaluationCacheKey：历史输入 + 规则内容 + 有序前置摘要实际内容
Cell / RowResult：普通不可变记录 ID + UNIQUE / FK
Head：实例身份 + 单调 generation
OperationReceipt：稳定操作 ID + 命令匹配依据 + 结果
Prepared：已准备的逻辑请求输入 + 必要调用目标 + 一处最终内容校验
```

这些是业务概念，不要求新建七套 framework/API。已有类型能收缩就收缩。hash 可以作为内部索引实现，不再默认成为每层对象的公开身份、持久格式和恢复兼容合同。

## 2. 需求台账与证据等级

| 编号 | 要求或判断 | 来源 |
|---|---|---|
| U1 | 个人自用、未发布、无下游，优先简单、及时重构 | 用户与根 AGENTS.md，明确 |
| U2 | 不需要应用层防恶意篡改；质疑 hash/fingerprint 的数量与价值 | 本次用户，明确 |
| U3 | 设计经 dialectical-simplification 完善后已获采纳；授权带领 subagents 实施、维护文档并按需提交 | 2026-09-14 用户后续指令，明确 |
| B1 | 已有会话、摘要、工具回执和未完成请求需要跨进程重启保留 | 生产代码、当前实例使用与现有恢复测试 |
| B2 | 摘要不可混用历史区间、规则、前置输入；重复构建沿用首个已提交结果 | Store/Manager 真实消费者与 first-winner 测试 |
| B3 | 操作已生效、会话结果尚未提交时，恢复不能重复应用副作用 | Control receipt 与 SessionJournal 工具执行链 |
| B4 | 不同用户/仓库不能误绑定；真实工具权限和原生 provider 载荷解析边界仍有效 | 当前执行与隔离消费者；U2 不等于取消业务权限 |
| D1 | 重试沿用原模型与已准备 prompt/history/tools，允许当前修好的 adapter 执行；不承诺旧 HTTP wire 逐字不变 | 本设计建议，已随用户采纳整体方案而确认 |
| D2 | 当前 store 内相同内容、不同来源的摘要仍可通过内部缓存键复用 | 当前行为及设计建议；不要求跨 store 全局去重或保住所有孤立缓存命中率 |

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
| 结果身份与唯一性 | [SchemaV2.sql](../../prototypes/SessionJournal.RecapGrid/Store/SchemaV2.sql)、[SqliteRecapGridStore](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs) | 已确认：cell 的 evaluation key 唯一；row 的 `(ref,timeline,recipe,row)` 唯一 |
| 摘要缓存输入 | [ManagerRowBuild](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs)、[BuildContracts](../../prototypes/SessionJournal.RecapGrid/Abstractions/BuildContracts.cs) | 已确认：缓存区分规则、历史和前置摘要内容 |
| 操作结果摘要 | [ControlOperationCanonicalizer](../../prototypes/SessionJournal.RecapGrid/Control/ControlOperationCanonicalizer.cs)、[ControlRuntime](../../prototypes/SessionJournal.RecapGrid/Control/ControlRuntime.cs) `TryReplay` | 已确认：ResultIdentity 不参与操作判重；receipt 参与 |
| Prepared 内容证明 | [Manifest](../../prototypes/SessionJournal/SessionRequestManifest.cs)、[Reconstructor](../../prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs) | 已确认：局部 hash 与最终 commitment 并存；最终检查可发现重构变化，但该故障场景目前是结构推论 |

## 4. 决策总表

| 对象 | 判定 | 最小机制与边界 |
|---|---|---|
| RequestAdapterFingerprint | delete | 删除执行硬门槛及当前身份字段，不用另一个手工标签或常量 hash 替代 |
| ConnectionFingerprint | defer | 第一切片保持当前行为；改变 endpoint/reasoning 后如何恢复是另一项产品选择 |
| HistoryRowId + DescriptorDigest | merge | 保留一个不可变 HistoryRowId，暂沿用现有 RowId 算法；无需顺便换 UUID |
| CellDigest / RowViewDigest | simplify | 后续改普通记录 ID，保留唯一约束、前驱及成员 FK |
| Content / Projection / Evaluation 三层摘要 | merge | 逐步收敛为一个内部缓存键；不漏掉实际前置内容，也不以来源 ID 代替内容相等 |
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

## 6. 后续切片：先移除重复身份，再整理缓存

发布顺序：§5 独立发布；§6.2 可作为不改命令编码的小切片先完成；§6.1 与 §6.3 开发可分工作包，但采用一个目标格式组合、一次派生数据转换和发布，不上线中间格式。这避免为了先删 DescriptorDigest、再改结果主键而重编码同一数据图两次。

### 6.1 合并 Timeline 行身份

保留现有 HistoryRowId 值与前驱关系，删除单独 DescriptorDigest 的运行身份。需要迁移的引用包括 descriptor、Control bootstrap witness、Cadence seal、RecapGrid evaluation key、row coordinate、fulfilled view key、Manager/Runtime 及其持久 schema。

迁移时不能把旧 DescriptorDigest 字符串直接当 RowId：两者 domain 不同。必须从已解码历史行建立 `old descriptor digest → existing rowId` 映射，在同一停服快照中更新引用。沿用原来的历史区间和 row frontier，不重新切历史，不重新调用摘要模型。

该替换会改变 EvaluationKey canonical，进而改变旧 CellDigest、RowViewDigest、成员与前驱引用。不能只改 SQL 列：读取器还对照 canonical BLOB。与 §6.3 一起按依赖顺序转换完整数据图，原始 Journal 的历史审计字节不改。

转换输入不能从 digest 反推：旧 EvaluationKey 仅存 prior projection digest，没有 projection 正文表。迁移工具从旧 RowView 及成员建立临时 `oldProjectionDigest → ordered(columnId, content)` 映射，FirstRow 单独编码，然后转换需要保留的 cell、row、previous/member/fulfilled 引用。映射只存在于离线转换工具，不成为新的运行时注册库。

保留集合包括所有已引用/已选结果，以及仍可能由未完成构建再次查询的已提交 first-winner cell；“尚未挂入 RowView”不等于可丢弃缓存。无法证明可丢弃或无法恢复所需 prior 输入时，副本转换停止并报告，不靠重新调用模型补洞。已证明不可达且不再参与恢复的纯缓存项可以不迁，不要求跨 store 复用率或所有历史缓存命中保持不变。

还有一个迁移前提：旧 registration command 的编码含 bootstrap DescriptorDigest，改它会令 `TryReplay` 的 CommandDigest 不匹配，receipt 又没有完整命令可供逆向转换。**先通过正常恢复收敛所有仍需执行/回放的旧 Control 操作，再停服迁移并复查。** 清点包括该数据集所有仍允许执行的持久 ref/分支、未打开的会话，以及已持久化但尚未调用 Control 的工具命令；不能只看当前网页或只查 receipt 表。

无法收敛时不得重执行、改写 receipt 摘要或只凭 operationId 跳过命令检查；暂缓该数据集转换，另行处理具体未决操作。已完成历史仅作审计读取，不借迁移从旧历史重新复活已不受支持的 pending 操作。本设计不为该迁移新增旧命令执行兼容框架。

### 6.2 简化 Control 操作回执

删除 `hash(commandDigest, terminalKind)` 这层 ResultIdentity；使用已有稳定 operation key 作为回执引用，无需新建全局 ReceiptRegistry。保留 execution sequence、既有 command digest 与 runtime 绑定检查、已应用结果与 generation；本切片不改变命令编码，也不新存第二份完整命令正文。

旧 receipt 解码时接受并丢弃派生结果字段；新结果输出以明确的 operation/receipt 字段表达，不能继续在名为 `ResultIdentity` 的字段里悄悄更换语义。跨 Control 提交后、SessionJournal tool result 前崩溃，恢复应返回已应用结果且不二次推进语义状态。该独立切片不借输出字段清理改变 operation/runtime 的判重身份，否则应并入需要先收敛旧操作的迁移发布。

该切片涉及历史 tool result 的审计读取与当前 receipt 的输出格式；不修改已有 raw tool result 文本，也不重新执行旧操作。若有当前代码消费者依赖旧结果字段，必须一起重构而非增加长期双输出。

实施再审视已确认该切片可独立，但需明确文件升级边界：当前 `ControlState` 是 canonical JSON v2，receipt 内有结果摘要，读取会对照原 canonical bytes 与 state digest。后续 writer 应写 v3；旧 v2 在 codec 验证后投影为当前 receipt，保留读入的原 Head 与 CanonicalBytes，直到下一次正常 mutation 才写新格式。不能在纯读取时重算 Head，否则旧 backup manifest、CAS 与 restore 会失配。

当前 AgentControl 的输出 DTO 不进入 runtime identity；后续输出改为 `schemaVersion: 2` 与 `operationKey`，同时保持输入定义、工具说明、catalog、命令编码及既有 runtime identity。验证需包含旧 v2 receipt 冷读/重放不写文件、不递增 generation，正常 mutation 后写 v3、旧操作仍可重放，以及 v2 backup 与 v3 状态的 receipt 合并。这里是后续切片的具体方案，尚未修改 Control 实现。

### 6.3 结果主键与缓存身份

Cell/RowResult 用 store 内不可变主键；保留 cell 对 evaluation cache key 的唯一约束，以及 row 对 `(ref,timeline,recipe,row)` 的唯一约束、previous/member FK。不同 store 的 ID 不相互解析，保留 store 实例边界。

EvaluationCacheKey 在当前 store 内覆盖 `(HistoryRowId, Definition内容键, FirstRow | ordered(columnId, UTF8 content))`；不要只使用 previous RowViewId，不能混淆列顺序、空内容和 FirstRow。两个不同构建来源产生相同内容时由该键自然复用，不引入跨 store 全局去重。规则修改不要求操作者记得手工 bump version。Definition/Recipe 可暂保留一个内部内容标识，避免额外引入版本注册服务。

把 ContentDigest、PriorProjectionDigest、EvaluationKeyDigest 的公开类型、序列化对象和层层对账合并，不能仅在末端新包一层 cache key。内部编码稳定规则只服务缓存，普通 operator JSON 的属性顺序/空白不进入身份。

### 6.4 暂缓的项目

ConnectionFingerprint 的替代需要单独决定 endpoint/reasoning 改配的恢复语义；第一切片不替用户选“自动采用所有新配置”。Prepared 局部 hash、ControlStateDigest 与整个 tool catalog 指纹分别等到有具体切片与消费者盘点再动，不顺手扩大变更。

最终 request commitment 暂保留一处：它可以发现代码升级导致旧输入被展开成不同 prompt。保存直接可加载的完整 request snapshot 是另一种方案，但不为删除一处 SHA 再引入第二份全文持久数据。

## 7. 持久数据与迁移约束

- 不因为“未发布”就清空真实会话、Control receipt 或已提交摘要；无需支持不存在的下游，但真实当前数据是迁移对象。
- 第一切片只引入旧 v7 的窄读取兼容，不改写 Journal。删除旧读取分支的条件是所有仍需读取/恢复的实际数据已另行有可用路径，不能仅看没有 pending turn。
- 涉及 Timeline/Control/Store 的后续格式迁移须先满足 §6.1 的旧 Control 操作收敛前提，再停服，连同原始 Journal、派生存储和相关配置制作一份可恢复快照。先在隔离副本转换、重开与遍历引用，不边跑服务边改库。
- 多文件不能假装一条 SQLite transaction 就原子升级完毕。选完整目录副本作为迁移/回退单位；转换成功后整体切换，失败恢复原副本。不新增常驻迁移协调服务。
- 迁移不调用 provider；保持历史内容、前驱、已选择结果、操作回执与执行序号。仅改变键与引用表达。
- 旧程序不能打开新写入格式时，部署回退需恢复匹配快照；不能只回退二进制，也不能暗示升级后产生的新轮次会自动保留在旧快照里。

## 8. 实施入口、验证与完成定义

第一轮只实施 §5，顺带更新其实际协议/运行文档；§6 按其发布顺序随后推进。没有 `/goal` 指令，没有自动发布或真实会话操作授权。

开始前检查 `git status` 和 `git log`，重新确认本文列出的关键类型与 schema，保留并行会话已提交修复。以当前生产消费者划范围，不把全部公共类型快照测试当成设计保留理由。

第一切片验证项目：

```bash
dotnet test tests/Completion.Tests/Completion.Tests.csproj --no-restore -m:1 -nr:false
dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj --no-restore -m:1 -nr:false
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~CharacterNoteTranscriptionLiveTests&FullyQualifiedName!~GalateaCodexDelegationLiveTests&FullyQualifiedName!~GalateaScenarioLabLiveTests' -- xUnit.MaxParallelThreads=4
dotnet build prototypes/Galatea/Galatea.Server.csproj --no-restore -m:1 -nr:false
dotnet build prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj --no-restore -m:1 -nr:false
```

串行执行；restore 未准备时先恢复依赖。场景与项目过滤须检查当时测试命名，live opt-in 环境变量不得意外开启。后续切片增加受影响的 Timeline/RecapGrid Control/Store/Manager/Runtime/Getter/Cadence 测试，重点验证引用迁移、同输入 first-winner、相同内容复用、旧操作 receipt 重放和非空摘要冷恢复。

完成定义：目标门槛/重复身份确实从当前代码与新写入数据消失；没有常量 hash 或伪造旧身份；旧实际数据可读可恢复；负面绑定与副作用边界仍成立；文档说清当前格式及回退限制。验证次数、测试通过数和 live 证据只记录实际执行结果。

## 9. 辩证审查记录

已进行三个独立视角的完整阅读、源码核对和交叉质询：需求质疑、最小架构、语义辩护。以下修订来自具体消费者和执行轨迹，不以票数作裁决。

| 初稿问题 | 反例或成本 | 修订结果 |
|---|---|---|
| “adapter 升级后可以恢复”过宽 | 真正非法的原生载荷仍不能执行，易扩成兼容协商工程 | 只删除标签否决权，其他检查维持 |
| v7 兼容可能复制完整历史模型 | 同一逻辑请求只因 target 多一字段 | codec 边界投影，v7/v8 同一 body 与重构器 |
| 只关注未决请求 | 已完成 v7 的 audit 分派、v5 的共享 target reader 也会被新布局破坏 | 明确可读/可执行版本及混合 lineage 审计验收 |
| Timeline 与结果主键分两次部署 | EvaluationKey→Cell→RowView→前驱引用整图改写两次 | 开发可拆，后续派生格式只转换发布一次 |
| “保留 receipt 就可恢复”不充分 | bootstrap descriptor 改变 command hash，旧已应用操作回放变 conflict | 先正常收敛旧操作，再改命令相关格式 |
| 从旧结果直接生成新缓存键 | 旧 prior 只有 digest，没有正文表 | 从旧 row/member 建临时映射，保留可恢复 first winner，不可丢状态不靠重算 |
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
随后 §6.1/§6.3 仍须按单次目标格式和迁移约束推进，不将本轮完成误记为整份后续路线全部完成。

## 11. 唯一 Dev 实例 E2E（2026-09-14）

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

# Timeline 单一行身份：下一实施切片

> 状态：设计已完成三视角独立审查与交叉质询，待实施；本轮只修改文档。
> 日期：2026-09-14；源码基线：`6d87a4c1`。
> 承接[身份简化总设计](identity-simplification-design.md)与已完成的 [Store 切片](recap-store-simplification-plan.md)。真实数据处置和 LLM 重建仍留到全部重构完成后。

## 1. 最小模型

**一行历史只使用既有的 `HistoryRowId`。删除同一 descriptor 的第二个 `DescriptorDigest`，保持原行 ID、历史分区和前驱关系。**

```text
HistorySegmentDescriptor = RowId + 原有历史区间、前驱、规则与测量事实
AncestorWitness = 原有 repository / whole-head 作用域 + RowId
RowResult = 原有 assignment、成员、前驱；不再附带第二个历史行身份
FulfilledViewKey = (RefId, TimelineId, TimelineHeadGeneration, ThroughRowId, RecipeDigest)
```

这是一次贯穿 Timeline → Control → RecapGrid → CLI/Galatea 的字段与表示收口。已有 `HistoryRowId` 的算法暂留，不换 UUID，不重新切分历史。无需增加 IdentityRegistry、跨版本 ID 映射、并行 Timeline 或通用迁移框架。

持久处置仅有三个不同职责：保留 Timeline 行并做一次离线格式转换；Control 沿既有 codec 边界读旧格式、正常写入时升级；可丢弃的 Recap Store 最终整体 Reset。Cadence、Recipe 正文和 Journal 无须格式变化。

## 2. 需求台账与源码证据

| 要求 | 来源与边界 |
|---|---|
| 个人未发布项目；不需要应用层防恶意篡改或假想下游兼容 | 用户与根 AGENTS.md |
| 旧 Recap 可以丢弃，全部重构完成后最后统一 LLM 重建 | 用户已采纳决定；仅放弃摘要物化内容和旧缓存命中 |
| 本轮规划并完善下一切片 | 本次用户请求；不授权提前实现或操作真实数据 |
| 保留 Journal、冻结请求、raw tool result、执行序号、规则与 receipt | 当前持久恢复消费者；清 Store 不等于清整个 derived/control |
| 保留 Timeline 的实际分区、前驱和非当前路径行 | 当前 ledger、rewind/reconcile、Control bootstrap 消费者 |
| 同 Slot 首个结果、missing-only、Overlay、预算与事务恢复继续有效 | 上一切片已实现的 Store/Manager/Runtime |
| witness/proof 仍绑定真实 repository、whole head、selected path 和 raw capture | 当前 Timeline/Cadence 调用链；一个 RowId 不代表当前仍选中该行 |
| Started 结果不明仍须明确授权；冻结恢复零 recap | 当前 SessionJournal/Host 恢复语义 |

故障模型仍是本地进程崩溃、冷重开、并发写入、rewind 与跨组件提交窗口，不新增外部 exactly-once 或分布式迁移协调承诺。相邻设计文档是路线来源，不作为源码事实的独立佐证。

| 位置 | 已核对事实与影响 |
|---|---|
| [HistoryTimelineContracts](../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelineContracts.cs) `HistorySegmentDescriptorFactory.Create`；[CanonicalCodec](../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelineCanonicalCodec.cs) `EncodeDescriptorBody` | 两个身份对同一 body 换 domain 计算；RowId 已覆盖全部 descriptor 身份事实 |
| [SqliteHistoryTimelineLedger](../../prototypes/SessionJournal.HistoryTimeline/SqliteHistoryTimelineLedger.cs) | schema 2 的 rows 同时存 `row_id`、`descriptor_digest` 和严格 canonical descriptor；直接删字段不能读旧库 |
| [HistorySelectedPathCommitment](../../prototypes/SessionJournal.HistoryTimeline/HistorySelectedPathCommitment.cs) | path leaf 使用 ordinal、RowId、previous、end；删除 DescriptorDigest 不需要重算路径身份 |
| [CadenceCanonicalCodec](../../prototypes/SessionJournal.RecapGrid.Cadence/CadenceCanonicalCodec.cs)；[HistoryRecentReserveContracts](../../prototypes/SessionJournal.HistoryTimeline/HistoryRecentReserveContracts.cs) | Cadence 持久字段不含 DescriptorDigest；相关 reserve proof 在 Timeline 内，属于瞬态消费者 |
| [BuildContracts](../../prototypes/SessionJournal.RecapGrid/Abstractions/BuildContracts.cs) `GridBuildRecipeBodyDto` | Recipe 本体只引用 `BootstrapThroughRowId`，不含 DescriptorDigest；内容键不必改变 |
| [ControlState](../../prototypes/SessionJournal.RecapGrid/Control/ControlState.cs)；[ControlOperationCanonicalizer](../../prototypes/SessionJournal.RecapGrid/Control/ControlOperationCanonicalizer.cs) | 重复字段位于 Control bootstrap 附加记录和 registration 命令的 Recipe DTO；并非 Recipe 正文 |
| [ControlMaintenance](../../prototypes/SessionJournal.RecapGrid/Control/ControlMaintenance.cs) | export/backup 使用源 Head/bytes；旧 backup 的读取与 receipt union 已有真实消费者 |
| [StoreContracts](../../prototypes/SessionJournal.RecapGrid/Store/StoreContracts.cs) `RecapGridStoreExportCursor` | fulfilled cursor 的 through 固定 64 hex，旧 digest 与新 RowId 长度相同；须显式区分新语义 |

## 3. 当前代码的目标形状

### 3.1 Timeline 与选择证明

删除公开 `HistorySegmentDescriptorDigest` 类型、`HistorySegmentDescriptor.DescriptorDigest`、相关构造参数、专用语法检查，以及当前生成/读取路径的第二次 hash。原 `RowIdDomain` 和 `EncodeDescriptorBody` 的字段、顺序、编码及 body `v:1` 保持。**descriptor 外层格式升级不能修改 ID preimage 的版本。**

Witness、SelectedRow、BuildReadSession、reserve proof 已持有 RowId，删除第二个身份及重复比较即可。保留独立 expected 与实际读取结果的 RowId 比较，以及 repository/Ref/Timeline、whole head、selected membership、token 生命周期、raw fence、reserve/策略/测量等真实条件；不把选中证明降成 rows 表中“存在此 ID”。

TimelineHead、locator、PartitionPolicy、selected-path leaf/Merkle/root、行前沿算法保持。继续验证原 RowId 与 descriptor body 一致，继续保留 raw range/setup 等现有事实校验。Timeline 的 canonical 表示和路径结构是否还能简化，另待独立证据，不并入本切片。

### 3.2 Control：一个运行模型，复用已有读取边界

`RegisteredRecipeBootstrap` 删除 DescriptorDigest，保留已有 TimelineHead 和 RowId；检查 RowId 与 `Recipe.BootstrapThroughRowId` 一致，以及当前选中行与作用域。空 bootstrap 与非空 bootstrap 仍分别验证。

目标 writer 为 Control v4。v2/v3 仅在 codec 内按各自原格式校验 canonical bytes、state digest、旧字段形状和 RowId/DescriptorDigest 的 null 配对，然后丢弃旧 descriptor 值，投影到同一个当前 graph。旧 v2 的 ResultIdentity 继续沿现有 codec 边界丢弃，不重新进入运行模型。

读取、成功 receipt replay、export、backup 保留原 Head/CanonicalBytes；下一次真实 mutation 才按 v4 生成新状态并按既有规则推进 generation。不得为升级格式制造空写入。Restore 先读取两边为同一模型，再沿现有 receipt union 与新实例发布流程处理。

Family、Definition、BuildTarget、Recipe 的 canonical bytes 和 digest 原值保留；receipt 的 OperationKey、command/runtime/sequence、OriginalInstanceId/Generation 全部保留。不为删一个附加字段增加 Control/backup 离线转换器。

### 3.3 registration 命令的精确变化范围

`RecipeCommandDto` 删除 `BootstrapDescriptorDigest`，保留 Recipe canonical bytes 与 BootstrapRowId。保留 `atelia.recap-grid.control-command.registration.v1` domain：它是命令种类的 hash 域，不是一个要解码的格式版本；改变实际 preimage 已能区分发生变化的命令。

必须用旧/新固定语料证明：

| 命令 | 预期 bytes / digest |
|---|---|
| 空 bundle、family-only、definition-only（Recipes 为空） | 不变 |
| 任意 Recipes 非空的 registration，包括 witness 为 null | 改变；旧 DTO 即使为 null 也写出被删字段 |
| promotion | 不变，只使用 RecipeDigest |

工具 input schema/说明、built-in catalog、admission、完整 runtime identity 的原 golden 保持。它们没有发生实际变更，不能额外 bump 标签阻塞无关 Frozen/ToolContinuation。也不能因 domain 未升级就宣称旧 recipe receipt 可按新命令重放。

已生效且 Journal 尚未记录 ToolResult 的 recipe registration，会因新 command digest 与旧 receipt 不同而 Conflict。最终切换前按 §6 正常收敛相关 pending；不改 receipt、不跳过匹配、不增加永久旧命令执行协议。

### 3.4 Store、构建与消费者

删除 `RowViewCoordinate/RowBuildSpec.HistorySegmentDigest`、`RecapRowView.RowDescriptorDigest`、Manager proof/progress/authority 及 Getter selection 中与已有 RowId 重复的属性和参数。只有旧 descriptor 的 through 位置改为 `HistoryRowId ThroughRowId`；已有 through RowId 的 DTO 直接删重复字段，不再传两份相同 RowId。

目标 Store schema v4：row_view 删 `row_descriptor_digest`；fulfilled 的 through 改为 `through_history_row_id`。保留 `(ref,timeline,recipe,historyRow)` assignment 唯一性，fulfilled 的 FK 精确绑定结果 ID、作用域、recipe 和 through HistoryRowId，删除只服务旧 descriptor 的索引，复用必要的列组合约束。不得只把旧 descriptor 字符串改名当成 RowId。

CellSlot、CellId、RowResultId、winner、前驱与成员语义保持。Manager/Runtime 的独立 frozen expected 校验改用实际 RowId，Overlay 与 Getter 来源关系保持；不加入新的输入 hash。

Store export cursor wire 升 v2，统一拒绝旧 v1，不留 cursor 转换分支。否则旧 fulfilled cursor 的 64 hex through 会被静默解释成另一种排序位置。SQL schema 升级本身挡不住外部保存的旧 cursor。

CLI selection 输出与 Galatea readiness DTO/JS 校验删重复 descriptor 字段，保留现有 row/through 字段；更新导出临时 JSON 投影。RG0001 analyzer 的窄值类型例外同步删去已不存在的类型，不放开 Timeline 读写/maintenance 模块权限。

## 4. Timeline 的一次离线格式转换

按本基线，目标为 Timeline SQLite schema 3、descriptor 外层 wire v2；目录仍为 `derived/history-timeline/v2/...`，路径名字不充当 schema 版本。Control writer v4、Store v4 与 cursor v2 是各自独立版本。实施前检查并行提交是否已占号，只对实际变更的格式取下一版本。

普通 Timeline reader 只读新格式；打开旧 schema 返回明确 unsupported，不能自动重切历史、改文件或调用模型。旧 row decoder/hash 只属于限定的离线升级入口，不形成第二套在线 Timeline 服务。

实现一个具体的 Timeline maintenance 操作及 CLI 入口即可，对停止的完整 repository 隔离副本工作，沿现有 Ref 独占锁按明确的物理 RefId/TimelineId 逐库处理。范围不能只取 active locator；需要继续使用的 retained Timeline 也纳入最终清点，不新增自动 GC：

1. 对指定 scope 取得离线独占权，验证源 schema 2、Ref/Timeline/head、旧 descriptor/行索引及完整关系，验证和转换阶段不改源文件；该 Ref 的 locator 保持原值，不要求每个待转换库都处于 active 状态。
2. 按确切目标 DDL 新建同目录临时数据库，分页处理**所有 rows**，包括 rewind 后未在 current_selected_path 的行。去掉第二身份的 SQL 列和外层 canonical 字段，保留原 RowId、previous、区间、setup、规则、测量事实。当前 reader 严格比较 SQLite schema SQL，不能假定一次 `ALTER DROP COLUMN` 就满足新 schema。
3. 原值保留 policies、TimelineId/Ref、locator/head/generation、selected path 与其索引/commitment；用新 reader 做完整 verify。源格式校验、目标校验或预算失败均不发布。
4. 关闭连接，沿既有持久文件发布机制替换该数据库，严格冷开复验。目标已是健康新格式时只验证并返回已完成，不推进 head/generation。

不把这项操作伪装成现有 Restore：当前 Restore 要求可读的当前格式，不能直接完成旧 schema 转换。可复用锁、临时文件、发布与验证机制，源格式读取必须明确实现。单库发布边界前后中断，只能留下完整旧库或完整新库；下次可明确验证状态。副本内可暂时存在不同 schema 的多个库，全部完成并验证后才能用于最终切换；不把逐库替换宣称为跨文件原子事务，也不建设迁移任务日志或协调器。

旧备份保持原样用于匹配旧代码的回退；不得重写其 manifest 冒称原备份。需要升级备份时，先用匹配旧格式的程序在隔离 repository 副本恢复，或直接还原完整旧 repository 快照，再执行同一个具体升级操作；不能调用新 Restore 直接读取旧 schema。新备份由新代码按实际新数据库生成。

Store 旧库（包括本次已实现但未部署的 v3）不参与转换；普通打开旧 Store 仍 unsupported，最终只 Reset 一次。Control 的旧文件/备份由 §3.2 的已有读取边界处理。Cadence 文件逐字保持，不增加升级阶段。

## 5. 最小纵向实施与验收

先冻结当前生产版本生成的非空旧样本，再修改模型。不能在新 writer 写出的数据上删除几个字段，冒充真实旧格式证据。

| 工作包 | 范围与完成条件 |
|---|---|
| A：Timeline | 单一 RowId、外层 codec/SQL、witness/proof、限定离线升级一起贯通；原分区和所有行保持 |
| B：Control | 单一 bootstrap、2/3 源格式投影和 writer4、实际 command 变化范围、receipt/backup/restore 验证 |
| C：生产消费者 | Store4、Manager/Runtime/Getter、CLI/Galatea/JS、cursor 与 analyzer 同步收口；无旧字段过渡 API |
| D：集成与文档 | 固定旧样本与纵向回归，更新当前合同/CLI 操作入口，记录实际验证与最终部署条件 |

这些是协作分工，不是四次部署。首条闭环为：**非空旧 Timeline/Control 测试副本 → Timeline 离线升级 → 旧规则读取 → 空新 Store 经真实 Manager/Runtime 与假 provider 构建 → Getter/CLI/Host 读取**。保留多行两列的精确前驱正文检验，不以只构造新 DTO 代替生产链。

| 场景 | 必须证明 |
|---|---|
| 原 RowId golden、旧 descriptor 固定字节 | 原 RowId 完全不变；新外层格式不含第二身份，坏 RowId/body 仍拒绝 |
| 非空且含分叉的旧 Timeline | 全部行/前驱/分区/测量/规则、head、selected path/root 原值保持；冷开后能继续新行与分叉 reconcile |
| 转换失败/中断/重复执行/有活跃 handle | 不发布部分数据库；锁与结果语义明确；成功后重复执行不改 generation |
| 新 reader 打开旧 Timeline/Store | unsupported，源 bytes 不变，无 provider 调用；旧样本仅由明确离线入口处理 |
| stale witness、deselected row、错 ref/head/raw fence、reserve 不足 | 删除第二 hash 后仍在提交前拒绝，不把旧行存在等同于当前有权使用 |
| 非空旧 Control bootstrap；v2/v3 读取、备份、恢复、下一真实 mutation | 规则键/receipt 原值保持；纯读保存旧 Head/bytes；新写 v4；旧 backup 与 receipt union 仍有效 |
| registration 固定语料与实际跨提交窗口 | §3.3 的变化范围成立；旧 recipe receipt 按新命令确实 Conflict，不能把边界声明当兼容证据 |
| 未受影响的旧 family/definition receipt、promotion 命令与 runtime golden | command/runtime 原值保持，正常可重放；Store Reset 前 promotion 证明条件单独保留 |
| Manager/Runtime/Getter，Overlay、零列前驱、missing-only、through 选择 | 按原 RowId 正确匹配；独立 expected 仍拒绝错误行；首个结果与前驱正文不变 |
| fulfilled FK/排序/分页与导出 cursor | through 精确指向正确行；新分页不漏不重；旧 v1 cursor 明确拒绝 |
| Cadence seal/reconcile 与持久文件 | cadence.json、策略/Head/DomainDigest 原值不变，保留 recent raw 量与崩溃恢复 |
| 非空旧 Prepared，旧 Store 不可用 | canonical request、commitment、完整 ExactContextInputs 原值保持，恢复零 recap；新 fresh 请求才用重建结果 |

规划时只读检查了现有 `LegacyV2/repository.zip` 与 `ControlReceiptV2` 三个 ZIP：四个 Timeline 都是 schema 2、rows=0。它们继续证明既有 Journal/Control 边界，**不能证明本次非空行格式升级**。新增样本至少含非空 bootstrap、多个行、selected 与 retained 非selected 行，记录生成基线；旧 ZIP 原字节保留。

实现后按影响串行验证 Timeline/其 public surface、Cadence、Control/AgentControl、RecapGrid Abstractions/Store/Manager/Runtime/Getter/Hosting/Online/WalkingSkeleton、相关 public surface、CLI、Galatea 与 analyzer；不为文档规划重跑上一切片全部测试。统一 `--no-restore -m:1 -nr:false`，测试加 `-- xUnit.MaxParallelThreads=4`；Server 使用 [E2E 指南](e2e-testing.md)的精确三类 Live 排除，并清除 live opt-in。独立 build Server/CLI/Timeline/RecapGrid，检查 JS 与 scoped docs。数字只在真正执行后填写。

## 6. 最终真实切换条件

仍在全部计划内代码重构完成后统一执行；本计划不操作真实 `.atelia`，也不生成暗含授权的删除清单。

先清点所有仍支持继续执行的分支、其 Timeline scope 与 pending Action。用仍能读旧状态的匹配版本，正常推进以下调用到对应 ToolResultObserved 或更后状态，包括同一 Action 内尚未轮到的调用：

- 本切片实际受影响的 **Recipes 非空 registration**。family/definition-only 的命令不变，不因本切片额外执行它们。
- 已有 Store 最终清空前要求收敛的 **promotion**。其 command 不变，但当前工具先检查 Store 构建证明，后查 receipt。

无法正常收敛的该数据集暂缓切换，不改 Journal、receipt 或成功结果来绕过。没有把任意历史曾执行的命令都重放一遍，也不建常驻旧命令计算器。

之后停服、保留完整匹配备份 → 在完整隔离副本对需要继续使用的 Timeline 库执行窄升级并核验保留数据 → 全部成功后按既有机制 Reset Store → 最后统一 LLM 重建所需活动 recipe 及必要 base 闭包 → 核验后恢复使用。Control 在新代码正常写入时升格式，Cadence/Journal 原文件保留；不默认重建全部 inactive recipes。

恢复冻结请求仍使用 Prepared 中已保存正文。回退使用匹配代码与完整数据备份；不得把新旧 Timeline、Control 和 Store 随意拼回一个目录。

## 7. 辩证裁决与简化收益

三位 reviewer 分别担任需求怀疑者、最小架构师、语义守卫；首轮独立读源码，第二轮交换最强反例，主线程复核。争点已收敛，不需要第三轮或额外产品决定。

| 争点 | 裁决 | 反例或修订 |
|---|---|---|
| 同 body 双 hash / 双行身份 | merge / delete | 保原 RowId 已覆盖全部事实；删除第二运行 type、生成和重复校验链 |
| 顺手换 UUID、改 ID body 版本 | defer | 会改变前驱、recipe bootstrap 和整条 selected path；本次没有收益消费者 |
| Timeline/Cadence/Control 全部离线转换 | simplify | Cadence 无对应持久字段；Control 可沿现有源格式投影，只有 Timeline SQL 需要专用升级 |
| Control 为了单字段必须离线转当前文件和全部备份 | 撤回 | 旧 backup 要求源 Head/hash 对应；复用现有 codec 可免第二套转换及 manifest 处置 |
| registration domain 和整个 tool runtime 一起 bump | delete 额外变更 | 会阻塞未受影响的 family/definition 与 Frozen；实际 Recipes 非空 preimage 变化已足够 |
| Store 内容/结果图转换、旧 cursor reader | delete | 旧 Recap 可丢弃；新 cursor 明确拒绝旧语义即可 |
| 行存在就算有效 witness | keep 原作用域与因果校验 | rewind 后行仍存在，但可能不再选中；reserve proof 还有真实 raw/policy 条件 |
| 把旧零行 ZIP 当升级成功证据 | 修正验收 | 只读实查四个 rows=0，需先冻结非空/分叉旧样本 |
| 原 RowId/Recipe/TimelineHead、路径算法、receipt 与冻结请求 | keep | 都有当前持久或恢复消费者，不因删冗余字段重编码 |

在所查 Timeline、RecapGrid、Cadence、Galatea、CLI、analyzer 六个生产目录，目标符号及拼写变体命中 30 个源文件，Cadence 为 0；这是当前传播范围，不是预计删除行数。可确认的减少是一种公开 digest 类型及其生成/存储/参数/比较链；没有增加新的行身份。升级路线收缩为一种 Timeline 专用转换，取消 Cadence 迁移、Control 离线转换和旧 Store 整图转换。

完成时当前生产链及新写入数据不再含第二行身份。只允许明确的旧 Control wire DTO、离线 Timeline 源解码和固定历史证据保留旧字段名；不得借兼容之名把它投影回运行对象。实施文档记录实际提交/验证，待办只保留未完成工作。

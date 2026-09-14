# RecapGrid Store 简化：实施计划

> 状态：工作包 A/B/C 已实现并通过本地验证：25 个项目、1,771 项通过，0 失败、0 跳过；尚未部署。设计经过三视角独立审查、交叉质询和剩余争议裁决。
> 日期：2026-09-14；设计基线：`4f718d87`；实施基线：`e7693a64`。
> 承接[身份简化总设计](identity-simplification-design.md)。本轮代码、测试与文档已完成；真实清库、部署与 LLM 重建留到全部重构完成后。

## 1. 最小模型与需求来源

**把 Recap 视为可以整体丢弃、重新生成的派生内容。旧数据不转换；所有重构完成后，最后统一 LLM 重建。**

```text
CellSlot = (RecipeDigest, HistoryRowId, LogicalColumnId)
同 Slot 重试 → 读取首个已提交 Cell
RowResult = 同 recipe / history row 的有序成员 + 已提交前驱
CellId / RowResultId = Store 分配的普通随机 128-bit ID
```

不再需要单独 EvaluationKey 或正文/投影 hash。规则快照、历史行、已发布前驱都不可变，Slot 已确定求值输入。Slot 是普通结构化工作坐标，不编码成新的 hash、拼接字符串或注册表条目。

| 要求 | 来源 |
|---|---|
| 丢弃旧 Recap，不保旧正文或旧缓存命中；重构结束后最后统一重建 | 用户最新明确决定，取代旧方案的保留/转换前提 |
| 允许取消跨 recipe 同输入或同正文的自动缓存共享，包括首行共享 | 用户采纳需求精简；这是 Slot 粒度的明确代价，不能宣称新旧缓存行为等价 |
| 保留 Journal、冻结请求、规则配置和 Control 生效回执 | 实际持久消费者；用户放弃的是派生摘要内容 |
| 正常运行仍沿用同 Slot 首个结果、missing-only 恢复、逐行前驱、预算和原子发布 | 当前 Manager/Store/Runtime；清旧缓存不等于每次重启重算 |
| Overlay 显式引用 base cell，不改其来源 | 当前 `RowBuildAssignment.Reuse` 与 bootstrap 执行链 |
| 个人未发布项目，无下游兼容或防恶意篡改需求 | 用户与根 AGENTS.md；不新增兼容框架、全局 ID 服务 |

故障模型是本地进程崩溃、重开、并发构建与提交结果不明。新运行周期不支持原地改变已发布规则/结果、局部删除 winner 或跨 Store 拼表；整个 Store Reset 更换实例。这些边界让 Slot 唯一确定输入。

## 2. 切片范围与来源证据

本切片贯通 Abstractions → Store → Manager → Runtime → Getter → Hosting/CLI/Galatea 消费者。Timeline、Cadence、Control 的持久布局、规则内容键、command/runtime 编码和 Prepared 格式保持。

| 源码位置 | 事实及设计意义 |
|---|---|
| [ManagerRowBuild](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs) `DeriveRowPlan` | 前驱必须属于同 ref/Timeline/recipe/target，并是 Timeline 指定的上一行 |
| 同文件 `DeriveAssignments`；[ArtifactContracts](../../prototypes/SessionJournal.RecapGrid/Abstractions/ArtifactContracts.cs) | recipe 固定列与 definition；Evaluate 使用当前输入，Reuse 引用同历史行 base cell |
| 规划基线 `4f718d87` 的 `Store/SchemaV2.sql`；当前 [SchemaV3.sql](../../prototypes/SessionJournal.RecapGrid/Store/SchemaV3.sql) | 旧版 cell 按 evaluation hash 唯一、SQL 与 canonical BLOB 并存；v3 改 Slot 唯一与普通结果 ID |
| [SqliteRecapGridStore](../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs) `PutCell/PutRowView` | cell 返回首个 winner；row 改按业务关系比较并返回实际持久 winner |
| [Getter materializer](../../prototypes/SessionJournal.RecapGrid/Getter/RecapGridContextMaterializer.cs) | 原内容等价仅用于 provenance 诊断；现改来源关系与正文/数量预算 |
| [RuntimeContracts](../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimeContracts.cs)、[RuntimeHosting](../../prototypes/SessionJournal.RecapGrid.Hosting/RuntimeHosting.cs) | outcome/work 关联和日志统一使用 Slot、StoreIdentity 与必要前驱 ID |
| [SessionRequestManifest](../../prototypes/SessionJournal/SessionRequestManifest.cs) `SessionRequestContextInput` | 已冻结输入保存 ContextSnapshot 正文，无 Cell/Row/Store ID，不参与旧缓存转换 |
| [AgentControlTool](../../prototypes/SessionJournal.RecapGrid/AgentControl/RecapGridAgentControlTool.cs) `PromoteAsync` | 先查 Manager progress/fulfillment，后调用 Control 并查 receipt；清 Store 会影响未完成 promotion |

不先搭一个只有类型定义的 vNext 平台。普通 ID、Slot、SQL 表示与真实生产链在一个可验收切片中收口；可以拆开发工作包，但不保留双运行模型。

## 3. 具体设计边界

### 3.1 位置与来源

Cell 的唯一业务位置为 `(RecipeDigest, HistoryRowId, LogicalColumnId)`，三个字段均非 null。复用现有不可变 RecipeDigest 和 HistoryRowId；由 recipe 的 target 找 definition，不新建规则版本注册器。新 Cell 的 Slot 必须与 frozen spec/目标列一致，前驱必须先持久发布。

同 Slot 只有一个合法输入来源：recipe 固定规则和列，Timeline 行固定历史及上一行，同 recipe/上一行的 RowResult 唯一且不可变。把前驱 ID 再放进唯一键没有增加区分能力；规则或前驱不匹配的调用应拒绝，不能造一个新 key 绕过校验。

删除 `EvaluationKey`、`EvaluationKeyDigest`、`ContentDigest`、`PriorInputProjectionDigest`、`PriorInputReference` 及其专用 DTO、编码/解码/对账链；前驱有无由现有行关系表达。`CellDigest/RowViewDigest` 改为普通 `CellId/RowResultId`。`FromCells` 是上个无格式切片的过渡性计算入口，本切片随旧投影 digest 一起删除。

Slot 同时用于 missing-work 查询、executor outcome 关联和 Store 唯一约束，避免为同一工作再引入另一种 ID。验证 outcome 正好对应 frozen missing-work 子集，拒绝重复、缺失和错位；不以相同 ordinal 认领另一个 batch 的结果。

不另存 cell 的 `ProducerPreviousRowResultId`。来源按需推导：

```text
cell.Slot.HistoryRowId → Timeline.PreviousRowId
(cell.Slot.RecipeDigest, PreviousRowId) → 已提交的源前驱 RowResult
```

本行只提交部分 cell、尚无本行 RowResult 时，前驱仍已存在，所以该推导不依赖本行发布。前驱缺失是缺失/无效，不是 FirstRow；没有历史前驱才是 FirstRow。有前驱但零列的 RowResult 仍有真实 ID，不能折叠成首行。

### 3.2 普通 ID、唯一性与提交结果

采用 Store 分配的普通随机 128-bit ID（例如 `Guid.NewGuid()`，SQL 可存 32 位文本），不从内容生成。保留现有 StoreInstanceId、frozen authority、handle/lease 与 Reset 边界；不引入额外 `(scope,id)` 公开类型、号段或全局注册器。跨 Store/Reset 后旧 ID 正常返回不存在即可，无须新增 WrongStore 分类；旧 StoreIdentity 不得继续认领新实例。

Store API 的语义收口为：

```text
PutCell(spec, internal draft) → Inserted(Winner) | AlreadyFilled(Winner)
PutRowView(spec, stored cells) → Inserted(Winner) | AlreadyPresent(Winner)
```

具体参数复用现有已验证业务类型，不为示意签名新建一套 DTO 框架。ID 由 Store 分配，所有成功路径返回实际持久记录。cell 同 Slot 即使竞争正文不同也返回 first-winner；row 同 assignment、同业务成员与前驱返回已存 row，成员/前驱不同才 Conflict。候选随机 ID 不参与业务相等。

提交结果不明时按 Slot 或 row assignment 重新观察，沿用既有明确/不明确结果语义；不能用候选 ID 查不到就断言未提交。新 Store 内不增加局部缓存淘汰或改写前驱的功能。

### 3.3 SQL 一份数据与单一 reader

本轮 schema 确认为 v3，DDL owner 为 `Store/SchemaV3.sql`。保留现有物理槽位 `derived/recap-grid/v1/grid.sqlite`，目录名不是 schema 版本；不为过渡期建设第二套运行 Store。

新表至少表达：

- cell：普通 ID、源 Slot、正文/结果字段；`UNIQUE(recipe, historyRow, column)`。
- row：普通 ID、assignment、前驱 ID、必要状态；assignment 唯一，前驱 FK。
- row members：row、ordinal、已存 cell 引用；列唯一、顺序明确，允许合法 Overlay 引用 base cell。
- fulfillment：既有精确 Timeline head/recipe/through 选择条件 → 已存 row；保留 scope 与前沿验证。

删除 `cell.canonical`、`row.canonical`、`fulfilled.key_canonical` 全量副本及其读回对账。正文存正文列；对象由 SQL 列和成员关系物化。导出是临时投影，不作为第二份持久权威或生产回读格式。具体 DDL、read/write/verify/export 方法须在实施包内一起落地，保留现有分页、数量、正文和事务上限。

Slot 的唯一键没有 nullable prior，因此不需要 FirstRow 专用唯一索引或虚构 row 0。普通启动遇旧 schema 仅返回 unsupported，不自动 Reset、转换或调用模型。旧 Store 不留 reader/迁移分支；显式离线 Reset 可复用[现有机制](../../prototypes/SessionJournal.RecapGrid/Store/StoreMaintenance.cs)，不要求解码旧 cell。

### 3.4 Runtime、Overlay 与诊断

Runtime 保留独立 frozen spec、history/recipe/target、前驱 RowResult 与实际 cells 的顺序/成员校验。模型输入仍为原有正文渲染协议，不把普通 ID 或 Slot 塞进 prompt。`RecapRewriterProtocolV3` 的 prior schema 名称不因删除内部投影类型而变化。

Evaluate 属于当前 Slot；Overlay Reuse 必须引用明确的同历史行 base cell，验证列和 definition，保留 base Slot。不能强制每个 member 都属于 candidate recipe，也不能给复用 cell 重写来源。

Getter 的旧 `PriorInputAligned` 替换为 `PriorSourceAligned`，定义为来源关系一致性，不承诺内容等价。用 cell 源 recipe 与历史前驱定位 source row，与当前 row 的前驱比较；合法 Overlay 复用可为 NotSatisfied，不能因此拒绝正文或触发重建。缺来源或预算耗尽为 Incomplete；FullRebuildChain 不能把 Overlay 冒充 full。输出与测试同步使用新语义。

移除 `ExaminedCanonicalUtf8Bytes` 等旧对象编码字节度量，改为 `ExaminedContentUtf8Bytes` 加独立 `ExaminedRows/ExaminedCells/ExaminedMembers` 数量预算；明确度量单位变化，不为计量重新编码已删除的对象。Runtime/Hosting 日志直接记录 Slot、已有 StoreIdentity 和必要前驱 ID，不生成替代 hash 或用旧 digest 名称承载新值。

## 4. 实施工作包与验收

| 工作包 | 完成条件 |
|---|---|
| A：模型与 SQL | 新 ID/Slot、DDL、唯一约束、FK、SQL 物化与实际 winner 返回一起落地；删旧 hash/canonical 表示 |
| B：生产链 | Manager/Runtime/Getter、telemetry、CLI/Galatea 消费者贯通；Overlay 与来源诊断收口 |
| C：集成与文档 | 删除旧 API/fixtures 依赖，验证新 Store 与保留的旧 Journal/Control；更新当前说明与实施记录 |

按自然消费者分工，生产键/SQL 只有一个负责人。API 删除与调用方一次收口，不提交长期双构造形状。完整实现前可做小规模 DDL/固定输入实验，不用真实数据重建试错。

| 场景 | 必须证明 |
|---|---|
| 实际 Manager → Runtime → 假 provider，多行两列 | 每行收到精确前驱正文/顺序，关闭重开后同 Slot 零新增调用 |
| 同 Slot 并发生成不同正文；本行部分 cell 已提交后中断 | 仅一个持久 winner；重开只补缺失列，来源仍可由 Slot 推导 |
| row 并发发布与提交结果不明 | 返回实际已存 row；同成员/前驱不因 ID 不同冲突，真差异拒绝；按 assignment 结算 |
| 同输入/正文但不同 recipe | 不再隐式共享；同 Slot 正常复用，明确替换旧内容等价缓存测试 |
| Overlay bootstrap、嵌套 Overlay、KeepUnchanged | Reuse 保留 base CellId/Slot，无额外模型调用；正常后续行仍逐行推进 |
| 首行、零列前驱、错前驱/列/规则/outcome | 空列行仍发布并推进；非法输入在 provider 调用前拒绝，不能覆盖独立 expected |
| Reset 与陌生 ID | 新实例/新结果 ID；旧句柄失效，陌生 ID 不读到其他正文；不局部删除 winner |
| SQL read/verify/export 与预算 | 同一份数据物化、必要 FK/唯一性、页/正文/成员上限；诊断不保旧 hash 算法 |
| 旧 schema 打开与离线 Reset（隔离副本） | 打开明确 unsupported 且不改文件、不调模型；显式 Reset 后可构建新库 |
| 非空旧 Prepared，旧 Store 已移除/不可用 | canonical request、commitment、ExactContextInputs 不变，零 recap；下一 fresh 用新库内容 |
| Control/pending promotion | command/runtime/receipt 不改；固定跨提交窗口说明 proof-before-replay 边界，最终处置条件可验证 |

现有旧 Journal/Control 固定样本原字节保留；允许在隔离测试副本重置其旧 Store，不为让新测试通过而改写冻结正文或工具回执。旧 Store 的 hash golden 退出新模型，不能批量更新成另一串 hash 冒充简化。保留 Timeline/Control/Prepared 的现行格式证据。

重 .NET 验证串行使用 `--no-restore -m:1 -nr:false`，测试按最终符号影响覆盖 Abstractions/Store/Manager/Runtime/Getter/Hosting/WalkingSkeleton、相关 public surface、Online/AgentControl、CLI、Galatea；Server/CLI 独立 build。Galatea 用 [E2E 指南](e2e-testing.md#离线与非-live-命令)的精确 Live 类过滤。实际验收结果见 §7，不引用上个切片的 1,454 项作为本切片完成证据。

## 5. 所有重构完成后的真实重建

本切片代码完成也不立即重建真实 Recap。剩余计划中的改造完成并验证后，才按[总设计 §7](identity-simplification-design.md#7-保留范围与最终重建边界)执行一次最终处置：保留历史/规则/receipt，清旧 Store，初始化新 Store，最后统一 LLM 重建。

清库前须用仍能读旧 Store 的版本，将所有仍支持继续执行的分支中 pending promotion 正常收敛到 ToolResultObserved 或更后状态；不能只查 receipt 或当前网页。未收敛就暂缓该数据集清库。普通 registration 在编码未变化时没有这个 Store-proof 前提；以后 Timeline/Control 命令若变化再按实际范围处置。

不默认重建全部 inactive recipes，不为旧 cell 保留建 GC 或映射工具，也不以自动重建掩盖普通启动的 schema 错误。真实部署时明确需重建的活动目标及其必要 base 闭包，沿用构建预算和故障恢复；本次没有做真实实例清点或给出可直接执行的清理清单。

## 6. 辩证裁决

三位 reviewer 先独立查源码，再交换最强反例；第三轮仅裁决 ID 形状和冗余来源字段。主线程独立核对调用顺序与数据所有权。结论靠支持的执行轨迹，不按票数决定。

| 争点 | 裁决 | 证据与变化 |
|---|---|---|
| 保全部旧 cell/row、反推 projection、整图 converter | delete | 最新用户决定使它们失去消费者；不换名为重建迁移框架 |
| 历史+规则+前驱结果组成新的 EvaluationKey | simplify → delete 独立模型 | 交叉质询确认 immutable recipe/row assignment 已决定输入，改普通 Slot |
| Cell/Row 内容 hash ID、canonical 双份数据 | simplify | 普通随机 ID 与 SQL UNIQUE/FK；Store 返回实际 winner |
| scoped Int64、额外 producer prior 字段 | 不采用 / delete | Int64 需额外公开作用域形状；来源能由 Slot 和已提交前驱推导，partial window 也成立 |
| 跨 recipe 内容等价缓存与旧 provenance 含义 | delete / simplify | 用户接受更低复用率；来源诊断不再声称正文等价，不留诊断 hash |
| 事务、first-winner、行前沿、Overlay 显式复用 | keep | 真实并发/中断和逐行输入消费者仍需要 |
| Control 字节不变就保证所有 pending 无影响 | 撤回 | promotion 先查 Store proof 后查 receipt；最终清库前正常收敛相关操作 |
| Timeline/Control 同时改、规则版本注册器 | defer | 本次 Store 切片无需它们；涉及保留数据/命令时再独立设计 |

本切片已删除独立 EvaluationKey、Content/Prior/Evaluation 三种 digest、两种内容寻址结果 ID 的计算和整对象持久 canonical 链；增加普通 CellSlot 与两种普通结果 ID。旧 Store 数据转换阶段完全取消，没有额外产品裁决阻塞本切片。

## 7. 实施记录与验证

当前实施采用 `CellSlot` sealed record、32 位随机结果 ID 与 SQL v3；实体名 `RecapCellArtifact/RecapRowView`
保留，成功 Store put 返回 `Winner`。完整的当前数据/API/operator 入口见
[Store v3 说明](../SessionJournal/current/contracts/recap-grid-store-sqlite-v3.md)。旧 v2 合同、旧工作包和前置输入
切片保留其历史证据，不要求继续保留旧 hash 模型。

CLI 导出使用临时 JSON 投影的 `JsonUtf8Bytes/Json/FulfilledRowResultId`，外层字段为
`jsonBase64/fulfilledRowResultId`；selection 输出 `rowResultId`。Galatea missing-work 只保留
ordinal、rowId、recipeDigest、logicalColumnId，不再输出 evaluationKey。

本轮主要提交：

| 工作包 | 提交与结果 |
|---|---|
| A：模型/SQL | `ed9b113a`：Slot、普通 ID、actual winner、SQL v3 单份表示；`fdcb7c98` 收口 ID 命名 |
| B：构建与执行 | `775dfa0f`：Manager/Runtime/Hosting 全链 Slot 与实际 winner；`9d0a7c3b`：Getter 来源诊断与四轴预算 |
| C：消费者与验收 | `cf067cbc`：CLI/Galatea 与模块边界；`700c9ce6`：Abstractions 新合同；`2c5033f7`：恢复/Reset 边界；`9f0f139b` 完成集成测试尾修 |
| 文档 | `3478540d`：当前 v3 说明与历史边界；本节补最终验证，完整实现范围为 `e7693a64..9f0f139b` |

三个实现包均经过独立只读 review，主线程复核跨包接缝。审阅中发现并修复：Row reader 隐式读取正文绕过
Getter 预算，现只读成员元数据；新增 bootstrap gate 错拒合法 partial fulfillment，现恢复既有 through 语义。
SQL 字段物化的非法值在读取边界转换为 typed Invalid，不把调用者参数错误吞成 Store 损坏。当前无未收口实质 findings。

本轮串行验证共 **25 个项目、1,771 项通过，0 失败、0 跳过**；最终测试日志无 warning。

| 测试项目（省略共同前缀 SessionJournal.RecapGrid） | 通过 |
|---|---:|
| Abstractions | 33 |
| Store | 62 |
| Manager | 83 |
| Runtime | 69 |
| Getter | 31 |
| Hosting | 29 |
| WalkingSkeleton | 27 |
| Online | 33 |
| AgentControl | 33 |
| Control | 86 |
| Cadence | 29 |
| Galatea.RecapGrid | 9 |
| 上述相关 public-surface 十项目 | 32 |
| Analyzers.Style | 88 |
| SessionJournal.Cli | 142 |
| Galatea.Server（精确排除三类 Live） | 985 |

public-surface 分项为 Store 5、Manager 3、Runtime 4、Getter 3、Hosting 7、Online 3、AgentControl 1、
Control 3、Cadence 2、Galatea.RecapGrid 1。项目命令统一为
`dotnet test tests/<项目>/<项目>.csproj --no-restore -m:1 -nr:false -- xUnit.MaxParallelThreads=4`。
Server 清除 `ATELIA_RUN_GALATEA_NOTE_LIVE`、`ATELIA_RUN_GALATEA_LAB_LIVE`、`ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE`，
使用精确过滤：

```text
FullyQualifiedName!~CharacterNoteTranscriptionLiveTests&FullyQualifiedName!~GalateaCodexDelegationLiveTests&FullyQualifiedName!~GalateaScenarioLabLiveTests
```

RecapGrid、Galatea.Server、SessionJournal.Cli 的独立 `dotnet build <csproj> --no-restore -m:1 -nr:false`
均为 0 warning、0 error。`node --check prototypes/Galatea/wwwroot/assets/galatea.js` 通过；文档检查
`python3 scripts/check_session_journal_docs.py` 为 39 files、0 diagnostics，`git diff --check` 通过。
本轮本机测试日志为 `/tmp/recap-store-test-<项目>.log`，汇总为 `/tmp/recap-store-validation-final.json`；
build 日志为 `/tmp/recap-store-production-build.log`、`/tmp/recap-store-build-Galatea.Server.log`、
`/tmp/recap-store-build-SessionJournal.Cli.log`。这些临时日志不是仓库持久证据，以上命令可重跑。

新增及迁移验收已覆盖实际 Manager→Runtime 的多行两列、跨 recipe 不隐式共享、并发/部分行首个结果、
提交结果不明后的 assignment 观察、Overlay 保留 base Slot、零列前驱、旧 schema unsupported/显式 Reset、
metadata-only row read 与四轴预算。旧 Journal/Control ZIP 原文件未改写。

非空 v7 Prepared 的四个 Host 恢复场景全部通过：两种 dispatch 边界分别测试保留和 Reset Store；恢复保持
canonical request、commitment、完整 ExactContextInputs，零 Recap 调用；Reset 后 fresh 请求从空 Store 经假 provider
重建五行两列。另保留原有进程 kill 恢复测试，未把普通冷重开冒称进程崩溃。

promotion 回归先真实构建并提交 Control receipt，再关闭 handles、Reset 测试 Store，同 operation 重试返回
`budget-exceeded`，Control bytes 与 Journal head 不变。它证明实际工具的 proof-before-replay 顺序，
不冒称 Journal failpoint 实验；真实清库前仍须按 §5 收敛 pending promotion。

本轮不执行真实数据清空、部署、LLM 重建或 push。下一切片见 [Timeline 单一行身份计划](timeline-row-identity-simplification-plan.md)：
保留原 RowId，删除第二个 descriptor 身份；Timeline 窄升级，Control 复用旧格式投影，Cadence/Recipe 内容键保持。
该计划尚未实施，不改变本节 Store v3 的完成证据；全部重构完成后才统一处理真实 Store。

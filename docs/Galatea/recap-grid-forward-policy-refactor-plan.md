# RecapGrid：旧记忆持续可用，新维护策略向前生效

状态：**三轮辩证审查后收敛的实施设计，尚未实施。** 2026-09-16。
实现入口：[工作单](recap-grid-forward-policy-work-order.md)；[Terra 施工提示词](GOAL-recap-grid-forward-policy.md)。
基线：`426eb987dfbe63eaee860724a5765addba989e1c`。源码若前进，先检查差异，不以本文代替现场事实。

最小模型：Control 继续选择一份已采用的构建根；每个历史行在该根内有唯一、持久的维护工作 RowWork。
RowWork 保存实际选用的生成规则和真实前驱。新默认策略只用于尚无 RowWork 的行。
已有摘要按自身来源读取；主动重建产生另一构建根，完成后由原有 Control CAS 采用。

## 1. 需求账本与授权边界

| 编号 | 要求 | 来源 |
|:--|:--|:--|
| U1 | 提示词、模型或角色显示名变化不得使已有有效 Recap 不可读，也不得仅因此阻断活动 | 用户明确赞同的产品原则 |
| U2 | 新规则影响未来生成；正常整理承接旧摘要与新增经历，不默认重算过去 | 用户明确赞同的分析 |
| U3 | 重建旧历史及采用重建结果是主动操作 | 用户明确决定 |
| U4 | 保留真实 producer、前驱和覆盖，不把旧摘要改标为新规则生成 | 用户赞同的分析；现有 provenance 消费者 |
| C1 | 真实旧数据与 Prepared 存在；无公开下游兼容义务，但保留已存事实 | 源码、实例记录、AGENTS |
| C2 | 单进程 owner、每角色 TurnLock；Control 文件 CAS 与 Store SQLite 分域；会取消、崩溃、重启 | Host、Control、Store |
| C3 | partial cells 可先落盘；Getter 有 reserve、nthPrevious；支持 Undo/重新选择历史 | Manager、Getter、Timeline、测试 |
| C4 | 正常逐行维护不能随策略变化回根重建，不能新增按行累积的 Control 上限 | 当前 C3 实现及测试 |

产品原则由当前用户请求授权；技术结构是本轮代码与反例收敛的方案，不冒称用户逐字段批准。
后续用户将工作单交给实现者时才启动实施。本轮只写文档。参考文档中的命令与状态标签不自行授权行动。

不包含：真实实例迁移/重建、服务启动、NuGet 发布、Completion 上游改动、策略热更新服务、分布式协调、权限平台重构。
32 KiB 超长重试另行处理，不放宽上限。fresh 角色没有新增 AgentControl 工具需求。

## 2. 当前为何阻断

| 当前入口 | 事实 | 本次责任 |
|:--|:--|:--|
| [TargetAlignment](../../prototypes/Galatea/GalateaRecapGridTargetAlignment.cs) | 当前 V7＋角色名与 active target 不同就拒绝 | 删除历史等于当前默认的门禁 |
| [Readiness](../../prototypes/Galatea/GalateaRecapGridReadiness.cs) | 当前及 nthPrevious 重复比较最新 target | 按实际产物验真 |
| [Composition](../../prototypes/Galatea/GalateaRecapGridComposition.cs) | fresh RequireCurrent；Prepared 独立精确绑定；fresh 无 AgentControl tools | 接新维护入口，保留 frozen 路径 |
| [ManagerRowBuild](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs) | 要求前驱同 recipe/target；从 recipe 取生成定义 | 按 RowWork 构造 |
| [SchemaV4](../../prototypes/SessionJournal.RecapGrid/Store/SchemaV4.sql) | 前驱 FK 含 recipe 和 target；cell 槽仅 recipe/row/column | V5 保留同根邻接，允许 producer 变化，cell 按 work 定位 |
| [BuildContracts](../../prototypes/SessionJournal.RecapGrid/Abstractions/BuildContracts.cs) | Overlay 重算变更列历史；Full recipe 为内容身份 | 区分构建根和逐行 producer |
| [Getter](../../prototypes/SessionJournal.RecapGrid/Getter/RecapGridContextHandle.cs) | 整条前驱链用同一 active recipe 校验 | 每行依据实际 target/definition 校验 |

真实事故见[本机升级验收](recap-grid-autonomy-asset-upgrade-20260916.md)：heartbeat prompt 改动使 V7 Family 改变，
旧数据内部 verify 通过，仍被 `character-asset-mismatch` 拒绝。上一轮重建是旧实现的操作性恢复，不是目标产品要求。

## 3. 最小数据模型与唯一责任

### 3.1 四个概念

- **HistoryRow**：Timeline 唯一规定历史位置、前驱、覆盖与 selected lineage。
- **RootRecipeDigest**：复用既有 recipe digest，表示已采用或候选的构建空间。Control.ActiveRecipeDigest 仍是唯一采用入口。
  普通策略更新、逐行维护、Undo 均不创建新 root、不修改 active。Full/Overlay 是显式施工规则。
- **RowWork**：在 `(RefId, TimelineId, RootRecipeDigest, HistoryRowId)` 下唯一，选择后不可变。
- **Cell / RowResult**：成功事实；完整 row 才作为新摘要读取。不另建 adopted-head/choice 表。

root 表示“采用哪条解释链”，不承诺整条链所有行使用同一提示词。
新 API/报告区分 RootRecipeDigest 与 ProducerTarget；旧格式字段保留原解码语义。
禁止大范围字符串重命名悄悄改变持久格式。

### 3.2 RowWork

```text
RowWorkKey = (RefId, TimelineId, RootRecipeDigest, HistoryRowId)
RowWork = {
  Key,
  PreviousRowResultId?,
  ProducerTarget,       // 有序(LogicalColumnId, DefinitionDigest)，保存 canonical bytes/digest
  OrderedAssignments   // Evaluate；仅 explicit Overlay 可 Reuse(CellId)
}
WorkId = domainSeparatedDigest(canonical(RowWork))
NewCellSlot = (WorkId, LogicalColumnId)
```

ProducerTarget 的完整有序映射须可恢复，不能只存没有实体可查的 digest。
definition/family 正文继续由 Control 不可变注册表保存，不复制到每行、不建第二个 policy registry。
OrderedAssignments 纳入 WorkId；Overlay 复用来源是实际输入，恢复时不能按今天的可用 cell 重选。
正常 Galatea 两列都是 Evaluate，共用完整 prior pack 和一个持久 RowWork。
Runtime 渲染 prior pack 时使用 prior row 各 cell 的实际 definition/header，不能用新 work 的定义重标旧正文；
即使新旧 logical column 相同、只有角色名或 semantic heading 变化，也要用不同值的 fixture 验证这一点。

首次选择在任何 provider 调用之前事务落盘。无 work 才消费当前默认；已有 work 永远优先。
竞争/重启时读取同 key 的已存赢家，不覆盖。Store 直接提交同 key 不同内容可返回 selection-conflict；
正常“默认 P2，已选 P1”的恢复不是错误。

不存 Planned/Running/Done：work 存在而无 row 即待完成，cells 决定缺列，完整 row 是完成证据。
不冻结可替换的 provider/model，WorkId 不含连接、重试次数或进程身份。
现有 [ModelSwitchAfterPartialFailureBuildsOnlyMissingCell](../../tests/Galatea.Server.Tests/GalateaRollingRecapGridHostTests.cs)
允许保留 A 列、换模型补 B 列，必须继续通过。主线 Prepared 的精确绑定保持独立。

### 3.3 前驱

同 root 内一个 HistoryRow 只有一个 work；其 prior 必须是同 Ref、Timeline、root、直接 HistoryRow 前驱的完整 RowResult。
首行 prior 为空，非首行必须有真实 prior；ProducerTarget 可以不同。
由根行归纳，同 root 的 prior 不会被后来的策略或 promotion 改写。替换已有解释必须使用另一 root。
不同 root 不拼接尾部。Overlay 的 cell 复用是显式成员引用，不是跨根伪造前驱。

## 4. 正常维护与崩溃

```text
持有角色 TurnLock / 正式 owner:
  先补当前 root 的欠账，再按现有 cadence 至多 seal 一行
  定位 selected lineage 上下一份未完成 row
  if work 不存在:
      取得同根完整 prior
      选择 host 当前默认 target（explicit build 则为候选施工规则）
      持久注册所需 definitions/families
      Store 事务创建 RowWork
  读取成功 cells，只执行缺列
  Store 事务发布完整 RowResult + 现有 fulfillment/progression 证据
```

无维护需要时，升级零调用、零新 work。backlog 中尚未选择的行属于未来生成，可用新规则。
每行都持久选择，不只在 target 改变时保存；注册定义不代表采用，不靠 cell 出现推测选择时点。

| 崩溃位置 | 恢复 |
|:--|:--|
| 注册定义后、work 前 | 没有选定 work，下一次可采用当前默认 |
| work 后、首调用前 | 零 cell 也恢复原 work |
| 一列成功后 | 只补另一列 |
| provider 返回后、cell 提交前 | 沿现有纯生成/结算规则；不承诺远端 exactly-once |
| cell/row commit 不明 | fresh reopen 按 WorkId/slot/row 确认，不先重发 |
| row 完成但响应丢失 | 读取已提交结果 |
| 显式候选完成、promote 前 | 仍采用旧 root |
| promote 结果不明 | 重开 Control 判断 active，不重建完成 cells |

保留每 pass 一行、外层 call/time/step budgets、typed settlement 和取消边界。
正常 append 从 row assignment/progression 增量恢复，不因换策略从根扫描。
Undo/reselect 可处理实际变动后缀，不新增任意行数天花板。

## 5. 读取与历史重选

Getter：Control active root → Timeline selected row → 同 root/row 的 work 与完整 result。
不要求最新策略或维护 route 可用，不做 LLM、采纳、schema 迁移。
row 未完成仍是 unfulfilled，由既有 Online 的预算/backpressure 处理，不能无限跳过欠账使用过旧摘要。

reserve、nthPrevious、provenance 沿真实 PreviousRowResultId；各行验证自身 ProducerTarget、列与 definition。
保留 scope、selected lineage、邻接、覆盖、完整性、stale fencing。历史 heading/name snapshot 不动态替换。
主线仍使用当前角色身份，旧名字不成为阻断理由。

例：R/H5 → R/H6(P1)；Undo 到 H5 后分叉 H6′，可选 P2；回原 H6 时命中原 P1 work。
BootstrapThroughRowId 只约束显式施工/采用，不能成为 live root 的持续有效条件。
ManagerAuthority 对 live 路径不得强制 bootstrap 仍在 selected path；explicit candidate 验证保留。

诊断可展示 root/work/实际 producer，不把“非最新默认”显示为 invalid，不新增强制用户确认的升级界面。
旧 Prepared、LegacyStarted、tool continuation 走原冻结恢复；不先维护摘要或加载新 route。
回归必须证明 recap calls=0、routeLoads=0、Control 未变。

## 6. 显式重建与采用

Full/Overlay 仍是 operator 候选；普通策略更新不走 compose/build-all/promote。
任何 build 都尊重已有 work。`build --recipe R` 是完成/检查 R 的工作，不是强制改写。
progress/fulfilled 证明工作完整，不宣称后来所有行都由 root.Target 生成；export 显示实际 producer。

选择规则由调用目的明确给出：Host 的 live 维护传入当前默认 target；CLI explicit candidate 的新 work 使用候选施工规则。
CLI `build --live` 增加 `--producer-target <canonical BuildTarget 文件>`（拟新增），用于无 work 行的首次选择，
所引用 definition/family 必须已正式注册。已有 work 的续跑及已完成检查不要求该参数；若确有未选工作却未提供，
返回 typed `producer-policy-required`，不能偷偷从 active root.Target 推断最新策略。`progress` 仍纯读，无须提供策略。

重解释旧行须显式创建新 root。Full 的相同 target/through 可能命中已混合 producer 的 live root，
因此新增 **Full recipe canonical V2 的 OriginRootRecipeDigest**：固定为本次重建观察到的 active root。
它表示重建从哪份已采用解释发起、参与候选身份；不是 BaseRecipe，不递归构建或计入 base depth。
同 source/规则/边界可恢复同一候选；采用后再次重建以新的 active 为 origin，产生新 root，
不用另一个 epoch/migrationId。旧 Full/Overlay canonical bytes、digest、receipt 不重写；新 bootstrap 无 active 时 origin=null。
Overlay 已有 base 参与身份；若请求命中已采用根，应明确要求新候选，不能隐式改写。

Overlay 逐行验证实际 base cell 的 definition、来源前驱与目标列，不能仅比较 baseRecipe.Target。
不满足固定候选规则时返回 typed `overlay-source-incompatible`，要求显式调整重算列或使用 Full；
不得自动扩大重算范围/调用预算，不得将旧 cell 冒称新规则产物。冻结的 Reuse(CellId) 恢复时不重选。
原有更强 provenance 拒绝条件保留，本轮不放宽真实输入证明。

promotion 只有 Control CAS 一个采用点。CLI 与历史 AgentControl promote 仍走 proof→CAS，
proof 针对 exact root、Timeline、through 和完整工作。Store 不保存第二份“采用了谁”。
新规则注册用内容幂等 Put 或绑定真实 command digest 的 operation identity；
不能用固定 V7 asset operation key覆盖变化内容。

保留当前 `--through-row` 前缀 promotion：B 完成到 H5、当前历史 H10 时可显式采用，
B/H6…H10 成为操作造成的欠账，不得把 A/H10 拼到 B。已有 B work 继续原规则；无 work 才选当前默认。
CLI 提示这一 backpressure 后果。历史 frozen tool 的身份、权限、结果回放保持原合同。

## 7. Galatea 默认规则与稳定路由

当前 code-owned asset + character name 只在新 work 选择时使用。
退役 GalateaRecapGridTargetExpectation/RequireCurrent 和读取时 current-target 比较；健康检查由 Getter/Store 承担。
搜索 Host admission、Composition、Readiness、recent/current/Undo 与测试，不能只删一条异常。

Galatea 配置 V12 的 recapGrid live 部分采用稳定用途配置：

```json
{
  "maintenance": {
    "connectionId": "<现有连接ID>",
    "maximumConcurrency": 1,
    "dispatchTimeoutMilliseconds": 900000
  },
  "historicalAgentControlProfileFiles": ["<历史冻结恢复所需文件>"]
}
```

数值仅为形状示例，转换须保留原值。移除 live 对 routeManifestPath/currentAgentControlProfileId 的依赖；
独立 CLI 的 exact route manifest 保留。fresh 无 AgentControl，不自动扩旧 profile 的 family allowlist。
新 session bootstrap/默认定义注册由 host 针对 code-owned bundle 的窄入口完成，不依赖历史工具 profile，
不向角色增加任意注册权限。旧 profile bytes/identity 保留，历史 tool recovery 按需精确加载。

Host 为本次 persisted work 的 family 与支持的 protocol/semanticModel 构造 exact route，
connection/预算来自 maintenance。新默认 family 与旧未完成 family 都可执行。
neutral Runtime 仍 exact preflight；Hosting 只增按本次 work 绑定的小入口，不建设动态路由平台。
维护沿 GalateaCompletionOwner 同一 registry、retry invoker 和并发 lane；
每个 work 独立创建 semaphore 会放大并发预算，禁止这样接入。
主线 Prepared 的连接冻结不变。

新增 provider-free V11→V12 operator dry-run/apply。
旧相关 routes 的 connection/concurrency/timeout 全相同则机械采用；不相同时要求一次明确选择，不能猜第一项。
旧 profile 路径全部保留；角色、Player、状态路径等不变。不以旧 target 与当前默认不同拒绝配置。
有效但暂不可用的维护连接不阻断已完成 Recap 的读取；需要新生成时报告具体阻塞。

## 8. Store V5 与旧数据

必要结构升级；Control 文件外壳可保留，recipe codec 读 V1/新 V2。
Store 在既有位置升级 user_version/schema_version 为 5，不能偷偷切空目录使旧库不可见。

最小关系变化：

1. row_work：WorkId 主键，RowWorkKey 唯一；canonical target/assignments、exact prior 与结果的唯一关联。
2. 新 cell 用 work slot；旧 cell 原 slot/producer/ID 可读。work-member 关系关联新旧 cell，
   不能给 Overlay 共享 cell 改归属。新 draft 只用 work；旧 slot 仅用于真实持久对象解码/导入。
3. row_view 增 work 关联，保留现有 ID/member。前驱 FK 保留 scope/root/history，移除 target 相等要求。
4. fulfilled key 保留 scope、Timeline generation、through、root；验证真实 work/result，不因默认策略变化重建。

CellId/RowResultId 是现有 Store 分配的身份，**不是一律内容 hash**；不得重算或重新分配。
WorkId 才是新确定性身份。旧 recipe/cell 来源/旧导出语义有明确版本边界，禁止偷偷改解码含义。
旧 ID 对应内容、列及 producer 不变，不伪造一份“新规则生成”的旧摘要。

新增正式 `recap-grid upgrade-store-v5`（拟新增，当前不存在）：默认 dry-run，apply 需停服、锁、备份和明确 repo scope。
扫描该 Store 的所有 Ref/Timeline/recipe rows，不只 main；读取匹配的 Control/Timeline，无 provider。
副本验证后，以单 SQLite 事务改结构和回填；崩溃后只能完整 V4 或完整 V5。
verify/export/backup/restore 同步升级；只读打开 V4 返回 upgrade-required，不隐式迁移。

- 旧 row.RecipeDigest 为 root，Target 为实际 producer，exact prior/member 保留。
- 完成 rows 机械建立 work；非 active roots 仍是候选，不能因它们存在就采用。
- partial cells 由原 recipe、HistoryRow、同根 exact prior 恢复；Overlay 按真实来源建立关联。
- 无法唯一证明 prior 的 orphan partial：bounded typed 报告，停止 apply，不猜测、不删除。
- V4 从未保存的“内存已选但零 cell”无法恢复；无证据的行视为未选。V5 起所有首次调用前选择落盘。
- raw Journal、Timeline/Cadence policy、Delegation、CharacterMemory、Prepared payload 不在迁写范围。

实现验收后另行安排真实实例升级。默认只用合成仓/获准隔离副本，不再次操作 live .atelia。

## 9. 验收矩阵

| 编号 | 可观察行为 | 主要层 |
|:--|:--|:--|
| A1 | 旧 P0 完整、默认 P1、无维护需要：ready，0 calls/work/active变更 | Host/Getter |
| A2 | 新 H1 只生成一行两列 P1，准确引用 R0，旧内容/IDs/producer 不变 | Manager/Store |
| A3 | work后0cell崩溃，部署P2仍完成P1；下一份未选work才用P2 | Store/Online |
| A4 | A成功B失败，重启换模型只补B，WorkId/producer不变 | Runtime/Host |
| A5 | 不同prior不复用同cell槽；同root/row不能重绑 | Abstractions/Store |
| A6 | reserve/nthPrevious跨producer正常；错Ref/列/前驱/覆盖仍拒绝 | Getter |
| A7 | Undo早于bootstrap、分叉、重选旧后缀：命中原work，不按最新时间选择 | Manager/Getter |
| A8 | candidate失败、完成后promote前崩溃不影响旧root；不明CAS先reopen | Control/CLI/tool |
| A9 | prefix promotion不收紧；新根欠账不拼接旧尾部 | CLI/Online |
| A10 | mixed root不冒称统一producer；Full origin生成新候选；Overlay不符时0额外调用拒绝 | CLI/Manager |
| A11 | prompt/改名不需改digest文件；旧family补列；并发lane不放大 | Hosting/Host |
| A12 | Prepared/LegacyStarted/tool恢复：recap=0、routeLoads=0、旧profile identity不变 | Host |
| A13 | V4 full/overlay/partial/候选/多ref无LLM升级、ID内容不变；crash/orphan可诊断 | Migration/CLI |
| A14 | 4,097/65,537级历史增量；换策略不回根，不每行增加Control recipe | Manager/Online |
| A15 | V11转换保留连接预算，歧义dry-run不写；新仓无历史profile可bootstrap | Config/Host |

A1-A7 属于首个可运行垂直片；不能把 Getter、Undo 或首cell前恢复推迟成“优化”。
大历史使用现有 fixture/计数证明扫描量，不以放宽超时替代算法修复。

## 10. 辩证裁决

三位独立 reviewer：需求怀疑者、最小架构者、语义守卫。第一轮同一草案/账本与源码；
第二轮互审最强反例；第三轮只裁决 adoption authority。主线程查证并提出 root + row_work，
各方用事件轨迹接受，不按票数决定。

| 裁决 | 内容 | 决定性理由 |
|:--|:--|:--|
| delete | 最新target相等门禁、Continuation/epoch/策略时间线候选 | 正确旧数据停机；版本链不能自然解决Undo与0cell窗口 |
| delete | 每行Control recipe、Store adopted-head/choice | 4096recipe/256depth/32MiB整体重写；原Control CAS可唯一选root |
| simplify | 整条链统一producer → root选择＋逐行真实producer | 同root固定位置/prior保留因果 |
| merge | schema、跨策略Getter、0cell恢复、Undo纳入首片 | 数据库和读取链直接反例 |
| keep | partial、exact prior、selected lineage、原子row、Prepared、显式promotion | 真实消费者和失败轨迹 |
| defer | provider永久冻结、新工具权限、超长语义重试、分布式策略平台 | 前者违背现有换模型测试，其余无本轮需求 |

实质修订：守卫撤回“active continuation足以冻结逐行工作”；怀疑者撤回“promotion必须迁至Store”。
同root唯一work使另设choice失去必要性。Overlay实际复用、prefix promotion、bootstrap前Undo纳入规范。
主线程另处理Full身份碰撞：新显式Full记录OriginRoot，普通维护不新增root。

可观察化简：不新增Continuation kind、PendingPolicy、epoch、Store adopted head、独立策略服务；
保留1个原采用入口，新增1个必要逐行工作事实。代价是必要的Store V5/配置V12迁移及消费者收口，不能称“删几行”。

当前无待用户另选的产品分歧；施工发现不变量与事实冲突时给最小反例，不默默恢复全量重建或放宽验真。
三线程独立上下文，继承主线程模型/effort；工具未报告实际模型、分项成本，不宣称节省比例。
终稿窄核对确认 Full V2 OriginRoot 和 CLI 首次选择策略参数与裁决一致，无新增阻塞项。
本设计轮没有执行代码或迁移测试，矩阵是后续完成标准。

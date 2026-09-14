# Control 回执简化：设计与实施

> 状态：已实现并通过本地验证；代码提交 `0e9524d6`。未部署或改动真实会话数据。
> 日期：2026-09-14；源码核对基线：`48a9ec92`，包含 E2E 修复 `351095b5`。
> 承接[总设计 §6.2](identity-simplification-design.md#62-简化-control-操作回执)。用户后续已明确授权带领 subagents 实施、按需提交和维护文档；本轮不部署或操作真实实例。

## 1. 最小目标与需求来源

**删除派生的 `ResultIdentity`，直接返回已有 `OperationKey`。** 操作回执继续记录首次生效事实，阻止重复副作用。

```text
Receipt = OperationKey + ExecutionSequence + RuntimeIdentityDigest
        + CommandDigest + OriginalInstanceId + OriginalGeneration

同 OperationKey，匹配 sequence/runtime/command → Replayed，零写入
同 OperationKey，任一匹配项不同             → Conflict
新 OperationKey，合法命令                   → 语义变更与 receipt 一次发布
```

| 要求 | 来源与当前证据 |
|---|---|
| 减少无价值 hash，优先直接重构；无下游兼容或防恶意篡改需求 | 用户与根 AGENTS.md；不是要求清零所有 hash |
| 保留现有持久状态，重启后不重复应用已完成操作 | `ControlRuntime.TryReplay/PublishTerminalOperation`，Control receipt 与 crash 测试 |
| 同操作必须匹配命令、执行序号、runtime；保留首次生效坐标 | 当前 receipt 字段、restore 合并与重放消费者 |
| 已提交 Journal 工具结果保持原文；冻结请求保持原逻辑输入 | SessionJournal 的 `ToolResultObserved`、Prepared 重构与 CLI tool continuation 测试 |
| 落实本切片并验证、提交、维护文档 | 用户采纳计划后的明确实施请求；先前首轮和 E2E 的记录不能代替本轮证据 |

实际故障模型是本地进程崩溃、重启、重试、文件发布结果不明、CAS 与备份恢复。Control 和 Journal 分别持久提交，存在“Control 已生效，Journal 尚未记录工具结果”的窗口；不新增分布式事务或外部 exactly-once 承诺。

当前 `OperationKey` 本身仍是既有 operation-id 派生键。本切片删的是另一层结果摘要，不改 operation key 算法，也不把 hash 换个名字继续计算。

## 2. 实施前源码结论与边界（`48a9ec92`）

| 位置 | 已核实的消费者与结论 |
|---|---|
| [ControlOperationCanonicalizer](../../prototypes/SessionJournal.RecapGrid/Control/ControlOperationCanonicalizer.cs) `ResultIdentity` | 只 hash `commandDigest + terminalKind`，没有独立结果正文 |
| [ControlRuntime](../../prototypes/SessionJournal.RecapGrid/Control/ControlRuntime.cs) `TryReplay` | 查 operation key，再比 sequence/runtime/command；不读 ResultIdentity |
| [ControlState](../../prototypes/SessionJournal.RecapGrid/Control/ControlState.cs) `Decode/Create` | 当前 canonical JSON v2；旧字段参与整份状态摘要，不能先删字段再验证旧 Head |
| [ControlMaintenance](../../prototypes/SessionJournal.RecapGrid/Control/ControlMaintenance.cs) `ReadBackup/MergeOperationReceipts` | backup 对照原 Head；restore 合并 receipt，当前 record 相等间接包含结果字段 |
| [AgentControlTool](../../prototypes/SessionJournal.RecapGrid/AgentControl/RecapGridAgentControlTool.cs) `MapOperation` | 输出 resultIdentity；重放本来就返回当前 Head 和 replayed 状态，未承诺重建首次响应原文 |
| [AgentControlFactory](../../prototypes/SessionJournal.RecapGrid/AgentControl/RecapGridAgentControlFactory.cs) `Identity` | 指纹覆盖输入定义、catalog、admission，不覆盖输出 DTO；输出变更不需改 runtime identity |
| [CLI 垂直测试](../../tests/SessionJournal.Cli.Tests/ProgramRecapGridCommandTests.cs) `ToolContinuationBindsFrozenProfileAndReplaysReceiptAfterRewind` | 真实 Journal → frozen profile → Control → tool result 链；可扩充旧格式升级恢复证据 |

全仓源码搜索未发现业务代码解析 `resultIdentity`；现有显式测试消费者只比较 Applied/Replayed 字段相等。旧 public API evidence 是历史快照，不构成未发布项目的下游约束。

最新 E2E 修复 `351095b5` 保留已冻结 Note 通知正文，避免当前 renderer 否决旧文案。这里沿用“尊重已落盘事实”的原则，但不为 Control 新增正文快照：Control receipt 只存生效事实；已提交工具输出的正文权威在 Journal。

## 3. 当前实现合同

### 3.1 当前模型与输出

- 删除 `ControlOperationCanonicalizer.ResultIdentity`、仅为它存在的 `ResultDto` 和 `PublishTerminalOperation` 的 `terminalKind` 参数。
- 从当前 receipt、当前 wire DTO、`RecapGridControlOperationResult.Applied/Replayed` 删除 `ResultIdentity`。后两个结果明确返回 `OperationKey`，值取既有 operation/receipt，不计算新身份。
- AgentControl 输出统一为 `schemaVersion: 2`，字段改成 `operationKey`。Applied/Replayed 为对应 key；inspect 与普通失败为 null。保留现有 status、Head、`HeadAdvancedSinceApply` 和 `InstanceReplaced` 语义，不双输出旧字段。
- 输入 schema、工具说明、catalog、admission、registration/promotion canonical command、operation key 算法、runtime identity 的值均保持。用既有 golden 固定实际值，不增加版本映射或伪造旧 identity。

### 3.2 持久格式：旧格式仅在 codec 边界存在

Control 文件 writer 为 v3，receipt 不写结果摘要；state body 使用 v3 schema 与对应 `atelia.recap-grid.control-state.v3` domain。旧 v2 继续按原 domain 和原字段布局验证。v1 仍不支持，不新增历史执行分支。

读取顺序：

```text
按源版本解码 wire DTO
  → 验证源 canonical bytes、源 body digest 和字段形状
  → 共用 graph 解码与验证，receipt 投影为唯一当前类型
  → 构造 ControlState：原 Head + 当前语义集合 + 原 CanonicalBytes
```

v2 的旧 `resultIdentity` 只做原有形状校验，随后丢弃；不新增“重新推导旧结果摘要”的门槛。旧布局信息只留在 codec 临时 DTO 和原始 bytes，不进入运行 receipt 相等或操作判重。

必须显式保留共用 `ValidateGraph`：当前 `Decode` 间接借 `Create` 做 scope、列关系和 base depth 等验证；拆开后不能漏掉。复用已有 entries/graph 逻辑，避免复制两套完整业务模型。

纯读取、重放、export/backup 不重编码状态。正常持久 mutation 经唯一 v3 writer 发布，格式变化与本次语义/receipt 提交共用既有发布步骤；不增加单独 upgrade transaction。禁止 `Create(v3)` 后再覆盖旧 Head，这会制造 Head 与 bytes 不一致的对象。

### 3.3 必须保住的几条执行轨迹

1. **重复操作。** 旧 receipt 在 writer lock 内先于 stale-head 判断命中，返回 Replayed，不写文件、不增加 generation。
2. **新操作但语义已满足。** 新 operation 注册已存在 bundle 或 promote 已激活 recipe，仍提交自己的 receipt，generation 加一。这与普通 direct API 的 no-op 不同；没有原本需要的持久写入时，不为格式升级额外写入。
3. **发布结果不明。** 沿用重新读取当前状态并 `TryReplay` 的结算逻辑；匹配则返回 Applied 与 OperationKey，不能确认则保持 `CommitIndeterminate`，不猜成功或重做操作。
4. **restore/reinitialize。** 保留现有 receipt union。v2/v3 先投影成同一当前 record，再比较所有剩余字段；不同 command/runtime/sequence 或首次 instance/generation 仍是冲突。不能只保留旧 backup 的 receipts，丢掉当前新 receipt 会使后续恢复重复生效。
5. **Journal 边界。** 已提交的 v1 tool result 原文继续读取和审计；尚未提交结果的旧 pending 操作，允许 receipt 重放后生成当前 v2 输出。不新增 v1 renderer、结果正文表或完整命令副本。

## 4. 实施与验收

按一条生产链集成，不发布“只删输出但仍计算和持久保存 ResultIdentity”的中间补丁。

| 已实现部分 | 交付 | 证据入口 |
|---|---|---|
| Control 模型与持久格式 | 删除结果派生层；writer v3、旧 v2 投影、restore 归一化比较 | [ControlLegacyV2Tests](../../tests/SessionJournal.RecapGrid.Control.Tests/ControlLegacyV2Tests.cs)及既有 wire/crash/settlement 测试 |
| AgentControl 输出 | 输出 v2 + OperationKey，保留输入/runtime/command golden | [AgentControlVerticalTests](../../tests/SessionJournal.RecapGrid.AgentControl.Tests/AgentControlVerticalTests.cs) |
| CLI 旧状态续行 | 两个提交窗口与冻结 Prepared，真实入口恢复、旧 raw 保真 | [ProgramControlReceiptUpgradeTests](../../tests/SessionJournal.Cli.Tests/ProgramControlReceiptUpgradeTests.cs) |
| 相邻消费者 | Getter/Galatea 当前 Control schema 负面样本同步为 v3 | GetterTailClosureTests、GalateaRecapGridCompositionTests |

旧格式 fixture 在更改 production writer 之前由 `6a544481` 合成并固定完整字节。
[Control 样本](../../tests/SessionJournal.RecapGrid.Control.Tests/Fixtures/LegacyV2/README.md)含非空 receipt 和真实 backup；
[CLI 样本](../../tests/SessionJournal.Cli.Tests/Fixtures/ControlReceiptV2/README.md)记录三个状态及其生成边界，不含私人实例数据。

| 验收场景 | 核心断言 |
|---|---|
| 非空旧 v2 冷读、同操作重放、export、backup 读回 | 源文件/导出 bytes 与 Head 不变，零 generation 增量，backup manifest 仍匹配 |
| 下一次实际 mutation 写 v3，再冷开重放旧操作 | 新 bytes 无 resultIdentity，旧匹配仍成功；合法新 no-op operation 也有 receipt |
| v2 backup 与 v3 当前状态 restore；随后 reinitialize | 两侧独有 receipt 均保留，同一 receipt 正常合并；剩余字段冲突拒绝；原始应用坐标保持 |
| 同 operation 换 command/runtime/sequence | Conflict，状态和 receipt 不变；不回落当前默认身份 |
| 文件发布失败/结果不明 | 沿用既有 crash/settlement 测试，确认观察到 receipt 与无法确认两条结果均不变 |
| Control v2 已提交 receipt，Journal 缺 tool result，冷开生产续行 | 同 operation/sequence/runtime，输出 v2 + operationKey；Control bytes/generation 不因重放变化，业务 effect 不重复 |
| 旧 v1 ToolResultObserved 已提交，下一 Prepared 尚未提交 | 从该 head 冷开续行，零工具重执行，Control bytes/generation 不变；新 Prepared 沿用旧 raw Blocks |
| 含旧 v1 tool result 的 Prepared 已存在，再冷开重构 | raw Blocks 不变，full/selected audit 成功；冻结 canonical request、commitment、ExactContextInputs 不变 |
| strict codec 负面样本 | v2 缺失/非法旧字段与错误摘要仍拒绝；v3 拒绝旧字段；未知版本、重复字段等既有边界保留 |
| AgentControl 输出与绑定 | 成功、重放、inspect、失败都用新输出版本；既有 input/runtime/command golden 不变 |

两个 Journal 窗口由基线代码的既有 `AfterToolExecutionBeforeResultCommitted`、`AfterToolResultCommitted` failpoint 实际中断产生，随后关闭所有 owner。已有 Prepared 的第三样本是正常完成后把 ref 定点到已持久 Prepared，只证明冻结重构，不作为 crash 证据；本轮没有新增生产 hook。

已 ToolResult 场景移除 admission 文件并省略 CLI 参数，使 Host 没有工具 profile；续行成功可发现误入工具重新绑定的问题。两个续行场景都断言 Control bytes/Head 不变，第三场景对照基线 request bytes、commitment、ExactContextInputs 与旧 raw。样本尚无已选 recap row，ExactContextInputs 为 `[]`；非空 RecapGrid 的冻结恢复由既有 Galatea 回归覆盖，不能把这些最小样本冒充新增非空摘要证据。

验证项目：`SessionJournal.RecapGrid.Control.Tests`、`Control.PublicSurface.Tests`、`AgentControl.Tests`、`AgentControl.PublicSurface.Tests`、`Getter.Tests`（后四者同样带 `SessionJournal.RecapGrid.` 前缀），以及 `SessionJournal.Cli.Tests`、`SessionJournal.Tests`、`Galatea.Server.Tests`。Getter 的当前 Control schema 负面测试也需要同步升级。按 `tests/<项目名>/<项目名>.csproj` 执行，重 .NET 工作串行使用 `--no-restore -m:1 -nr:false`；Server 与 CLI 独立 build。Galatea 使用 [E2E 指南](e2e-testing.md#离线与非-live-命令)的明确 Live 类过滤与受限 xUnit 并发，不能用会误排除 `Delivery` 的 `!~Live`。

最终串行验证结果（2026-09-14）：

| 项目 | 通过 |
|---|---:|
| SessionJournal.RecapGrid.Control.Tests | 86 |
| SessionJournal.RecapGrid.AgentControl.Tests | 32 |
| SessionJournal.RecapGrid.Control.PublicSurface.Tests | 3 |
| SessionJournal.RecapGrid.AgentControl.PublicSurface.Tests | 1 |
| SessionJournal.RecapGrid.Getter.Tests | 29 |
| SessionJournal.Tests | 517 |
| SessionJournal.Cli.Tests | 142 |
| Galatea.Server.Tests（明确排除三个 Live 类） | 983 |
| **合计** | **1,793** |

八个项目均 0 失败、0 跳过；Server 与 CLI 独立 build 均 0 warnings、0 errors。
文档检查 35 文件、0 diagnostics，`git diff --check` 通过。
本机最终日志为 `/tmp/receipt-final-<项目名>.log` 与 `/tmp/receipt-final-build-<入口名>.log`，不是持久协议的一部分。

独立 code review 覆盖生产链、codec 与跨格式测试；指出的两处证据缺口已补齐：正确旧摘要下的非法结果字段明确拒绝，以及 backup-only/current-only receipt 合并后两者均可重放。最终无未解决 findings。
样本捕获/重开期间修正了测试 owner 生命周期、锁文件保留与 Unix 权限恢复，没有放宽生产打开规则。
未调用真实 provider、未启动或修改现有 Dev 实例；fresh 浏览器对话不能代替本轮旧 Control receipt 续行证据。

## 5. 发布边界与完成条件

本切片不改命令/引用图，不要求先收敛旧 pending，不需要离线批量转换工具。将来部署按 [E2E 指南](e2e-testing.md)保留完整匹配快照；已有旧数据可正常读取和重放，下一次原本就需要的持久写入自然升级。v3 写入后旧程序不能直接打开，回退须配合数据快照；快照之后的新操作不会自动保留到旧数据中。

后续路线已按用户新决定改为“丢弃旧 Recap，全部重构后统一重建”，不再做旧 Store 整图转换。
后继 [Store 切片](recap-store-simplification-plan.md)已实现并通过本地验证，保持 Timeline/Control 持久格式和命令编码。
Control 规则与 receipt 不属于可清空缓存。当前 promotion 工具在查 receipt 前依赖 Store proof，
因此最终清 Store 前须正常收敛相关 pending promotion；不能因 command 字节不变就宣称所有恢复无影响。
具体处置见[总设计 §7](identity-simplification-design.md#7-保留范围与最终重建边界)。
`ControlStateDigest`、ConnectionFingerprint、tool catalog/runtime identity、Prepared 局部 hash 均留给各自切片。

现行入口为[当前 Grid 概念](../SessionJournal/current/derived-recap/concepts.md)、[CLI 指南](../../prototypes/SessionJournal.Cli/README.md)与 [Galatea 运行机制](runtime.md)。R2 合同已注明 Control 后继边界；旧 WP-07C 与 API evidence 保留历史定位，不重写当时验收。总设计 §6.2 保留结果与后继入口，本文不继续累积已完成工作包。

当前运行模型、结果 API、新输出与新状态均不再包含 ResultIdentity；仅旧 codec wire DTO 和字段形状检查保留该名字。旧样本与跨提交窗口已通过真实生产链验收。删除了 **1 个派生身份概念、1 个 hash 计算函数及其 DTO/参数传播**；旧格式读取和验证样本仍有成本，不将本轮描述为总代码行减少。

## 6. 辩证裁决

三位 reviewer 分别承担需求质疑、最小架构、语义辩护；第一轮独立查源码，第二轮交换反例。主线程核对原始实现后收敛，无需第三轮或额外产品决策。

| 争点 | 裁决 | 理由或修订 |
|---|---|---|
| 删除 ResultIdentity | delete | 没有独立判重/结果语义，当前消费者可直接使用 OperationKey |
| 只删除 tool 输出 | 不采用 | 留下计算、持久字段和校验，后续还需第二次改同一条链 |
| 同版 optional 字段替代 v3 | 不采用 | 隐藏两种 canonical 布局，仍不能让旧 reader 读新文件，无净简化 |
| 旧 v2/current v3 receipt | simplify | codec 内源格式验证后投影一个当前模型，restore 用归一化后的 record 比较 |
| 逐字再生首次工具输出 | defer | 现有 replay 本就改变 status/head；保留 Journal 原文即可，不新增正文副本 |
| command/runtime/sequence、首次应用坐标、receipt union | keep | 删除会错误认领操作、丢失生效事实或在 restore 后重复副作用 |
| 所有 Control 格式变更先收敛旧操作 | simplify | 总设计 §7 原措辞过宽，收窄到改变命令/引用图的 §6.1/§6.3 |
| Decode 拆开 Create 后的验证 | keep | 交叉质询补出 `ValidateGraph` 隐式调用；直接构造不能漏掉原检查 |
| 成功返回即写新格式 | 修正 | 普通 no-op/read/replay 零写；新 agent operation 仍按既有语义发布 receipt |

与更大迁移合并能延后 codec 改动，但没有减少运行模型的额外收益；当前切片无需批量转换，适合作为可独立验收的一步。

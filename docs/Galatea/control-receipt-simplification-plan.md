# Control 回执简化：下一实施切片

> 状态：规划完成，经过三视角独立审查与交叉质询；尚未实施。
> 日期：2026-09-14；源码核对基线：`48a9ec92`，包含 E2E 修复 `351095b5`。
> 承接[总设计 §6.2](identity-simplification-design.md#62-简化-control-操作回执)。本轮用户请求是规划；本文不授予实施、部署或真实数据操作权限。

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
| 本轮只规划下个切片 | 用户最新请求；先前首轮和 E2E 的完成记录不能代替本轮实施证据 |

实际故障模型是本地进程崩溃、重启、重试、文件发布结果不明、CAS 与备份恢复。Control 和 Journal 分别持久提交，存在“Control 已生效，Journal 尚未记录工具结果”的窗口；不新增分布式事务或外部 exactly-once 承诺。

当前 `OperationKey` 本身仍是既有 operation-id 派生键。本切片删的是另一层结果摘要，不改 operation key 算法，也不把 hash 换个名字继续计算。

## 2. 源码结论与边界

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

## 3. 一次贯通的目标变更

### 3.1 当前模型与输出

- 删除 `ControlOperationCanonicalizer.ResultIdentity`、仅为它存在的 `ResultDto` 和 `PublishTerminalOperation` 的 `terminalKind` 参数。
- 从当前 receipt、当前 wire DTO、`RecapGridControlOperationResult.Applied/Replayed` 删除 `ResultIdentity`。后两个结果明确返回 `OperationKey`，值取既有 operation/receipt，不计算新身份。
- AgentControl 输出统一为 `schemaVersion: 2`，字段改成 `operationKey`。Applied/Replayed 为对应 key；inspect 与普通失败为 null。保留现有 status、Head、`HeadAdvancedSinceApply` 和 `InstanceReplaced` 语义，不双输出旧字段。
- 输入 schema、工具说明、catalog、admission、registration/promotion canonical command、operation key 算法、runtime identity 的值均保持。用既有 golden 固定实际值，不增加版本映射或伪造旧 identity。

### 3.2 持久格式：旧格式仅在 codec 边界存在

当前 Control 文件 writer 升为 v3，receipt 不写结果摘要；state body 使用 v3 schema 与对应 `atelia.recap-grid.control-state.v3` domain。旧 v2 继续按原 domain 和原字段布局验证。v1 仍不支持，不新增历史执行分支；实施前若版本被其他提交占用，先核对并调整编号。

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

## 4. 工作包与验收

按一条生产链集成，不发布“只删输出但仍计算和持久保存 ResultIdentity”的中间补丁。

| 包 | 写入范围与交付 | 依赖 |
|---|---|---|
| A：Control 模型与持久格式 | `Control/` 与对应 Control 测试；固定非空 v2 receipt/backup 样本，完成 v3 writer、旧读取、发布与 restore | 先保留基线旧样本，再改 writer |
| B：AgentControl 与会话续行 | `AgentControl/`、AgentControl 测试、CLI tool continuation 测试；当前输出、旧 pending 恢复、旧 raw 保真 | 依赖 A 的结果/codec 合同；可先独立设计场景 |
| C：集成审查与文档 | 主线程串行验证，独立 reviewer 查漏；更新现行协议/运行说明 | A、B 集成后 |

旧格式 fixture 从基线合成数据固定完整字节及来源版本，不能仅由新 writer 换版本号反造。无需把私人实例正文加入测试。

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

上述 Journal 场景优先扩充现有 CLI 垂直测试，使用真实恢复入口和合成旧持久样本；结果已提交与 Prepared 已存在的两个断点可在同一场景顺序验证。只把 ref 移回旧 Action 的测试可以证明 receipt 重放，但不能冒充进程在跨组件窗口中断的证据；新增场景需实际保留已提交 Control 与尚缺结果的 Journal 状态并冷重开。若现有 hooks 无法形成该窗口，仅加最小测试 hook，不建设通用故障注入平台。

验证项目：`SessionJournal.RecapGrid.Control.Tests`、`Control.PublicSurface.Tests`、`AgentControl.Tests`、`AgentControl.PublicSurface.Tests`（后三者同样带 `SessionJournal.RecapGrid.` 前缀），以及 `SessionJournal.Cli.Tests`、`SessionJournal.Tests`、`Galatea.Server.Tests`。按 `tests/<项目名>/<项目名>.csproj` 执行，重 .NET 工作串行使用 `--no-restore -m:1 -nr:false`；Server 与 CLI 独立 build。Galatea 使用 [E2E 指南](e2e-testing.md#离线与非-live-命令)的明确 Live 类过滤与受限 xUnit 并发，不能用会误排除 `Delivery` 的 `!~Live`。

本轮未运行这些实现测试；上述均为实施验收要求。真实 provider 调用不是该格式重放证据的前提，fresh 浏览器对话也不能代替旧 Control receipt 续行场景。

## 5. 发布边界与完成条件

本切片不改命令/引用图，不要求先收敛旧 pending，不需要离线批量转换工具。将来部署按 [E2E 指南](e2e-testing.md)保留完整匹配快照；已有旧数据可正常读取和重放，下一次原本就需要的持久写入自然升级。v3 写入后旧程序不能直接打开，回退须配合数据快照；快照之后的新操作不会自动保留到旧数据中。

Timeline 行身份与 Store 结果/缓存身份仍按总设计 §6.1/§6.3 合并为后续一次整图转换。该阶段才适用旧命令收敛前提。`ControlStateDigest`、ConnectionFingerprint、tool catalog/runtime identity、Prepared 局部 hash 均留给各自切片。

实施完成时更新[当前 Grid 概念](../SessionJournal/current/derived-recap/concepts.md)、CLI 指南和 Galatea 相关运行/场景说明；旧 WP-07C 与 API evidence 保留历史定位，必要时加后继设计链接，不重写当时验收。总设计 §6.2 只保留结果与后继入口；本文的工作包不持续追加已完成待办，改为简短实际交付与验证记录。

完成意味着当前运行模型、结果 API、新输出与新状态均不再包含 ResultIdentity；旧样本与跨提交窗口通过真实生产链验收，文档如实记录证据。预计减少 **1 个派生身份概念、1 个 hash 计算函数及其 DTO/参数传播**。codec 暂留旧格式读取，暂不承诺总代码行减少。

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

# Player / Character 与结构化输入实施工作单

状态：实施中；2026-09-15 开始。基线：`fdf8e856`。本页记录施工状态和验收证据，不替代设计合同。

## 目标与授权

完整实施 [身份分离方案](player-character-separation-design.md) 与 [结构化输入方案](structured-input-rendering-design.md)，包括全部消费者、故障验收和真实实例离线迁移。用户已授权实现、按需 Git 提交、调度 subagents 及最后的真实迁移；两份设计里“本轮只修文档”的历史授权说明不再限制本次工作。

最终必须同时成立：Character 不依赖 Player 存在；Player 明确选择操作目标；输入及系统指令保存稳定语义内容；所有 LLM 输入在请求时投影；发送尝试保有独立证据；旧历史和未知外部工作保留原恢复边界。局部类型、单独 renderer 或部分测试通过不能代表完成。

## 工作包与写入归属

| 包 | 内容 | 归属与状态 | 验收闸门 |
|:--|:--|:--|:--|
| P0 | 接口收口、消费者清单、独立审阅 | 主线程；首轮接口已收口 | 核心与 host 接缝有明确类型、schema 和恢复规则 |
| P1a | SessionJournal 内容、setup、语义 Prepared、Started、查询/undo/audit/recovery | 单一核心负责人；实现中 | 不调用 renderer 的重开/审计，换 renderer 的请求尝试与旧格式恢复 |
| P1b | Galatea 领域输入、来源、md-json 与最小执行主线 | 单一 host 负责人；实现中 | 合成 Player 动作通过真实 Journal/provider/query/undo 链 |
| P1c | 常规调用日志只保存诊断事实 | 已提交 `6d1b432a`；隔离验证通过 | 两个 composition root 与 CLI smoke 均不落渲染全文，identity/disposal/失败语义保持 |
| P2a | Character/Player/config/API/UI/心跳 | 待 P1 接口稳定 | 零 Player、显式目标、来源可信、认证失效与全量路由 |
| P2b | Note receipt、recall、DerivedInfo | 待 P1 接口稳定 | 内容选择冻结、精确原文/来源、稳定 proof 与旧记录 |
| P2c | 角色信、reply lease、Codex Start/Inspect/恢复 | 待 P1 接口稳定 | 原子 capture/claim、UTF-8 证据、exact append proof、未知不重发 |
| P2d | RecapGrid 与公共 CLI | 资产去 Player 和 typed Online/history 接入进行中 | 语义 context/carrier、新资产采用、历史读取与瞬态辅助请求 |
| P3 | 跨域集成、故障与独立 review | 主线程统筹；未开始 | 两份设计全部验收矩阵逐项有证据 |
| P4 | 真实盘点、备份、离线升级、资产采用与调用 | 主线程串行；未开始 | 原 owner/路径/dispatch 保持，strict reopen，真实调用及运行状态记录 |

公共类型和 `prototypes/SessionJournal` 执行合同由一个负责人编辑；`GalateaServices.cs` 由一个 host 负责人编辑。模块 worker 不并行修改这些入口，必要改动交给负责人集成。重型 .NET build/test 统一排队；不依靠调宽生产截止时间获得通过。

## P0 接口约束

- 原 text 与新 structured 内容用明确 schema/tag 区分；不能将 JSON 字符串伪装成旧 prompt。结构值拥有自己的存储生命周期，不依赖外部 JsonDocument。
- 核心不引用 Galatea/MdJson。host 投影只在请求组装使用；读取、setup 语义比较、证明和审计不要求 projector 可用。
- 新 Prepared 保存 raw 范围/哈希、setup 引用、语义内容选择、工具与具体连接边界；每次 Started 单独保存 canonical request 长度/摘要。不持久渲染全文/样式版本，不新增 attempt ID 或 supersede 事件。
- Prepared 无 Started 时允许当前投影；Started 未知时保留现有明确恢复授权。提交结果未知不调用 provider，重开读取实际 head。
- 旧 v5 仅审计；旧 v7/v8 exact 恢复不经过新 projector。内容/target 改动不能当成风格改变。
- 新 SystemPromptSetup 保存有序指令源和已绑定事实；Recap 所选 cell 与 carrier 也是语义内容，不将历史包装复制到新 plan。
- receipt 全文/IDs 属于内容选择；recall 保存当时 title/exactText；renderer 不改变字段、次序、可见性和来源。

### 首轮接口裁决

两位 explorer 独立核查 core 与 host 调用链后，采用以下接口方向；具体 C# 签名以实现为准：

- `SessionInputContent` 为不可变 Text / Structured 判别类型，Structured 保存 `SchemaId` 与拥有生命周期的 JSON 值。核心严格验证其机读结构；Galatea 另验自己的领域 schema。
- `ISessionInputProjector` 接受 Structured 输入并产生临时文本，挂在 `SessionRuntime`；Text 原样使用。未知 schema 或未提供 projector 明确失败。
- create/setup/send、completed/retracted turn、exact proof 和 history planning 保留 typed 内容，历史只读路径不会隐式生成 provider message。旧 string 便利调用只创建 Text；不能将 Structured 转成 JSON 字符串供旧 reader 继续使用。
- Observation/SystemPromptSetup 新 schema 与旧 text schema 明确区分；Prepared v9 不含 rendered snapshot/commitment，Started v2 含 canonical codec 与本次长度/SHA-256。v7/v8 常量与 decoder 明确作为历史版本保留。
- 语义 derived contributions 复用现有 `SessionContextContribution` 的 target（carrier/blockKey/semanticHeading）与 ExactText，并保存实际来源边界；原 v7/v8 的 exact recipe 不改用新布局。
- `InspectRuntimeRecoveryRequirements` 和 Refuse uncertain 分支先验证事实及权限，不能为了拒绝未知重发先要求 renderer 可用。
- 独立 reviewer 补充的版本矩阵：v9 只接受带证据的 Started v2；旧 v7/v8 恢复继续写 Started v1，并按原 Prepared 承诺逐字重建。若读取旧 Prepared 后的 v2 Started，须与旧承诺严格一致，不能通过混合 schema 绕过证据校验。
- host schema 初版命名 `galatea.observation.v1` / `galatea.system-instructions.v1`，严格区分动作、邮件、心跳等业务 shape；系统指令保存 sources 和 bindings，去掉固定 Player 依赖。
- 独立 reviewer 补充的 sources 边界：home 的操作含义、Codex 默认 cwd 以及 roster 的解释也必须保存为指令源；不能仅存路径/名单而让当前 projector 重新定义指令。

后续更改这些接缝需由两个负责人和主线程先对齐，不能让各消费者自行建立字符串降级通路。

## 依赖与验证记录

- md-json 初始源码身份：`d9b60306f6c42cde9fce26e52fb329570ecf9a53`。采用可配置兄弟源码根的 ProjectReference；本记录不代表已完成集成或已运行其测试。
- 2026-09-15 初始工作树干净；当前实现仍是 text Observation / text SystemPromptSetup、Prepared v8 与空 Started v1。
- 正式验证按包记录实际命令、退出结果和覆盖范围；provider-free 结果与真实 provider/实例结果分开记录。
- P4 之前不转换真实配置、状态或摘要资产。迁移先停止并取得独占锁、备份和 dry-run；保留未决工作，不能为通过验证重发未知请求。
- P2d 的非阻塞前置切口已开始：新 Recap 资产 V7 移除固定 Player 参数与 CLI `--player-name`，来源改为按输入块读取；仍待测试和后续 structured history 接入，不能视为 P2d 完成。

### P1c 日志包验证

在基线 `fdf8e856` 的隔离 checkout 应用日志包，串行验证：

| 命令（共同使用 `-m:1 -nr:false`，xUnit 线程上限 4） | 结果 |
|:--|:--|
| `dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj --filter FullyQualifiedName~CompletionMetadataLoggingClientTests` | 5/5；正文/工具描述/返回内容/异常文本不入日志，取消和日志 I/O 失败不替换结果 |
| `dotnet test tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj --filter FullyQualifiedName~ExplicitCandidateBuildAndPromotionKeepOnlineRequestsToolFree` | 1/1；真实 Recap 调用路径生成 metadata 日志 |
| `dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --filter FullyQualifiedName~ResumePrepared_ExactBindsWithoutOpeningRecapGridRoutes` | 1/1；原 frozen recovery 的 logging factory 绑定路径保持 |

Recap 的合法非空 Tail 超出现有 SessionJournal request canonicalizer 的适用域，因此诊断 logger 使用独立且明确的 request metadata 摘要编码，覆盖 prefix/tail/tools/output policy；不更改 durable commitment 编码，不将正文作为摘要失败 fallback。以上是日志包隔离验证，P1 核心与全量消费者仍须在最终工作树集成验收。

## 完成审计清单

- [ ] 主方案配置、身份、全量 API/UI、零 Player、prompt/资产及迁移要求。
- [ ] 专题全部输入消费者与常规日志无新渲染落盘旁路。
- [ ] 新 Prepared/Started、提交不确定、显式 uncertain 重试、旧 v5/v7/v8 恢复。
- [ ] 换 renderer/清缓存后内容、proof、查询/undo、reopen/audit 稳定。
- [ ] Note 大批量选择、Memo 更新、recall/provenance、Recap semantic target。
- [ ] Codex 完整 UTF-8 证据、cold/live/operator 一致、旧大任务可 Inspect。
- [ ] append/settle/undo 竞争、工具 continuation 与目标门禁。
- [ ] 隔离实例验证、真实备份/升级/Recap 采用/调用与最终运行状态。
- [ ] 当前合同、运行文档、独立 review findings 与 Git 提交全部收口。

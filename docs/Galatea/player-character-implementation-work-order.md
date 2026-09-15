# Player / Character 与结构化输入实施工作单

状态：实施中；2026-09-15 开始。基线：`fdf8e856`。本页记录施工状态和验收证据，不替代设计合同。

## 目标与授权

完整实施 [身份分离方案](player-character-separation-design.md) 与 [结构化输入方案](structured-input-rendering-design.md)，包括全部消费者、故障验收和真实实例离线迁移。用户已授权实现、按需 Git 提交、调度 subagents 及最后的真实迁移；两份设计里“本轮只修文档”的历史授权说明不再限制本次工作。

最终必须同时成立：Character 不依赖 Player 存在；Player 明确选择操作目标；输入及系统指令保存稳定语义内容；所有 LLM 输入在请求时投影；发送尝试保有独立证据；旧历史和未知外部工作保留原恢复边界。局部类型、单独 renderer 或部分测试通过不能代表完成。

## 工作包与写入归属

| 包 | 内容 | 归属与状态 | 验收闸门 |
|:--|:--|:--|:--|
| P0 | 接口收口、消费者清单、独立审阅 | 主线程；首轮接口已收口 | 核心与 host 接缝有明确类型、schema 和恢复规则 |
| P1a | SessionJournal 内容、setup、语义 Prepared、Started、查询/undo/audit/recovery | 已提交 `f9c4f4e4`；核心全套 543 项通过、独立审阅完成 | 不调用 renderer 的重开/审计，换 renderer 的请求尝试与旧格式恢复 |
| P1b | Galatea 领域输入、来源、md-json 与最小执行主线 | typed 主线已接通并纳入全工程回归 | 合成 Player 动作通过真实 Journal/provider/query/undo 链 |
| P1c | 常规调用日志只保存诊断事实 | 已提交 `6d1b432a`；隔离验证通过 | 两个 composition root 与 CLI smoke 均不落渲染全文，identity/disposal/失败语义保持 |
| P2a | Character/Player/config/API/UI/心跳 | 已接入；配置转换专项与相关回归 91 项通过 | 零 Player、显式目标、来源可信、认证失效与全量路由 |
| P2b | Note receipt、recall、DerivedInfo | V4/store 升级 88 项通过；辅助来源和语义说明已修复、独立审阅通过，纳入最终 1152 项回归 | 内容选择冻结、精确原文/来源、稳定 proof 与旧记录 |
| P2b-MemoPod | Open/Freeze 与 Recall 的瞬态缓存边界 | 已提交 `464b254b`；全套 284 项及独立审阅通过 | 坏 renderer 不阻止机读发布/打开，缓存失效不改变 epoch，原样重新冻结使旧在途 Recall 失效 |
| P2c | 角色信、reply lease、Codex Start/Inspect/恢复 | SQLite V5 / sidecar V6 已接入；25 项新故障专项通过、review 已收口 | 原子 capture/claim、UTF-8 证据、exact append proof、未知不重发 |
| P2d | RecapGrid 与公共 CLI | Recap 部分已提交 `d34f839c`；各项目全套通过，但迁移预检发现 CLI build 尚缺 Galatea structured projector，正在补齐 | 语义 context/carrier、新资产采用、历史读取与瞬态辅助请求 |
| P3 | 跨域集成、故障与独立 review | Galatea provider-free 全套 1152/1152；CLI 新发现接缝须修复并追加验证 | 两份设计全部验收矩阵逐项有证据 |
| P4 | 真实盘点、备份、离线升级、资产采用与调用 | 主线程串行；只读预检已开始，配置/数据库未转换 | 原 owner/路径/dispatch 保持，strict reopen，真实调用及运行状态记录 |

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
- 2026-09-15 初始工作树干净；当时的改造基线是 text Observation / text SystemPromptSetup、Prepared v8 与空 Started v1。
- 正式验证按包记录实际命令、退出结果和覆盖范围；provider-free 结果与真实 provider/实例结果分开记录。
- P4 之前不转换真实配置、状态或摘要资产。迁移先停止并取得独占锁、备份和 dry-run；保留未决工作，不能为通过验证重发未知请求。
- 新 Recap 资产 V7 移除固定 Player 参数与 CLI `--player-name`，来源改为按输入块读取；structured history 与实际请求预算已接入。语义资产采用仍须在真实实例阶段按现有构建/promotion 流程完成。

### P1c 日志包验证

在基线 `fdf8e856` 的隔离 checkout 应用日志包，串行验证：

| 命令（共同使用 `-m:1 -nr:false`，xUnit 线程上限 4） | 结果 |
|:--|:--|
| `dotnet test tests/SessionJournal.Tests/SessionJournal.Tests.csproj --filter FullyQualifiedName~CompletionMetadataLoggingClientTests` | 5/5；正文/工具描述/返回内容/异常文本不入日志，取消和日志 I/O 失败不替换结果 |
| `dotnet test tests/SessionJournal.Cli.Tests/SessionJournal.Cli.Tests.csproj --filter FullyQualifiedName~ExplicitCandidateBuildAndPromotionKeepOnlineRequestsToolFree` | 1/1；真实 Recap 调用路径生成 metadata 日志 |
| `dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --filter FullyQualifiedName~ResumePrepared_ExactBindsWithoutOpeningRecapGridRoutes` | 1/1；原 frozen recovery 的 logging factory 绑定路径保持 |

Recap 的合法非空 Tail 超出现有 SessionJournal request canonicalizer 的适用域，因此诊断 logger 使用独立且明确的 request metadata 摘要编码，覆盖 prefix/tail/tools/output policy；不更改 durable commitment 编码，不将正文作为摘要失败 fallback。以上是日志包隔离验证，P1 核心与全量消费者仍须在最终工作树集成验收。

### 核心与派生消费者验证

以下是各项目在其对应修订上的完整测试结果；共享入口后续变更仍须做最终 Galatea 集成，不能把不同版本的通过项相加当作一次全套验收。

| 项目 / 范围 | 结果 | 证据文件 |
|:--|:--|:--|
| SessionJournal 核心 | 543/543 | `semantic-core-integration.trx` |
| SessionJournal public surface / Offline | 4/4、11/11 | `public-semantic-integration-recheck.trx`、`offline-semantic-integration.trx` |
| Recap runtime / online / hosting | 93/93、33/33、37/37 | `recap-runtime-semantic-integration.trx`、`recap-online-semantic-integration.trx`、`recap-hosting-semantic-integration.trx` |
| HistoryTimeline（含 65,537 行持久链） | 204/204 | `timeline-semantic-integration.trx` |
| CLI 全套 | 160/160 | `cli-semantic-integration-recheck.trx` |
| Galatea V7 资产 | 9/9 | `galatea-v7-final-assets.trx` |
| MemoPod 全套 | 284/284 | MemoPod worker 的全套运行记录；已独立审阅并提交 |
| sidecar 全套 Node | 134 通过、2 个 live 跳过 | `/tmp/galatea-sidecar-full-node.log` |
| 浏览器协议全部 Node | 20/20 | `/tmp/galatea-browser-node-final.log` |

核心实际字段与旧版本矩阵见 [Prepared v9 合同](../SessionJournal/current/contracts/completion-request-prepared-v9.md)。已修复并验收 contribution 来源范围、Prepared 提交后才投影、Refuse 先验证持久完整性，以及 source 模板闭合语法。主线已支持完整身份与 notices/recalls，旧的阶段性空内容限制已移除。

### P2a 接口与后续入口

文件 v10 根只含 `v/characters/players/runtime`。C# 合并配置保留既有外部 connections/delegates 服务字段，独立拥有 Characters 和 Players；不为 JSON 分组额外复制运行时配置权威。

`GalateaCharacterConfig` 不含密码或固定 PlayerName；`GalateaPlayerConfig` 只含 PlayerId/Name/Password；会话 host 改为 `CharacterSessionHost.Character`。`GalateaHtml` 已从共享 Services 抽出，以便 API/UI 与 config/后台分别独占写入。API 的全部 13 个角色操作后缀迁到显式 Character 路由，登录 cookie/claim 换成新的 Player 身份。

已只读盘点原位置的真实配置：V9、两个角色、一个共同玩家显示名但两套不同密码。管理员拟使用 `player-main` 和原显示名，沿用哪套密码已向用户询问；不记录密码，不影响代码继续施工。真实配置、模板和数据库尚未转换。

2026-09-16 已提交 Galatea 身份与语义通信主包 `b4a44960`。真实停服预检、锁定备份及冷读取结果见[真实实例迁移验收](player-character-migration-validation.md)；后续升级与真实调用仍未完成。

### P2 集成收口与新增接缝

- 设计补充复核已提交 `8ab7c933`：补入 MemoPod 早渲染、辅助来源、混合代际绑定与支持域/失败阶段；对应实现继续纳入完成审计。
- 当前工作树的 SessionJournal 核心全套 **543/543** 通过（`semantic-core-integration.trx`）；Prepared v9/Started v2、旧读取路径和核心 current docs 已独立审阅。此证据限于核心，不替代 Galatea 全消费者验收。
- 补齐辅助输入语义说明后的 V7 资产 **9/9** 通过（`galatea-v7-final-assets.trx`）；Family/definitions/bundle 的新 digest 已按实际注册结果更新固定值。
- MemoPod 新瞬态投影包定向 **27/27**、全套 **284/284** 通过；独立 review 已通过并提交 `464b254b`。
- `dotnet build tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false`：通过，零警告、零错误。
- 2026-09-16 最终 Galatea provider-free 回归 **1152/1152**：`dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~LiveTests' --logger 'trx;LogFileName=galatea-complete-provider-free-final.trx' -- xUnit.MaxParallelThreads=4`。包括 14 项 GalateaLab 进程/故障场景，以及同实例零 Player 双角色的心跳→内部邮件→Codex 回信联合链。
- 最后一个间歇性失败来自测试取消回调竞态：独立 Delay 的取消先唤醒清理，可能注销要注入的 fatal 回调。现由单回调同时取消 TCS 并抛出 fatal，保留 mail-first Aggregate、drain 与零活跃调用断言；相关类 **24/24**，独立审阅通过，生产取消逻辑未改。
- 前轮 fixture/旧断言问题、P2c 的容量预留与 Text Bind 绕过、辅助 Action 来源范围及语义说明均已修复并纳入此次全套。独立 reviewer 已关闭这些 findings。
- 真实迁移预检发现新的覆盖缺口：`recap-grid build` 创建 completion host 时未提供 Galatea projector，既有 CLI 测试主要使用 Text 历史。新版 structured 历史会报 `InputProjectorUnavailable`。正在抽出共享 Observation 投影并补真实公共 CLI mixed-history build 测试；不能借真实实例尚有旧 Text 历史跳过此要求。

离线工具已有真实锁与文件/SQLite 故障证据：ConfigV9 专项 **23/23**、相关合并组 **91/91**；CharacterMemory store 原 74 项与新增升级 14 项共 **88/88**，命令侧 **6/6**。工具可用不代表真实配置或数据库已迁移。

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

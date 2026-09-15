# Player / Character 与结构化输入实施验收

状态：**已完成代码、集成/故障验收及真实实例迁移，服务已恢复运行。** 2026-09-15 开始，2026-09-16 验收；基线 `fdf8e856`。本页保存交付证据，不再作为待办清单。

## 交付范围

完整实施 [身份分离方案](player-character-separation-design.md) 与 [结构化输入方案](structured-input-rendering-design.md)。Character 拥有连续历史和后台活动；Player 是独立外部身份，所有操作显式指定角色。新输入和系统指令保存机读内容，LLM 请求时瞬态投影；已发送尝试保留独立证据，旧历史保持原恢复合同。

真实实例现为一个 `player-main` 管理员、两个原 Character；角色 ID、目录、home 和原状态身份保持不变。配置 V10、Delegation V5、CharacterMemory V4、两角色 V7 Recap 均已采用。真实操作、备份、登录和最终运行结果见[迁移验收记录](player-character-migration-validation.md)。

## 主要提交

| 提交 | 交付 |
|:--|:--|
| `6d1b432a` | Galatea / CLI 调用日志只保存诊断 metadata |
| `f9c4f4e4` | SessionJournal typed 输入、语义 Prepared v9、Started v2、旧格式恢复及 current contracts |
| `464b254b` | MemoPod Open/Freeze 与 Recall 瞬态渲染、独立冻结周期 |
| `d34f839c` | RecapGrid runtime/online/hosting、Timeline、V7 资产及 CLI 基础接入 |
| `b4a44960` | Player/Character/config/API/UI、Note/recall、邮件/Codex V6、离线升级工具及集成测试 |
| `d07e4d46` | 共享 Galatea.Input 严格 Observation schema / projector，公共 CLI mixed-history build |
| `62c1e956` | 已 append 未 ack 的结构化回执进入真实 undo 门禁的组合验收 |

md-json 使用可配置 `MdJsonSourceRoot` 的 ProjectReference；实际验证的兄弟仓 HEAD 为 `d9b60306f6c42cde9fce26e52fb329570ecf9a53`，工作树干净。核心不引用 Galatea 或 MdJson；Server 与 CLI 共用 Observation 的唯一校验/路径权威。

## 保持的合同

- `SessionInputContent` 明确 Text / Structured，SchemaId 和值有独立生命周期；未知领域 schema 不猜字段、不当普通 prompt 派发。
- 新 Observation/SystemPromptSetup 为事件 body v2，业务 JSON 有自己的 schema；输入、来源、内容选择和语义协议独立于围栏与布局。
- Prepared v9 保存已选语义计划；Started v2 在外部调用前保存本次 canonical request 承诺。不持久化新渲染全文、样式版本或缓存，不新增 attempt ID/WAL。
- 无 Started 时可重投影原计划；已发送未知不自动重发。Started 提交结果未知时不调用外部服务，重开后按真实 head 判断。
- v5 仅审计；v7/v8 保留原 exact 合同。旧 Prepared 后若出现 Started v2，必须匹配旧承诺。见 [Prepared v9 合同](../SessionJournal/current/contracts/completion-request-prepared-v9.md)。
- receipt 全文/IDs、recall 当时 title/exactText/来源版本是内容选择；缓存和 renderer 不重选内容、不改变冻结周期。
- 旧记录与已绑定证明不原地改写。纯读、UI、undo、audit 不依赖 LLM projector；新 DesiredSetup 在合法 fresh 边界采用。
- Codex Start/Inspect 使用完整任务的严格 UTF-8 承诺；同 generation 的关联 live 证据与 cold 回读证据分别保持原信任边界。

## 实际验证

重型 .NET 编译/测试串行，使用 `--no-restore -m:1 -nr:false`，常规 xUnit 并行上限 4。各行是对应修订上的真实结果，不能相加描述为一次全仓运行。

| 范围 | 结果 | 证据 |
|:--|:--|:--|
| SessionJournal 核心 | 543/543 | `semantic-core-integration.trx` |
| SessionJournal public surface / Offline | 4/4、11/11 | `public-semantic-integration-recheck.trx`、`offline-semantic-integration.trx` |
| 补查 MemoPod、HistoryTimeline 与 9 个 RecapGrid public surface 套件 | 11 套共 46/46 | 迁移操作目录 `public-surface-final/`；Hosting / Runtime 已严格验收可选 projector 的公开签名 |
| Recap runtime / online / hosting | 93/93、33/33、37/37 | `recap-runtime-semantic-integration.trx`、`recap-online-semantic-integration.trx`、`recap-hosting-semantic-integration.trx` |
| HistoryTimeline，含 65,537 行持久链 | 204/204 | `timeline-semantic-integration.trx` |
| MemoPod | 284/284 | 原始工具结果已提取到迁移操作目录 `memopod-284-original-tool-output.log`，非重跑；独立审阅通过 |
| Galatea V7 资产 | 9/9 | `galatea-v7-final-assets.trx` |
| Galatea provider-free 全套，包含进程故障与零 Player 联合链 | 1195/1195 | `/tmp/galatea-shared-input-full-tests.log` |
| 最终追加的 undo 门禁组合测试，未修改生产逻辑 | 1/1 | `structured-delivery-rewind-gate.trx` |
| CLI 全套，包含新增 public mixed-history build | 162/162 | `galatea-shared-cli-full.trx`；新增专项 `galatea-v7-public-build.trx` 2/2 |
| ConfigV9 离线转换 / 相关组 | 23/23、91/91 | 合成配置、实际文件锁及发布故障；未声称断电验证 |
| CharacterMemory store / 离线命令 | 88/88、6/6 | `memory-offline-upgrade-spine.trx` 及命令专项 |
| sidecar Node 全套 | 134 通过、2 个 live 跳过 | `/tmp/galatea-sidecar-full-node.log` |
| 浏览器协议 Node | 20/20 | `/tmp/galatea-browser-node-final.log` |
| Release Server / CLI | 零警告、零错误 | 迁移操作目录中的 release build 日志 |
| 真实 Codex、8 次 Recap、真实 Player 主线、冷重开与最终运行 | 已通过 | [真实实例证据](player-character-migration-validation.md) |

## 完成审计映射

独立 reviewer 按设计条款和具体测试内容核查，并经主线程对照真实执行证据收口。未留下必须修复项。

| 要求 | 实现/验收证据 |
|:--|:--|
| 独立身份、全量目标路由、旧/移除 cookie、零 Player | `GalateaPlayerCharacterApiTests` 的 13 条角色路由；`GalateaZeroPlayerAutonomyTests` 的 hosted 心跳→内部信→Codex 结果联合链；真实双角色页面和 API |
| 稳定输入、可信分块来源、字符保真、无渲染落盘旁路 | `GalateaStructuredInputTests`、`GalateaObservationSharedProjectionTests`；真实新 Observation/SystemPromptSetup 冷读取 |
| 语义 Prepared/Started、提交不确定与旧恢复 | `SessionStructuredInputTests` 的提交前后异常、双 Started、Refuse、坏引用；真实旧 v1/v7/v8、v5 拒派发；非空 Recap process crash |
| 投递 proof、迁移及 undo 竞争 | `GalateaSemanticDelegationTests`、Memory receipt migration、Note receipt process crash；`GalateaStructuredDeliveryRewindGateTests` 直接调用真实门禁，先 Delivered 再拒绝撤销未完成轮次 |
| receipt 全或零正文、Memo 版本、来源和派生语义 | `GalateaSemanticMemoryInputTests`、MemoRecallProductionVertical、DerivedInfoEnricher/Reconciler；已 Prepared 的产物不重新 materialize/调用模型 |
| MemoPod 缓存/冻结周期与辅助请求预算 | `TransientProjectionTests`、`RecallLimitsAndProviderTests`、`RuntimeStructuredInputTests`；破 renderer 不阻止机读打开，原样 refreeze 也使旧在途结果失效 |
| Recap/CLI 全消费者及语义资产 | public `ProgramGalateaStructuredBuildTests` 的真旧 v1 + 新 typed 历史到 provider；未知 schema 零调用；两角色真实 V7 Fulfillment/promotion |
| 日志、Codex 完整 UTF-8、cold/live/operator | `CompletionMetadataLoggingClientTests`、TS dispatch-inspection/durable-protocol、SemanticDelegation；真实 canary 的重复派发拒绝和 Inspect Completed |
| 真实迁移与最终运行 | 锁定备份、逐原列比较、两次冷审计、真实登录/SSE、重启后 ready/idle 和后台 deadline 观察 |

首次真实 sidecar 初始化在 Ready 前退出，原因未确认；后续两次初始化探测、真实 canary 与最终运行已成功。此观察及原失败证据保留在迁移记录，不声称已经修复一个未定位的根因。

最终 public surface 补查发现 Hosting 测试仍断言旧参数列表；已更新为当前可选 `ISessionInputProjector` 合同，并补强 Runtime 的对应断言。复验 Hosting 7/7、Runtime 4/4，通过后计入上述 46/46；此修正未改生产代码，首次失败日志同样保留。

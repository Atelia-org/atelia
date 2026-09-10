# Galatea

Galatea.Server 是基于 SessionJournal 的多用户 Role-Play Agent host。你可以在网页里交互，也可以让指定角色在服务端持续运行，再通过网页观察。关闭网页不影响已启用的服务端 Agent。

本文介绍日常运行；完整配置、API 和内部机制从[文档索引](../../docs/Galatea/README.md)进入。

## 启动服务

需要 Linux、.NET 10 SDK，以及已配置的 Completion connection。以下命令在仓库根目录执行：

```bash
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

默认读取项目 ContentRoot 下的 `.atelia/galatea/config.json`；使用上述命令时通常为 `prototypes/Galatea/.atelia/galatea/config.json`。也可以显式指定配置文件：

```bash
dotnet run --project prototypes/Galatea/Galatea.Server.csproj -- \
  --Galatea:ConfigPath=/absolute/path/to/config.json
```

首次缺少配置时，程序会生成模板并退出。这是要求检查配置的正常流程，不能直接把模板当成可运行配置。准备好以下文件后，再用同一命令启动：

| 文件 | 需要准备什么 |
|:--|:--|
| `config.json` | V8；账号和密码、角色名与玩家名、各状态目录、默认连接、监听地址 |
| 同目录 `connections.json` | V3；可用连接、可选连接列表，以及全部四个 feature bindings |
| 同目录 `delegates.json` | V2；有效的 Node/Codex/sidecar 路径与工作目录，不能留下模板占位路径 |
| character context 文件 | 检查角色设定，保留模板要求的名字变量 |
| `recapGrid.agentControlProfileFiles` 指向的文件 | **启动必需，Galatea bootstrap 不会生成**；用 SessionJournal.Cli 的 `recap-grid scaffold` 准备 |

字段说明、scaffold 步骤、配置示例和状态目录规则见[配置指南](../../docs/Galatea/configuration.md)。Route manifest 按需读取；启动成功并不表示完整 RecapGrid 已激活。

如果 `connections.json` 包含 `openai-codex-responses`，还需在启动环境设置已 provision 的 `ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT`，并将 `listenUrls` 配成 loopback 地址，例如 `http://127.0.0.1:3510`。普通模板中的 `0.0.0.0` 不适用于该模式。认证文件和环境变量细节也见配置指南。

启动成功后，打开 `listenUrls` 对应的浏览器地址。若配置为 `http://0.0.0.0:3510`，本机访问 `http://127.0.0.1:3510`；使用 `config.json` 中的 `userId` 和密码登录。登录页为 `/login`，交互页为 `/`。

修改配置或连接后需要重启。终端中按 `Ctrl+C` 正常关服，等待进程退出后再维护状态目录。

## 启用服务端自主运行

在 V8 `config.json` 根对象中，将需要持续运行的账号加入列表。例如该账号的 `userId` 为 `alice`：

```json
"serverAgentUserIds": ["alice"]
```

这是配置片段，需合入已有根对象。列表省略或为 `[]` 时，所有账号均不受后台驱动，仍可人工交互。修改后重启生效；没有运行时 enrollment 开关或管理员 pause/resume API。

- 服务端启动时检查已启用账号的 Ready 回信，之后每10秒检查一次；有回信时优先续接。
- 没有 Ready 回信时，完整空闲10分钟后可启动一次自主轮次。成功完成主线轮次会重新计时。
- 自动轮次使用该账号的 `defaultConnectionId`。网页模型选择只影响人工请求。
- 关闭或休眠网页不会停止后台 Agent。重启重新计时，不补跑停机期间的轮次。

自主轮次会正常调用模型；启用的 recall、邮件和笔记处理也会照常执行。当前服务需要由你启动和管理，尚未提供开机启动或进程崩溃后的自动重启部署。

## 在网页中交互

登录后，页面会显示近期已完成轮次、当前生成内容，以及 Agent、邮箱和 Recap 状态。

| 操作 | 行为 |
|:--|:--|
| 选择模型并发送 | 为本次人工请求选择连接，提交输入框内容 |
| 停止 | 请求停止当前轮次，等待收尾；它不是关闭后台 Agent 的总开关 |
| 撤销上一轮 | 回退最近完成的一轮，并把输入放回编辑区；可以继续撤销 |
| 恢复待处理轮次 | 显式恢复持久化的未完成轮次；结果不确定时按页面提示确认 |

网页不会自动恢复未完成轮次。观察到的后台轮次不会改变模型选择或清空草稿；本页亲自发送的人工轮次成功后才清空输入。

页面可见时会约每5秒读取 Agent 状态，并根据当前情况刷新 recent 或接入生成流。轮询期间已经完成的后台轮次会从 recent 补看；recent 是最近6轮的视图，不是完整历史浏览器。

`POST /api/v1/mailbox/ready-turn` 是已启用账号的 Dev API，网页没有对应按钮。它只立即执行一次条件检查，不强制跳过10分钟间隔。需要使用它或注入来信时，参见 [API 调用示例](../../docs/Galatea/server-api.md)。

## 查看状态

页面的“服务端 Agent”、邮箱状态和 Recap 进度分别回答不同问题：

| Agent 状态 | 含义与处理 |
|:--|:--|
| `disabled` | 账号未加入 `serverAgentUserIds` |
| `starting` | 正在建立该账号的运行会话 |
| `waiting` | 正常等待空闲间隔；页面倒计时仅供观察，服务端决定何时启动 |
| `running` | 当前有主线轮次执行中 |
| `autonomy-paused` | 上次自主轮次失败，空激活暂停，仍会检查 Ready 回信 |
| `blocked` | 需要处理 `code` 指出的原因，例如待恢复轮次或初始化失败 |
| `maintenance` | 维护模式，正常运行及发送、恢复、撤销、停止等写操作已禁用 |
| `stopping` | 正在关闭服务 |

成功完成后续主线轮次可清除会话内的失败暂停；若是初始化失败，应先检查服务端日志、修复原因再重启。存在未完成轮次时先按恢复入口处理。

邮箱状态显示排队数量、待续接回信数量及重试原因；它观察的是 Codex 代行链。Recap readiness 表示当前摘要上下文是否可用；cadence 进度表示何时达到摘要构建条件。**HistoryLoad 不是模型 token 数，也不是完整 context window 占用。**

登录后，也可在同一浏览器直接打开这些 JSON 地址：

- `/api/v1/agent/status`：后台 Agent 状态、默认连接、激活时间和原因码。
- `/api/v1/mailbox/status`：邮件链状态、排队数、Ready notice 数和重试时间。
- `/api/v1/chat/turns/current`：当前轮次及是否需要恢复。

前两个状态接口不创建会话或推动后台工作。`recent-turns` 与 `recap-cadence-progress` 则可能先按配置 attach/provision 会话；不能把所有 GET 都当成对磁盘零写。完整返回值见 [API 参考](../../docs/Galatea/server-api.md)。

## 日志与排障

开发时可使用以下 Debug 启动命令查看各环节进度：

```bash
ATELIA_DEBUG_CATEGORIES='Galatea.Autonomy,Galatea.Api,Galatea.Mailbox,Galatea.TextExtractor,Galatea.CharacterMemory,Galatea.Delegation,Galatea.Delegation.Supervisor,Galatea.DelegateSidecar' \
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

`Trace/Info` 在 Release 中不编译调用；控制台类别主要控制低级别日志，`Warning/Error` 达到控制台级别阈值后不依赖类别开关。Debug 文件日志默认写入进程工作目录下 `.atelia/debug-logs/`，不可用时回退到 `gitignore/debug-logs/`。可用 `ATELIA_DEBUG_FILE_LEVEL`、`ATELIA_DEBUG_CONSOLE_LEVEL` 调整级别。

| 现象 | 先检查 |
|:--|:--|
| 启动后生成模板并退出 | 按提示检查模板，准备有效 delegates 路径及 Agent Control profile |
| Codex connection 启动失败 | account fingerprint 环境变量、认证文件配置和 loopback 监听地址 |
| 页面显示 `disabled` | 当前登录账号是否在 `serverAgentUserIds` 中，修改后是否重启 |
| `blocked` 或需要恢复 | 页面原因码、当前轮次、`Galatea.Autonomy` 与相关服务端错误日志 |
| 切换模型后提示“结果不确定”，日志含 `reasoning replay requires Origin` | 旧版 Responses 投影错误；先核对 frozen adapter identity，旧版未完成轮次应在匹配版本上显式恢复，再升级。不要修改 Origin、清空历史或反复重试；详见[模型切换排障与升级边界](../../docs/Galatea/runtime.md#模型切换与-reasoning-回放排障) |
| 主回复已有内容但轮次未结束 | 邮件/笔记后处理可能仍在执行；检查对应日志 |
| 邮箱持续 backoff 或 `accepted-history-unavailable` | 检查 delegation 日志；已提交任务会保守查询结果，不会自动重发 |
| Recap 显示 `character-asset-mismatch` | 配置名字与 active asset 是否匹配，不能只改角色/玩家名 |

Character Note 日志及启用 `callLogDir` 后的调用日志可能含完整故事正文；不要提交到 Git。迁移状态前先停服并备份。持续无法查询到 Codex 已完成结果时，按[专门恢复 runbook](../../docs/Galatea/codex-delegation-operator-recovery.md)核实证据。

## 深入阅读

- [配置指南](../../docs/Galatea/configuration.md)：文件准备、feature bindings、Codex 接入、RecapGrid 配置。
- [HTTP / SSE API](../../docs/Galatea/server-api.md)：认证、请求示例、接口和完整状态协议。
- [内部机制](../../docs/Galatea/runtime.md)：自动轮次、Mailbox、Character Memory、恢复与资源生命周期。
- [Codex 代行验证](../../docs/Galatea/codex-delegation-verification.md)：显式启用的 canary 与有日期的历史证据。
- [文档索引与维护约定](../../docs/Galatea/README.md)：当前指南、源码入口及设计/历史材料的归属。

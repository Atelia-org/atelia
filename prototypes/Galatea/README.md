# Galatea

Galatea.Server 是基于 SessionJournal 的Player 与 Character 分离的 Role-Play Agent host。你可以在网页里交互，也可以让指定角色在服务端持续运行，再通过网页观察。关闭网页不影响已启用的服务端 Agent。

本文介绍日常运行；完整配置、API 和内部机制从[文档索引](../../docs/Galatea/README.md)进入。

## 启动服务

需要 Linux、仓库固定的 .NET 10 SDK，以及已配置的 Completion connection。当前 Completion 包
`0.1.0-preview.3` 已发布到 nuget.org；按[依赖指南](../../docs/completion-dependency.md)使用根 nuget.config。以下命令在仓库根目录执行：

```bash
dotnet restore prototypes/Galatea/Galatea.Server.csproj
dotnet run --no-restore -c Release --project prototypes/Galatea/Galatea.Server.csproj
```

默认读取项目 ContentRoot 下的 `.atelia/galatea/config.json`；使用上述命令时通常为 `prototypes/Galatea/.atelia/galatea/config.json`。也可以显式指定配置文件：

```bash
dotnet run --no-restore -c Release --project prototypes/Galatea/Galatea.Server.csproj -- \
  --Galatea:ConfigPath=/absolute/path/to/config.json
```

首次缺少配置时，程序会生成模板并退出。这是要求检查配置的正常流程，不能直接把模板当成可运行配置。准备好以下文件后，再用同一命令启动：

| 文件 | 需要准备什么 |
|:--|:--|
| `config.json` | V13；Characters 的身份/状态/home/连接与自主 interval、Players 的登录信息、Runtime 设置 |
| 同目录 `connections.json` | V3；可用连接、可选连接列表，以及全部四个 feature bindings |
| 同目录 `delegates.json` | V5；有效的 Node/Codex/sidecar 路径、已存在的 codexHome 与 allowedRoots，不能留下模板占位路径 |
| character context 文件 | 检查角色设定，保留模板要求的名字变量 |

字段说明、独立 CLI scaffold 和状态目录规则见[配置指南](../../docs/Galatea/configuration.md)。V13 host 的
`runtime.recapGrid` 只配置 maintenance（connection、全局并发、attempt timeout）；fresh bootstrap 使用
code-owned bundle，不创建 Completion client 或调用 provider。每个 persisted RowWork 以其 actual family/protocol/semantic
key 延迟构造 exact route，并复用共享 connection registry、retry 与全局 lane；已完成 Recap 的读取不依赖 route。独立 CLI 的 exact
route manifest 仍保留，不能误解为全仓删除；它也不再是 Galatea root config 的 live authority。

如果 `connections.json` 包含 `openai-codex-responses`，还需在启动环境设置已 provision 的 `ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT`。`runtime.listenUrls` 与其他连接使用相同规则，可以绑定 `0.0.0.0`。认证文件和环境变量细节也见配置指南。

启动成功后，打开 `runtime.listenUrls` 对应的浏览器地址。若配置为 `http://0.0.0.0:3510`，本机访问 `http://127.0.0.1:3510`；使用 `players[].id` 和密码登录（bootstrap 示例 ID 为 `player-main`）。登录页为 `/login`，登录后 `/` 显示角色目录，选择后进入 `/characters/{characterId}`。登录使用 `galatea_player_auth` cookie；Player 身份不决定唯一角色。

修改配置或连接后需要重启。终端中按 `Ctrl+C` 正常关服，等待进程退出后再维护状态目录。

## 启用服务端自主运行

在 V13 `config.json` 的每个 `characters[]` 项内设置必填的分钟数：

```json
"autonomyIntervalMinutes": 30
```

`0` 关闭**无 Ready reply 时**的周期 `HeartbeatActivation`；正整数 `1..525_600` 是该角色的自主激活间隔。
字段不可省略，负数与 `heartbeatEnabled` 都被 strict parser 拒绝。修改后重启生效；没有运行时 enrollment
开关。它不关闭独立的角色信 relay、durable delegation 或人工交互。`players: []` 是合法配置：无人登录，角色仍可收发信及运行委派。

- 服务端每个 Character 每 10 秒检查一次。已有 session 的待处理生成优先恢复，包括 interval 为 `0`；缺失且没有其他 wake 的 `0` 角色不会因此 provision。
- 有 durable Ready reply 或 active reply lease 时，任意 interval 都可按既有 recovery/lease 规则续接 `DelegateReply`；它不是空闲自主激活。
- 正 interval 角色在没有 Ready reply 时，完整空闲该分钟数后可启动一次自主轮次。成功完成主线轮次会重新计时；重启重新 arm，不补跑停机期间的轮次。
- 新自动轮次使用该角色的 `defaultConnectionId`；恢复已有 Prepared 使用其绑定连接与原计划。网页模型选择只影响允许选择连接的人工请求。
- 关闭或休眠网页不会停止后台 Agent。重启重新计时，不补跑停机期间的轮次。

自主轮次会正常调用模型；启用的 recall、邮件和笔记处理也会照常执行。当前服务需要由你启动和管理，尚未提供开机启动或进程崩溃后的自动重启部署。

## 在网页中交互

登录后先选择角色；角色页面会显示近期已完成轮次、当前生成内容，以及 Agent、邮箱和 Recap 状态。

| 操作 | 行为 |
|:--|:--|
| 选择模型并发送 | 为本次人工请求选择连接，提交输入框内容 |
| 停止 | 请求停止当前轮次，等待收尾；它不是关闭后台 Agent 的总开关 |
| 撤销上一轮 | 回退最近完成的一轮，并把输入放回编辑区；可以继续撤销 |
| 恢复待处理轮次 | 显式重试 blocked 的原任务；纯生成无需人工确认“结果不确定” |
| 结束待处理轮次 | 在安全边界追加结束事实，保留输入与已执行工具，不回退历史 |
| 重试未完成处理 | 重试阻塞自主活动的旧Note/发信提取或保存处理，不创建新的角色轮次 |

恢复由服务端驱动，不依赖网页在线。观察到的后台轮次不会改变模型选择或清空草稿；本页亲自发送的人工轮次成功后才清空输入。

主生成的暂时网络故障会清理调用后退避重试同一 Prepared，不重复接收输入或执行已提交工具。
默认单次期限为 30 分钟；`runtime.completionAttemptTimeoutSeconds` 可按 connection id 覆盖（1..86400 秒）。
未知协议错误、认证或永久配额错误保留任务并 blocked，不按 pulse 不断重发。重复计算可能重复计费。
Stop 接受不等于已停止；完整工具批次结算后才能持久化结束。关机取消则保留任务供启动恢复。

页面可见时会约每5秒读取 Agent 状态，并根据当前情况刷新 recent 或接入生成流。轮询期间已经完成的后台轮次会从 recent 补看；recent 是最近6轮的视图，不是完整历史浏览器。

`POST /api/v1/characters/{characterId}/mailbox/ready-turn` 是 Dev API，网页没有对应按钮。它只立即执行一次条件检查；interval 为 `0` 时仅在 durable wake evidence 存在时尝试 reply-only admission，绝不创建空闲自主轮次。需要使用它或注入来信时，参见 [API 调用示例](../../docs/Galatea/server-api.md)。

## 查看状态

页面的“服务端 Agent”、邮箱状态和 Recap 进度分别回答不同问题：

| Agent 状态 | 含义与处理 |
|:--|:--|
| `disabled` | 保留的内部投影；正常 API 的未知角色会先返回 404，不用它表达 interval 为 `0` |
| `starting` | 正在建立该角色的运行会话 |
| `waiting` | 正常等待；正 interval 有自主 deadline，`nextActivationAt...=null` 则表示 interval 为 `0`、仍监视 durable reply |
| `running` | 当前有主线轮次执行中 |
| `autonomy-paused` | 上次自主轮次失败，空激活暂停，仍会检查 Ready 回信 |
| `blocked` | 需要处理 `code` 指出的原因，例如待恢复轮次或初始化失败 |
| `maintenance` | 维护模式，正常运行及发送、恢复、撤销、停止等写操作已禁用 |
| `stopping` | 正在关闭服务 |

成功完成后续主线轮次可清除会话内的失败暂停；若是初始化失败，应先检查服务端日志、修复原因再重启。存在未完成轮次时先按恢复入口处理。

`AUTOMATIC_ADMISSION_FAILED`表示新轮次开始前的处理失败。页面可显示具体的Note提取、存储或发信处理原因，并提供“重试未完成处理”；无需为了重试而发送一句新聊天。成功重试只结算旧工作，实际自主轮次仍由服务端协调器决定；若仍有独立的回信失败或待恢复主线轮次，应继续处理对应原因。重试失败会保留阻塞，不跳过未保存内容。该操作使用[重试admission API](../../docs/Galatea/server-api.md)，维护模式不可用。

邮箱状态显示排队数量、待续接回信数量及重试原因；它观察的是 Codex 代行链。Recap readiness 表示当前摘要上下文是否可用；cadence 进度表示何时达到摘要构建条件。**HistoryLoad 不是模型 token 数，也不是完整 context window 占用。**

登录并选择角色后，也可在同一浏览器打开以下 JSON 地址；将 `{characterId}` 换成经 URL segment 编码的目标 ID：

- `/api/v1/characters/{characterId}/agent/status`：后台 Agent 状态、默认连接、激活时间和原因码。
- `/api/v1/characters/{characterId}/mailbox/status`：邮件链状态、排队数、Ready notice 数和重试时间。
- `/api/v1/characters/{characterId}/chat/turns/current`：当前轮次及是否需要恢复。

前两个状态接口不创建会话或推动后台工作。`recent-turns` 与 `recap-cadence-progress` 则可能先按配置 attach/provision 会话；不能把所有 GET 都当成对磁盘零写。完整返回值见 [API 参考](../../docs/Galatea/server-api.md)。

## 日志与排障

开发时可使用以下 Debug 启动命令查看各环节进度（文件日志默认已开，无须设置环境变量）：

```bash
dotnet run --no-restore -c Debug --project prototypes/Galatea/Galatea.Server.csproj
```

`DebugUtil.Debug` 调用只在 Debug 构建中生成；Release 包内的库内部 Debug 调用已被裁掉，环境变量不能恢复。
控制台输出一律写 stderr。文件日志写入进程工作目录下 `.atelia/debug-logs/{safe-category}.log`；
两个级别变量接受 `DEBUG`/`WARNING`/`ERROR`/`OFF`。文件 sink 默认 `DEBUG`（Debug 落盘默认开），
控制台默认 `WARNING`（安静）；需要实时视图时设 `ATELIA_DEBUG_CONSOLE_LEVEL=DEBUG`。没有类别开关。

| 现象 | 先检查 |
|:--|:--|
| 启动后生成模板并退出 | 按提示检查模板并准备有效 delegates 路径；RecapGrid 只需配置 maintenance |
| Codex connection 启动失败 | account fingerprint 环境变量、认证文件配置和服务端异常日志 |
| interval 为 `0` 但期待自主活动 | 将 `autonomyIntervalMinutes` 设为正整数后重启；`0` 仍恢复已有任务和 durable reply，但不创建空闲 heartbeat |
| `blocked` 或需要恢复 | 页面原因码、当前轮次、`Galatea.Autonomy` 与相关服务端错误日志 |
| 切换模型后提示“结果不确定”，日志含 `reasoning replay requires Origin` | 先核对当前异常与已绑定的 connection/client/API、原生载荷；旧 adapter 标签已不再作为执行身份。未完成轮次只按实际恢复状态显式处理。不要修改 Origin、清空历史或反复重试；详见[模型切换排障与升级边界](../../docs/Galatea/runtime.md#模型切换与-reasoning-回放排障) |
| 主回复已有内容但轮次未结束 | 邮件/笔记后处理可能仍在执行；检查对应日志 |
| 邮箱持续 backoff 或 `accepted-history-unavailable` | 检查 delegation 日志；已提交任务会保守查询结果，不会自动重发 |
| Recap maintenance blocked | 检查 maintenance connection 与实际 work route；既有 Recap 仍按其持久 producer 读取，默认 asset 变化不构成读取或恢复门禁 |

启用 `runtime.callLogDir` 后的新 Completion 日志只记摘要、长度、计数、耗时及异常类型，不保存请求/输出全文。Character Note 等领域 Debug 日志和已有旧全文日志仍可能含故事内容，不要提交到 Git。迁移状态前先停服并备份。持续无法查询到 Codex 已完成结果时，按[专门恢复 runbook](../../docs/Galatea/codex-delegation-operator-recovery.md)核实证据。

## 深入阅读

- [配置指南](../../docs/Galatea/configuration.md)：文件准备、feature bindings、Codex 接入、RecapGrid 配置。
- [HTTP / SSE API](../../docs/Galatea/server-api.md)：认证、请求示例、接口和完整状态协议。
- [内部机制](../../docs/Galatea/runtime.md)：自动轮次、Mailbox、Character Memory、恢复与资源生命周期。
- [Codex 代行验证](../../docs/Galatea/codex-delegation-verification.md)：显式启用的 canary 与有日期的历史证据。
- [文档索引与维护约定](../../docs/Galatea/README.md)：当前指南、源码入口及设计/历史材料的归属。

## 输入保存与升级

新 Observation 和 system setup 保存机读 JSON 事实与来源快照，给 LLM 的 Markdown 在请求时生成。
新 Prepared 保存所选语义计划，每次 Started 记录实际请求摘要；换格式不授权重发结果未知的调用。
旧 v7/v8 exact 请求仍走旧恢复合同。当前 V13 host 不接受旧 root config；停服、备份并只读检查 live session 后显式转换，详见[配置指南](../../docs/Galatea/configuration.md#从历史-root-config-切换到-v13)。旧工具 runtime 若仍处于当前尾部会明确拒绝恢复。

停服后需让后续委派使用新 Codex session，可运行 `operator reset-codex-binding --config <absolute-path> --character <id>` 预览，再追加 `--apply`。活动邮件默认拒绝；精确放弃和恢复边界见[日常解绑说明](../../docs/Galatea/codex-session-reset-design.md)。

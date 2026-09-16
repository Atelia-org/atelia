# Galatea 配置参考

本页说明 Galatea 的 operator 配置、首次生成和 RecapGrid 接入。日常启动与浏览器操作见
[Galatea 文档索引](README.md)；HTTP 路由见 [server-api.md](server-api.md)，运行时状态、恢复与维护模式见
[runtime.md](runtime.md)。根配置当前为 [V11](../SessionJournal/current/contracts/galatea-root-config-v11.md)；exact 字段由
[`GalateaStrictConfigReader`](../../prototypes/Galatea/GalateaStrictConfigReader.cs) 与
[`GalateaRootFileConfig`](../../prototypes/Galatea/GalateaConfig.cs) 定义。
[V10](../SessionJournal/current/contracts/galatea-root-config-v10.md)及更早合同仅用于识别历史数据，不是当前配置入口。

## 配置目录与首次生成

默认根配置是内容根目录下的 `.atelia/galatea/config.json`。可通过 ASP.NET Core 配置键
`Galatea:ConfigPath` 覆盖：命令行使用 `--Galatea:ConfigPath /绝对或相对路径/config.json`，环境变量使用
`Galatea__ConfigPath`。相对路径以 content root 解析，而不是以 `config.json` 的父目录猜测。

可选 ASP.NET 启动键 `Galatea:DataProtectionKeysDirectory`（环境变量
`Galatea__DataProtectionKeysDirectory`）指定登录 cookie 的 DataProtection key-ring 目录，必须为非空绝对路径。
未设置时维持 ASP.NET 原有默认行为；它不属于 strict `config.json`，也不改变会话/Completion identity。
隔离实验必须显式指向自己的私有目录。指定文件系统存储不自动提供密钥静态加密，请限制目录权限；更换 key ring
会使旧登录 cookie 无法解密，需要重新登录。实验流程见 [Scenario lab](scenario-lab.md)。

首次以一个不存在的配置路径启动时，host 会在同一目录 create-new 生成：

- `config.json`；
- `connections.json`；
- 同一目录的 `delegates.json`；
- 每个 Character 的缺失、且仍在配置目录内的 `characterContextTemplateFile`。

生成后程序会故意退出，必须检查并修改模板后再次启动。它不会覆盖已有文件，不会猜测 Codex/Node 路径，也不会生成
`runtime.recapGrid.agentControlProfileFiles` 引用的 profile、route manifest 或任何 SessionJournal state。因此首次配置的顺序是：
先让模板生成并退出，修改密码、角色、连接和 delegate placeholder，创建各 Character 的 home，再用下面的 `scaffold` 创建 RecapGrid 文件，最后启动。

默认模板有 `alice`、`bob` 两个示例 Character、独立的 `player-main` Player 和本地 `local` connection；密码、模型 ID 与 delegate 路径仍须由操作者填写。

## `config.json`

根文件必须是 strict V11 JSON：`v` 必须是整数 `11`，根字段为 `v`、`characters`、`players`、`runtime`。
`characters` 至少一项；`players` 可以为 `[]`。`runtime.recapGrid` 是必需 object。未知字段、旧版、未来版、
`null` 或 `11.0` 都拒绝；正常启动不会迁移或重写旧文件。

下面展示当前字段归属，密码位置仅为占位符；除必需的 absolute `homeDir` 外，相对路径以配置文件目录解析。

```json
{
  "v": 11,
  "characters": [{
    "id": "alice",
    "name": "Alice",
    "sessionDir": "sessions/alice",
    "delegationStateDir": "delegation-state/alice",
    "characterMemoryStateDir": "character-memory/alice",
    "homeDir": "/galatea-homes/alice",
    "sessionProvisioning": "create-if-missing",
    "defaultConnectionId": "local",
    "autonomyIntervalMinutes": 0,
    "characterContextTemplate": "",
    "characterContextTemplateFile": "prompts/character-context-standard-zh-cn.md"
  }],
  "players": [{"id": "player-main", "name": "玩家", "password": "REPLACE_WITH_A_PRIVATE_PASSWORD"}],
  "runtime": {
    "listenUrls": ["http://127.0.0.1:3510/"],
    "callLogDir": null,
    "maintenanceMode": false,
    "recapGrid": {
      "routeManifestPath": "recap-grid-routes.json",
      "agentControlProfileFiles": ["recap-grid-agent-control-profile.json"],
      "currentAgentControlProfileId": "default"
    }
  }
}
```

Character 的 `id`/`name`、状态目录、home、默认连接与心跳独立于 Player。Player 只提供 `id`、`name`、
`password`；当前所有已配置并认证的 Player 都是同一级管理员，可以选择任意 Character。`player-main` 是
bootstrap 示例 ID，不是硬编码角色或权限。`players: []` 没有可登录身份，但不停止角色心跳、来信投递或委派。

名字必须已经是 NFC 且没有首尾空白，loader 不自动 Trim/normalize。Character 名称必须大小写精确地唯一，
且不能为保留收件人 `Codex`；它们构成角色邮件地址簿。Player 名称不进入 Character 的固定设定或 Recap asset。
改角色名/设定等语义资产时仍需核对 active recipe；有未结算邮件时遵守
[角色间站内信的身份漂移前提](character-mail-design.md#6-配置漂移与运维前提)。

`sessionProvisioning` 只有两种闭合策略：

- `existing-only` 只打开已经 provision 的 repository；
- `create-if-missing` 只会为完全不存在的 `sessionDir` 原子创建首轮 repository：raw 三个 setup event、Cadence、empty Timeline、Control、Store、该 Character 的 V7 asset、empty-Timeline full recipe 与 active recipe 在同一 private staging 完成后才发布。

这不会读取 route、创建 Completion client 或调用 provider；空 Timeline 的首轮上下文仍是 raw-only。它也绝不补写已有空目录、残缺 repository 或既有 RecapGrid 派生产物。maintenance mode 不会创建 session。

每个 Character 必须提供 `autonomyIntervalMinutes` integer：`0` 关闭没有 Ready reply 时的周期 `HeartbeatActivation`，`1..525_600` 是该角色的分钟 interval。没有第二个关闭值，负数、浮点、指数和旧 `heartbeatEnabled` 都拒绝。`0` 不控制独立的角色信 relay，也不阻止人工交互或已 durable 的 Codex reply 自动续接；自动轮次始终使用该 Character 的 `defaultConnectionId`，浏览器当前连接仅影响人工请求。旧根字段 `serverAgentUserIds` 已删除。

### 角色上下文

可使用 inline `characterContextTemplate`，或以配置目录为基准的 `characterContextTemplateFile`；后者存在时覆盖 inline 内容。上下文必须含有 `${characterName}`，拒绝旧 `${playerName}` 和其他变量，不递归展开。旧设定中的固定玩家文字须显式迁移，不能用当前登录 Player 自动补入。它只提供角色语境，不定义 GM、输出或邮箱协议；这些 protocol bytes 由代码和 tracked prompt 资源拥有，详见 [prompt 资源说明](prompt/README.md)。

bootstrap 会为配置目录内的缺失文件目标 create-new；配置目录外的缺失路径不会自动创建。单个 source 和机读 system-instructions 有独立大小检查；实际投影请求在发送前再次检查。启动时读取并验证 source，SystemPromptSetup 保存原指令源与绑定快照，`${characterName}` 替换及 md-json 包装只在请求时生成；运行时不会每回合重新读取模板文件。

### 路径、持久状态与日志

`homeDir` 是每个 Character 的个人文件目录，也是 Codex 新任务的默认 CWD。它必须是预先创建的 Linux absolute canonical directory，落在 delegates 的 `allowedRoots` 内；不能与其他 home 或 session、delegation、Character Memory、call-log 目录相同或互相包含。启动只校验，不创建 home 或修补权限。bootstrap 示例使用 `/galatea-homes/alice` 与 `/galatea-homes/bob`，需在启动前按实际角色准备。

个人目录不更改 Unix `$HOME`、`~`、`CODEX_HOME` 或 Codex 登录身份，不承诺强隔离。角色在 outbound-mail 能力启用时获得实际 home 路径；新语义 Prepared 使用当时绑定的目录事实；旧 exact Prepared 保留原始 prompt bytes。修改 homeDir 后重启，旧派发先按原任务身份恢复结果，后续尚未派发任务在同一 Codex thread 使用新目录；不会自动搬移文件。

相对 `sessionDir`、`delegationStateDir`、`characterMemoryStateDir` 和 `callLogDir` 都以 `config.json` 的目录为基准；加载后使用 canonical absolute path。Character Memory 路径必须彼此唯一且不嵌套，也不能与 session、delegation 或 call log 路径嵌套；已有路径组件不能是 symlink/reparse point。

`characterMemoryStateDir` 只建立路径 authority：未绑定 Character Note、或处于 maintenance mode 时不会打开、锁定或创建其 store。启用绑定的 writable session 首次 attach 才会创建或严格打开 store。Delegation state 同样不会从其它路径回退；现有 state 与 session 缺失、schema/owner/lock 不匹配时均 fail closed。

Delegation supervisor 在 host 启动时就分类每个 Character 的状态。仅当 `delegationStateDir` 和匹配的 `sessionDir` 都存在时才 strict-open store，并持有进程生命周期的 exclusive OS writer lock；state 存在而 session 缺失时，以 `SESSION_MISSING` 在打开 SQLite/lock 前拒绝。state 不存在则保持 `Uninitialized`，直到首次 writable session attach/provision 成功后才在 exact path 创建 baseline。即使没有打开网页，已有 delegation state 也可能已被运行中的 host 持有；备份或离线操作不能只依据页面是否打开。此规则继承自 [V6 storage/delegation 合同](../SessionJournal/current/contracts/galatea-root-config-v6.md)。

`runtime.callLogDir` 启用 Completion metadata 日志：记录连接/模型、请求长度和摘要（可用时）、耗时、结果计数及异常类型，不记录请求/输出全文、工具参数或异常消息。它不构成 durable dispatch 证据。旧全文日志不会自动删除；其他领域 Debug 日志仍按各自规则处理。

## `connections.json`

`connections.json` 是 Completion endpoint catalog，与 Character/session 身份分离。Galatea 只接受 V3：根对象必须有非空 `connections`、非空 `selectableConnectionIds` 和恰好四个 `bindings`：

根 `defaultConnectionId` 已移到 `config.json` 的每个 Character，不能留在 V3 catalog 中；Galatea 不读取 V1/V2 connections。

```json
"bindings": {
  "galatea.input-normalizer": null,
  "galatea.outbound-mail-extractor": null,
  "galatea.character-note-extractor": null,
  "galatea.memo-recall": null
}
```

每个 non-null binding 必须精确指向 catalog connection；缺失、拼写大小写不符或额外 binding 会拒绝启动。`selectableConnectionIds` 是浏览器和普通 Agent 可选的 allowlist；每个 Character 的 `defaultConnectionId` 也必须在其中。RecapGrid 和 helper 可以使用不在该 allowlist 中、但由 exact route/binding 指定的连接。

四个 binding 都是显式开关：`galatea.input-normalizer` 在首次实际需要时清洗玩家输入；`galatea.outbound-mail-extractor` 从可见 Action 提取发给 Codex 的邮件；`galatea.character-note-extractor` 提取并保存 Character Note；`galatea.memo-recall` 在允许的触发点检索 Default MemoPod。值为 `null` 即禁用对应能力，非 `null` 时 client 仍按实际使用惰性创建。Memo recall 是独立 binding，可以显式复用 Character Note 的 connection ID，但不隐式复用；其非 `null` 前提是 Character Note binding 也非 `null`。

一个普通 connection 必须显式给出 `completionSurfaceId`，并在 `baseAddress`/`baseAddressEnv` 中二选一、在 `apiKey`/`apiKeyEnv` 中至多选一。把 secret 放入 `*Env` locator，而不是提交到配置文件。

### Codex subscription connection

只要 catalog 中存在 `kind: "openai-codex-responses"`，host 就进入 Codex subscription composition：

- operator 必须从已获授权的 subscription account provisioning 中取得非空
  `ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT`；不要臆造其值，也不要把 auth 内容写入配置或文档；
- 可选 `ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR`，默认 `galatea`；
- 可选 `ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE`，配置时必须是绝对路径；未配置时读取 Codex CLI 的默认 auth file。

正式 `recap-grid build` CLI 可直接读取本 V3 catalog，并以 `ParseJson` 读取现有 route 文件，允许人工格式化。
CLI 使用共用 subscription factory，但只在实际创建 Codex client 时读取 subscription 环境；默认 originator 为
`session-journal-cli`。无 missing work 不因此要求 subscription 环境/认证，普通 `apiKeyEnv/baseAddressEnv` 配置验证
仍执行。Galatea 本身仍保留上述启动时配置校验。其他 CLI connections 入口（如 `run-online-turn/llm-smoke`）
继续使用各自 V2 文件，不能直接替换成本 V3 catalog。示例和进度说明见
[CLI 构建与即时诊断](../../prototypes/SessionJournal.Cli/README.md#构建与即时诊断)。

Codex connection 与其他 Completion connection 使用相同的 ASP.NET 监听配置：`listenUrls` 交给 `UseUrls`；未配置时采用 host 的 URL 设置，显式 `Kestrel:Endpoints` 按框架规则生效。可以使用 `http://0.0.0.0:3510`，没有 Codex 专属的 loopback 限制。

## `delegates.json`

`delegates.json` 与 Completion catalog 分离，但同样位于 `config.json` 同目录，是 machine-local、启动必需的 Codex delegation 配置。它是 closed V4 schema，只允许一条大小写精确的 `recipient: "Codex"` / `kind: "codex-app-server"` route。bootstrap 写出的 placeholder 需要替换为本机已验证的 canonical path；不要保留 `REPLACE_WITH_...`。

```json
{
  "v": 4,
  "sidecar": {
    "nodeCommand": "/canonical/path/to/node",
    "entryPoint": "/canonical/path/to/local-codex-mcp/dist/src/galatea-durable-sidecar.js",
    "codexCommand": "/canonical/path/to/codex.js",
    "rpcTimeoutMs": 30000,
    "shutdownGraceMs": 5000,
    "maximumFrameUtf8Bytes": 1048576
  },
  "allowedRoots": ["/galatea-homes"],
  "routes": [{
    "recipient": "Codex",
    "kind": "codex-app-server",
    "codexConfig": {
      "sandbox_mode": "danger-full-access",
      "approval_policy": "never"
    },
    "maximumQueuedMails": 128,
    "maximumTaskUtf8Bytes": 100000,
    "maximumReplyUtf8Bytes": 100000,
    "maximumInboxReplies": 128,
    "maximumInboxUtf8Bytes": 4194304
  }]
}
```

全部路径必须是现存的 Linux absolute canonical realpath，且配置路径及其已有祖先不能含 symlink/reparse point。`nodeCommand`、`codexCommand` 必须是 executable regular file；`entryPoint` 必须是 regular file；每个 Character 的 `homeDir` 必须落在 `allowedRoots` 内；全局 route 不再接受 `cwd`。V4 删除了旧 `mode`、`localCommandNetwork`、`tools` 字段，不接受 V1–V3 配置。除可选 `codexConfig` 外，未知/缺失字段、重复或大小写变体、额外 route、路径或范围不合法均 fail closed。

`codexConfig` 使用 Codex 原生配置名，是传给 app-server thread 配置的 JSON object。省略或设为 `{}` 都不会添加配置覆盖；显式 `false` 等值会照常传递。对象可包含嵌套对象、数组、字符串、数字和布尔值，不能包含 TOML 无法表示的 `null`，各层对象键不能重复或存在大小写冲突。具体原生字段及其合法值交给 app-server 处理，Galatea 不维护另一套 Codex 配置 schema。上例显式关闭 Codex 沙盒并设置 `approval_policy: "never"`；删除整个 `codexConfig` 就恢复由 Codex 自身决定默认值。

Galatea 保留父进程的 `HOME` / `CODEX_HOME`，不会因 Character 的 `homeDir` 创建另一套 Codex home。未显式配置时，新 thread 由 Codex 正常加载公共 `config.toml` 及其原生配置层级；恢复已有 thread 时也遵循 Codex 的恢复规则，可能沿用已持久化的设置，并不强制重置为公共默认值。Galatea 不再附加 `mcp_servers={}`、`features.apps=false` 启动参数，也不再替沙盒、审批、工具开关填入隐式覆盖。

配置在 sidecar 启动时取得快照；修改 `delegates.json` 后需重启 Galatea 才会生效。显式配置会在创建 thread 和冷恢复已有 thread 时传入；同一 app-server 已加载的 thread 可能保留当前设置，不依赖 warm resume 热更新配置。bridge 仍是非交互客户端：如果继承的审批策略产生人工审批请求，现有客户端会拒绝该请求；无人值守且无需审批时应显式设置 `approval_policy: "never"`。

sidecar/app-server 进程固定从 `/` 启动；每个创建 thread / 启动 turn 请求显式携带该 Character 的 home，结果查询不依赖旧目录。共享进程不会通过 `process.chdir()` 切换用户目录。

task/reply/inbox 的限制按 strict UTF-8 bytes 计算；task/reply 即使经过最坏 JSON escaping 和 envelope reserve 也必须装入 `maximumFrameUtf8Bytes`，inbox 还必须容纳一条最大 reply 或 delivery failure。`rpcTimeoutMs` 仅限制单次 sidecar/app-server 控制 RPC，`shutdownGraceMs` 仅限制开始关服后的 child reap；两者都不是已接受 Codex turn 的生命周期 deadline。

在填写 `entryPoint` 前，先按 [local-codex-mcp 的安装、pin 与构建说明](../../local-codex-mcp/README.md#1-安装生成-schema-与构建)构建受 pin 保护的 sidecar，再填入生成物的 canonical path。delegation 的 wire、durable recovery 与 operator 处理细则见 [delegation durability design](codex-delegation-durability-design.md) 和 [operator recovery runbook](codex-delegation-operator-recovery.md)。

## RecapGrid 文件与首次 scaffold

`runtime.recapGrid` 指向 route manifest、一个或多个现有 Agent Control profile，以及 current profile ID。profile 文件必须已经存在；它是 missing-session structural bootstrap 所需的 admission authority。历史 profile 也要保留，供冻结的 Prepared/ToolContinuation 使用。route manifest 在首次 RecapGrid 工作时才读取；每条 route 精确拥有自己的 `connectionId`、并发和 timeout，不能用 default/wildcard route 或业务 output cap 覆盖 provider 的输出策略。

Galatea 将 route manifest 作为普通 V2 JSON 配置读取，允许缩进、末尾换行和属性顺序变化；仍拒绝重复/未知/缺失字段、重复 route key 和越界值。内部 canonical 编码不要求人工编辑的配置文件逐字节匹配。修改后需重启，避免继续使用已缓存的路由加载结果。

以下命令是根据当前 CLI 参数和公共 operator-chain 测试核对过的首次 scaffold 示例。将 `<配置目录>`、`<角色名>` 和 `<RecapGrid连接ID>` 换成实际值；三个输出路径须不存在，CLI 以 create-new 写入：

```bash
dotnet run --project prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj -- \
  recap-grid scaffold \
  --asset galatea-rolling-rewrite-zh-cn-v7 \
  --character-name '<角色名>' \
  --profile-id default \
  --connection-id '<RecapGrid连接ID>' \
  --permission create \
  --permission register-family \
  --permission register-definition \
  --permission register-recipe \
  --permission activate \
  --logical-column-prefix world-understanding \
  --logical-column-prefix autobiography \
  --max-bootstrap-rows 64 \
  --max-projected-calls 1024 \
  --max-concurrency 2 \
  --dispatch-timeout-ms 30000 \
  --admission-output '<配置目录>/recap-grid-admission.json' \
  --profile-output '<配置目录>/recap-grid-agent-control-profile.json' \
  --route-output '<配置目录>/recap-grid-routes.json'
```

将 `config.json` 的 `runtime.recapGrid` 内 `routeManifestPath`、`agentControlProfileFiles` 和 `currentAgentControlProfileId` 对应到上述 route/profile 输出。profile 是启动必需的 bootstrap admission；existing/raw-only session 仍可按日常 Galatea 流程运行，host 不会为既有 repository 补写派生状态或调用 Recap provider，主 Agent 仍会调用其 Completion connection。普通 `GetSessionAsync()` 不会 repair 已有 repository。对完全不存在的 new session，staging bootstrap 会一并建立 Store、该 Character 的 asset、empty-Timeline full recipe 和 active recipe；整个过程仍不会读取 route、创建 Completion client 或调用 provider。因为还没有历史行，首轮 context 仍是 raw-only。

对已有 raw-only/partial session 的完整启用，必须停服、备份、先做 strict read-only audit，再使用专用 bounded admission 走 `init`、受限 `timeline sync`、`control provision-asset`、compose/put recipe、有界 candidate build 与 `control promote`。`build` 才是 provider effect，direct `activate` 不能取代 promotion；未知结果绝不自动重试。完整、按当前 CLI 参数编写的流程见[已有 SessionJournal 的 RecapGrid 显式升级](recap-grid-existing-session-upgrade.md)。`provision-asset` 必须使用与 scaffold 完全相同的 `--character-name`。scaffold 不会创建 provider、Timeline、Control 或 Store，Galatea 的 route manifest 读取允许上述 JSON 格式化；其他持久产物仍使用各自的 canonical 格式。

该 asset 包含 `world-understanding` 与 `autobiography` 两列。Host 会在 fresh admission 前验证 active recipe 是否精确匹配该角色的新语义资产定义；不匹配时以 `character-asset-mismatch` fail closed。CLI 的完整 operator 链见 [SessionJournal.Cli operator 指南](../../prototypes/SessionJournal.Cli/README.md)，运行期观察字段见 [runtime.md](runtime.md)。

## V11 唯一开发实例的人工切换

V11 binary 严格拒绝 V10；不存在 V10 runtime reader、自动转换器或 CLI migration。当前仅有一份开发实例，切换由 operator 人工完成：

1. 正常停服并确认 writer 已退出。
2. 在 session、delegation、CharacterMemory 等状态目录**之外**备份 `config.json`。
3. 将根 `v` 从 `10` 改为 `11`；每个 `heartbeatEnabled:true` 改为 `autonomyIntervalMinutes:10`，每个 `false` 改为 `0`，删除旧字段。之后可按角色模型配额调整正分钟数。
4. 用 V11 binary 重启；如果仍有 V10 字段或版本，strict parser 会 fail closed。

不要把此配置编辑当成 SQLite、prompt asset、未完成 delegation 或历史 Observation 的迁移：本次不改写任何这些状态，也不自动重发外部工作。旧 V9→V10 的实例迁移记录仍见[实施工作单](player-character-implementation-work-order.md)，它是历史证据而非当前操作入口。

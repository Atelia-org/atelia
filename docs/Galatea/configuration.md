# Galatea 配置参考

本页说明 Galatea 的 operator 配置、首次生成和 RecapGrid 接入。日常启动与浏览器操作见
[Galatea 文档索引](README.md)；HTTP 路由见 [server-api.md](server-api.md)，运行时状态、恢复与维护模式见
[runtime.md](runtime.md)。根配置的完整 closed schema 以
[Root config V8 合同](../SessionJournal/current/contracts/galatea-root-config-v8.md)为准。

## 配置目录与首次生成

默认根配置是内容根目录下的 `.atelia/galatea/config.json`。可通过 ASP.NET Core 配置键
`Galatea:ConfigPath` 覆盖：命令行使用 `--Galatea:ConfigPath /绝对或相对路径/config.json`，环境变量使用
`Galatea__ConfigPath`。相对路径以 content root 解析，而不是以 `config.json` 的父目录猜测。

首次以一个不存在的配置路径启动时，host 会在同一目录 create-new 生成：

- `config.json`；
- `connections.json`；
- 同一目录的 `delegates.json`；
- 每个 user 的缺失、且仍在配置目录内的 `characterContextTemplateFile`。

生成后程序会故意退出，必须检查并修改模板后再次启动。它不会覆盖已有文件，不会猜测 Codex/Node 路径，也不会生成
`recapGrid.agentControlProfileFiles` 引用的 profile、route manifest 或任何 SessionJournal state。因此首次配置的顺序是：
先让模板生成并退出，修改密码、角色、连接和 delegate placeholder，再用下面的 `scaffold` 创建 RecapGrid 文件，最后启动。

默认模板有 `alice`、`bob` 两个示例账户和本地 `local` connection；示例密码、模型 ID、API key 与 delegate 路径都不能直接用于实际服务。

## `config.json`

根文件必须是 strict V8 JSON：`"v"` 必须是整数 `8`，必须有至少一个 `users` 和一个 `recapGrid` object。未知字段、旧版、未来版、`null` 或 `8.0` 都会拒绝；程序不会自动迁移或重写此文件。升级时应停服、备份，并显式完成 schema 变更。

下面是完整、可识别的 V8 形状。路径相对于本文件所在的配置目录；这里的 loopback `listenUrls` 适合仅本机访问，若使用普通 Completion endpoint，也可按部署需要改为局域网监听地址。

```json
{
  "v": 8,
  "serverAgentUserIds": [],
  "users": [{
    "userId": "alice",
    "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
    "characterName": "Alice",
    "playerName": "Alex",
    "sessionDir": "sessions/alice",
    "delegationStateDir": "delegation-state/alice",
    "characterMemoryStateDir": "character-memory/alice",
    "sessionProvisioning": "create-if-missing",
    "defaultConnectionId": "local",
    "characterContextTemplate": "",
    "characterContextTemplateFile": "prompts/character-context-standard-zh-cn.md"
  }],
  "listenUrls": ["http://127.0.0.1:3510/"],
  "callLogDir": null,
  "maintenanceMode": false,
  "recapGrid": {
    "routeManifestPath": "recap-grid-routes.json",
    "agentControlProfileFiles": ["recap-grid-agent-control-profile.json"],
    "currentAgentControlProfileId": "default"
  }
}
```

每个 user 的 `userId`、`password`、`characterName`、`playerName`、`sessionDir`、`delegationStateDir`、`characterMemoryStateDir`、`sessionProvisioning` 和 `defaultConnectionId` 都是业务配置。`characterName` 与 `playerName` 是独立故事身份，不从登录 ID 推导；它们必须已经是 NFC 且没有首尾空白，loader 会拒绝非规范输入而不会自动 `Trim` 或 normalize。已有 session 不能只改这两个名字，必须停服后迁移或重建 RecapGrid asset 并切换 active recipe。

`sessionProvisioning` 只有两种闭合策略：

- `existing-only` 只打开已经 provision 的 repository；
- `create-if-missing` 只会为完全不存在的 `sessionDir` 原子创建首轮 raw-only repository。

后者不补写已有空目录、残缺 repository 或 RecapGrid 派生产物。maintenance mode 也不会创建 session。

`serverAgentUserIds` 省略或 `[]` 时禁用所有服务端自动轮次；列出的 user 必须存在且不能重复。自动轮次始终使用该 user 的 `defaultConnectionId`，浏览器中当前选中的连接仅影响人工请求。

### 角色上下文

可使用 inline `characterContextTemplate`，或以配置目录为基准的 `characterContextTemplateFile`；后者存在时覆盖 inline 内容。上下文必须含有 `${characterName}`，可选 `${playerName}`，不支持其他变量或递归展开。它只提供角色语境，不定义 GM、输出或邮箱协议；这些 protocol bytes 由代码和 tracked prompt 资源拥有，详见 [prompt 资源说明](prompt/README.md)。

bootstrap 会为配置目录内的缺失文件目标 create-new；配置目录外的缺失路径不会自动创建。单个 source 以及组合后的最终 system prompt 都有 1 MiB 上限，运行时不会每回合重新读取模板文件。

### 路径、持久状态与日志

相对 `sessionDir`、`delegationStateDir`、`characterMemoryStateDir` 和 `callLogDir` 都以 `config.json` 的目录为基准；加载后使用 canonical absolute path。Character Memory 路径必须彼此唯一且不嵌套，也不能与 session、delegation 或 call log 路径嵌套；已有路径组件不能是 symlink/reparse point。

`characterMemoryStateDir` 只建立路径 authority：未绑定 Character Note、或处于 maintenance mode 时不会打开、锁定或创建其 store。启用绑定的 writable session 首次 attach 才会创建或严格打开 store。Delegation state 同样不会从其它路径回退；现有 state 与 session 缺失、schema/owner/lock 不匹配时均 fail closed。

Delegation supervisor 在 host 启动时就分类每个 user 的状态。仅当 `delegationStateDir` 和匹配的 `sessionDir` 都存在时才 strict-open store，并持有进程生命周期的 exclusive OS writer lock；state 存在而 session 缺失时，以 `SESSION_MISSING` 在打开 SQLite/lock 前拒绝。state 不存在则保持 `Uninitialized`，直到首次 writable session attach/provision 成功后才在 exact path 创建 baseline。即使没有打开网页，已有 delegation state 也可能已被运行中的 host 持有；备份或离线操作不能只依据页面是否打开。此规则继承自 [V6 storage/delegation 合同](../SessionJournal/current/contracts/galatea-root-config-v6.md)。

`callLogDir` 会记录 provider request 和工具参数，可能含故事内容和敏感数据；请将其放入受限的本地目录，并自行安排保留期。

## `connections.json`

`connections.json` 是 Completion endpoint catalog，与 user/session 身份分离。Galatea 只接受 V3：根对象必须有非空 `connections`、非空 `selectableConnectionIds` 和恰好四个 `bindings`：

根 `defaultConnectionId` 已移到 `config.json` 的每个 user，不能留在 V3 catalog 中；Galatea 不读取 V1/V2 connections。

```json
"bindings": {
  "galatea.input-normalizer": null,
  "galatea.outbound-mail-extractor": null,
  "galatea.character-note-extractor": null,
  "galatea.memo-recall": null
}
```

每个 non-null binding 必须精确指向 catalog connection；缺失、拼写大小写不符或额外 binding 会拒绝启动。`selectableConnectionIds` 是浏览器和普通 Agent 可选的 allowlist；每个 user 的 `defaultConnectionId` 也必须在其中。RecapGrid 和 helper 可以使用不在该 allowlist 中、但由 exact route/binding 指定的连接。

四个 binding 都是显式开关：`galatea.input-normalizer` 在首次实际需要时清洗玩家输入；`galatea.outbound-mail-extractor` 从可见 Action 提取发给 Codex 的邮件；`galatea.character-note-extractor` 提取并保存 Character Note；`galatea.memo-recall` 在允许的触发点检索 Default MemoPod。值为 `null` 即禁用对应能力，非 `null` 时 client 仍按实际使用惰性创建。Memo recall 是独立 binding，可以显式复用 Character Note 的 connection ID，但不隐式复用；其非 `null` 前提是 Character Note binding 也非 `null`。

一个普通 connection 必须显式给出 `completionSurfaceId`，并在 `baseAddress`/`baseAddressEnv` 中二选一、在 `apiKey`/`apiKeyEnv` 中至多选一。把 secret 放入 `*Env` locator，而不是提交到配置文件。

### Codex subscription connection

只要 catalog 中存在 `kind: "openai-codex-responses"`，host 就进入 Codex subscription composition：

- operator 必须从已获授权的 subscription account provisioning 中取得非空
  `ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT`；不要臆造其值，也不要把 auth 内容写入配置或文档；
- 可选 `ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR`，默认 `galatea`；
- 可选 `ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE`，配置时必须是绝对路径；未配置时读取 Codex CLI 的默认 auth file。

这与普通 endpoint 不同：有效 `listenUrls` 必须是无 query/fragment/path 的 HTTP/HTTPS loopback URL（`localhost` 或 loopback IP），例如 `http://127.0.0.1:3510/`。此模式下代码直接拥有 Kestrel listener，环境或 appsettings 中的 `Kestrel:Endpoints` 不能将其改为非 loopback。普通 connection 则按 `listenUrls` 使用 host URL 配置，可使用局域网监听地址。

## `delegates.json`

`delegates.json` 与 Completion catalog 分离，但同样位于 `config.json` 同目录，是 machine-local、启动必需的 Codex delegation 配置。它是 closed V2 schema，只允许一条大小写精确的 `recipient: "Codex"` / `kind: "codex-app-server"` route。bootstrap 写出的 placeholder 需要替换为本机已验证的 canonical path；不要保留 `REPLACE_WITH_...`。

```json
{
  "v": 2,
  "sidecar": {
    "nodeCommand": "/canonical/path/to/node",
    "entryPoint": "/canonical/path/to/local-codex-mcp/dist/src/galatea-durable-sidecar.js",
    "codexCommand": "/canonical/path/to/codex.js",
    "rpcTimeoutMs": 30000,
    "shutdownGraceMs": 5000,
    "maximumFrameUtf8Bytes": 1048576
  },
  "allowedRoots": ["/canonical/allowed/root"],
  "routes": [{
    "recipient": "Codex",
    "kind": "codex-app-server",
    "cwd": "/canonical/allowed/root",
    "mode": "work",
    "localCommandNetwork": true,
    "tools": {
      "webSearch": "live",
      "imageGeneration": true,
      "viewImage": true
    },
    "maximumQueuedMails": 128,
    "maximumTaskUtf8Bytes": 100000,
    "maximumReplyUtf8Bytes": 100000,
    "maximumInboxReplies": 128,
    "maximumInboxUtf8Bytes": 4194304
  }]
}
```

全部路径必须是现存的 Linux absolute canonical realpath，且配置路径及其已有祖先不能含 symlink/reparse point。`nodeCommand`、`codexCommand` 必须是 executable regular file；`entryPoint` 必须是 regular file；`cwd` 必须落在 `allowedRoots` 内。`mode` 只接受 `research` 或 `work`，`webSearch` 只接受 `disabled`、`cached`、`indexed` 或 `live`。未知/缺失字段、重复或大小写变体、额外 route、路径或范围不合法均 fail closed。

task/reply/inbox 的限制按 strict UTF-8 bytes 计算；task/reply 即使经过最坏 JSON escaping 和 envelope reserve 也必须装入 `maximumFrameUtf8Bytes`，inbox 还必须容纳一条最大 reply 或 delivery failure。`rpcTimeoutMs` 仅限制单次 sidecar/app-server 控制 RPC，`shutdownGraceMs` 仅限制开始关服后的 child reap；两者都不是已接受 Codex turn 的生命周期 deadline。

在填写 `entryPoint` 前，先按 [local-codex-mcp 的安装、pin 与构建说明](../../local-codex-mcp/README.md#1-安装生成-schema-与构建)构建受 pin 保护的 sidecar，再填入生成物的 canonical path。delegation 的 wire、durable recovery 与 operator 处理细则见 [delegation durability design](codex-delegation-durability-design.md) 和 [operator recovery runbook](codex-delegation-operator-recovery.md)。

## RecapGrid 文件与首次 scaffold

`recapGrid` 指向 route manifest、一个或多个现有 Agent Control profile，以及 current profile ID。profile 文件必须已经存在；它是 missing-session structural bootstrap 所需的 admission authority。历史 profile 也要保留，供冻结的 Prepared/ToolContinuation 使用。route manifest 在首次 RecapGrid 工作时才读取；每条 route 精确拥有自己的 `connectionId`、并发和 timeout，不能用 default/wildcard route 或业务 output cap 覆盖 provider 的输出策略。

以下命令是根据当前 CLI 参数和公共 operator-chain 测试核对过的首次 scaffold 示例。将 `<配置目录>`、`<角色名>`、`<玩家名>` 和 `<RecapGrid连接ID>` 换成实际值；三个输出路径须不存在，CLI 以 create-new 写入：

```bash
dotnet run --project prototypes/SessionJournal.Cli/SessionJournal.Cli.csproj -- \
  recap-grid scaffold \
  --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name '<角色名>' \
  --player-name '<玩家名>' \
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

将 `config.json` 的 `routeManifestPath`、`agentControlProfileFiles` 和 `currentAgentControlProfileId` 对应到上述 route/profile 输出。profile 是启动必需的 bootstrap admission；但完整 RecapGrid 是可选增强：没有 active recipe 的 existing/raw-only session 仍可按日常 Galatea 流程运行，host 不会为既有 repository 补写派生状态或调用 Recap provider，主 Agent 仍会调用其 Completion connection。只在要为一个适格的 session 显式启用完整 RecapGrid 时，才按 [SessionJournal.Cli operator 指南](../../prototypes/SessionJournal.Cli/README.md)运行 `recap-grid init`、`control provision-asset`、compose/put/activate recipe 与 build；不要把 `init` 对已有 repository 当成通用修复命令。`provision-asset` 必须使用与 scaffold 完全相同的 `--character-name` 和 `--player-name`。scaffold 不会创建 provider、Timeline、Control 或 Store，Galatea 只消费它们的 strict canonical outputs。

该 asset 包含 `world-understanding` 与 `autobiography` 两列。Host 会在 fresh admission 前验证 active recipe 是否精确匹配该 user 的两个名字；不匹配时以 `character-asset-mismatch` fail closed。CLI 的完整 operator 链见 [SessionJournal.Cli operator 指南](../../prototypes/SessionJournal.Cli/README.md)，运行期观察字段见 [runtime.md](runtime.md)。

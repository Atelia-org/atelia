# Galatea.Server

家庭局域网里的单会话 Chat 服务。每个账号绑定一个已经 provision 的
`SessionJournal` repository；raw journal 是会话正确性来源，DerivedRecap 是可恢复、可重建的
provider context sidecar。

## 启动前准备

```bash
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

首次启动会生成 `.atelia/galatea/config.json` 和同目录的 `connections.json` 模板，然后退出。
Galatea 不会在第一次 Send 时自动创建或补齐会话仓库。每个 `sessionDir` 必须由 operator 预先准备：

- 一个有效的 SessionJournal main branch；
- 对应 RefId 的 `derived/recap/v4` Store；
- repository-owned `config/recap-planner-config.json`。

可使用 `prototypes/SessionJournal.Cli` 的 import/provision/config 命令完成这一步。Host启动只加载并
验证配置，不会遍历或打开各账号的raw repo。某个账号第一次访问需要session的endpoint时，Host才按
`sessionDir` lazy open该用户repo并检查durable phase；此时仍不会创建Completion client、
DerivedRecap scaffolding或加载active Planner config。

## 配置

`config.json` 只保存账号、session路径、system prompt与监听地址：

```json
{
  "users": [
    {
      "userId": "alice",
      "password": "replace-me",
      "sessionDir": ".atelia/galatea/sessions/alice",
      "systemPromptFile": "prompts/alice.md"
    }
  ],
  "callLogDir": "../../../../gitignore/galatea/completion-calls",
  "maintenanceMode": false,
  "listenUrls": ["http://0.0.0.0:3510"]
}
```

`systemPromptFile` 相对 `config.json` 所在目录解析，内容覆盖内联 `systemPrompt`。旧
`compactionThresholdTokens` / `compactionSystemPrompt` / `compactionPrompt` 已删除；Recap cadence、
profiles和limits统一由每个SessionJournal repo中的 `recap-planner-config.json` 管理。

`callLogDir`是可选的，也相对`config.json`所在目录解析。配置后，agent调用写入
`agent/`，Maintainer调用按profile写入`maintenance/<maintainer-id>/`；该目录必须与所有
`sessionDir`互不包含。未配置时不包装client、不创建日志目录。日志wrapper透传client identity，
因此开关日志不会改变Prepared中冻结的completion target；日志可能包含完整prompt/response，应位于
repo之外且按敏感数据管理。日志是best-effort operational evidence：初始化、reserve、write、flush
或cleanup失败都不会令agent Send或Maintainer调用失败，也不会替换provider异常；相应调用可能没有
call-log文件。初始化失败会在该wrapper的剩余生命周期禁用日志；cleanup失败可能留下未登记且不完整的
orphan文件。只有完成serialize/write/flush/close并成功登记的文件才计为成功日志，orphan不得用于推断
调用次数、provider结果或recovery状态。

`maintenanceMode`是startup-time只读开关，默认`false`。设为`true`后，fresh send、durable
resume、Undo与stop endpoint都会在打开session前返回typed `503 maintenance-mode`。登录、页面和
`/api/me`不打开repo；current、recent等首次需要该用户session的读取endpoint会lazy open
read-only `SessionJournalEngine`，形成第二层写保护。SSE成功订阅只附着已有in-memory turn，但endpoint
首次访问仍会先解析并lazy open用户session，再查找turn。页面会显示维护提示并禁用所有写按钮。该开关
不会热加载，也没有admin bypass；解除维护需要修改config并重启，外部
ingress应保持关闭直到重启后对目标用户完成所需的只读检查。

`connections.json` 保存可选的Completion routes：

```json
{
  "defaultConnectionId": "dsv4p",
  "connections": [
    {
      "id": "dsv4p",
      "kind": "openai-chat",
      "modelId": "deepseek-v4",
      "completionSurfaceId": "openai-chat/deepseek-v4",
      "requestTimeoutSeconds": 300,
      "baseAddress": "https://example.invalid/v1/",
      "apiKeyEnv": "DEEPSEEK_API_KEY"
    }
  ]
}
```

`requestTimeoutSeconds`是可选的connection-local operation policy，范围1..3600；未配置时保持100秒
默认值。timeout覆盖完整streaming operation，包括收到response headers之后持续读取SSE body的阶段；
effective timeout会进入Completion call log，便于解释provider等待失败，但不会进入durable dispatch
fingerprint：它不改变endpoint/model/request wire，调整纯等待策略也不应让已经Prepared的exact
recovery失去binding。

Fresh Send 会在 exact Idle head 执行 desired setup reconciliation，再做 Building-first Recap
preparation；只有这些检查成功后才运行输入清洗并创建provider client。Prepared recovery精确绑定
durable completion identity，不回退default connection，也不读取active Recap config。

### `PublishedPlanUnavailable`排障

如果前端或`galatea.api.log`报告`PublishedPlanUnavailable`，先用只读命令检查 exact Published
slot；不要据此判断Completion provider不可用：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap materialize-inspect --input <sessionDir> --branch main
```

若 defect 是`Unsupported publication schema`或`Unsupported manifest schema`，说明
DerivedRecap payload来自旧 wire schema。`derived/recap/v4`目录名和Store header仍为v4，但当前
manifest/publication payload是v6；项目不提供旧 payload兼容读取。先确认当前branch的exact RefId，
再显式隔离旧Store并从raw journal重建：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap reset --input <sessionDir> --branch main --confirm-ref <exact-ref-id>

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap run --input <sessionDir> --branch main \
  --connections <connections.json> --connection <id>
```

`reset`会把旧branch-local Store原子移动到同一`refs/`目录下的quarantine，而不修改raw
SessionJournal或repo-owned Planner config；`recap run`可能产生Maintainer LLM调用。若当前schema
的Published block损坏，应优先检查是否可用exact `recap restore`，不要把所有损坏都等同于旧schema。

## Durable recovery

- 新消息只能进入 `Idle`；`TurnFailed` 会先做 exact abandon。
- Observation、Prepared、Started或tool tail不会被新消息覆盖，返回 `409 recovery-required`。
- `POST /api/chat/turns/resume` 不接受新消息，并携带 current endpoint 返回的 exact
  `recoveryHead`。
- Prepared 可安全恢复；Started 默认拒绝，只有显式
  `restartUncertainCompletion=true` 才接受可能重复调用的风险。
- G1 的 tool continuation 明确 unsupported。

页面会自动恢复安全的 Observation/Prepared tail；Started uncertain 保留为人工确认状态。

## Recent与Undo

`GET /api/recent-turns` 的权威输入是raw completed-turn projection，newest-first返回最近6个可见turn。
Host在per-session writer gate内重建一份只读cache，使页面在active turn期间仍能reload/reattach而不读取
正在append的SessionJournal；cache不是authority，进程重启或writer完成后都可从raw projection重建。
active turn一经接受，cached `rewindLatestToken`立即失效。只有captured raw head本身就是最新terminal
Action时才重新返回该token。Undo必须原样回传此token；server使用它执行exact-head CAS，因此陈旧页面
不会误撤后来新增的turn。DerivedRecap不会被投影为conversation turn。

## 输入清洗与停止

设置 `DEEPSEEK_BASE_URL` 和 `DEEPSEEK_API_KEY` 后，可在主模型调用前进行短消息最小纠错。失败会回退
原文；成功结果进入durable Observation。

Stop 在Recap lifecycle成功前取消pre-dispatch工作；成功后只通过stream observer停止provider，避免
取消Prepared/Started持久化步骤。只有known failed turn被exact abandon成功后，Host才承诺该轮未进入
active history。每个账号同一时刻只有一个SessionJournal engine driver：per-session `TurnLock`覆盖所有
durable read/write；active turn期间current、recent、busy、SSE与stop只读in-memory live state或上述cache，
不接触Engine。idle current在取得gate后才检查durable recovery。SSE订阅与stop endpoint不占writer lock。

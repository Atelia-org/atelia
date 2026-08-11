# Galatea.Server

家庭局域网里的单会话 Chat 服务。每个账号绑定一个已经 provision 的
`SessionJournal` repository；raw journal 是会话正确性来源，DerivedRecap 是可恢复、可重建的
provider context sidecar。

## 启动前准备

```bash
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

首次启动会生成 `.atelia/galatea/config.json` 和同目录的 `connections.json` 模板，然后退出。
Galatea 不会在第一次 Send 时创建raw SessionJournal repository。每个 `sessionDir` 必须由 operator 预先准备
一个有效的 SessionJournal main branch。DerivedRecap v8 sidecar与Planner config边界是：

- 第一次writable recap preparation会为exact RefId验证或创建`derived/recap/v8` Store；
- `config/recap-planner-config.json`缺失时使用built-in v3 defaults；需要固定非默认cadence/limits时由operator
  预先写入repository-owned config。

可使用 `prototypes/SessionJournal.Cli` 的 import/provision/config 命令完成这一步。Host启动只加载并
验证配置，不会遍历或打开各账号的raw repo。某个账号第一次访问需要session的endpoint时，Host才按
`sessionDir` lazy open该用户repo并检查durable phase；单独读取current仍不会创建Completion client、
DerivedRecap scaffolding或加载active Planner config。`GET /api/recent-turns`还会在取得该session的
writer gate后执行一次只读DerivedRecap planning progress inspection；它只打开已有Store、读取repo
config与bounded raw planning facts，不创建Completion client、Maintainer或lifecycle，也不写Store。

WP-07B另有一个只供internal tests显式构造的RecapGrid candidate composition。它没有注册到Program DI、没有env/config flag，也不改变
本README上述production default。candidate只在Fresh/NewRequest为每turn创建Online handle；Frozen Prepared沿frozen request exact bind，
Started/Refuse在client前终止。candidate recent projection不读取old v8 planner。Agent-facing Control tool、built-in genesis与
ToolResult/ToolContinuation仍属于WP-07C；normal Host不会自动provision Timeline/Control/Grid。

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
调用次数、provider结果或recovery状态。call-log v8结构化记录request的prompt prefix、output contract、
shared context、tail与max tokens，并在connection snapshot之外另行记录每次调用请求的
`invocationOptions.promptCacheReuseHint`；response存在时还记录provider-neutral usage/cache telemetry。
Recap调用另带member/target/source/family、Leader/Follower和dispatch/lane wait attribution。运行hint与usage
均不会改变wrapper透传的dispatch identity或durable recap identity。

`maintenanceMode`是startup-time只读开关，默认`false`。设为`true`后，fresh send、durable
resume、Undo与stop endpoint都会在打开session前返回typed `503 maintenance-mode`。登录、页面和
`/api/me`不打开repo；current、recent等首次需要该用户session的读取endpoint会lazy open
read-only `SessionJournalEngine`，形成第二层写保护；recent中的planning inspection同样保持纯只读。
SSE成功订阅只附着已有in-memory turn，但endpoint
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
      "baseAddress": "https://example.invalid/v1/",
      "apiKeyEnv": "DEEPSEEK_API_KEY"
    }
  ]
}
```

Anthropic route 可以通过 `anthropicPromptCacheTtl` 选择 prompt cache 的存续档位；例如人在回合间可能需要超过五分钟回复时，可配置为一小时：

```json
{
  "defaultConnectionId": "opus",
  "connections": [
    {
      "id": "opus",
      "kind": "anthropic",
      "modelId": "claude-opus-4-6",
      "completionSurfaceId": "anthropic",
      "baseAddress": "https://api.anthropic.com/",
      "apiKeyEnv": "ANTHROPIC_API_KEY",
      "anthropicPromptCacheTtl": "1h"
    }
  ]
}
```

允许值为 `provider-default|5m|1h`。缺省的 `provider-default` 保持厂商默认行为，并在 wire 中省略
`ttl`；非 Anthropic connection 配置非默认值会 fail fast。该值会写入Completion call-log v8的
connection snapshot，但作为可调整的运行策略不进入durable dispatch fingerprint。Galatea agent当前
使用`ConnectionDefault` invocation hint，所以继续继承所选connection配置（上例为一小时TTL）；single-shot的
shared recap `RecapExecutionLane`则显式传递`ReuseExpectedSoon`，Anthropic adapter把它映射为5分钟
typed prefix cache boundary。该hint只证明cache request intent；实际write/read必须由response usage证明。
连续tool-loop采用短TTL、停下来等待用户时采用长TTL的动态策略尚未实现；connection配置仍在创建client时
确定。

Galatea的`connections.json`还可在shared Completion connection字段之外，显式绑定每个built-in
Recap Maintainer所用的connection：

```json
{
  "defaultConnectionId": "opus",
  "recapMaintainerConnections": [
    {
      "maintainerId": "roleplay.world-understanding.rewrite",
      "connectionId": "deepseek"
    },
    {
      "maintainerId": "roleplay.first-person-autobiography.rewrite",
      "connectionId": "opus"
    }
  ],
  "connections": [
    {
      "id": "deepseek",
      "kind": "openai-chat",
      "modelId": "deepseek-v4-pro",
      "completionSurfaceId": "openai-chat/deepseek-v4",
      "baseAddress": "https://example.invalid/v1/",
      "apiKeyEnv": "DEEPSEEK_API_KEY"
    },
    {
      "id": "opus",
      "kind": "anthropic",
      "modelId": "claude-opus-4-6",
      "baseAddress": "https://api.anthropic.com/",
      "apiKeyEnv": "ANTHROPIC_API_KEY",
      "anthropicPromptCacheTtl": "1h"
    }
  ]
}
```

`recapMaintainerConnections`整段缺失时保持legacy行为：两个Maintainer都跟随本轮选中的agent
connection。该字段一旦出现（包括空数组），就必须按case-sensitive exact ID完整覆盖当前built-in
catalog；blank、duplicate、unknown、missing maintainer或unknown connection都会在config load时
fail fast，不会回退default connection。该mapping只属于Galatea runtime composition：它不写入
`recap-planner-config.json`、Store/Planner/raw schema、Maintainer capability fingerprint或agent的
durable completion target。

具体Maintainer registry继续延迟到首次真实binding lookup才构造。`NoBuild`、readiness failure以及只需
精确恢复frozen agent completion的路径都不会因此创建recap-only client或logger；当前首次lookup仍会
一次构造完整built-in catalog。active Building重启后的剩余步骤使用当次启动加载的当前route，
connection/model不会冻结进Building。

Completion runtime不设置elapsed-operation timeout：provider在不可见reasoning或排队期间即使长时间没有
输出，client也会继续等待。调用只会因caller cancellation、明确的HTTP/连接错误，或stream在provider
terminal event之前断开而结束；静默本身不被解释为LLM failure。连接等待策略不进入durable dispatch
fingerprint。

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

当前production root是`derived/recap/v8`，Store header、epoch input、manifest、direct final与publication
均使用strict v8 wire。若出现`Unsupported ... schema`，说明selected v8 sidecar含旧或损坏payload；旧v4-v7
sidecar/wire只属于historical evidence，current reader不读取、迁移或fallback。先确认当前branch的exact
RefId，再通过显式rebuild先seal raw authority、reset current v8 Store并从raw journal重建：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap rebuild --input <sessionDir> --branch main \
  --campaign <safe-id> --reset --confirm-ref <exact-ref-id> \
  --connections <connections.json> --connection <id>
```

若返回`MoreWorkPending`，使用相同campaign且不再传`--reset`继续。rebuild在destructive reset前完成sealed
selected-lineage audit；reset只隔离current v8 branch-local Store，不修改raw SessionJournal或repo-owned
Planner config。若current v8 Published block损坏，普通`recap run`会先尝试exact frozen repair；不要把所有
损坏都等同于旧schema。

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
不会误撤后来新增的turn。成功后若新head本身是上一轮terminal Action，响应会携带下一枚token，页面可
继续逐轮Undo；每次只回填刚撤销轮次的用户输入。若用户已修改该回填文本，再次Undo会先确认，避免静默
覆盖未发送草稿。setup-only suffix、active/recovery tail或已经到达初始setup边界时不会跨边界继续回退。
DerivedRecap不会被投影为conversation turn。

`RecentTurnsResponseDto.recapPlanning`同时暴露当前stable raw head上的DerivedRecap cadence快照：

- `recentHistoryUnitCount`是latest Published `SetAdmissionAnchor`（首次build前为SessionCreated
  boundary）之后dependency-closed `SessionHistoryPlanningUnit`的数量；它用于解释history suffix，
  不是cadence trigger；
- `recentHistoryLoad`、`minimumRecentHistoryLoad`（R）、`recapBuildIntervalHistoryLoad`（B）、
  `buildThresholdHistoryLoad`（R+B）与`remainingHistoryLoad`给出exact cadence进度；HistoryLoad是
  Planner内部度量，不是provider/model token数；
- `cadence-ready`只表示下次lifecycle会尝试build，不承诺后台立即运行；达到阈值后仍可能处于
  `awaiting-replay-safe-admission`；current-lineage Building则显示`frozen-building`，下一次lifecycle
  按frozen authority恢复；
- `freshness=exact`只有在recent raw projection与Planner inspection观察到同一raw head时才发布。
  active writer期间recent endpoint绝不读取Engine/Store/config，只返回`freshness=stale`的上一稳定
  cache并清除rewind token；terminal success、failure/stop收尾与Undo都会在释放writer gate前刷新；
- Planner/Store/config inspection defect只把`recapPlanning`标为typed `unavailable`，不会吞掉已经
  成功投影的recent turns。无同次captured head的retry/exception状态不得声称`exact`。

## 输入清洗与停止

设置 `DEEPSEEK_BASE_URL` 和 `DEEPSEEK_API_KEY` 后，可在主模型调用前进行短消息最小纠错。失败会回退
原文；成功结果进入durable Observation。

Stop 在Recap lifecycle成功前取消pre-dispatch工作；成功后只通过stream observer停止provider，避免
取消Prepared/Started持久化步骤。只有known failed turn被exact abandon成功后，Host才承诺该轮未进入
active history。每个账号同一时刻只有一个SessionJournal engine driver：per-session `TurnLock`覆盖所有
durable read/write；active turn期间current、recent、busy、SSE与stop只读in-memory live state或上述cache，
不接触Engine。idle current在取得gate后才检查durable recovery。SSE订阅与stop endpoint不占writer lock。

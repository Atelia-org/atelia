# Galatea Server API

本文档是 Galatea first-party browser 与 server 共同使用的 HTTP/SSE V1 参考。部署、认证与连接配置见[配置参考](configuration.md)，进程、会话与后台 Agent 行为见[运行时参考](runtime.md)，文档入口见[Galatea 文档索引](README.md)。实现入口是 [`Program.cs`](../../prototypes/Galatea/Program.cs)，DTO 定义见 [`GalateaConfig.cs`](../../prototypes/Galatea/GalateaConfig.cs)。

## 认证与通用约定

`/api/v1` 全部要求 `galatea_player_auth` cookie。未认证 API 请求返回 401 `{code:"authentication-required",error}`；浏览器页面则跳转 `/login`。登录由 `POST /login` 的 form fields `playerId`、`password` 建立 HttpOnly、SameSite=Lax cookie；不要把密码写进脚本、shell history或本文档。

登录票据只标识 Player；每次验证要求该 Player 仍在配置中。旧 cookie/旧 user claim 不继续认证。当前所有 Player 都是管理员，可以访问任意角色，但请求必须显式指向角色；`players: []` 时不能登录，后台角色仍可运行。

最简单的人工调用方式是在已经登录的 Galatea 页面 DevTools Console 中使用 same-origin cookie：

```js
const me = await fetch("/api/v1/me", {
  credentials: "same-origin",
  cache: "no-store",
}).then(async response => ({ status: response.status, body: await response.json() }));
console.log(me);
const characters = await fetch("/api/v1/characters", {credentials: "same-origin"}).then(r => r.json());
console.log(characters);
const characterId = "alice"; // 改为上述目录中明确选择的 characterId。
const segment = encodeURIComponent(characterId).replace(/[!'()*]/g,
  value => `%${value.charCodeAt(0).toString(16).toUpperCase()}`);
const apiBase = `/api/v1/characters/${segment}`;
```

所有 `/api/v1` route 都是 versioned route；旧 `/api/*` 及省略 Character 的旧 `/api/v1/chat/...` 等路由没有 alias、redirect 或 compatibility route。GET 观察接口不会因为“只读”而绕过认证。Maintenance mode 禁止标记为写操作的 POST，返回 503；只读 GET 保持可用，但各接口自己的 attach/read 语义仍然适用。

带 JSON body 的 endpoint 只接受 `application/json` 与可选 UTF-8 charset，不接受 `Content-Encoding`。JSON 必须使用 exact camelCase；unknown、wrong-case、duplicate、missing required、wrong type、required null、comment 和 trailing comma 均会被拒绝。request body 上限为 1 MiB。

matched V1 endpoint 的 failure 只有 `turn-busy` 使用 `{code,error,turnId}`，其余使用 `{code,error}`，包括 `recent-view-busy` 与 `recap-cadence-progress-busy`。unknown 或 retired route 保持 exact 404，但不承诺 endpoint-owned envelope。`error` 诊断文本不属于 machine branch contract；调用方应按 status 与 `code` 分支。

## Endpoint 总表

| Method | Path | 成功响应与作用 |
|:--|:--|:--|
| GET | `/api/v1/me` | 200 `{playerId,name,maintenanceMode}`；当前访问者 |
| GET | `/api/v1/characters` | 200 `[{characterId,name}]`；配置中的角色目录，不 attach session |
| GET | `/api/v1/characters/{characterId}/recent-turns` | 200；latest 6 completed turns、同 head Context header、rewind token 与 RecapGrid readiness |
| GET | `/api/v1/characters/{characterId}/recap-cadence-progress` | 200；独立 Timeline/Cadence HistoryLoad telemetry |
| GET | `/api/v1/characters/{characterId}/mailbox/status` | 200；delegation store 的只读聚合状态 |
| GET | `/api/v1/characters/{characterId}/agent/status` | 200；server Agent loop 的只读状态 |
| POST | `/api/v1/characters/{characterId}/agent/retry-admission` | strict `{}`；200 Agent 状态，或 409 busy/具体未完成原因；不创建主线轮次 |
| POST | `/api/v1/characters/{characterId}/agent/resume-autonomy` | strict `{}`；仅在失败暂停且会话 Idle 时重新计时；200 Agent 状态或 409 |
| GET | `/api/v1/characters/{characterId}/chat/turns/current` | 200；current/recovery 状态 |
| POST | `/api/v1/characters/{characterId}/chat/turns` | 202 `{turnId}`；接纳 fresh player turn |
| POST | `/api/v1/characters/{characterId}/chat/turns/resume` | 202 `{turnId}`；在 exact recovery head 恢复 |
| POST | `/api/v1/characters/{characterId}/mailbox/inbound` | 202 `{turnId,messageId}`；接纳 inbound mail turn |
| POST | `/api/v1/characters/{characterId}/mailbox/ready-turn` | strict `{}` one-shot；200 状态或 202 `{turnId,origin}` |
| POST | `/api/v1/characters/{characterId}/chat/turns/pop-latest` | 200 `{poppedUserText}`；按 rewind token 取出最近一轮 |
| POST | `/api/v1/characters/{characterId}/chat/turns/{turnId}/stop` | 204 empty |
| POST | `/api/v1/characters/{characterId}/chat/turns/pending/stop` | exact-head 安全结束，没有 provider 调用 |
| GET | `/api/v1/characters/{characterId}/chat/turns/{turnId}/events` | 200 `text/event-stream`；SSE V1 stream |

两个全局 GET 之外，表内其余 13 条 route 都属于 `/characters/{characterId}`。未知角色在 attach/session mutation 前返回
404 `character-not-found`；角色 ID 作为一个 URL segment 编码，不能直接拼接原始斜杠，也不能重复解码字面 `%2F`。
登录后的 `/` 是角色目录，`/characters/{characterId}` 是该角色页面；切换角色使用整页导航。

`turnId`、rewind token 和恢复 head 必须属于同一目标角色。管理员权限允许对其他角色操作，但不能把甲角色
的 turnId 放进乙角色的 stop/events 路径，或把甲的 token/head 当成乙的证据。网页的诊断连接只作用于下一次人工发送，不写入浏览器存储；切换角色不会迁移草稿。

## Turn mutation 请求

Fresh player turn：

```json
{"message":"向北走。","diagnosticConnectionId":null}
```

`message` required；`diagnosticConnectionId` optional，省略或 null 时使用目标 Character 的进程内 `runtimeConnectionOverrideId`，若没有则使用 `defaultConnectionId`。显式诊断 ID 必须精确命中目标 Character 的 `connectionOptions[].connectionId`，只影响本次新回合；original 与 normalized message 各最多 64 KiB UTF-8，连接 ID 最多 128 UTF-8 bytes。202 只表示已接纳；随后订阅返回的 `turnId` 对应 SSE 才能观察 terminal。response-loss 后只能查询 current/recent reconciliation，不得自动重发 mutation。

Resume：

```json
{
  "expectedHead":"canonical-event-address-from-current",
  "diagnosticConnectionId":null
}
```

`expectedHead` required，必须逐字使用 current response 的 `recoveryHead`。`diagnosticConnectionId` optional；它只在 `NewRequestRequired` 的既有 model/surface 约束内选择连接。`FrozenCompletionRequired` 使用 Prepared 的原连接，显式诊断 ID 会返回 `diagnostic-connection-not-applicable`。恢复不读取新回合的进程内 override；没有人工 uncertain-restart 开关。重复计算可能重复计费，但不会重放已提交 Action 的工具。

Inbound mail：

```json
{
  "from":"Codex",
  "body":"邮件正文",
  "subject":"可选主题",
  "diagnosticConnectionId":null
}
```

`from`、`body` required；`subject`、`diagnosticConnectionId` optional。连接优先级与 Fresh player turn 相同。caller 不能提交 `to`，server 固定 `To=session.Character.CharacterName` 并生成 canonical 32-lowerhex `messageId`。body 最多 64 KiB UTF-8，from 最多 1 KiB，subject 最多 4 KiB；from/subject 拒绝 CR、LF、NEL、Unicode line separator 等换行。来信内容是故事数据，不取得指令权限。Runtime 记录已认证 Player 的 sender/injectedBy；`from` 只是信内自称署名，即使为 `Codex` 也不冒充经过核实的 Codex 来源。

Ready-turn 是任意 configured Character 的 Dev one-shot：

```js
const result = await fetch(`${apiBase}/mailbox/ready-turn`, {
  method: "POST",
  credentials: "same-origin",
  headers: { "Content-Type": "application/json" },
  body: "{}",
}).then(async response => ({ status: response.status, body: await response.json() }));
console.log(result);
```

body 必须是 strict `{}`，不能带 `diagnosticConnectionId`、player text 或其他字段。它不强制越过 cadence：启动时返回 202 `{turnId,origin}`，其中 `origin` 为 `delegate-reply|heartbeat-activation`；等待或暂停返回 200 `{state,nextActivationAtUnixTimeMilliseconds,lastActivationAtUnixTimeMilliseconds,code}`；busy、recovery 或失败阻断返回 409。正 interval 角色可在 Empty 后继续检查 cadence；`autonomyIntervalMinutes:0` 角色只在 durable Ready notice/active reply lease 的纯读 wake evidence 存在时 attach，并且即使 cutoff 变 Empty 也只能返回状态，绝不创建 `heartbeat-activation`。它与后台 loop 复用同一 coordinator，不是后台 loop 的启动条件。

Undo/pop-latest body 为：

```json
{"rewindLatestToken":"exact-token-from-recent-turns"}
```

token 必须来自最新 `recent-turns` 并逐字回传。recent operation 共享最多 4,096 次 physical header preview visit 与 16 MiB cumulative decoded logical payload，最终 production JSON 最多 4 MiB。pop display source 最多 256 KiB UTF-8，exact receipt 最多 2 MiB；receipt 在 CAS 前预编码。response-loss 时刷新 current/recent，不得盲目重试 mutation。

recent/cadence 查询与后台检查也短暂持有会话锁。没有已发布 live turn 时，pop 最多等待 1 秒取得锁，再检查 exact head；等待期间 head 改变仍返回 `409 rewind-not-available`。已有 live turn 或等待超时返回 `409 turn-busy`，请求取消则取消等待，不自动重发。

Stop 没有 request body。`turnId` 必须使用接纳响应或 current 返回的 canonical id；成功返回 204，未知或已经完成返回 404 `turn-not-found`。

204 只确认停止请求已接受；`terminated` terminal 或后续 recent 才证明安全结束已经落盘。
没有 live turn 的 blocked 任务使用 `pending/stop`，body 为 `{"expectedHead":"exact-head"}`；
维护模式、活动写者、head 已变或工具批次未闭合时拒绝。结束保留输入与已执行工具，不是 Undo。

## 只读状态与 browser 读取策略

后台输入整理/Note/邮件提取可能独立于主轮进行。`GET /api/v1/characters/{characterId}/agent/admission`
返回 `{operationId,state}`，state 为 idle/running/stopping，idle 的 operationId 为 null。
`POST /api/v1/characters/{characterId}/agent/admission/{operationId}/stop` 按精确 id 请求停止本次整理，
202 只表示接受；它不等待 TurnLock，也不创建 Observation/TurnEnded。旧 id 不得取消后继操作。
实际调用退出和资源清理后才释放写权；已提交 Action 和待结算提取目标保留，后续 pulse 可继续原目标。
session attach 本身不执行 provider 整理调用，关机信号同时取消并清理这些非主轮操作。

`GET /api/v1/characters/{characterId}/chat/turns/current` 返回 exact object：

```text
{status,turnId,connectionId,recoveryHead}
```

| `status` | 字段约束 |
|:--|:--|
| `idle` / `unprovisioned` | `turnId`、`connectionId`、`recoveryHead` 均为 null |
| `running` | `turnId` 与 `connectionId` 同时为 null（接纳尚未发布）或同时有值；有值时 turnId 为 32-lowerhex。`recoveryHead=null` |
| `recovery-required` | `turnId`、`connectionId` 均为 null；`recoveryHead` 为非空 exact head |

这些字段始终存在；`running` 尚无 turnId 时继续查询 current，不能猜测 SSE 地址。该 GET 也会先通过 `GetSessionAsync` attach session，服从其 provisioning 策略。

`GET /api/v1/characters/{characterId}/agent/status` 返回 exact object：

```text
{state,defaultConnectionId,runtimeConnectionOverrideId,effectiveConnectionId,nextActivationAtUnixTimeMilliseconds,lastActivationAtUnixTimeMilliseconds,code,admissionFailure}
```

它不 attach、不 reconcile、不领取 lease、不调用 provider、不等待长 turn。state 为 `disabled|starting|waiting|autonomy-paused|blocked|running|maintenance|stopping`。`effectiveConnectionId` 是下一次**新**回合不带诊断 ID 时的选择：进程内 `runtimeConnectionOverrideId ?? defaultConnectionId`；绑定启用时，成功回合末状态识别可自动设置该角色的运行时值，没有对外设置 API。进程重启即清空，成功 Undo 也清除该角色的运行时选择。它不表示当前运行或待恢复回合的连接；已发布的运行回合以 `/chat/turns/current.connectionId` 为准。细节见[状态驱动的连接选择](character-connection-state-design.md)。时间字段只作诊断。`waiting` 且 `nextActivationAtUnixTimeMilliseconds=null` 表示该 Character 的 interval 为 `0`：没有自主 deadline、仍监视 durable reply；正 interval 的 waiting 才有 deadline。`disabled` 是保留内部投影，正常 API 的未知 Character 会先返回 404。blocked 的 `code` 解释阻断原因。`admissionFailure`为nullable `{code,error}`，在`AUTOMATIC_ADMISSION_FAILED`时可补充具体处理失败；没有细节时为null，不能据此推断未发生失败。它只提供受限的错误类别与说明，不返回provider正文或任意异常消息。响应带 `Cache-Control: no-store`。

`POST /api/v1/characters/{characterId}/agent/retry-admission`接受strict `{}`，遵守同一认证、JSON与Maintenance写操作guard；维护模式返回503。它尝试立即取得目标角色`TurnLock`，只重试旧的未完成admission处理，不创建角色轮次、不领取新轮次的cutoff。旧Action尚未capture时，此操作可能调用extractor provider；已capture内容沿原有恢复流程处理。

`POST /api/v1/characters/{characterId}/agent/resume-autonomy`接受strict `{}`，只对已 attach 且处于 `autonomy-paused/AUTONOMOUS_TURN_FAILED` 的角色生效。它在 `TurnLock` 内确认没有运行中的回合、admission 操作、自动工作阻塞或待恢复 Journal 轮次，随后从当前时间重新计算自主活动 deadline；不创建轮次、不调用 provider、不修改 Journal。成功返回200 Agent 状态；忙碌、未暂停或需要恢复时返回409。Maintenance 写操作 guard 同样适用。

成功返回200及上述Agent状态；没有admission失败时只返回当前状态。200不承诺已经开始自主活动，也不清除独立的reply失败暂停。interval 为 `0` 且无 durable wake evidence 时它不 attach；有 evidence 时也只 reconciliation/settlement，绝不领取新 cutoff 或创建主线回合。已有运行/恢复需求仍须按其入口处理，不能借此跳过runtime recovery。忙碌返回409 `{code:"turn-busy",error,turnId}`；提取、存储、会话未就绪或恢复未完成返回409 `{code,error}`，保留阻断和具体原因。它不把失败写成零Note结果，也不在失败后推进角色head。示例：

```js
const retry = await fetch(`${apiBase}/agent/retry-admission`, {
  method: "POST",
  credentials: "same-origin",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({}),
}).then(async response => ({ status: response.status, body: await response.json() }));
console.log(retry);
```

`GET /api/v1/characters/{characterId}/mailbox/status` 返回：

```text
{state,queuedCount,readyNoticeCount,attemptCount,code,nextRetryAtUnixTimeMilliseconds}
```

state 为 `no-mail|queued|active-running|backoff|accepted-history-unavailable|ready-reply|quarantined|unavailable`。它只读 supervisor 已持有的 delegation store：不调用 `GetSessionAsync`、不 attach session、不 signal pulse，也不触发 extractor、transport 或 provider；不会返回正文或 message/dispatch/thread/turn identity。它在单个 SQLite read transaction 中聚合状态，响应带 `Cache-Control: no-store`。

`attemptCount` 表示当前邮件的连续恢复失败次数，绑定、启动前失败和结果检查共用上限 8；不是模型调用次数。backoff/accepted-history-unavailable 表示有限恢复；耗尽后生成正常失败回信并继续队列。`nextRetryAtUnixTimeMilliseconds` 是显示与重开调度提示，不承诺绝对墙钟执行时间。结果不明的失败回信不表示旧工作已经停止。JSON 字段不变。

First-party browser 的读取是有条件串联，而不是三个接口无条件固定轮询：

- mailbox status 使用独立 single-in-flight、递归 `setTimeout` 的约 5 秒 poller；它只观察，不驱动 server 的 10 秒 automatic pulse。
- Agent follower 每轮先 GET agent status；仅在 UI 不 busy 时继续 GET current。
- current 为 `running` 且有 turnId 时 attach SSE；current 为 `idle` 时才继续 GET recent。
- recap cadence progress 在成功加载/刷新 recent 后独立 best-effort 读取，也会在已确认的 terminal/Undo reconciliation 边界刷新；失败保留上一稳定显示。

每个 response 都以页面 generation 与本地 mutation revision fencing，慢 GET 不能覆盖更新的 send、rewind 或 SSE 结果。页面打开不会自动 resume；SSE transport EOF 也不能被当作成功。

current/recent 读取失败不会覆盖同次轮询已经成功读取的 Agent status。状态读取失败时，页面区分 `HTTP_<status>`、`INVALID_CONTENT_TYPE`、`INVALID_JSON`、`INVALID_RESPONSE` 和兜底 `STATUS_READ_FAILED`；浏览器 console 记录失败的请求或字段校验原因。服务端 API 5xx 异常记录在 `Galatea.Api` 日志中，排查时先区分请求失败与响应校验失败。

## Recent turns 与 RecapGrid readiness

成功响应的顶层形状为：

```text
{
  turns: [{userText, assistant: {text, reasoningText} | null, endReason: null | "stopped" | "rejected" | "incomplete"}],
  rewindLatestToken,
  contextHeader: {observation, action},
  recapGridReadiness
}
```

`userText`、`assistant.text`、两个 context header 字段均为 string；`assistant.reasoningText` 是始终存在的 nullable string。`rewindLatestToken` 是 nullable string，null 表示此视图没有可用的撤销 token，不能当作缺字段或据此猜测 head；stale readiness 也会撤掉 token。production response 携带 readiness object。完整 DTO 与嵌套 authority/metrics 见 [`GalateaConfig.cs`](../../prototypes/Galatea/GalateaConfig.cs)，closed browser 校验见 [`galatea.js`](../../prototypes/Galatea/wwwroot/assets/galatea.js)。该 GET 先 attach session，可能按 `create-if-missing` 创建缺失 repository；下述只读承诺属于 attach 后的 inspector。

`RecentTurnsResponseV1` 始终包含 required `contextHeader:{observation:string,action:string}`。当当前 exact RecapGrid candidate 可 materialize 时，两字段分别是 coherent request recipe 实际放在 raw tail 之前的首条 Observation 与 Action 内容，包括 `recap-block` fence。raw-only、未 provision 或 candidate 不可用时 object shape 不变，对应字符串为空。

stale cache 保留上一稳定边界的 header，并由同 response 的 `recapGridReadiness.freshness=stale` 标识，不能冒充当前 raw head。每个 derived block 由后端统一渲染为 `## {SemanticHeading}`、空行和动态长度 `recap-block` fence；browser 不按 `BlockKey` 拼标题。

`recapGridReadiness` 绑定同一 read view 与 recent raw head。它先用 Getter resolve；仅 nonempty active 且 unfulfilled 时调用 Manager 的只读 `InspectBuildProgress`。closed state 完整集合为：

```text
ready | raw-only | reserve-bootstrap-raw-only | frontier |
fulfillment-missing | blocked | no-rows | no-active | invalid |
limited | cancelled | unavailable | stale | busy | unprovisioned
```

response 可携带 `authority`、bounded `metrics`、`orderedMissing`、`code`、`detail` 与 `reserveBootstrap` evidence。`ready` 时同一 Getter handle 按该 raw head 的 governing `derivedContext.nthPrevious` 只读 resolve/materialize `contextHeader`，并在最终 raw-head fence 后与 readiness 一起发布。该读取不 dispatch provider、不 build、不写。

Terminated 轮次保留 `userText`，`assistant` 为 null、`endReason` 为明确原因；此时不存在
`assistant.text` 或 `assistant.reasoningText`，不得合成成功回答。普通 Completed 轮次的
`endReason` 为 null，assistant 子对象沿用上述字符串合同。

## Cadence telemetry

`GET /api/v1/characters/{characterId}/recap-cadence-progress` 返回 exact closed object：

```text
{
  freshness, state, observedRawHead, cadenceBaseline,
  recentHistoryPlanningUnitCount, recentHistoryLoad,
  recapIntervalHistoryLoad, minimumRecentHistoryLoad,
  buildThresholdHistoryLoad, remainingHistoryLoad,
  historyLoadEstimatorId, code, detail
}
```

整条 route 先复用 `GetSessionAsync` 取得 session。因此 `create-if-missing` Character的 missing repository 首次 GET 会先执行既有 first-turn structural SessionJournal/Cadence/Timeline/Control bootstrap；这是 session attach policy，不属于 telemetry inspector 的纯读承诺。attach/bootstrap 后，service inspector non-blocking 获取 `TurnLock`；writer 占用时立即返回 503 `{code:"recap-cadence-progress-busy",error}`，busy 分支不读 Engine、Timeline 或 Cadence。

取得 gate 后，它捕获 current raw head，纯读 Cadence snapshot、selected Timeline head row 以及到 captured head 的 recent raw suffix。raw head 不存在时返回 exact `unprovisioned/raw-head-absent`。从 TurnLock gate 开始的 inspector 不创建 Completion client、Online、Manager 或 Store，不 capture Timeline、不 dispatch provider，也不写 repository/sidecar。

所有 HistoryLoad 字段都是 nullable canonical nonnegative decimal string：`0` 或无前导零十进制；browser 使用 `BigInt` 比较与格式化。`recentHistoryPlanningUnitCount` 是 nullable nonnegative safe integer。closed state 为 `below-target|awaiting-replay-safe-boundary|awaiting-recent-reserve|cadence-ready|limited|unavailable|unprovisioned|stale`，freshness 独立为 `exact|stale`。

`recapIntervalHistoryLoad` 是 B，`minimumRecentHistoryLoad` 是 R。尚未选出 first replay-safe boundary 时，threshold 是 ideal `B+R`；选出 boundary 后，threshold 是 `boundary measured load + R`，已包含 overshoot。`remainingHistoryLoad` 只表示距 cadence threshold 的差，不承诺 Recap build 已开始。

新 exact progress 的 `observedRawHead` 若不同于当前 exact readiness 的 head，browser 降为 `stale/browser-head-mismatch`。HistoryLoad 是 estimator-scoped Timeline cadence 度量，不是 provider/model token 数、完整 prompt 或 context-window 占用。

Cadence progress 不嵌入 `RecentTurnsResponseV1` 或 SSE `done.recent`，所以 recent 与 SSE stable grammar 保持逐字段不变，progress 失败也不会压掉已经成功读取的 turns、Context header 或 rewind token。

## SSE V1 stable protocol

SSE 只接受以下 closed event language：

```text
status          { code, changed? }
reasoning-delta { delta }
text-delta      { delta }
done            { recent: RecentTurnsResponseV1 | null }
error           { code, message }
attempt-start   { attempt, segment }
attempt-reset   { segment }
retry-wait      { attempt, code, nextRetryAtUnixTimeMilliseconds }
terminated      { reason, recent: RecentTurnsResponseV1 | null }
```

`status.code` 为 `generating|normalizing-input|input-normalization-finished|using-tools|transport-unresponsive`；只有 `input-normalization-finished` 携带 required `changed:boolean`。`error.code` 为 `operator-stop|server-shutdown|completion-failed|memo-recall-failed|turn-unavailable|internal-failure`。`memo-recall-failed` 表示记忆召回阶段失败、主模型尚未开始生成；具体异常写入 `Galatea.TurnRunner` 日志。server 与 first-party browser 必须同步更新。

`attempt-reset` 只清除当前未提交生成段，不删除先前已提交的工具/Action 展示；服务端 replay buffer 同步清除失败 partial。
`retry-wait` 为瞬态诊断，不是持久化 attempt。`transport-unresponsive` 表示期限已到、仍等待旧调用退出；不得发起重叠补偿。
`terminated` 是与 done/error 互斥的 terminal，reason 为 stopped/rejected/incomplete；它不表示成功 Action。

frame 使用 strict UTF-8 与 LF：exact 一个 `event:` 行、一个单行 `data:` JSON 和终止空行。id、retry、comment、multi-data 与 CRLF 均不属于 V1 grammar。

nonterminal preview 最多 4 MiB / 16,383 events，terminal reserve 为 5 MiB / 1 event，whole replay 最多 9 MiB / 16,384 events；subscriber channel 容量为 256 frame references。preview cap hit 只进入 internal `PreviewSuppressed` 并丢弃后续 preview，不停止 provider 或改变 durable outcome。browser 在 decode 前限制每 connection 9 MiB、每 raw frame 5 MiB，并使用 fatal UTF-8 decoder。

process-alive nonfatal turn 必须 exactly-one terminal。fatal transport EOF 可能没有 terminal；browser 必须查询 current 并有限重试，不能当作 success。durable completion 后 view 不可用表达为 `done {recent:null}`，typed 原因由独立 HTTP recent 读取。

## Stable authority 与演进边界

早期 HTTP/SSE grammar、bounds、terminal/reconciliation 语义，以及当时 first-party browser 的消费行为，曾由历史 tag `session-journal-contract-r2-approved-surfaces-v1` 批准为 Stable V1。该批准不包含 deployment/provider readiness、diagnostic 逐字文本、login HTML、bootstrap、cache token、cookie 实现或 ignored operator state。

该历史 tag 也不会自动认证后来新增或修改的 API，包括 Character 目标路由、Player cookie、目录与 /me、cadence telemetry、mailbox status、agent status 和 ready-turn 的当前请求/响应。它们的 current closed contract 由本文、当前代码与测试共同定义；不能借旧批准声称后来 delta 也已获认证。没有真实需求前不增加 pagination、cursor、Last-Event-ID、ack 或 dual grammar；breaking change 应形成新 candidate/version。

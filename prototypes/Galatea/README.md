# Galatea

Galatea 是面向真实 SessionJournal repository 的单会话 HTTP host。raw journal 和
selected `RefId` lineage 是会话 authority；RecapGrid Timeline、Control、Store 都是
可验证、可重建的 derived authority。

## 配置

`config.json` 使用单一 strict V2 language，必须包含exact integer `"v": 2`、至少一个user与strict
`recapGrid`：

```json
{
  "v": 2,
  "users": [
    {
      "userId": "alice",
      "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
      "sessionDir": "sessions/alice",
      "sessionProvisioning": "create-if-missing",
      "systemPrompt": "你是家庭局域网里的私人助手。",
      "systemPromptFile": null
    }
  ],
  "listenUrls": ["http://0.0.0.0:3510"],
  "callLogDir": null,
  "maintenanceMode": false,
  "recapGrid": {
    "routeManifestPath": "recap-grid-routes.json",
    "agentControlProfileFiles": ["recap-grid-agent-control-profile.json"],
    "currentAgentControlProfileId": "default"
  }
}
```

writer固定把`v`放在首字段，reader不要求property order。missing version、future version、`null`、string、
`2.0`或`2e0`都拒绝；V1、versionless与future config都没有compatibility reader或自动迁移。升级V1时必须在停服、
备份并确认实际`Galatea:ConfigPath`后，为每个user显式选择`sessionProvisioning`并改为`"v": 2`；应用不会重写
其中的password或其他operator配置。

相对`sessionDir`以`config.json`所在目录为base，loader向runtime只交付absolute path；absolute值保持同一target。
每个user必须选择closed exact policy：`existing-only`只打开已provision的repository；`create-if-missing`允许普通
writable host在首次需要该user session时，仅对完全不存在的`sessionDir`创建first-turn structural raw-only repository。
Galatea在final path同一parent下的unique staging中创建raw SessionJournal、Cadence、empty Timeline与empty Control；
Cadence/Timeline policy由`GalateaFirstTurnBootstrapPolicy`唯一拥有，Control使用exact current AgentControl profile的canonical
Admission，且该Admission必须授权`Create`。candidate经raw Idle/三事件、三域exact empty、Store absent与Getter exact raw-head
`RawHistoryAuthorized`验证，关闭所有handle后才以Linux `renameat2(RENAME_NOREPLACE)`原子create-only发布，并从final path
重新打开。已存在但为空、不完整或invalid的路径不会被provisioning层adopt、reset或rebuild；普通
writable `Open`仍保留SessionJournal owner定义的crash-tail recovery，owner recovery后仍无法打开才fail closed，且不会
fallback到create；existing raw-only或partial repository也不会自动补写三域。maintenance mode只read-only open且不会创建。
该bootstrap不创建Store、asset、Family、Definition、recipe或activation，不读取route且不dispatch provider；它只保证首轮可以
走正式no-active/raw-only lifecycle，不承诺完整RecapGrid activation。crash或candidate Create/Dispose失败可能留下unique
staging residue，normal runtime不会扫描或自动清理历史residue。这里没有process-CWD fallback、existence-based双解释或
repository move；`..`与
absolute path仍是合法的lexical path，这项规则不承诺把session限制在config目录内，也不声称提供额外的no-follow
filesystem边界。

Current product contract见[root config V2](../../docs/SessionJournal/current/contracts/galatea-root-config-v2.md)。
[Root config V1 appendix](../../docs/SessionJournal/current/contracts/galatea-root-config-v1.md)仍保留其当时获批准并由
`session-journal-contract-r2-approved-surfaces-v2`锚定的历史事实；该旧tag不认证current V2 delta。

`connections.json` 是唯一 Completion endpoint catalog，同时携带 host-level selection
metadata。根必须包含 integer token `"v": 1`、非空 `connections`、exact
`defaultConnectionId`、非空 `selectableConnectionIds` 与 exact `bindings` object。
`selectableConnectionIds` 是有序的 Agent/UI allowlist：每项必须 exact 命中 catalog，
不得重复，且必须包含 `defaultConnectionId`。不在 allowlist 中的 helper/Recap
connection 仍可被内部 exact binding、RecapGrid route 或 frozen recovery 使用，但不会
显示在 browser 中，也不能作为 fresh/current Agent connection 提交。

Galatea 当前要求 `bindings` exact 包含两个兄弟 key：
`"galatea.input-normalizer"` 与 `"galatea.outbound-mail-extractor"`。每个值为
connection ID 时启用对应feature，为 `null` 时显式禁用；不存在、blank、wrong-case、
unknown ID 或多余 binding
都会在 startup fail closed，绝不 fallback 到 `defaultConnectionId`。Normalizer 的
model/provider/surface/endpoint/secret locator 全部来自该 connection，client 只在首次真正
需要清洗时惰性创建；OutboundMailExtractor 同样使用hidden、lazy、borrowed client，且不进入
Agent/UI selectable allowlist。

每个 connection 必须显式提供 `completionSurfaceId`，并在 `baseAddress` /
`baseAddressEnv` 中恰好选择一个，在 `apiKey` / `apiKeyEnv` 中至多选择一个。
Numeric V1 现在通用地允许 optional `selectableConnectionIds` / `bindings`；Galatea 对两者
做上述 required 收紧。当前 binary 仍可读取没有这两个字段的通用 V1，但扩展后的
Galatea 文件会被旧 closed-root binary 拒绝；operator 必须停服、备份并将 code 与
manifest 配套发布，应用不会自动改写可能含 secret 的文件。

`.atelia/galatea/delegates.json` 是独立于 Completion catalog 的 required、
machine-local Codex 代行配置。V1 是 closed schema，并且当前只允许一条 exact、
case-sensitive route：`recipient: "Codex"` / `kind: "codex-app-server"`。示意结构如下：

```json
{
  "v": 1,
  "sidecar": {
    "nodeCommand": "/canonical/path/to/node",
    "entryPoint": "/canonical/path/to/galatea-sidecar.js",
    "codexCommand": "/canonical/path/to/codex.js",
    "rpcTimeoutMs": 30000,
    "turnTimeoutMs": 1200000,
    "shutdownGraceMs": 5000,
    "maximumFrameUtf8Bytes": 1048576
  },
  "allowedRoots": ["/repos/focus/atelia"],
  "routes": [{
    "recipient": "Codex",
    "kind": "codex-app-server",
    "cwd": "/repos/focus/atelia",
    "mode": "work",
    "network": false,
    "maximumQueuedMails": 128,
    "maximumTaskUtf8Bytes": 100000,
    "maximumReplyUtf8Bytes": 100000,
    "maximumInboxReplies": 128,
    "maximumInboxUtf8Bytes": 4194304
  }]
}
```

所有路径都必须是Linux absolute、existing、canonical realpath；配置路径自身或任一
ancestor含symlink会被拒绝。`nodeCommand`与`codexCommand`还必须是executable regular
file。特别是常见的`.../bin/codex`安装入口本身是symlink时，operator必须显式填写其
canonical resolved target，loader不会悄悄follow。`cwd`也必须canonical且落在至少一个
`allowedRoots`内。unknown/missing/wrong-case/duplicate（包括case变体）、额外route、
非法mode/range都会在startup fail closed。task/reply上限按strict UTF-8 bytes计数，且
必须在JSON最坏六倍escaping加code-owned envelope reserve后仍装入
`maximumFrameUtf8Bytes`；inbox总bytes必须至少容纳一条最大reply或4 KiB delivery failure。
Bootstrap会同时生成一份
带`REPLACE_WITH_...`的明显placeholder模板并停止，绝不猜测本机可执行文件或权限边界。
programmatic `GalateaConfig`也走同一套path/executable/range/frame/containment校验；sidecar
持有canonical immutable snapshot，caller之后修改原始list不会改变生效policy。

`GalateaHostService`拥有一个host-wide、lazy `GalateaCodexSidecarClient`。首个真实dispatch
前保持零进程；每个`UserSessionHost`拥有独立的内存coordinator、ledger、ReplyInbox和一个
fixed Codex thread binding，创建或登录session不会启动child。transport
启动`nodeCommand entryPoint`并通过environment注入code-owned allowed roots、cwd、Codex
command、mode、network及timeout/body/frame bounds，邮件正文只能进入JSONL `task`字段，
不能覆盖任何route policy。V1 input是exact
`{v,type:"dispatch",requestId,dispatchId,threadId?,task}`；成功accept后返回稳定
`dispatchId/threadId/turnId`和一个terminal task，terminal只产生bounded exact final或
stable stage/code failure。child继承环境中的全部`CODEX_BRIDGE_*`与`GALATEA_CODEX_*`先被
清除，再由host显式钉死；其中`CODEX_BRIDGE_CODEX_ARGS`固定为app-server stdio并关闭继承的
MCP/apps，ambient process environment不能改写route capability。C# write gate、单个完整
frame write、flush与acceptance各受`rpcTimeoutMs`约束：write开始前取消可安全放弃；从frame
可能写入pipe开始，timeout/cancel/IO都映射stable outcome-unknown、fail当前generation且绝不
自动retry。attached duplicate waiter自身取消或等待超时不会伤害原owner exchange。
Node sidecar自身向C# stdout写frame使用独立、code-owned 10000ms deadline；它不继承最长可到
300000ms的JSON-RPC `rpcTimeoutMs`，并始终落在Node wire允许的100..60000ms范围内。

active exact同dispatch会在C# generation内coalesce；一旦frame可能写出，dispatchId就进入
client-lifetime、最多4096项的fail-closed tombstone，正常terminal或outcome-unknown后即使换
generation也不得重发。同ID携带不同thread/task直接拒绝；容量耗尽后拒绝所有新ID而不evict
旧tombstone。sidecar的同值4096项tombstone上限也由host显式注入。request-level protocol
rejection只结束对应尚未accepted的request，不会终结另一个已accepted business exchange。

stdout只有一个bounded strict-UTF8 reader；malformed、oversize、unknown、重复字段、错误
correlation或process exit会protocol-fatal当前generation，并把所有未决exchange映射为失败，
不会自动retry outcome-unknown操作。stderr被持续drain但内容不进入普通日志。下一请求只可在
旧process完成bounded kill/reap后lazy创建新generation，旧generation事件不能污染新状态。
shutdown严格为sessions -> sidecar（close stdin，bounded wait，必要时kill entire process
tree）-> Completion/RecapGrid owner，且Dispose不等待无界child task。

`GalateaCompletionOwner` 唯一拥有 host-wide `CompletionConnectionRegistry`；main Agent、
input normalizer、outbound mail extractor 与 RecapGrid exact routes 共用其惰性 clients。Completion侧的Shutdown顺序为：drain
sessions/per-turn operation与delegate sidecar，再 drain borrowed RecapGrid runtime，最后清理 distinct Completion
clients。`callLogDir` 由统一 Completion factory decorator 服务上述所有调用；启用
normalizer 时，清洗前输入、prompt 与 provider output 也会进入该本地调用日志。

## Internal TextExtractor

`TextExtractor` 是已被 mailbox specialization 使用的通用 internal、ephemeral结构化提取器；
它自身不拥有HTTP endpoint、SessionJournal integration或任何persistence。构造时固定业务
`systemPrompt` 与 immutable `TextExtractorToolSet`，并注入一个
connection及惰性的borrowed `ICompletionClient` accessor；它不拥有或dispose client。每次调用只提供
`targetText` data与`userPrompt` instruction：

```csharp
TextExtractorToolSet tools = TextExtractorToolSet.Create(
    TextExtractorArtifactTool.Create<PersonArtifact>("artifact_person")
);
var extractor = new TextExtractor(systemPrompt, tools, connection, getClient);
TextExtractionResult result = await extractor.ExtractAsync(
    targetText,
    "提取文本中明确出现的人名。",
    cancellationToken
);
```

V1每次只发起一次Completion，使用`Auto`与parallel tool calls；provider返回的artifact tool calls
就是terminal output，不进入通用tool-result/repair loop。合法结果包含0..N个按Action block顺序执行的
异构`TextExtractionArtifact<T>` typed POCO；artifact列表与顺序被冻结，`T`的immutability由契约类型自身负责。
0 calls表示没有产物，普通正文只保留为bounded diagnostics，
绝不解析为artifact。任一unknown/duplicate/malformed call、schema/DataAnnotations/custom validation失败、
provider invocation/termination/error不匹配都会使整次调用失败，不返回partial result。

每次调用创建独立`ToolSession`与collector，因此同一extractor可并发复用且不会串线；该结果不具备durable
recovery或dedupe语义。工具契约继续由`Completion.Tools/ArtifactToolWrapper<T>`提供，无需修改其core。
如果connection kind exact为`openai-codex-responses`，工具名还必须满足1..64个ASCII字母、数字、下划线或
连字符（例如`artifact_person`），且必须省略当前尚无verified mapping的`MaxTokens`。system/target/instruction、
provider tool name/call ID、tool/call数量、raw arguments与diagnostics均有
code-owned bounds；caller cancellation与transport exception直接传播。

## Mailbox、OutboundMailExtractor 与 Codex coordinator

Codex 回信向下一玩家回合 Observation 的注入仍在施工；当前已决策边界和会逐步收缩的未完成工作见
`docs/Galatea/codex-delegation-refactor-status.md`。

所有新普通player turn（包括当前尚无ready reply的情况）都以runtime-owned composite Observation
持久化。首个兄弟块固定为`## 玩家角色试图采取的行动`/`player-action`，随后可按顺序携带0..16个
`Reply`或`DeliveryFailure`兄弟块；canonical `Codex`成功heading为
`外界代行者 Codex 给 Galatea 的回信`，失败heading为
`Galatea 发给外界代行者 Codex 的信未能送达`。每个块独立使用
`AdaptiveMarkdownFenceRenderer`：tilde fence至少4字符且长于正文内最长连续tilde，正文不trim、
normalize或escape，因此嵌套backtick fence、Markdown、HTML/XML与Unicode可原样呈现给LLM。
reply正文上限256 KiB UTF-8，failure上限4 KiB，整份composite上限1 MiB；越界全部拒绝而不截断。

composite parser只接受code-owned prefix、heading、info string、顺序与动态fence的canonical重渲染结果。
recent view显示玩家文本及每条独立通知；普通Undo仍把它识别为player turn，但pop receipt只返回玩家文本。
历史backtick player envelope继续只读兼容recent/Undo；inbound mail envelope仍不属于普通player Undo。
input normalizer只接收玩家文本，绝不接收ready notices。ReplyInbox已经由per-session coordinator
维护，但尚未接入fresh turn的durable lifecycle，因此当前HTTP新建的普通player turn仍携带空notice集合。

`SessionJournal`公开的`AdaptiveMarkdownFenceRenderer.RenderBlock(infoString, exactBody)`要求1..64字符
ASCII token作为code-owned info string。现有Recap contribution已复用它，并保持原`recap-block`输出逐字不变。

`POST /api/v1/mailbox/inbound` 接受authenticated strict JSON
`{from,body,subject?,connectionId?}`。runtime生成canonical 32-lowerhex `messageId`并固定
`To=Galatea`，以202返回`{turnId,messageId}`，随后沿用普通turn/SSE执行主线模型。Inbound mail
使用code-owned escaped Observation envelope，不经过input normalizer；recent view则显示自然的
发件人、主题与正文。来信正文在prompt中明确只是故事数据，不获得指令权限。该入口共享maintenance、
per-session `TurnLock`、recovery admission与main connection allowlist。

主线terminal Action durable后，host在recent refresh与SSE `done`之前使用
`GalateaVisibleActionTextRenderer`提取可见文本：按顺序连接Text blocks，排除reasoning/tool block，
再整体剥离inline think。常驻`OutboundMailExtractor`通过
`emit_send_mail_intent`产出0..N个有序`SendMailIntent`，字段为故事内`Recipient`、可选`Subject`、
完整`Body`、可选canonical `InReplyToMessageId`与exact `EvidenceQuote`。Recipient仍是未解析、未验证的
故事文本；只有后续coordinator对case-sensitive exact `Codex`的匹配构成当前唯一recipient allowlist，
其余recipient只保留为`Unrouted`候选且绝不调用sidecar。Actor ownership、actual send以及计划、
草稿、他人邮件和来信引用等语义，只由extractor LLM依据code-owned prompt保守判断，并没有被runtime
fail-closed证明。runtime只验证artifact结构与UTF-8 bounds、single-line Recipient/Subject、canonical reply ID，
以及Recipient/非空Subject/Body/reply ID/Evidence均为source exact substring。Subject、reply ID与evidence
只保留extractor provenance，不进入Codex能力参数；sidecar task逐字等于`Body`，cwd/mode/network只来自
code-owned exact route。

每个Action extraction batch由单一authoritative coordinator ledger全有或全无地capture，按terminal
Action head保留fail-closed tombstone。stable dispatch ID是对length-prefixed
`(userId,"Codex",canonical Action head,artifact ordinal)`计算SHA-256后形成的
`gd1-<64-lowerhex>`；候选、tombstone、queue和inbox都有code-owned count/byte上限，容量耗尽时拒绝整批，
不evict旧项而冒险重复执行。route pump按capture顺序与artifact ordinal严格FIFO，同一时刻至多一个
Starting/Running exchange。首封以`threadId=null`创建Codex thread，accepted后绑定authoritative thread ID；
以后每封只能continue该thread。accepted或terminal identity mismatch会产生bounded delivery failure并
quarantine本session route，绝不覆盖binding或继续产生副作用；`START_OUTCOME_UNKNOWN`和其他start/terminal
failure都只形成one-shot failure notice，不透明重试。

合法final必须是strict Unicode、nonblank，且同时满足route reply上限和composite单reply 256 KiB上限；
非法或超限final转为code-owned failure，不truncate冒充回复。ReplyInbox按单调completion sequence保序，
`BeginReadyReplyCutoff(playerText)`在同一gate冻结最早的、最多16条且能与该玩家文本精确渲染进1 MiB
composite的FIFO前缀，其余保持Ready。一次只允许一个active lease；显式`Commit`永久变成`Consumed`，
`Rollback`或未提交lease的`Dispose`恢复Ready。该lease可以跨fresh/recovery调用栈长期持有，供后续runtime
在Observation真正durable后提交，而不是在方法返回时自动消费。

普通player Undo只在SessionJournal exact rewind确实`Moved`后回调同一ledger gate：Unrouted/Queued变为
`RetractedBeforeDispatch`并释放正文；与pump竞争失败而已经Starting/Running的exchange只标记
`SourceRetracted`，不interrupt，完成结果仍可one-shot呈现；Ready/Leased/Consumed不撤回、不重新武装。
已经建立的fixed Codex thread context也不随故事turn Undo而倒退。

该状态仍不是durable outbox，不承诺provider-call exactly-once或进程重启恢复，session dispose后即丢失。
Extraction有独立的code-owned 30秒elapsed deadline，并同时向
provider传递linked shutdown/deadline cancellation；即使provider不合作，recent refresh、SSE `done`和
`TurnLock`释放最多只额外等待该deadline。nonfatal failure/cancellation/timeout只写single-line bounded
`Galatea.Mailbox`摘要log，不改判已经durable的主turn；deadline内观测到的fatal exception仍传播，
超时后被放弃task的eventual fault仅被安全观察。capture完成只唤醒后台pump，主Galatea turn不等待
sidecar accepted或final；mailbox Observation不产生普通player rewind token，commit前也再次防御其被
player pop入口撤销。session shutdown先停止capture并drain turn，再取消/监督coordinator pump；host随后
关闭共享sidecar，最后关闭Completion/RecapGrid owner。Completion call log仍可能包含完整tool arguments，
debug摘要不重复正文、subject或evidence。

历史 Agent Control profiles 必须继续保留，供 Prepared/ToolContinuation 按 frozen
identity 绑定；current profile 只用于新 request。Route manifest 仍在首次
RecapGrid work 时延迟读取，保留 exact per-route `connectionId` 及调度 policy，
没有 wildcard/default fallback。

## 恢复顺序

- Prepared：先 exact bind frozen completion 与 frozen tool identity；不打开 Online 或
  derived stores。
- Started：启动时 strict config/connections 已冻结；默认 Refuse 早于本次 current
  connection selection/client、route 与 derived owner。
- 当前 root strict config language为V2；connections、profile、route各自仍保持owner-defined V1。当前Linux-only
  file loader对这些文件与`systemPromptFile`都执行code-owned byte cap、existing-ancestor no-reparse与final-file
  no-follow regular-file 规则读取；bootstrap 也会在首次写前验证 parent chain。
- ToolContinuation：先 bind frozen tool profile/operation，再 bind current completion，
  最后打开 Online readiness。
- ToolResult NewRequest：使用 current profile，并保留 ToolResult raw tail。

Fresh/NewRequest 才创建 per-turn Online context。生命周期在合法 raw boundary 执行
Timeline reconcile/seal、必要的 Manager build，再由 Getter 产生 coherent candidate。
empty Timeline 或 no-active recipe 走 raw-only，不打开 Store 或 recap provider。

## HTTP V1 stable protocol

Galatea的first-party browser与server一起直接使用`/api/v1`；旧`/api/*`没有alias、redirect或compatibility
route。当前versioned endpoints是：

| Method | Path | Success |
|:--|:--|:--|
| GET | `/api/v1/me` | `{userId,maintenanceMode}` |
| GET | `/api/v1/recent-turns` | latest 6 completed turns、同head Context header、rewind token与RecapGrid readiness |
| POST | `/api/v1/chat/turns` | 202 `{turnId}` |
| POST | `/api/v1/chat/turns/resume` | 202 `{turnId}` |
| POST | `/api/v1/mailbox/inbound` | 202 `{turnId,messageId}` |
| POST | `/api/v1/chat/turns/pop-latest` | `{poppedUserText}` |
| GET | `/api/v1/chat/turns/current` | `status,turnId,connectionId,restartRequired,recoveryHead` |
| POST | `/api/v1/chat/turns/{turnId}/stop` | 204 empty |
| GET | `/api/v1/chat/turns/{turnId}/events` | SSE V1 stream |

JSON body只接受`application/json`与可选UTF-8 charset，不接受`Content-Encoding`；exact camelCase，unknown、
wrong-case、duplicate、missing required、wrong type、required null、comment和trailing comma均拒绝。request body上限
为1 MiB，original与normalized message各为64 KiB UTF-8，mail body为64 KiB、sender为1 KiB、subject为4 KiB；
mail sender/subject与outbound recipient/subject均拒绝CR/LF/NEL/Unicode line separator等换行，
connection id为128 UTF-8 bytes。matched V1 endpoint
failure除busy使用`{code,error,turnId}`外统一为`{code,error}`；unknown或retired route保持exact 404，但不承诺
该endpoint-owned envelope。diagnostic文本不作为machine branch。

recent operation共享最多4,096次physical header preview visit与16 MiB cumulative decoded logical payload，
最终production JSON最多4 MiB。pop的display source最多256 KiB UTF-8，exact receipt最多2 MiB；receipt在CAS前
预编码，response-loss只允许browser做current/recent reconciliation，不能自动重发mutation。

`RecentTurnsResponseV1`始终携带required
`contextHeader:{observation:string,action:string}`。当当前exact RecapGrid candidate可materialize时，两个字段分别是
coherent request recipe实际放在raw tail之前的首条Observation与Action内容（包括`recap-block` fence）；因此browser可直接
展示模型看到的Recap正文及各标题声明的语义范围。raw-only、未provision或当前candidate不可用时仍返回同一object shape，但对应字符串为空。
stale cache保留上一稳定边界的header，并由同一response中的`recapGridReadiness.freshness=stale`标识；它不冒充当前raw head。
每个derived block都由后端统一渲染为`## {SemanticHeading}`加空行，再接动态长度的`recap-block`围栏；Galatea V5 asset为
World Understanding与Autobiography分别声明稳定的中文语义标题，browser不根据`BlockKey`自行拼接标题。

## SSE V1 stable protocol

SSE只接受下列closed event language：

```text
status          { code, changed? }
reasoning-delta { delta }
text-delta      { delta }
done            { recent: RecentTurnsResponseV1 | null }
error           { code, message }
```

`status.code`为`generating|normalizing-input|input-normalization-finished|using-tools`；只有
`input-normalization-finished`携带required `changed:boolean`。`error.code`为
`operator-stop|server-shutdown|completion-failed|turn-unavailable|internal-failure`。frame使用strict UTF-8与LF：
exact一个`event:`行、一个单行`data:` JSON和终止空行；id、retry、comment、multi-data与CRLF均不是V1 grammar。

nonterminal preview最多4 MiB / 16,383 events，terminal reserve为5 MiB / 1 event，whole replay最多9 MiB /
16,384 events；subscriber channel容量为256 frame references。preview cap hit只进入internal
`PreviewSuppressed`并丢弃后续preview，不停止provider或改变durable outcome。browser在decode前限制每connection
9 MiB、每raw frame 5 MiB，并使用fatal UTF-8 decoder。process-alive nonfatal turn必须exactly-one terminal；
fatal transport EOF可能没有terminal，browser必须查询current并有限重试，绝不能当success。durable completion后的
view不可用表达为`done {recent:null}`，typed原因由独立HTTP recent读取。

这些HTTP/SSE grammar、bounds、terminal/reconciliation语义，以及tracked first-party browser对它们的消费行为，
已由`session-journal-contract-r2-approved-surfaces-v1`批准为Stable V1。该批准不包含deployment/provider readiness、
diagnostic逐字文本、login HTML、bootstrap、cache token、cookie实现或ignored operator state。没有真实需求前不增加pagination、cursor、
Last-Event-ID、ack或dual grammar；breaking change必须形成新candidate/version。

## Readiness

`GET /api/v1/recent-turns` 返回 `recapGridReadiness`。它绑定同一 read view 与 recent raw
head：先用 Getter resolve；仅 nonempty active 且 unfulfilled 时调用 Manager 的只读
`InspectBuildProgress`。状态为 `ready`、`frontier`、`blocked`、`no-rows`、`no-active`、
`unprovisioned`、`busy`、`stale` 或 `invalid`，并携带可证明的 Timeline/Control/Store/
recipe/row authority 与 bounded metrics。`ready`时同一Getter handle还会按该raw head的governing
`derivedContext.nthPrevious`只读resolve/materialize `contextHeader`，并在最终raw-head fence后与readiness一起发布；
该读取不dispatch provider、不build、不写。

Galatea只在上述unpublished missing-session candidate中自动创建first-turn structural Cadence/Timeline/Control；不为existing
repository补写，也不自动创建Store、provision asset、compose recipe或activate。需要完整RecapGrid时，operator 应先使用
SessionJournal.Cli 的
`recap-grid scaffold`生成strict admission/profile/route files，再用`recap-grid init`、
`recap-grid control provision-asset --asset galatea-rolling-rewrite-zh-cn-v5`、
Control compose/put-recipe/activate 与 build 命令完成显式配置。该asset提供一个shared Family下的
`world-understanding`与`autobiography`两列；实际connection/model只来自route/connections配置，不进入durable semantic identity。
Galatea 是会话内角色，不等于 provider Assistant；两列使用 Observation/Action 只是为了选择 provider carrier。
主流程中的 provider Action 是 TRPG GM 的复合回复，可能含 `[Galatea]`、`[旁白]` 与 `[状态摘要]`，只有显式
`[Galatea]` 第一人称内容是 Galatea 自身体验的直接证据。
当前两条exact target分别是
`Observation / galatea.world-understanding / galatea.world-understanding Galatea积累的世界理解：`与
`Action / galatea.first-person-autobiography / galatea.first-person-autobiography Galatea积累的第一人称自传：`。
两列Definition将provider-facing `SemanticHeading`与carrier/`BlockKey`成套定义；heading进入Definition v2 digest，但不参与
context contribution的routing identity/order，且不进入冻结的maintainer input `atelia.recap.input.v1`。
scaffold不会构造provider、
Timeline、Control或Store；Galatea仍只消费其strict canonical outputs。

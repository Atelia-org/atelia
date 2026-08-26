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

Galatea 当前要求 `bindings` exact 只有一个 key：
`"galatea.input-normalizer"`。其值为 connection ID 时启用 input normalization，为
`null` 时显式禁用；不存在、blank、wrong-case、unknown ID 或多余 binding
都会在 startup fail closed，绝不 fallback 到 `defaultConnectionId`。Normalizer 的
model/provider/surface/endpoint/secret locator 全部来自该 connection，client 只在首次真正
需要清洗时惰性创建。

每个 connection 必须显式提供 `completionSurfaceId`，并在 `baseAddress` /
`baseAddressEnv` 中恰好选择一个，在 `apiKey` / `apiKeyEnv` 中至多选择一个。
Numeric V1 现在通用地允许 optional `selectableConnectionIds` / `bindings`；Galatea 对两者
做上述 required 收紧。当前 binary 仍可读取没有这两个字段的通用 V1，但扩展后的
Galatea 文件会被旧 closed-root binary 拒绝；operator 必须停服、备份并将 code 与
manifest 配套发布，应用不会自动改写可能含 secret 的文件。

`GalateaCompletionOwner` 唯一拥有 host-wide `CompletionConnectionRegistry`；main Agent、
input normalizer 与 RecapGrid exact routes 共用其惰性 clients。Shutdown 顺序为：drain
sessions/per-turn operation，再 drain borrowed RecapGrid runtime，最后清理 distinct Completion
clients。`callLogDir` 由统一 Completion factory decorator 服务上述所有调用；启用
normalizer 时，清洗前输入、prompt 与 provider output 也会进入该本地调用日志。

## Internal TextExtractor

`TextExtractor` 是尚未接入 HTTP、SessionJournal 或 RecapGrid 主链的 internal、ephemeral
结构化提取器。构造时固定业务 `systemPrompt` 与 immutable `TextExtractorToolSet`，并注入一个
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
| POST | `/api/v1/chat/turns/pop-latest` | `{poppedUserText}` |
| GET | `/api/v1/chat/turns/current` | `status,turnId,connectionId,restartRequired,recoveryHead` |
| POST | `/api/v1/chat/turns/{turnId}/stop` | 204 empty |
| GET | `/api/v1/chat/turns/{turnId}/events` | SSE V1 stream |

JSON body只接受`application/json`与可选UTF-8 charset，不接受`Content-Encoding`；exact camelCase，unknown、
wrong-case、duplicate、missing required、wrong type、required null、comment和trailing comma均拒绝。request body上限
为1 MiB，original与normalized message各为64 KiB UTF-8，connection id为128 UTF-8 bytes。matched V1 endpoint
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

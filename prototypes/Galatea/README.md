# Galatea

Galatea 是面向真实 SessionJournal repository 的单会话 HTTP host。raw journal 和
selected `RefId` lineage 是会话 authority；RecapGrid Timeline、Control、Store 都是
可验证、可重建的 derived authority。

## 配置

`config.json` 使用单一 strict V4 language，必须包含exact integer `"v": 4`、至少一个user与strict
`recapGrid`：

```json
{
  "v": 4,
  "users": [
    {
      "userId": "alice",
      "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
      "characterName": "Alice",
      "playerName": "Alex",
      "sessionDir": "sessions/alice",
      "delegationStateDir": "delegation-state/alice",
      "sessionProvisioning": "create-if-missing",
      "systemPromptTemplate": "",
      "systemPromptTemplateFile": "prompts/trpg-host-standard-zh-cn.md"
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
`4.0`或`4e0`都拒绝；V1/V2/V3、versionless与future config都没有compatibility reader或自动迁移。升级V3时必须在
停服、备份并确认实际`Galatea:ConfigPath`后，为每个user显式配置`characterName`与
`playerName`，把prompt source改成使用exact `${characterName}` / `${playerName}`变量的template，
并改为`"v": 4`；应用不会重写其中的password或其他operator配置。本轮仍保持V4：
项目未发布，且player-name delta与character-name delta属于同一批未运行的开发迁移。

`characterName`与`playerName`都是required、already-trimmed Unicode NFC label，按strict UTF-8限制为
1..128 bytes；它们分别表示GM扮演的主要NPC与故事内玩家角色，都不从login `userId`推导。两者拒绝控制/
换行字符及除U+200D ZWJ外的Unicode Format，且必须至少含一个non-Format rune。`[`/`]`/`$`/`{`/`}`与reserved
marker `旁白`/`状态摘要`/`角色名`也拒绝；ZWJ emoji仍合法。Template language只有
case-sensitive `${characterName}`与`${playerName}`；source至少出现一次character token，player token可选，
其他或残缺`${...}`拒绝，replacement使用ordinal、one-pass、non-recursive语义。两个name都不承担
代词、别名或persona生成。Inline `systemPromptTemplate`保持exact空白；有效
`systemPromptTemplateFile`仍以config directory为base、覆盖inline，并在strict UTF-8 decode后先
`Trim()`再render。Runtime保留两个validated name与finalized `SystemPrompt`，不会在每个turn重读template file。

[`docs/Galatea/prompt/trpg-host.md`](../../docs/Galatea/prompt/trpg-host.md)是embedded、code-owned的标准TRPG
source template，只预设`${characterName}`与`${playerName}`的名字/好友关系，不包含特定Player
的年龄、性别、职业、家庭、历史或昵称。Bootstrap在`systemPromptTemplateFile`指向
config directory内的missing path时，会创建缺失parent并以create-new写入该exact resource，然后
fail-stop要求operator检查后重启。Existing file永不覆盖；config root外的missing target也不自动创建。

相对`sessionDir`与`delegationStateDir`都以`config.json`所在目录为base，loader向runtime只交付canonical absolute
path；absolute值保持同一target。`delegationStateDir`没有fallback，也不会从`sessionDir`推导；所有user的
delegation state paths必须exact unique、互不嵌套，并与所有session paths及optional `callLogDir`双向non-nested。
existing delegation path components不得是symlink/reparse point。当前hard-cut只建立并验证这个Galatea-owned
storage boundary。durable supervisor现已接入production composition：host启动时先eager classify每个user；只有
`delegationStateDir`与对应`sessionDir`都存在时才strict-open store并取得process-lifetime exclusive OS lock。
state存在但session缺失时以`SESSION_MISSING` fail closed，不打开SQLite/lock；state路径不存在时先保持
`Uninitialized`，直到对应writable SessionJournal首次成功打开或provision后，才在该exact target创建baseline。目录内的权威文件是
`delegation-state.sqlite3`与`delegation-state.lock`。baseline固定记录当时的physical append frontier、selected
raw head、exact user/session identity、route policy fingerprint与容量上限；frontier之前的历史Action永不补做
extraction。existing store的schema、owner、route policy、limits、integrity或lock任一不匹配都会使该user fail
closed，不会adopt、reset、迁移或换用内存路径。baseline创建失败留下的目录也保留供检查；修复后必须重启。
当前binary已经删除旧process-local owner，因此baseline写入前后都没有回退到旧owner或删除candidate后继续运行的
产品分支。
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

Current product contract见[root config V4](../../docs/SessionJournal/current/contracts/galatea-root-config-v4.md)。
[Root config V3](../../docs/SessionJournal/current/contracts/galatea-root-config-v3.md)、
[Root config V2](../../docs/SessionJournal/current/contracts/galatea-root-config-v2.md)与
[Root config V1 appendix](../../docs/SessionJournal/current/contracts/galatea-root-config-v1.md)仍保留其当时获批准并由
`session-journal-contract-r2-approved-surfaces-v2`锚定的历史事实；该旧tag不认证V2/V3/V4 delta。

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
machine-local Codex 代行配置。V2 是 closed schema，并且当前只允许一条 exact、
case-sensitive route：`recipient: "Codex"` / `kind: "codex-app-server"`。示意结构如下：

```json
{
  "v": 2,
  "sidecar": {
    "nodeCommand": "/canonical/path/to/node",
    "entryPoint": "/canonical/path/to/local-codex-mcp/dist/src/galatea-durable-sidecar.js",
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

`GalateaHostService`拥有一个host-wide `GalateaDelegationSupervisor`、一个共享且lazy的
`GalateaCodexDurableSidecarClient`以及每个user独立的SQLite store/driver。所有其他fallible composition
preflight都先完成，最后才构造supervisor，因为existing writable outbox可能从构造成功后立即被pulse。
首个真实`ensure-binding`、`start-turn`或`inspect-dispatch`之前仍保持零Node/Codex child；登录或只打开
session不会凭内存状态重新建立队列。signal只是低延迟提示，bounded capacity-1 channel会合并它们；每秒
fallback pulse总会重读SQLite。每个user至多一个pulse在途，每次pulse至多一个external call，不同user可
并行；进程内task、signal和cache都不是恢复authority。maintenance host只read-only打开existing store，
不attach writable SessionJournal、不启动scheduler，也不执行任何durable transport call；missing state目录
保持`Uninitialized`，maintenance不会创建baseline。

transport启动`nodeCommand entryPoint`并通过environment注入code-owned allowed roots、cwd、Codex
command、mode、local command network、built-in tool policy及timeout/body/frame bounds。邮件正文只能进入
JSONL `task`字段，不能覆盖route policy。现行wire是strict bounded JSONL V2，三个input分别为
`ensure-binding`、`start-turn`与`inspect-dispatch`；对应结果为`binding-established`、`turn-accepted`与
`dispatch-inspected(not-found|running|completed|failed|ambiguous)`。`turn-accepted`只持久化稳定
`dispatchId/threadId/turnId`，不等待final；之后只用read-only inspect读取app-server persistent state。
`ensure-binding`只允许`thread/start + thread/name/set + ownership/cwd verify`，绝不携带邮件正文或执行
`turn/start`。只有`Bound(threadId)`已经durable后，第一封和后续邮件才能`Queued -> Started`并调用
`start-turn`。

child继承环境中的全部`CODEX_BRIDGE_*`与`GALATEA_CODEX_*`先被清除，再由host显式钉死；其中
`CODEX_BRIDGE_CODEX_ARGS`固定为app-server stdio并关闭继承的MCP/apps。host在Node启动前精确清除
`CODEX_SESSION_ID`、`CODEX_THREAD_ID`、`CODEX_INTERNAL_ORIGINATOR_OVERRIDE`、
`CODEX_PERMISSION_PROFILE`、`CODEX_CI`，Node启动app-server时再次清除同一组父Codex context；
`HOME`、`PATH`、`CODEX_HOME`以及auth/provider/proxy环境保持不变。ambient environment不能改写route
capability或把代行thread附着到父Codex session。fixed thread ownership只依赖response ID、
profile-specific exact name marker、canonical cwd/path policy与Galatea持久route identity；
`threadSource`/`source`都只是analytics，不参与authorization。

V2将本地命令出网与Codex内建工具解耦：`localCommandNetwork`只控制turn
`sandboxPolicy.networkAccess`；`tools.webSearch`逐turn映射到Codex top-level
`web_search`（`disabled|cached|indexed|live`），`imageGeneration`与`viewImage`分别映射到
`features.image_generation`和`tools.view_image`。当前开发实例使用`true + live + true + true`，
因此代行者可使用OpenAI hosted Web Search、Image Generation和本地图像查看，sandboxed shell命令也可按需访问外网。
Codex app-server当前provider capability probe返回`webSearch=true`、`imageGeneration=true`、
`namespaceTools=true`；Apps/MCP仍由`mcp_servers={}`与`features.apps=false`显式关闭，避免继承宿主个人工具。
Browser/Computer Use依赖客户端/图形宿主，不属于本次headless sidecar承诺的工具集合。

`Queued -> Started`与冻结operation/thread/policy、占用route active dispatch在一个SQLite transaction
完成，commit后才允许`start-turn`。从此任何timeout、cancel、EOF、process death或protocol loss都进入
durable `OutcomeUnknown`；host crash后遗留`Started`也先零external-call转为`OutcomeUnknown`。这两种状态
都不得回到Queued或重发task，只能按持久1/2/4/...秒bounded backoff执行`inspect-dispatch`。not-found或
暂时unavailable继续等待；exact running持久Accepted；exact terminal在同一事务写Reply/DeliveryFailure
notice并释放route active dispatch；ownership/cwd/body/multiple/identity冲突使route/mail durable
Quarantined。binding outcome unknown可以使用同一binding operation重试，因为该阶段保证没有邮件turn。

C# client只在`start-turn` frame可能写出时登记最多4096个client-lifetime dispatch tombstones；相同ID
不因换generation重发，容量耗尽fail closed。stdout由一个bounded strict-UTF8 reader拥有；malformed、
oversize、unknown、duplicate property、错误correlation或process exit会使当前generation失败，绝不把
outcome unknown透明重试。stderr持续drain但不进入普通业务日志。下一操作只可在旧process完成bounded
kill/reap后lazy创建新generation；若无法确认reap，client稳定fault为
`shutdown/SIDECAR_REAP_UNCONFIRMED`并禁止重启child。

ApplicationStopping先通知supervisor停止新pulse/signal；host disposal逐个drain/dispose session，再等待
timer/consumer/in-flight pulse，随后dispose共享sidecar transport、关闭每个SQLite store并释放lifetime lock，
最后关闭Completion/RecapGrid owner。任何阶段的nonfatal cleanup failure都会被保留并在最终以single或
aggregate exception诚实返回，不把未确认的child或lock cleanup报告成成功。

### Development Codex delegation observability

开发期可在repo root用Debug build启动，并显式打开server-side进度日志：

```bash
ATELIA_DEBUG_CATEGORIES='Galatea.Mailbox,Galatea.TextExtractor,Galatea.Delegation,Galatea.Delegation.Supervisor,Galatea.DelegateSidecar' \
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

`Galatea.Mailbox`显示Action可见文本进入extractor及其intent数量；`Galatea.TextExtractor`显示
pre-response transient transport failure的attempt与退避时间；`Galatea.Delegation`显示durable
binding、dispatch、reconciliation、terminal与backoff；`Galatea.Delegation.Supervisor`显示store availability、
pulse fail-closed与shutdown；`Galatea.DelegateSidecar`显示Node child启动、ready与stable transport failure。
`Info`调用在Release被编译掉；Debug下无论console category是否
打开，仍按`DebugUtil`规则写入`.atelia/debug-logs/galatea.mailbox.log`、
`galatea.delegation.log`、`galatea.delegation.supervisor.log`与`galatea.delegatesidecar.log`
（若该目录不可写则使用既有fallback）。这些
progress log只包含bounded identifier/recipient summary、count、byte size、boolean、stage/code和
process id，不重复邮件正文、subject、evidence、Codex final或sidecar stderr。

当前Node hop是实现边界而不是产品语义要求。未来可以让C# host直接spawn并通过stdio驱动Codex
app-server，从而移除一层process/protocol；这项简化需要等价接管strict framing与bounds、RPC
correlation/notification projection、fixed-thread ownership、environment scrubbing、outcome-unknown
fencing以及bounded kill/reap。SQLite store/driver与durable reply lease的产品契约不应因此改变。

### Gated real Codex V2 transport canary

2026-08-27通过的real app-server canary验证的是已经删除的process-local/V1 owner，只保留为历史
证据，不能证明当前SQLite/V2产品链。Hard cut已删除旧V1 canary实现/runbook、C# V1
coordinator/sidecar以及Node V1 entry；任何current V2 live test都必须重新建立自己的exact契约与证据，
不能复用旧结论。

当前`GalateaCodexDelegationLiveTests.DurableV2_EnsureStartInspectCompletesInCleanRepo`是显式opt-in的
real app-server V2 transport canary；默认test discovery在读取配置、创建临时目录或启动sidecar之前skip。
运行前构建Node sidecar并确认ignored machine-local config中的exact Codex executable已登录：

```bash
npm --prefix local-codex-mcp run build
export ATELIA_GALATEA_CODEX_DELEGATES_CONFIG="$(realpath prototypes/Galatea/.atelia/galatea/delegates.json)"
codex_command="$(jq -r '.sidecar.codexCommand' "$ATELIA_GALATEA_CODEX_DELEGATES_CONFIG")"
"$codex_command" login status
export ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE=1
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName=Atelia.Galatea.Server.Tests.GalateaCodexDelegationLiveTests.DurableV2_EnsureStartInspectCompletesInCleanRepo'
```

2026-08-28当前build按该gate真实PASS 1/1，业务段约9秒：`ensure-binding`先建立empty owned thread，
pre-start `inspect-dispatch`返回NotFound，随后exact一次unique `start-turn`；同dispatch重发被C#本地
tombstone以`DUPLICATE_DISPATCH_ID`拒绝而未写第二个frame，最终只经inspect读到Completed且final exact
匹配随机token。隔离临时repository保持clean、顶层仅`.git`后删除，sidecar/app-server无测试残留进程。
Canary把route重建为research、local command network=false、全部hosted tools disabled，并把唯一allowed root/cwd
钉在随机临时Git repository；它仍需要app-server连接provider/auth的网络。

该canary只证明当前C#/Node V2 transport、fixed-thread ownership、一次start fencing与real app-server
ensure/start/inspect链；它不构造Galatea host/SQLite baseline，不验证accepted后C# host restart、双信FIFO或
durable reply lease。Codex保存的thread/turn及随机token是外部持久状态；测试不把本地临时目录清理冒充外部
history已删除。完整durable real-provider vertical仍可作为独立future operational verification，即使通过也不
构成app-server/provider exactly-once承诺。

同日唯一ignored开发实例的`cyber` session另完成了不调用provider的production smoke：第一次writable attach
自动发布SQLite baseline；HTTP login为302，recent为200/6 turns；停服后writer lock释放且SQLite
`quick_check=ok`；冷重启strict-open existing store后recent仍为200/6。全过程没有启动sidecar/app-server child、
没有发LLM请求，也没有留下认证临时文件或测试进程。该smoke证明当前machine-local path/config上的baseline、
lock与cold reopen，不证明Codex dispatch/reply链。

`GalateaCompletionOwner` 唯一拥有 host-wide `CompletionConnectionRegistry`；main Agent、
input normalizer、per-user outbound mail extractors 与 RecapGrid exact routes 共用其惰性 clients。每个
user 的 extractor 在 supervisor 启动前以该 user 的 exact `characterName` eager 构造，但不会因此创建
独立 connection registry/provider client。Host先按上文顺序
drain sessions与delegation supervisor，再由Completion owner drain borrowed RecapGrid runtime并清理distinct
Completion clients。`callLogDir` 由统一 Completion factory decorator 服务上述所有调用；启用
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

## Mailbox、OutboundMailExtractor 与 durable Codex delegation

Codex delegation现已hard-cut到SQLite-backed durable owner；本节及对应代码/测试是现行产品契约。
阶段实现记录与real-provider证据边界见
[`docs/Galatea/codex-delegation-refactor-status.md`](../../docs/Galatea/codex-delegation-refactor-status.md)。

所有新普通player turn（包括当前尚无ready reply的情况）都以runtime-owned composite Observation
持久化。首个兄弟块固定为`## 玩家角色试图采取的行动`/`player-action`，随后可按顺序携带0..16个
`Reply`或`DeliveryFailure`兄弟块；canonical `Codex`成功heading为
`来自外界代行者 Codex 的回信`，失败heading为
`发往外界代行者 Codex 的信未能送达`。只读 parser 还严格接受旧
`外界代行者 Codex 给 Galatea 的回信` / `Galatea 发给外界代行者 Codex 的信未能送达`
dialect；同一 envelope 不得混用新旧 headings，无 notice 的共同形状保持不变。每个块独立使用
`AdaptiveMarkdownFenceRenderer`：tilde fence至少4字符且长于正文内最长连续tilde，正文不trim、
normalize或escape，因此嵌套backtick fence、Markdown、HTML/XML与Unicode可原样呈现给LLM。
reply正文上限256 KiB UTF-8，failure上限4 KiB，整份composite上限1 MiB；越界全部拒绝而不截断。

composite parser只接受code-owned prefix、heading、info string、顺序与动态fence的canonical重渲染结果。
recent view显示玩家文本及每条独立通知；普通Undo仍把它识别为player turn，但pop receipt只返回玩家文本。
历史Galatea heading与backtick player envelope继续只读兼容recent/Undo；inbound mail envelope仍不属于普通player Undo。
input normalizer只接收玩家文本，绝不接收ready notices。普通player入口持有per-session `TurnLock`，先结算
既有durable reply lease和latest post-baseline extraction gap，再验证recovery boundary与main connection；
随后在HTTP 202之前完成normalization。Caller cancellation、fatal、显式`GalateaTurnException`或input-limit
failure会阻止放弃旧failed turn、创建cutoff和接受新turn；普通nonfatal normalizer exception则fail-open使用
原始玩家文本并继续admission。取得exact effective text后才revalidate/abandon允许放弃的旧failed turn，并以SQLite transaction建立
`CutoffFrozen` lease。cutoff之前已经Ready的bounded FIFO前缀冻结进本轮typed fresh input，之后才ready的
结果留给下一次普通player turn；未选项保持原FIFO次序。选择前缀时为任意合法64 KiB normalized player text
的最坏adaptive-fence渲染预留空间。inbound与recovery入口都不开始新cutoff。

`SessionJournal`公开的`AdaptiveMarkdownFenceRenderer.RenderBlock(infoString, exactBody)`要求1..64字符
ASCII token作为code-owned info string。现有Recap contribution已复用它，并保持原`recap-block`输出逐字不变。

`POST /api/v1/mailbox/inbound` 接受authenticated strict JSON
`{from,body,subject?,connectionId?}`。runtime生成canonical 32-lowerhex `messageId`并固定
`To=session.User.CharacterName`，以202返回`{turnId,messageId}`，随后沿用普通turn/SSE执行主线模型。HTTP caller
不能自报`to`；`MailboxMessage`自身冻结validated To，parser从XML读取并用同一writer exact round-trip，
所以既有`to="Galatea"`与其他合法角色名都不依赖current config即可读取。Inbound mail
使用code-owned escaped Observation envelope，不经过input normalizer；recent view则显示自然的
发件人、主题与正文。来信正文在prompt中明确只是故事数据，不获得指令权限。该入口共享maintenance、
per-session `TurnLock`、recovery admission与main connection allowlist。

主线terminal Action durable并回到`Idle`后，host先用SessionJournal exact raw evidence结算当前reply lease，
再在recent refresh与SSE `done`之前使用
`GalateaVisibleActionTextRenderer`提取可见文本：按顺序连接Text blocks，排除reasoning/tool block，
再整体剥离inline think。每个user的immutable `OutboundMailExtractor`通过
`emit_send_mail_intent`产出0..N个有序`SendMailIntent`，字段为故事内`Recipient`、可选`Subject`、
完整`Body`、可选canonical `InReplyToMessageId`与exact `EvidenceQuote`。Recipient仍是未解析、未验证的
故事文本；只有durable capture对case-sensitive exact `Codex`的匹配构成当前唯一recipient allowlist，
其余recipient持久为terminal `Unrouted`且绝不调用sidecar。Actor ownership、actual send以及计划、
草稿、他人邮件和来信引用等语义，只由extractor LLM依据code-owned prompt保守判断，并没有被runtime
fail-closed证明。runtime只验证artifact结构与UTF-8 bounds、single-line Recipient/Subject、canonical reply ID；
它有意不对Recipient、Subject、Body、reply ID或Evidence做raw Action substring、Markdown、whitespace、标点或
其他机械source-grounding比较。actual send、actor ownership、recipient与正文语义由配置的extractor LLM
承担；Evidence只保留extractor provenance，不是runtime authority。Subject、reply ID与evidence不进入Codex
能力参数；sidecar task逐字等于结构验证后的`Body`，cwd/mode/local command network/built-in tools只来自code-owned exact route。

Extractor system/user source prompt使用shared `${characterName}` renderer并锁定exact
`[${characterName}]` voice marker；tool schema wording保持角色中立。实例`ContractId`为
`atelia.galatea.outbound-mail-extractor.v2.<64-lowerhex>`，fingerprint覆盖code-owned semantic/visible-renderer/
tool contract版本与exact rendered system/user prompts，不包含provider/model/connection。相同名字与contract
产生相同ID，不同名字分离；底层Completion client仍由host registry按connection惰性共享。

每个Action extraction batch由`GalateaDelegationSqliteStore`单事务全有或全无地capture；成功的0-intent
extraction也写`action_capture` tombstone，extractor failure绝不能冒充空结果。stable dispatch ID是对length-prefixed
`(userId,"Codex",canonical Action head,artifact ordinal)`计算SHA-256后形成的
`gd1-<64-lowerhex>`；candidate/outbox/inbox都有code-owned count/byte上限，容量耗尽时拒绝整批，不evict
旧项而冒险重复执行。capture前必须重新确认exact terminal Action仍是current selected head；capture commit后
SessionJournal Undo不删除、不retract、不重新武装该batch。正常串行admission下崩溃gap至多一个，下一次
player/inbound admission或session attach会在允许新turn前结算latest post-baseline terminal Action；baseline
frontier之前的历史和rewind留下的orphan均不补做。重复结算以first durable capture为authority，只验证原Action
bytes/digest，不因后来升级extractor contract而改写历史产物。

durable driver按capture顺序与artifact ordinal严格FIFO，同一route至多一个active dispatch。它先以独立
`ensure-binding`建立并持久一个empty owned thread；所有邮件（包括首封）都只能在exact `Bound(threadId)`后
`Queued -> Started`。每个dispatch最多调用一次`start-turn`；一旦Started，任何不确定结果都只进入
`OutcomeUnknown`并read-only inspect，不回Queued、不重发task。Codex app-server提供可持久读取的owned
thread/turn history，Galatea用它做保守reconciliation；这不等于app-server/provider承诺exactly-once。

合法final必须是strict Unicode、nonblank，且同时满足route reply上限和composite单reply 256 KiB上限；
非法或超限final转为code-owned failure，不truncate冒充回复。SQLite `reply_notice`按单调
`completionSequence`保序并持久`Ready|Leased|Consumed`。普通player cutoff在一个事务中冻结最多16条及
player text，此时`CutoffFrozen`还没有SessionJournal base/body；desired setup完成后、紧邻`SendAsync`前才以
exact selected head和canonical rendered Observation执行`BindObservationBase`。Observation/Action可能已经durable
但上层尚未返回时，恢复只用selected raw lineage中的exact base、Observation bytes/digest和terminal Action
分类：无effect证据才rollback到Ready，terminal Action exact成立才consume，并把同一receiving Action address
写入每条notice。证据分叉则durable quarantine，不能靠函数返回值或exception文本猜测。

Delegation SQLite schema仍为V1且没有新增renderer/version列。`CutoffFrozen`没有rendered Observation，重启时
按既有合同rollback；`ObservationBound|ObservationCommitted`已经冻结exact Observation bytes/byte count/SHA-256。
Store reopen先用closed current/legacy dialect parser验证stored bytes，再逐项核对player text与notice
kind/order/body，不用current writer把历史heading重渲染成新heading。

每个user至多一个active lease。recovery只继承已持久lease，不claim后来Ready的notice；inbound turn也不claim。
lease settlement发生在outbound extraction之前，因此已经接收回信的terminal Action即使后处理失败也不会重新
投递同一notice。普通player Undo只移动SessionJournal selected lineage：已capture的outbox继续推进，active
Codex turn不interrupt，Ready保持Ready，Consumed永不重新武装，fixed Codex thread context也不倒退。这里没有
旧`RetractedBeforeDispatch`/`SourceRetracted`内存状态。

该SQLite current-state machine可跨Galatea/sidecar重启恢复，但只承诺同一dispatch **at-most-one
`start-turn` attempt** 与`OutcomeUnknown`保守只读reconciliation；不承诺provider-call exactly-once。
TextExtractor只对`OpenAICodexResponsesException`的exact
`TransportOutcomeUnknown`做最多5次总尝试，重试前依次等待1s、2s、4s、8s；HTTP status failure、
SSE/protocol failure与普通异常不重试。每个logical extraction复用同一request/client，artifact tool只在
最终成功响应后执行；因为pre-response outcome仍可能已经消耗provider算力，重试可能产生重复计费，但不会
重复本地artifact副作用。Extraction不再设置code-owned elapsed deadline，只服从caller cancellation；若
provider持续不结束且caller不取消，当前turn、recent refresh、SSE `done`与`TurnLock`会继续等待，这是当前
有意选择的完成优先语义。failure/cancellation不写empty tombstone；主Action仍已durable，当前SSE以错误结束，
下一次admission会在接受新turn前重试该exact extraction gap。capture commit只signal supervisor，主Galatea turn
不等待sidecar accepted或final。Completion call log仍可能包含完整tool arguments，debug摘要不重复正文、
subject或evidence。

历史 Agent Control profiles 必须继续保留，供 Prepared/ToolContinuation 按 frozen
identity 绑定；fresh/NewRequest 不再绑定 current profile，也不向新的模型请求注入
`recap_grid_control`。配置中的 current profile 仅提供 missing-session structural bootstrap
所需的 admission authority。Route manifest 仍在首次
RecapGrid work 时延迟读取，保留 exact per-route `connectionId` 及调度 policy，
没有 wildcard/default fallback。

## 恢复顺序

- Prepared：先 exact bind frozen completion 与 frozen tool identity；不打开 Online 或
  derived stores。
- Started：启动时 strict config/connections 已冻结；默认 Refuse 早于本次 current
  connection selection/client、route 与 derived owner。
- 当前 root strict config language为V4；connections与profile保持owner-defined V1，delegate route为owner-defined V2。当前Linux-only
  file loader对这些文件与`systemPromptTemplateFile`都执行code-owned byte cap、existing-ancestor no-reparse与final-file
  no-follow regular-file 规则读取；bootstrap 也会在首次写前验证 parent chain。
- ToolContinuation：先 bind frozen tool profile/operation，再以无工具的 current completion
  继续，最后打开 Online readiness。
- ToolResult NewRequest：不绑定 current tool profile，并保留 ToolResult raw tail。

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
每个derived block都由后端统一渲染为`## {SemanticHeading}`加空行，再接动态长度的`recap-block`围栏；
当前parameterized V6 asset按per-user角色名与玩家名物化两份member prompt，并按角色名为
World Understanding与Autobiography分别声明中文语义标题，
browser不根据`BlockKey`自行拼接标题。参数为`Galatea`时，标题及其canonical Definition identity与历史V5 exact相同。

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
`recap-grid control provision-asset --asset galatea-rolling-rewrite-zh-cn-v6 --character-name <角色名> --player-name <玩家名>`、
Control compose/put-recipe/activate 与 build 命令完成显式配置。该asset提供一个shared Family下的
`world-understanding`与`autobiography`两列；实际connection/model只来自route/connections配置，不进入durable semantic identity。
`--character-name`与`--player-name`必须分别与目标user的validated names完全相同；它们在
canonical bundle构造前展开两个member prompt，角色名另外进入topic与semantic heading。角色不等于
provider Assistant；两列使用 Observation/Action 只是为了选择 provider carrier。
主流程中的 provider Action 是 TRPG GM 的复合回复，可能含 `[<角色名>]`、`[旁白]` 与 `[状态摘要]`，只有显式
`[<角色名>]` 第一人称内容是角色自身体验的直接证据。
当前两条target的carrier与`BlockKey`固定，heading分别为
`Observation / galatea.world-understanding / galatea.world-understanding <角色名>积累的世界理解：`与
`Action / galatea.first-person-autobiography / galatea.first-person-autobiography <角色名>积累的第一人称自传：`。
两列Definition将provider-facing `SemanticHeading`与carrier/`BlockKey`成套定义；heading进入Definition v2 digest，但不参与
context contribution的routing identity/order，且不进入冻结的maintainer input `atelia.recap.input.v1`。
`Galatea` + `刘世超`参数会精确复现旧V5的Family、两个Definition与registration command digest；
任一name变化都保持Family与routing keys，但会产生新的Definition/command identity。scaffold与provision
必须使用同一对names；existing-session character/player rename都不由该命令承诺。
Host composition会为每个user从validated `characterName` + `playerName`构造一次V6 bundle，并只缓存其ordered
`BuildTargetDigest` expectation。fresh admission、后台fresh send与`OpenFreshAsync`都在任何current
Recap/main completion或SessionJournal setup write前，用Galatea-owned typed inspector核对active recipe target；
Control/Timeline absent及no-active继续允许raw-only，wrong-name、mixed、reordered或缺列target统一fail closed为
`character-asset-mismatch`。该稳定code现在表示角色名或玩家名与active asset不一致。NewRequest走同一
current gate；FrozenCompletionRequired完全按frozen identity恢复；
ToolContinuationRequired先把frozen tool结算到durable ToolResult boundary，再核对current target且仅在通过后打开Online。
recent/readiness复用同一inspector，并在mismatch时返回exact `state=invalid`、
`code=character-asset-mismatch`与空`contextHeader`。首个release不支持existing-session character/player rename；operator必须停服后
显式迁移/重建并切换active recipe，不能只改config。
scaffold不会构造provider、
Timeline、Control或Store；Galatea仍只消费其strict canonical outputs。

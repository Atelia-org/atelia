# Galatea

Galatea 是面向真实 SessionJournal repository 的单会话 HTTP host。raw journal 和
selected `RefId` lineage 是会话 authority；RecapGrid Timeline、Control、Store 都是
可验证、可重建的 derived authority。

## 配置

`config.json` 使用单一 strict V6 language，必须包含exact integer `"v": 6`、至少一个user与strict
`recapGrid`：

```json
{
  "v": 6,
  "users": [
    {
      "userId": "alice",
      "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
      "characterName": "Alice",
      "playerName": "Alex",
      "sessionDir": "sessions/alice",
      "delegationStateDir": "delegation-state/alice",
      "characterMemoryStateDir": "character-memory/alice",
      "sessionProvisioning": "create-if-missing",
      "characterContextTemplate": "",
      "characterContextTemplateFile": "prompts/character-context-standard-zh-cn.md"
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
`6.0`或`6e0`都拒绝；V1–V5、versionless与future config都没有compatibility reader或自动迁移。
V6保留V5的code-owned prompt composition，并新增required `characterMemoryStateDir`。V5升级时必须停服、备份并
确认实际`Galatea:ConfigPath`，将version改为`6`并为每个user选择独立、互不嵌套的character-memory path。
应用不会自动迁移或重写password及其他operator配置；V4及更早完整prompt的拆分要求见历史合同。

`characterName`与`playerName`都是required、already-trimmed Unicode NFC label，按strict UTF-8限制为
1..128 bytes；它们分别表示GM扮演的主要NPC与故事内玩家角色，都不从login `userId`推导。两者拒绝控制/
换行字符及除U+200D ZWJ外的Unicode Format，且必须至少含一个non-Format rune。`[`/`]`/`$`/`{`/`}`与reserved
marker `旁白`/`状态摘要`/`角色名`也拒绝；ZWJ emoji仍合法。Template language只有
case-sensitive `${characterName}`与`${playerName}`；source至少出现一次character token，player token可选，
其他或残缺`${...}`拒绝，replacement使用ordinal、one-pass、non-recursive语义。两个name都不承担
代词、别名或persona生成。Inline `characterContextTemplate`保持exact空白；有效
`characterContextTemplateFile`以config directory为base、覆盖inline，并在strict UTF-8 decode后先
`Trim()`。Context必须nonblank且至少包含一次exact `${characterName}`；它不承载GM、voice、output或mail
协议，也不是security boundary，runtime不会解析其中的Markdown H2来决定权限或行为。Character-context fields
不能移除、替换或重排validated binding所选择的code-owned protocol bytes；但context与protocol处在同一
trusted system message中，operator prose仍可在语义上与协议冲突，这一ownership边界不承诺prompt-level安全隔离。

最终system prompt由code-owned
[`TRPG protocol prefix`](../../docs/Galatea/prompt/trpg-protocol-prefix-zh-cn.md)、operator character context与
universal code-owned
[`mailbox protocol base`](../../docs/Galatea/prompt/trpg-mailbox-protocol-base-zh-cn.md)按exact
`prefix + "\n\n---\n\n" + context + "\n\n---\n\n" + mailboxBase`拼接；仅当validated
`galatea.outbound-mail-extractor` binding非`null`时，再以`"\n\n"`追加code-owned
[`Codex outbound appendix`](../../docs/Galatea/prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md)；仅当validated
`galatea.character-note-extractor` binding非`null`时，再追加code-owned
[`Character Note save appendix`](../../docs/Galatea/prompt/trpg-character-note-save-appendix-zh-cn.md)。
完成组合后才用同一个closed renderer展开名字。Mailbox base与两个appendix物理分离以表达capability boundary；
启用对应binding时按base→outbound→Note顺序向模型呈现简短Quick Start。Heading只用于呈现，两个appendix各自只看
对应validated binding。
每个external/resource source有1 MiB读取上限，拼接后的composite source与final rendered prompt也分别受
1 MiB上限；runtime只保留两个validated names与finalized
`SystemPrompt`，不会在每个turn重读template file。五份tracked resource的ownership见
[`prompt/README.md`](../../docs/Galatea/prompt/README.md)。

Bootstrap在`characterContextTemplateFile`指向config directory内的missing path时，只以create-new写入
[`standard character context`](../../docs/Galatea/prompt/character-context-standard-zh-cn.md)，然后fail-stop要求
operator检查后重启。该starter明确说明较早History由RecapGrid派生为带来源的world-understanding与
first-person-autobiography context、冲突时newer raw History优先；下方自主记忆是独立人工长期记录，未来可由
动态外部记忆接管。Code-owned protocol resources不会复制到operator目录。Existing file永不覆盖；config root外的
missing target也不自动创建。

相对`sessionDir`、`delegationStateDir`与`characterMemoryStateDir`都以`config.json`所在目录为base，loader向
runtime只交付canonical absolute path；absolute值保持同一target。Delegation与Character Memory path都没有fallback
或derived-path规则。所有character-memory paths必须exact unique、互不嵌套，并与所有session/delegation paths及
optional `callLogDir`双向non-nested；total topology validator也由直接构造runtime config的consumer共用。Existing
character-memory path components不得是symlink/reparse point。`characterMemoryStateDir`本身只建立path authority：
Character Note binding为`null`时只完成path/topology/reparse preflight，不create/open/lock/store-validate；maintenance
mode即使binding非`null`也完全不接触Character Memory state；只有binding非`null`的writable session lazy attach才会在
missing path创建baseline store与empty Default MemoPod，或strict-open existing state，并把owner/lock保持到session
dispose。Bootstrap仍不创建character-memory state。Durable delegation supervisor继续在production composition中由host启动时eager classify每个user；只有
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

Current product contract见[root config V6](../../docs/SessionJournal/current/contracts/galatea-root-config-v6.md)。
[Root config V5](../../docs/SessionJournal/current/contracts/galatea-root-config-v5.md)、
[Root config V4](../../docs/SessionJournal/current/contracts/galatea-root-config-v4.md)、
[Root config V3](../../docs/SessionJournal/current/contracts/galatea-root-config-v3.md)、
[Root config V2](../../docs/SessionJournal/current/contracts/galatea-root-config-v2.md)与
[Root config V1 appendix](../../docs/SessionJournal/current/contracts/galatea-root-config-v1.md)仍保留其当时获批准并由
`session-journal-contract-r2-approved-surfaces-v2`锚定的历史事实；该旧tag不认证V2–V6 delta。

`connections.json` 是唯一 Completion endpoint catalog，同时携带 host-level selection
metadata。根必须包含 integer token `"v": 2`、非空 `connections`、exact
`defaultConnectionId`、非空 `selectableConnectionIds` 与 exact `bindings` object。
`selectableConnectionIds` 是有序的 Agent/UI allowlist：每项必须 exact 命中 catalog，
不得重复，且必须包含 `defaultConnectionId`。不在 allowlist 中的 helper/Recap
connection 仍可被内部 exact binding、RecapGrid route 或 frozen recovery 使用，但不会
显示在 browser 中，也不能作为 fresh/current Agent connection 提交。

Galatea 当前要求 `bindings` exact 包含四个兄弟 key：
`"galatea.input-normalizer"`、`"galatea.outbound-mail-extractor"` 与
`"galatea.character-note-extractor"`、`"galatea.memo-recall"`。每个值为
connection ID 时启用对应feature，为 `null` 时显式禁用；不存在、blank、wrong-case、
unknown ID 或多余 binding
都会在 startup fail closed，绝不 fallback 到 `defaultConnectionId`。Normalizer 的
model/provider/surface/endpoint/secret locator 全部来自该 connection，client 只在首次真正
需要清洗时惰性创建；OutboundMailExtractor 同样使用hidden、lazy、borrowed client，且不进入
Agent/UI selectable allowlist。CharacterNoteExtractor也按每个user的exact `CharacterName`构造，借用同一
registry并保持client lazy。DerivedInfo enricher复用同一个Character Note connection/client routing，但每个user拥有
独立实例、prompt与`ContractId`。Memo recall使用独立optional binding；它可以显式指向同一connection ID，但不会
隐式复用Character Note binding。non-null Memo recall要求non-null Character Note binding。Character Note保存路径在successful fresh/recovery完成边界
把提取结果交给Character Memory reconciler；
0结果也写durable tombstone，非0结果幂等保存到每个角色的默认MemoPod。只有当前
post-completion返回`AppliedNow`且final head仍一致时才queue保存回执；admission/restart恢复不补回执。Binding非
`null`时主system prompt追加Character Note保存Quick Start，`null`时完全不出现该能力。
Bootstrap connections template把outbound、Character Note与Memo recall bindings都写为`null`。
这是一次有意的closed-shape hard cut：现有只含三个binding key的`connections.json`会在startup被拒绝。升级时必须
先停服、备份，并显式增加`"galatea.memo-recall": null`；runtime不自动改写可能包含secret的sibling config，也不保留
three-key兼容reader。
Outbound或Character Note binding从`null`切换到non-`null`或反向切换都会改变对应appendix presence，并在下一次
fresh turn自然触发existing exact desired-setup rotation；不引入operator prompt module field。

每个 connection 必须显式提供 `completionSurfaceId`，并在 `baseAddress` /
`baseAddressEnv` 中恰好选择一个，在 `apiKey` / `apiKeyEnv` 中至多选择一个。
Numeric V2 通用地允许 optional `selectableConnectionIds` / `bindings`；Galatea 对两者
做上述 required 收紧，并且不接受caller-selected output cap。V1不会被读取或迁移；operator必须停服、
备份并将code与manifest配套发布，应用不会自动改写可能含secret的文件。

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

delegated Codex turn有意不设置elapsed deadline：一旦`start-turn`被接受，Galatea不会仅因
时间流逝而自动`interrupt`仍在工作的LLM会话。`rpcTimeoutMs`只限制单次sidecar/app-server
控制RPC的等待，不是turn lifetime；`shutdownGraceMs`只限制sidecar关服已经开始后等待child
process退出或kill后reap确认的时长，也不是运行中turn的deadline。

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
command、mode、local command network、built-in tool policy及RPC/body/frame bounds。邮件正文只能进入
JSONL `task`字段，不能覆盖route policy。现行wire是strict bounded JSONL V3，三个input分别为
`ensure-binding`、`start-turn`与`inspect-dispatch`；对应结果为`binding-established`、`turn-accepted`与
`dispatch-inspected(not-found|unavailable|running|completed|failed|ambiguous)`，每个semantic inspection结果
都带`source=live|persistent`。inspect的`expectedTurnId`是required nullable字段：`OutcomeUnknown`只能发null并按
dispatch发现，`Accepted`只能发durable exact turn ID。`turn-accepted`只持久化稳定
`dispatchId/threadId/turnId`，不等待final；之后只用read-only inspect读取same-generation live observation或
app-server official persistent APIs。
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

sidecar profile将本地命令出网与Codex内建工具解耦：`localCommandNetwork`只控制turn
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
都不得回到Queued或重发task，只能按持久1/2/4/...秒bounded backoff执行`inspect-dispatch`。只有
`OutcomeUnknown`零dispatch匹配可返回ordinary not-found；`Accepted` exact turn或identity items尚未出现在
official projection时返回`ACCEPTED_TURN_NOT_VISIBLE`并保持Accepted。两者与暂时transport unavailable都继续
等待；exact running持久Accepted；exact terminal在同一事务写Reply/DeliveryFailure
notice并释放route active dispatch；ownership/cwd/body/multiple/identity冲突使route/mail durable
Quarantined。binding outcome unknown可以使用同一binding operation重试，因为该阶段保证没有邮件turn。
已接受邮件的exact Running观测会清除之前`ACCEPTED_TURN_NOT_VISIBLE`/暂时transport miss留下的
reconcile attempt/code/next-at，因此后续新miss从attempt 1重新计算，SQLite也不会
持续显示已经恢复的旧错误。

`inspect-dispatch`先以metadata-only `thread/read`核对exact thread/name/cwd，再在同一app-server generation内
检查一份bounded、non-durable exact live turn observation。该observation只由`turn/start`response及官方
`turn/started`、`item/completed`、`turn/completed`通知建立，保存task digest与bounded identity/final evidence，
不保存raw task、command output或完整turn items；terminal压过late Running，重复冲突fail closed。
process exit/stop/generation replacement会整体清理，`TaskStore` persistent hydrate不能创建live evidence。
live miss后，Accepted通过官方bounded`thread/turns/list`与filtered`thread/items/list`按exact turn检查；
OutcomeUnknown先用bounded thread items发现dispatch，再用turn pages分类。分页要求cursor单调进展、generation
不变且shape/identity/capacity完整；不再使用deprecated full-history hydration。live证据只经正常C# driver与
SQLite terminal CAS发布，不成为第二份durable authority。

当operator已分别核实某个exact `Accepted` turn确实完成、但official projection持续返回
`ACCEPTED_TURN_NOT_VISIBLE`时，可以在停服、锁检查及备份完成后使用Galatea-owned offline
`operator recover-codex-completed`命令。该命令在`WebApplication.CreateBuilder`之前分流，不启动Web host、
Completion provider或sidecar；默认dry-run，只有显式`--apply`才复用production `RecordCompletedMail`事务写入
exact Reply notice并释放active dispatch。证据为strict closed V1 JSON，final只通过evidence file中的canonical
UTF-8 base64传入，不进入argv或普通日志；错误证据和终态冲突在store terminal调用前拒绝。首次apply前会exact
验证task digest；terminal后task body按设计已擦除，因此rerun只按durable dispatch/thread/turn、final与notice
确认`AlreadyApplied`并保持零写，不声称再次独立验证已经不存在的task body。
完整前置条件、schema与操作步骤见
[`docs/Galatea/codex-delegation-operator-recovery.md`](../../docs/Galatea/codex-delegation-operator-recovery.md)。

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
若关服时尚有nonterminal dispatch，它保持durable active并在transport dispose前记录
`preservedForColdRestartReconciliation=true`；关服不为此新增turn elapsed deadline。下次driver
创建会记录reconciliation scheduled，随后仅通过read-only inspect结算final或
`TURN_INTERRUPTED`，不再执行`start-turn`。关服取消与read-only inspect竞态时，cancellation只压过
`NotFound`/exact `Running`等非终态结果；已返回的terminal、identity conflict与fatal transport
evidence仍优先持久化。

### Development runtime observability

开发期可在repo root用Debug build启动，并显式打开server-side进度日志：

```bash
ATELIA_DEBUG_CATEGORIES='Galatea.Mailbox,Galatea.TextExtractor,Galatea.CharacterMemory,Galatea.Delegation,Galatea.Delegation.Supervisor,Galatea.DelegateSidecar' \
dotnet run --project prototypes/Galatea/Galatea.Server.csproj
```

`Galatea.Mailbox`显示Action可见文本进入extractor及其intent数量；`Galatea.TextExtractor`显示
pre-response transient transport failure的attempt与退避时间；`Galatea.Delegation`显示durable
binding、dispatch、reconciliation、terminal与backoff；inspection日志区分`selectorMode`、known turn、
`source=live|persistent|none`、stage/code与recovered/attempt/next-at。Accepted projection不可见使用
`ACCEPTED_TURN_NOT_VISIBLE` Warning并受durable backoff限频；exact Running在首次确认、每约60秒liveness及
miss恢复时记录，不再为每秒fallback pulse刷重复行。terminal failure显式带stage/code。
`Galatea.Delegation.Supervisor`显示store availability、pulse fail-closed、active dispatch的cold-restart保留
与shutdown completion；`Galatea.DelegateSidecar`显示Node child启动、ready、正常stopping/stopped与真实
transport/reap failure，正常dispose不再误报`SIDECAR_DISPOSED` Warning。
`Galatea.CharacterMemory`每批输出identity/hash、Mail/Note outcome、durable memo/queue count与latency的single-line
JSON；只有durable `AppliedNow`结果逐条输出JSON-escaped `PodId`、`MemoId`与`ExactText`，不输出`EvidenceQuote`。
`Info`调用在Release被编译掉；Debug下无论console category是否
打开，仍按`DebugUtil`规则写入`.atelia/debug-logs/galatea.mailbox.log`、
`galatea.charactermemory.log`、`galatea.delegation.log`、`galatea.delegation.supervisor.log`与
`galatea.delegatesidecar.log`
（若该目录不可写则使用既有fallback）。除上述显式Character Note content event外，progress log只包含
bounded identifier/recipient summary、count、
byte size、boolean、stage/code和process id，不重复邮件正文、subject、evidence、Codex final或sidecar stderr。
Character Note debug log与启用`CallLogDir`后可能保存的provider request/tool arguments都包含敏感故事内容；两者
都不是replay、migration或未来Memo apply authority，使用者必须自行管理本地文件访问与保留期。

当前Node hop是实现边界而不是产品语义要求。未来可以让C# host直接spawn并通过stdio驱动Codex
app-server，从而移除一层process/protocol；这项简化需要等价接管strict framing与bounds、RPC
correlation/notification projection、fixed-thread ownership、environment scrubbing、outcome-unknown
fencing以及bounded kill/reap。SQLite store/driver与durable reply lease的产品契约不应因此改变。

### Gated real Codex V3 transport canary

2026-08-27通过的real app-server canary验证的是已经删除的process-local/V1 owner，只保留为历史
证据，不能证明当前SQLite/V3产品链。Hard cut已删除旧V1 canary实现/runbook、C# V1
coordinator/sidecar以及Node V1 entry；任何current V3 live test都必须重新建立自己的exact契约与证据，
不能复用旧结论。

当前`GalateaCodexDelegationLiveTests.DurableV3_EnsureStartInspectCompletesInCleanRepo`是显式opt-in的
real app-server V3 transport canary；默认test discovery在读取配置、创建临时目录或启动sidecar之前skip。
运行前构建Node sidecar并确认ignored machine-local config中的exact Codex executable已登录：

```bash
npm --prefix local-codex-mcp run build
export ATELIA_GALATEA_CODEX_DELEGATES_CONFIG="$(realpath prototypes/Galatea/.atelia/galatea/delegates.json)"
codex_command="$(jq -r '.sidecar.codexCommand' "$ATELIA_GALATEA_CODEX_DELEGATES_CONFIG")"
"$codex_command" login status
export ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE=1
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName=Atelia.Galatea.Server.Tests.GalateaCodexDelegationLiveTests.DurableV3_EnsureStartInspectCompletesInCleanRepo'
```

2026-08-28旧V2 build曾按该gate真实PASS 1/1，业务段约9秒：`ensure-binding`先建立empty owned thread，
pre-start `inspect-dispatch`返回NotFound，随后exact一次unique `start-turn`；同dispatch重发被C#本地
tombstone以`DUPLICATE_DISPATCH_ID`拒绝而未写第二个frame，最终只经inspect读到Completed且final exact
匹配随机token。隔离临时repository保持clean、顶层仅`.git`后删除，sidecar/app-server无测试残留进程。
Canary把route重建为research、local command network=false、全部hosted tools disabled，并把唯一allowed root/cwd
钉在随机临时Git repository；它仍需要app-server连接provider/auth的网络。

该历史canary只证明当时C#/Node V2 transport、fixed-thread ownership、一次start fencing与real app-server
ensure/start/inspect链；当前V3 phase未运行live canary或E2E。canary不构造Galatea host/SQLite baseline，
不验证accepted后C# host restart、双信FIFO或
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
连字符（例如`artifact_person`）。Completion request与connection有意不暴露caller output cap；具体adapter
在省略表示不限量/模型最大值时省略provider字段，否则只发送所选模型的provider-reported maximum。system/target/instruction、
provider tool name/call ID、tool/call数量、raw arguments与diagnostics均有
code-owned bounds；caller cancellation与transport exception直接传播。

`TextExtractor` 与 composite Observation 共同形成的异步双向通讯模式；Mailbox与durable Character Note保存/
save receipt已复用这条路径，后续recall仍可沿同一边界设计，见
[`TextExtractor / Observation Bridge`](../../docs/Galatea/text-extractor-observation-bridge.md)。

## Mailbox、OutboundMailExtractor 与 durable Codex delegation

Codex delegation现已hard-cut到SQLite-backed durable owner；本节及对应代码/测试是现行产品契约。
阶段实现记录与real-provider证据边界见
[`docs/Galatea/codex-delegation-refactor-status.md`](../../docs/Galatea/codex-delegation-refactor-status.md)。

所有新普通player turn（包括当前尚无ready reply的情况）都构造成runtime-owned
`PlayerTurnObservation`，再由`PlayerTurnObservationEnvelope`包装成composite Observation持久化。
runtime在canonical Observation materialization时通过宿主`TimeProvider`只采样一次本地时间，
向下截断到整秒，并在原有prefix之后、player块之前写入code-owned metadata行
`Observation 形成时的外界本地时间（不自动等同于故事世界时间）：yyyy-MM-dd'T'HH:mm:sszzz`；
例如`2026-08-29T14:23:05+08:00`，UTC也固定写`+00:00`而不是`Z`。这只是Observation形成时的
外界粗粒度时间，不是故事世界时间，也不参与turn排序、identity或settlement。首个兄弟块固定为
`## 玩家角色试图采取的行动`/`player-action`，随后可按顺序携带0..32个
角色笔记recall兄弟块，再携带合计0..16个notice：`Reply` / `DeliveryFailure`，以及至多1个且必须位于最后的
`NoteSaveReceipt`（它计入16个总上限且必须最后）。canonical heading/info string为`Note 保存回执` /
`character-note-save-receipt`；旧V0 `Note 请求回执` / `character-note-request-receipt`明确拒绝。recall block使用
`SourceId: ...`单行metadata作为anchor，`RecallType+SourceId`构成exact去重key，当前三种info string为
`memo-gist-recall`、`memo-summary-recall`、`memo-exact-text-recall`；对应heading分别是
`召回的角色笔记（一句话印象）`、`召回的角色笔记（摘要）`、`召回的角色笔记（原文）`。
canonical `Codex`成功heading为
`来自外界代行者 Codex 的回信`，失败heading为
`发往外界代行者 Codex 的信未能送达`。只读 parser 还严格接受旧
`外界代行者 Codex 给 Galatea 的回信` / `Galatea 发给外界代行者 Codex 的信未能送达`
dialect以及既有无timestamp的current/legacy历史；带timestamp的shape只允许current headings，同一 envelope
不得混用新旧 headings；recall block只属于current dialect，不能混入legacy headings。新写入只接受带offset、无小数秒的exact timestamp文本；`Z`、fractional seconds与
其他非canonical变体均拒绝。每个块独立使用
`AdaptiveMarkdownFenceRenderer`：tilde fence至少4字符且长于正文内最长连续tilde，正文不trim、
normalize或escape，因此嵌套backtick fence、Markdown、HTML/XML与Unicode可原样呈现给LLM。
recall `SourceId`上限512 bytes UTF-8，recall正文上限262,677 bytes，reply正文上限256 KiB，
failure上限4 KiB，整份composite上限1 MiB；越界全部拒绝而不截断。

`PlayerTurnObservationEnvelope` parser只接受code-owned prefix、heading、info string、顺序与动态fence的canonical重渲染结果。
recent view显示玩家文本、recall正文及每条独立通知，但隐藏recall anchor metadata；普通Undo仍把它识别为player turn，但pop receipt只返回玩家文本。
历史Galatea heading与backtick player envelope继续只读兼容recent/Undo；inbound mail envelope仍不属于普通player Undo。
input normalizer只接收玩家文本，绝不接收ready notices。普通player入口持有per-session `TurnLock`，先结算
既有durable reply lease和latest post-baseline extraction gap，再验证recovery boundary与main connection；
随后在HTTP 202之前完成normalization。Caller cancellation、fatal、显式`GalateaTurnException`或input-limit
failure会阻止放弃旧failed turn、创建cutoff和接受新turn；普通nonfatal normalizer exception则fail-open使用
原始玩家文本并继续admission。取得exact effective text后才revalidate/abandon允许放弃的旧failed turn，并以SQLite transaction建立
`CutoffFrozen` lease。cutoff之前已经Ready的bounded FIFO前缀冻结进本轮typed fresh input，之后才ready的
结果留给下一次普通player turn；未选项保持原FIFO次序。选择前缀时为任意合法64 KiB normalized player text
以及固定timestamp metadata的最坏adaptive-fence渲染预留空间。inbound与recovery入口都不开始新cutoff；
`GalateaMailboxObservationEnvelope`也不因此增加timestamp。

Galatea侧的internal `IGalateaPlayerTurnRecallProvider`由per-session factory在CharacterMemory lazy attach后构造。
`galatea.memo-recall`为`null`或maintenance mode时使用disabled singleton，并在context selection/barrier构建前直接绕过，
因此disabled路径没有额外CharacterMemory或selector I/O。enabled MVP只在没有active durable reply lease的普通player turn调用provider；有reply lease时暂不注入recall，
避免在未设计lease schema与restart settlement前破坏exact rendered Observation recovery。provider request同时携带
`RecallBarrier`与`CharacterNoteOriginBarrier`。前者由同一轮RecapGrid online candidate source选出的provider-visible
raw Observation后缀经`PlayerTurnObservationEnvelope` parser聚合；后者在同一次materialization中读取带exact
source address的raw Action units，以runtime-derived visible-text SHA-256/UTF-8 byte count与CharacterMemory
durable provenance做exact join，只把来源Action仍可见的`Applied` `{DefaultPodId, MemoId}`作为typed blocker。
origin join最多接受65,536个distinct Action source，按400条批量写入connection-local TEMP request table后以单条
`capture LEFT JOIN character_note`查询读取，并贯穿turn cancellation；不会为每个Action分别执行capture/notes查询。
selected candidate会先确认可materialize，derived context contribution与browser recent display文本都不参与构造。
`RecallBarrier`当前只做parser-based exact-key去重，尚不表达`MemoExactText`覆盖`MemoSummary`/`MemoGist`的dominance；
origin barrier则按Memo阻止全部召回粒度，不解析`SourceId`。

enabled provider把已采样timestamp且尚无recall的typed `PlayerTurnObservation`、同一pre-append raw window中的
Reply/DeliveryFailure notices与最近一条非空visible Action确定性渲染为
`atelia.galatea.memo-recall-context.v1` JSON；不调用第二个query-builder模型。required player text完整保留，optional
notice按whole-item前缀、Action按whole item纳入512 KiB总query budget，`NoteSaveReceipt`不进入query。Default MemoPod
在短`_podMutationGate`内按settled state identity打开独立Frozen handle，provider await发生在gate外。selector最多返回8个
ordered IDs；Galatea按Title eligibility、origin barrier、recall barrier与1 MiB final Observation budget选择第一条
`MemoExactText`，最终注入数量为0..1。canonical SourceId为
`memo-pod:v1/<32-lowerhex PodId>/<canonical MemoId>`，body为完整`Title + ExactText`，不截断。selector成功返回空数组或
候选全部被过滤是正常underfill；configured provider/authority failure fail closed并阻止main Completion。

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

`POST /api/v1/mailbox/ready-turn` 是供first-party browser心跳使用的conditional mutation，接受strict JSON
`{connectionId?}`。它不向browser暴露`reply_notice`正文，也不接受player text：server在per-session
`TurnLock`内先结算既有lease与latest extraction gap，只允许exact `Idle` boundary，再验证main connection与
fresh admission。随后用code-owned固定文本`玩家本轮未提交新的动作；本轮仅由外界回信到达触发。`执行
`BeginCutoff`。没有`Ready` notice时返回204 empty，且不创建新live turn、main-Agent provider call或active reply
lease；但admission仍可能结算既有reply lease/extraction gap，必要时会调用outbound extractor并持久化
capture/tombstone。有notice时才原子冻结bounded FIFO prefix，并以202 `{turnId}`启动现有
player-composite/SSE fresh turn。该固定文本不经过input normalizer，textarea等browser草稿不进入Observation。
busy、failed turn及其他recovery boundary返回typed
409；尤其不会自动abandon failed turn、resume/restart recovery或claim对应Ready notice。多tab竞争由
`TurnLock`与SQLite lease共同串行化；server endpoint自身不形成后台无限循环，browser opt-in仍是调度开关。

First-party browser在composer中提供默认关闭且不持久化的“页面打开时，收到 Codex 回信后自动继续”开关。
勾选后使用single timer递归执行1秒heartbeat；前一次HTTP admission或其accepted SSE turn未结束时不会重叠
发起下一次。204只续约下一次heartbeat；202沿用现有turn stream；busy只跟随已发布turn。任意terminal SSE
error都会取消勾选，避免rollback为Ready的notice被自动重试。recovery、
unprovisioned、非预期协议以及无法由current/recent只读视图确认的response-loss都会fail closed并取消勾选，
不会自动重发mutation或授权resume。自动turn从不读取、提交或清空textarea；只有本tab手动提交且亲自收到
202的turn在`done`时清空草稿。Checkbox由每个tab各自拥有，关闭/休眠页面会暂停调度，browser timer throttling
也意味着1秒只是best-effort间隔；唯一消费仍由server `TurnLock`与durable lease保证。

独立的`GET /api/v1/mailbox/status`只读取supervisor已持有的delegation store，不调用
`GetSessionAsync`、不attach session、不Signal pulse，也不触发extractor、transport或provider。它在单个
SQLite read transaction中只聚合状态、排队数量、Ready notice数量、attempt/code与next retry time；不会选择或
返回正文、收件人、主题、dispatch/thread/turn/operation identity或hash。响应固定为
`{state,queuedCount,readyNoticeCount,attemptCount,code,nextRetryAtUnixTimeMilliseconds}`并带
`Cache-Control: no-store`；state优先级为`unavailable > quarantined > accepted-history-unavailable > backoff >
active-running > ready-reply > queued > no-mail`。页面以独立single-in-flight、递归`setTimeout`的5秒轮询展示
该状态和两个count，无论自动续接checkbox是否勾选都会继续；它只提供观察能力，不改变
`POST /mailbox/ready-turn`的lease/admission语义。

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

successful fresh/recovery主Completion回到`Idle`并结算reply lease后，host只读取/render一份frozen terminal
Action target，同时直接启动Mail与Character Memory `ReconcileTargetAsync`；两者始终drain。Note linked token只影响
capture前；capture后settlement不观察deadline/Mail-abort token。post-completion只把明确的pre-capture cancellation/
deadline、`TextExtractionException`与Pod availability当best-effort；`DeferredAfterCapture`保留pending且不回执，
Quarantined/invariant fail closed。admission先恢复pending，再对latest exact target并行结算Mail/Note；任何admission
pre-capture失败都会阻止新mutation。

non-empty ExactText batch结算为`Applied`时，CharacterMemory SQLite V2在同一事务建立DerivedInfo `Pending` work。
session-owned background pump使用capacity-1 wakeup channel与单调external signal generation；channel可以合并忙碌期
wakeup，但不会丢掉每个外部signal对应的单次推进额度，也不会无signal自主热重试。每个signal至多处理一批：它短暂取得`TurnLock`，按
source Action地址从SessionJournal重建raw Observation与visible Action并核验durable fingerprint，随即释放锁，再调用
独立`CharacterNoteDerivedInfoEnricher`生成完整Title/Gist/Summary batch。provider调用使用独立30秒deadline，不继承HTTP/
browser/当前turn cancellation；timeout、invalid output或provider failure保留Pending，不撤销ExactText或回执，也不让主
turn失败。startup和后续安全turn boundary会再次signal，session内Pending cursor按round-robin避免一个长期失败batch
永久遮挡后续batch；当前没有持久化retry schedule或attempt counter。30秒deadline通过cancellation请求实现；若底层
provider完全忽略cancellation，session shutdown会继续等待该调用返回，以免提前释放仍被使用的durable/session资源。

生成结果先durable写成`Prepared`，之后不再调用模型；Default MemoPod mutation按base/target identity执行
`UpdateDerivedInfo -> Planned -> Freeze/confirm -> Applied`。只有Planned占用mutation slot，并在attach、fresh、recovery
admission以及任何新ExactText capture前provider-free恢复。MemoPod document/state identity包含DerivedInfo，但FrozenPrompt v3
仍只包含`id + exact_text`。CharacterMemory store会在strict V1 validation后事务化迁移为V2；程序测试不会主动打开ignored
live store。

Note extractor只把`${characterName}`本人已明确完成提交的长期Note保存请求识别为artifact；想到、计划、
草稿、普通世界内书写、引用旧Note或仅声称已经保存都不构成提交。`ExactText`与`EvidenceQuote`必须是visible Action
的ordinal substring；semantic contract为`semantic.v4`，tool name与`exactText` / `evidenceQuote` schema保持不变。
Action address、visible-text SHA-256与UTF-8 byte count始终由runtime从同一canonical visible target派生并持久化，
不进入`CharacterNoteIntent`或模型tool schema。它们也供`CharacterNoteOriginBarrier`判断新近Memo的来源正文是否仍在
当前provider-visible raw context，避免零信息增量的重复召回。

只有durable `AppliedNow`且final head仍等于target Action时，1..N条`CharacterNoteAppliedMemo`才由code-owned
renderer冻结成一条`NoteSaveReceipt`，再放入每个`UserSessionHost`私有的bounded in-process FIFO。zero、Rejected、
Deferred、AlreadyApplied、admission recovery、render failure或queue full都不伪造/补发。只有下一次普通player
`StartTurn`的`BeginCutoff == Empty`分支会`TryDequeue`一条，作为sole/final notice执行at-most-once attach。
Created reply cutoff、ready-turn、inbound与recovery不领取；领取后的pre-dispatch stop、失败、Undo、rewind或restart
都不重新排队。该回执只证明列出的ExactText已保存到默认MemoPod，不承诺分类、metadata补全或召回。非fatal Mail
失败不回滚已保存Memo：final fence仍成立时先queue真实回执，再原样传播Mail错误；fatal/caller cancel/head change不queue。
DerivedInfo pump signal同样在最终Mail错误传播前完成，但signal不等于metadata已生成或已落盘。

每个outbound-mail Action extraction batch由`GalateaDelegationSqliteStore`单事务全有或全无地capture；成功的0-intent
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
current bind要求带timestamp的新canonical shape，且prompt、SQLite rendered Observation/digest与SessionJournal raw
Observation共用同一份已采样字符串。Store reopen仍用closed parser验证带timestamp的新shape以及既有无timestamp的
current/legacy历史bytes，再逐项核对player text与notice kind/order/body，不用current writer把历史heading或timestamp
重渲染成新值。

每个user至多一个active lease。recovery只继承已持久lease，不claim后来Ready的notice；inbound turn也不claim。
lease settlement发生在outbound mail extraction之前，因此已经接收回信的terminal Action即使后处理失败也不会重新
投递同一notice。普通player Undo只移动SessionJournal selected lineage：已capture的outbox继续推进，active
Codex turn不interrupt，Ready保持Ready，Consumed永不重新武装，fixed Codex thread context也不倒退。这里没有
旧`RetractedBeforeDispatch`/`SourceRetracted`内存状态。

该SQLite current-state machine可跨Galatea/sidecar重启恢复，但只承诺同一dispatch **at-most-one
`start-turn` attempt** 与`OutcomeUnknown`保守只读reconciliation；不承诺provider-call exactly-once。
TextExtractor只对`OpenAICodexResponsesException`的exact
`TransportOutcomeUnknown`做最多5次总尝试，重试前依次等待1s、2s、4s、8s；HTTP status failure、
SSE/protocol failure与普通异常不重试。每个logical extraction复用同一request/client，artifact tool只在
最终成功响应后执行；因为pre-response outcome仍可能已经消耗provider算力，重试可能产生重复计费，但不会
重复本地artifact副作用。Outbound Mail extraction不设置code-owned elapsed deadline，只服从caller cancellation；若
provider持续不结束且caller不取消，当前turn、recent refresh、SSE `done`与`TurnLock`会继续等待，这是当前
有意选择的完成优先语义。failure/cancellation不写empty tombstone；主Action仍已durable，当前SSE以错误结束，
下一次admission会在接受新turn前重试该exact extraction gap。capture commit只signal supervisor，主Galatea turn
不等待sidecar accepted或final。Completion call log仍可能包含完整tool arguments，debug摘要不重复正文、
subject或evidence。

历史 Agent Control profiles 必须继续保留，供 Prepared/ToolContinuation 按 frozen
identity 绑定；fresh/NewRequest 不再绑定 current profile，也不向新的模型请求注入
`recap_grid_control`。配置中的 current profile 仅提供 missing-session structural bootstrap
所需的 admission authority。Route manifest 仍在首次
RecapGrid work 时延迟读取；current canonical V2只保留 exact per-route `connectionId` 及并发/timeout调度
policy。Completion contract有意不提供caller-selected output cap；具体adapter只使用“不限量/模型最大值”语义，
route与connection都不能覆盖该策略，并且没有 wildcard/default fallback。

## 恢复顺序

- Prepared：先 exact bind frozen completion 与 frozen tool identity；不打开 Online 或
  derived stores。
- Started：启动时 strict config/connections 已冻结；默认 Refuse 早于本次 current
  connection selection/client、route 与 derived owner。
- 当前 root strict config language为V6；connections与delegate route保持owner-defined V2，profile保持owner-defined V1。当前Linux-only
  file loader对这些文件与`characterContextTemplateFile`都执行code-owned byte cap、existing-ancestor no-reparse与final-file
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
| GET | `/api/v1/recap-cadence-progress` | 独立Timeline/Cadence HistoryLoad telemetry；session attach后inspector纯读 |
| POST | `/api/v1/chat/turns` | 202 `{turnId}` |
| POST | `/api/v1/chat/turns/resume` | 202 `{turnId}` |
| POST | `/api/v1/mailbox/inbound` | 202 `{turnId,messageId}` |
| POST | `/api/v1/mailbox/ready-turn` | 无Ready为204 empty；已原子启动为202 `{turnId}` |
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

Cadence progress没有嵌入`RecentTurnsResponseV1`或SSE `done.recent`；新增的
`GET /api/v1/recap-cadence-progress`是first-party browser独立best-effort读取的closed telemetry。
因此现有recent与SSE stable grammar保持逐字段不变，progress暂时失败也不会压掉已经成功读取的turns、
Context header或rewind token。

这些HTTP/SSE grammar、bounds、terminal/reconciliation语义，以及tracked first-party browser对它们的消费行为，
已由`session-journal-contract-r2-approved-surfaces-v1`批准为Stable V1。该批准不包含deployment/provider readiness、
diagnostic逐字文本、login HTML、bootstrap、cache token、cookie实现或ignored operator state。没有真实需求前不增加pagination、cursor、
Last-Event-ID、ack或dual grammar；breaking change必须形成新candidate/version。
该旧tag不认证后来新增的独立cadence telemetry endpoint；它的current closed contract由本节、代码与测试共同定义，
且不改变获批的`RecentTurnsResponseV1`或SSE frame grammar。

## Readiness 与 cadence telemetry

`GET /api/v1/recent-turns` 返回 `recapGridReadiness`。它绑定同一 read view 与 recent raw
head：先用 Getter resolve；仅 nonempty active 且 unfulfilled 时调用 Manager 的只读
`InspectBuildProgress`。状态为 `ready`、`frontier`、`blocked`、`no-rows`、`no-active`、
`unprovisioned`、`busy`、`stale` 或 `invalid`，并携带可证明的 Timeline/Control/Store/
recipe/row authority 与 bounded metrics。`ready`时同一Getter handle还会按该raw head的governing
`derivedContext.nthPrevious`只读resolve/materialize `contextHeader`，并在最终raw-head fence后与readiness一起发布；
该读取不dispatch provider、不build、不写。

`GET /api/v1/recap-cadence-progress`整条route先复用既有`GetSessionAsync`取得`UserSessionHost`。
因此对`create-if-missing`用户的missing repository，第一次直接GET会先执行同一份first-turn structural
SessionJournal/Cadence/Timeline/Control bootstrap；这一步是既有session attach policy，不属于telemetry
inspector的纯读承诺。session attach/bootstrap完成后，service method才以non-blocking方式取得该session的
`TurnLock`；writer占用时立即返回503 `{code:"recap-cadence-progress-busy",error}`，并且在busy分支不读
Engine、Timeline或Cadence。取得gate后，它捕获current raw head，使用
`O200kBaseHistoryUnitLoadEstimator`纯读Cadence snapshot与selected Timeline head row，再从该row end
（empty Timeline则从SessionCreated seed）测量到captured head的recent raw suffix。raw head不存在时返回
exact `unprovisioned/raw-head-absent`。从TurnLock gate开始的service inspector区段不创建Completion client、
Online、Manager或Store，不capture Timeline、不dispatch provider，也不写repository/sidecar。

response始终是exact closed object：

```text
{
  freshness, state, observedRawHead, cadenceBaseline,
  recentHistoryPlanningUnitCount, recentHistoryLoad,
  recapIntervalHistoryLoad, minimumRecentHistoryLoad,
  buildThresholdHistoryLoad, remainingHistoryLoad,
  historyLoadEstimatorId, code, detail
}
```

所有HistoryLoad字段都是nullable canonical nonnegative decimal string（`0`或无前导零的十进制），
browser用`BigInt`比较和格式化；`recentHistoryPlanningUnitCount`是nullable nonnegative safe integer。
closed state为`below-target|awaiting-replay-safe-boundary|awaiting-recent-reserve|cadence-ready|limited|
unavailable|unprovisioned|stale`，freshness独立为`exact|stale`。`recapIntervalHistoryLoad`是B，
`minimumRecentHistoryLoad`是R：尚未选出first replay-safe boundary时，threshold为ideal `B+R`；
选出boundary后，threshold为`boundary measured load + R`，已经包含overshoot，是effective threshold。
`remainingHistoryLoad`只表示距对应cadence threshold的差，不承诺Recap build已经开始。

tracked browser在初始recent成功后、terminal current确认完成后以及Undo/reconciliation的recent刷新后，
独立best-effort刷新该endpoint；fresh/resume的202一经确认就在首次读取accepted response body前把上一份
progress标为stale，`turn-busy`也无条件标stale，有turnId才继续attach。turn attach与rewind pending保持同一
规则，busy或其他progress失败保留上一稳定边界。若新exact progress的`observedRawHead`与当前exact
`recapGridReadiness.observedRawHead`不同，browser会降为`stale/browser-head-mismatch`，绝不显示为exact。
进度条只接收由BigInt缩放得到的0..1展示比例，权威load、threshold与remaining不转换为JavaScript Number。
HistoryLoad是estimator-scoped的Timeline cadence内部度量，不是provider/model token数，也不是完整prompt或
context-window占用；这里也不尝试重建Getter实际选中的context load。

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

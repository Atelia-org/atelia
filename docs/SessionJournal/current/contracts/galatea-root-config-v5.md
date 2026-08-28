# Galatea root config V5 current contract

状态：**Current product contract；hard cut from V4**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`、
`GalateaConfigValidationTests`、`GalateaSessionProvisioningTests`与
`GalateaTrackedPromptTemplateTests`  
Prior historical contracts：[V4](galatea-root-config-v4.md)、[V3](galatea-root-config-v3.md)、
[V2](galatea-root-config-v2.md)、[V1 approved contract](galatea-root-config-v1.md)

本文定义Galatea `config.json` current V5的exact field language、path semantics、per-user
character context materialization、SessionJournal provisioning policy与durable delegation storage boundary。
V5只改变Galatea-owned root config与启动期主system prompt ownership：TRPG/output/mail协议由binary拥有，
operator只拥有世界观、人物设定与长期记忆context。Completion `connections.json`、RecapGrid、HTTP/SSE、
mail/delegation与SessionJournal durable wire继续服从各自owner的现有版本。

V1–V4文档保留各自当时的历史事实，但不认证V5 delta。Current reader没有旧版本fallback、dual fields、
automatic migration、旧完整prompt识别或existing-file rewrite。

## 1. Authority、文件与materialization边界

Reader与runtime materialization由`GalateaStrictConfigReader`、`GalateaConfigLoader`、
`GalateaSystemPromptComposer`及`GalateaConfigValidation`拥有；bootstrap writer由
`GalateaConfigBootstrapper`与`GalateaConfigTemplateFactory`拥有。共享name与closed renderer contract由
`Atelia.Galatea.Prompts` assembly拥有。

主system prompt的三份source及ownership为：

1. [TRPG protocol prefix](../../../Galatea/prompt/trpg-protocol-prefix-zh-cn.md)：Galatea.Server embedded、
   code-owned，定义TRPG GM、voice/output grammar与GM carrier来源边界；
2. operator `characterContextTemplate`或`characterContextTemplateFile`：世界观、人物设定与长期记忆；
3. [mailbox protocol suffix](../../../Galatea/prompt/trpg-mailbox-protocol-suffix-zh-cn.md)：Galatea.Server
   embedded、code-owned，定义界外邮箱叙事协议。

`GalateaUserFileConfig`只表示operator file shape；它保存character context source及optional file path。
`GalateaUserConfig`只表示resolved runtime shape；它保存validated `GalateaCharacterName`、
`GalateaPlayerName`与finalized `SystemPrompt`，不保留context source、path或分段identity。所有user在host
composition前完成一次materialization；provider request与每个turn都不会重读或重新渲染文件。

Operator context是provider-visible prose，不是权限、feature或安全边界。Runtime不解析Markdown H2、列表、
注释或其他自然语言结构来启用/禁用协议；code-owned prefix/suffix也不能由context覆盖。

`config.json`不是完整runtime配置：loader仍要求同目录Completion-owned `connections.json`与Galatea-owned
`delegates.json`，并加载required RecapGrid profile metadata。这些routing fields不进入raw SessionJournal或
RecapGrid durable semantic identity。

## 2. Strict JSON与version language

Root file必须是Linux no-follow regular file，长度为1 byte..1 MiB，JSON max depth为32。Accepted language为：

- exact一个JSON object；strict UTF-8，无BOM、comment、trailing comma或trailing data；
- property order与whitespace不固定；合法escaped property name decode后按exact name处理；
- 每层unknown或wrong-case property拒绝；duplicate按decoded name的`OrdinalIgnoreCase`比较拒绝；
- `v`可位于任意property位置，但必须存在且raw token为exact integer `5`；V1–V4、versionless、future、
  `null`、string、fraction或exponent form全部拒绝；
- source-generated materialization只发生在strict reader通过之后；`sessionProvisioning`、`characterName`与
  `playerName`的required/type acceptance由strict reader本身决定，不能依赖record或enum default。

## 3. Exact field language

### 3.1 Root object

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `v` | required number token | raw token exact `5` |
| `users` | required array，1..256 items | 每项服从§3.2；`userId`、resolved session path与resolved delegation state path分别unique |
| `listenUrls` | optional；missing/`null`为runtime `null`；否则array 0..256 | item为nonblank string；duplicate与order保留；loader视为opaque string |
| `callLogDir` | optional；missing/`null`为disabled；否则string | nonblank；relative以config directory为base；runtime为absolute；existing components不得是symlink/reparse point；与每个resolved `sessionDir`及`delegationStateDir`双向non-nested |
| `maintenanceMode` | optional boolean | missing为`false`；`null`或非boolean拒绝 |
| `recapGrid` | required object | 服从§3.5；没有`null`或default fallback |

### 3.2 User object与operator context source

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `userId` | required string | nonblank；所有user中exact ordinal unique |
| `password` | required string | nonblank；用于Galatea login validation |
| `characterName` | required string | 服从§3.3；无root/global default或history推断；不同user可以相同 |
| `playerName` | required string | 服从§3.3；故事内玩家角色，不是login `userId`；无default/推断；不同user可以相同 |
| `sessionDir` | required string | nonblank；relative以config directory为base；absolute保持同一target；runtime只接收absolute path |
| `delegationStateDir` | required string | nonblank；relative以config directory为base；runtime只接收canonical absolute path；没有fallback或从`sessionDir`推导 |
| `sessionProvisioning` | required string | closed exact token：`existing-only`或`create-if-missing` |
| `characterContextTemplate` | string；可missing | 没有有效file时必须提供合法context source；inline text除validation外保持exact |
| `characterContextTemplateFile` | optional string-or-null | missing/`null`/empty/whitespace视为absent；nonblank path在load时必须读取成功并覆盖inline；missing in-root path可由§4 bootstrap创建 |

`characterContextTemplateFile` relative path以config directory为base。文件必须是1 byte..1 MiB no-follow
regular file与strict UTF-8；decode后执行`Trim()`。有效file允许inline missing或blank，但explicit
`characterContextTemplate:null`仍因wrong JSON type拒绝。Inline source不由loader或renderer `Trim()`。

V4 `systemPromptTemplate`与`systemPromptTemplateFile`在V5中是unknown fields；V5没有兼容字段、constructor
fallback、旧完整prompt自动剥离或literal name default。

### 3.3 Character/player names与template language

`characterName`是进入voice marker和prompt prose的canonical单行label；`playerName`是进入prompt
source-attribution/prose的canonical单行label。两者服从同一语法：

- strict UTF-16、already Unicode NFC、already trimmed；strict UTF-8长度1..128 bytes；
- 拒绝Unicode Control、LineSeparator、ParagraphSeparator；Unicode Format只允许U+200D ZWJ；
- 至少包含一个non-Format rune；拒绝`[`、`]`、`$`、`{`、`}`；
- exact拒绝reserved output marker `旁白`、`状态摘要`与`角色名`；不同user无需unique。

Template language只有两个exact、case-sensitive token：`${characterName}`与`${playerName}`。Operator context
必须nonblank且至少出现一次exact character token；player token optional。任何其他或残缺`${...}`都拒绝。
Replacement使用ordinal、one-pass、non-recursive语义；不normalize或修改其他字符。

两个name都不承担别名、代词、年龄、性别、关系史或persona生成。

### 3.4 Fixed composition与bounds

Materialization顺序为：validate names → resolve storage paths → select/read operator context → exact拼接三段 →
执行一次closed renderer → construct runtime user。Exact composite source为：

```text
protocolPrefix + "\n\n---\n\n"
    + characterContext
    + "\n\n---\n\n" + mailboxProtocolSuffix
```

Prefix/suffix embedded source均为nonblank、BOM-less、LF-only、bounded strict UTF-8，并在使用前通过同一template
grammar验证。External context file、每份embedded resource的读取上限为1 MiB；拼接后的composite source与final
rendered prompt也分别不得超过1 MiB。这里没有per-section digest、runtime module list、include、condition、
priority、H2 parser或第四份完整prompt。

两个user可以引用同一context file；每个user使用自己的exact names独立物化。Runtime及durable owner只接收最终
`SystemPrompt`，不感知三段来源。

### 3.5 Session provisioning、RecapGrid与Completion sibling

两种provisioning policy保持V4行为：

- `existing-only`只打开已provision的raw SessionJournal repository；missing、empty或incomplete path映射为
  `session-unprovisioned`。Writable `Open`保留SessionJournal owner定义的crash-tail recovery；maintenance mode只
  `OpenReadOnly`。Provisioning层不adopt、reset、rebuild或fallback create。
- `create-if-missing`只在普通writable host首次实际需要user session、且final path完全不存在时，在同一parent下的
  unique staging创建raw SessionJournal、Cadence、empty Timeline与empty Control。Raw初始化使用default connection的
  `ModelId`、`CompletionSurfaceId`与该user finalized `SystemPrompt`；全部验证和handle close成功后才用Linux
  `renameat2(RENAME_NOREPLACE)`原子create-only发布。Existing filesystem entry绝不替换或补写。
- Maintenance mode绝不create；login本身不provision；首次需要`GetSessionAsync`的authenticated operation触发lazy
  initialization。
- `GalateaFirstTurnBootstrapPolicy`继续唯一拥有Cadence/Timeline policy；Control使用current exact AgentControl
  profile Admission且missing-session create要求`Create` permission。Private candidate必须通过exact raw三事件、
  Cadence/Timeline/Control empty、Store/asset/recipe absent与Getter raw-head验证；不读route、不创建Completion
  client、不dispatch provider。
- Failed lazy从in-process cache按user key与Lazy instance identity精确移除。只有本次Create与Dispose都成功的
  unpublished owned staging才best-effort删除；crash/partial residue与published final path不由normal runtime清理。

`sessionDir`没有process-CWD或existence-based fallback、repository move或config-directory confinement承诺。
Resolved session paths按platform comparer exact unique。所有resolved `delegationStateDir`也exact unique、彼此双向
non-nested，并与所有session paths及optional `callLogDir`双向non-nested；existing path components不得含
symlink/reparse point。

Durable delegation supervisor继续在host启动时eager classify每个user。Existing state与matching session才
strict-open并取得process-lifetime writer lock；state存在但session missing时在SQLite/lock open前fail closed；
missing state直到writable SessionJournal成功open/provision后才创建physical-frontier baseline。Maintenance mode不
创建baseline、不attach writable session、不启动pulse scheduler或sidecar effect。没有process-local fallback、
automatic reset或migration。

`recapGrid` exact fields保持不变：required nonblank `routeManifestPath`只resolve并拒绝existing symlink/reparse
components；`agentControlProfileFiles`为1..256个nonblank、canonical-path unique、eager strict decode的existing
files；`currentAgentControlProfileId`必须exact命中loaded registry且只提供missing-session bootstrap admission。
Route仍延迟到首次RecapGrid work读取，没有wildcard/default fallback。

Galatea RecapGrid V6继续使用validated character/player names展开member prompts；V5不改变asset、Definition、
BuildTarget、route或active recipe。Sibling `connections.json`继续使用Completion-owned numeric V1；Galatea仍要求
nonempty exact `selectableConnectionIds`包含default connection，并要求`bindings` exact只含
`galatea.input-normalizer`与`galatea.outbound-mail-extractor`，每个值为exact existing connection ID或explicit
`null`。Missing、wrong-case、extra、blank或unknown全部fail closed，不fallback default。Provider/model/endpoint/
secret不进入主prompt分段identity。

## 4. Bootstrap与V4 migration

Current bootstrap写exact numeric `v:5`，为`alice`/`bob`显式写character/player names、
`characterContextTemplateFile:"prompts/character-context-standard-zh-cn.md"`、`create-if-missing`与各自storage path。
[Standard context](../../../Galatea/prompt/character-context-standard-zh-cn.md)是embedded、BOM-less、LF-only、
bounded strict UTF-8 starter，包含通用世界观、人物设定与空memory slots，不是code-owned runtime protocol。

Bootstrap只为existing/new V5 root中nonblank `characterContextTemplateFile`指向的missing in-root target创建
missing parent，以`FileMode.CreateNew`写入standard context并`Flush(true)`。多user共享同一路径只创建一次；
existing file永不覆盖；missing outside-root target不创建。任何生成都fail-stop并列出paths，operator检查后重启。
Prefix/suffix只从binary embedded resources读取，绝不复制到operator目录。Bootstrap不创建SessionJournal、
RecapGrid state、delegation state或provider effect。

V4 operator必须停服、备份并确认actual `Galatea:ConfigPath`，将每个完整prompt拆出operator-owned context，
删除V4 prompt fields并改为V5 fields/version。应用不自动迁移config或ignored machine-local files。把旧完整prompt
直接作为V5 context会重复code-owned protocol，属于operator migration错误；runtime不通过自然语言或Markdown
heading猜测并修复它。

## 5. Existing durable sessions与recovery

V5不改写任何existing raw setup。Existing Idle session在下一次fresh turn由现有`ReconcileDesiredSetup` exact比较
finalized prompt：bytes不变则复用governing setup，变化则append一个新的`SystemPromptSetup`。这是正常setup
rotation，不是SessionJournal schema migration。

Prepared/Frozen recovery继续按historical governing setup与frozen request identity恢复，不用current prefix/context/
suffix重组历史请求。V5不增加protocol/context durable columns、renderer version、receipt或migration event。

## 6. Bounds、classification与non-promises

V5保留root 1 MiB、users 1..256、`listenUrls` 0..256、profile paths 1..256、external context/composite
source/final prompt各1 MiB、profile 128 KiB与deferred Route 1 MiB bounds。Syntax/type/version/unknown/duplicate、
invalid root/file UTF-8及invalid `sessionProvisioning`归类为`InvalidDataException`；name/context、blank、duplicate identity/path与dependency
mismatch等semantic failure归类为`InvalidOperationException`，inner exception可保留shared contract detail。
Underlying IO/path/permission与owner-local dependency exception可以传播；diagnostic逐字文本不是machine contract。

本合同不承诺password encryption、hot reload、automatic config migration、existing-session character/player rename、
pronoun/persona profile、arbitrary prompt modules、独立world/memory files、operator protocol override、Markdown security
policy或普遍path confinement。V5不改版RecapGrid、mail extractor/ContractId、mail/player envelope、delegation SQLite、
SessionJournal、Completion、HTTP或SSE durable/schema contract。

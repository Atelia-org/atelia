# Galatea root config V6 current contract

状态：**Current product contract；hard cut from V5**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`、
`GalateaConfigValidationTests`、`GalateaSessionProvisioningTests`与
`GalateaTrackedPromptTemplateTests`  
Prior historical contracts：[V5](galatea-root-config-v5.md)、[V4](galatea-root-config-v4.md)、[V3](galatea-root-config-v3.md)、
[V2](galatea-root-config-v2.md)、[V1 approved contract](galatea-root-config-v1.md)

本文定义Galatea `config.json` current V6的exact field language、path semantics、per-user
character context materialization、SessionJournal provisioning policy与storage topology boundary。
V6只改变Galatea-owned root config：每个user显式增加`characterMemoryStateDir`，并把session、delegation、
character-memory与optional call-log路径关系收进一个total topology contract。V5建立的system prompt ownership、
Completion `connections.json`、RecapGrid、HTTP/SSE、mail/delegation与SessionJournal durable wire保持不变。

V1–V5文档保留各自当时的历史事实，但不认证V6 delta。Current reader没有旧版本fallback、dual fields、
automatic migration、旧完整prompt识别或existing-file rewrite。

## 1. Authority、文件与materialization边界

Reader与runtime materialization由`GalateaStrictConfigReader`、`GalateaConfigLoader`、
`GalateaSystemPromptComposer`及`GalateaConfigValidation`拥有；bootstrap writer由
`GalateaConfigBootstrapper`与`GalateaConfigTemplateFactory`拥有。共享name与closed renderer contract由
`Atelia.Galatea.Prompts` assembly拥有。

主system prompt的composition inputs与tracked source authority为：

1. [TRPG protocol prefix](../../../Galatea/prompt/trpg-protocol-prefix-zh-cn.md)：Galatea.Server embedded、
   code-owned，定义TRPG GM、voice/output grammar与GM carrier来源边界；
2. operator `characterContextTemplate`或`characterContextTemplateFile`：世界观、人物设定与长期记忆；
3. [mailbox protocol base](../../../Galatea/prompt/trpg-mailbox-protocol-base-zh-cn.md)：Galatea.Server
   embedded、code-owned，始终定义邮箱Quick Start的收件部分；
4. [Codex outbound appendix](../../../Galatea/prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md)：
   Galatea.Server embedded、code-owned，仅在validated `galatea.outbound-mail-extractor` binding非`null`时追加，
   并继续同一份Quick Start的发件部分；
5. [Character Note save appendix](../../../Galatea/prompt/trpg-character-note-save-appendix-zh-cn.md)：
   Galatea.Server embedded、code-owned，仅在validated `galatea.character-note-extractor` binding非`null`时追加，
   定义长期Note保存Quick Start；只有runtime保存回执证明成功，不承诺分类、metadata补全或召回。

三份base/appendix resource的物理拆分保留capability boundary；启用对应binding时，它们向模型连续呈现为简短
Quick Start。其`##` / `###` heading只是呈现结构，两个appendix presence各自只由validated sibling binding决定。

`GalateaUserFileConfig`只表示operator file shape；它保存character context source及optional file path。
`GalateaUserConfig`只表示resolved runtime shape；它保存validated `GalateaCharacterName`、
`GalateaPlayerName`与finalized `SystemPrompt`，不保留context source、path或分段identity。所有user在host
composition前完成一次materialization；provider request与每个turn都不会重读或重新渲染文件。

Operator context是provider-visible prose，不是权限、feature或安全边界。Runtime不解析Markdown H2、列表、
注释或其他自然语言结构来启用/禁用协议。Character-context fields不能移除、替换或重排validated binding所选的
code-owned bytes；但context与protocol位于同一trusted system message，operator prose仍可能在语义上与协议
冲突，本合同不承诺prompt-level安全隔离。

`config.json`不是完整runtime配置：loader仍要求同目录Completion-owned `connections.json`与Galatea-owned
`delegates.json`，并加载required RecapGrid profile metadata。这些routing fields不进入raw SessionJournal或
RecapGrid durable semantic identity。

## 2. Strict JSON与version language

Root file必须是Linux no-follow regular file，长度为1 byte..1 MiB，JSON max depth为32。Accepted language为：

- exact一个JSON object；strict UTF-8，无BOM、comment、trailing comma或trailing data；
- property order与whitespace不固定；合法escaped property name decode后按exact name处理；
- 每层unknown或wrong-case property拒绝；duplicate按decoded name的`OrdinalIgnoreCase`比较拒绝；
- `v`可位于任意property位置，但必须存在且raw token为exact integer `6`；V1–V5、versionless、future、
  `null`、string、fraction或exponent form全部拒绝；
- source-generated materialization只发生在strict reader通过之后；`sessionProvisioning`、`characterName`与
  `playerName`的required/type acceptance由strict reader本身决定，不能依赖record或enum default。

## 3. Exact field language

### 3.1 Root object

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `v` | required number token | raw token exact `6` |
| `users` | required array，1..256 items | 每项服从§3.2；`userId`、resolved session/delegation/character-memory path分别unique |
| `listenUrls` | optional；missing/`null`为runtime `null`；否则array 0..256 | item为nonblank string；duplicate与order保留；loader视为opaque string |
| `callLogDir` | optional；missing/`null`为disabled；否则string | nonblank；relative以config directory为base；runtime为absolute；existing components不得是symlink/reparse point；与每个resolved `sessionDir`、`delegationStateDir`及`characterMemoryStateDir`双向non-nested |
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
| `characterMemoryStateDir` | required string | nonblank；relative以config directory为base；runtime只接收canonical absolute path；没有fallback或从其他storage path推导；字段本身只建立path authority，binding/session-mode lifecycle见§3.5 |
| `sessionProvisioning` | required string | closed exact token：`existing-only`或`create-if-missing` |
| `characterContextTemplate` | string；可missing | 没有有效file时必须提供合法context source；inline text除validation外保持exact |
| `characterContextTemplateFile` | optional string-or-null | missing/`null`/empty/whitespace视为absent；nonblank path在load时必须读取成功并覆盖inline；missing in-root path可由§4 bootstrap创建 |

`characterContextTemplateFile` relative path以config directory为base。文件必须是1 byte..1 MiB no-follow
regular file与strict UTF-8；decode后执行`Trim()`。有效file允许inline missing或blank，但explicit
`characterContextTemplate:null`仍因wrong JSON type拒绝。Inline source不由loader或renderer `Trim()`。

V4 `systemPromptTemplate`与`systemPromptTemplateFile`在V6中是unknown fields；V6没有兼容字段、constructor
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

Materialization顺序为：decode/validate sibling connections与bindings → validate names → resolve三类per-user storage paths →
select/read operator context → exact组合protocol/context → 执行一次closed renderer → construct runtime user。
Exact mandatory composite source为：

```text
protocolPrefix + "\n\n---\n\n"
    + characterContext
    + "\n\n---\n\n" + mailboxProtocolBase
```

若validated outbound binding非`null`，再追加：

```text
"\n\n" + outboundMailProtocolAppendix
```

若validated Character Note binding非`null`，在outbound appendix（若有）之后再追加：

```text
"\n\n" + characterNoteSaveAppendix
```

四份code-owned protocol source均为nonblank、BOM-less、LF-only、bounded strict UTF-8，并在使用前通过同一
template grammar验证。External context file、每份embedded resource的读取上限为1 MiB；组合后的composite
source与final rendered prompt也分别不得超过1 MiB。这里没有per-section digest、runtime module list、include、
operator condition/priority、H2 parser或第四份完整composed prompt。

两个user可以引用同一context file；每个user使用自己的exact names独立物化。Runtime及durable owner只接收最终
`SystemPrompt`，不感知source分段。

### 3.5 Session provisioning、RecapGrid与Completion sibling

两种provisioning policy保持V5行为：

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
non-nested，并与所有session paths双向non-nested。所有resolved `characterMemoryStateDir` exact unique、彼此双向
non-nested，并与所有session/delegation paths双向non-nested。Optional `callLogDir`与上述全部per-user storage paths
双向non-nested。Loader对character-memory与delegation path执行existing-ancestor symlink/reparse preflight；所有这些
lexical关系由loader与直接构造runtime config共同调用的同一total topology validator拥有。

Character Memory runtime lifecycle由validated binding与session mode共同决定：

- Character Note binding为`null`时，loader仍完成path resolve、total topology与existing-ancestor reparse preflight，
  但runtime不create/open/lock/store-validate该目录；
- maintenance mode即使binding非`null`也不create/open/lock/apply Character Memory，只保留SessionJournal read-only能力；
- binding非`null`的writable session lazy attach时，missing path以current physical frontier和selected head创建baseline
  store与committed empty Default MemoPod，existing path则strict-open并验证owner/schema/integrity；owner与exclusive lock保持到
  session dispose。Runtime不按路径存在性adopt、reset或自动迁移state。

Bootstrap仍只写V6字段，不创建character-memory state。字段存在或binding启用本身都不是Note保存证明；只有
Default MemoPod durable apply settlement建立保存事实，可见save receipt另受at-most-once in-process delivery限制。完整合同见
[Character Note Default MemoPod V1](../../../Galatea/character-note-default-memopod-v1.md)。

Durable delegation supervisor继续在host启动时eager classify每个user。Existing state与matching session才
strict-open并取得process-lifetime writer lock；state存在但session missing时在SQLite/lock open前fail closed；
missing state直到writable SessionJournal成功open/provision后才创建physical-frontier baseline。Maintenance mode不
创建baseline、不attach writable session、不启动pulse scheduler或sidecar effect。没有process-local fallback、
automatic reset或migration。

`recapGrid` exact fields保持不变：required nonblank `routeManifestPath`只resolve并拒绝existing symlink/reparse
components；`agentControlProfileFiles`为1..256个nonblank、canonical-path unique、eager strict decode的existing
files；`currentAgentControlProfileId`必须exact命中loaded registry且只提供missing-session bootstrap admission。
Route仍延迟到首次RecapGrid work读取，没有wildcard/default fallback。

Galatea RecapGrid V6继续使用validated character/player names展开member prompts；V6不改变asset、Definition、
BuildTarget、route或active recipe。Sibling `connections.json`继续使用Completion-owned numeric V1；Galatea仍要求
nonempty exact `selectableConnectionIds`包含default connection，并要求`bindings` exact只含
`galatea.input-normalizer`、`galatea.outbound-mail-extractor`与`galatea.character-note-extractor`，每个值为
exact existing connection ID或explicit
`null`。Missing、wrong-case、extra、blank或unknown全部fail closed，不fallback default。Provider/model/endpoint/
secret不进入主prompt分段identity。Outbound binding为`null`时final prompt仍包含universal mailbox base但不包含
主动发送承诺；非`null`时追加Codex outbound appendix。这个fixed feature branch来自validated sibling binding，
不是新的root/operator prompt module field。Character Note binding同样提供hidden、lazy、borrowed的per-user
extractor runtime supply；为`null`时final prompt不出现Note保存能力，非`null`时追加Character Note保存Quick Start。
该appendix只教角色提交完整原文，并明确只有后续runtime保存回执证明成功；不承诺分类、metadata补全或召回。

## 4. Bootstrap与V5 migration

Current bootstrap写exact numeric `v:6`，为`alice`/`bob`显式写character/player names、
`characterContextTemplateFile:"prompts/character-context-standard-zh-cn.md"`、`create-if-missing`与各自session、
delegation及`character-memory/{userId}` path。
[Standard context](../../../Galatea/prompt/character-context-standard-zh-cn.md)是embedded、BOM-less、LF-only、
bounded strict UTF-8 starter。它说明较早History由RecapGrid派生为带来源的world-understanding与
first-person-autobiography context、冲突时newer raw History优先；下方自主记忆是独立人工长期记录，未来可由
动态外部记忆接管。它不是code-owned runtime protocol。

Bootstrap只为existing/new V6 root中nonblank `characterContextTemplateFile`指向的missing in-root target创建
missing parent，以`FileMode.CreateNew`写入standard context并`Flush(true)`。多user共享同一路径只创建一次；
existing file永不覆盖；missing outside-root target不创建。任何生成都fail-stop并列出paths，operator检查后重启。
Code-owned protocol resources只从binary embedded resources读取，绝不复制到operator目录。Bootstrap生成的
`connections.json`把outbound与Character Note extractor bindings都写为`null`，所以starter composition只有
mailbox base、不含两个appendix，也不启用Character Note extraction supply。
Bootstrap不创建SessionJournal、RecapGrid state、delegation state、character-memory state、MemoPod或provider effect。

V5 operator必须停服、备份并确认actual `Galatea:ConfigPath`，把exact version改为`6`，并为每个user增加互不冲突的
required `characterMemoryStateDir`。`characterContextTemplate*`与finalized prompt composition不需改变。应用不自动迁移
config或ignored machine-local files；bootstrap读取existing V5时会先被strict version gate拒绝，绝不重写该文件。

## 5. Existing durable sessions与recovery

V6不改写任何existing raw setup，也不改变finalized prompt bytes。Existing Idle session在下一次fresh turn由现有
`ReconcileDesiredSetup` exact比较finalized prompt：bytes不变则复用governing setup，变化则append一个新的`SystemPromptSetup`。这是正常setup
rotation，不是SessionJournal schema migration。停服修改sibling config并重启后，把validated outbound或Character
Note binding从`null`切到connection ID或反向切换都会自然改变对应appendix presence，并在下一次fresh触发同一
exact rotation。

Prepared/Frozen recovery继续按historical governing setup与frozen request identity恢复，不用current prefix/context/
mail protocol resources重组历史请求。V6不增加protocol/context durable columns、renderer version、receipt或
migration event。

## 6. Bounds、classification与non-promises

V6保留root 1 MiB、users 1..256、`listenUrls` 0..256、profile paths 1..256、external context/composite
source/final prompt各1 MiB、profile 128 KiB与deferred Route 1 MiB bounds。Syntax/type/version/unknown/duplicate、
invalid root/file UTF-8及invalid `sessionProvisioning`归类为`InvalidDataException`；name/context、blank、duplicate identity/path与dependency
mismatch等semantic failure归类为`InvalidOperationException`，inner exception可保留shared contract detail。
Underlying IO/path/permission与owner-local dependency exception可以传播；diagnostic逐字文本不是machine contract。

本合同不承诺password encryption、hot reload、automatic config migration、existing-session character/player rename、
pronoun/persona profile、arbitrary prompt modules、独立world/memory files、operator protocol override、Markdown security
policy、普遍path confinement、Character Note分类/metadata补全/recall，或save receipt跨restart durable delivery。
V6只版本化root config field language与上述lifecycle gate；Character Memory store/apply/save-receipt grammar由其owning
code、tests与V1合同独立拥有。V6不改版RecapGrid、mail extractor/ContractId、delegation SQLite、SessionJournal、
Completion、HTTP或SSE durable/schema contract。

# Galatea root config V4 historical contract

状态：**Archived historical predecessor；current contract is [V5](galatea-root-config-v5.md)**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`、
`GalateaConfigValidationTests`与`GalateaSessionProvisioningTests`  
Prior historical contracts：[Galatea root config V3](galatea-root-config-v3.md)、
[V2](galatea-root-config-v2.md)、[V1 approved contract](galatea-root-config-v1.md)

本文保留Galatea `config.json` V4当时的exact field language、path semantics、per-user character/player prompt
materialization、SessionJournal provisioning policy与durable delegation storage boundary。
V4当时只改变Galatea-owned root config与启动期prompt materialization；Completion `connections.json`、RecapGrid
Route manifest、AgentControl profile、HTTP、SSE与durable SessionJournal wire继续服从各自owner的现有版本。

V1/V2/V3文档保留各自当时的历史事实，但不认证V4 delta。V4 reader没有旧版本fallback、dual
interpretation、automatic migration或existing-file rewrite。

## 1. Authority、文件与materialization边界

Reader与runtime materialization由`GalateaStrictConfigReader`、`GalateaConfigLoader`及
`GalateaConfigValidation`拥有；bootstrap writer由`GalateaConfigBootstrapper`与
`GalateaConfigTemplateFactory`拥有。共享的character-name与template contract由零依赖
`Atelia.Galatea.Prompts` assembly拥有。Code-owned标准TRPG source template当时由
`Galatea.Server`以单个embedded resource拥有；该单文件source后来在V5拆分，current三段source及ownership见
[Galatea prompt router](../../../Galatea/prompt/README.md)。

`GalateaUserFileConfig`只表示operator file shape；它保存source template及optional file path。
`GalateaUserConfig`只表示resolved runtime shape；它保存validated `GalateaCharacterName`、
`GalateaPlayerName`与finalized
`SystemPrompt`，不保留source template或template path。所有user在host composition前完成一次materialization；
provider request与每个turn都不会重读或重新渲染文件。

`config.json`不是完整runtime配置：loader仍要求同目录的Completion-owned `connections.json`与
Galatea-owned `delegates.json`，并加载required RecapGrid profile metadata。这些host routing fields不进入
raw SessionJournal或RecapGrid durable semantic identity。

## 2. Strict JSON与version language

Root file必须是Linux no-follow regular file，长度为1 byte..1 MiB，JSON max depth为32。Accepted language为：

- exact一个JSON object；strict UTF-8，无BOM、comment、trailing comma或trailing data；
- property order与whitespace不固定；合法escaped property name decode后按exact name处理；
- 每层unknown或wrong-case property拒绝；duplicate按decoded name的`OrdinalIgnoreCase`比较拒绝；
- `v`可位于任意property位置，但必须存在且raw token为exact integer `4`；V1/V2/V3、versionless、future、
  `null`、string、fraction或exponent form全部拒绝；
- source-generated materialization只发生在strict reader通过之后；`sessionProvisioning`、`characterName`与
  `playerName`的
  required/type acceptance由strict reader本身决定，不能依赖enum或record default。

## 3. Exact field language

### 3.1 Root object

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `v` | required number token | raw token exact `4` |
| `users` | required array，1..256 items | 每项服从§3.2；`userId`、resolved session path与resolved delegation state path分别unique |
| `listenUrls` | optional；missing/`null`为runtime `null`；否则array 0..256 | item为nonblank string；duplicate与order保留；loader视为opaque string |
| `callLogDir` | optional；missing/`null`为disabled；否则string | nonblank；relative以config directory为base；runtime为absolute；existing components不得是symlink/reparse point；与每个resolved `sessionDir`及`delegationStateDir`双向non-nested |
| `maintenanceMode` | optional boolean | missing为`false`；`null`或非boolean拒绝 |
| `recapGrid` | required object | 服从§3.4；没有`null`或default fallback |

### 3.2 User object与prompt source

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `userId` | required string | nonblank；所有user中exact ordinal unique |
| `password` | required string | nonblank；用于Galatea login validation |
| `characterName` | required string | 服从§3.3；没有root/global default，不从`userId`、文件名、history或旧prompt推断；不同user可以相同 |
| `playerName` | required string | 服从§3.3；表示故事内玩家角色，不是login `userId`；无default/推断；不同user可以相同 |
| `sessionDir` | required string | nonblank；relative以config directory为base；absolute保持同一target；runtime只接收absolute path |
| `delegationStateDir` | required string | nonblank；relative以config directory为base；runtime只接收canonical absolute path；没有fallback或从`sessionDir`推导 |
| `sessionProvisioning` | required string | closed exact token：`existing-only`或`create-if-missing`；无root/default fallback |
| `systemPromptTemplate` | string；可missing | 没有有效`systemPromptTemplateFile`时必须提供合法name-dependent source；inline text除template validation外保持exact |
| `systemPromptTemplateFile` | optional string-or-null | missing/`null`/empty/whitespace视为absent；nonblank path在load时必须读取成功并覆盖inline source；missing in-root path可由§4的bootstrap以标准template创建 |

`systemPromptTemplateFile` relative path以config directory为base。文件必须是1 byte..1 MiB no-follow regular
file与strict UTF-8；decode后执行`Trim()`，再进入同一个renderer。有效file允许inline source missing或blank，但
explicit `systemPromptTemplate:null`仍因wrong JSON type拒绝。Inline source不由renderer或loader `Trim()`。

Materialization顺序为：validate character/player names → resolve storage paths → select/read source → render →
construct runtime user。两个user可以引用同一template file；每个user使用自己的exact names独立渲染。
Source与rendered prompt分别不得超过1 MiB strict UTF-8。

V3 `systemPrompt`与`systemPromptFile`在V4中是unknown fields。V4没有兼容字段、constructor default或literal
`Galatea` fallback。

### 3.3 Character/player names与template language

`characterName`是进入voice marker和prompt prose的canonical单行label；`playerName`是进入
prompt source-attribution/prose的canonical单行label。两者服从同一语法：

- 必须是strict UTF-16、already Unicode NFC且already trimmed；不自动修正；
- strict UTF-8长度为1..128 bytes；
- 拒绝Unicode Control、LineSeparator、ParagraphSeparator；Unicode Format默认拒绝，只精确允许U+200D ZWJ；
- 整个名字必须至少包含一个non-Format rune，纯ZWJ等不可见label拒绝；
- 拒绝`[`、`]`、`$`、`{`、`}`，防止破坏voice marker或在rendered prompt中留下`${...}` opener；
- exact拒绝reserved output marker `旁白`、`状态摘要`与`角色名`；不同user无需unique。

Template language只有两个exact、case-sensitive token：`${characterName}`与`${playerName}`。Renderer
要求任何source至少出现一次character token；player-aware overload另外允许任意次player token，
而character-only mail/extractor caller遇到player token会fail closed。任何其他或残缺`${...}`都拒绝。
Replacement使用ordinal、one-pass、non-recursive语义；不trim、不normalize、不改换行或其他字符，
并在分配final output前验证rendered strict UTF-8 cap。

`playerName`不是玩家账号、现实姓名必须项或persona profile；`characterName`也不生成代词。
两个field都不承担别名、代词、年龄、性别、关系史或其他人物设定。

### 3.4 Session provisioning与storage

两种provisioning policy保持V3行为：

- `existing-only`只打开已provision的raw SessionJournal repository；missing、empty或incomplete path映射为
  `session-unprovisioned`。普通writable `Open`保留SessionJournal owner定义的crash-tail recovery；maintenance mode只
  `OpenReadOnly`。Provisioning层不adopt、reset、rebuild或fallback create。
- `create-if-missing`只在普通writable host首次实际需要user session、且final path完全不存在时，在同一parent下的
  unique staging创建raw SessionJournal、Cadence、empty Timeline与empty Control。Raw初始化使用default connection的
  `ModelId`、`CompletionSurfaceId`与该user finalized `SystemPrompt`；全部验证和handle close成功后才用Linux
  `renameat2(RENAME_NOREPLACE)`原子create-only发布。Existing filesystem entry绝不被替换或补写。
- maintenance mode绝不create；login本身不provision；首次需要`GetSessionAsync`的authenticated operation触发lazy
  initialization。
- `GalateaFirstTurnBootstrapPolicy`继续唯一拥有Cadence/Timeline policy；Control使用current exact AgentControl
  profile Admission且missing-session create要求`Create` permission。Private candidate必须通过V3已定义的exact raw三事件、
  Cadence/Timeline/Control empty、Store/asset/recipe absent与Getter raw-head验证；不读route、不创建Completion client、不
  dispatch provider。
- failed lazy从in-process cache按user key与Lazy instance identity精确移除。只有本次Create与Dispose都成功的unpublished
  owned staging才best-effort删除；crash/partial residue与published final path不由normal runtime自动清理。

`sessionDir`没有process-CWD或existence-based fallback、repository move或config-directory confinement承诺。
Resolved session paths按platform comparer exact unique。所有resolved `delegationStateDir`也exact unique、彼此双向
non-nested，并与所有session paths及optional `callLogDir`双向non-nested；existing path components不得含
symlink/reparse point。

Durable delegation supervisor继续在host启动时eager classify每个user。Existing state与matching session才strict-open并
取得process-lifetime writer lock；state存在但session missing时在SQLite/lock open前fail closed；missing state直到writable
SessionJournal成功open/provision后才创建physical-frontier baseline。Maintenance mode不创建baseline、不attach writable
session、不启动pulse scheduler或sidecar effect。没有process-local fallback、automatic reset或migration。

### 3.5 `recapGrid`与Completion sibling

`recapGrid` exact fields保持不变：

| Field | Required shape | Load-time rule |
|:--|:--|:--|
| `routeManifestPath` | required nonblank string | relative以config directory为base；root load只resolve并拒绝existing symlink/reparse components，不要求route file存在 |
| `agentControlProfileFiles` | required array，1..256 strings | item nonblank；relative以config directory为base；resolved path按platform comparer unique；file必须存在并eager strict decode |
| `currentAgentControlProfileId` | required nonblank string | exact匹配一个已加载profile ID；仅作为missing-session bootstrap admission authority，不注入fresh/NewRequest completion |

Route manifest仍延迟到首次RecapGrid work读取，没有wildcard/default route fallback。V4不改变Route/profile owner language。
Galatea RecapGrid V6使用同一对validated character/player names展开member prompts；两者任一变化都旋转
Definition/BuildTarget identity，但不进入route/provider identity。

Sibling `connections.json` 继续使用Completion-owned numeric V1。Galatea仍要求nonempty exact
`selectableConnectionIds`包含default connection，并要求`bindings` exact只含
`galatea.input-normalizer`与`galatea.outbound-mail-extractor`，每个值为exact existing connection ID或explicit
`null`。Missing、wrong-case、extra、blank或unknown全部fail closed，不fallback default。Character/player names与template不进入
connection catalog、route或provider secret locator。

## 4. Bootstrap与migration

Current bootstrap写exact numeric `v:4`，为`alice`/`bob`分别显式写`characterName:"Alice"|"Bob"`、
`playerName:"Alex"|"Blair"`、shared `systemPromptTemplateFile:"prompts/trpg-host-standard-zh-cn.md"`、
`sessionProvisioning:"create-if-missing"`、`sessions/alice|bob`与`delegation-state/alice|bob`。该标准template是
embedded、BOM-less、LF-only、bounded strict UTF-8 resource，不包含特定Player的个人信息、昵称或交互历史。

Bootstrap生成缺失root、required sibling templates，以及existing/new root中`systemPromptTemplateFile`
所指向的missing in-root template：只对resolved path仍在config directory内的target创建missing parent，
然后以`FileMode.CreateNew`写入标准resource并执行`Flush(true)`。多user共享同一path只会创建一次。
Existing file永不覆盖；missing outside-root target不创建，后续load继续`FileNotFoundException`。任何生成都
fail-stop并列出paths，operator必须检查后重启。Bootstrap不创建SessionJournal、不验证provider，也不
provision sidecar/RecapGrid asset。

Existing root file不会被添加field、升级version、改password或按template重写；唯一允许的旁路创建
是上述operator已显式指向的missing in-root prompt file。V3 operator必须停服、备份、确认actual
`Galatea:ConfigPath`，为每个user显式配置validated `characterName`与`playerName`，把旧prompt source改成
`${characterName}` / `${playerName}` template，删除V3 prompt fields并改为`v:4`。应用不自动迁移config，
ignored machine-local config/prompt也不属于tracked bootstrap authority。本未发布checkout在character-name delta尚未
运行时继续把player-name delta并入V4，不新建valueless version。

V4 root migration不授权existing session原地character/player rename，也不改变mail/extractor semantic
contract或frozen recovery；这些是独立migration gate。Finalized prompt与current governing prompt不exact相同时，现有fresh
Idle desired-setup reconciliation会按SessionJournal owner合同append新的`SystemPromptSetup`，不会改写旧setup。

## 5. Bounds、classification与non-promises

V4保留root 1 MiB、users 1..256、`listenUrls` 0..256、profile paths 1..256、prompt source/rendered 1 MiB、
profile 128 KiB与deferred Route 1 MiB bounds。Syntax/type/version/unknown/duplicate、invalid root/file UTF-8及invalid
`sessionProvisioning` field language归类为`InvalidDataException`；character/player/template、blank、duplicate identity/path与
dependency mismatch等semantic failure归类为`InvalidOperationException`，其inner exception可保留shared contract detail。
Underlying IO/path/permission与owner-local dependency exception可以传播；diagnostic逐字文本不是machine contract。

本合同不承诺password encryption、secret-store integration、bootstrap file permissions、Kestrel deployment、provider
readiness、automatic config rewrite/migration、hot reload、session repair/move、existing-repository character/player rename、
pronoun/alias/persona profile、derived migration、full RecapGrid activation或普遍path confinement。Connections、Route、profile、
HTTP/SSE及SessionJournal durable wire不会因root version升到V4而同步改版。

# Galatea root config V3 current contract

状态：**Current product contract；hard cut from V2**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`、`GalateaConfigValidationTests`与
`GalateaSessionProvisioningTests`  
Prior historical contracts：[Galatea root config V2](galatea-root-config-v2.md)、
[Galatea root config V1 approved contract](galatea-root-config-v1.md)

本文定义Galatea `config.json` current V3的exact field language、path semantics、per-user SessionJournal
provisioning policy与required durable delegation storage boundary。V3只改变Galatea-owned root config；Completion
`connections.json`、RecapGrid Route manifest、AgentControl profile、HTTP、SSE与durable SessionJournal wire仍服从
各自owner的现有版本。

V1/V2文档保留各自当时的历史事实，但不认证本文的V3 delta。Current reader没有V1/V2 fallback、
dual interpretation、automatic migration或existing-file rewrite。

## 1. Authority与文件边界

Reader与runtime materialization由`GalateaStrictConfigReader`、`GalateaConfigLoader`及
`GalateaConfigValidation`拥有；bootstrap writer由`GalateaConfigBootstrapper`与
`GalateaConfigTemplateFactory`拥有。`config.json`不是完整runtime配置：loader仍要求同目录的
Completion-owned `connections.json`，并从中解析完整 catalog、exact default connection、
Agent selection allowlist 与 input-normalizer binding。这些 host routing fields 不进入
`config.json`、raw SessionJournal 或 RecapGrid durable identity。

## 2. Strict JSON与version language

Root file必须是Linux no-follow regular file，长度为1 byte..1 MiB，JSON max depth为32。Accepted language为：

- exact一个JSON object；strict UTF-8，无BOM、comment、trailing comma或trailing data；
- property order与whitespace不固定；合法escaped property name decode后按exact name处理；
- 每层unknown或wrong-case property拒绝；duplicate按decoded name的`OrdinalIgnoreCase`比较拒绝；
- `v`可位于任意property位置，但必须存在且raw token为exact integer `3`；V1、V2、versionless、future、`null`、
  string、fraction或exponent form全部拒绝；
- source-generated materialization只发生在strict reader通过之后；`sessionProvisioning`的required/type/token
  acceptance由strict reader本身决定，不能依赖enum default。

## 3. Exact field language

### 3.1 Root object

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `v` | required number token | raw token exact `3` |
| `users` | required array，1..256 items | 每项服从§3.2；`userId`、resolved session path与resolved delegation state path分别unique |
| `listenUrls` | optional；missing/`null`为runtime `null`；否则array 0..256 | item为nonblank string；duplicate与order保留；loader视为opaque string |
| `callLogDir` | optional；missing/`null`为disabled；否则string | nonblank；relative以config directory为base；runtime为absolute；existing components不得是symlink/reparse point；与每个resolved `sessionDir`及`delegationStateDir`双向non-nested |
| `maintenanceMode` | optional boolean | missing为`false`；`null`或非boolean拒绝 |
| `recapGrid` | required object | 服从§3.3；没有`null`或default fallback |

### 3.2 User object、prompt与session provisioning

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `userId` | required string | nonblank；所有user中exact ordinal unique |
| `password` | required string | nonblank；用于Galatea login validation |
| `sessionDir` | required string | nonblank；relative以config directory为base；absolute保持同一target；runtime只接收absolute path |
| `delegationStateDir` | required string | nonblank；relative以config directory为base；runtime只接收canonical absolute path；没有fallback或从`sessionDir`推导 |
| `sessionProvisioning` | required string | closed exact token：`existing-only`或`create-if-missing`；无root/default fallback |
| `systemPrompt` | string；可missing | 没有有效`systemPromptFile`时必须nonblank；inline text除blank检查外保持原值 |
| `systemPromptFile` | optional string-or-null | missing/`null`/empty/whitespace视为absent；nonblank path必须读取成功并覆盖inline prompt |

`systemPromptFile` relative path以config directory为base。文件必须是1 byte..1 MiB no-follow regular file和strict
UTF-8；decode后执行`Trim()`且结果必须nonblank。有效文件允许inline `systemPrompt` missing或blank，但explicit
`systemPrompt:null`仍拒绝。

两种provisioning policy的行为为：

- `existing-only`：只打开已provision的raw SessionJournal repository；path missing、empty或因required file missing而
  incomplete时，current host映射为`session-unprovisioned`且不写入该path。其他existing path进入owner-defined open：普通
  writable `Open`可以执行SessionJournal自身定义的crash-tail recovery，maintenance mode只`OpenReadOnly`。若owner open/
  recovery仍判定corrupt或invalid，则provisioning层fail closed，不会adopt、reset、rebuild或fallback create；V3 root config
  contract不统一这些owner/host classification。
- `create-if-missing`：普通writable host在首次实际请求该user session且`sessionDir`完全不存在时调用
  `SessionJournalEngine.Create`在final path同一parent下的不可预测unique staging path构造raw candidate，并在同一unpublished
  candidate内依次创建Cadence、empty Timeline与empty Control。raw初始化使用default connection的`ModelId`、
  `CompletionSurfaceId`及该user最终resolved `SystemPrompt`；三域policy与validation服从下述first-turn bootstrap。仅当全部创建、
  验证及handle/engine关闭成功后，才以Linux `renameat2(RENAME_NOREPLACE)`原子create-only发布到final path，随后从final path
  重新`Open`。若final path已有任何filesystem entry，publish不能替换它；initial observation时已存在则只走owner
  open/fail-closed路径，不会删除、overwrite、adopt、reset、rebuild或补写缺失三域。
- maintenance mode对两种policy都只允许read-only open，绝不create。
- login/authentication本身不provision；首次需要`GetSessionAsync`的authenticated session operation触发lazy initialization。
- first-turn bootstrap的唯一Galatea-owned policy owner为`GalateaFirstTurnBootstrapPolicy`：partition algorithm为
  `FirstReplaySafeBoundaryAtTargetV1`、estimator为O200k、R=24,000、B=60,000、max raw=65,536、max rendered=1,048,576；
  Timeline policy必须从同一Cadence spec投影。Control使用`currentAgentControlProfileId` exact profile的canonical Admission；
  仅在final path确实missing且即将创建时、在创建parent/staging前要求其含`Create` permission。
- private brand-new staging中的Cadence、Timeline、Control create只接受`Created`。发布前必须验证raw为Idle且exact三事件、Cadence
  exact policy、Timeline empty且policy match、Control绑定同一Ref/Timeline并保持generation 0与empty/no-active、Store及
  asset/recipe absent，并由Getter对exact raw head返回`RawHistoryAuthorized`。该过程不读取route、不创建Completion client且不
  dispatch provider。
- auto-create不创建Store、asset、Family/Definition、recipe或activation；它只承诺first-turn structural raw-only，不承诺
  full RecapGrid ready。完整provision/activation仍由operator拥有。
- failed lazy initialization会从in-process session cache精确移除，使operator修复后可在同一process重试；失败本身不会
  自动清理可能残留的filesystem state。
- candidate关闭后、atomic publish前的失败或atomic publish失败时，runtime只best-effort删除本次Create与Dispose都已成功的
  owned staging candidate；
  crash及candidate Create/Dispose中途失败可以留下unique staging residue。Normal runtime不扫描或自动清理任何历史
  staging residue；publish成功后final repository在后续Open/inspection/recent projection失败时也绝不删除。

`sessionDir`没有process-CWD或existence-based path fallback，也没有repository move。Absolute path与`..`仍可进入
platform lexical normalization；本合同不承诺config-directory confinement或完整hostile-filesystem defense。两个user
resolve到同一normalized lexical session path时，在session/client/log side effect前拒绝。

所有resolved `delegationStateDir`按platform comparer exact unique并双向non-nested；每一个还必须与所有user的
resolved `sessionDir`及optional `callLogDir`双向non-nested。existing delegation path components不得包含
symlink/reparse point。Absolute path与`..`只做platform lexical normalization，不要求位于config directory内。
当前V3 hard cut只发布、解析并验证这个Galatea-owned storage boundary；durable delegation supervisor尚未接入
production composition，因此normal host暂不创建、打开或调度该目录，也不会因为目录存在与否改变SessionJournal lazy
provisioning语义。

### 3.3 `recapGrid` object与owned dependencies

| Field | Required shape | Load-time rule |
|:--|:--|:--|
| `routeManifestPath` | required nonblank string | relative以config directory为base；root load只resolve并拒绝existing symlink/reparse components，不要求route file存在 |
| `agentControlProfileFiles` | required array，1..256 strings | item nonblank；relative以config directory为base；resolved path按platform comparer unique；file必须存在并eager strict decode |
| `currentAgentControlProfileId` | required nonblank string | exact匹配一个已加载profile ID；仅作为missing-session bootstrap admission authority，不注入fresh/NewRequest completion |

Route manifest仍延迟到首次RecapGrid work读取，没有wildcard/default route fallback。Root V3不改变Route或profile的
owner-defined V1 language。

### 3.4 Completion `connections.json` 的 Galatea 收紧

Sibling `connections.json` 使用 Completion-owned numeric V1。通用 owner 要求 exact
`v` / `connections` / `defaultConnectionId`，并可选解码 `selectableConnectionIds` /
`bindings`；Galatea 在 provider client、session、logger 或 route side effect前对后两者做
required 收紧：

- `selectableConnectionIds` 必须是 1..256 个 bounded、exact unique、exact existing ID，保留
  operator order，且必须包含 `defaultConnectionId`。它只限制 fresh/current Agent selection；
  RecapGrid route、input normalizer 与 frozen completion recovery 仍从完整 catalog exact bind。
- `bindings` 必须 exact 只含 `galatea.input-normalizer` 与
  `galatea.outbound-mail-extractor`。每个值必须是 exact existing connection ID 或 explicit
  `null`；`null` 唯一表示对应feature disabled。Missing、wrong-case、extra key、blank 或 unknown ID
  都 fail closed，不 fallback default。
- Input normalizer 与 main Agent/RecapGrid 共用一个 host-wide registry，在首个合格短输入
  到来前不创建其 client。`callLogDir` enabled 时，normalizer prompt、清洗前输入与
  provider output 会按同一 Completion call-log contract 写入本地日志。

Numeric V1 未升版：当前 binary 仍接受没有可选扩展的通用 V1，但 Galatea
自身会因缺少 required host metadata 拒绝该文件。包含扩展字段的 manifest 会被旧
closed-root binary 拒绝；迁移必须停服、备份并将 code/manifest 配套发布，没有
automatic rewrite 或 dual reader。

## 4. Bootstrap与migration

Current root bootstrap template写exact numeric `v:3`，为`alice`与`bob`都显式写
`sessionProvisioning:"create-if-missing"`，使用`sessions/alice|bob`与
`delegation-state/alice|bob`相对路径。Bootstrap只生成缺失的root或sibling
connections template；该 template 显式写 `selectableConnectionIds:["local"]` 与
`bindings:{"galatea.input-normalizer":null,"galatea.outbound-mail-extractor":null}`。Config bootstrap本身不创建SessionJournal
repository、不验证provider，也不provision任何sidecar。

若root file已经存在，bootstrap不会添加field、升级version、修改password或按template重写。Operator从V2升级时必须
停服、备份、确认实际`Galatea:ConfigPath`，为每个user明确配置`delegationStateDir`，再把version改为3；应用不会自动迁移。

## 5. Bounds、classification与non-promises

V3保留V2的root 1 MiB、users 1..256、`listenUrls` 0..256、profile paths 1..256、prompt 1 MiB、profile
128 KiB与deferred Route 1 MiB bounds。Syntax/type/version/unknown/duplicate、invalid UTF-8及invalid
`sessionProvisioning` field language归类为`InvalidDataException`；blank、zero lower bound、duplicate identity/path、
dependency mismatch等semantic failure归类为`InvalidOperationException`。Underlying IO/path/permission与owner-local
dependency exception可以传播；diagnostic逐字文本不是machine contract。

本合同不承诺password encryption、secret-store integration、bootstrap file permissions、Kestrel deployment、provider
readiness、automatic config rewrite/migration、session repair/move、existing-repository derived migration、full RecapGrid
provision/activation或path confinement。Connections、
Route、profile、HTTP/SSE及SessionJournal durable wire不会因root version升到V3而同步改版。

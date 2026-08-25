# Galatea root config V2 current contract

状态：**Current product contract；hard cut from V1**  
Authority：current Galatea code、`GalateaRootConfigFieldLanguageTests`与`GalateaConfigValidationTests`  
Prior historical contract：[Galatea root config V1 approved contract](galatea-root-config-v1.md)

本文定义Galatea `config.json` current V2的exact field language、path semantics与per-user raw SessionJournal
provisioning policy。V2只改变Galatea-owned root config及其session initialization行为；Completion
`connections.json`、RecapGrid Route manifest、AgentControl profile、HTTP、SSE与durable SessionJournal wire仍服从
各自owner的现有版本。

V1 appendix及其immutable approval tag保留当时已批准事实，但不认证本文的V2 delta。Current reader没有V1 fallback、
dual interpretation、automatic migration或existing-file rewrite。

## 1. Authority与文件边界

Reader与runtime materialization由`GalateaStrictConfigReader`、`GalateaConfigLoader`及
`GalateaConfigValidation`拥有；bootstrap writer由`GalateaConfigBootstrapper`与
`GalateaConfigTemplateFactory`拥有。`config.json`不是完整runtime配置：loader仍要求同目录的
Completion-owned `connections.json`，并从中解析exact default connection。

## 2. Strict JSON与version language

Root file必须是Linux no-follow regular file，长度为1 byte..1 MiB，JSON max depth为32。Accepted language为：

- exact一个JSON object；strict UTF-8，无BOM、comment、trailing comma或trailing data；
- property order与whitespace不固定；合法escaped property name decode后按exact name处理；
- 每层unknown或wrong-case property拒绝；duplicate按decoded name的`OrdinalIgnoreCase`比较拒绝；
- `v`可位于任意property位置，但必须存在且raw token为exact integer `2`；V1、versionless、future、`null`、
  string、fraction或exponent form全部拒绝；
- source-generated materialization只发生在strict reader通过之后；`sessionProvisioning`的required/type/token
  acceptance由strict reader本身决定，不能依赖enum default。

## 3. Exact field language

### 3.1 Root object

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `v` | required number token | raw token exact `2` |
| `users` | required array，1..256 items | 每项服从§3.2；`userId`与resolved session path分别unique |
| `listenUrls` | optional；missing/`null`为runtime `null`；否则array 0..256 | item为nonblank string；duplicate与order保留；loader视为opaque string |
| `callLogDir` | optional；missing/`null`为disabled；否则string | nonblank；relative以config directory为base；runtime为absolute；existing components不得是symlink/reparse point；与每个resolved `sessionDir`双向non-nested |
| `maintenanceMode` | optional boolean | missing为`false`；`null`或非boolean拒绝 |
| `recapGrid` | required object | 服从§3.3；没有`null`或default fallback |

### 3.2 User object、prompt与session provisioning

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `userId` | required string | nonblank；所有user中exact ordinal unique |
| `password` | required string | nonblank；用于Galatea login validation |
| `sessionDir` | required string | nonblank；relative以config directory为base；absolute保持同一target；runtime只接收absolute path |
| `sessionProvisioning` | required string | closed exact token：`existing-only`或`create-if-missing`；无root/default fallback |
| `systemPrompt` | string；可missing | 没有有效`systemPromptFile`时必须nonblank；inline text除blank检查外保持原值 |
| `systemPromptFile` | optional string-or-null | missing/`null`/empty/whitespace视为absent；nonblank path必须读取成功并覆盖inline prompt |

`systemPromptFile` relative path以config directory为base。文件必须是1 byte..1 MiB no-follow regular file和strict
UTF-8；decode后执行`Trim()`且结果必须nonblank。有效文件允许inline `systemPrompt` missing或blank，但explicit
`systemPrompt:null`仍拒绝。

两种provisioning policy的行为为：

- `existing-only`：只打开已provision的raw SessionJournal repository；path missing、empty、incomplete或invalid时返回
  `session-unprovisioned`，不写入该path。
- `create-if-missing`：普通writable host在首次实际请求该user session且`sessionDir`完全不存在时调用
  `SessionJournalEngine.Create`。初始化使用default connection的`ModelId`、`CompletionSurfaceId`及该user最终resolved
  `SystemPrompt`，产生合法Idle raw repository。若path已有任何filesystem entry，则只走open/fail-closed路径；不会删除、
  overwrite、adopt或repair empty/incomplete/corrupt path。
- maintenance mode对两种policy都只允许read-only open，绝不create。
- login/authentication本身不provision；首次需要`GetSessionAsync`的authenticated session operation触发lazy initialization。
- auto-create仅涵盖raw SessionJournal。Timeline、Cadence、Control、Store、route/profile及任何RecapGrid asset仍必须由
  operator显式provision；raw-only recent view保持合法。
- failed lazy initialization会从in-process session cache精确移除，使operator修复后可在同一process重试；失败本身不会
  自动清理可能残留的filesystem state。

`sessionDir`没有process-CWD或existence-based path fallback，也没有repository move。Absolute path与`..`仍可进入
platform lexical normalization；本合同不承诺config-directory confinement或完整hostile-filesystem defense。两个user
resolve到同一normalized lexical session path时，在session/client/log side effect前拒绝。

### 3.3 `recapGrid` object与owned dependencies

| Field | Required shape | Load-time rule |
|:--|:--|:--|
| `routeManifestPath` | required nonblank string | relative以config directory为base；root load只resolve并拒绝existing symlink/reparse components，不要求route file存在 |
| `agentControlProfileFiles` | required array，1..256 strings | item nonblank；relative以config directory为base；resolved path按platform comparer unique；file必须存在并eager strict decode |
| `currentAgentControlProfileId` | required nonblank string | exact匹配一个已加载profile ID |

Route manifest仍延迟到首次RecapGrid work读取，没有wildcard/default route fallback。Root V2不改变Route或profile的
owner-defined V1 language。

## 4. Bootstrap与migration

Current root bootstrap template写exact numeric `v:2`，为`alice`与`bob`都显式写
`sessionProvisioning:"create-if-missing"`，使用`sessions/alice|bob`相对路径。Bootstrap只生成缺失的root或sibling
connections template；它不创建SessionJournal repository、不验证provider，也不provision RecapGrid sidecar。

若root file已经存在，bootstrap不会添加policy、升级version、修改password或按template重写。Operator从V1升级时必须
停服、备份、确认实际`Galatea:ConfigPath`，为每个user明确选择policy，再把version改为2；应用不会自动迁移。

## 5. Bounds、classification与non-promises

V2保留V1的root 1 MiB、users 1..256、`listenUrls` 0..256、profile paths 1..256、prompt 1 MiB、profile
128 KiB与deferred Route 1 MiB bounds。Syntax/type/version/unknown/duplicate、invalid UTF-8及invalid
`sessionProvisioning` field language归类为`InvalidDataException`；blank、zero lower bound、duplicate identity/path、
dependency mismatch等semantic failure归类为`InvalidOperationException`。Underlying IO/path/permission与owner-local
dependency exception可以传播；diagnostic逐字文本不是machine contract。

本合同不承诺password encryption、secret-store integration、bootstrap file permissions、Kestrel deployment、provider
readiness、automatic config rewrite/migration、session repair/move、RecapGrid auto-provision或path confinement。Connections、
Route、profile、HTTP/SSE及SessionJournal durable wire不会因root version升到V2而同步改版。

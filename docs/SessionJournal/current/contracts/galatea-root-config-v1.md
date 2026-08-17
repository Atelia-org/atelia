# Galatea root config V1 candidate

状态：**post-tag approval candidate；尚未批准**  
适用product source：`8c450bf03f58cb62753d8b3732e66adae36b1809`  
不属于immutable tag：`session-journal-contract-r2-approved-surfaces-v1`

本文整理Galatea `config.json` current V1的accepted language、path semantics与composition dependencies。
它是root-config field language的审阅入口，不改变Completion connections、Route manifest或AgentControl profile
各自owner的协议，也不把root config提升为Stable V1。

## 1. Authority、文件与提交边界

Reader与runtime materialization由
[`GalateaStrictConfigReader`](../../../../prototypes/Galatea/GalateaStrictConfigReader.cs)、
[`GalateaConfigLoader`](../../../../prototypes/Galatea/GalateaServices.cs)及
[`GalateaConfigValidation`](../../../../prototypes/Galatea/GalateaConfig.cs)拥有；bootstrap writer由同一Galatea
assembly中的`GalateaConfigBootstrapper`与`GalateaConfigTemplateFactory`拥有。本文不复制可执行parser，也不建立
第二个config DTO。

| Commit | Candidate delta |
|:--|:--|
| `0f0afb2c` | relative `sessionDir`改为config-directory-relative并向runtime交付absolute path；absolute target保持；bootstrap template改为`sessions/*` |
| `319bd425` | 将path cut登记为post-tag candidate，明确不扩大surface-set-1 tag |
| `0515083f` | 新增test-owned handwritten full/minimal V1、required/optional/count/path/prompt/dependency/strict-byte field-language gates |
| `8c450bf0` | 将JSON materialization `JsonException`归类为`InvalidDataException`，补empty/count/blank/missing-file/prompt-precedence与invalid-UTF8分类tails |

Field-language oracle位于
[`GalateaRootConfigFieldLanguageTests`](../../../../tests/Galatea.Server.Tests/GalateaRootConfigFieldLanguageTests.cs)，
path/bootstrap vertical位于
[`GalateaConfigValidationTests`](../../../../tests/Galatea.Server.Tests/GalateaConfigValidationTests.cs)。

`config.json`不是完整runtime配置：loader还要求同目录的Completion-owned `connections.json`。后者属于已批准的
Completion connections V1，root config不会吸收其endpoint、secret或default-connection fields。

## 2. Strict JSON与version language

Root file必须是Linux no-follow regular file，长度为1 byte..1 MiB，JSON max depth为32。accepted language是：

- exact一个JSON object；strict UTF-8，无BOM、comment、trailing comma或trailing data；
- property order与JSON whitespace不固定；合法escaped property name在decode后按其exact name处理；
- 每层unknown或wrong-case property拒绝；duplicate按decoded name的`OrdinalIgnoreCase`比较拒绝；
- `v`可出现在任意位置，但必须存在且value token的raw bytes是exact integer `1`；missing、`null`、string、
  `0`、`2`、`1.0`或`1e0`均拒绝；
- string escape只服从strict JSON；除下表明确列出的semantic rule外，没有额外的root-wide canonical re-encode要求。

因此V1 reader不是canonical-JSON reader。两个semantic-equivalent document可因property order、whitespace或escape方式
不同而具有不同bytes，但都被接受；root bytes也不进入SessionJournal durable identity。

## 3. Exact field language

本节所有path field除各自semantic rule外，还必须通过underlying Linux/.NET lexical path operations；不能由
`Path.GetFullPath`表示的输入（例如包含NUL）不属于accepted language。本文不会把这些platform rules复制成另一套
portable path grammar。

### 3.1 Root object

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `v` | required number token | raw token exact `1` |
| `users` | required array，1..256 items | 每项服从§3.2；`userId`与resolved session path必须分别unique |
| `listenUrls` | optional；missing/`null`为runtime `null`；否则array 0..256 | item必须是nonblank string；duplicate与order保留；loader把内容视为opaque string |
| `callLogDir` | optional；missing/`null`为disabled；否则string | nonblank；relative以config directory为base，runtime为absolute；existing components不得是symlink/reparse point；必须与每个resolved `sessionDir`双向non-nested |
| `maintenanceMode` | optional boolean | missing为`false`；explicit `null`或非boolean拒绝 |
| `recapGrid` | required object | 服从§3.3；没有`null`/default fallback |

`users` missing/`null`/wrong type/empty均失败；256为inclusive maximum，257在materialization前拒绝。`userId`
identity是exact ordinal，因此`alice`与`Alice`可同时存在；session path的lexical comparer则服从platform path comparer。

### 3.2 User object与prompt precedence

| Field | Required shape | Semantic rule |
|:--|:--|:--|
| `userId` | required string | nonblank；所有user中exact ordinal unique |
| `password` | required string | nonblank；只用于Galatea login validation |
| `sessionDir` | required string | nonblank；relative以config directory为base，absolute保持同一target；runtime只接收absolute path |
| `systemPrompt` | string；可missing | 没有有效`systemPromptFile`时必须nonblank；inline text除blank检查外保持原值 |
| `systemPromptFile` | optional string-or-null | missing/`null`/empty/whitespace视为absent；nonblank path必须成功读取，并覆盖inline prompt |

`systemPromptFile` relative path以config directory为base。文件必须是1 byte..1 MiB no-follow regular file和strict
UTF-8；decode后执行`Trim()`，结果必须nonblank。有效文件允许inline `systemPrompt` missing或blank，但explicit
`systemPrompt:null`仍是invalid JSON materialization。file missing、empty、invalid UTF-8或trim后blank均失败，不回退inline。

`sessionDir`只改变lexical base：没有process-CWD或existence-based fallback，不自动create/move repository；absolute与
`..`仍合法，也不承诺config-directory confinement。不同配置文本在resolve/normalize后指向同一lexical session path
会被拒绝，避免一个runtime中两个user共享同一repository owner。

### 3.3 `recapGrid` object与owned dependencies

| Field | Required shape | Load-time rule |
|:--|:--|:--|
| `routeManifestPath` | required nonblank string | relative以config directory为base；root load只resolve并拒绝existing symlink/reparse components，不要求route file已存在 |
| `agentControlProfileFiles` | required array，1..256 strings | 每项nonblank，relative以config directory为base；resolved path按platform comparer unique；file必须存在并eager strict decode |
| `currentAgentControlProfileId` | required nonblank string | 必须exact匹配一个已加载profile的ID |

每个profile file必须是1 byte..128 KiB no-follow regular file，并服从已批准AgentControl profile V1的canonical
language；distinct paths decode出的profiles还必须形成owner-valid registry，`ProfileId`与`RuntimeIdentity`分别exact
unique。Registry identity duplicate由current loader传播`ArgumentException`，但其message逐字文本不构成本candidate。
root appendix不复制或扩张profile协议。Route manifest延迟到首次RecapGrid work读取；届时file必须是
1 byte..1 MiB并服从已批准Route manifest V1。route不存在不会阻止root load，但会使需要route的后续work失败；
没有wildcard/default route fallback。

## 4. Bootstrap writer与existing-file policy

Current bootstrap只在root或sibling connections file缺失时生成template。Root template：

- 使用exact numeric `v:1`、两个placeholder users与`sessions/alice|bob`；
- UTF-8 no BOM，current writer在document后追加single LF；
- 使用indented JSON与`UnsafeRelaxedJsonEscaping`，但这些bytes、property order、indentation和single-LF formatting不是
  canonical compatibility promise；authority仍是§2 reader language；
- 不创建session repository，不验证provider；root中的placeholder password与sibling connections template中的
  model/endpoint都不是可部署配置。

若existing root存在，bootstrap不会添加field、迁移version、修改password或按template重写它；versionless/future/invalid
root仍由loader fail closed。没有dual reader、auto rewrite或silent migration。

## 5. Bounds、classification与operator action

| Boundary | Current V1 value |
|:--|:--|
| root `config.json` | 1 byte..1 MiB；max depth 32 |
| users | 1..256 |
| `listenUrls` | 0..256 when present as array |
| AgentControl profile paths | 1..256 |
| each `systemPromptFile` | 1 byte..1 MiB |
| each AgentControl profile file | 1 byte..128 KiB |
| deferred Route manifest file | 1 byte..1 MiB |

Syntax/type/version/unknown/duplicate-property、BOM、invalid UTF-8、comment、trailing comma/data，以及source-generated
JSON materialization failure均归类为`InvalidDataException`；materialization failure保留`JsonException`作为inner，
diagnostic不回显具体field value。missing required semantic field、blank、zero lower bound、duplicate user/session、
current-profile mismatch或call-log nesting等为`InvalidOperationException`；missing config/dependency file为
`FileNotFoundException`。这些exception types描述current loader分类，不把message逐字文本冻结为machine protocol。

上述分类只覆盖root reader/loader拥有的syntax、materialization与semantic cases。Underlying path/IO/permission failure
以及owned profile registry拒绝可能传播.NET或owner-local exception（current duplicate identity为`ArgumentException`）；
本candidate既不统一包装这些低层failure，也不把其exact type或message提升为稳定classification contract。

Operator升级必须停服、备份并确认实际`Galatea:ConfigPath`；需要改变relative-path target时显式改成目标absolute或
config-relative value。应用不会自动移动repository或重写existing config。Provider/deployment acceptance必须另跑，
不能由provider-free loader/tests推导。

## 6. Explicit non-promises

本candidate不批准、也不承诺：

- password或其他secret的at-rest encryption/hashing、redaction、secret-store integration；
- bootstrap生成文件的Unix mode、ownership、ACL或`0600`强制；operator必须单独管理permissions；
- `listenUrls`的URI syntax、Kestrel endpoint parsing/binding、port availability、TLS或network exposure；loader只锁opaque
  nonblank list与count；
- bootstrap JSON的byte identity、indentation、property order、escaping、whitespace或newline formatting；
- exception/diagnostic message逐字文本、provider construction/content quality、real deployment readiness或ignored
  operator state；
- automatic config rewrite、version migration、session create/move、CWD fallback、path confinement或完整hostile-filesystem
  defense；
- 把root fields、connections、Route或AgentControl profile合并成superset schema，或为旧language保留dual reader。

批准本appendix必须是后续显式user decision；测试通过、文档合入或既有surface-set-1 tag都不能自动提升其状态。

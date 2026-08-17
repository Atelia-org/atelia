# SessionJournal Contract R2 candidate

状态：R5 candidate；code/rebuild gates complete、docs closure Pending；**未声明任何tier stable/frozen，未创建tag**  
source candidate：`a77ed16c1ddef949dc519811fde56600db38316e`  
记录日期：2026-08-17

本文是current SessionJournal、HistoryTimeline与RecapGrid contract的候选Shape/Rule入口。它把明确支持的
.NET role、raw/companion/operational wire与upgrade policy放在同一张地图中，但只有
[R5 candidate evidence](../../evidence/contract-freeze-r2-r5-candidate.md)记录的final gates完成、独立复核通过，
且用户明确批准具体tier后，才可以改变本文状态或创建tag。

源码、strict codec、tests和goldens仍是实现事实；本文不把所有CLR `public`、human diagnostic文本、provider行为、
ignored operator state或历史candidate自动升级为兼容承诺。

## 1. Authority与tier边界

1. raw EventJournal events与selected `RefId` Parent lineage是会话历史和recovery的authority。
2. HistoryTimeline、Cadence、Control和Grid Store是有独立identity/head/fence的companion state；它们可显式
   reprovision，但不能反向覆盖或猜测raw authority。
3. Prepared/Started recovery服从已冻结的setup、origin、execution、tool/runtime、target与exact input proof，
   不能被active config或derived availability改写。
4. CLI report、HTTP/SSE display、telemetry与call log是operational output，不是raw、provider invocation或
   recovery authority。
5. 本candidate按四tier分别裁决，批准一个tier不自动批准其他tier：

| Tier | Surface | Candidate policy |
|:--|:--|:--|
| A | raw SessionJournal event/recovery wire | 最高兼容风险；R2无wire cut，未来breaking change必须另有migration/recovery plan |
| B | Timeline/Cadence/Control/Store/Rewriter companion wire | pre-release hard cut；unsupported/legacy state显式拒绝或reprovision，不dual-read/silent migrate |
| C | config、CLI JSON、Galatea HTTP/SSE | versioned direct cut；code、tracked first-party client与operator manifest按各自部署边界成对迁移 |
| D | S/T/O/C/G/H public .NET support roles | 只承诺本节明确role；`public`本身不构成support或binary compatibility承诺 |

strict versioning与旧格式拒绝只说明hard-cut policy清晰，不等于backward compatibility。

## 2. Tier D support-role map

### 2.1 Exact compiled inventory

inventory口径是effective-public type，以及其declared-only public/protected/protected-internal logical API member。
construction inventory另清点visible constructor、`init`/`set`与record clone。下表由exact source candidate的
isolated tracked archive生成；复现方法、tool identity与完整hash见
[R5 evidence §3](../../evidence/contract-freeze-r2-r5-candidate.md#3-current-public-inventory)。

| Assembly | Alias | Types | API rows | API SHA-256 | Construction lines | Construction SHA-256 |
|:--|:--:|--:|--:|:--|--:|:--|
| `Atelia.SessionJournal` | S | 162 | 1,358 | `f1a24eac3142c6ddc8e97418127d8ad4b908866c35b9730d8c9df10db6d42018` | 379 | `8cbf9ba4f803260bb1acc117b9fbe00f1613655c53cd9ab30176838606753d59` |
| `Atelia.SessionJournal.HistoryTimeline` | T | 227 | 2,592 | `8f257a497b890555d9c71c50c7eee19a285001f6c2e2e6d324e43bf6d58ca320` | 568 | `8b07fc60fc1ae7c28c4580ca2f3f491846454c3b2e24d6a402f49eb52d9df6f3` |
| `Atelia.SessionJournal.HistoryTimeline.O200k` | O | 1 | 4 | `35b5c4c62b37807b8d8211bcbe40177a6d97f76559a5f677ad02edb67e57465a` | 1 | `c80c0478817fc961faf7c994c7e6a0ec6c1f0cc7de853bfd0e37de2877580d52` |
| `Atelia.SessionJournal.RecapGrid.Cadence` | C | 76 | 827 | `f0ab6567b5c8f3e4013107e93311ed94c4683151530c9e5b85f019b5ea7f274a` | 182 | `f886260ad094bde84848c634fe710917766cd0aedadbce486e35edc8026119a3` |
| `Atelia.SessionJournal.RecapGrid` | G | 415 | 4,417 | `efde4a41f2c0f6cc8d77a441083d8a04d9fcb20f830be164b2b5fe15b6625452` | 941 | `5b0b146c432deb88bed2b4889314a16419b82a156da3f4e3e46b793392c96c84` |
| `Atelia.SessionJournal.RecapGrid.Hosting` | H | 20 | 221 | `53e3d95687bb9e0f856017c2673ad790a909ac4546b3894018a7f3ab127aa907` | 52 | `bfd51fb2eb4ca10c0f12bdee73fa2063b3bd3a011ec4d2086edd6ed3dce21f9a` |
| **总计** |  | **901** | **9,419** | per-assembly | **2,123** | per-assembly |

相对R0为`+10 types / -17 API rows / -48 construction lines`。count/hash用于精确识别candidate，不能把下表
未列出的export自动解释为stable role。

### 2.2 Supported roles与non-promises

| Owner | Candidate supported roles | Explicit non-promise / internal boundary |
|:--|:--|:--|
| S | engine create/open/runtime binding；read-only/offline/audit；legacy import；typed recovery、bounded planning/recent/pop input与readable result | test hook、arbitrary raw payload escape、owner-issued internal body、result record synthesis不是support目标 |
| T | Timeline create/open/read/coordinator；policy与partition input；descriptor/canonical codec；maintenance/operator result；estimator seam | SQLite/path/syscall helper、test persistence hook、`BoundHistorySegmentRange`与`HistorySegmentDescriptorFactory` owner-local assembly |
| O | fixed O200k estimator implementation与stable estimator ID | tokenizer/renderer implementation detail |
| C | cadence policy/head、factory/coordinator、reserve/seal与maintenance result | durable syscall/codec helper及owner-issued state construction |
| G | Abstractions consumer input/value；Control/Store/Manager/Getter/Runtime/Online/AgentControl owner APIs；明确external implementer seams | source-module mechanics、diagnostic/test shape；Manager/Getter/Online output可读但argument construction/mutation不承诺；不建立cross-owner generic result family |
| H | first-party route/config composition、Host lifetime/factory、telemetry snapshot read；standalone injectable `IRecapCompletionTelemetry`/collector | duplicate syntax reader、lazy registry与host-owned mutable collector identity/materialization state |

普通consumer可以依赖上表role中的public inputs、factories、handles、readable results与documented external implementer
seams；不得仅因reflection发现exported symbol，就假设其constructor、record clone、diagnostic text或assembly-qualified
identity是冻结承诺。candidate批准前仍要求consumer clean rebuild；批准后的breaking support-role变化必须形成新的
candidate与显式policy，不通过compatibility wrapper或普通consumer `InternalsVisibleTo`掩盖。

## 3. Tier A raw/recovery wire inventory

raw payload使用strict `{v,body}` envelope；unknown kind/version/field、duplicate、wrong type、非法null、retired ID、
off-lineage address、Parent/setup/hash drift均fail closed。

| Kind | Numeric ID | Body version |
|:--|--:|--:|
| `RuntimeConfigSetup` | 1 | 2 |
| `SystemPromptSetup` | 2 | 1 |
| `SessionCreated` | 3 | 2 |
| `ObservationAccepted` | 4 | 1 |
| `AgentActionProduced` | 5 | 1 |
| `ToolExecutionStarted` | 6 | 1 |
| `ToolResultObserved` | 7 | 1 |
| `CompletionRequestPrepared` | 8 | 5 |
| `CompletionAttemptFailed` | 9 | 2 |
| `ImportedAgentAction` | 10 | 1 |
| `CompletionAttemptStarted` | 13 | 1 |

ID 11 retired。Prepared exact inputs最多128；artifact context snapshot最多4 MiB。`CompletionAttemptStarted`是
uncertain external dispatch的durable phase proof；`ImportedAgentAction`虽复用action body shape，但lineage/origin
不同。`EventAddress`文本是`ej1:`加32个lowercase hex；filename codec独立。R2没有删除或重编号raw字段/ID，
也不以legacy export的逻辑可重建性承诺physical RBF bytes deterministic。

owner入口是[`SessionEventCodec`](../../../../prototypes/SessionJournal/SessionEventCodec.cs)、
[`SessionRequestManifestCodec`](../../../../prototypes/SessionJournal/SessionRequestManifestCodec.cs)及current
reconstructor/recovery tests。未来Tier A breaking change必须先给出raw-preserving migration、full phase replay/reopen
与recovery evidence；companion reprovision不能替代这项义务。

## 4. Tier B companion wire inventory

| Artifact | Exact slot / version | Owner proof、bounds与accepted language | Failure / upgrade policy |
|:--|:--|:--|:--|
| History locator | `derived/history-timeline/v2/refs/<ref>/locator.json`；JSON v1 | 1..4,096 bytes、canonical exact；path Ref、Timeline ID与generation绑定active DB/ABA | invalid/non-v1不fallback；旧v1 root inert；显式重建V2 domains |
| History ledger | locator sibling SQLite；application id `0x41544854`、Schema V2 | exact pragma、6 tables+6 triggers、metadata scope/head hash/counts、canonical policy/row、selected path与Merkle；normal open bounded、maintenance full verify | unsupported typed；backup/restore/reprovision；existing create在同一exclusive lease内验证head与active policy后才`AlreadyExists` |
| Cadence | `control/recap-grid/v1/.../cadence.json`；`atelia.session-journal.recap-grid.cadence.v1` | 2..4,096 bytes、canonical exact；Ref/generation/domain digest与expected Timeline policy；fd-relative publish | stale/busy/invalid/indeterminate typed；不fallback到Timeline或active config猜测 |
| Control | layout v1 `control.json`；content `schemaVersion=2` | 2 bytes..32 MiB、strict whole JSON；whole head/state digest、canonical closure、bootstrap、definitions/recipes与receipts | strict non-V2 discriminator typed Unsupported；V2 malformed Invalid；backup/restore/reinitialize，无silent migration |
| Grid Store | `derived/recap-grid/v1/grid.sqlite`；application id `0x41544752`、Schema V2 | exact pragma/catalog、single metadata identity、canonical payload+indexed columns/FK/counts；transactional writes与physical reset witness | future version优先typed Unsupported；V2 corruption Invalid/Unhealthy；explicit reset/reprovision，不auto-repair |
| Rewriter | IDs durable inControl Family/Definition；runtime/output v3，input/prior/history v1 | 五个独立protocol轴在route/dispatch前exact preflight；provider output仍是Completion block shape | 任一mismatch为`ProtocolUnavailable`且provider call为0；不合并为suite ID或兼容grammar |

History/Store metadata version、head/digest/count、indexed columns等是corruption/scope/query proof，不是待删双authority。
`a77ed16c`只让一份owner-local `SchemaEntry[]`驱动History create+verify；test-owned independent fingerprint保持外部
oracle，Schema V2与accepted language不变。

Tier B hard cut的部署规则是显式provision Cadence、Timeline、Control、Store四域；不得把旧Timeline locator/head与
current companion state拼成混合generation。raw append后的rollback必须raw-preserving，不能用旧backup覆盖新经历。

## 5. Tier C operational wire inventory

| Surface | Current candidate language | Boundary / compatibility policy |
|:--|:--|:--|
| Route manifest | canonical numeric `v:1`；1 MiB / 4,096 entries；unknown/missing/duplicate/noncanonical reject | operator routing policy，无secret，不是durable semantic identity；old language不兼容读取 |
| Completion connections | Completion-owned numeric `v:1`；1 MiB、depth 8、1..256 entries；id/env 128 UTF-8 bytes、endpoint 4 KiB、secret 64 KiB；wire endpoint exactly-one、API key at-most-one | strict syntax与owner-local path/no-follow分层；secret/resolved value不进report/fingerprint；code+manifest停服成对迁移，无dual reader |
| AgentControl profile | canonical `v:1`；bounded profile/admission bytes | operator profile且参与durable runtime fingerprint；unknown/version mismatch fail closed |
| Galatea root config | root raw integer `v:1`；1 MiB、最多256 users；profile files 1..256；strict duplicate/unknown/case/version/path rules；bootstrap no BOM | existing file不自动重写；停服迁移，no-version/future拒绝；users/routes/secrets不并入connections superset |
| RecapGrid CLI JSON | `atelia.session-journal.recap-grid-cli.v1`、`{schema,command,status,detail}`、16 MiB final report；Store page 128 items / 2 MiB | machine workflow contract；old Store-specific envelope不再emit；human stdout/stderr/help与逐字diagnostic不冻结 |
| Other reports | offline validation v2、legacy import v1、desired setup v1、history-load v1、legacy-root v2 | 各自versioned operator artifact；不因外层相似抽generic envelope |
| Galatea HTTP | complete group `/api/v1`；old `/api/*` exact 404；strict endpoint-local JSON，body 1 MiB；original/normalized message各64 KiB；typed status/success/error | server与cache-busted browser原子共部署；不保留route alias/redirect/dual DTO；breaking change需新candidate/path policy |
| Galatea SSE | `status`、`reasoning-delta`、`text-delta`、`done`、`error`；strict UTF-8/LF与exact terminal；4 MiB preview + 5 MiB terminal = 9 MiB whole replay，最多16,384 events；subscriber 256 refs；browser 9 MiB connection/5 MiB frame | cap hit只internal suppress preview，不取消provider/durable；`done {recent:null}`后HTTP reconciliation；无Last-Event-ID、ack、heartbeat或dual grammar |

HTTP/SSE numeric budgets、terminal/reconciliation语义和first-party client在本candidate中是`Prototype locked`；只有用户
明确批准Tier C后才能提升为stable V1。ignored operator config与real provider并非本文可读取的tracked contract；
R5 evidence把它们分别记录为`NotRun`，不得用历史local observation续期。

## 6. Cross-tier non-promises与变更纪律

- 本candidate不承诺arbitrary仓外consumer、assembly binary compatibility、physical SQLite/RBF byte-for-byte
  determinism、真实断电、hostile same-directory writer、provider exactly-once或内容质量。
- report、telemetry、call log、recent projection与SSE preview不能成为raw或durable completion的第二truth。
- future direct cut不得增加versionless fallback、dual reader/writer、compatibility wrapper、generic parser options、
  cross-owner result hierarchy或silent migration。
- 数值bound变更必须重新验证最终encoded bytes，不能只从inner payload cap纸面推导outer envelope安全。
- stable/frozen tier、tag名称及本机deployment readiness均不由本文自行宣布；批准边界与remaining gates见
  [R5 candidate evidence](../../evidence/contract-freeze-r2-r5-candidate.md)。

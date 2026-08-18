# SessionJournal Contract R2 — anchored v1-v6 surfaces and intentional Defer map

状态：**R2 normalization Complete / Closed / Stop**；approved surface sets 1 + 2 + 3 + 4 + 5 + 6 anchored；surface set 6 post-tag docs independent review PASS  
surface set 1 validated product source：`cd966fc7fddfa6acbda6f80431cf9b588177d969`  
surface set 2 validated product source：`8c450bf03f58cb62753d8b3732e66adae36b1809`；integration evidence：`6c5d3d50e68b84b9dca1391c16438a86cef418c1`  
surface set 3 production source：`da3aa27af56add07bc70229120c522b8d24c99ba`；contract test evidence：`8a54e613f7c1a92bab3a4dd0806aad19411c41b1`  
surface set 4 production/test source：`881afb39af511567b8bb900c5db103426791ab95`；candidate appendix：`2fa9808bfac0d8836da490548e9b3c98c38f2395`  
surface set 5 production/test source：`4e1e80e6875a3a963bd90c3845250da261548730`；candidate docs：`6ed308f0268d8e337753252aad0d2ad4f5039eb8`；promotion/gate candidate：`aebc4040370029bedb1ed46e26423f079cbe59a9`  
surface set 6 production/test source：`97ec7c1c6129b73062f9e46725c1fe3f2dcece92` + `e9dbf4aa0834418bea10c6fe98d379fb826e7829`；candidate docs/review tail：`d9fcc9db6e9cc160afdc085d0a7cece889d47269` + `c5b22d5230fe2b1889b3559dd05b64448594054c`  
approval anchors：immutable v1 `session-journal-contract-r2-approved-surfaces-v1`；immutable v2 `session-journal-contract-r2-approved-surfaces-v2`（tag object `13111f3d` → `c4c6dd16`）；immutable v3 `session-journal-contract-r2-approved-surfaces-v3`（tag object `511c5099` → `adf547e2`）；immutable v4 `session-journal-contract-r2-approved-surfaces-v4`（tag object `76dcdc70` → `0dac57a9`）；immutable v5 `session-journal-contract-r2-approved-surfaces-v5`（tag object `e1100017` → `89d61ba2`）；immutable v6 `session-journal-contract-r2-approved-surfaces-v6`（tag object `acc73dab` → `14b570cb`）  
记录日期：2026-08-19

本文是current SessionJournal、HistoryTimeline与RecapGrid contract的Shape/Rule入口。它把明确支持的
.NET role、raw/companion/operational wire与upgrade policy放在同一张地图中，但只有
[R5 candidate evidence](../../evidence/contract-freeze-r2-r5-candidate.md)记录了prior source `a77ed16c`的final gates；
[surface set 1 approval review](../../evidence/contract-freeze-r2-approval-review.md)又关闭了freeze-specific证据缺口和两个最窄
producer/reader问题，并为新source重新完成solution、Node、inventory与provider-free disposable rebuild。用户已于
2026-08-17批准本文件精确列出的surface set 1，随后又明确批准
[additive surface set 2](../../evidence/contract-freeze-r2-approval-surface-set-2.md)中的Store SQLite V2与Galatea root
config V1；annotated v2 tag已锚定exact approval ledger。用户之后又批准
[additive surface set 3](../../evidence/contract-freeze-r2-approval-surface-set-3.md)中的Desired Setup reconciliation
report V2 exact narrow scope；v3 unified gates已通过并由annotated tag锚定。用户现又批准
[additive surface set 4](../../evidence/contract-freeze-r2-approval-surface-set-4.md)中的HistoryLoad report V2 exact
top-level/read-only窄scope；unified gates与independent review已通过，并由annotated v4 tag锚定。用户现又批准
[additive surface set 5](../../evidence/contract-freeze-r2-approval-surface-set-5.md)中的Cadence `set-reserve` command-local
ledger/recovery窄scope；unified gates与final pre-tag review已通过，并由annotated v5 tag锚定。对post-tag commit
`845539c5`与actual v5 tag的independent review已PASS；本commit不反向移动tag、续期证据或扩大scope。除上述exact scope外的
surface继续作为显式`Defer` / non-promises，不能由任一tag顺带认证。用户又明确批准
[additive surface set 6](../../evidence/contract-freeze-r2-approval-surface-set-6.md)中的Offline Validation Report V3
producer-decoded/read-only/publication/retry/privacy/resource窄scope；fresh gates/rebuild与final pre-tag review已完成，并由
annotated v6 tag object `acc73dab`锚定reviewed ledger `14b570cb`。对post-tag review object `bbfd7823`与actual tag的
independent review已PASS；本tail不移动tag、不续期证据或扩大scope。

Contract Freeze R2已在surface set 6后有意停止；[closure evidence](../../evidence/contract-freeze-r2-closure.md)
记录六个exact tag anchors、Stop-after-V6理由、remaining matrix与reopen triggers。本文后续列出的`Defer`与
non-promises是R2的intentional boundary，不是尚待逐项处理的backlog或缺陷。

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
5. 本R2 contract按四tier分别裁决，批准一个tier不自动批准其他tier：

| Tier | Surface | R2 approval state / policy |
|:--|:--|:--|
| A | raw SessionJournal event/recovery wire | **Approved / Frozen R2 logical wire**；不含physical RBF bytes；未来breaking change必须另有version/migration/recovery plan |
| B | Timeline/Cadence/Control/Store/Rewriter companion wire | **Partial**：批准Rewriter五个exact protocol axes与Store SQLite V2 exact logical-schema sub-surface；History/Cadence/Control仍Defer并按显式reprovision/no-dual-reader政策演进 |
| C | config、CLI JSON、Galatea HTTP/SSE | **Partial Stable operational wire**：批准§5列出的Connections、Route、Profile、root config、HTTP/SSE、CLI outer+Store ledger，以及v3-anchored Desired Setup report V2、v4-anchored HistoryLoad report V2 top-level/read-only窄scope、v5-anchored Cadence set-reserve ledger/recovery与v6-anchored Offline Validation V3 exact窄scope；其余Defer |
| D | S/T/O/C/G/H public .NET support roles | **Approved Stable source-compatible**：只承诺§2.3 exact named roles；不承诺blanket export或binary ABI |

strict versioning与旧格式拒绝只说明hard-cut policy清晰，不等于backward compatibility。

## 2. Tier D support-role map

### 2.1 Exact compiled inventory

inventory口径是effective-public type，以及其declared-only public/protected/protected-internal logical API member。
construction inventory另清点visible constructor、`init`/`set`与record clone。下表已从current `cd966fc7`的
isolated tracked archive重新生成，两层byte stability均通过，且逐assembly counts/hashes与prior R5
`a77ed16c` byte-identical。复现方法、tool identity与current run见
[approval review](../../evidence/contract-freeze-r2-approval-review.md)；prior方法全文见
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

### 2.2 Candidate navigation map（非批准承诺）

下表帮助定位current owner API，但不是Tier D source-compatibility allowlist。只有§2.3明确列出的named roles已进入
surface set 1；下表其余大类继续是candidate navigation。

| Owner | Candidate navigation categories | Explicit non-promise / internal boundary |
|:--|:--|:--|
| S | engine create/open/runtime binding；desired-setup reconciliation；exact-head `SendAsync`/`ResumeAsync`；`ExecutePendingToolToBoundaryAsync`；`PrepareContextLifecycleMaintenanceAsync`；`AbandonFailedTurn`/`RewindLatestCompletedTurn`；read-only/offline/audit；legacy import；typed recovery、bounded planning/recent/pop；external `ICoherentContextCandidateSource`与`ISessionContextLifecycleCoordinator`及其合法output variants | test hook、arbitrary raw payload escape、owner-issued internal body、pending-boundary result construction、selected-lineage snapshot implementation、result record synthesis不是support目标 |
| T | Timeline create/open/read/coordinator；policy与partition input；descriptor/canonical codec；maintenance/operator result；external `IHistoryUnitLoadEstimator`与`HistoryUnitLoadMeasurement` construction | SQLite/path/syscall helper、test persistence hook、`BoundHistorySegmentRange`与`HistorySegmentDescriptorFactory` owner-local assembly |
| O | fixed O200k estimator implementation与stable estimator ID | tokenizer/renderer implementation detail |
| C | cadence policy/head、factory/coordinator、reserve/seal与maintenance result | durable syscall/codec helper及owner-issued state construction |
| G | Abstractions consumer input/value；Control/Store/Manager/Getter/Runtime/Online/AgentControl owner APIs；external `IRecapCellBatchExecutor`及全部ordinary outcome/batch results；external `IRecapCompletionInvoker`、`IRecapCompletionRouteResolver`、`IRecapCompletionTelemetry`及其必要input/output shape | source-module mechanics、diagnostic/test shape；Manager/Getter/Online owner-issued output的argument construction/mutation不承诺；supported telemetry flow由Runtime签发，external event construction不属于support promise；不建立cross-owner generic result family |
| H | first-party route/config composition、Host lifetime/factory、telemetry snapshot read；standalone injectable telemetry/collector；Host composition需要的`ICompletionClientFactory`、`ICompletionClient`与`CompletionConnectionConfig` named shape | 不把Completion assembly的其他export、duplicate syntax reader、lazy registry或host-owned mutable collector identity/materialization state升级为承诺 |

### 2.3 Approved stable exact named roles

| Owner | Exact role/member | Transitive public shape与oracle |
|:--|:--|:--|
| S | `ICoherentContextCandidateSource.SelectAsync/MaterializeAsync`；`ISessionContextLifecycleCoordinator.PrepareAsync` | selection七个current status、materialization `Materialized/Stale/Busy/Disposed/Invalid`、lifecycle `Ready/Backpressure/Unavailable/RawHistoryAuthorized`及其public request/descriptor/candidate/result shape；[nonfriend oracle](../../../../tests/SessionJournal.PublicSurface.Tests/SessionJournalNamedRoleTests.cs) |
| T/O | `IHistoryUnitLoadEstimator.Id/Measure`、`HistoryUnitLoadMeasurement`；`O200kBaseHistoryUnitLoadEstimator`与`EstimatorId` | external estimator可构造measurement，O assembly只export fixed estimator；[nonfriend oracle](../../../../tests/SessionJournal.HistoryTimeline.PublicSurface.Tests/HistoryTimelinePublicSurfaceTests.cs) |
| G Manager | `IRecapCellBatchExecutor.ExecuteAsync` | `RecapCellBatchExecutionResult.Completed/RejectedBeforeDispatch`与`RecapCellExecutionOutcome.Updated/KeepUnchanged/Failed/NotStartedDueToCallerCancellation`；[nonfriend oracle](../../../../tests/SessionJournal.RecapGrid.Manager.PublicSurface.Tests/ManagerPublicSurfaceTests.cs) |
| G Runtime | `IRecapCompletionRouteResolver.Resolve`、`IRecapCompletionInvoker.ProviderId/ApiSpecId/InvokeAsync`、`IRecapCompletionTelemetry.Record` | route resolution `Bound/Unavailable/Invalid`、minimal legal `CompletionResult`与telemetry event readable input；[nonfriend oracle](../../../../tests/SessionJournal.RecapGrid.Runtime.PublicSurface.Tests/PublicSurfaceTests.cs) |
| H + named Completion dependency | `ICompletionClientFactory.Create(CompletionConnectionConfig) -> ICompletionClient`；`RecapGridRuntimeHost.Create`、`RecapGridCompletionHost.Create`与其exact inspect/bind/snapshot flows | 只承诺linked oracle实际编译的named cross-assembly dependency，不承诺Completion assembly其余export；[nonfriend oracle](../../../../tests/SessionJournal.RecapGrid.Hosting.PublicSurface.Tests/PublicSurfaceTests.cs) |

普通consumer只能把本节的exact role/member及其linked transitive shape视为stable source-compatibility promise；§2.2的owner API
大类只是导航。不得仅因reflection发现exported symbol，就假设其constructor、record clone、diagnostic text或
assembly-qualified identity已冻结。consumer仍需clean build；breaking named-role变化
必须形成新的candidate与显式policy，不通过compatibility wrapper或普通consumer `InternalsVisibleTo`掩盖。

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

ID 11、12 retired，均不得重新分配。current identifier与commitment language是：

| Fact | Exact identifier / hash domain | Current owner |
|:--|:--|:--|
| trunk schema | `atelia.session-journal.trunk.v1` | [`SessionJournalDefaults.Schema`](../../../../prototypes/SessionJournal/SessionJournalContracts.cs) |
| Prepared recipe | `atelia.session-journal.coherent-artifact-tail.recipe.v1` | [`SessionRequestManifestDefaults`](../../../../prototypes/SessionJournal/SessionRequestManifest.cs) |
| canonical request codec | `atelia.completion-request.canonical-json.v1` | [`SessionRequestManifestDefaults`](../../../../prototypes/SessionJournal/SessionRequestManifest.cs)、[`SessionRequestCanonicalizer`](../../../../prototypes/SessionJournal/SessionRequestCanonicalizer.cs) |
| tool definition codec | `atelia.tool-definition.canonical-json.v1` | [`SessionRequestManifestDefaults`](../../../../prototypes/SessionJournal/SessionRequestManifest.cs)、[`SessionRequestCanonicalizer`](../../../../prototypes/SessionJournal/SessionRequestCanonicalizer.cs) |
| raw-range SHA-256 | `atelia.session-journal.raw-range.v1`；start-exclusive、end-inclusive与ordered parent-contiguous `(address,parent,event kind,body schema version,payload SHA-256)` | [`SessionRawRangeHasher`](../../../../prototypes/SessionJournal/SessionRawRangeHasher.cs) |
| artifact snapshot SHA-256 | `atelia.session-journal.artifact-context-snapshot.sha256.v1`；length-prefixed `systemPromptFragment`、`observationMessage`、`actionMessage` UTF-8 | [`SessionArtifactContextSnapshotHasher`](../../../../prototypes/SessionJournal/SessionArtifactContextSnapshotHasher.cs) |
| history semantic commitment | `atelia.session-journal.history-semantic-commitment.v1`；`history-message`、`tool-result`、`tool-results-contribution`、`history-contribution-sequence` subdomains | [`SessionHistorySemanticCommitment`](../../../../prototypes/SessionJournal/SessionHistorySemanticCommitment.cs) |
| context-contribution SHA-256 | `atelia.session-journal.context-contribution-text-sha256.v1` + NUL + exact UTF-8 text | [`SessionContextContributionHasher`](../../../../prototypes/SessionJournal/SessionContextCandidateContracts.cs) |

Prepared exact inputs最多128；artifact context snapshot最多4 MiB。`CompletionAttemptStarted`是
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
| History ledger | `derived/history-timeline/v2/refs/<ref>/timelines/<timelineId>.sqlite`；application id `0x41544854`、Schema V2 | exact pragma、6 tables+6 triggers、metadata scope/head hash/counts、canonical policy/row、selected path与Merkle；normal open bounded、maintenance full verify | unsupported typed；backup/restore/reprovision；existing create在同一exclusive lease内验证head与active policy后才`AlreadyExists` |
| Cadence | `control/recap-grid/v1/refs/<ref>/cadence/cadence.json`；`atelia.session-journal.recap-grid.cadence.v1` | 2..4,096 bytes、canonical exact；Ref/generation/domain digest与expected Timeline policy；fd-relative publish | stale/busy/invalid/indeterminate typed；不fallback到Timeline或active config猜测 |
| Control | `control/recap-grid/v1/refs/<ref>/timelines/<timelineId>/control.json`；content `schemaVersion=2` | 2 bytes..32 MiB、strict whole JSON；whole head/state digest、canonical closure、bootstrap、definitions/recipes与receipts | strict non-V2 discriminator typed Unsupported；V2 malformed Invalid；backup/restore/reinitialize，无silent migration |
| Grid Store | `derived/recap-grid/v1/grid.sqlite`；application id `0x41544752`、Schema V2 | **Approved / Frozen R2 logical-schema sub-surface**：[exact appendix](recap-grid-store-sqlite-v2.md)列persistent pragmas、5-table shape、metadata、canonical payload/bounds与indexed locator proof；不冻结physical SQLite bytes/layout | future version优先typed Unsupported；V2 corruption Invalid/Unhealthy；explicit reset/reprovision，不auto-repair；由immutable surface-set-2 tag锚定 |
| Rewriter | IDs durable in Control Family/Definition：runtime `text-runtime-v3`、output `atelia.recap.output.v3`、input `atelia.recap.input.v1`、prior `atelia.recap.prior.v1`、history `atelia.history.segment.v1` | **Approved / Frozen R2 sub-surface**：五个独立protocol轴在route/dispatch前exact preflight；provider renderer/output实现不在批准范围 | 任一mismatch为`ProtocolUnavailable`且provider call为0；不合并为suite ID或兼容grammar |

History/Store metadata version、head/digest/count、indexed columns等是corruption/scope/query proof，不是待删双authority。
`a77ed16c`只让一份owner-local `SchemaEntry[]`驱动History create+verify；test-owned independent fingerprint保持外部
oracle，Schema V2与accepted language不变。

Grid Store appendix是在immutable surface-set-1 tag之后形成，并已由用户明确批准进入additive surface set 2；该批准
不反向扩大或移动v1 tag，也不把SQLite physical bytes、runtime/source ID或未列出的connection policy纳入承诺。
surface-set-2 unified gates与docs review已通过；annotated v2 tag已锚定approval ledger commit `c4c6dd16`。

Control只有在完整strict JSON root的首字段是exact、unescaped `schemaVersion`，其值是plain Int32且不等于2，
并且整个top-level不存在duplicate或case-confusable property时才分类为`Unsupported`。malformed/truncated、
`schemaVersion`不在首位、wrong-case/escaped name、wrong type、fraction、exponent、Int32 overflow或任何top-level
duplicate/case-confusable均为`Invalid`，不能借future discriminator绕过strict corruption classification。

Tier B hard cut的部署规则是显式provision Cadence、Timeline、Control、Store四域；不得把旧Timeline locator/head与
current companion state拼成混合generation。raw append后的rollback必须raw-preserving，不能用旧backup覆盖新经历。

## 5. Tier C operational wire inventory

| Surface | Current approved/candidate language | Boundary / compatibility policy |
|:--|:--|:--|
| Route manifest | canonical numeric `v:1`；1 MiB / 4,096 entries；connection id strict UTF-8 128 bytes；concurrency 1..1,024；timeout 1 ms..1 day且整毫秒；maximum output tokens为null或positive；unknown/missing/duplicate/noncanonical reject | operator routing policy，无secret，不是durable semantic identity；old language不兼容读取；runtime/model/family identifiers服从各自owner而不被route的connection-id bound顺带覆盖 |
| Completion connections | Completion-owned numeric `v:1`；1 MiB、depth 8、1..256 entries；id/env 128 UTF-8 bytes、endpoint 4 KiB、secret 64 KiB；wire endpoint exactly-one、API key at-most-one | strict syntax与owner-local path/no-follow分层；resolved `BaseAddress`是non-secret且进入Completion durable connection fingerprint；API key（inline或env-resolved secret value）不进report/fingerprint；code+manifest停服成对迁移，无dual reader |
| AgentControl profile | canonical `v:1`；profile id strict UTF-8 128 bytes；profile最多128 KiB；admission inclusive 2..64 KiB；registry 1..256且profile id/runtime identity分别exact unique | admission canonical bytes进入durable tool runtime identity；profile id与whole profile bytes不进入该identity；unknown/version/order/duplicate mismatch fail closed；public admission producer不会生成owner decoder拒绝的bytes |
| Galatea root config | **Approved Stable V1**：[exact appendix](galatea-root-config-v1.md)锁required/optional/count、prompt precedence、config-directory-relative path与profile/route dependencies；root 1 MiB、prompt 1 MiB、profile 128 KiB；bootstrap no BOM/existing no-rewrite policy | product source `8c450bf0` + integration evidence `6c5d3d50`；无CWD/existence fallback、auto rewrite/move或confinement；由immutable surface-set-2 tag锚定；deployment/provider与appendix non-promises不在批准范围 |
| RecapGrid CLI JSON | `atelia.session-journal.recap-grid-cli.v1`、`{schema,command,status,detail}`、16 MiB final report；Store page 128 items / 2 MiB | outer envelope/fallback与Store `inspect/verify/export/reset` [status/detail/exit ledger](../../evidence/contract-freeze-r2-r1-priority-review.md#52-stable-detail-ledger)是freeze-ready machine contract；[Cadence `set-reserve` receipt](cadence-set-reserve-receipt.md) exact command-local ledger/recovery由surface set 5 immutable tag锚定；其他non-Store command detail/status仍按owner result为candidate，不因共享printer自动冻结；human stdout/stderr/help与逐字diagnostic不冻结 |
| Other reports | [offline validation V3 approved contract](offline-validation-report-v3.md)、legacy import v1、[desired setup reconciliation V2 approved contract](desired-setup-reconciliation-report-v2.md)、[history-load V2 approved top-level contract](history-load-report-v2.md)、legacy-root v2 | **Partial**：desired-setup V2由v3 tag锚定；history-load V2 top-level/read-only由v4 tag锚定；offline validation V3 exact 25-field/nested/closed-token/read-only/publication/retry/privacy/resource scope由v6 tag锚定；其余report继续Defer；不因外层相似抽generic envelope |
| Galatea HTTP | complete group `/api/v1`；old `/api/*` exact 404；strict endpoint-local JSON，body 1 MiB；original/normalized message各64 KiB；typed status/success/error | server与cache-busted browser原子共部署；不保留route alias/redirect/dual DTO；breaking change需新candidate/path policy |
| Galatea SSE | `status`、`reasoning-delta`、`text-delta`、`done`、`error`；strict UTF-8/LF与exact terminal；4 MiB preview + 5 MiB terminal = 9 MiB whole replay，最多16,384 events；subscriber 256 refs；browser 9 MiB connection/5 MiB frame | cap hit只internal suppress preview，不取消provider/durable；`done {recent:null}`后HTTP reconciliation；closed error codes与exact payload见[tracked SSE ledger](../../../../prototypes/Galatea/README.md#sse-v1-stable-protocol)；无Last-Event-ID、ack、heartbeat或dual grammar |

Connections、Route manifest、AgentControl profile、Galatea HTTP/SSE与CLI outer+Store ledger是surface set 1中批准的
Stable V1；[root config V1](galatea-root-config-v1.md)由additive surface set 2批准为Stable V1；
[Desired Setup report V2](desired-setup-reconciliation-report-v2.md)的exact narrow receipt/recovery scope由additive
surface set 3批准、通过unified gates并由v3 tag锚定。[HistoryLoad report V2](history-load-report-v2.md)的exact
top-level/read-only scope已获surface set 4用户批准、通过unified gates/review并由v4 tag锚定。其他reports、
HistoryLoad nested shape与非Store CLI detail/status仍是candidate/Defer。root批准
不引入dual interpretation，不把absolute/`..`改成非法或把repository
限制在config目录内；也不承诺password at rest、permissions、Kestrel、diagnostic、provider或deployment readiness。
ignored operator config与real provider并非本文可读取的tracked contract；不得用历史local observation冒充current
deployment或provider gate。

[HistoryLoad report V2](history-load-report-v2.md)形成于immutable surface-set-3 tag之后；它直接materialize full
`ReadHistoryPlanningWindow()`的units/boundaries，没有final byte cap、pagination或stable oversize semantics，只能作为
offline operator contract。Surface set 4只批准decoded exact 11-field/types/meanings、V1字段删除与read-only
publication/retry semantics；nested exact shape与bounded resource policy不在批准范围。该授权与本文件都不反向扩大v3 tag。

[Cadence `set-reserve` approved receipt contract](cadence-set-reserve-receipt.md)形成于immutable surface-set-4 tag之后；
surface set 5只批准updated/unchanged minimal receipt、closed failure details与fresh-inspect recovery rule，不批准owner
result、generic outer envelope、Cadence durable V1或receipt atomicity。该授权不会反向扩大v4 tag；v5 tag object
`e1100017`已锚定reviewed ledger `89d61ba2`。本post-tag docs commit不移动tag、不续期证据或扩大scope。

[Offline validation report V3 approved contract](offline-validation-report-v3.md)形成于immutable surface-set-5 tag之后。Current
producer exact输出25个root fields、current nested shapes、7个phase与11个event-kind closed tokens，并执行read-only full
selected-lineage audit；work、memory、cumulative payload与final JSON都没有production cap。用户已批准
[surface set 6 addendum](../../evidence/contract-freeze-r2-approval-surface-set-6.md)圈定的exact scope；gates/rebuild与final
pre-tag review已完成，annotated v6 tag object `acc73dab`已锚定reviewed ledger `14b570cb`。该approved surface不属于surface
set 5；existing v1-v5 tags与historical V2/R5 evidence未移动、重释或续期。Rebuild PASS不批准physical bytes或current
operator/provider/deployment state。对post-tag review object `bbfd7823`与actual tag的independent review已PASS；本tail不反向
移动tag、不续期证据或扩大scope。

HTTP普通`{code,error}`中的existing machine code保持含义，但code namespace允许additive新值，first-party client必须有
unknown fallback；`turn-busy`专用shape与SSE error code集合仍是closed。`error`逐字文本、property order、login HTML、
cookie实现与bootstrap不是stable network contract。SSE subscriber channel字面容量256是tracked operational bound，
stable可观察语义是slow subscriber可单独断开、无不可靠in-band overflow code、不停止turn/provider/durable processing，
并可从bounded whole-turn replay重连。

## 6. Cross-tier non-promises与变更纪律

- 本R2 contract不承诺arbitrary仓外consumer、assembly binary compatibility、physical SQLite/RBF byte-for-byte
  determinism、真实断电、hostile same-directory writer、provider exactly-once或内容质量。
- report、telemetry、call log、recent projection与SSE preview不能成为raw或durable completion的第二truth。
- future direct cut不得增加versionless fallback、dual reader/writer、compatibility wrapper、generic parser options、
  cross-owner result hierarchy或silent migration。
- 数值bound变更必须重新验证最终encoded bytes，不能只从inner payload cap纸面推导outer envelope安全。
- surface sets 1与2分别由immutable v1/v2 tags锚定；v2 tag exact object为`13111f3d`，dereferenced target为
  `c4c6dd16`。Surface set 3由immutable v3 tag object `511c5099`锚定到`adf547e2`，不移动或重释v1/v2。未列出的surface、
  本机deployment readiness与real-provider readiness仍不在批准范围。Surface set 4由immutable v4 tag object
  `76dcdc70`锚定到`0dac57a9`，不移动或重释v1/v2/v3；exact additive范围见
  [surface set 2 addendum](../../evidence/contract-freeze-r2-approval-surface-set-2.md)与
  [surface set 3 addendum](../../evidence/contract-freeze-r2-approval-surface-set-3.md)、
  [surface set 4 addendum](../../evidence/contract-freeze-r2-approval-surface-set-4.md)。Surface set 5由immutable v5 tag
  `session-journal-contract-r2-approved-surfaces-v5` object `e1100017`锚定到reviewed ledger `89d61ba2`；exact范围见
  [surface set 5 addendum](../../evidence/contract-freeze-r2-approval-surface-set-5.md)。V5不移动或重释v1-v4；post-tag docs
  commit不移动tag、不续期证据或扩大scope。

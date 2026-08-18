# SessionJournal Contract Freeze R2 — additive surface set 6 approval

状态：**user approval recorded；unified gates与provider-free rebuild complete；tag-ready；independent final pre-tag review与annotated tag Pending**  
production source：`97ec7c1c6129b73062f9e46725c1fe3f2dcece92`  
provider-zero test tail：`e9dbf4aa0834418bea10c6fe98d379fb826e7829`  
candidate appendix/consumer docs：`d9fcc9db6e9cc160afdc085d0a7cece889d47269`  
candidate review tail：`c5b22d5230fe2b1889b3559dd05b64448594054c`  
promotion docs / unified gate candidate：`a2aa4d3ddc84993fbb24f27402b25990b84ac5ac`  
authorized tag：`session-journal-contract-r2-approved-surfaces-v6`（尚未创建）  
记录日期：2026-08-19

本文只记录用户在immutable surface sets 1至5之上新增批准的SessionJournal Offline Validation Report V3
producer-decoded contract与operator boundary。Surface set 6完整继承既有批准与non-promises，但不替换、移动、重释或
续期v1-v5 tags；它也不把Offline public CLR surface、raw/durable wire、physical repository或通用CLI report提升为新承诺。

## 1. Approved additive surface

Surface set 6只新增下列producer-only Tier C operational report contract：

1. `SessionJournal.Cli validate --report-json`成功输出JSON object；`schema` exact为
   `atelia.session-journal.offline-validation.v3`。Decoded root恰有下列25个case-sensitive names，types按组exact：
   - string：`schema`、`repositoryPath`、`branchName`、`branchRefId`、`executionPhase`、
     `systemPromptUtf8Sha256CodecId`、`historySemanticCommitmentCodecId`、`historySemanticCommitmentSha256`；
   - string or null：`head`、`headKind`、`runtimeConfigSetup`、`systemPromptSetup`、
     `systemPromptUtf8Sha256`；
   - object or null：`runtimeConfig`；
   - integer：`eventCount`、`logicalPayloadBytes`、`toolExecutionSequenceCheckpoint`、`preparedRequestCount`、
     `observationCount`、`agentActionCount`、`importedAgentActionCount`、`toolResultHistoryCount`、
     `historyContributionCount`；
   - array：`eventKindCounts`；object：`scanDiagnostics`。
2. 上述25个fields的exact meanings由
   [Offline V3 appendix §1](../current/contracts/offline-validation-report-v3.md#1-producerschema与exact-root-shape)
   的table定义：它们表达resolved absolute input path、selected branch/ref、captured head与checked lineage totals、
   captured execution/governing setup、prompt/history commitments、event/history counts及scan diagnostics。Report是本次
   captured-head witness，不是branch随后推进后的continuous authority。
3. Current nested decoded producer shapes也进入批准范围：
   - `runtimeConfig`为null或exact
     `{modelId:string,completionSurfaceId:string,schema:string,derivedContext:object}`，其中`derivedContext` exact为
     `{nthPrevious:integer}`；
   - `eventKindCounts[]` element exact为`{kind:string,count:integer}`，只表达selected lineage中实际出现kind的count；
     array index/order不承担kind identity；
   - `scanDiagnostics` exact为
     `{capturedEventCount:integer,repositoryEventReadCount:integer,indexedHeaderLookupCount:integer,
     indexedEventLookupCount:integer,decodedPayloadBytes:integer,preparedReconstructionCount:integer}`。
4. `executionPhase`只允许exact 7 tokens：`empty`、`idle`、`awaiting-agent-action`、
   `awaiting-completion-dispatch`、`awaiting-completion`、`awaiting-tool-execution`、`turn-failed`。
   `headKind`与`eventKindCounts[].kind`只允许exact 11 tokens：`runtime-config-setup`、
   `system-prompt-setup`、`session-created`、`observation-accepted`、`agent-action-produced`、
   `tool-execution-started`、`tool-result-observed`、`completion-request-prepared`、
   `completion-attempt-failed`、`imported-agent-action`、`completion-attempt-started`。Numeric enum values、enum names、
   wrong-case与unknown/future tokens不属于V3。
5. Validator以read-only engine检查完整selected Parent lineage，重建historical Prepared并把forward fold与captured-head
   tail/governing setup做differential。成功report只证明该次captured head；它不append raw、修改branch ref或写derived
   owners。
6. CLI先完成audit，再以same-directory unique temporary file serialize、`Flush(true)`并replace-capable move到target。
   Production writer允许overwrite；create-only只是tracked runbook排除stale receipt的operator precondition。Unknown/future
   raw/body、corruption、differential或serialization failure不会publish一个新的valid V3 receipt；existing target仍可能是旧
   receipt。失败后必须使用fresh output重新read-only validate，并以新captured head/ref/phase为准。
7. Full audit的work、memory、cumulative decoded payload与final encoded JSON没有production bound、pagination/cursor或
   stable oversize result。Report不含message/tool/prompt正文、raw arguments、operation/correlation IDs、provider response
   或secret，但包含absolute path、model/surface、addresses、hashes、counts与diagnostics，因而不是content-free。

Exact meanings与consumer rule以
[Offline validation report V3 appendix](../current/contracts/offline-validation-report-v3.md)为准。批准的是上述decoded
producer contract、read-only captured-head、publication/fail-closed/retry、privacy与resource boundary，不是report、raw、
governing setup或recovery之间的新authority。

## 2. Explicit non-promises

Surface set 6明确不批准：

- public CLR source/binary compatibility、blanket public API、record ABI或assembly metadata identity。三个public properties
  新增observable `JsonConverterAttribute`与`ReportSchema` V3是intentional hard cut；只如实记录constructor/property typed
  signatures、record equality与clone shape未改，不把它提升为未来兼容承诺；
- JSON property/array order、whitespace、escaping、terminal newline、canonical bytes、serializer option identity或byte
  identity；
- supported whole-document V3 reader、unknown-field tolerance、V2 reader/compatibility、dual reader/writer、schema negotiation
  或generic Other-report framework；
- bounded work/memory/cumulative payload/final bytes、pagination、cursor、truncation、oversize status/exit或online latency；
- raw event/durable companion schema、physical RBF/SQLite/repository bytes、migration/rebuild authority或report-as-raw truth；
- 其他reports、RecapGrid/SessionJournal CLI envelope/inputs/status/diagnostics、stdout/stderr或filesystem exception taxonomy；
- directory fsync、cross-filesystem durability、permissions/ownership、hostile-directory defense、report/raw atomicity或
  create-only writer behavior；
- current operator/ignored state、provider behavior、deployment readiness、content quality或任何未列出的future field。

Historical R5、Offline V2与surface sets 1至5 evidence保持各自original scope；本授权不重释其counts、values、status或
compatibility claims。

## 3. Evidence、unified gates与provider-free rebuild

- `97ec7c1c`是V3 producer、serialization hard cut与owning tests的source pin；`e9dbf4aa`补齐future raw/body failure的
  provider construction/calls zero gate。
- `d9fcc9db`是appendix与tracked G2A consumer gate pin；`c5b22d52`关闭post-setup equality fail-closed与observable public
  serialization metadata表述两项review findings。
- 本promotion只修改docs，不修改production、tests、operator state或tag。
- `a2aa4d3d`是exact clean promotion docs与本轮unified gate/rebuild source pin；pre-promotion draft independent review已PASS。
- Public inventory为**NotRun / 无需**：existing six-assembly inventory没有type/member delta；Offline observable serialization
  metadata由external wire/reflection tests覆盖。这不产生§2排除的CLR compatibility承诺。
- Ignored config、actual target repository、real provider与deployment均**NotRun**且不属于本promotion gate。

### 3.1 Fresh unified gate ledger at `a2aa4d3d`

下列是本轮fresh结果，不是implementation package或surface sets 1至5旧counts的复制：

| Gate | Result |
|:--|:--|
| `SessionJournal.Offline.Tests` full | 11 passed / 0 failed / 0 skipped |
| `SessionJournal.Cli.Tests` full | 116 passed / 0 failed / 0 skipped |
| `GalateaRecapGridCompositionTests` focused | 15 passed / 15 total |
| `dotnet test Atelia.sln --no-restore -m:1 -nr:false` | 38 projects / 4,702 passed / 0 failed / 0 skipped |
| `dotnet build Atelia.sln --no-restore -m:1 -nr:false` | 0 warnings / 0 errors；MSBuild 19.73s，wall 20.16s |
| Galatea production HTTP Node contract suite | 1 passed / 0 failed；220 ms |
| Galatea production SSE Node contract suite | 1 passed / 0 failed；223 ms |
| scoped SessionJournal docs checker | 18 files / 0 diagnostics |
| candidate diff/status/tag preflight | clean；v1-v5 targets unchanged；v6 tag absent |
| public inventory | NotRun / 无需；six-assembly type/member inventory无delta，metadata hard cut由wire/reflection tests覆盖 |
| ignored config / actual repo / real provider / deployment | NotRun；不属于本promotion gate |

### 3.2 Provider-free disposable rebuild at `a2aa4d3d`

Rebuild使用fresh run root `/tmp/atelia-ap-v6-rebuild.Ngc4Eg`；summary SHA-256为
`019c602db6db4ae978355e70a6fd70dbdb72ee5432f0189c89e037f29c2e8b5f`，harness SHA-256为
`25b700f6c0582b6d29486edcfb911fd3ba486bcf73a4a7ce108732727da1e482`，CLI SHA-256为
`5a5d68063243f095e8e001076765f36cd103ade665d5e9097842950402bb5502`。Harness pin的source是
`a2aa4d3ddc84993fbb24f27402b25990b84ac5ac`。

- 两次fresh import的normalized outputs exact equal；A、B、post-derived与final四份V3 normalized reports exact equal。
  Report均为schema V3、exact 25 root names/types、string enums、7 phase/11 kind closed token language，numeric enum未出现；
- tracked runbook helper从pinned markdown exact抽取，并对上述4份reports全部PASS；没有复制或重写second parser；
- fixture facts为148 events、474,498 logical payload bytes、71 observations、71 actions、142 history contributions、
  Prepared 0、phase `idle`、head kind `imported-agent-action`、5个observed kind rows；
- 三个raw checkpoints exact，四份operator assets exact；scaffold/init/sync/provision/compose/put与四owner gates均green；
  repeat-init及standalone Timeline create的13-file snapshot exact；
- harness在`bwrap --unshare-net`内运行，actual root masked，只bind neutral single export；21次product command stderr均为空，
  provider/call-log artifacts为0，calibrations为0，product semantic failures为0。没有in-process factory counter，因此这里不把
  artifact/network evidence夸大成独立provider call-count proof；`e9dbf4aa` owning test仍是future raw/body failure的factory/call
  zero gate。

Disposable rebuild证明candidate在该fresh fixture上的reconstructability与tracked V3 consumer闭合，不批准physical bytes、
current operator state、unbounded work上限或跨版本migration authority。

因此当前candidate的unified gates与provider-free rebuild已经完成，状态为**tag-ready**；independent final pre-tag review与
annotated tag仍Pending，所以还不是tagged/anchored completion。

## 4. Immutable anchors与tag-before checklist

Immutable prior dereferenced targets继续为：

- v1 `6378cebbde4cf150ecb4d8de5699ef1f77ce4f0b`；
- v2 `c4c6dd1698c7460fbf8ff3563d7800203f3202e0`；
- v3 `adf547e2a2319fd3009a7015a4289ab875af43f7`；
- v4 `0dac57a9e32ae5d0367394404524404689dfa4ef`；
- v5 `89d61ba2c561d84eed235ee196b24d2016ecd3ff`。

创建annotated tag前checklist：

1. **Complete**：exact clean `a2aa4d3d`上的fresh owning tests、full solution test/build、production HTTP/SSE Node suites、
   scoped docs/diff/status gates已记录于§3.1；
2. **Complete**：provider-free disposable rebuild已按§3.2核对V3 report、tracked consumer与raw/repository invariants；
   ignored operator data、actual target与real provider未运行；
3. **Pending**：由independent reviewer核对本addendum、current appendix/contract、routers、active plan、owner guide与runbook没有扩大§1，
   且historical evidence与v1-v5 tags未重释；
4. **Pending final review ledger**：再次确认v1-v5 dereferenced targets仍为上列commits，
   `session-journal-contract-r2-approved-surfaces-v6`仍不存在，并记录
   reviewed final gate ledger；
5. **Pending tag**：annotated tag message同时pin production `97ec7c1c`、test tail `e9dbf4aa`、candidate docs `d9fcc9db`、review tail
   `c5b22d52`、promotion/final gate ledger、§1 exact scope与§2 non-promises；
6. **Pending post-tag closure**：tag创建后另做status docs commit；不得移动tag吸收post-tag文档。

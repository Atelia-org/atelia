# SessionJournal Contract Freeze R2 — additive surface set 6 approval

状态：**user approval recorded；promotion docs candidate；unified gates、provider-free rebuild、independent final review与annotated tag Pending**  
production source：`97ec7c1c6129b73062f9e46725c1fe3f2dcece92`  
provider-zero test tail：`e9dbf4aa0834418bea10c6fe98d379fb826e7829`  
candidate appendix/consumer docs：`d9fcc9db6e9cc160afdc085d0a7cece889d47269`  
candidate review tail：`c5b22d5230fe2b1889b3559dd05b64448594054c`  
authorized tag：`session-journal-contract-r2-approved-surfaces-v6`（尚未创建）  
记录日期：2026-08-18

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

## 3. Evidence与Pending boundary

- `97ec7c1c`是V3 producer、serialization hard cut与owning tests的source pin；`e9dbf4aa`补齐future raw/body failure的
  provider construction/calls zero gate。
- `d9fcc9db`是appendix与tracked G2A consumer gate pin；`c5b22d52`关闭post-setup equality fail-closed与observable public
  serialization metadata表述两项review findings。
- 本promotion只修改docs，不修改production、tests、operator state或tag。
- 本轮fresh owning/solution/build/Node/docs unified gates、provider-free disposable rebuild与independent final pre-tag review
  尚未运行或登记；不得复制implementation package或surface sets 1至5的旧counts作为本轮结果。
- Public inventory当前NotRun；若tag前运行，它只用于candidate identity/metadata delta核对，不产生§2排除的CLR compatibility
  承诺。Ignored operator state、real provider与deployment均NotRun且不属于本promotion gate。

因此当前状态只是**user-authorized promotion candidate**，不是tagged/anchored completion，也尚未通过本轮tag-ready
mechanical evidence。

## 4. Immutable anchors与tag-before checklist

Immutable prior dereferenced targets继续为：

- v1 `6378cebbde4cf150ecb4d8de5699ef1f77ce4f0b`；
- v2 `c4c6dd1698c7460fbf8ff3563d7800203f3202e0`；
- v3 `adf547e2a2319fd3009a7015a4289ab875af43f7`；
- v4 `0dac57a9e32ae5d0367394404524404689dfa4ef`；
- v5 `89d61ba2c561d84eed235ee196b24d2016ecd3ff`。

创建annotated tag前必须：

1. 在exact clean promotion HEAD上完成并记录fresh owning tests、full solution test/build、production HTTP/SSE Node suites、
   scoped docs/diff/status gates；
2. 完成provider-free disposable rebuild，核对V3 report、tracked consumer、raw/repository invariants与provider calls zero，
   不读取ignored operator data或运行real provider；
3. 由independent reviewer核对本addendum、current appendix/contract、routers、active plan、owner guide与runbook没有扩大§1，
   且historical evidence与v1-v5 tags未重释；
4. 确认v1-v5 dereferenced targets仍为上列commits，`session-journal-contract-r2-approved-surfaces-v6`仍不存在，并记录
   reviewed final gate ledger；
5. annotated tag message同时pin production `97ec7c1c`、test tail `e9dbf4aa`、candidate docs `d9fcc9db`、review tail
   `c5b22d52`、promotion/final gate ledger、§1 exact scope与§2 non-promises；
6. tag创建后另做post-tag status docs commit；不得移动tag吸收post-tag文档。

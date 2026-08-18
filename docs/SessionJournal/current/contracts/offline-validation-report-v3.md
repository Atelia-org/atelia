# SessionJournal offline validation report V3 candidate contract

状态：**Post-surface-set-5 candidate；approval Defer**  
production/test source：`97ec7c1c6129b73062f9e46725c1fe3f2dcece92` + `e9dbf4aa0834418bea10c6fe98d379fb826e7829`  
approval boundary：不属于immutable surface-set-5 tag，也不修改或续期v1-v5 anchors

本文定义`SessionJournal.Cli validate --report-json` current producer输出的V3 machine-readable report，以及tracked
activation runbook消费该report时必须遵守的fail-closed规则。它是read-only offline audit的captured-head witness，不是raw
authority、continuous readiness proof、generic CLI envelope或bounded service contract。Current source/tests仍是实现事实；本
appendix只是post-v5 approval candidate。

## 1. Producer、schema与exact root shape

Production report由
[`SessionJournalOfflineValidator`](../../../../prototypes/SessionJournal.Offline/SessionJournalOfflineValidator.cs)
构造，`SessionJournal.Cli validate`通过
[`CliIo.WriteJsonAtomically`](../../../../prototypes/SessionJournal.Cli/CliIo.cs)选择性publish。
Exact shape与closed-token oracle在
[`SessionJournalOfflineReportWireTests`](../../../../tests/SessionJournal.Offline.Tests/SessionJournalOfflineReportWireTests.cs)。

Root必须是JSON object，`schema`必须exact等于
`atelia.session-journal.offline-validation.v3`，且必须恰有下列25个decoded property names；missing、extra、wrong-case、
wrong-type或用旧numeric enum representation均不属于V3：

| Field | JSON type | Current exact meaning |
|:--|:--|:--|
| `schema` | string | exact V3 identifier |
| `repositoryPath` | string | validator以`Path.GetFullPath`解析的input repository absolute path |
| `branchName` | string | read-only engine本次open的exact selected branch name |
| `branchRefId` | string | captured selected branch RefId的hex text |
| `head` | string or null | checked audit scan捕获的exact selected-lineage head；empty branch为null |
| `eventCount` | integer | captured selected Parent lineage中的logical event数量 |
| `logicalPayloadBytes` | integer | captured selected lineage已checked/decoded的logical payload bytes累计值 |
| `executionPhase` | string | captured head处forward fold与authoritative tail resolver一致的closed phase token；见§2 |
| `headKind` | string or null | captured head event的closed kind token；empty branch为null；见§2 |
| `toolExecutionSequenceCheckpoint` | integer | captured execution state的last-issued tool sequence checkpoint |
| `runtimeConfigSetup` | string or null | captured head governing runtime-config setup address；empty branch为null |
| `systemPromptSetup` | string or null | captured head governing system-prompt setup address；empty branch为null |
| `runtimeConfig` | object or null | captured head governing runtime configuration；empty branch为null；shape见§3 |
| `systemPromptUtf8Sha256CodecId` | string | exact `atelia.utf8-text.sha256.v1` |
| `systemPromptUtf8Sha256` | string or null | lowercase hex `SHA256(UTF8(governing system prompt))`；empty branch为null |
| `preparedRequestCount` | integer | lineage中reconstructed `CompletionRequestPrepared` event数量 |
| `observationCount` | integer | lineage中`ObservationAccepted` event数量 |
| `agentActionCount` | integer | produced与imported agent actions总数 |
| `importedAgentActionCount` | integer | `agentActionCount`中`ImportedAgentAction`子集数量 |
| `toolResultHistoryCount` | integer | 已闭合、进入history的tool-result groups数量，不是raw tool-result event总数 |
| `historyContributionCount` | integer | Observation、Action及已闭合ToolResults写入semantic history commitment的contribution数量 |
| `historySemanticCommitmentCodecId` | string | exact `atelia.session-journal.history-semantic-commitment.v1` |
| `historySemanticCommitmentSha256` | string | 按lineage order计算的semantic history commitment lowercase SHA-256 hex |
| `eventKindCounts` | array | 只列实际出现kind的count rows；nested shape见§3 |
| `scanDiagnostics` | object | 本次checked audit scan的work diagnostics；nested shape见§3 |

V3是对旧report representation的intentional hard cut：producer只写V3，不提供V2/V3 dual writer、compat parser或schema
negotiation。Public DTO的CLR properties仍保留typed `SessionExecutionPhase` / `SessionEventKind` signatures；internal
property converters只定义这三个property positions的closed JSON representation，不新增public wire-authority type。

仓内没有supported whole-document V3 reader。Tests对public DTO做typed serializer round-trip是writer/metadata闭合gate，
不等于承诺任意`JsonSerializer.Deserialize` options、unknown-root-field policy或future compatibility language。

## 2. Closed phase与event-kind tokens

`executionPhase`只接受/生成下列7个exact lower-kebab strings：

| Token | Typed meaning |
|:--|:--|
| `empty` | `SessionExecutionPhase.Empty` |
| `idle` | `SessionExecutionPhase.Idle` |
| `awaiting-agent-action` | `SessionExecutionPhase.AwaitingAgentAction` |
| `awaiting-completion-dispatch` | `SessionExecutionPhase.AwaitingCompletionDispatch` |
| `awaiting-completion` | `SessionExecutionPhase.AwaitingCompletion` |
| `awaiting-tool-execution` | `SessionExecutionPhase.AwaitingToolExecution` |
| `turn-failed` | `SessionExecutionPhase.TurnFailed` |

`headKind`与`eventKindCounts[].kind`共享下列11个exact lower-kebab strings：

| Token | Typed meaning |
|:--|:--|
| `runtime-config-setup` | `SessionEventKind.RuntimeConfigSetup` |
| `system-prompt-setup` | `SessionEventKind.SystemPromptSetup` |
| `session-created` | `SessionEventKind.SessionCreated` |
| `observation-accepted` | `SessionEventKind.ObservationAccepted` |
| `agent-action-produced` | `SessionEventKind.AgentActionProduced` |
| `tool-execution-started` | `SessionEventKind.ToolExecutionStarted` |
| `tool-result-observed` | `SessionEventKind.ToolResultObserved` |
| `completion-request-prepared` | `SessionEventKind.CompletionRequestPrepared` |
| `completion-attempt-failed` | `SessionEventKind.CompletionAttemptFailed` |
| `imported-agent-action` | `SessionEventKind.ImportedAgentAction` |
| `completion-attempt-started` | `SessionEventKind.CompletionAttemptStarted` |

Numeric values（包括旧`1`表示Idle）、enum names、wrong-case、unknown/future token与null `executionPhase`均不是V3。
`headKind`只有empty branch可以为null；future typed enum无法serialize为V3。

## 3. Current nested producer shapes

下列是current V3 producer的decoded nested shape。Tracked consumer只需gate root exact shape与其实际读取的Idle/head/ref
facts，不应把nested property order或array order复制成隐式compat contract：

- `runtimeConfig`为null，或exact object
  `{modelId:string, completionSurfaceId:string, schema:string, derivedContext:object}`；`derivedContext`为exact
  `{nthPrevious:integer}`。这里的`schema`是governing runtime configuration schema，不是report schema；
- `eventKindCounts[]` element为exact `{kind:string, count:integer}`；`kind`服从§2 closed language，`count`是该kind在
  selected lineage中的出现次数。Current producer只emit observed kinds，并按raw numeric kind ID排列；consumer不得把
  array index当kind identity；
- `scanDiagnostics`为exact
  `{capturedEventCount:integer, repositoryEventReadCount:integer, indexedHeaderLookupCount:integer,
  indexedEventLookupCount:integer, decodedPayloadBytes:integer, preparedReconstructionCount:integer}`。这些是本次audit
  work observations，不是repository authority、resource budget或performance SLA。

V3 candidate记录这些nested names/types/meanings以避免producer与tracked consumer漂移；它不承诺JSON property declaration
order、whitespace、escaping、terminal newline、byte identity，亦不承诺`eventKindCounts`或其他array的serialization order。

## 4. Read-only captured-head与resource boundary

Validator以`SessionJournalEngine.OpenReadOnly`打开selected branch，并消费`ScanCheckedAuditEvents()`的完整selected Parent
lineage。Scan检查header/codec/parent与body schema，forward fold重建所有historical Prepared commitments并与captured-head
tail execution state和governing setup做differential。成功report只证明这些checks在该次captured head上一致；branch随后
推进时，旧report不会自动成为current witness。

Audit不会append raw、修改branch ref或写derived owners。它会遍历并decode完整selected lineage、保存full fold state与
semantic contribution hashes，并重建每个Prepared request。Current operation没有header/event/payload/work/memory budget、
pagination/cursor、final encoded JSON byte cap或stable oversize result/exit。因此它是显式offline/full-audit action，不能
进入online request path、continuous readiness loop或以文档声称bounded。

## 5. Publication、failure与operator recovery

`validate --report-json`先完成read-only audit，再在report sibling directory创建unique temporary file，serialize V3，
`Flush(flushToDisk: true)`后用replace-capable move发布到target leaf。Production writer允许overwrite existing target；
runbook要求fresh absent path只是防止stale receipt的operator precondition，不是writer的create-only guarantee。该边界也不
承诺directory fsync、cross-filesystem durability、permissions/ownership或hostile-directory defense。

下列失败必须fail closed：

- unknown/future raw event kind、future body schema、malformed/corrupt lineage或differential mismatch在成功V3 report前失败；
- future typed phase/kind在temporary serialization阶段抛出，不得发布一个V3-looking numeric/fallback token；
- report publication失败不会修改raw repository，但existing target可能仍是旧receipt，不得当成本轮成功输出；
- 失败后只可用fresh output path重新运行read-only validation，并从新report读取new captured head/ref/phase；若raw被外部actor
  或更早command推进，应以current raw authority为准，不能把旧report或旧head盲重试。

Tracked G2A runbook在读取`.branchRefId`、`.head`或`.executionPhase`前，必须验证exact V3 schema/root field set、root
types与`executionPhase == "idle"`。该runbook gate不冻结nested ordering/bytes，也不证明本轮deployment或provider Passed。

## 6. Privacy与explicit non-promises

Report不包含message/tool/system-prompt正文、raw tool arguments、operation/correlation IDs、pending body、Completion endpoint、
API key、provider response或call log。但它包含absolute repository path、branch/ref/head、model/surface/runtime schema、setup
addresses、prompt/history hashes、event/history counts与scan diagnostics。因此它**不是content-free**，会泄露identity、
equality、规模、结构与变化，应按operational metadata处理。

本candidate不承诺：

- V1/V2 compatibility、dual reader/writer、supported whole-document deserializer或unknown-field tolerance；
- bounded work/memory/cumulative payload/final bytes、pagination/truncation、oversize status或online latency；
- JSON property order、array order、whitespace、escaping、terminal newline、canonical bytes或byte identity；
- stdout/stderr、complete CLI input/path language、exception/diagnostic逐字文本或所有failure exit分类；
- atomic raw/report transaction、report create-only、file permissions/ownership、directory durability或secret protection；
- report作为raw、governing setup、recovery或continuous readiness authority；
- current ignored operator state、provider/deployment success或任何未列入本文的future field。

Historical R5/V2 evidence与immutable v1-v5 approval tags保持原样。本文形成于v5 tag之后，不属于surface set 5；是否批准
V3 exact producer/report boundary须另行review与用户裁决。

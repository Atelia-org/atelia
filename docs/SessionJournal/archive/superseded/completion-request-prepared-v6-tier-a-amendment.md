# CompletionRequestPrepared v6 Tier-A amendment — withdrawn historical candidate

状态：**Historical / Superseded / Withdrawn — 用户于2026-08-20撤回；`1d8c33bb`已回滚；Gate B Canceled / Never Granted；promotion Never Started；旧B2 Canceled / Never Authorized**  
withdrawal disposition：历史`83477c06` implementation/reviews仍是其exact candidate事实，但不认证current code、current authority或未来方案；旧Gate A已随撤回终止且不可续用  
proposal baseline：[MemoPod Galatea / SessionJournal integration plan](../../work/active/memo-pod-galatea-integration-plan.md)，locked at `d5a403c4`  
frozen antecedent：[SessionJournal Contract R2](../../current/contracts/session-journal-contract-r2.md)、
[R2 closure](../../evidence/contract-freeze-r2-closure.md)  
记录日期：2026-08-20

本文曾是 post-R2、SessionJournal-owner 的 Tier-A raw/recovery wire amendment Candidate。它历史上定义
`CompletionRequestPrepared` v5/v6 split-write、dual-read、reconstruction 与 deployment boundary；Gate A授权的B1
product/tests曾实现并形成[candidate evidence](../../evidence/completion-request-prepared-v6-candidate.md)，随后由用户撤回并在
`1d8c33bb`回滚。本文现在只作历史设计输入，不是active Candidate、current contract、approval evidence、migration
runbook、deployment approval或tag authority。

Gate A 已于 2026-08-20 由用户以原文显式授权：`授权 Gate A：按 Prepared v6 Tier-A Candidate 实施 WP-07B B1。`
该历史授权只曾允许按本Candidate修改§7 product与§8.1 tests；用户后续撤回使它终止，不得被未来工作包续用。
Gate B从未授予，promotion从未开始，旧B2从未授权。rollback后current code与current approved Tier-A authority均为
Prepared v5/recipe v1/count `0..128`；任何新方向都必须从fresh Candidate、fresh review和fresh user gates重新开始。

## 1. Why this candidate exists

### 1.1 Owner and consumer

- Tier-A owner 是 `Atelia.SessionJournal`：`SessionEventCodec`、`SessionRequestManifestCodec`、
  `SessionPreparedRequestReconstructor` 与 recovery/audit/offline readers 共同拥有 raw Prepared accepted language。
- tracked first-party consumer 是 Galatea 的 MemoPod main-request integration。它需要把一次 turn-level supplemental
  selection 固化进 Prepared，使 Prepared/Started recovery 与 tool continuation 都不重新访问 MemoPod 或 recall provider。
- MemoPod、Galatea 或 RecapGrid 都不成为 raw codec owner；SessionJournal product 不新增对这些 assembly 的引用。

### 1.2 Post-R2 reopen rationale

current R2 Tier A 只批准 `CompletionRequestPrepared` body v5、recipe v1 与 `ExactContextInputs` count `0..128`。
Enabled MemoPod 需要 mandatory terminal input、recipe v2、count ceiling 129 和 dual-version recovery reader；这些都是
accepted language 与 retention/rollback policy 的变化，不能被解释为旧 v5 approval 的延伸。

本 tracked consumer 至少触发 R2 closure §4 的 first-party consumer 条件；proposed accepted-language expansion 与
deployed old-schema retention 又要求本 Candidate 显式裁决 closure 所列的 language/migration dimension，而不是假装 v5
approval 已覆盖。重新开启仅限本 amendment；R2 current contract、closure、approval evidence 与
`session-journal-contract-r2-approved-surfaces-v1` 至 `v6` tags 都保持 immutable，不移动、不续期、不重释。

## 2. Exact decision

writer 与 reader 只接受下列 pair：

| Request source / mode | Written body version | Recipe | Exact inputs | Terminal supplemental input |
|:--|:--:|:--|:--:|:--|
| Disabled initial request | 5 | `atelia.session-journal.coherent-artifact-tail.recipe.v1` | `0..128` | forbidden |
| Enabled initial request, NoMatch or Selected | 6 | `atelia.session-journal.coherent-artifact-tail-plus-supplemental.recipe.v2` | `1..129` | exactly one, mandatory, last |
| Tool continuation from v5/v1 source Prepared | 5 | recipe v1 | `0..128` | forbidden |
| Tool continuation from v6/v2 source Prepared | 6 | recipe v2 | `1..129` | exactly one, copied and revalidated |

Rules：

1. Disabled writer 必须继续产生 current v5/v1 exact canonical bytes；不得因 new reader 存在而升级或重写。
2. Enabled writer 只产生 v6/v2。NoMatch 也必须有 terminal control input，因此最小 count 是 1。
3. new reader 只 dual-read exact pairs `v5/v1`、`v6/v2`。`v5/v2`、`v6/v1`、Prepared v1-v4、v7+、
   unknown recipe、invalid count 与 invalid terminal shape 一律 fail closed。
4. 同一 journal 可以合法包含旧 turn 的 v5 与新 turn 的 v6。read、audit、offline validation、replay 与 reopen 必须按
   每个 event 的 actual body version 验证，不能把 journal 压成单一 global version。
5. existing v5 event 不 rewrite、不 migrate，也不在 read time 升级；本方案没有 background migration 或 compatibility copy。
6. v5-only old reader 读到 v6 时必须返回其现有 explicit `Unsupported` classification；不得把 v6 当 malformed v5、skip
   或 silently truncate。

## 3. Body and recipe grammar

### 3.1 Nine-field body remains exact

v6 body object 保持 v5 的同一组九个 exact fields，不新增 provenance、store、MemoPod、provider 或 migration field：

```text
origin
execution
plan
setups
parameters
toolSet
recipe
target
commitment
```

字段顺序、field-local grammar、unknown/missing/duplicate/reordered handling 与 canonical JSON rules 都沿用 v5；唯一
version-specific 差异是 recipe ID、`ExactContextInputs` partition/count、terminal envelope validation 和由此产生的
whole-request commitment。v6 不允许用 union parser 放宽 v5，v5 也不识别 v2 terminal input。

### 3.2 Exact input partition

```text
v5/v1:
  ExactContextInputs[0..count] = current Recap exact inputs
  count                        = 0..128

v6/v2:
  ExactContextInputs[0..^1] = current Recap exact inputs, count 0..128
  ExactContextInputs[^1]    = exactly one supplemental control input
  total count               = 1..129
```

The terminal input is one `SessionRequestArtifactContextSnapshot`：

```text
SystemPromptFragment = ""
ObservationMessage   = exact canonical supplemental control JSON
ActionMessage        = ""
```

The control schema ID is `atelia.session-journal.supplemental-context.control.v1`。

NoMatch exact bytes：

```json
{"schema":"atelia.session-journal.supplemental-context.control.v1","status":"no-match","observationContent":null}
```

Selected exact logical shape：

```json
{"schema":"atelia.session-journal.supplemental-context.control.v1","status":"selected","observationContent":"<exact provider-facing carrier>"}
```

The terminal control parser/renderer contract is：

- strict UTF-8, no BOM, no leading/trailing whitespace, no final LF；stored `ObservationMessage` 必须是完整 JSON；
- exact property order `schema,status,observationContent`；unknown、missing、duplicate、reordered、wrong-case fields 拒绝；
- `status` exact lowercase `no-match|selected`；NoMatch 必须为 `null`，Selected 必须为 non-empty string；
- encoder 对 `\" \\ \b \t \n \f \r` 使用 short escape；其余 C0、C1、U+2028、U+2029 使用 lowercase four-digit
  `\u` escape；其他 Unicode scalar 写 raw UTF-8；invalid UTF-16/UTF-8 拒绝；
- decoder materialize owned values 后用同一 fixed encoder re-encode，stored bytes 必须逐 byte 相等；
- parser、renderer 与 byte pre-count 必须使用相同 bounds，checked arithmetic；overflow、overbound、partial input 拒绝，
  不以 truncation、replacement character 或 normalization 修复。

## 4. Hash, numeric and reconstruction authority

### 4.1 Hash and numeric rules

- body version 与 count 用 existing canonical integer language；不接受 fraction、exponent、sign alias、leading zero、
  overflow 或 stringified number。
- terminal snapshot 的 `ContentSha256` 必须由 current
  `SessionArtifactContextSnapshotHasher.ComputeSha256` 对三个 exact owned strings 计算；不得建立 Memo-specific hash domain。
- `SessionRawRangeHasher` 必须继续把每个 raw event 的 **actual body schema version** 纳入 range commitment；mixed journal
  不能把 v6 降格成 v5 后再 hash。
- final request 使用 unchanged canonical request codec；Prepared `commitment.byteLength` 与 `commitment.sha256` 必须对
  reconstructed exact request 重新计算并 exact match。
- existing v5 numeric、UTF-8、artifact-size 与 request-size bounds 不变。每个 artifact context snapshot 的 existing hard
  cap 仍是 `4,194,304` UTF-8 bytes，并完整覆盖 terminal 的三个 strings；v6 只把 exact input count ceiling 提升到 129。
  它不暗示 provider token、wire-size、aggregate-manifest 或 memory budget 已扩大；final canonical-request cap 仍服从
  runtime 的 existing configured policy。

### 4.2 Reconstruction

v5/v1 完全沿用 current reconstruction。v6/v2 固定执行：

1. 按 actual body v6 验证 recipe v2、count `1..129` 和 terminal position；
2. strict parse terminal snapshot 与 canonical control JSON，并重算 snapshot hash；
3. 对 preceding `0..128` inputs 执行 unchanged v1 Recap aggregate/expand；
4. 得到 base system prompt 与 `0..2` 条 Recap header messages；
5. Selected 时 append exactly one `ObservationMessage(observationContent)`；NoMatch 时 append nothing；
6. append dependency-closed raw suffix，再创建 empty-tail `CompletionRequest`；
7. canonicalize whole request 并验证 Prepared byte length/SHA-256 commitment。

provider-facing prefix order 因而固定为 Recap Observation（若有）→ Recap Action（若有）→ supplemental Observation（仅
Selected）→ dependency-closed raw suffix。control envelope 自身不发给 provider。

### 4.3 Authority

- raw EventJournal events 与 selected `RefId` Parent lineage 继续是 history/recovery authority。
- Frozen MemoPod 是 current memo ID/text authority；Prepared inline terminal snapshot 只是一个 exact request 的 execution
  authority。它不是 current Memo authority，也不允许 recovery 回查、校正或重新选择 Memo。
- 就 supplemental selection 而言，Prepared/Started recovery、audit/replay 与 tool continuation 只消费 durable Prepared
  pair/input；source、Pod open、client construction 与 recall dispatch count 必须为 0。exact request reconstruction 仍须读取
  并验证 raw Parent lineage/range 与 Prepared pinned setup references，不得把 Prepared copy 提升为 raw/setup authority。
- a crash before Prepared commit may recall again；一旦 Prepared committed，recovery 必须 exact reconstruct，不得重新 recall。

## 5. Tool continuation inheritance

pair 是 initial Prepared 固化的 turn-level execution input，而不是每次 request 从 active config 重选：

- v5/v1 source 永远继续写 v5/v1，即使 config 后来 Enabled；不得中途 recall 或升级；
- v6/v2 source 永远继续写 v6/v2，即使 config/Pod 后来 Disabled、移动或编辑；
- each continuation copies and strict-revalidates the source terminal snapshot；不得重新 render current Memo text；
- `SourcePrepared` missing 时不得猜测 historical selection。Disabled imported branch 保持 v5/v1；enabled marker 的 imported
  branch fail closed，且两者都不访问 Pod/client/recall。

## 6. Deployment, mixed journals and rollback

1. First deployment 必须同时包含 v5/v6 dual reader 与 split writer；不得先部署 v6 writer。
2. v6 write 前的 repository 保持 v5-only binary compatible；v6 write 后，old reader 的 explicit `Unsupported` 是 expected。
3. historical candidate contract曾把mixed v5/v6 journals定义为受支持数据，且`83477c06` reader曾支持其
   backup/restore、audit、offline validation、replay与reopen并保留actual bytes/order/version；rollback后的current code
   不支持v6，这不是current data-support claim。
4. no rewrite/migration means no operator job, startup scan or read-time promotion modifies existing v5。
5. first v6 write 后 rollback 只能到仍含 exact dual reader 的 build。回滚到 v5-only binary 不受支持；不得靠删除 v6 raw
   events、覆盖 old backup、skip unknown event 或重写 history 恢复。

## 7. Exact B1 product write scope

Gate A曾于2026-08-20获得用户显式授权，但已随用户撤回而终止，当前不再允许任何B1 production mutation；下列scope仅为
historical record：

- new `prototypes/SessionJournal/SessionSupplementalContextContracts.cs`
- new `prototypes/SessionJournal/SessionSupplementalContextRecipe.cs`
- `prototypes/SessionJournal/SessionJournalContracts.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `prototypes/SessionJournal/SessionEventCodec.cs`
- `prototypes/SessionJournal/SessionRequestManifest.cs`
- `prototypes/SessionJournal/SessionRequestManifestCodec.cs`
- `prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs`

`SessionEventCodec.cs` is mandatory：only Prepared gains supported versions `{5,6}`；all other kinds remain single-version exact。
`SessionExecutionTailResolver.cs` 与 `SessionCoherentRequestRecipe.cs` 不是 default write scope。若 compile/accessibility 证据
要求抽取 pure helper，实施者必须先在 B1 change record 列出 exact file/reason，并证明 v1 bytes/render unchanged；不得顺带
重写 resolver、v1 recipe、RecapGrid 或 recovery algorithm。

final implementation使用下列closed public outcome family：

```csharp
public abstract class SessionSupplementalContextSelection {
    private SessionSupplementalContextSelection() { }

    public sealed class NoMatch : SessionSupplementalContextSelection { }

    public sealed class Selected : SessionSupplementalContextSelection {
        public Selected(string exactObservationContent) {
            // Exact null/empty/Unicode guards are part of the implementation.
            ExactObservationContent = exactObservationContent;
        }
        public string ExactObservationContent { get; }
    }
}
```

原plan中的`abstract record`经post-review hardening改为private-ctor abstract class，避免record合成protected copy
constructor形成外部派生入口。nested outcomes仍sealed、get-only、validated并保持同名engine pattern；这是封闭性收紧，
不增加success/failure状态，也不承诺record value equality。

## 8. Exact B1 test and evidence scope

### 8.1 Tests

- new `tests/SessionJournal.Tests/SessionSupplementalContextIntegrationTests.cs`
- new `tests/SessionJournal.Tests/PreparedV6Fixture.cs`
- `tests/SessionJournal.Tests/SessionEventCodecGoldenTests.cs`
- `tests/SessionJournal.Tests/SessionEventCodecStrictnessTests.cs`
- `tests/SessionJournal.Tests/SessionRequestManifestCodecTests.cs`
- `tests/SessionJournal.Tests/SessionPreparedRequestReconstructorTests.cs`
- `tests/SessionJournal.Tests/SessionPreparedCompletionRecoveryEngineTests.cs`
- `tests/SessionJournal.Tests/SessionContextCandidateProviderRouteTests.cs`
- `tests/SessionJournal.Tests/SessionEventBodySchemaVersionTests.cs`
- `tests/SessionJournal.Tests/SessionJournalAuditScanTests.cs`
- `tests/SessionJournal.Tests/SessionJournalOfflineValidatorTests.cs`
- `tests/SessionJournal.Tests/SessionContextCandidateContractTests.cs`（post-review dependency-boundary test-only expansion）
- `tests/SessionJournal.Tests/SessionSelectedLineageAuditTests.cs`（post-review paged mixed-reader test-only expansion）
- `tests/SessionJournal.Tests/SessionTailContextProjectionTests.cs` only if needed to lock final message order
- `tests/SessionJournal.PublicSurface.Tests/SessionJournalNamedRoleTests.cs`

`SessionDependencyClosedFoldSeedTests.cs`未修改；existing dependency-closed ordering由engine/reconstructor/route tests覆盖。
两项test-only expansion没有扩大production scope，其原因与实际文件列表登记在candidate evidence §2。

The acceptance matrix must prove：

- literal v5/v1 Prepared and reconstructed-request goldens remain byte-identical；Disabled encode still writes v5；
- literal v6/v2 NoMatch/Selected goldens, exact nine fields, mandatory terminal input and count boundaries `1/129`；
- v5 counts `0/128` pass；v6 129th terminal passes；v5 129、v6 0、v6 129th nonterminal reject；
- exact-pair matrix accepts only v5/v1 and v6/v2；unknown/cross-pair/version/recipe fail closed；
- strict UTF-8/BOM/surrogate/property order/duplicate/unknown/wrong type/status/nullability/canonical escape and every relevant
  byte/numeric boundary；
- mixed v5→v6 and v6→v5 turns reopen, replay, audit and offline validate without rewrite；
- Prepared/Started recovery and one/many tool continuation make zero source/Pod/client/recall calls and preserve the source pair；
- request prefix order, artifact hash, raw-range hash and whole-request commitment round-trip；
- a pinned old v5-only reader built from immutable pre-amendment source returns explicit `Unsupported` for v6；此证据不得复制
  new parser 或把 new reader 配成“old mode”伪造；
- SessionJournal product has no MemoPod/Galatea/RecapGrid production dependency；external consumer can implement only the intended
  minimal supplemental seam。

### 8.2 Candidate implementation evidence

B1 code/tests 已完成并新增 candidate evidence：

- [completion-request-prepared-v6-candidate.md](../../evidence/completion-request-prepared-v6-candidate.md)
- update `docs/SessionJournal/evidence/README.md` to route that candidate

Candidate evidence已pin code/test commit、old-reader source/tag identity、literal golden hashes、mixed-journal fixture hashes、
focused/full commands、platform/runtime与residual risks。independent implementation review与independent evidence/docs
review均曾PASS；这些是历史candidate事实。用户随后撤回，Gate B canceled/never granted，promotion never started，旧B2
canceled/never authorized。该记录不创建tag，也不修改旧R2 evidence。

## 9. Documentation scope and immutable antecedents

本 pre-B1 document package exact scope 是：

- new `docs/SessionJournal/work/active/completion-request-prepared-v6-tier-a-amendment.md`
- update `docs/SessionJournal/README.md`
- update `docs/SessionJournal/session-journal-doc-check-scope.txt`

本文引用的 integration plan proposal source 是 commit
`d5a403c485807b1ba01c52e39471acfe3429a8ad`。该 plan 提供 cross-layer B1/B2 sequencing；本 Candidate 是 Tier-A owner
对 wire/reader/recovery/rollback 的 exact amendment。两者都不是 implementation 或 approval authority。

以下 antecedents 只读且不得被本工作包或 B1 改写：

- `docs/SessionJournal/current/contracts/session-journal-contract-r2.md`
- `docs/SessionJournal/evidence/contract-freeze-r2-closure.md`
- all `session-journal-contract-r2-approved-surfaces-v1` through `v6` tags and their approval ledgers

## 10. Historical gate disposition

1. pre-B1 document review、历史Gate A、`83477c06` implementation与implementation/evidence/docs reviews均曾完成；
2. 用户于2026-08-20撤回旧方向；旧Gate A随之终止，不得续用或解释为future implementation authority；
3. Gate B **Canceled / Never Granted**；promotion **Never Started**；没有v6 current contract、approval ledger或new tag；
4. 旧B2 **Canceled / Never Authorized**；Galatea从未获得使用这组SessionJournal seam/wire的product授权；
5. code/test已由single atomic rollback `1d8c33bb`回到`a5098a77` exact SessionJournal product/test tree；
6. current code与current approved Tier-A authority均为Prepared v5/recipe v1/count `0..128`；旧R2 contract/evidence/tags
   保持不动。

## 11. Terminal historical boundary

本文已经以**Withdrawn / Rolled Back / Superseded**终止，不再等待任何旧gate。未来若重新考虑MemoPod与
SessionJournal/Galatea结合，必须先解决active integration plan列出的设计闸，再建立fresh Candidate、fresh independent
review、fresh user implementation authorization与fresh final approval gates。不得复制旧Gate A原文、提交链或历史PASS来
跳过任何fresh gate，也不得把本文重新移动回active后直接施工。

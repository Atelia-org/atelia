# SessionJournal Beta contract snapshot (v7 evidence)

状态：Archived-in-place historical evidence snapshot；DerivedRecap v7 candidate-specific gate **NotRun**，见 §7  
Prior Beta-supported candidate：`49ebb4634e5b4136032db983dd92a9a4560b33eb`  
Current recap contract已由WP-08 formal RecapGrid source cutover取代v8；见
[`../derived-recap/durable-target.md`](../derived-recap/durable-target.md)与
[`../derived-recap/concepts.md`](../derived-recap/concepts.md)。

本文只保留v7时点的Beta contract/evidence，不描述当前RecapGrid wire、owner或production caller。
其他 SessionJournal contract 内容仍可作该时点证据参考。它不是所有当前 `public` 类型的兼容性承诺；未列入角色 allowlist 的
diagnostic、low-level 或 first-party cross-assembly mechanics 仍可在 Beta 前后按明确决策收窄。§7保留
prior candidate的exact历史证据，并明确它不能认证本次v7 direct cut。

## 1. Authority model

1. raw events 与 selected `RefId` 的 Parent lineage 是会话事实和恢复的唯一 authority。
2. 在本snapshot的historical v7系统中，DerivedRecap是可删除、可重建的sidecar；Store管structure/membership/strict ordinal，Planner管
   schedule/frozen execution，Maintainers 管 concrete profile/prompt。
3. active Planner config 只决定 NewPlanning；Resume/Restore 服从 frozen plan。
4. Host 必须串行驱动同一 writable Engine，并使用 exact-head mutation；final reread 是 fence，不是 CAS。
5. CLI report 与 call log 是 E-level operational evidence，不是 invocation、raw 或 recovery authority。

## 2. Beta-supported roles

| Consumer role | Supported entry shape |
|---|---|
| Online Host | `SessionJournalEngine.Create/Open`、`UseRuntime`、`InspectRuntimeRecoveryRequirements`、`ReconcileDesiredSetup`、exact-head `SendAsync/ResumeAsync`、completed-turn projection、exact abandon/rewind |
| Read-only/offline | `OpenReadOnly`、checked audit/read view、`SessionJournalOfflineValidator`；read-only 不修复 tail |
| Derived consumer | engine-bound `SessionJournalReadView` 的 bounded prefix/proof/materialization/setup validation |
| Migration | create-only `SessionJournalLegacyImportWriter`；不授予普通 Host low-level append authority |
| Composition root | CLI/Galatea 组合 Store、Planner、Maintainers；raw core 不引用具体 Maintainers |

普通 consumer 不应依赖 public diagnostic records、test/trusted seams、Planner/Building low-level executors或伪造
descriptor。是否 `public` 本身不构成 Beta support 证据。

当前contract的public factory不接收`SessionRuntime`；writable Host唯一public runtime attachment是无-runtime
`Create/Open`之后的`UseRuntime`。三个runtime-bearing factory overload是direct cut，不保留compatibility
overload。`ReadPayloadBytes(EventAddress)`已internal；普通consumer应使用completed/recovery projection、
engine-bound `SessionJournalReadView`或Offline checked audit。

Store authority-bearing inspection/success只能由Store签发。Galatea loader与public Host constructor在任何
session/client/log/maintainer side effect前拒绝两个user指向同一normalized lexical `sessionDir`；preparation
`BeyondPrefix`映射为typed `recap-beyond-prefix`，不扩界、不fallback full scan。

## 3. A-level raw and recovery wire

| Kind | Numeric ID | Body version |
|---|---:|---:|
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

ID 11 retired；其他未定义 ID、未知版本与未知字段都拒绝。

冻结 identifiers：

- repo/trunk：`atelia.session-journal.trunk.v1`
- Prepared request recipe：`atelia.session-journal.coherent-artifact-tail.recipe.v1`
- canonical request：`atelia.completion-request.canonical-json.v1`
- tool definition：`atelia.tool-definition.canonical-json.v1`
- raw range：`atelia.session-journal.raw-range.v1`
- artifact snapshot：`atelia.session-journal.artifact-context-snapshot.sha256.v1`
- history semantic：`atelia.session-journal.history-semantic-commitment.v1`
- context contribution：`atelia.session-journal.context-contribution-text-sha256.v1`
- EventAddress text：`ej1:` 加 32 个 lowercase hex；filename codec 是独立定义

Prepared exact inputs 最多 128；artifact context snapshot 最大 4 MiB。unknown/missing/duplicate/wrong-type、
非 nullable 位置的 null、
整数越界、非法或 off-lineage address、错误 Parent、setup drift 与 hash mismatch 均 fail closed。direct encode bytes、
strict decode semantics 与 Prepared reconstruction canonical bytes 使用同一 authority rules；只有 schema 明确声明为
nullable 的字段接受 null。

Prior candidate `49ebb463`没有A-level raw/recovery wire变化；本次v7 cut同样不修改A-level wire。

## 4. Historical B-level repo-owned Planner config

- path：retired B-level Planner config slot；exact path只由formal legacy-root inventory持有
- schema：`atelia.session-journal.recap-planner-config.v2`
- 最大 64 KiB，JSON depth 32，禁止 comments/trailing comma
- writer 输出 canonical bytes；reader 可接受合法的非 canonical whitespace/property order
- unknown policy/profile 可完成 document decode，但 resolver 必须 typed reject
- hard caps：raw growth 512、route endpoints 4、maintainer calls 8、step events 64、build events 512、
  contribution content 256 KiB、catalog entries 128

config snapshot 每次 operation 只解析一次。Building/Resume/Restore 不以 active config、当前 default connection 或
当前 maintainer roster 覆盖 frozen authority。

`RecapPlannerConfigResolutionCatalog`直接接收`IReadOnlyList<IRecapPlanningPolicy>`与
`IReadOnlyList<IHistoryUnitLoadEstimator>`。catalog construction按Ordinal冻结implementation当时的`Id`与
对象，拒绝null/blank/duplicate；后续identity drift仍typed reject为`PolicyIdentityMismatch`或
`EstimatorIdentityMismatch`。两个registration wrapper/key type是direct cut。此变化只收窄in-memory public
catalog：config v2 bytes、reader language、path/hash/caps及active/frozen routing均不变。

## 5. Historical C-level DerivedRecap filesystem wire

root：retired C-level DerivedRecap filesystem root；exact path只由formal legacy-root inventory持有

| Artifact | Schema | Read cap |
|---|---|---:|
| `store.json` | `atelia.session-journal.derived-recap-store.v4` | 16 KiB |
| `manifest.json` | `atelia.session-journal.derived-recap-manifest.v7` | 2 MiB |
| frozen input | `atelia.session-journal.derived-recap-frozen-input.v5` | 5 MiB |
| final block | `atelia.session-journal.derived-recap-block.v4` | 512 KiB |
| `publication.json` | `atelia.session-journal.published-recap-set.v7` | 3 MiB |

Manifest v7在根级只保存一份`PriorContext + PriorContextPayloadSha256`；每个Maintain plan只保存相同
digest，`BlockPlanSha256`由此绑定exact set-level execution input。all-Inherit使用Empty；policy不输出
per-block prior，Resume/Restore不从inputs重新render。Building inventory 最多1024 entries；bounded lineage
proof最多513 headers。old manifest/publication v6及更早版本与frozen-input v4严格拒绝，不提供
compatibility reader。

2 MiB/3 MiB是完整canonical manifest/publication encoded bytes的读写边界。创建Building或写入/重写
publication envelope前必须先编码并检查实际bytes；JSON escaping、ordered plans与commitments全部计入。
本版不增加独立`prior-context.json`。

Linux durability path 使用 same-directory temporary、flush/fsync、no-replace rename 与 directory fsync。
`Selected` 只认证 publication/manifest metadata descriptor；component missing/corrupt 由 exact-slot Restore
按 missing-only 规则修复。损坏 Published slot 仍占 strict ordinal，不 fallback 到更旧 set。

以下九个.NET authority-bearing结果类型是Store-issued seam，不是filesystem wire：

- `BuildingBlockInspection`；
- `PublishedBlockRestoreInspection`；
- `PublishedRestoreInspection`；
- `PublishedRestoreInspectionResult.Available`；
- `PublishedCheckpointWriteResult.Updated` / `AlreadyCurrent`；
- `PublishedFinalWriteResult.Installed` / `ReplacedDamaged` / `AlreadyHealthy`。

它们是sealed、没有externally-callable constructor，public properties均get-only；inspection/success从Store构造
起携带non-null matching authority，成功mutation返回与post-write state绑定的refreshed authority。manifest、
publication、block、checkpoint bytes与schema均未改变。

## 6. Explicit non-promises and residual risks

- `RawHistoryAuthorized` 只用于 `EmptyLineage` genesis，不是 invalid/unavailable 的 fallback。
- `BeyondPrefix` 不分页、不自动扩大，也不退回 full scan。
- provider 不保证 exactly-once；重复 LLM 调用允许，call log 缺失不等于没有调用。
- Event append 与 ref CAS 不组成事务；CAS 失败可能留下不可达 orphan event。
- `OpenReadOnly` 不做 tail recovery；当前没有 full scrub、proactive healing、tamper signature、backup 或 replication。
- process-death crash test 不证明真实断电；当前 durability 支持口径限定 Linux。
- 静态 symlink/reparse 防护不抵抗拥有同目录写权限的 hostile concurrent writer。
- sidecar 可重建；旧 generation 直接切断，不增加 silent migration、compatibility shim 或 full-raw fallback。

## 7. Verification boundary

`681fc02b`的历史R4在两个独立`--no-local` fresh clone上合计通过2080 tests、10 expected skips、0 failed，
并完成real-provider/staging workflow；该证据只认证对应prior candidate，不自动转移到current v7 candidate。

Gate-tooling实施前，`49ebb463`的独立local Release solution build为0 warnings、0 errors；七套default
tests合计1044 passed、5 expected opt-in skips、0 failed。另以已验证的1,281,881-byte真实legacy export
运行1项scripted real-data acceptance，完成
failed-run/resume、damaged-final missing-only restore、online turn与Prepared recovery，并保持source与raw prefix
invariants。它不包含real-provider dispatch；本轮未改provider request construction，因此调用预算与实际调用均为0。
Galatea的4个staging tests在default轮是expected skips；该skip不单独写成通过。

candidate-specific Clone A/B均在exact candidate `49ebb463`上通过：每份Release solution build都是0
warnings、0 errors；每份七套default tests都是1044 passed、5 expected skips、0 failed；每份explicit
real-data acceptance都是1 passed、0 skipped、0 failed。合计default tests 2088 passed、10 expected skips、0
failed，explicit real-data acceptance 2 passed、0 failed。随后两个clone都以current-writer v6 scripted
fixture显式运行4项disposable-Host staging acceptance，各自4 passed、0 skipped、0 failed，fixture base hash不变。

Gate-tooling commit `81a1fa24`只修改tests/runbook，不改变product assemblies或candidate contract；其提交后
Release solution build为0 warnings、0 errors，CLI全套116 passed、1 expected opt-in skip、0 failed，Galatea带
v6 fixture全套70 passed、0 skipped、0 failed。这些scripted gates不包含real-provider dispatch或real Host canary；
本轮provider request construction未变，external provider calls为0。因此`49ebb463`是prior
Beta-supported candidate。上述build/tests/fixture均早于set-level prior与manifest/publication v7 direct cut，
不能认证current contract；该historical v7 candidate-specific gate状态为**NotRun**。完整历史证据边界见
[`contract-normalization-review.md`](../../evidence/contract-normalization-review.md)。

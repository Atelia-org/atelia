# SessionJournal Beta contract snapshot

状态：Beta-supported  
Product candidate：`681fc02bb9f1e4a45cd012aa7feadefe3f33fa9e`

本文冻结首个 Beta 准备支持的边界。它不是所有当前 `public` 类型的兼容性承诺；未列入角色 allowlist 的
diagnostic、low-level 或 first-party cross-assembly mechanics 仍可在 Beta 前后按明确决策收窄。

## 1. Authority model

1. raw events 与 selected `RefId` 的 Parent lineage 是会话事实和恢复的唯一 authority。
2. DerivedRecap 是可删除、可重建的 sidecar；Store 管 structure/membership/strict ordinal，Planner 管
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

## 4. B-level repo-owned Planner config

- path：`<repo>/config/recap-planner-config.json`
- schema：`atelia.session-journal.recap-planner-config.v2`
- 最大 64 KiB，JSON depth 32，禁止 comments/trailing comma
- writer 输出 canonical bytes；reader 可接受合法的非 canonical whitespace/property order
- unknown policy/profile 可完成 document decode，但 resolver 必须 typed reject
- hard caps：raw growth 512、route endpoints 4、maintainer calls 8、step events 64、build events 512、
  contribution content 256 KiB、catalog entries 128

config snapshot 每次 operation 只解析一次。Building/Resume/Restore 不以 active config、当前 default connection 或
当前 maintainer roster 覆盖 frozen authority。

## 5. C-level DerivedRecap filesystem wire

root：`<repo>/derived/recap/v4/refs/<ref-id>/`

| Artifact | Schema | Read cap |
|---|---|---:|
| `store.json` | `atelia.session-journal.derived-recap-store.v4` | 16 KiB |
| `manifest.json` | `atelia.session-journal.derived-recap-manifest.v6` | 2 MiB |
| frozen input | `atelia.session-journal.derived-recap-frozen-input.v5` | 5 MiB |
| final block | `atelia.session-journal.derived-recap-block.v4` | 512 KiB |
| `publication.json` | `atelia.session-journal.published-recap-set.v6` | 3 MiB |

Building inventory 最多 1024 entries；bounded lineage proof 最多 513 headers。old manifest/publication v5 与
frozen-input v4 是 direct cut，严格拒绝，不提供 compatibility reader。

Linux durability path 使用 same-directory temporary、flush/fsync、no-replace rename 与 directory fsync。
`Selected` 只认证 publication/manifest metadata descriptor；component missing/corrupt 由 exact-slot Restore
按 missing-only 规则修复。损坏 Published slot 仍占 strict ordinal，不 fallback 到更旧 set。

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

本快照的 wire/API 结论以综合报告中的 R0–R3 review 和 exact product candidate 为准。R4 在两个独立
`--no-local` fresh clone 上重复通过：每个 clone 1035 个 default test、5 个 explicit opt-in test、真实数据
import/recap/NoBuild、disposable Host canary、reopen/Undo 与 source/raw-ref invariants。合计 2080 passed、
10 expected skips、0 failed；因此本文状态为 Beta-supported。

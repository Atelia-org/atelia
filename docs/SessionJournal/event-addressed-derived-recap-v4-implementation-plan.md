# SessionJournal EADR V4：实现与替换计划

> **状态**：Implementation Plan
> **日期**：2026-07-30
> **核心概念**：
> [EADR 核心概念](event-addressed-derived-recap-concepts.md)
> **目标设计**：
> [Event-addressed Derived Recap V4](event-addressed-derived-recap-v4-target-design.md)
> **兼容策略**：不迁移、不双写、不读取 current DerivedMemory v2/v3

## 0. 原则

V4 从新 projects/tests 开始，不在 current `SessionJournal.DerivedMemory` 内原地删改。四个纵向包
分别形成 contract → implementation → focused tests → independent review → tail-fix 闭环。

目标 projects：

```text
prototypes/SessionJournal.DerivedRecap.Store/
prototypes/SessionJournal.DerivedRecap.Planner/
prototypes/SessionJournal.DerivedRecap.Maintainers/
tests/SessionJournal.DerivedRecap.Store.Tests/
tests/SessionJournal.DerivedRecap.Planner.Tests/
```

最终删除 current：

```text
prototypes/SessionJournal.DerivedMemory/
tests/SessionJournal.DerivedMemory.Tests/
```

全程不变量：

- raw Parent lineage 是 correctness authority；
- Recap 是 resident cold-prefix approximation；Memory 保留给未来 dynamic retrieval；
- `SetAdmissionAnchor` 与 `AbsorbedThrough` 分离；
- Building 不计 ordinal，Published directory 才计；
- exact invalid Published set不 fallback；
- source bytes、route 与 prior context在第一次 Maintainer 调用前冻结；
- healthy final block不重做；
- rolling checkpoint不进入 ordinal；
- Store不调 Planner/Maintainer；
- Prepared exact reopen不访问 Recap Store；
- 不保留 V4 Memory/Artifact 命名兼容层。

## 1. R0：Contracts + Publish/Read vertical

### Intent

一次性锁定 Recap vocabulary、neutral context contract 与最小 Store，从 empty Building 走通 atomic
Publish、strict select 与 materialize。

### In scope

#### Contract cutover

- `DerivedArtifactSet` → `DerivedRecapSet`；
- `MemoryBlockId` → `RecapBlockId`；
- `IMemoryBlockMaintainer` → `IRecapBlockMaintainer`；
- maintenance request/result/profile/rewriter 改为 Recap naming；
- `MemoryPack*` context-header projection 改为：
  - `ContextHeaderCarrier`；
  - `ContextHeaderBlock`；
  - `ContextHeaderBlockPath`；
  - `ContextHeaderPack` / Draft；
  - `RenderedContextHeader` 或直接 `ContextHeaderSnapshot`；
- `ISessionMemoryLifecycleCoordinator` 评估并 cut over 为
  `ISessionContextLifecycleCoordinator`；
- 保持 persisted MaintainerId、block keys、prompt bytes/logical names不变。

这是单次 breaking cutover，不增加 obsolete wrapper 或双 contract。

#### Store vertical

- `derived/recap/v4/refs/<ref>/store.json + building/ + published/`；
- crash-safe CreateStore/reset；
- RefId/EventAddress/RecapBlockId path codecs 与 safe-descendant guard；
- canonical manifest/block/publication codecs 与 checksums；
- manifest checksum 覆盖排除 hash 字段的 canonical manifest bytes；
- Building manifest authority；
- `CanPublish` shared validator；
- publication candidate seal：

```text
flush/close/fsync manifest, inputs and every final block
  -> containing-directory durability barriers
  -> temp envelope
  -> file fsync
  -> rename to publication.json
  -> directory barrier
  -> final eligibility/latest-anchor validation
  -> building directory atomic create-new rename to published
```

- Published publication authority；
- envelope ETag descriptor；
- Parent-chain point lookup strict ordinal；
- exact materialization + per-block `AbsorbedThrough`；
- typed selection reasons。

### Out of scope

- Planner trigger/Maintainer calls；
- rolling checkpoint；
- Published semantic Restore；
- full scrub；
- old data migration。

### Tests

- codecs/path traversal/symlink/reparse isolation；
- root Create/reset 每个 crash point；
- missing required root directory不是 EmptyLineage；
- valid-JSON manifest mutation触发 checksum failure；
- manifest/blocks Complete 但未 promoted不计 ordinal；
- envelope temp/final seal crash matrix；
- manifest/input/final-block file flush与 directory barrier前后的 power-loss matrix；
- directory rename old-or-new visibility，destination exists fail-fast；
- final gate拒绝 retroactive insertion；
- `n = 0 / n > 0 / ordinal short / empty lineage`；
- invalid exact Published directory仍计数、不 fallback；
- off-lineage/ref rewind不可见；
- descriptor/materialization ETag double-read；
- shared limits 与 raw ancestry；
- Prepared snapshot/reopen不访问 Store；
- 10k cold prefix只做 raw header walk + point lookup。

### Done when

fake Recap blocks 可以完成：

```text
Building -> CanPublish -> atomic Published
         -> strict NthPrevious -> exact Context contributions
```

且无需 Planner、Maintainer、derived lineage 或 latest pointer。

## 2. R1：Planner + Build/Resume vertical

### Intent

实现 frozen source、Maintain/Inherit、endpoint-only catch-up 与 rolling checkpoint，并证明 partial
failure/reopen只补当前缺失工作。

### In scope

- `RecapPlannerConfig`：
  - raw growth trigger/hard-limit；
  - admission selection；
  - active recap catalog；
  - NoBuild/Maintain/Inherit policy；
  - content/route/call limits；
- exact source envelope read：
  - read token；
  - copy by commitments；
  - validate payloads；
  - reread same token；
  - changed 时隔离 pre-manifest work；
- discriminated `RecapBlockPlan`；
- manifest self-check；
- Existing/Empty source；
- ordered `CatchUpThrough[]`，start 由 previous endpoint推导；
- one frozen prior context per Maintain block；
- single `work/<block>.json` rolling checkpoint；
- `BlockPlanSha256`；
- `EnsureFinalRecapBlock`；
- valid final block skip；
- Building Resume；
- Galatea/老王 fixture。

### Out of scope

- Published Restore；
- recursive source repair；
- step checkpoint chain；
- per-step distinct prior context；
- relevance Tag/LLM policy implementation；首版使用 injectable deterministic policy。

### Tests

- trigger未达到返回 NoBuild；
- Inherit零 Maintainer调用且 cursor不变；
- Maintain正文不变仍推进到 admission；
- source envelope在多 block copy中变化时整个 pre-manifest build重试；
- source repair在 manifest 后不改变 frozen input；
- endpoint start唯一推导、strict increase、final=admission；
- prior context Inline/Empty shape与 ancestry；
- partial Building output不作为其他 block prior context；
- rolling(A5/A11) crash 后只补 suffix；
- rolling replace只看见旧或新 healthy version；
- rolling missing/damaged只重跑当前 block完整 route；
- final endpoint checkpoint后、final install前 crash不再调用 LLM；
- alpha final healthy、zeta failed；reopen不重跑 alpha；
- old BlockPlanSha256 checkpoint被拒绝；
- structurally valid但绑定错误 BlockPlanSha256 的 final block被拒绝；
- route/call limit产生稳定 backpressure；
- 已有较新 Published anchor时拒绝旧 target。

### Galatea acceptance

```text
A1  client AbsorbedThrough
A8  weekend Inherit
A12 weekend Inherit
A20 work target

source container A12
frozen old cursor A1
route A5 -> A11 -> A20
```

- A5/A11 可以早于 SourceSetAnchor A12；
- A5/A11不成为 set、ordinal或其他 block source；
- 只有 A20 atomically Published。

### Done when

真实进程 reopen 证明：正常 crash保留 block-local missing-suffix resume；checkpoint corruption最多重跑
该 block，其他 final blocks不重跑。

## 3. R2：Exact-slot Restore + Online lifecycle

### Intent

把 Published structural defects转成一个 bounded RestorePlan，并闭合 exact invalid-slot
not-ready → Restore → reselect，同时不建立全库 self-heal system。

### In scope

- Store `StructuralDefects[]`；
- Planner：

```text
TryCreateRestorePlan
  -> RestorePlan
  | RestoreUnavailable(reason)
```

- Published authority只来自 `publication.FrozenPlanSnapshot`；
- Resume/Restore共用 `EnsureFinalRecapBlock`，外层 phase不合并；
- frozen plan exact不变；
- component atomic replace + publication envelope last；
- pending replacement复用；
- envelope-loss restore：
  healthy manifest cache + frozen inputs + final blocks → full revalidation → new envelope；
- inputs/work missing只影响 Restore capability；
- exact selected-slot on-demand Restore；
- lifecycle：

```text
inspect/select
  -> Selected: materialize
  -> ExactPublishedSetInvalid:
       bounded RestorePlan -> execute -> reselect
       RestoreUnavailable -> stable not-ready
```

- no blind retry；只有显式 operation或外部状态变化后重试。

### Out of scope

- background/full-generation scrub；
- periodic/proactive heal；
- dependency scheduler；
- recursive source chain repair；
- backup/replication；
- terminal/transient三态 persisted taxonomy。

### Tests

- final block损坏但 rolling final健康：0次 LLM restore；
- earlier rolling endpoint健康：只跑 suffix；
- rolling/source均损坏：RestoreUnavailable或仅该 block完整重跑；
- healthy Published target在 inputs/work丢失后仍 materialize；
- block replace后、envelope前 crash保持 exact not-ready；
- pending replacement匹配 BlockPlanSha256时不重复调用；
- envelope replace后旧 descriptor fail-fast；
- byte-identical restore ETag可不变；
- publication missing但 directory membership保留；
- healthy manifest cache可重建 envelope；
- publication/manifest均不可用时ordinal保留、stable not-ready；
- Restore不得改变 roster/mode/source/route/prior/Maintainer/limits；
- latest/middle invalid slot均不 fallback、不重编号；
- Store不调用 Planner/Maintainer；
- selector不边读边修；
- Prepared后删除整个 Store仍 exact reopen。

### E2E

```text
raw growth
  -> NoBuild 或 plan
  -> Maintain/Inherit
  -> rolling catch-up
  -> partial failure + reopen
  -> atomic Published
  -> strict NthPrevious
  -> completion Prepared
  -> exact block/envelope damage
  -> exact-slot not-ready
  -> bounded Restore same Published directory
  -> reselect/materialize
  -> Prepared reopen
```

### Done when

Galatea 可以多 epoch运行，Published damage不改变 ordinal，Restore没有演化成 transaction、scrub
或 recursive dependency workflow。

## 4. R3：Cutover、CLI 与真实验收

### Intent

把 production composition切到 DerivedRecap，删除 current DerivedMemory，并用真实 repo证明替换完成。

### In scope

- Host/CLI只组合 Store + Planner + Maintainers；
- 最小命令：
  - inspect exact Building/Published；
  - plan/run/resume Building；
  - restore exact Published set；
  - quarantine unpublished Building；
  - explicit Store rebuild；
  - run online turn；
- 删除 current DerivedMemory production/test projects；
- 更新 solution、active docs 与 architecture guards；
- 删除 active target/runtime 的 ArtifactSet/DerivedMemory V4 naming；
- 保留 done/superseded/current baseline 的历史名称。

### Out of scope

- v2/v3 migration、fallback、dual read/write；
- full scrub CLI；
- broad repair console；
- future dynamic Memory；
- multi-process writer。

### Structure scan

```text
rg "MaintenanceJob|RoleAttempt|RoleSettlement|Finalization|JobReprovision"
rg "PreviousSetId|latest pointer|PolicyFingerprint"
rg "DerivedArtifactSet|DerivedMemoryBlock|SourceRawHead"
rg "SessionJournal\\.DerivedMemory"
rg "MemoryPack|IMemoryBlockMaintainer|ISessionMemoryLifecycleCoordinator"
rg "derived/memory/v2|derived/memory/v3"
```

允许：

- done/superseded/current baseline 文档；
- v2/v3 historical paths；
- migration/export-only references。

不允许：

- EADR V4 target/runtime 继续使用宽泛 Memory 或 ArtifactSet作为 Recap 领域名；
- current/target contract 双表面并存。

### Final validation

- Store/Planner/Maintainers/SessionJournal/CLI focused tests；
- solution build；
- relative Markdown link scan；
- `git diff --check`；
- 一个真实 SessionJournal repo：

```text
Create/rebuild Store
  -> plan
  -> partial rolling progress
  -> reopen
  -> publish
  -> strict select
  -> corrupt exact component
  -> not-ready
  -> restore
  -> online completion
  -> Prepared reopen after Store deletion
```

## 5. 完成定义

- Recap/Memory/Context边界进入 canonical docs和target symbols；
- 四个 assemblies职责单向；
- Building/Published只有一个原子 boundary；
- source/admission/cursor如实；
- endpoint-only route与rolling checkpoint可用；
- strict exact ordinal可用；
- Published bounded Restore可用；
- Prepared exact reopen不依赖 Recap Store；
- current DerivedMemory transaction workflow从 production tree删除；
- future dynamic Memory、full scrub和advanced self-heal保持 deferred。

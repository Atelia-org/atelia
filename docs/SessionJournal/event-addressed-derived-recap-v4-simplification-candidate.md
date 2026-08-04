# SessionJournal Event-addressed Derived Recap V4：化简候选

> **状态**：Accepted Design Review / 已回写 canonical
> **日期**：2026-07-30
> **Canonical**：
> [EADR 核心概念](current/derived-recap/concepts.md)、
> [EADR V4 目标设计](current/derived-recap/durable-target.md)、
> [EADR V4 实现计划](event-addressed-derived-recap-v4-implementation-plan.md)
> **目的**：记录从 EADM V4 到 EADR V4 的化简论证与 adversarial review gates。具体 contract
> 以 canonical 文档为准。

## 0. 定位与命名

V4 解决的是：

> 把无限增长的旧 session history 截断，用有限、常驻、可持续维护的近似内容替代它，并继续拼接
> admission anchor 之后的 exact raw suffix。

这类内容更准确地称为 **Recap（前情提要）**，而不是广义 Memory：

- Recap 是对旧 history prefix 的有损、有限、常驻近似；
- Memory 是未来更广的上位概念，可以包含 query-dependent retrieval、vector recall、episodic
  records、knowledge graph 与 multi-hop traversal；
- Context 是一次 completion request 实际看到的 materialized input；它可以由 Recap、retrieved
  Memory、raw suffix、tool/runtime setup 等共同组成。

目标命名：

| 当前 V4 候选名 | 化简后的目标名 |
|---|---|
| `DerivedArtifactSet` | `DerivedRecapSet` |
| `MemoryBlock` | `RecapBlock` |
| `MemoryBlockId` | `RecapBlockId` |
| `DerivedMemoryBlockPlan` | `RecapBlockPlan` |
| `DerivedMemoryPublicationSlot` | `PublishedRecapSet` / `publication.json` |
| `SessionJournal.DerivedMemory.Store` | `SessionJournal.DerivedRecap.Store` |
| `SessionJournal.DerivedMemory.Planner` | `SessionJournal.DerivedRecap.Planner` |
| `SessionJournal.Maintainers`（target concrete recap 部分） | `SessionJournal.DerivedRecap.Maintainers` |
| `derived/memory/v4` | `derived/recap/v4` |

current `MemoryPack`、`IMemoryBlockMaintainer` 等已经实现的 contract 不在本文直接改名。R0
执行一次不留兼容层的 contract cutover：

- recap-specific maintenance 改为 `IRecapBlockMaintainer`、`RecapBlockMaintenanceRequest /
  Result`、`RecapRewriteProfile` 与 `RewriteRecapBlockMaintainer`；
- current `MemoryPack*` 实际是 context-header projection，改为 `ContextHeaderPack /
  ContextHeaderBlock / ContextHeaderBlockPath / ContextHeaderCarrier`；
- `RenderedMemoryPack` 删除并直接产生 `ContextHeaderSnapshot`，或改为
  `RenderedContextHeader`；
- `ISessionMemoryLifecycleCoordinator` 评估改为通用的
  `ISessionContextLifecycleCoordinator`；
- existing persisted `MaintainerId` values、`roleplay.*` block keys 与 embedded prompt resource
  logical names 保持不变。

在该 cutover 落地前，target 文档必须明确这些是目标名，不得把 current Memory symbols 误写成
已经完成的 API。

## 1. 不可牺牲的不变量

1. raw SessionJournal events 与 Parent lineage 是 correctness authority。
2. `SetAdmissionAnchor` 与 per-block `AbsorbedThrough` 分离。
3. `Maintain` 推进 cursor；`Inherit` exact-copy 且不推进。
4. published membership 与 payload 当前是否可 materialize 分离。
5. strict `NthPrevious` 命中坏 slot 时 exact not-ready，不 fallback、不重编号。
6. catch-up 中间状态不成为 set，不进入 ordinal，也不供其他 block 继承。
7. frozen build 在 Resume 时不重读可能已经 repair/change 的 live source payload。
8. 一个 block 失败不重跑其他已有合法 final block。
9. Prepared 保存 exact request/context snapshot，reopen 不访问 Recap Store。
10. Store 只报告结构事实；Planner 决定 Maintain/Inherit/Restore，不形成反向依赖。

## 2. 最小 durable model

```text
derived/recap/v4/
  refs/
    <ref-id-hex>/
      store.json
      building/
        <event-address-hex>/
          manifest.json
          inputs/
            <recap-block-id>.json
          blocks/
            <recap-block-id>.json
          work/
            <recap-block-id>.json
          publication.json
      published/
        <event-address-hex>/
          publication.json
          manifest.json
          inputs/
            <recap-block-id>.json
          blocks/
            <recap-block-id>.json
          work/
            <recap-block-id>.json
```

### 2.1 Store root

`store.json` 只包含：

```text
RecapStoreHeader {
  Schema
  RefId
}
```

- valid root 是 Store open precondition；`building/` 与 `published/` 是 required structural
  directories，缺失时不得报告 `EmptyLineage`；
- missing/malformed root 返回 `StoreUnavailable`；
- 创建空 Store 必须走显式 `CreateStore`；它既可服务 fresh session，也可服务 existing-session
  destructive rebuild，不绑定 strict raw bootstrap topology；
- 只有 `EmptyLineage -> empty-context request` 必须由 Host 额外验证 strict raw bootstrap
  topology；
- Create 在 sibling temporary ref directory 中建立 `store.json + building/ + published/`，全部
  flush/durable 后才 atomic rename 到 `<ref-id>/`；也可以先建立 required directories，最后原子
  install `store.json` 作为 root commit marker；
- 显式 reset 先把整个 `<ref-id>/` 原子 rename/quarantine，再创建新 root；
- reset 在 quarantine 后、新 root commit 前崩溃时稳定返回 `StoreUnavailable`，不得自动补 root；
- 首版不持久化 `GenerationId / Available / Unavailable / Reason` 状态机；
- 单个 published directory 被带外删除仍超出 correctness guarantee。

### 2.2 Building 与 Published

durable phase 只有：

```text
Building
Published
```

publication：

```text
building/<anchor>/
  -> validate CanPublish
  -> write publication.<nonce>.tmp
  -> flush/close
  -> atomic rename temp -> publication.json
  -> fsync building directory
  -> final CanPublish + latest-anchor revalidation
  -> atomic create-new rename directory
  -> published/<anchor>/
```

authority 规则：

- rename 前：`manifest.json` 是 Building authority，final-name `publication.json` 仍只是 sealed
  candidate；
- atomic directory rename 是唯一 membership/authority commit boundary；
- rename 后：`published/<anchor>/` directory entry 占据 strict ordinal；
- rename 后：`publication.json` 是唯一 online metadata authority；
- 原 `manifest.json` 只是 envelope-loss restore cache，不参与 normal online health；
- publication 与 manifest 都合法但不一致时，Published phase 以 publication 为准，不形成双
  authority conflict。

该 backend 必须明确支持 same-filesystem sibling-directory atomic create-new rename。所有 required
files 必须先 flush/close/fsync；destination 必须不存在；rename 前不得再有 block/manifest writer；
rename 后必须对 `building/` 与 `published/` parent directories 做 durability barrier。Windows 上
必须关闭会阻止 non-empty directory move 的 handles。无法保证时 fail unsupported，不能退化为
copy + delete。

Planner 选择 target anchor 时，以及 Store 执行最终 directory rename 前，都必须在同一个 per-Ref
single-coordinator/exclusive-publish scope 中验证：

```text
new SetAdmissionAnchor
  is a strict descendant of current-lineage latest published anchor
```

禁止在已有较新 slot 后 retroactive 插入旧 anchor，避免改变既有 ordinal。

## 3. 最小 manifest

```text
DerivedRecapSetManifest {
  Schema
  RefId
  SetAdmissionAnchor
  Blocks[]
  ManifestPayloadSha256
}

RecapBlockPlan =
  Inherit {
    RecapBlockId
    Target
    SourceSetAnchor
    SourcePublicationEnvelopeSha256
    SourceInputPayloadSha256
    MaxContentUtf8Bytes
  }
  | Maintain {
    RecapBlockId
    Target
    MaintainerId
    Source =
      Existing {
        SourceSetAnchor
        SourcePublicationEnvelopeSha256
        SourceInputPayloadSha256
      }
      | Empty {
        ReplayStartExclusive
      }
    CatchUpThrough[]
    PriorContext =
      Empty
      | Inline {
          AdmissionAnchor
          Snapshot
        }
    MaxContentUtf8Bytes
  }
```

规则：

- `Mode` 由 discriminated union 明示，不从 nullable 字段猜测；
- `ManifestPayloadSha256` 覆盖 canonical manifest bytes（hash 字段本身除外），只用于发现
  accidental corruption，不是 set identity；
- Existing source 的 first replay start 由 frozen input block 的 `AbsorbedThrough` 得出；
- Empty source 显式保存 `ReplayStartExclusive`；
- manifest 只保存 strictly increasing `CatchUpThrough[]`；
- step start 由 source cursor 或前一 endpoint 唯一推导，不持久化 `StepStartExclusive`；
- 最后一个 endpoint 必须等于 `SetAdmissionAnchor`；
- 每个 Maintain block 只冻结一个 prior context，整条 route 复用；
- `PriorContext.Inline.AdmissionAnchor` 必须位于 first replay start 的相同 raw Parent lineage，且
  是其 inclusive ancestor；
- `inputs/` 保留为独立文件，避免把最多 128 个大 block 内联进 manifest；
- source input 在 manifest 之前 durable；无 manifest 的 orphan build 整体隔离，不静默复用。

复制 frozen inputs 必须绑定 exact source publication envelope，不能只逐 block 读取 live source：

```text
read source EnvelopeSha256
  -> copy required blocks according to that envelope commitments
  -> validate every copied payload
  -> reread source EnvelopeSha256
  -> token unchanged: commit target manifest
  -> token changed: isolate pre-manifest build and retry
```

若不同 blocks 来自不同 `SourceSetAnchor`，分别对每个 source envelope 执行上述协议。

允许 block-local catch-up endpoint 早于 source set container anchor。例如老王 block 可从 published
set A12 取得 cursor A1，再沿 `A5 -> A11 -> A20` catch up；A5/A11 不是 set admission，因此不要求
`SourceSetAnchor <= CatchUpThrough`。

`Defer` 不再是 canonical contract：

- whole-set 暂缓是 `NoBuild`；
- block-level 暂缓落为 `Inherit`；
- Planner 可以在 diagnostics 中记录 policy reason。

## 4. 单个 rolling checkpoint

final block 与 rolling checkpoint 都使用：

```text
DerivedRecapBlock {
  Schema
  RecapBlockId
  Target
  BlockPlanSha256
  AbsorbedThrough
  Content
  PayloadSha256
}
```

`PayloadSha256` 覆盖 ID、Target、cursor 与 Content；`BlockPlanSha256` 单独把 wrapper 绑定到
authoritative frozen plan。`Inherit` exact-copy payload/cursor，但 target final wrapper 使用当前
Inherit plan hash。

每个 Maintain block 最多有一个可丢弃的 progress cache：

```text
work/<recap-block-id>.json {
  // exact DerivedRecapBlock shape
}
```

规则：

- `AbsorbedThrough` 必须 exact-match frozen route 的某个 endpoint；
- `BlockPlanSha256` 必须匹配当前 phase authoritative frozen plan 中该 block plan 的 canonical
  hash；
- Building Resume 只使用 healthy manifest plan；Published Restore 只使用
  `publication.FrozenPlanSnapshot`；runner 不自行选择 authority winner；
- 每个 step 完成后使用 same-directory temporary + file flush/fsync + atomic replace + directory
  durability barrier；
- 健康 rolling checkpoint 后只运行 missing suffix；
- missing/damaged checkpoint 视为 cache miss，仅该 block 从 frozen source 重跑完整 route；
- 最终 endpoint 也先写 rolling checkpoint，再从它原子安装 final block；
- checkpoint 不参与 completeness、publication、ordinal 或其他 block 输入；
- Published 后保留 checkpoint 只为 exact-slot Restore；它不是 online payload authority。
- Published `inputs/` 或 `work/` 缺失/损坏不影响 `CanMaterialize`；它们只影响
  `TryCreateRestorePlan`；
- `work/` 是可删除 cache；`inputs/` 是 frozen restore dependency，普通 cache cleanup 不得删除。
  inputs 丢失时健康 Published set 仍可读，但未来可能 `RestoreUnavailable`。

这保留正常 crash 后 missing-suffix resume，同时删除 checkpoint chain 的 prefix scan、gap、suffix
invalidation 和多文件 corruption matrix。

## 5. publication.json 与 snapshot token

```text
PublishedRecapSet {
  Schema
  RefId
  SetAdmissionAnchor
  FrozenPlanSnapshot
  BlockCommitments[]
  EnvelopeSha256
}

RecapBlockCommitment {
  RecapBlockId
  Target
  AbsorbedThrough
  PayloadSha256
}
```

- `EnvelopeSha256` 覆盖 canonical envelope bytes（hash 字段本身除外）；
- descriptor 绑定 `RefId + SetAdmissionAnchor + EnvelopeSha256`；
- materialization 读取 blocks 前后都重验同一 envelope token；
- Published Restore 必须保持 `FrozenPlanSnapshot` exact 不变；只允许 regenerated block
  commitments 与 `EnvelopeSha256` 改变；
- 无法按 frozen plan Restore 时返回 `RestoreUnavailable`，不得借 repair replan；
- byte-identical repair 后 token 不变是安全的；
- 不再保存 `PublicationRevision` 或单独的 `ManifestSha256`。

初次 publication 与 repair 不共用 transaction protocol：

- initial publish：atomic directory rename 建立 membership；
- repair：membership 不动，逐 component atomic replace，最后 atomic replace publication envelope；
- repair 中间态允许 exact slot 暂时不可 materialize。

Published envelope missing/damaged 时：

- published directory entry 继续保留 membership；
- 只有 self-check healthy 的 `manifest.json` cache、frozen inputs 与合法 final blocks可以用于
  重建 envelope；
- 全量 revalidate 后才 envelope-last install；
- manifest cache 与 envelope 都不可用时返回 `RestoreUnavailable`。

repair 在替换 final block 后、envelope 前崩溃时，允许复用一个尚未被旧 envelope commitment接纳的
pending replacement，只要它：

- self-checksum、ID、Target、cursor 与 limit 合法；
- 匹配 authoritative frozen block plan；
- 不要求其他仍匹配旧 commitments 的 blocks 重跑。

无法证明 pending replacement 属于 frozen plan 时，才重新运行该 block。

## 6. predicates 与 query result

不建立七状态状态机：

```text
CanPublish(build)
  -> Publishable(publication candidate)
  | Defects[]

CanMaterialize(published)
  -> Descriptor(EnvelopeSha256)
  | Defects[]

IsVisible(SetAdmissionAnchor, completionBoundary)
  -> bool

Planner.TryCreateRestorePlan(defects, frozen references)
  -> RestorePlan
  | RestoreUnavailable(reason)
```

`Complete`、`OnlineEligible`、`Healthy`、`VisibleAtBoundary`、`Repairable` 可以保留为解释性术语或
predicate 名称，但不落盘、不组成 cross-product enum。

selection：

```text
RecapSelectionResult =
  Selected(descriptor)
  | Unavailable(reason)

UnavailableReason =
  EmptyLineage
  | OrdinalUnavailable
  | ExactPublishedSetInvalid(defects)
  | StoreUnavailable
```

`EmptyLineage` 只有在 Store root 健康、published 目录存在且当前 raw lineage 上没有 slot 时成立；
是否允许 fresh bootstrap 由 Host 结合 strict raw topology 决定。

## 7. Resume、Restore 与职责边界

Store：

- point read/write；
- shape、size、checksum 与 commitment validation；
- atomic component replace；
- atomic directory publication；
- 返回 structural defects。

Planner：

- `NoBuild / Inherit / Maintain`；
- frozen source、route 与 prior context；
- 把 defects 转为一个 bounded `RestorePlan` 或 `RestoreUnavailable`；
- 不自动递归扫描 source chain。

runner 复用一个内部 primitive：

```text
EnsureFinalRecapBlock(plan, frozen input, rolling checkpoint?)
```

- Building Resume 与 Published Restore 共用该 primitive；
- 两者的外层 authority 不合并：只有 Published directory 占 ordinal；
- selector 不边读边修；
- `RestoreUnavailable` 保持 exact ordinal unavailable，不触发 fallback；
- Host 只在外部状态变化或显式运维请求后重试 unavailable restore。

## 8. MVP 与 deferred hardening

MVP：

- exact-point validation；
- strict Parent-chain ordinal；
- Building missing-only Resume；
- exact selected-slot bounded Restore；
- rolling checkpoint；
- envelope-last repair；
- Galatea/老王 multi-cursor E2E；
- Prepared exact reopen。

Deferred：

- full-generation scrub；
- proactive/periodic self-heal；
- recursive source repair；
- dependency scheduler 与 persisted retry trigger；
- per-step distinct prior context；
- checkpoint chain/history；
- manifest 多副本/自动 metadata healing；
- global membership ledger；
- slot-directory deletion detection；
- multi-generation management；
- multi-process writer；
- backup/replication、Merkle/signature/tamper evidence；
- dynamic retrieval、vector memory 与 multi-hop graph memory。

后四类 Memory 能力属于未来 Memory subsystem，不进入 Recap V4。

## 9. 实施切片候选

若本文成为 canonical target，首次实施压为四个纵向包：

1. **Contracts + Publish/Read**
   - Recap vocabulary 与 neutral candidate contract；
   - Store root、building/published codec；
   - Complete/eligible validation；
   - atomic publish、strict select/materialize。
2. **Planner + Build/Resume**
   - frozen inputs；
   - Maintain/Inherit；
   - endpoint route、per-block prior context；
   - rolling checkpoint 与 missing-only reopen。
3. **Exact-slot Restore + Online lifecycle**
   - defects → bounded RestorePlan；
   - envelope-last restore；
   - no fallback；
   - Galatea E2E 与 Prepared。
4. **Cutover**
   - composition/CLI 最小入口；
   - current DerivedMemory 删除；
   - real SessionJournal acceptance。

不以 full scrub、广泛运维 CLI 或未来 Memory retrieval 作为首次 replacement 的完成门槛。

## 10. adversarial review gates

回写 canonical 文档前必须证明：

1. 每个 crash point 只得到 Building 或 Published，不出现半 membership。
2. publication temp 先原子 seal 为 final filename；`publication.json` 在 directory rename 前仍不
   成为 Published authority。
3. published envelope missing 时 directory membership 仍保留。
4. manifest cache 与 publication 不一致时 online 只有一个 winner。
5. rolling checkpoint crash 后只补当前 block missing suffix。
6. rolling checkpoint 损坏只使当前 block 从 frozen source 重跑。
7. final block 已完成的其他 blocks 不重跑。
8. source set repair/change 不改变 frozen build input。
9. repair component 后、envelope 前 exact slot 仍 not-ready。
10. envelope token 防止 selection/materialization 跨 repair 混读。
11. off-lineage published directory不参与当前 boundary ordinal。
12. `EmptyLineage`、ordinal 不足、exact slot invalid、Store unavailable 可区分。
13. 删除整个 Recap Store 后 Prepared 仍 exact reopen。
14. Recap 与未来 query-dependent Memory 的命名/ownership 不重叠。
15. source inputs 的多 block copy 被同一个 source envelope token 前后夹验，不产生跨 revision
    mixed source。
16. final publication gate 阻止 retroactive anchor insertion 与 concurrent publisher race。
17. manifest self-check 与 `BlockPlanSha256` 能拒绝 valid-JSON accidental mutation 或旧 work cache。
18. Published Restore 保持 frozen plan exact，只改变 block commitments/envelope token。
19. Create/reset crash 不把 partial root 伪装成 healthy empty Store。

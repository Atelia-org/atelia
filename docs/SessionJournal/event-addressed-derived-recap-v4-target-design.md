# SessionJournal Event-addressed Derived Recap V4：目标设计

> **状态**：Target Shape / Rule
> **日期**：2026-07-30
> **核心概念**：
> [EADR 核心概念](event-addressed-derived-recap-concepts.md)
> **配套计划**：
> [EADR V4 实现与替换计划](event-addressed-derived-recap-v4-implementation-plan.md)
> **Post-R3 配置设计**：
> [Repo-owned RecapPlannerConfig](recap-planner-config-repository-design.md)
> **Post-R3 Cadence 设计**：
> [Derived Recap Cadence](derived-recap-cadence-target-design.md)
> **Post-C2 HistoryLoad 设计**：
> [Derived Recap History Load](derived-recap-history-load-target-design.md)
> **Post-C3 Host integration 设计**：
> [DerivedRecap Host Integration](derived-recap-host-integration-target-design.md)
> **Galatea cutover 计划**：
> [Galatea → SessionJournal + DerivedRecap](galatea-session-journal-cutover-plan.md)
> **化简审阅记录**：
> [V4 化简候选](event-addressed-derived-recap-v4-simplification-candidate.md)
> **取代的候选设计**：
> [DerivedMemory V3 candidate](superseded/derived-memory-v3-candidate/derived-memory-next-target-design.md)
> **实施状态**：R0 Contracts + Publish/Read、R1 Planner + Build/Resume、R2 Exact-slot
> Restore + Online lifecycle、R3 Cutover + CLI + real-data acceptance均已完成；具体证据只在
> implementation plan维护。Post-R3 C0 HistoryUnit cadence、C1 repo-owned config
> document/composition与C2 CLI/online authority cutover也已完成；C3 Galatea real-repo
> acceptance前先执行 H0～H2 HistoryLoad cutover

## 0. 一句话目标

EADR 用一个有限、常驻、可持续维护的 `DerivedRecapSet` 近似替代无限增长的 cold raw prefix：

```text
raw RefId + Parent lineage
  -> frozen Building plan
  -> blocks 独立 Maintain / Inherit / bounded catch-up
  -> atomic directory promotion
  -> strict Published ordinal
  -> selected Recap + admission 后 dependency-closed raw suffix
```

V4 不恢复 Job/Attempt/Settlement/Finalization transaction workflow，不建立 derived lineage，不承诺
provider exactly-once 或 byte-identical LLM regeneration。

## 1. Recap subsystem

目标 assemblies：

| Assembly | 职责 |
|---|---|
| `Atelia.SessionJournal.DerivedRecap.Store` | event-addressed point IO、validation、Building/Published directories、strict selection descriptor 与 structural defects |
| `Atelia.SessionJournal.DerivedRecap.Planner` | trigger、NoBuild/Maintain/Inherit、frozen plan、bounded Resume/Restore |
| `Atelia.SessionJournal.DerivedRecap.Maintainers` | concrete `IRecapBlockMaintainer`、rewrite profiles 与 prompts |

依赖：

```text
DerivedRecap.Store ───────> SessionJournal neutral context contracts
DerivedRecap.Planner ─────> Store + SessionJournal neutral contracts
DerivedRecap.Maintainers ─> SessionJournal neutral contracts

SessionJournal.Cli / Agent Host
  └─ composition root：组合三者
```

约束：

- raw `Atelia.SessionJournal` 不引用 concrete Recap assemblies；
- Store 不引用 Planner 或 Maintainers；
- Planner 只接收注入的 `IRecapBlockMaintainer`；
- Store 不回调 Planner；
- Store 与 Planner 都不得修改 raw SessionJournal；
- current `MemoryPack* / IMemoryBlockMaintainer` 已在 R0 完成一次无兼容层 contract cutover：
  recap-specific API 改为 Recap，context-header projection 改为 `ContextHeader*`；
- persisted `MaintainerId` values、`roleplay.*` keys 和 embedded prompt logical names不随类型改名。

## 2. Durable layout

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

`building/<anchor>/` 不占 ordinal。`published/<anchor>/` directory entry 存在即表示 membership；
其中 envelope 或 payload 损坏不撤销 ordinal。

### 2.1 Store root

```text
RecapStoreHeader {
  Schema
  RefId
}
```

- `store.json` healthy 且 `building/`、`published/` required directories 都存在，Store 才 Ready；
- missing/malformed root 返回 `StoreUnavailable`，不得伪装成 `EmptyLineage`；
- `CreateStore` 是显式 storage operation，可用于 fresh session 或 existing-session rebuild；
- fresh empty-context request 另由 Host 验证 strict raw bootstrap topology；
- Create 在 sibling temp root 完成全部文件/目录并 durable 后 atomic rename，或最后原子安装
  `store.json` 作为 root commit marker；
- reset 先 atomic rename/quarantine 整个 ref root；新 root commit 前崩溃稳定 unavailable；
- 不持久化 `GenerationId / Available / Unavailable / Reason` 状态机。

### 2.2 Filename 与 backend capability

- `RefId`：现有 16 位小写十六进制；
- `EventAddress`：现有 16-byte binary codec → 32 位小写十六进制 filename；
- `RecapBlockId`：有长度上限的稳定 ASCII token；
- 所有路径必须通过 root/safe-descendant 与 symlink/reparse ancestor guard。

Store backend 必须支持：

- same-directory file create/replace；
- same-filesystem sibling non-empty directory atomic create-new rename；
- destination-exists fail-fast，不覆盖 Published directory；
- file flush/fsync 与 directory-entry durability barrier；
- rename 前关闭会阻止 move 的 handles；
- per-Ref single coordinator；publish、restore、reset 与 materialization有明确读写协调。

无法提供这些保证时 fail unsupported，不能退化为 copy + delete。

## 3. Frozen Building plan

> **实现分期注记**：R0 持久化完整 union 的 canonical shape，但只允许
> `Maintain { Source = Empty }` 创建 Building。`Inherit` 与
> `Maintain { Source = Existing }` 必须等 R1 exact source envelope double-read/copy 后启用；
> 这不改变下述最终态 contract。

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
    MaintainerCapabilityFingerprint
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

- source inputs 先于 manifest durable；manifest 先于任何 Maintainer 调用或 output；
- 无 healthy manifest 的 pre-build files 是 orphan，整体隔离，不被新 manifest复用；
- `ManifestPayloadSha256` 覆盖 manifest 的 canonical bytes（排除该 hash 字段本身），包括
  schema、RefId、SetAdmissionAnchor 与 ordered block plans；它只检测 accidental corruption，
  不是 identity；
- `SetAdmissionAnchor` 等于 directory key，是同一 `RefId` 上 replay-safe raw boundary；
- `RecapBlockId` 与 Target 均不得重复；
- explicit discriminated union 决定 Maintain/Inherit，不从 nullable fields猜测；
- 每个Maintain plan冻结exact
  `(MaintainerId, Target, MaintainerCapabilityFingerprint)`；fingerprint是opaque
  `sha256:<64 lowercase hex>`，Store/Planner不从当前catalog推断或解释其preimage；
- Existing first replay start 由 frozen input `AbsorbedThrough` 得出；
- Empty 显式保存 replay seed；
- 只持久化 ordered `CatchUpThrough[]`；step start 由 source cursor/previous endpoint 推导；
- endpoints 同 lineage、strictly increasing、dependency-closed bounded materializable；
- final endpoint 等于 `SetAdmissionAnchor`；
- per-block prior context整条 route复用，Inline anchor 是 first replay start 的同-lineage ancestor；
- prior context 不读取当前 Building 的 partial output；
- raw-core candidate limits 与 Store publication gate 使用同一 versioned validator。

### 3.1 Exact source snapshot

每个 Existing/Inherit source 使用 exact source envelope read：

```text
read source EnvelopeSha256
  -> copy required blocks by envelope commitments
  -> validate copied payloads
  -> reread source EnvelopeSha256
  -> unchanged: write target manifest
  -> changed: isolate pre-manifest build and retry
```

多个 source sets 分别执行。manifest 生效后 Resume/Restore 只读 build-local frozen inputs，不读 live
source payload。

source block cursor 可以早于其 source container：

```text
sourceBlock.AbsorbedThrough
  <= SourceSetAnchor
  < target SetAdmissionAnchor
```

catch-up endpoints 也可以早于 SourceSetAnchor，因为它们不是 set admission。

## 4. Recap block 与 rolling checkpoint

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

`PayloadSha256` 覆盖 ID、Target、cursor 与 Content。`BlockPlanSha256` 覆盖 authoritative frozen
manifest 中该 block 的 exact discriminated `RecapBlockPlan` canonical bytes，把 wrapper 绑定到
当前 phase authoritative frozen plan。

规则：

- Maintain final `AbsorbedThrough == SetAdmissionAnchor`；
- Inherit exact-copy payload/cursor，不调用 Maintainer；wrapper 使用当前 Inherit plan hash；
- Maintainer 审阅后 Content 可不变，但 cursor 仍推进；
- final block 与 rolling checkpoint 使用同一 shape；
- 每 block 最多一个 `work/<id>.json`；
- 每 step 用 temp → flush/fsync → atomic replace → directory barrier 更新 rolling checkpoint；
- healthy checkpoint 必须匹配 authoritative `BlockPlanSha256` 和某个 frozen endpoint；
- healthy checkpoint 后只补 route suffix；
- checkpoint missing/damaged 时，仅该 block 从 frozen source 重跑完整 route；
- final/checkpoint 的 `damaged` 只表示 Store 已在大小上限内完整捕获 bytes、因而能生成
  exact state token 的可替换内容缺陷；若 bounded read 本身失败或文件超过上限，则返回 typed
  `Unavailable`，不得为获取 token 再次读取或计算无界 hash，也不得自动覆盖该文件；
- final endpoint 先写 rolling checkpoint，再原子安装 final block；
- checkpoint 不参与 Complete、Published、ordinal 或其他 block 输入。

Published `inputs/`/`work/` 只影响 Restore capability，不影响正常 `CanMaterialize`。`work/` 可删除；
`inputs/` 是 frozen restore dependency，普通 cache cleanup 不得删除。

## 5. Publication

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

`EnvelopeSha256` 覆盖 canonical envelope bytes（自身字段除外）。它是 optimistic snapshot token，
不是 set identity。

publication protocol：

```text
Building final blocks ready
  -> CanPublish
  -> flush/close/fsync manifest, inputs and every final block
  -> durability barriers for their containing directories
  -> write publication.<nonce>.tmp
  -> flush/close/fsync
  -> atomic rename temp -> building/<anchor>/publication.json
  -> directory barrier
  -> final CanPublish + latest-anchor revalidation
  -> atomic create-new rename
     building/<anchor> -> published/<anchor>
  -> both parent-directory durability barriers
```

directory promotion 的 durability precondition 覆盖该 publication 承诺的全部 manifest、frozen
inputs 与 final blocks，而不只是 `publication.json`。在 required payload 或其 directory entry
尚未 durable 时不得建立 Published membership。

authority：

- directory promotion 前 manifest 是 Building authority，final-name publication 只是 sealed candidate；
- atomic directory rename 同时建立 Published phase、membership 与 publication authority；
- Published normal read只信 `publication.json`；
- co-located manifest 只是 envelope-loss restore cache，不参与 normal health，不与 publication
  形成双 authority conflict。

final gate 在 per-Ref exclusive scope 内要求 target anchor 是 current-lineage latest Published anchor 的
strict descendant，禁止 retroactive insertion。

descriptor 绑定：

```text
RefId + SetAdmissionAnchor + EnvelopeSha256
```

materialization 在 blocks 前后重验 envelope token，并核对每个 block commitment；跨 Restore 混读
必须 fail-fast。byte-identical Restore token 不变是安全的。

## 6. Validation、selection 与 online composition

不落盘 cross-product state enum：

```text
CanPublish(build)
  -> Publishable(candidate)
  | Defects[]

CanMaterialize(published)
  -> Descriptor(EnvelopeSha256)
  | Defects[]

RestoreAsync(exact anchor + expected raw head)
  -> Store exact inspection
  -> internal bounded ephemeral actions
  | typed unavailable/retryable
```

`CanPublish` 同时验证：

- manifest/source/mode/route/size/count shape；
- required final blocks；
- 1～128 contributions；
- unique supported targets；
- non-empty exact text；
- shared per-block UTF-8 hard limit；
- `AbsorbedThrough <= SetAdmissionAnchor` raw ancestry；
- no retroactive publication。

selection：

```text
open exact RefId Store
  -> unavailable: StoreUnavailable
  -> walk completion boundary raw Parent chain
  -> point lookup published/<EventAddress>
  -> directory absent: 不计数
  -> directory present: 计数，不论 envelope/payload 当前是否 valid
  -> exact ordinal
       -> CanMaterialize success: Selected(descriptor)
       -> defects: ExactPublishedSetInvalid
```

typed reasons：

- `EmptyLineage`；
- `OrdinalUnavailable`；
- `ExactPublishedSetInvalid(defects)`；
- `StoreUnavailable`。

`EmptyLineage` 只有在 healthy Store root、required directories存在、当前 raw lineage无 Published set
时成立；是否 fresh bootstrap 由 Host 结合 raw topology决定。

selected Recap blocks 提供 candidate-level `SetAdmissionAnchor` 与 per-contribution
`AbsorbedThrough`。raw core 验证 ancestry，然后拼接 anchor 后 dependency-closed raw suffix。
绑定 exact engine 的 context-candidate Source 用 header-only raw lineage驱动 Store point lookup，
并在 selected admission anchor 上从 raw authority解析 `SessionContextAnchorSetupReferences`；
setup refs 不进入 Recap manifest/publication。Store 不另开 raw repository，也不按目录名推断
lineage。

Prepared 保存 exact materialized Context/request commitment；reopen 不访问 Recap Store。

## 7. Planner 与 Galatea multi-cursor

Planner配置已拆成四层：persisted `RecapPlannerConfigDocument`表达 repo-owned operator
intent；resolved `RecapPlanningInputs`表达本次 active catalog、cadence与 policy；
`RecapPlanningLimits`表达新 planning ceilings；`RecapProtocolHardCaps`则是 code/schema-owned
frozen-plan边界。它们合起来至少描述：

- `RecapCadenceConfig`：versioned HistoryLoad estimator + minimum recent reserve + build interval；
- `MaxRawGrowthEventCount` raw traversal hard-limit，与 cadence计量分离；
- replay-safe admission selection；
- active `RecapBlockId / Target / MaintainerId / MaintainerCapabilityFingerprint` catalog；
- content/route/call limits；
- NoBuild/Maintain/Inherit policy；
- bounded endpoint policy。

一次 build：

```text
open exact raw boundary + healthy Recap Store
  -> find current-lineage latest Published set
  -> exact HistoryLoad growth < reserve + interval: NoBuild
  -> threshold reached但无 cadence-safe replay boundary: NoBuild
  -> select strict-later SetAdmissionAnchor，admission后至少保留 reserve
  -> decide each block Maintain/Inherit
  -> exact-envelope copy frozen sources
  -> freeze route + per-block prior context
  -> write manifest
  -> execute phase-specific final-block actions
  -> CanPublish + latest revalidation
  -> atomic directory promotion
```

Galatea：

```text
客户老王 block：
  A1  = real AbsorbedThrough
  A8  = weekend Published set, Inherit client block
  A12 = later weekend Published set, still cursor A1
  A20 = return-to-work target

source container = A12
frozen old cursor = A1
CatchUpThrough = A5 -> A11 -> A20
```

A5/A11 只是 rolling progress endpoints；只有 A20 Published。Tag/policy 未审阅时选择 Inherit；
Maintainer 实际审阅后即使正文不变，也属于 Maintain 并推进 cursor。

## 8. Resume 与 Published Restore

Store 返回 structural defects与 exact per-block capability；Planner在一次调用内创建 ephemeral
bounded actions，或返回 `RestoreUnavailable(reason)`。Building Resume 与 Published Restore只复用：

```text
frozen raw validator
pending replay-window preparation
one Maintainer step runner
```

外层 authority、Store mutation与 envelope protocol不合并：

- Building Resume：manifest 是 plan authority，合法 final block直接 skip；
- Published Restore：healthy exact publication存在时，其 `FrozenPlanSnapshot` 是唯一 plan
  authority；仅当下述 envelope-loss winner规则成立时，co-located exact manifest可作为一次性
  restore witness。membership始终保留。

Published Restore：

- frozen plan、anchor、roster、mode、source、route、prior context、MaintainerId、
  MaintainerCapabilityFingerprint 与 per-block
  `MaxContentUtf8Bytes` exact不变；当前 operator trigger/planning ceilings不参与恢复裁决；
- 只允许 regenerated block commitments 和 envelope token 改变；
- component 逐个 atomic replace，publication envelope last；
- block replace 后、envelope 前 exact set保持 unavailable；
- self-check healthy、匹配 authoritative block plan 的 pending replacement 可直接纳入新 envelope，
  不重复调用 Maintainer；
- publication envelope missing时，可用 self-check healthy manifest cache + frozen inputs + final
  blocks 全量 revalidate 后重建；
- manifest cache 与 publication 都不可用时 ordinal仍保留，但 RestoreUnavailable；
- selector 不边读边修，online lifecycle 不递归扫描无界 source chain。

authority winner进一步形式化为：

- publication能 canonical decode、自校验且 identity匹配 exact directory时，它始终是唯一 plan
  authority；manifest cache冲突不参与裁决；
- publication missing，或在 bounded read内完整捕获的 bytes因 shape/checksum/canonical validation
  无法形成自校验 authority时，self-hashed、shape/identity均健康的 manifest只能作为一次性的
  **envelope-loss restore witness**；
- publication自校验健康但 identity/anchor与目录冲突属于 coherent authority conflict，不得
  fallback manifest；
- publication authority file的 I/O/permission fault或在完整读取前超过资源上限，不是可证明的
  envelope damage：RestoreUnavailable，不 fallback manifest；
- final/input/work component的 I/O/permission fault或资源超限只令该 block capability
  unavailable/unusable，不发可写 component CAS token；它不重新裁决 publication/manifest winner；
- manifest witness不参与 normal eligibility，不形成第二个在线 authority；它只授权按 exact
  manifest全量重验 frozen inputs/final blocks并重建 envelope；
- Published Restore使用 Published专用 component CAS 与 envelope-last API；不与 Building Resume
  合并成带 phase分支的 public workflow。代码级只共享 `RecapFrozenPlanRawValidator`、
  `RecapPendingWindowPreparer`、`RecapMaintainerStepRunner`；phase-specific Store API可复用底层
  atomic-replace primitive。

MVP 不做 full-generation scrub 或 proactive self-heal，只做 exact-point validation、selected-slot
bounded Restore 与显式运维。

## 9. 必须保留的约束

1. Recap/Memory/Context 三层边界。
2. `SetAdmissionAnchor` 与 `AbsorbedThrough` 分离。
3. Building/Published 原子 phase boundary。
4. Published invalid 仍占 strict ordinal，exact failure 不 fallback。
5. exact source envelope double-read 后才冻结 inputs。
6. manifest 与 block plan commitments 防止 valid-JSON accidental drift。
7. endpoint-only route；中间 endpoint 不成为 set。
8. single rolling checkpoint 只是可丢弃 progress cache。
9. Store defects/capabilities → Planner bounded ephemeral restore actions，Store 不调 Maintainer。
10. Prepared exact reopen 不访问 Recap Store。
11. cadence只测量实际进入 Context 的 dependency-closed HistoryUnits所贡献的 HistoryLoad；
    raw API failed/retry不推进 cadence。
12. `SetAdmissionAnchor` 后至少保留 configured minimum recent HistoryLoad。

## 10. 非目标

- current v2/v3 data migration、fallback 或 compatibility adapter；
- Job/Attempt/Outcome/Settlement/Finalization；
- `SetId`、`PreviousSetId`、derived latest pointer 或 independent ordering；
- full-generation scrub、periodic self-heal、recursive source repair；
- checkpoint chain/history；
- per-step distinct prior context；
- dependency scheduler/persisted retry trigger；
- global membership ledger 与带外 directory deletion detection；
- byte-identical LLM regeneration；
- model/provider audit identity；
- multi-process writers；
- tamper evidence、Merkle/signature、backup/replication；
- dynamic retrieval、vector memory 与 multi-hop graph memory。

## 11. 验收

- Store Create/reset/publish 各 crash point不产生假 healthy root 或半 membership；
- publication temp 先 seal final filename，再原子 directory promotion；
- source multi-block copy 被同一 exact envelope token 前后夹验；
- final gate拒绝 retroactive insertion 与 concurrent publish race；
- rolling checkpoint健康时只补 suffix，损坏时只重跑该 block；
- 其他 healthy final blocks不重跑；
- Maintain unchanged content仍推进 cursor；
- 老王 block可跨 leisure sets Inherit，随后从真实 old cursor分段 catch up；
- A5/A11 不进入 ordinal或成为 inheritance source；
- Published envelope missing保留 membership；
- block-first/envelope-last中间态 exact not-ready；
- Restore不改变 frozen plan；
- descriptor ETag阻止跨 Restore 混读；
- latest/middle invalid Published set均保持 ordinal，不 fallback；
- off-lineage Published directory不可见；
- `EmptyLineage`、ordinal不足、exact invalid、Store unavailable可区分；
- growth HistoryLoad低于 `R+B` 时 NoBuild；达到 threshold且存在 cadence-safe replay boundary时
  Build，Published后 recent load至少为 R且 absorbed load至少为 B；
- API failed/retry不推进 cadence；dependency closure无法留下 exact R时只允许多留、不允许少留；
- 10k cold prefix selection不读取未选 raw event payload；
- Prepared 后删除整个 `derived/recap/v4`仍 exact reopen；
- active target 不再使用 DerivedArtifactSet/DerivedMemory 作为 V4 Recap 领域名。

## 12. Maintainer capability schema cutover

durable layout、Store header、frozen input与block schema继续使用v4；manifest与publication envelope
升级为v5，使canonical payload hash覆盖每个Maintain plan的
`MaintainerCapabilityFingerprint`。v4 manifest/publication不提供兼容读取、默认值或current-ID
推断。首次采用v5前必须显式处理旧sidecar：只有Building时执行`recap abandon-building`；存在
Published membership时执行带exact `--confirm-ref`的`recap reset`，随后显式`recap run`重建。

# SessionJournal Event-addressed Derived Recap：核心概念

> **状态**：Canonical Target Vocabulary（EADR V4）
> **日期**：2026-07-30
> **目标设计**：
> [Event-addressed Derived Recap V4](event-addressed-derived-recap-v4-target-design.md)
>
> 本文只定义 EADR V4 的领域术语与不变量。目录布局、checksum 和原子写入属于 target design；
> 实施顺序属于 implementation plan；current P6 与历史实现仍按原文档中的 DerivedMemory /
> ArtifactSet 名称解释。

## 0. Authority

- raw SessionJournal events 是 session correctness source；
- `EventFrameHeader.Parent` 决定 raw lineage 与顺序；
- Recap 是可删除、可重建的 derived sidecar，不成为 raw fact；
- Published Recap 只决定哪个有限近似进入 strict ordinal；
- 实际进入 completion request 的 exact Context 由 raw `CompletionRequestPrepared` 固定，reopen
  不访问 Recap Store。

`RefId`、`EventAddress` 与 raw Parent traversal 的底层定义见
[EventJournal 使用指南](../../src/EventJournal/README.md)。

## 1. Recap、Memory 与 Context

### Recap

`Recap`（前情提要）是对 cold raw history prefix 的有限、有损、常驻近似。它的目的不是保存所有
事实，而是让无限增长的 session 仍能用有限上下文继续运行。

一个 `DerivedRecapSet` 包含多个 coherent `DerivedRecapBlock`，并通过
`SetAdmissionAnchor` 与之后的 exact raw suffix 接合。autobiography、world understanding、
relationship state 与 open threads 都可以是 Recap blocks。

### Memory

`Memory` 是更广的长期信息能力，包括 dynamic retrieval、episodic/semantic index、vector recall、
entity/time lookup 与 multi-hop graph query。Memory 通常 query-dependent，不要求常驻每次请求、
共享一个 admission anchor 或替代完整 cold prefix。

Recap 可以成为未来 Memory 的一个来源，但 EADR V4 不实现动态召回系统。

### Context

`Context` 是一次 completion 实际看到的有限输入：

```text
governing setup
+ selected DerivedRecapSet
+ admission anchor 后 dependency-closed raw suffix
+ future retrieved Memory / pinned contributions
+ tools 与 runtime request material
```

关系：

```text
Raw facts -> Recap / Memory derived views -> Context materialization -> Prepared exact fact
```

## 2. Identity、anchor 与 cursor

| 术语 | 定义 |
|---|---|
| **RefId** | ref lifetime 的 durable identity；branch name 只是人类 selector。 |
| **Raw Parent lineage** | 从 exact boundary 沿 raw `Parent` 得到的祖先链；决定 set 可见性和顺序。 |
| **EventAddress** | raw event 的精确地址，也是 Recap set 的 event-addressed key；不是 ordinal。 |
| **DerivedRecapSet** | 在一个公共 admission anchor 上整体 Published 的 coherent Recap baseline。 |
| **DerivedRecapBlock** | 一个稳定 `RecapBlockId + Target` 上的有限派生文本及其 progress cursor。 |
| **SetAdmissionAnchor** | set-level 公共 raw boundary；raw suffix 从其后开始，但不声称所有 blocks 都吸收到这里。 |
| **AbsorbedThrough** | per-block 已处理到的最后 raw boundary，也是后续 catch-up 的真实起点。 |
| **SourceSetAnchor** | 提供 frozen old block 的 Published source container；不等于该 block 的 cursor。 |

必须满足：

```text
block.AbsorbedThrough <= enclosingSet.SetAdmissionAnchor

sourceBlock.AbsorbedThrough
  <= SourceSetAnchor
  < targetSet.SetAdmissionAnchor
```

`<=` 表示同一 raw Parent lineage 上的 inclusive ancestor relation。

## 3. Durable phase 与 predicates

durable phase 只有：

```text
Building
Published
```

- `Building`：有 frozen manifest，但不占 ordinal；
- `Published`：原子 membership boundary 已建立，从此占 strict ordinal；
- Published payload 损坏不会把它退化成“从未发布”。

其余都是 validation/query result，不组成 persisted 状态机：

```text
CanPublish(build) -> publication candidate | defects
CanMaterialize(published) -> descriptor | defects
IsVisible(anchor, completionBoundary) -> bool
Planner.TryCreateRestorePlan(defects) -> plan | unavailable
```

`Complete` 与 `OnlineEligible` 仍可作为解释性 predicate：

- Complete：frozen plan 所需 final blocks 全部结构合法；
- OnlineEligible：同时满足共享 candidate shape、limits 与 raw ancestry；
- 只有二者都成立才可以 Publish。

`Healthy` 可以作为“当前 `CanMaterialize` 成功”的口语，不是独立 persisted state。

## 4. Maintain、Inherit 与 Catch-up

### Maintain

`Maintain` 实际处理一个或多个 bounded raw segments，并把 final
`AbsorbedThrough` 推进到目标 `SetAdmissionAnchor`。正文可以不变；只要区间确实经过审阅，cursor
仍推进。

### Inherit

`Inherit` 不调用 Maintainer，exact-copy frozen source payload，保持 `AbsorbedThrough`。它如实
表示该 block 尚未处理旧 cursor 与新 admission 之间的 history。

Planner 的 whole-set 暂缓是 `NoBuild`；block-level 暂缓落为 `Inherit`。`Defer` 可以作为
diagnostic reason，但不是 canonical persisted mode。

### Catch-up

落后 block 从真实 old cursor 沿 frozen ordered endpoints 分段 Maintain，最终到达 common
`SetAdmissionAnchor`。中间 endpoint：

- 不是 set admission；
- 不进入 `NthPrevious`；
- 不供其他 block 继承；
- 可以早于提供 old block 的 `SourceSetAnchor`。

每个 block 最多持久化一个 rolling checkpoint。健康 checkpoint 后只补 missing suffix；checkpoint
缺失或损坏时，仅该 block 从 frozen source 重跑完整 route。它是可丢弃 progress cache，不是
workflow authority。

## 5. Frozen input

一次 Maintain 的 durable input 至少包括：

- build-local exact old block；
- stable MaintainerId；
- ordered `CatchUpThrough[]`；
- per-block frozen prior context 或显式 Empty；
- content limit。

每步 start 由 old cursor 或前一 endpoint 推导，不重复持久化。

prior context 不得读取当前 Building 的 partial results。非空 snapshot 的 admission anchor 必须不晚于
first replay start，保证 parallel order 与 crash/reopen 不改变输入。

## 6. Published membership 与 strict ordinal

`NthPrevious(n)` 从 exact completion boundary 沿当前 `RefId` raw Parent lineage逆序 point lookup
Published directories：

- `n = 0` 是最近 Published set；
- Building、rolling checkpoint 与 off-lineage Published set 不计数；
- exact set 可 materialize时返回 descriptor；
- exact set invalid 时返回 typed unavailable，不扫描更旧 set；
- membership 数量不足才是 ordinal unavailable。

strict ordinal 的故障模型要求 Published directory entry 未被带外删除。没有 membership ledger 时，
单个 directory 被外部彻底删除不可检测；这明确超出首版 correctness guarantee。

## 7. Resume、Restore 与 Rebuild

| 术语 | 定义 |
|---|---|
| **Resume** | 按 Building frozen plan 只补 missing/damaged final block 或 rolling suffix。 |
| **Restore** | 在 membership 不变、frozen plan exact 不变的前提下恢复同一个 Published set。 |
| **Replan** | 隔离尚未 Published 的 Building，再按当前 policy 建立新 plan。 |
| **Rebuild** | 整个 Recap Store 丢失/reset 后，从 raw/config/Recap Maintainers 建立新的 sidecar。 |

Store 只返回 structural defects。Planner 产生一个 bounded `RestorePlan` 或
`RestoreUnavailable(reason)`；selector 不边读边修，Store 不调用 Maintainer。

Published `inputs/` 或 `work/` 损坏不影响正常 materialization，只影响未来 Restore。无法按 frozen
plan恢复时 exact ordinal保持 unavailable；不得借 Restore replan 或 fallback。

## 8. 必须避免的术语混用

- Recap ≠ 广义 Memory
- Memory ≠ 本次 Context
- branch name ≠ `RefId`
- `EventAddress` ≠ ordinal
- `SetAdmissionAnchor` ≠ `AbsorbedThrough`
- `SourceSetAnchor` ≠ source block cursor
- `Inherit` ≠ Maintain 后正文无变化
- Building ≠ Published
- Published ≠ 当前可 materialize
- strict ordinal ≠ “第 n 个当前健康 set”
- rolling checkpoint ≠ `DerivedRecapSet`
- Resume ≠ Restore
- Restore ≠ Replan
- Prepared snapshot ≠ Recap set identity

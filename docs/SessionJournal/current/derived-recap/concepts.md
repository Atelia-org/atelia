# SessionJournal Event-addressed Derived Recap：核心概念

> **状态**：Canonical Current Vocabulary（EADR V4）
> **日期**：2026-07-30
> **目标设计**：
> [Event-addressed Derived Recap V4](durable-target.md)
>
> 本文定义 current EADR V4 的领域术语与不变量。R0～R3 已完成 contracts、Store、Planner、
> Maintainers、CLI/online composition、旧 DerivedMemory 删除与 real-data acceptance。目录布局、
> checksum 与原子写入的 normative Shape/Rule 详见 target design；current API、wire 与实施状态必须
> 以对应 component README、code 和 tests 为准。implementation plan 是已关闭的 delivery/evidence
> record，不再承担 current 状态 authority。旧 P6 与更早实现只在明确标记的 historical/frozen
> 文档中继续按 DerivedMemory / ArtifactSet 名称解释。

## 0. Authority

- raw SessionJournal events 是 session correctness source；
- `EventFrameHeader.Parent` 决定 raw lineage 与顺序；
- Recap 是可删除、可重建的 derived sidecar，不成为 raw fact；
- Published Recap 只决定哪个有限近似进入 strict ordinal；
- 实际进入 completion request 的 exact Context 由 raw `CompletionRequestPrepared` 固定，reopen
  不访问 Recap Store。

`RefId`、`EventAddress` 与 raw Parent traversal 的底层定义见
[EventJournal 使用指南](../../../../src/EventJournal/README.md)。

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

### HistoryUnit 与 HistoryLoad

`HistoryUnit`是 raw SessionJournal投影出的一个 dependency-closed Context message，也是
replay-safe boundary对齐所使用的结构单位。

`HistoryLoadUnit`是 Planner用于 recent reserve与rolling interval的内部负载计量单位。它由
versioned estimator从 ordered HistoryUnits导出：

- 不表示推理模型/provider token、计费单位或 context-window容量；
- unit estimator独立测量每个 HistoryUnit，Planner projector对window直接求和；
- 不写入 raw event或Recap Store；
- 只在同一 estimator identity下可比较；
- API failed/retry不形成 HistoryUnit，因此不贡献 HistoryLoad；
- Building安装后，Resume/Restore不重新估算 HistoryLoad。

current NewPlanning cadence 使用 repo-owned HistoryLoad config V2 与 versioned estimator；
`HistoryUnit` count 只承担 window structure、baseline 对齐与 raw safety bound，不再是 scheduling
trigger。Building/Resume/Restore 继续服从 frozen authority，不读取 active cadence config，也不重新测量
HistoryLoad。设计与 cutover 约束见
[Derived Recap History Load](history-load.md)。

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

### Contract normalization gate

结构、字段或变体名称同构，不足以证明两个 contract 可以合并。合并 result、health、durable
phase、state machine 或 opaque authority 之前，必须逐项比较并记录：

1. **合法状态与行为**：合法状态集合、状态 transition、recovery 路径，以及每种结果要求
   operator 采取的 action 是否等价；
2. **authority 边界**：authority 的 owner、构造/签发权限与可伪造性，以及它绑定的 exact
   repository、`RefId`、raw head、plan/restore handle、component state 等身份是否等价；
3. **proof 与 verification obligation**：调用者和实现分别必须证明、重验哪些事实；有意义的
   独立冗余是 correctness evidence，不能仅因实现相似而删除；
4. **durable 语言**：涉及持久化时，reader accepted wire language、canonical bytes，以及各个
   crash point 后 reopen 的可观察行为是否保持不变。

可以共享 internal validation、evidence propagation 或 operational-semantics kernel；本门禁禁止的
是语义坍缩，不是代码复用。当 proof obligation 不同时，必须保留 outer stage-specific typed
results、authority boundary 与 fail-closed behavior，即使它们的目录、字段或控制流形状相似。

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
- ordered `CatchUpBoundaries[]`，每项冻结 `(Address, Setups)`；
- source replay start 与 frozen input cursor 的 exact governing setup refs；
- frozen shared prior context：首轮显式 Empty；后续build把exact previous Published set的全部
  frozen blocks投影成同一个`ContextHeaderPack` snapshot，并复制到每个Maintain plan；
- content limit。

每步 start 由 source replay boundary 或前一 frozen boundary 推导；address 与 setups共同构成
replay authority，执行时不得重新发现后静默替换。

非空 prior context必须来自planning时同一次exact Published source snapshot，包含当前block和其他
blocks的上一版；不得读取当前Building的partial results。其admission anchor必须不晚于first replay
start，保证block执行顺序与crash/reopen不改变输入。neutral maintenance request仍携带`OldBlock`，
但current rewrite以shared prior context作为旧recap唯一prompt表示，避免当前block重复出现。
policy只能把authoritative shared prior按值保留到每个Maintain decision；Evaluator必须拒绝Empty/
Inline kind、anchor或snapshot内容发生变化的输出。

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
- HistoryLoadUnit ≠ provider/model token
- Resume ≠ Restore
- Restore ≠ Replan
- Prepared snapshot ≠ Recap set identity

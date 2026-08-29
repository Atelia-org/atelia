# Galatea Dynamic Memory Puzzle Map

本文记录 Galatea 自主笔记与动态信息召回系统当前已经清晰的底层拼图。它是恢复思路用的工作笔记，不是已经落地的 wire contract；真正改动 `PlayerTurnObservation`、Memo 索引或调度层时，还需要把这里的候选命名、边界和测试补成代码级契约。

## 当前判断

把 Memo 召回信息注入 `PlayerTurnObservation`，并用一个 `RecallBarrier` 聚合“当前 provider-visible context 中已经可见的召回 anchor”，这个方向是合理的。

它和现有 Mailbox 模式是同一类跨 turn 通讯：

```text
role narrative intent  -> TextExtractor artifact
runtime memory effect  -> durable / rebuildable state
future Observation     -> visible recall result
```

需要先扶正几个边界：

- 后续代码和文档统一使用 `RecallBarrier` / `Barrier`，表达“当前上下文已可见召回 anchor 的去重屏障”。
- `{ RecallType, SourceId }` 适合作为去重 key，但它只说明“这个召回粒度对这个来源已经可见过”，不是 MemoPod 存储权威，也不是内容仍然最新的证明。
- `SourceId` 不能长期只是裸 `MemoId`，除非 Galatea 明确永远只有一个角色级 MemoPod。更稳妥的是给它一个 Galatea 侧 canonical source id，例如把 pod identity 与 memo id 一起编码。
- `RecallEntry` 最好只表示 anchor；真正渲染进 Observation 的 payload 可以另有类型，例如 `PlayerTurnRecall`，携带 `RecallEntry` 加上本次注入的 visible text。
- Barrier 的输入应该是本次 Completion 真正会看到的 context candidate / raw tail，而不是 browser recent list，也不是整条 SessionJournal 历史。

## 已有拼图

### MemoPod

源码入口：

- [`Memo`](../../prototypes/MemoPod/Memo.cs)
- [`MemoPod`](../../prototypes/MemoPod/MemoPod.cs)
- [`MemoPodRecall`](../../prototypes/MemoPod/Recall/MemoPodRecall.cs)
- [`MemoPod README`](../../prototypes/MemoPod/README.md)

MemoPod 当前解决的是“一堆 Memo 中如何按语义 query 找回候选 Memo”。`Memo` 已有 stable `Id`、必需的 `ExactText`，以及可空的 `Title`、`Gist`、`Summary` 元数据。

Memo 创建时必须有正文，因为正文是后续摘要、印象、标题与索引的事实来源；`Title`、`Gist`、`Summary` 在创建正文时都可以暂时缺失。未来的 LLM 内容处理管线负责补齐缺失条目，典型路径是补齐 `Summary` 与 `Gist`；很多时候 `Title` 可以在创建正文时直接从角色叙事里一起捕获。

MemoPod 不负责：

- 判断 Galatea 这一轮该不该召回；
- 决定召回 Gist、Summary 还是 ExactText；
- 判断哪些召回已经在当前上下文里可见；
- 把内容注入到 Galatea 的 `Observation`。

也就是说，MemoPod 是底层 corpus 与 semantic recall 组件，不是 Galatea runtime 的 context assembly engine。

### TextExtractor

源码入口：

- [`TextExtractor`](../../prototypes/Galatea/TextExtractor.cs)
- [`GalateaVisibleActionTextRenderer`](../../prototypes/Galatea/GalateaVisibleActionTextRenderer.cs)
- [`TextExtractor / Observation Bridge`](./text-extractor-observation-bridge.md)

`TextExtractor` 解决的是“角色到 runtime”的方向：角色继续用叙事化 Action 行动，runtime 在 turn 边界外读取 visible Action text，提取 typed artifact。这个模式避免要求主线角色模型显式进入 Assistant / Agent / tool-call 行为模式，减少出戏风险。

Mailbox 已经证明这个方向可行：`OutboundMailExtractor` 从角色叙事 Action 中提取 `SendMailIntent`。后续自主记笔记可以复用同一个模式，但应该定义自己的 contract，例如 `CharacterNoteIntent` / `CharacterNoteExtractor`，不要放进 Mailbox namespace。

### PlayerTurnObservation

源码入口：

- [`PlayerTurnObservation`](../../prototypes/Galatea/PlayerTurnObservation.cs)
- [`GalateaFreshInput`](../../prototypes/Galatea/GalateaFreshInput.cs)
- [`GalateaServices`](../../prototypes/Galatea/GalateaServices.cs)

`PlayerTurnObservation` 当前是普通玩家回合的 runtime-owned composite Observation。它已经支持：

- 玩家行动正文；
- runtime 采样的 external local timestamp；
- 0..N 条 `PlayerTurnNotice`，目前是 Codex delegation 的 reply / delivery failure；
- strict canonical render / parse / round-trip；
- adaptive Markdown fence，正文不 trim、不 normalize、不 escape。

这正适合扩展成“同一 Observation 里挂载当前回合需要的 Memo recall blocks”。Recall block 的 authority 应该仍然来自 runtime renderer，而不是外部 caller 自报。

### RecapGrid / Context Candidate

源码入口：

- [`SessionContextCandidate`](../../prototypes/SessionJournal/SessionContextCandidateContracts.cs)
- [`RecapGridContextMaterializer`](../../prototypes/SessionJournal.RecapGrid/Getter/RecapGridContextMaterializer.cs)
- [`GalateaRecapGridReadiness`](../../prototypes/Galatea/GalateaRecapGridReadiness.cs)

Galatea 的“当前上下文”不是简单的最近 N 条消息。Completion 真正看到的是 SessionJournal 在 exact boundary 上选出的 raw tail 加 derived context contributions。动态 recall 去重必须绑定这个 provider-visible materialized context，而不是：

- browser recent view；
- 全部 durable raw history；
- MemoPod 当前全量内容；
- 派生摘要里碰巧复制过的自然语言片段。

这是 `RecallBarrier` 最重要的设计点。

## 建议的数据形状

### RecallType

初始枚举可以按用户设想保持三档：

```csharp
internal enum RecallType {
    MemoGist,
    MemoSummary,
    MemoExactText,
}
```

这三个值表达的是渐进式可见粒度。`Title` 是三个粒度都统一携带的定位字段；差异主要在于除标题外还暴露哪一层内容。

- `MemoGist`：目录/索引级提示，包含 `Title` 与 `Gist`。
- `MemoSummary`：较详细摘要，包含 `Title` 与 `Summary`。
- `MemoExactText`：原始 Memo 正文，包含 `Title` 与 `ExactText`。

因为 `Title/Gist/Summary` 都是可空元数据，recall planner 需要区分“当前 metadata 还没补齐”和“选择这个粒度注入”。更成熟的路径是先由内容处理管线补齐缺失字段，再把对应粒度放入 `PlayerTurnObservation`；MVP 若允许缺字段通过，也应该在 renderer 中显式表达缺失，而不是把 `MemoGist` 悄悄降级成只有正文或只有 id 的提示。

如果未来 recall source 不止 Memo，可以再拆成 `RecallSourceKind + RecallGranularity`。当前阶段把 `Memo` 前缀写进 enum value 是可以接受的，因为它清楚表达这些 anchor 来自 Memo source。

### RecallEntry

`RecallEntry` 建议是去重 anchor，而不是 payload：

```csharp
internal sealed record RecallEntry(
    RecallType RecallType,
    string SourceId
);
```

约束建议：

- `RecallType` 必须是已定义枚举值；
- `SourceId` nonblank、valid Unicode、有 UTF-8 byte 上限；
- `SourceId` 使用 Galatea-owned canonical codec，不直接拼接任意外部文本；
- `{ RecallType, SourceId }` 是 exact de-dupe key。

对于同一 `SourceId`，还可以定义可选 coverage dominance：

```text
MemoExactText covers MemoSummary covers MemoGist
```

MVP 可以只做 exact-key de-dupe；一旦开始关心 context budget，建议让 `RecallBarrier` 同时支持 dominance 判断，避免 ExactText 已经可见时又重复注入 Gist。

### PlayerTurnRecall

为了让 anchor 和渲染 payload 分开，Observation 中可以挂载类似类型：

```csharp
internal sealed record PlayerTurnRecall(
    RecallEntry Entry,
    string Body
);
```

`Body` 是本次实际交给角色看的文本。它可以由 Memo 的 `Title/Gist/Summary/ExactText` 组装而来，但 `RecallEntry` 本身不承载这些自然语言字段。这样 barrier 可以只处理稳定 key，renderer 可以只处理可见文本。

### RecallBarrier

`RecallBarrier` 是一份只读 set / coverage map：

```text
RecallBarrier
├── exact keys: (RecallType, SourceId)
└── optional coverage: SourceId -> highest visible recall level
```

它回答的问题是：

> 对于本次将要注入的 recall candidate，当前 provider-visible context 是否已经包含同一 anchor，或已经包含更详细的同源 recall？

它不回答：

- Memo 是否还存在；
- Memo 内容是否仍然最新；
- 哪条 Memo 应该被召回；
- 召回内容应该放多少 token；
- index 是否需要维护。

## Observation 注入建议

`PlayerTurnObservation` 可以在现有 `Notices` 之外新增 recall 集合，例如：

```csharp
internal sealed class PlayerTurnObservation {
    internal IReadOnlyList<PlayerTurnRecall> Recalls { get; }
}
```

不要把 recall 强行塞成 `PlayerTurnNotice.Reply`。`Notice` 当前更像外界 delegation result；Memo recall 是 Galatea runtime memory assembly 结果，语义上相邻但不是同一种事件。

渲染顺序建议固定为：

```text
prefix
external-local-timestamp
player-action
memo recall blocks
delegate reply / failure notices
```

原因是 recall 通常由当前 player action 和当前 context 触发，放在 player action 后更容易让模型理解“这些是 runtime 为理解本轮行动补充的记忆”。Codex reply/failure notices 仍然作为异步外界事件保留在后面。最终顺序可以调整，但必须一旦落地就由 canonical renderer/parser 固定下来。

Recall block 需要同时渲染 anchor metadata 和正文。一个候选形状：

```text
## 召回的角色笔记（一句话印象）

SourceId: memo-pod:<pod-id>#<memo-id>

~~~~memo-gist-recall
标题：...
印象：...
~~~~
```

要点：

- heading 与 info string 都由 code-owned renderer 生成；
- `RecallType` 可以由 heading/info string 决定；
- `SourceId` 使用 bounded canonical metadata line；
- body 是角色可见数据，不是 instruction；
- parser 只接受 canonical round-trip；
- display projection 可以隐藏 SourceId，也可以在调试模式显示。

如果担心自然语言 heading 随后改名影响 parser，可以把 parser 主要绑定 info string 与 metadata grammar，heading 仍按现有 `PlayerTurnObservationEnvelope` 风格保持 exact。

## Barrier 聚合器

建议命名：

```text
GalateaRecallBarrierBuilder
PlayerTurnRecallParser
RecallBarrier
```

聚合器输入应该是“本次 Completion 会看到的 Observation messages”，而不是 UI DTO。理想接口类似：

```text
Build(contextMessages)
  foreach ObservationMessage in provider-visible raw tail:
      if PlayerTurnObservationEnvelope.TryUnwrap(...):
          foreach recall in observation.Recalls:
              barrier.Add(recall.Entry)
```

注意事项：

- 只解析 canonical `PlayerTurnObservation`；非 player Observation、inbound mail、legacy 无 recall dialect 都应自然跳过。
- 不对 `FormatForDisplay` 输出做正则解析；display 文本会丢失 authority 和 anchor。
- 不把 derived context contribution 当成 raw Observation 解析。派生摘要说“某条笔记曾被召回”并不等于 anchor 当前以 recall block 形式可见。
- 聚合结果最好保留 first/last seen 的 raw address 或 provider-message ordinal，便于调试，但去重判断只依赖 key。
- 构造当前 Observation 时，应先用 pre-observation context 生成 barrier，再把本轮新注入的 recall keys 加入临时 barrier，防止同一 Observation 内重复。

当前代码可能还缺一个窄的 Galatea-side provider-visible context projection。不要为了实现 barrier 去读取 browser recent list，也不要把新的 SessionJournal public supplemental seam 仓促做出来；先找现有 `SessionContextCandidate` / raw planning window 能否提供足够只读材料。

## 端到端拼装图

后续目标可以分成两条流。

角色主动记笔记：

```text
terminal Action
  -> GalateaVisibleActionTextRenderer
  -> TextExtractor<CharacterNoteIntent>
  -> durable note capture / validation
  -> MemoPod.Append / freeze / publish
```

动态召回注入：

```text
new player action + exact completion boundary
  -> materialize provider-visible context
  -> RecallBarrierBuilder parses visible PlayerTurnObservation recalls
  -> recall planner queries MemoPod / indexes
  -> drop entries blocked by RecallBarrier
  -> render PlayerTurnObservation with PlayerTurnRecall blocks
  -> main Galatea Completion receives composite Observation
```

## 尚缺的胶水层

这份笔记不设计完整方案，但当前缺口大致是：

- `CharacterNoteIntent` contract、prompt、extractor、reconciler；
- Memo 的归类、整理、合并、分裂、失效和二级索引维护；
- recall trigger：按当前 player action、场景、实体、时间、未完成事项等决定何时查；
- recall planner：在 Gist/Summary/ExactText 之间选择合适粒度；
- recall budget：和 RecapGrid recent raw tail、derived context contributions 共用 request budget；
- provider-visible context projection：给 barrier 一个正确输入；
- durable/rebuildable ownership：哪些状态必须持久化，哪些可以由 MemoPod/index 重建；
- tests：render/parse round-trip、barrier de-dupe、dominance、legacy skip、budget failure、Undo/recovery。

## 第一批实现建议

1. 先扩展 `PlayerTurnObservation` 的强类型模型和 canonical renderer/parser，加入 `RecallType`、`RecallEntry`、`PlayerTurnRecall`，只做手写测试，不接 MemoPod。
2. 做 `RecallBarrier` 与 parser-based 聚合器，测试它能从多条 canonical Observation 中聚合 exact keys。
3. 给 `GalateaServices` 预留一个 recall 注入 seam，但先喂固定测试 recall payload，验证 render/recovery/recent display。
4. 再接 MemoPod recall planner，把 `MemoGist` 作为第一档上线；Summary / ExactText 作为后续升级路径。
5. 最后做角色主动 note writing 的 TextExtractor/reconciler，复用 Mailbox 的 durable capture 思路，但保持独立 namespace，例如 `Atelia.Galatea.Server.Memory` 或 `Atelia.Galatea.Server.CharacterMemory`。

这个顺序保持自底向上：先让 Observation 能稳定携带 recall anchor，再让 barrier 能证明“已经可见”，最后才把 MemoPod 查询、索引维护和角色主动记笔记接进来。

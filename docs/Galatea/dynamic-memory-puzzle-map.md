# Galatea Dynamic Memory Puzzle Map

本文记录 Galatea 自主笔记与动态信息召回系统当前已经清晰的底层拼图。它是恢复思路用的工作笔记；其中
`PlayerTurnObservation` recall block、`RecallEntry`、`PlayerTurnRecall`、`RecallBarrier`、
`CharacterNoteOriginBarrier`、Galatea-side provider seam，以及Character Note保存请求的durable capture、
默认MemoPod apply与诚实保存回执已经落地为V1代码级契约。召回规划、分类/metadata补全与索引维护仍是后续设计。

## 当前判断

把 Memo 召回信息注入 `PlayerTurnObservation`，并用一个 `RecallBarrier` 聚合“当前 provider-visible context 中已经可见的召回 anchor”，这个方向是合理的。

它和现有 Mailbox 模式是同一类跨 turn 通讯：

```text
role narrative intent  -> TextExtractor artifact
runtime memory effect  -> durable / rebuildable state
future Observation     -> visible recall result
```

已经扶正的边界：

- 后续代码和文档统一使用 `RecallBarrier` / `Barrier`，表达“当前上下文已可见召回 anchor 的去重屏障”。
- `{ RecallType, SourceId }` 适合作为去重 key，但它只说明“这个召回粒度对这个来源已经可见过”，不是 MemoPod 存储权威，也不是内容仍然最新的证明。
- `SourceId` 不能长期只是裸 `MemoId`，除非 Galatea 明确永远只有一个角色级 MemoPod。V0 只约束它是 bounded canonical metadata line；未来把Default MemoPod接入recall时还需要决定 Galatea-owned source id codec，例如把 pod identity 与 memo id 一起编码。
- `RecallEntry` 只表示 anchor；真正渲染进 Observation 的 payload 是 `PlayerTurnRecall`，携带 `RecallEntry` 加上本次注入的 visible text。
- Barrier 的输入是本次 Completion provider-visible raw Observation 后缀，而不是 browser recent list，也不是整条 SessionJournal 历史。V0 已在 `GalateaServices` 内通过同一轮 RecapGrid online candidate source 构造。
- V0 barrier 只做 exact-key de-dupe，不做 `MemoExactText covers MemoSummary covers MemoGist` 这种 dominance 推理。
- `CharacterNoteIntent` 仍只表达模型从角色叙事中提取出的 `ExactText` / `EvidenceQuote`；Action address、visible-text SHA-256 与 UTF-8 byte count 必须由 runtime 派生并由 CharacterMemory 持久化，不能成为模型自报字段。
- `CharacterNoteOriginBarrier` 是与 `RecallBarrier` 并列的第二道屏障：前者阻止来源 Action 仍直接可见的 Character Note Memo，后者阻止已经作为 canonical recall block 注入过的 exact recall anchor。

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

Mailbox 已经证明这个方向可行：`OutboundMailExtractor`从角色叙事Action中提取`SendMailIntent`。现行
`CharacterNoteIntent` / `CharacterNoteExtractor`已复用该模式，保守提取角色明确完成提交的长期Note保存请求；对应binding
非`null`时主prompt才追加保存Quick Start。提取结果本身不表示保存；只有Default MemoPod reconciler的durable
`AppliedNow`结果有资格生成保存回执。

### PlayerTurnObservation

源码入口：

- [`PlayerTurnObservation`](../../prototypes/Galatea/PlayerTurnObservation.cs)
- [`RecallBarrier`](../../prototypes/Galatea/RecallBarrier.cs)
- [`PlayerTurnRecallProvider`](../../prototypes/Galatea/PlayerTurnRecallProvider.cs)
- [`CharacterNoteOriginBarrier`](../../prototypes/Galatea/CharacterMemory/CharacterNoteOriginBarrier.cs)
- [`GalateaFreshInput`](../../prototypes/Galatea/GalateaFreshInput.cs)
- [`GalateaServices`](../../prototypes/Galatea/GalateaServices.cs)

`PlayerTurnObservation` 当前是普通玩家回合的 runtime-owned composite Observation。它已经支持：

- 玩家行动正文；
- runtime 采样的 external local timestamp；
- 合计0..16条`PlayerTurnNotice`：Codex delegation reply / delivery failure，以及至多1条且必须最后的`NoteSaveReceipt`；
- 0..32 条 `PlayerTurnRecall`，目前三档是 Memo Gist / Summary / ExactText；
- strict canonical render / parse / round-trip；
- adaptive Markdown fence，正文不 trim、不 normalize、不 escape。

Recall block 的 authority 仍然来自 runtime renderer，而不是外部 caller 自报。recent display 会显示 recall heading 与 body，但隐藏 `SourceId` anchor metadata；Undo / pop receipt 仍只把这条 Observation 当成普通 player turn，返回玩家行动正文。

Galatea 侧已有 internal `IGalateaPlayerTurnRecallProvider` seam。生产默认 provider 为 disabled singleton，并在 context selection / barrier 构建之前直接绕过，因此未启用 recall 时没有额外的 provider-context 或 CharacterMemory I/O。测试里可以注入固定 provider，已经覆盖 render、recovery 与 recent display。enabled provider request 同时携带 `RecallBarrier` 与 `CharacterNoteOriginBarrier`。当前只在没有 active durable reply lease 的普通 player turn 调用 provider；reply lease 场景先保持无 recall 注入，等 lease schema / restart 语义设计清楚后再接。

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

## 已落地的数据形状

### RecallType

初始枚举按三档落地：

```csharp
internal enum RecallType {
    MemoGist,
    MemoSummary,
    MemoExactText,
}
```

这三个值表达的是渐进式可见粒度。设计意图上，`Title` 是三个粒度都统一携带的定位字段；差异主要在于除标题外还暴露哪一层内容。

- `MemoGist`：目录/索引级提示，包含 `Title` 与 `Gist`。
- `MemoSummary`：较详细摘要，包含 `Title` 与 `Summary`。
- `MemoExactText`：原始 Memo 正文，包含 `Title` 与 `ExactText`。

因为 `Title/Gist/Summary` 都是可空元数据，未来 recall planner 需要区分“当前 metadata 还没补齐”和“选择这个粒度注入”。更成熟的路径是先由内容处理管线补齐缺失字段，再把对应粒度放入 `PlayerTurnObservation`；MVP 若允许缺字段通过，也应该在 provider 组装的 body 中显式表达缺失，而不是把 `MemoGist` 悄悄降级成只有正文或只有 id 的提示。

如果未来 recall source 不止 Memo，可以再拆成 `RecallSourceKind + RecallGranularity`。当前阶段把 `Memo` 前缀写进 enum value 是可以接受的，因为它清楚表达这些 anchor 来自 Memo source。

### RecallEntry

`RecallEntry` 是去重 anchor，而不是 payload：

```csharp
internal sealed record RecallEntry(
    RecallType RecallType,
    string SourceId
);
```

已落地约束：

- `RecallType` 必须是已定义枚举值；
- `SourceId` nonblank、无换行或 NUL、strict UTF-8 valid，上限 512 bytes；
- `{ RecallType, SourceId }` 是 exact de-dupe key。

尚未落地的后续约束：

- `SourceId` 使用 Galatea-owned canonical codec，不直接拼接任意外部文本；
- `SourceId` 能表达 pod identity / memo id / 其他未来 recall source 的边界。

对于同一 `SourceId`，还可以定义可选 coverage dominance：

```text
MemoExactText covers MemoSummary covers MemoGist
```

V0 只做 exact-key de-dupe；一旦开始关心 context budget，建议让 `RecallBarrier` 同时支持 dominance 判断，避免 ExactText 已经可见时又重复注入 Gist。

### PlayerTurnRecall

为了让 anchor 和渲染 payload 分开，Observation 中挂载：

```csharp
internal sealed record PlayerTurnRecall(
    RecallEntry Entry,
    string Body
);
```

`Body` 是本次实际交给角色看的文本。它可以由 Memo 的 `Title/Gist/Summary/ExactText` 组装而来，但 `RecallEntry` 本身不承载这些自然语言字段。这样 barrier 只处理稳定 key，renderer 只处理可见文本。

已落地约束：

- `Body` nonblank、strict UTF-8 valid，上限 256 KiB；
- 单条 `PlayerTurnObservation` 最多 32 条 recall；
- 同一 Observation 内禁止重复 `{ RecallType, SourceId }`。

### RecallBarrier

`RecallBarrier` V0 是一份只读 exact-key set：

```text
RecallBarrier
└── exact keys: (RecallType, SourceId)
```

它回答的问题是：

> 对于本次将要注入的 recall candidate，当前 provider-visible context 是否已经包含同一 anchor？

它不回答：

- Memo 是否还存在；
- Memo 内容是否仍然最新；
- 哪条 Memo 应该被召回；
- 召回内容应该放多少 token；
- index 是否需要维护。

coverage dominance 可以作为未来扩展，但现在不是 `RecallBarrier` 的职责。

### CharacterNoteOriginBarrier

`CharacterNoteOriginBarrier` 回答另一个问题：

> 对于一个 typed Character Note Memo candidate，它的 exact source Action 是否仍在本次 provider-visible raw context 中？

它在同一次 context materialization 中遍历带来源地址的 raw `ActionMessage` units，经
`GalateaVisibleActionTextRenderer` 得到 visible text，再用 runtime-owned helper 派生 SHA-256 与 UTF-8 byte count。
CharacterMemory 以 `{SourceAction, VisibleActionSha256, VisibleActionUtf8Bytes}` 做 exact provenance join；只有
`Applied` capture 的 `{DefaultPodId, MemoId}` 才进入这份 ephemeral barrier。

enabled provider路径的join有显式工作量边界：最多接受65,536个distinct source Action；source address按400条一批
写入connection-local TEMP request table，再用一条`capture LEFT JOIN character_note`查询读取所有命中，保持输入顺序并
复用完整capture snapshot校验。Cancellation从turn一路贯穿到Action枚举、批量装载、结果扫描和barrier冻结；不会退化为
每个可见Action各执行一组SQLite查询。

这条 join 有几项刻意的边界：

- barrier 对 provider 暴露 typed `{MemoPodId, MemoId}`，不解析尚未定稿的 `RecallEntry.SourceId` codec；
- 同一 Action 产生多条 Note 时，每条 applied Memo 都独立进入 barrier；一次命中会阻止该 Memo 的 Gist、Summary 与 ExactText 所有召回粒度；
- capture absent、`ZeroCaptured`、`Rejected` 或没有 CharacterMemory binding 时自然不命中；手工创建且没有 Galatea provenance 的 Memo 也不会被猜测性屏蔽；
- 同地址但 hash / byte count 不一致、`Captured` / `Planned` 尚未在 admission 结算、或 CharacterMemory 已 Quarantined，均属于 authority / lifecycle 不一致，在调用 recall provider 前 fail closed；
- derived context contribution、browser display、off-lineage / rewound Action 不参与；当原始 Action 已离开 provider-visible raw tail 时，未来 planner 可以重新召回对应 Memo。

它不是新的 durable owner。屏障每轮从 selected provider context 与 CharacterMemory durable provenance 重建，
MemoPod 继续只负责 corpus，`CharacterNoteIntent` 继续只负责语义提取。

## Observation 注入现状

`PlayerTurnObservation` 在现有 `Notices` 之外已有 recall 集合：

```csharp
internal sealed class PlayerTurnObservation {
    internal IReadOnlyList<PlayerTurnRecall> Recalls { get; }
}
```

没有把 recall 强行塞成 `PlayerTurnNotice.Reply`。`Notice` 当前承载异步runtime结果，包括外界delegation reply/failure
与Character Note save receipt；Memo recall则是Galatea runtime memory assembly结果，语义上相邻但不是同一种事件。

渲染顺序已固定为：

```text
prefix
external-local-timestamp
player-action
memo recall blocks
delegate reply / failure notices
optional NoteSaveReceipt (final notice)
```

原因是 recall 通常由当前 player action 和当前 context 触发，放在 player action 后更容易让模型理解“这些是 runtime 为理解本轮行动补充的记忆”。Codex reply/failure notices 仍然作为异步外界事件保留在后面。

Recall block 同时渲染 anchor metadata 和正文。当前形状：

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
- display projection 当前隐藏 `SourceId`。

parser 仍按现有 `PlayerTurnObservationEnvelope` 风格绑定 exact heading、info string、metadata grammar 与顺序。historical / legacy heading dialect 不接受 recall block；reply/failure notices 之后也不能再出现 recall。

## Barrier 聚合器

已落地命名：

```text
GalateaRecallBarrierBuilder
RecallBarrier
```

聚合器输入是“本次 Completion 会看到的 Observation messages”，而不是 UI DTO。核心接口类似：

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
- 构造当前 Observation 时，先用 pre-observation context 生成 barrier，再由 `PlayerTurnObservation` 构造函数禁止同一 Observation 内重复。
- 聚合结果当前不保留 first/last seen 的 raw address 或 provider-message ordinal；如果后续需要调试 recall 去重决策，可以加 observation address / provider-message ordinal，但去重判断仍只依赖 key。

GalateaServices 当前会在 fresh player turn 中先构造无 recall 的 preliminary Observation，用它打开 RecapGrid online pass，再用同一 candidate source materialize provider-visible raw Observation 后缀构造 `RecallBarrier`。`RawHistoryAuthorized` 只有在同一 open pass 已授权 mature raw history 时才读取 raw window；`Selected` candidate 会先确认可 materialize。这个路径刻意没有新增 SessionJournal public supplemental seam。

## 端到端拼装图

现行Character Note保存闭环：

```text
terminal Action
  -> GalateaVisibleActionTextRenderer
  -> TextExtractor<CharacterNoteIntent>
  -> validate completed request submission + exact source grounding
  -> durable capture / zero-result tombstone
  -> Default MemoPod plan + apply
  -> AppliedNow only: best-effort in-process NoteSaveReceipt queue
  -> next eligible ordinary PlayerTurnObservation
```

它证明ExactText保存到单一默认MemoPod，但暂不承诺分类、metadata补全、索引或召回。

动态召回注入：

```text
new player action + exact completion boundary
  -> materialize provider-visible context
  -> RecallBarrierBuilder parses visible PlayerTurnObservation recalls
  -> CharacterNoteOriginBarrierBuilder joins visible Actions to Applied memos
  -> recall planner queries MemoPod / indexes
  -> drop entries blocked by RecallBarrier or CharacterNoteOriginBarrier
  -> render PlayerTurnObservation with PlayerTurnRecall blocks
  -> main Galatea Completion receives composite Observation
```

## 尚缺的胶水层

这份笔记不设计完整方案，但当前缺口大致是：

- Memo 的归类、整理、合并、分裂、失效和二级索引维护；
- recall trigger：按当前 player action、场景、实体、时间、未完成事项等决定何时查；
- recall planner：在 Gist/Summary/ExactText 之间选择合适粒度；
- recall budget：和 RecapGrid recent raw tail、derived context contributions 共用 request budget；
- active durable reply lease 场景的 recall 注入策略；
- `SourceId` canonical codec 与跨 pod / 跨 source 边界；
- dominance / coverage：例如 ExactText 已可见时是否阻止 Summary 和 Gist；
- durable/rebuildable ownership：哪些状态必须持久化，哪些可以由 MemoPod/index 重建；
- tests：MemoPod recall query接入、planner 决策、dominance、budget failure、active lease、provider invalid output / cancellation。

## 第一批实现建议

### 已完成

1. 已扩展 `PlayerTurnObservation` 的强类型模型和 canonical renderer/parser，加入 `RecallType`、`RecallEntry`、`PlayerTurnRecall`，并覆盖 render/parse/display/validation/legacy rejection 测试；生产recall provider尚未接Default MemoPod query。
2. 已实现 `RecallBarrier` 与 parser-based 聚合器，能从多条 canonical Observation 中聚合 exact keys，并跳过 invalid / legacy / inbound / null 输入。
3. 已给 `GalateaServices` 预留 `IGalateaPlayerTurnRecallProvider` 注入 seam，测试中用固定 recall payload 验证了 render、recovery、recent display，也验证了第二轮能从 provider-visible context 聚合已可见 barrier。
4. 已实现capability-gated的Character Note保存Quick Start、`CharacterNoteIntent` semantic.v4提取、durable capture/zero tombstone、Default MemoPod apply，以及只由`AppliedNow`生成的honest save receipt。
5. 已实现 `CharacterNoteOriginBarrier`：复用同一 provider-visible context materialization，按 runtime-derived Action 指纹与 CharacterMemory `Applied` provenance 构造 typed Memo blockers，并与 `RecallBarrier` 一起交给 provider；这避免刚写下的 Note 在来源 Action 尚可见时被零增量重复召回。

### 下一批候选

6. 设计 MemoPod recall planner 的最小边界，把 `MemoGist` 作为第一档上线；Summary / ExactText 作为后续升级路径。这里需要先决定 `SourceId` codec、Title/Gist 缺失时的显式展示策略，以及 planner 怎样同时消费两道 barrier。
7. 在真实 recall 开始消耗 context budget 之后，再补 dominance、budget、active durable reply lease 与 provider failure/cancellation 的契约。

这个顺序保持authority前置：V1已经建立真实保存的独立durable owner；下一步从该authority出发接查询、召回与索引维护。

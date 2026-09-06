# Galatea Dynamic Memory Puzzle Map

本文记录 Galatea 自主笔记与动态信息召回系统当前已经清晰的底层拼图。它是恢复思路用的工作笔记；其中
`PlayerTurnObservation` recall block、`RecallEntry`、`PlayerTurnRecall`、`RecallBarrier`、
`CharacterNoteOriginBarrier`、Galatea-side provider seam，以及Character Note保存请求的durable capture、
默认MemoPod apply与诚实保存回执已经落地为V1代码级契约。MemoPod DerivedInfo更新与全文-only recall
projection、Character Note DerivedInfo批量生成、durable apply与非阻塞runtime pump也已落地；Default MemoPod
`MemoExactText` recall MVP现已接通，分类、多Pod与二级索引维护仍是后续设计。
自动记忆闭环已进一步扩展为三种typed trigger共享召回、SQLite V3 durable receipt outbox；现行边界见
[Automatic memory工作单](automatic-memory-work-order.md)。旧V1阶段的in-process receipt与player-only限制已被替代。

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
- `SourceId` 不能只是裸 `MemoId`。MVP已锁定Galatea-owned canonical codec：`memo-pod:v1/<MemoPodId>/<MemoId>`，同时携带pod identity与memo id。
- `RecallEntry` 只表示 anchor；真正渲染进 Observation 的 payload 是 `PlayerTurnRecall`，携带 `RecallEntry` 加上本次注入的 visible text。
- Barrier 的输入是本次 Completion provider-visible raw Observation 后缀，而不是 browser recent list，也不是整条 SessionJournal 历史。V0 已在 `GalateaServices` 内通过同一轮 RecapGrid online candidate source 构造。
- V0 barrier 只做 exact-key de-dupe，不做 `MemoExactText covers MemoSummary covers MemoGist` 这种 dominance 推理。
- `CharacterNoteIntent` 仍只表达模型从角色叙事中提取出的 `ExactText` / `EvidenceQuote`；Action address、visible-text SHA-256 与 UTF-8 byte count 必须由 runtime 派生并由 CharacterMemory 持久化，不能成为模型自报字段。
- `CharacterNoteOriginBarrier` 是与 `RecallBarrier` 并列的第二道屏障：前者阻止来源 Action 仍直接可见的 Character Note Memo，后者阻止已经作为 canonical recall block 注入过的 exact recall anchor。
- 首个真实 recall MVP 应优先支持 `MemoExactText`，但 `Title` 是所有 Memo recall 的硬 eligibility 条件：正文已入库而 Title 尚未补齐的 Memo 暂不召回，不用占位标题冒充完成。实施顺序仍是先 ExactText、后 Gist/Summary，不改变三档渐进式可见粒度的长期设计。

## 已有拼图

### MemoPod

源码入口：

- [`Memo`](../../prototypes/MemoPod/Memo.cs)
- [`MemoPod`](../../prototypes/MemoPod/MemoPod.cs)
- [`MemoPodRecall`](../../prototypes/MemoPod/Recall/MemoPodRecall.cs)
- [`MemoPod README`](../../prototypes/MemoPod/README.md)

MemoPod 当前解决的是“一堆 Memo 中如何按语义 query 找回候选 Memo”。`Memo` 已有 stable `Id`、必需且创建后不可修改的 `ExactText`，以及可空的 `Title`、`Gist`、`Summary` DerivedInfo。

Memo 创建时必须有正文，因为正文是后续摘要、印象、标题与索引的事实来源；`Title`、`Gist`、`Summary` 在创建正文时都可以暂时缺失。`MemoPod.UpdateDerivedInfo`允许在保持`MemoId`与`ExactText`不变的前提下原子替换或清除这三项可重建信息；它们继续进入durable document与完整state identity，但不进入`MemoPodFrozenPrompt` / `ObservationMessage`。Prompt v3的Memo entry只包含`id + exact_text`，因此当前MemoPod recall selector不依赖Title、Gist或Summary。

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
非`null`时主prompt才追加保存Quick Start。提取结果本身不表示保存；只有Default MemoPod真正durable Applied
settlement才在同一SQLite事务建立保存回执，不依赖后续AppliedNow函数返回值。

### Character Note DerivedInfo Enricher

源码入口：

- [`CharacterNoteDerivedInfoEnricher`](../../prototypes/Galatea/CharacterMemory/CharacterNoteDerivedInfoEnricher.cs)
- [`CharacterMemorySqliteStore.DerivedInfo`](../../prototypes/Galatea/CharacterMemory/CharacterMemorySqliteStore.DerivedInfo.cs)
- [`CharacterNoteDefaultPodReconciler.DerivedInfo`](../../prototypes/Galatea/CharacterMemory/CharacterNoteDefaultPodReconciler.DerivedInfo.cs)
- [`CharacterNoteDerivedInfoPump`](../../prototypes/Galatea/CharacterMemory/CharacterNoteDerivedInfoPump.cs)

纯生成契约已经落地：一次调用只消费产生该批Note的raw `ObservationContent`、visible Action，以及ordered
`{ArtifactOrdinal, ExactText}` targets；模型必须用单个batch artifact为每条目标返回非空的`Title/Gist/Summary`。
runtime严格验证数量、ordinal exact-once覆盖、输入顺序、trim/control character、strict UTF-8与MemoPod字段上限，
任一项无效则整批失败。`Gist`的一句话要求与`Summary`的主旨摘要要求当前属于prompt semantic，不做脆弱的标点启发式判断。

这条生成契约现在已经接入自动内容增强管线。CharacterMemory SQLite V3延续V2，在ExactText `Applied`结算的同一事务中
创建`Pending` work；正常写回路径为`Pending -> Prepared -> Planned -> Applied`，确定性容量不足只允许从
`Prepared -> Rejected`分支退出。状态保存完整生成结果和Pod base/target identity。`Prepared`之后不再调用模型；
只有`Planned`占用Pod mutation slot，并在restart/admission中按
observed base/target恢复，neither则quarantine。

每个writable session拥有一个capacity-1 wakeup channel与单调external signal generation驱动的background pump。
每个外部signal至多换取一次work attempt；忙碌期间channel可以合并wakeup，但generation不会丢掉推进额度，也不会在
没有新signal时自主热重试。pump在`TurnLock`内只按source Action地址从
SessionJournal重建immutable raw Observation/visible Action并核验指纹，释放锁后才调用provider；ExactText保存、
`AppliedNow`、回执和主回合完成都不等待增强provider。provider失败或timeout保留`Pending`，后续startup或安全turn
boundary signal再做一次bounded尝试；当前不持久化attempt counter或retry schedule。MVP复用
`galatea.character-note-extractor`的connection/client routing，但enricher实例、prompt与`ContractId`保持独立。
30秒deadline通过cancellation请求实现；若底层provider完全忽略cancellation，session shutdown会继续等待该调用返回，
以免提前释放它仍在使用的SessionJournal、CharacterMemory或Completion资源。

### PlayerTurnObservation

源码入口：

- [`PlayerTurnObservation`](../../prototypes/Galatea/PlayerTurnObservation.cs)
- [`RecallBarrier`](../../prototypes/Galatea/RecallBarrier.cs)
- [`PlayerTurnRecallProvider`](../../prototypes/Galatea/PlayerTurnRecallProvider.cs)
- [`CharacterNoteOriginBarrier`](../../prototypes/Galatea/CharacterMemory/CharacterNoteOriginBarrier.cs)
- [`GalateaFreshInput`](../../prototypes/Galatea/GalateaFreshInput.cs)
- [`GalateaServices`](../../prototypes/Galatea/GalateaServices.cs)

`PlayerTurnObservation` 当前是PlayerAction、HeartbeatActivation与DelegateReply共享的runtime-owned composite Observation。它已经支持：

- 真实typed trigger：仅PlayerAction带玩家正文；Heartbeat带code-owned活动时机，DelegateReply以真实external notices触发；
- runtime 采样的 external local timestamp；
- 合计0..16条`PlayerTurnNotice`：Codex delegation reply / delivery failure，以及至多1条且必须最后的`NoteSaveReceipt`；
- 0..32 条 `PlayerTurnRecall`，目前三档是 Memo Gist / Summary / ExactText；
- strict canonical render / parse / round-trip；
- adaptive Markdown fence，正文不 trim、不 normalize、不 escape。

Recall block 的 authority 仍然来自 runtime renderer，而不是外部 caller 自报。recent display 会显示 recall heading 与 body，但隐藏 `SourceId` anchor metadata；只有PlayerAction支持返回玩家行动正文的Undo / pop与draft restoration，自动trigger不伪造玩家草稿。

Galatea侧的internal `IGalateaPlayerTurnRecallProvider`已由per-session factory接入production；历史名称不限制trigger。
`galatea.memo-recall`为null或maintenance mode时使用disabled singleton，并在context selection / barrier构建之前绕过；
仅禁用Memo binding不关闭独立receipt delivery；maintenance不打开CharacterMemory，也不进行投递写入。
enabled provider request携带preliminary typed Observation、recent visible Action、
`RecallBarrier`与`CharacterNoteOriginBarrier`，服务三个fresh trigger，包括携带reply lease的PlayerAction。
query使用`atelia.galatea.memo-recall-context.v2`，`currentTurn.trigger`保留真实kind及对应playerText/activationText，
DelegateReply只写kind并由externalNotices提供回信；receipt不进入query。runtime先附receipt，再按剩余预算选择0..1个Memo；
最终Observation在selector结束后才绑定到reply lease/outbox。inbound与recovery不做fresh recall，recovery复用durable bytes。

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

这三个值表达的是渐进式可见粒度。设计意图上，`Title` 是三个粒度都统一携带的定位字段；差异主要在于除标题外还暴露哪一层内容。粒度从粗到细的排列不等于功能应按同一顺序上线。

- `MemoGist`：目录/索引级提示，包含 `Title` 与 `Gist`。
- `MemoSummary`：较详细摘要，包含 `Title` 与 `Summary`。
- `MemoExactText`：原始 Memo 正文，包含 `Title` 与 `ExactText`。

因为 `Title/Gist/Summary` 都是可空DerivedInfo，未来 recall planner 需要区分“当前DerivedInfo还没补齐”和“选择这个粒度注入”。`Title`缺失时整个Memo不具备recall资格；`MemoGist`与`MemoSummary`还分别要求对应字段可用，不能把某一档悄悄降级成另一档。

首个 recall MVP 选择 `MemoExactText` 更合适：`ExactText` 是 Memo 的必需事实正文，因此不依赖Gist/Summary质量或更广的摘要上下文。它仍需等待最小内容增强管线补齐Title；这项前置只决定Memo何时具备recall资格，不把摘要字段引入MemoPod selector corpus。具体body形状留给recall MVP的种子设计决定。

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

- `Body` nonblank、strict UTF-8 valid，上限262,677 bytes，恰好覆盖最大合法Title、ExactText与固定标签；
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
PlayerAction: player-action / HeartbeatActivation: heartbeat-activation / DelegateReply: no synthetic trigger block
memo recall blocks
delegate reply / failure notices (forbidden in HeartbeatActivation; required in DelegateReply)
optional NoteSaveReceipt (final notice)
```

recall由真实typed trigger与当前context驱动，作为runtime为本轮行动补充的记忆，不伪造玩家输入。
PlayerAction/HeartbeatActivation先写各自trigger块，再写recall；DelegateReply可直接从recall开始，但后面必须有
真实Codex reply/failure notice。所有variant都把receipt放在最后，并保持相同canonical顺序与校验。

Recall block 同时渲染 anchor metadata 和正文。当前形状：

```text
## 召回的角色笔记（一句话印象）

SourceId: memo-pod:v1/00000000000000000000000000000001/m1:00000001

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

- 只解析三种trigger的canonical `PlayerTurnObservation`；其他Observation、inbound mail与legacy无recall dialect自然跳过。
- 不对 `FormatForDisplay` 输出做正则解析；display 文本会丢失 authority 和 anchor。
- 不把 derived context contribution 当成 raw Observation 解析。派生摘要说“某条笔记曾被召回”并不等于 anchor 当前以 recall block 形式可见。
- 构造当前 Observation 时，先用 pre-observation context 生成 barrier，再由 `PlayerTurnObservation` 构造函数禁止同一 Observation 内重复。
- 聚合结果当前不保留 first/last seen 的 raw address 或 provider-message ordinal；如果后续需要调试 recall 去重决策，可以加 observation address / provider-message ordinal，但去重判断仍只依赖 key。

GalateaServices在三种fresh trigger中先构造无recall、可含pending receipt的preliminary Observation，用它打开
RecapGrid online pass，再用同一candidate source materialize provider-visible raw Observation后缀构造`RecallBarrier`。
`RawHistoryAuthorized`只有在同一open pass已授权mature raw history时才读取raw window；`Selected` candidate会先确认
可materialize。这个路径刻意没有新增SessionJournal public supplemental seam。

## 端到端拼装图

现行Character Note保存闭环：

```text
terminal Action
  -> GalateaVisibleActionTextRenderer
  -> TextExtractor<CharacterNoteIntent>
  -> validate completed request submission + exact source grounding
  -> durable capture / zero-result tombstone
  -> Default MemoPod plan + apply
  -> Planned -> Applied atomically creates SQLite V3 Pending receipt
  -> next eligible PlayerAction / HeartbeatActivation / DelegateReply
  -> bind exact base + complete canonical Observation
  -> exact raw proof of append -> Delivered
```

它证明ExactText保存到单一默认MemoPod；保存回执仍只证明这件事，不承诺异步DerivedInfo已经补全，也不承诺分类、
索引或召回。
outbox以durable保存事实为资格，不要求source Action仍在selected lineage；AlreadyApplied不新建义务，旧版Applied
迁移也不补发。NotAppended proof退回Pending，pre-dispatch failure或restart可重试；Delivered只证明durable append，
不证明provider已读。abandon/rewind前先结算bound receipt，Delivered即使被rewind也不重发。reply cutoff为pending
receipt预留一个notice槽位与实际预算；极端fence-heavy原文使用明确标记的Source Action/Memo IDs确认，不能静默丢通知。

现行入库后DerivedInfo闭环：

```text
Applied Character Note batch + exact source completed turn
  -> durable Pending work + session pump signal generation
  -> rebuild exact source Observation/Action under TurnLock
  -> CharacterNoteDerivedInfoEnricher prepares one complete batch outside TurnLock
  -> persist exact generated result before touching MemoPod
  -> plan UpdateDerivedInfo against base/target Pod identities
  -> Freeze + durability confirmation
  -> settle enriched Pod tip
  -> Title-present Memo becomes recall-eligible
```

这条闭环已经接入生产。它是可恢复的post-store reconciler，不是绕过CharacterMemory settled Pod identity的普通
callback；provider失败保留已保存的ExactText和`Pending` work，不撤销保存回执，也不阻断后续turn。V1 store在持有
lifetime lock后执行strict validation，再事务化经过V2迁移到当前V3；历史Applied capture会得到DerivedInfo Pending
work，但不补建receipt outbox。ignored live store不会由测试或提交过程主动打开。

动态召回注入：

```text
fresh PlayerAction / HeartbeatActivation / DelegateReply + exact completion boundary
  -> materialize provider-visible context
  -> RecallBarrierBuilder parses visible PlayerTurnObservation recalls
  -> CharacterNoteOriginBarrierBuilder joins visible Actions to Applied memos
  -> canonical query renderer calls settled Default MemoPod selector
  -> drop entries blocked by RecallBarrier or CharacterNoteOriginBarrier
  -> render PlayerTurnObservation with PlayerTurnRecall blocks
  -> main Galatea Completion receives composite Observation
```

## 尚缺的胶水层

MVP之后仍真实存在的缺口大致是：

- Memo 的归类、整理、合并、分裂、失效和二级索引维护；
- DerivedInfo retry的持久化schedule、attempt telemetry与长期backoff策略；当前只有session内round-robin cursor与外部安全边界signal；
- 对完全不遵守cancellation的provider建立更强的隔离/终止边界；当前shutdown选择等待而不是冒险use-after-dispose；
- recall trigger：现行每个eligible PlayerAction、HeartbeatActivation或DelegateReply fresh turn查询一次；是否按场景、实体、时间或未完成事项跳过，需要真实telemetry后再设计；
- recall planner：现行只产出Title+ExactText；后续再根据DerivedInfo质量与预算在Gist/Summary/ExactText之间选择合适粒度；
- recall budget：和 RecapGrid recent raw tail、derived context contributions 共用 request budget；
- 多Pod启用后的SourceId跨pod routing与失效边界；
- dominance / coverage：例如 ExactText 已可见时是否阻止 Summary 和 Gist；
- durable/rebuildable ownership：哪些状态必须持久化，哪些可以由 MemoPod/index 重建；
- tests/eval：真实provider precision/recall、cache usage、dominance、active lease与多Pod routing。

## 第一批实现建议

### 已完成

1. 已扩展 `PlayerTurnObservation` 的强类型模型和 canonical renderer/parser，加入 `RecallType`、`RecallEntry`、`PlayerTurnRecall`，并覆盖 render/parse/display/validation/legacy rejection 测试。
2. 已实现 `RecallBarrier` 与 parser-based 聚合器，能从多条 canonical Observation 中聚合 exact keys，并跳过 invalid / legacy / inbound / null 输入。
3. 已把`IGalateaPlayerTurnRecallProvider`收口为host-wide factory创建per-session provider；fixed tests继续验证render、recovery、recent display与第二轮barrier，production binding vertical验证真实MemoPod no-match与configured failure顺序。
4. 已实现capability-gated的Character Note保存Quick Start、`CharacterNoteIntent` semantic.v4提取、durable capture/zero tombstone、Default MemoPod apply与honest save receipt；初版依赖AppliedNow返回值，现已由第10项的原子outbox取代。
5. 已实现 `CharacterNoteOriginBarrier`：复用同一 provider-visible context materialization，按 runtime-derived Action 指纹与 CharacterMemory `Applied` provenance 构造 typed Memo blockers，并与 `RecallBarrier` 一起交给 provider；这避免刚写下的 Note 在来源 Action 尚可见时被零增量重复召回。
6. 已实现`MemoPod.UpdateDerivedInfo`与prompt v3：DerivedInfo可按稳定MemoId重建更新并继续进入durable state identity，但不进入FrozenPrompt；现有MemoPod selector只消费id与ExactText。
7. 已实现`CharacterNoteDerivedInfoEnricher`契约：按同一source turn成批生成Title/Gist/Summary，并严格验证单batch、exact ordinal映射和字段边界。
8. 已实现CharacterMemory SQLite V2 DerivedInfo queue、strict V1->V2 migration、`UpdateDerivedInfo` plan/apply/settle与crash recovery；session-owned background pump在保存与回执之后非阻塞触发，provider失败保留Pending，Prepared不重调模型，Planned在所有新turn/capture admission前恢复。
9. 已实现Default MemoPod recall MVP：canonical runtime query、512 KiB whole-item budget、settled Frozen epoch、Title eligibility、canonical SourceId、两道barrier、0..1 `MemoExactText`、独立optional connection binding与production no-match/failure/selected vertical。provider await不持有Pod mutation gate；disabled与maintenance路径保持零selector。完整实施记录见[`Galatea Default MemoPod Recall MVP 实施工单`](./work/completed/memo-recall-mvp-work-order.md)。
10. 已将三trigger共享recall/query V2与SQLite V3 receipt outbox接通；Pending跨restart保留，exact raw append结算Delivered，不受后续rewind重发。Delivered清除整份Observation副本，仅保留receipt与source/base/Observation地址证据。

### 下一批候选

11. 在生产使用中观察最小DerivedInfo管线的摘要质量与失败分布，再决定是否增加持久化retry schedule、attempt telemetry、独立connection binding或更广的生成上下文。
12. 收集Memo recall的empty rate、Title-missing/filter rate、query/cache tokens与latency，先用证据判断是否需要typed exclusions、更宽recent Action suffix或cue extractor。
13. 内容增强质量达到可用门槛后，再启用`MemoGist`与`MemoSummary`，并迭代摘要质量和生成上下文。
14. 真实使用后再设计跨RecapGrid与main request的全局budget分配、dominance和可选best-effort policy。

这个顺序保持authority前置：真实保存、最小DerivedInfo与三trigger的Title+事实正文召回已通过同一Pod authority接通；
下一步先收集持续运行证据，不把尚未成熟的分类、二级索引或Gist/Summary策略提前塞进现有MVP。

# Character Note Request Receipt V0

## 状态

- 方案日期：2026-08-30
- 当前阶段：A0 shared target、A1 extractor contract、A2 config/composition与A4a receipt protocol/queue已完成；A3/A4b runtime接线尚未开始
- 目标版本：V0 development vertical slice
- 对外承诺：只确认 runtime 识别到角色的 Note 请求，不承诺 Memo 已保存

## 一句话目标

复用 Mailbox 已验证的 `Action -> TextExtractor artifact` 模式，从角色完成的叙事 Action 中提取 `CharacterNoteIntent`；在不接入 MemoPod、索引或召回管理的前提下，把一条 code-owned 请求回执放进下一次普通 player-turn `Observation`，先跑通：

```text
角色叙事请求
  -> runtime 提取
  -> runtime 回执
  -> 角色在未来 Observation 中看到回执
```

这是一条 development-only 的反馈闭环，不是临时伪装成完整记忆系统的兼容层。

## 为什么现在值得做

Mailbox 已证明 Galatea 可以让主线角色继续书写叙事 Action，再由 turn 边界外的独立模型提取 typed artifact。Note 请求和外发邮件具有相同的上游形状，但下游存储、召回和生命周期尚未收口。

当前最有价值的最小切口是验证三件事：

1. 保守 prompt 能否稳定区分“角色完成记录 Note”与普通想法、计划、复述；
2. Mail 与 Note 两个提取器能否消费同一份 terminal Action snapshot 并真正并行；
3. runtime-owned 反馈能否经过 canonical `PlayerTurnObservation` 在下一轮对角色可见。

MemoPod routing、内容整理和 recall planner 不影响这三个判断。现在实现它们会显著扩大状态空间，却不能提高 V0 的验证价值。

## 诚实语义

### 回执证明什么

回执只证明：

- runtime 对某条 exact terminal Action 运行了当前版本的 Note extractor；
- extractor 返回了至少一个通过 runtime 校验的 `CharacterNoteIntent`；
- runtime 将该批 intent 组织成一条待反馈回执。

回执不证明：

- 已创建 `Memo`；
- 已选择 MemoPod；
- 已持久化、索引、整理或发布；
- 未来可以召回；
- crash、restart、Undo 或 rewind 后会重新投递。

推荐的可见措辞是：

```text
## Note 请求回执

Galatea runtime 已识别到 1 条 Note 请求。

当前仅完成请求提取与回传，Memo 持久化尚未实现；
本回执不表示这些 Note 已经保存。

识别到的 Note 原文如下：
<由 canonical renderer 使用 adaptive fence 包裹原文>
```

同一 Action 有多条 Note 时仍只产生一条回执，正文按 extractor 返回的叙事顺序列出 `1..N`。

### “未接 Memo 存储”不等于“没有任何 durable bytes”

回执一旦进入下一次 `PlayerTurnObservation`，就会作为该 Observation 的一部分被 SessionJournal 持久记录。V0 接受这份叙事历史副本，但不把它当作 Memo corpus：

- 没有 `MemoId` / `PodId`；
- 不进入 MemoPod；
- 不建立索引或召回入口；
- 不把 SessionJournal 回执反向迁移成未来 Memo；
- 不以日志或回执作为未来 durable apply 的权威输入。

如果未来要求 Note 内容完全不落盘，只能把反馈改成瞬时 UI/SSE development event；那将不再验证 runtime 到角色的 Observation bridge。

## V0 产品形状

### 角色到 runtime：CharacterNoteIntent

新 domain 位于 `Atelia.Galatea.Server.CharacterMemory`，不放进 Mailbox namespace。

候选合同：

```csharp
internal sealed record CharacterNoteIntent(
    string ExactText,
    string EvidenceQuote
);
```

字段语义：

- `ExactText`：角色明确完成记录的 Note 正文；不得总结、润色或补写。
- `EvidenceQuote`：Action visible text 中能证明“谁完成了什么记录动作”的原文片段。

runtime 另行绑定 `SourceAction`、visible text SHA-256、UTF-8 byte count 和 extractor `ContractId`；这些不是模型自报字段。

初始 bounds：

- 每条 `ExactText` 最大 64 KiB；
- `EvidenceQuote` 最大 8 KiB；
- 每个 Action 最多 16 条 Note；
- 单批 `ExactText` 总计最大 256 KiB。

`ExactText` 与 `EvidenceQuote` 必须能以 ordinal substring 在本次 visible Action text 中找到。该约束有意比 Mailbox 更严格，用于避免 V0 在没有 durable apply/review 阶段时悄悄改写角色原文。

### 保守提取定义

只有同时满足以下条件才输出 artifact：

- `${characterName}` 本人完成了记录动作；
- 目标被明确表述为她自己的长期 Note / Memo / autonomous memory；
- 完整正文出现在当前 Action；
- `[characterName]` 直接建立该动作，或 `[旁白]` 客观建立她已完成该动作。

以下情况输出零 artifact：

- 普通想法、发现、结论或对话；
- “应该记住”“以后要记”“我想记”之类计划；
- 草稿、编辑中、打开界面、准备保存；
- 普通日记、便签、墙面题字等世界内书写，除非明确声明为长期 Note；
- 邮件；
- 玩家请求、其他角色的动作、状态摘要；
- 阅读、引用或回忆既有 Memo；
- “记住上面的内容”但本 Action 没有完整 Note 正文。

自然捕获率偏低是 V0 可接受结果。此阶段的目标是测量可靠性，不是通过放宽定义制造漂亮命中率。

## Terminal Action 单一 snapshot

Mail 和 Note 不应分别读取 SessionJournal、分别选择 latest turn、分别 render visible text。V0 引入 domain-neutral immutable target：

```csharp
internal sealed record GalateaTerminalActionExtractionTarget {
    internal GalateaTerminalActionExtractionTarget(
        EventAddress sourceAction,
        string visibleText
    );

    internal EventAddress SourceAction { get; }
    internal string VisibleText { get; }
    internal string VisibleTextSha256 { get; }
    internal int VisibleTextUtf8Bytes { get; }
}
```

`GalateaTerminalActionExtractionTargetReader.ReadAt` 在调用方选定的 exact head
上只读取 latest completed turn 一次，不自行读取 current head，并要求 terminal
Action address 等于 selected head；`GalateaVisibleActionTextRenderer` 也只运行一次。
target constructor 由 `sourceAction + visibleText` 计算并冻结 hash/byte count，调用方
不能传入不匹配的 identity metadata。Mail durable reconciler 保留自己的 baseline/capture
判定；新的 `ReconcileTargetAsync` 不重新投影 history，但仍保留 capture 前的 final
head fence。

Admission/restart 仍只执行 Mail 的 durable gap reconciliation。V0 Note 不建立 durable capture/tombstone，因此不能在 admission 时补偿性重跑。

## 并行执行与完成边界

主路径位于 `RunTurnAsync` 中 main Completion 成功以后：

```text
fresh/recovery main Completion 完成
  -> durable execution boundary 必须为 Idle
  -> settle durable reply lease
  -> 读取并 render 一份 terminal Action target
  -> 同时启动 Mail reconcile(target) 与 Note extract(target)
  -> 先等待并确认 durable Mail reconcile 成功
  -> 等待有独立 deadline 的 best-effort Note outcome
  -> 再次确认 current head == SourceAction
  -> 把 Note 回执放入进程内 pending queue
  -> refresh recent / PublishDone
```

约束：

- 直接启动两个 async I/O operation，不使用 `Task.Run`；
- 不 fire-and-forget；`TurnLock` 释放和 borrowed client lifetime 之前必须观察两个 task；
- Mail 失败保持现有 turn failure 语义，并取消、观察 Note task；
- Note 的 timeout、provider failure、invalid output 不得让已完成的主回合失败；
- caller/shutdown cancellation 原样传播；
- Note timeout 只取消 Note，不得取消 Mail；
- Note 成功结果先留在内存，只有 Mail 成功且最终 head fence 仍成立时才能排入回执；
- `0` artifact 是成功但不产生回执。

理论延迟从串行的 `mailMs + noteMs` 收敛为约 `max(mailMs, noteMs)`；但 Note 仍可能把 `PublishDone` 最多推迟到自身 deadline。开发日志应记录 `mailMs`、`noteMs` 和 batch wall time，而不是假定并行自然有效。

`ICompletionClient` 已明确同一实例可以接收重叠的 `StreamCompletionAsync`
调用：每次 invocation 的 request/observer/parser/result/cancellation 必须隔离，但
client 可以内部限流或串行化，不承诺 provider 请求必然并行。现有
provider/wrapper audit 未发现需要 runtime 修改的 shared mutable invocation state；
`TextExtractor` collector/session、Completion call-log reservation 与 Codex client admission/reload 都已有
并发测试保护。lifetime owner 仍必须在 active calls drain 之后才 dispose client。

## 回执排队与注入

V0 使用每个 `UserSessionHost` 私有的进程内 pending queue，不增加 SQLite schema，不复用 delegation store。queue 是 caller-serialized 的 bounded FIFO；`TurnLock` 保证领取路径不会并发。

一条 queue item 只包含：

- frozen `PlayerTurnNotice.NoteRequestReceipt`；
- receipt body 的 UTF-8 byte count。

queue 不同时保存 intents、contract或另一份可重新render的 DTO，避免形成双重 payload authority。固定上限为 16 项、合计 4 MiB；满时 drop newest并返回false，由caller记录development diagnostic，不改变已经完成的主回合。

单条 receipt body 上限为512 KiB。已锁定的 Note batch 最多包含256 KiB `ExactText`；另一半预算留给code-owned措辞、序号和inner adaptive fences。receipt factory还会用实际frozen notice验证“与最大合法player text组合仍可落入1 MiB Observation”；极端长tilde run导致fence膨胀时不创建receipt，而不是放宽outer hard bound。

投递规则：

1. 只在下一次普通 player turn 注入；不自动启动额外主模型 Completion。
2. 只在没有 active durable reply lease 的 player turn 注入；有 mail reply lease 时继续保留到后续普通回合。
3. 每轮最多注入一条 Note receipt；多条 pending item 保持 FIFO。
4. `BeginCutoff == Empty` 的普通 `StartTurn` 以 `TryDequeue` 领取queue head，并冻结进本次 `PlayerAction.Notices`；这就是at-most-once delivery attempt。
5. pre-dispatch stop、failed/aborted turn或后续recovery不重新排队；V0只承诺注入下一次eligible turn attempt，不承诺注入下一次成功turn。
6. 进程退出会丢失尚未领取的pending item；这是 V0 明示限制。
7. recovery、Undo和rewind都不重新武装已经领取的回执。

回执使用新的 `PlayerTurnNotice.NoteRequestReceipt` strong type 和独立 canonical heading/info string。它不伪装成：

- `PlayerTurnNotice.Reply`；
- `PlayerTurnNotice.DeliveryFailure`；
- `PlayerTurnRecall`；
- inbound mail；
- `MemoSaved` 或 storage receipt。

renderer 使用 adaptive fence，parser 只接受 canonical round-trip。recent display 显示自然回执正文；内部 source identity 不暴露给 browser 或模型。

current dialect 的canonical顺序固定为：

```text
player action
  -> 0..N recalls
  -> 0..N Reply / DeliveryFailure
  -> 0..1 NoteRequestReceipt（必须最后）
```

legacy dialect拒绝`NoteRequestReceipt`；receipt heading为`Note 请求回执`，info string为`character-note-request-receipt`。回执内每条`ExactText`按原顺序放进`character-note-exact-text` inner adaptive fence；`EvidenceQuote`、source address与extractor contract不进入可见body。0 intent不创建receipt。

## 配置与 capability

新增 exact binding：

```json
"galatea.character-note-extractor": null
```

- `bindings` 必须 exact 包含该 key；
- `null` 明确禁用；
- 非 `null` 必须 exact lookup，不回落 default connection；
- client 保持 lazy/borrowed；
- 可以与 outbound mail 指向同一 connection，前提是并发合同已验证。

A2 production composition在`null`时为每个user提供同一个
`DisabledCharacterNoteExtractor` singleton；非`null`时按exact `CharacterName`构造per-user extractor，
但只在首次真正提取时才从host-owned registry取得client。该binding不进入`selectableConnectionIds`、
extractor `ContractId`或主system prompt。

V0 不修改主系统提示词来宣称“角色拥有可保存、可召回的自主记忆”。只有真实 durable memory sink 落地后，才 capability-gated 地增加这项产品承诺。

## Development diagnostics

使用 `DebugUtil.Info("Galatea.CharacterMemory", ...)` 输出 bounded、single-line JSON：

- `developmentOnly: true`；
- `durableMemo: false`；
- `userId`；
- `sourceAction`；
- visible Action hash / byte count；
- extractor `ContractId`；
- outcome / artifact count / latency；
- 每条 artifact 的 `exactText`、`evidenceQuote`。

不得打印完整 source Action。日志不是 replay、migration 或未来 Memo apply 的输入。

注意 `DebugUtil.Info` 在 Debug build 默认可能写入 `.atelia/debug-logs/galatea.charactermemory.log`；`CallLogDir` 开启时 provider request/tool arguments 还可能被独立保存。测试与使用说明必须明确这项隐私边界。

## 明确非目标：防止伪需求扩张

V0 不实现：

- `Memo` / `MemoPod.Append` / Pod routing；
- Note SQLite capture、0-result durable tombstone、apply receipt；
- restart/admission compensation；
- Undo/rewind re-arm；
- receipt reservation、failure requeue或recovery transfer；
- 分类、重要性、过期时间、标签、合并、分裂、冻结；
- Summary / Gist 自动生成；
- index maintenance；
- recall planner、recall budget 或 `RecallBarrier` 集成；
- 独立 Note receipt 自动回合；
- frontend 专用 Note API / UI；
- 用一个 multi-tool extractor 同时输出 Mail 和 Note；
- 把日志或 SessionJournal 回执当作未来 Memo migration source。

出现上述需求时，先证明它是当前闭环不可缺少的约束；否则记录为后续候选，不在 V0 顺手实现。

## Failure matrix

| Mail | Note | head fence | 结果 |
|---|---|---|---|
| success | success, 1..N | current | Mail durable capture；排入一条 Note 回执 |
| success | success, 0 | current | Mail durable capture；无回执 |
| success | timeout / invalid / provider failure | current | 主回合成功；仅 development diagnostic |
| failure | 任意 | 任意 | 保持 Mail 当前失败语义；不排 Note 回执 |
| success | success | changed | 不排 Note 回执；沿现有 state-changed fence 处理 |
| caller/shutdown cancellation | 任意 | 任意 | 观察两个 task 后传播 cancellation |

## 工作包与实施状态

| 包 | 内容 | 状态 | 完成证据 |
|---|---|---|---|
| D0 | 建立本方案、目标/非目标、failure matrix | Complete | 本文档 |
| A0 | `ICompletionClient` 并发合同 audit；terminal Action 单一 target；Mail target overload | Complete | `GalateaOutboundMailExtractionReconcilerTests` 19/19 |
| A1 | `CharacterNoteIntent`、prompt、extractor、ContractId、bounds/source-grounding | Complete | `CharacterNoteExtractorTests` 10/10 |
| A2 | exact config binding、lazy per-user composition | Complete | focused config/composition tests 42/42 |
| A3 | post-completion 并行 coordinator、timeout/failure matrix、diagnostics | Pending | lifecycle/concurrency tests |
| A4a | code-owned receipt、bounded FIFO、`PlayerTurnObservation` canonical grammar | Complete | focused receipt/Observation tests 16/16；Galatea build 0 warnings/errors |
| A4b | `UserSessionHost` queue ownership与普通`StartTurn` at-most-once注入 | Pending | runtime injection tests |
| R0 | 独立 code review、尾修、完整串行验证、状态回写 | Pending | review findings + final commands |

## 验收标准

- 普通思考、计划、草稿、邮件和既有 Memo 引用均提取为零；完成的单条/多条 Note 保留原文和顺序。
- 同一 terminal Action 只读取/render 一次，Mail 和 Note 收到相同 address/text/hash。
- blocking fake clients 能证明两个 extractor 的最大并发数达到 2；同一 client instance 的并发 contract 有测试保护。
- Mail success + Note failure 不影响主回合；Mail failure + Note success 不产生回执；task 不泄漏。
- fresh 和 recovery 新完成的 terminal Action 都运行 Note；admission/restart reconciliation 不运行 Note。
- Note receipt 只在后续无 reply lease 的普通 player turn attempt出现；领取后不因失败、recovery或rewind重新排队。
- receipt render/parse/re-render byte exact，正文包含任意 Markdown fence、换行和控制字符时仍 bounded、无注入歧义。
- `null` binding 不创建 Note client；unknown/wrong-case/extra config fail closed。
- 主系统提示词 byte-for-byte 不因 V0 改变。
- focused Galatea tests、Galatea build、doc governance 和 `git diff --check` 通过。

## 后续 durable 阶段的入口

只有 V0 provider canary 能证明提取质量和额外成本值得继续后，才设计 durable Note mutation proposal：

```text
(SourceAction, VisibleTextHash, ContractId, ordered intents)
  -> durable proposal capture / zero-result tombstone
  -> owner-controlled Pod routing
  -> MemoPod apply
  -> durable apply receipt
  -> future recall
```

届时 `CharacterNoteIntent` 仍只表达角色叙事意图；`PodId` 是 owner policy，`MemoId` 是 apply result，不应提前塞进 extractor artifact。

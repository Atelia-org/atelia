# Galatea Default MemoPod Recall MVP 实施工单

状态：Active，已获准进入“逐步完成细节设计并实施落地”阶段

建立日期：2026-09-02

目标 owner：Galatea runtime / Character Memory integration

首版交付：单一 Default MemoPod、`MemoExactText`、每轮至多一条 recall

本文是交给专门 Coding Agent 会话的现行 implementation work order。它负责把已经落地的 MemoPod、DerivedInfo、
`PlayerTurnObservation`、`RecallBarrier`、`CharacterNoteOriginBarrier` 与 production Composition 串成第一条真实 recall
vertical，同时允许实施 Agent 在本文边界内继续完成局部细节设计。

## 0. 文档 authority 与阅读顺序

实施前只需按以下顺序恢复上下文：

1. 本文；
2. [`Dynamic Memory Puzzle Map`](../../dynamic-memory-puzzle-map.md)；
3. [`TextExtractor / Observation Bridge`](../../text-extractor-observation-bridge.md)；
4. 本文“源码地图”列出的当前代码；
5. 仅在需要追溯 MemoPod generic contract 时读取
   [`MemoPod target design`](../../../SessionJournal/work/active/memo-pod-target-design-and-implementation-plan.md)。

[`MemoPod / Galatea integration plan`](../../../SessionJournal/work/active/memo-pod-galatea-integration-plan.md) 中基于
`ISessionSupplementalContextSource`、Prepared v6 / recipe v2 的方案已经回滚并重新开放设计，只能作为历史证据。本文不恢复该
SessionJournal seam，也不授权修改 SessionJournal Prepared schema。现行注入点是已经落地的
`IGalateaPlayerTurnRecallProvider -> PlayerTurnObservation.Recalls`。

当本文与较早的 recall 草案冲突时，以当前源码、`dynamic-memory-puzzle-map.md` 与本文为准。实施完成后，应把本文状态改为
Complete并写入 implementation record；若目标设计发生实质变化，先更新本文再改代码。

## 1. 任务卡

### Intent

在普通 fresh player turn 中，把当前尚未注入 recall 的 `PlayerTurnObservation` 与pre-append context projection中最近的角色可见
Action，确定性投影成一次 MemoPod query。实施必须先证明这份pre-append projection与Observation append后主请求最终选中的context
兼容，不能仅因二者复用同一candidate source就宣称相同。MemoPod selector只输出相关 Memo IDs；Galatea随后执行Title资格、两道
barrier、SourceId和Observation预算等权威检查，最终至多注入一条包含`Title + ExactText`的`MemoExactText` recall。

### 用户可见承诺

- 角色无需切换到 Assistant / Agent / tool-call 行为模式；主角色模型只会在普通 Observation 中看到 runtime 注入的记忆正文。
- 只有Title已经补齐的Memo具备首版召回资格。
- MemoPod成功返回空ID列表表示真正的no-match；provider或authority失败不能伪装成“没有相关记忆”。
- 同一 recall anchor 已在当前 provider-visible context 可见时不重复注入。
- 产生某条 Character Note 的 source Action 仍在当前 provider-visible raw context 可见时，不把该Note零增量召回。
- 当前玩家行动是 query 的必需完整证据；最近Action是bounded辅助证据，不对任何正文做静默截断。

### MVP 完成后仍不承诺

- `MemoGist`或`MemoSummary`召回；
- 多Memo同时注入；
- 多Pod路由、Pod分类、分裂、合并或二级索引；
- 独立LLM query builder、query expansion或facet generation；
- durable recall result ledger、跨turn selection replay或exactly-once provider计费；
- active durable reply lease中的recall；
- provider不可用时静默降级成无recall；
- live provider质量、价格或cache hit率已经由单元测试证明。

## 2. 当前 baseline

### 已落地

- [`MemoPod`](../../../../prototypes/MemoPod/MemoPod.cs)在Frozen phase通过`RecallAsync`把稳定的
  `id + exact_text` corpus作为shared prompt prefix，只让selector输出排序后的Memo IDs。
- [`MemoPodPromptRenderer`](../../../../prototypes/MemoPod/Prompt/MemoPodPromptRenderer.cs)刻意不把Title/Gist/Summary写入
  FrozenPrompt；DerivedInfo变化不会改变selector corpus。
- [`Memo`](../../../../prototypes/MemoPod/Memo.cs)的`Title/Gist/Summary`是可重建DerivedInfo，`ExactText`是事实正文。
- [`PlayerTurnObservation`](../../../../prototypes/Galatea/PlayerTurnObservation.cs)已有`RecallType`、`RecallEntry`、
  `PlayerTurnRecall`以及strict canonical renderer/parser/display。
- [`RecallBarrier`](../../../../prototypes/Galatea/RecallBarrier.cs)从本次provider真正可见的canonical Observation中聚合exact
  `{RecallType, SourceId}` keys。
- [`CharacterNoteOriginBarrier`](../../../../prototypes/Galatea/CharacterMemory/CharacterNoteOriginBarrier.cs)把provider-visible
  raw Actions与CharacterMemory durable provenance做exact join，输出typed `{MemoPodId, MemoId}` blockers。
- [`PlayerTurnRecallProvider`](../../../../prototypes/Galatea/PlayerTurnRecallProvider.cs)已有internal provider seam；production仍使用
  disabled singleton，测试已有fixed provider vertical。
- [`GalateaServices`](../../../../prototypes/Galatea/GalateaServices.cs)已经在fresh ordinary player turn中、Observation append之前
  物化一份context window并构造两道barrier，随后在主Completion前允许recall注入。SessionJournal仍会在Observation append后为最终
  main request再次selection/materialization；两次结果的兼容性尚未由现行recall seam证明。active reply lease仍绕过recall。
- [`CharacterNoteDefaultPodReconciler`](../../../../prototypes/Galatea/CharacterMemory/CharacterNoteDefaultPodReconciler.cs)是每个
  writable session中Default MemoPod与CharacterMemory状态的owner，exact capture和DerivedInfo mutation共享短
  `_podMutationGate`。
- CharacterMemory SQLite V2已经把Title/Gist/Summary增强实现为可恢复的
  `Pending -> Prepared -> Planned -> Applied`后台管线。

### 当前关键缺口

1. provider request只携带player text、notices和barriers，尚未携带preliminary typed Observation、timestamp或recent Action；
2. barrier materialization得到的`SessionHistoryPlanningWindow`在构造barrier后被丢弃，没有同时投影query evidence；
3. Default MemoPod owner没有面向recall的Frozen snapshot/read contract；
4. 没有Galatea-owned SourceId codec、Title+ExactText body renderer或eligibility/filter planner；
5. 没有production recall connection binding与per-session provider composition；
6. MemoPod query当前64 KiB hard limit不能保证容纳最大合法player text加canonical encoding overhead；
7. Recall body当前256 KiB hard limit不能天然保证容纳最大合法ExactText再加Title和固定标签；
8. pre-append barrier/query projection与Observation append后main request最终context之间缺少pin或revalidation proof。

## 3. 锁定的设计裁决

### 3.1 MVP 不增加第二次 LLM 调用

查询文本由runtime-owned纯投影器确定性生成。MVP的唯一语义检索调用是`MemoPod.RecallAsync`中的selector。

原因：MemoPod selector同时看得到完整corpus和query，已经拥有相关性判断职责。前置LLM看不到corpus，却会把原始证据压缩成一个
有损瓶颈，并增加串行延迟、费用、失败点与模型漂移。`TextExtractor`用于提取明确业务artifact，Recap rewriter用于创造新的派生文本，
二者都不是本query renderer的直接职责模板。

未来若真实eval证明原始context query持续漏掉隐含需求，可以新增可选`RecallCueExtractor`；它必须输出bounded typed facets，并把
原始context与facets一起交给selector，不能用自由文本摘要替代原始证据。

### 3.2 Query、selection 与 injection 三层分权

```text
Galatea query context projector
  owns: exact current Observation projection + bounded recent visible Actions
  does not own: semantic relevance or Memo selection

MemoPod selector
  owns: one Frozen corpus epoch中的semantic relevance ordering
  returns: hydrated immutable Memo values in model-selected order
  does not own: Galatea barriers, Title eligibility, SourceId or Observation budget

Galatea recall planner/provider
  owns: eligibility + barriers + SourceId + body + final count/budget
  returns: PlayerTurnRecall values
  does not become: Memo durable authority or SessionJournal context owner
```

### 3.3 Context authority 与 compatibility proof

recent Action必须来自构造`RecallBarrier`与`CharacterNoteOriginBarrier`的同一个pre-append
`SessionHistoryPlanningWindow`。不得另行读取browser recent view、全部durable history或一个独立“最近N轮”快照。

但这份window当前不能直接称为“main provider最终可见window”：Galatea在Observation append前materialize它，之后
`SessionJournalEngine.SendAsync`仍会在新的completion boundary上再次执行candidate selection/materialization。复用同一个
`ICoherentContextCandidateSource`不自动证明descriptor、raw start、derived contributions或final context保持兼容。

WP-00必须先关闭以下二选一的阻断闸：

1. **Existing-contract proof**：用现有descriptor/admission anchor/setup/raw-range identities证明pre-append window恰好等于final main
   context去掉本轮新Observation后的部分，并用race/stale/candidate-change tests锁定；或
2. **Minimal pin/revalidation seam**：设计一个provider-neutral、非durable-schema的最小seam，让recall使用的selection在final request
   construction时被exact复用或重验。

若现有contracts无法证明，实施Agent必须先提交seam candidate与本文修订，取得fresh authorization后才能修改SessionJournal owner。
不得为了绕开该闸恢复Prepared v6/supplemental context，也不得先启用一个“看起来通常相同”的production recall路径。

目标形状：

```text
GalateaPlayerTurnRecallContext
├── RecallBarrier
├── CharacterNoteOriginBarrier
└── RecentVisibleActions
```

`BuildCurrentRecallBarriersAsync`应收口为一次pre-append materialization返回上述完整context。Action继续使用
`GalateaVisibleActionTextRenderer`，排除reasoning、tool-call blocks与inline think。MVP从window尾部向前寻找最近一条非空visible
Action；schema保持数组形状，默认数量1只是policy，不是wire grammar上限。

### 3.4 Current Observation authority

recall request应直接携带preliminary `PlayerTurnObservation` strong type，而不是再次用`PlayerText + Notices`拼一个缺timestamp的副本。
该instance必须：

- 已采样当前external local timestamp；
- 与打开本次RecapGrid fresh pass时使用的preliminary Observation语义一致；
- `Recalls.Count == 0`，避免query依赖本轮尚未选择的结果；
- 保持原始player text，不trim、不normalize、不总结。

`GalateaPlayerTurnRecallRequest`可及时重构成`CurrentObservation + RecentVisibleActions + Barriers`，不保留并行的旧字段真源。

### 3.5 Query V1

建议schema id：

```text
atelia.galatea.memo-recall-context.v1
```

建议canonical JSON语义形状：

```json
{
  "schema": "atelia.galatea.memo-recall-context.v1",
  "characterName": "Galatea",
  "retrievalGoal": "memories materially useful for the character's next narrative action",
  "currentTurn": {
    "externalLocalTimestamp": "2026-09-02T14:20:00+08:00",
    "playerText": "继续追问她刚才提到的那个人。",
    "externalNotices": []
  },
  "recentVisibleActions": [
    {
      "ordinalFromNewest": 0,
      "text": "她迟疑片刻，提到了旧城区的药剂师莱恩。"
    }
  ]
}
```

约束：

- 使用structured writer和strict UTF-8，不手拼JSON；
- 属性顺序、timestamp格式、notice kind token和array顺序固定并有golden；
- `PlayerTurnNotice.Reply`与`DeliveryFailure`可作为external notices进入query；
- `NoteSaveReceipt`只证明保存成功，不是新的retrieval evidence；防重复由typed barriers负责，因此receipt不进入query；
- recent Actions按从旧到新渲染；MVP只有一条时`ordinalFromNewest == 0`；
- corpus和query中的全部自然语言均为untrusted data；不得削弱MemoPod现有prompt-injection声明；
- renderer不做关键词抽取、指代消解、摘要或语言转换。

### 3.6 Bounds

必须在production activation前用可执行测试关闭以下问题：

- 当前player text上限为64 KiB，而JSON escaping后可能明显大于64 KiB；不得依赖“通常很短”。
- 推荐把`MemoPodLimits.MaximumRecallQueryUtf8Bytes`提高到至少能容纳最坏情况下的完整合法player text与固定V1 envelope，
  初始候选为512 KiB；最终数值由pre-count和worst-case golden证明，而不是拍脑袋锁定。
- current player text与固定字段是required，不能截断或省略。
- optional item的优先级固定为：current external notices的最长whole-item前缀，然后latest recent Action；
  不能只截取开头、结尾或中间片段。未纳入的optional item必须有bounded `DebugUtil.Trace/Info`诊断。
- 为visible Action建立code-owned UTF-8 admission cap，初始候选为64 KiB；超过cap时整条Action不进入query并记录bounded诊断，
  current player text仍完整保留。
- 若required部分仍无法render，configured recall path fail closed；不能把它伪装成no-match。
- 调整`MaximumRecallBodyUtf8Bytes`或建立更窄的MemoExactText body bound，使每条合法`Title + ExactText + fixed labels`都能完整
  表达；不能截断Memo正文。
- 所有新增数值必须是code-owned named constants，并覆盖boundary-1/boundary/boundary+1与JSON escaping adversarial tests。

### 3.7 SourceId 与可见 body

首版只支持Default MemoPod。锁定SourceId格式候选：

```text
memo-pod:v1/<32-char MemoPodId>/<canonical MemoId>
```

示例：

```text
memo-pod:v1/00000000000000000000000000000001/m1:00000001
```

由`GalateaMemoRecallSourceIdCodec`统一Format/TryParse；只接受canonical round-trip、exact ASCII prefix、canonical
`MemoPodId`与`MemoId`，不接受URI normalization、大小写变体、额外segment或percent encoding。formatter产物必须满足现有512-byte
`RecallEntry.SourceId`上限。

`MemoExactText` body锁定为：

```text
标题：{Title}

正文：
{ExactText}
```

Title已经由MemoPod验证为trimmed、nonblank、无control characters；ExactText逐字符保留。不要在body内部再发明一层Markdown fence，
外层`PlayerTurnObservationEnvelope`已有adaptive fence。

### 3.8 Eligibility、过滤与顺序

MVP final injection count为`0..1`。MemoPod可以bounded over-fetch。WP-03必须把`MemoRecallOptions`四项全部锁成named policy并测试：

- `MaxResults`：初始候选8；
- `MaxTokens`：优先使用dedicated connection的正数`MaxTokens`，null时使用code-owned小输出默认值；最终值必须落在MemoPod
  `1..4096`合同内，超界配置启动时拒绝；
- `MaximumFrozenPromptUtf8Bytes`：首版不低于Default Pod允许的完整FrozenPrompt hard limit，避免静默把合法Pod变成部分corpus；
- `MaximumHydratedExactTextUtf8Bytes`：至少覆盖`MaxResults * MaximumMemoExactTextUtf8Bytes`，并受MemoPod active exact-text hard
  limit约束。

准确数值与overflow-safe计算由WP-03 tests锁定；不得只依赖`MemoRecallOptions`构造函数碰巧抛错。

按MemoPod relevance order遍历hydrated Memos，对每个candidate依次：

1. 要求`Title`非null；Title缺失是正常ineligible，不是provider failure；
2. 要求PodId等于Default MemoPod；
3. 构造canonical SourceId与`RecallEntry(MemoExactText, sourceId)`；
4. 若`CharacterNoteOriginBarrier.Contains(podId, memoId)`则跳过；
5. 若`RecallBarrier.Contains(entry)`则跳过；
6. render完整Title+ExactText body并验证final aggregate Observation budget；若单条合法body本身满足body contract、但加入当前
   player text/notices后会超过1 MiB aggregate limit，则把该candidate视为正常budget-filtered underfill，不截断、不阻止主Completion；
7. 选择第一条eligible candidate后停止。

过滤后允许underfill，不做第二次LLM调用补位。MemoPod自身继续负责拒绝duplicate/unknown/inactive IDs并保持model ordering。若真实使用中
blocked/ineligible candidates经常占满over-fetch窗口，再单独设计MemoPod typed `excludedMemoIds`；MVP不得把“排除这些ID”写成query内
的自然语言指令。

### 3.9 Pod snapshot、锁与并发

Default MemoPod仍由`CharacterNoteDefaultPodReconciler`拥有。不得让production provider根据user path绕过owner自行打开第二条无协调路径。

实施应建立一个窄的Frozen recall snapshot/read contract：

1. 使用caller cancellation等待`_podMutationGate`；
2. gate内要求store为`Ready`、无active exact capture；若有active DerivedInfo `Planned` mutation，先执行现有provider-free recovery，
   recovery不能收口则fail closed；`Pending/Prepared`但尚未改变Pod的work可以与settled snapshot共存；
3. 打开exact Default Pod ID，要求Frozen phase，并要求opened `ComputeStateIdentity()` exact等于
   `CharacterMemoryStatusSnapshot.SettledDefaultPodStateIdentity`；
4. snapshot必须在打开时把FrozenPrompt与Memo values完整持有于内存，并保留MemoPod自身same-Frozen-epoch验证；
5. 释放`_podMutationGate`；
6. 在gate外调用Completion provider；
7. 从该snapshot返回self-contained `MemoRecallResult`，provider完成后不再读取owner/current Pod。

绝不允许在网络调用期间持有`_podMutationGate`，否则会让ExactText capture和DerivedInfo apply被provider latency阻塞。snapshot之后发生的
合法Pod publish不会使已打开的旧snapshot变成durable current authority，但它仍是本次query的一致输入；返回的Memo只负责当前
Observation，不成为新的Memo store。

不要为了recall把Memo正文复制进CharacterMemory SQLite，也不要在`RecallAsync`之后重新`List/Get`当前Pod来混合两个epoch。

### 3.10 Connection 与 capability gate

新增独立可选binding：

```text
galatea.memo-recall
```

它允许recall workload选择适合大prompt cache、低output成本的connection，例如用户提到的`deepseek-v4-flash`或
`gpt-5.6-luna`。binding可以指向与其他功能相同的connection id，因此独立路由不等于必须创建重复物理client。

MVP capability规则：

- binding为null：production使用disabled provider，并在context selection、CharacterMemory recall read和Completion call之前零成本绕过；
- binding非null：必须能从host-wide registry exact inspect/borrow该connection，不允许default fallback；
- startup静态校验要求non-null `galatea.memo-recall`必须同时有non-null `galatea.character-note-extractor`；
- 每个user的CharacterMemory store与Default Pod仍在首次`GetSessionAsync`时lazy attach，具体filesystem/store/Pod失败在该session
  admission时fail closed，不能误报成host startup validation；
- maintenance mode不创建recall provider、不打开writable CharacterMemory、不调用Completion；
- production seam从host-wide provider instance重构为host-wide factory：factory只持有validated routing/client borrow能力，并在
  CharacterMemory attach成功后为`UserSessionHost`创建per-session provider；
- `UserSessionHost`成为provider instance的唯一session owner，turn调用点只读`host.PlayerTurnRecallProvider`；现有host-wide fixed provider
  测试入口同步迁移为test factory，不保留第二条instance真源；
- per-session provider绑定该session的Default Pod owner与borrowed completion client；不得用user id反查可变session全局表；
- session disposal不拥有/释放borrowed host-wide client，但必须确保in-flight turn结束后才释放CharacterMemory owner。

实现时更新`GalateaCompletionOwner`、root config、strict loader、template与tests。不要默默复用
`galatea.character-note-extractor`；operator若希望共用模型，可以显式把两个binding写成同一个connection id。

### 3.11 Failure 与 cancellation

首版保持configured feature fail closed：

| 情况 | 语义 |
|---|---|
| selector成功返回`[]` | 正常no-match，主Completion继续且不注入recall |
| candidates全部被Title/barrier过滤 | 正常underfill，主Completion继续 |
| caller cancellation | 原样传播，不调用main Completion |
| query required部分超界 | local contract failure；不截断、不伪装no-match |
| context/barrier materialization unavailable或authority mismatch | fail closed |
| Pod missing/corrupt/not Frozen/epoch invalid | fail closed |
| recall transport、timeout、termination或model output invalid | fail closed |
| fatal/programming failure | 保持现有fatal taxonomy，不包装成普通provider unavailable |

如果未来需要“记忆服务故障时仍继续主回合”，必须新增显式best-effort Host policy、diagnostics和tests；不得用catch-all把failure压成空列表。

## 4. 目标端到端序列

```text
fresh PlayerAction + sampled timestamp
  -> preliminary typed PlayerTurnObservation (Recalls empty)
  -> RecapGrid OpenFresh with preliminary canonical Observation
  -> materialize one pre-append SessionHistoryPlanningWindow
       -> RecallBarrier
       -> CharacterNoteOriginBarrier
       -> latest non-empty visible Action (MVP max 1)
  -> prove/pin compatibility with the final post-append main-request context
  -> GalateaMemoRecallQueryRenderer
  -> open Default MemoPod Frozen snapshot under short pod gate
  -> release pod gate
  -> MemoPod.RecallAsync on dedicated memo-recall connection
  -> Title + origin barrier + recall barrier + body/budget filtering
  -> 0..1 PlayerTurnRecall(MemoExactText)
  -> final canonical PlayerTurnObservation
  -> SessionJournal SendAsync / main Galatea Completion
```

这条路径仍只在“普通fresh player turn且无active durable reply lease”运行。不要在本工单中扩张ready-turn、inbound mail、
tool continuation或recovery selection语义。

## 5. 工作包

每个工作包都按“再审视 -> 定稿 -> 实施与focused tests -> 独立review -> 当前包尾修”闭环推进。允许一个Agent连续完成多个包，
但commit应保持可审阅，不把全部改动压成一个无法定位问题的大提交。

### WP-00：Fresh baseline 与局部设计复核

目标：在改代码前确认本文没有被更新后的源码推翻。

必须核对：

- `IGalateaPlayerTurnRecallProvider`当前调用点、disabled bypass与active reply lease guard；
- pre-append recall materialization与post-append final main-request selection/materialization的真实调用链；
- existing-contract identity能否证明两者兼容，不能证明时所需最小pin/revalidation seam；
- `UserSessionHost.TurnLock`与`_podMutationGate`的持有范围；
- MemoPod Open确实物化独立Frozen snapshot，后续publish不会修改该handle；
- connection registry的borrowed client与dispose ownership；
-合法player text、Action、Title、ExactText和Observation的现有bounds。

若结论只要求本文边界内的小幅调整，先更新本文再继续；只有需要改变“无第二LLM、ExactText-only、两道barrier、每轮最多一条、
fail closed、PlayerTurnObservation注入”之一时才升级给用户。

完成定义：baseline call graph、涉及文件、拟新增types与测试分组在首个commit或implementation record中可定位；context compatibility
proof已有executable test，或本包停在seam candidate并取得fresh authorization。该闸未关闭时WP-04不得激活production recall。

### WP-01：Pure contracts、codec 与 canonical renderer

建议写入范围：

- `prototypes/Galatea/PlayerTurnRecallProvider.cs`；
- 新建`prototypes/Galatea/CharacterMemory/GalateaMemoRecall*.cs`，或fresh inventory后选择同一domain下更清楚的文件名；
- `prototypes/Galatea/PlayerTurnObservation.cs`仅在关闭body bound时修改；
- `prototypes/MemoPod/MemoPodLimits.cs`仅在关闭query bound时修改；
- 对应`tests/Galatea.Server.Tests/`与`tests/MemoPod.Tests/`。

交付：

- `GalateaPlayerTurnRecallContext` / recent Action value；
- 重构后的request strong type，移除重复旧字段真源；
- `GalateaMemoRecallQueryRenderer`与exact schema golden；
- `GalateaMemoRecallSourceIdCodec`；
- `MemoExactText` body renderer；
- named count/query/body budget policy；
- hostile Unicode、JSON injection、fence-like正文、boundary和round-trip tests。

本包不接Completion client、不打开Pod、不改production composition。

### WP-02：同窗 context materialization

建议写入范围：

- `prototypes/Galatea/GalateaServices.cs`中的current barrier/query context path；
- `prototypes/Galatea/RecallBarrier.cs`和
  `prototypes/Galatea/CharacterMemory/CharacterNoteOriginBarrier.cs`仅在抽取共享pure projection helper确有必要时修改；
- focused Galatea tests。

交付：

- 一次pre-append materialization同时产生两道barrier与recent visible Actions；
- latest non-empty Action选择、空window、reasoning/tool-only Action、Selected/raw-only context paths；
- current preliminary Observation含exact timestamp且recalls为空；
- 证明query和两道barrier没有各自触发重复的pre-append读取，同时承认SessionJournal final main request仍有自己的post-append
  selection/materialization；
- 通过WP-00选定的identity proof或pin/revalidation seam，证明final context不会遗漏pre-append barrier应看见的recall anchors与source
  Actions；
- disabled provider仍在materialization之前旁路。

本包继续使用fake/fixed provider，不查询MemoPod。

### WP-03：Default Pod Frozen recall snapshot 与 planner/provider

建议写入范围：

- `prototypes/Galatea/CharacterMemory/CharacterNoteDefaultPodContracts.cs`；
- `prototypes/Galatea/CharacterMemory/CharacterNoteDefaultPodReconciler*.cs`；
- 新的Default MemoPod recall provider/planner文件；
- `tests/Galatea.Server.Tests/CharacterMemory*`与新增recall tests。

交付：

- Default Pod owner提供窄Frozen recall snapshot/read contract；
- gate只覆盖authority检查与snapshot open，provider await发生在gate外；
- provider调用现有`MemoPod.RecallAsync`，不复制其tool protocol/parser；
- relevance order、Title eligibility、两道barrier、SourceId、body、0..1 final selection；
- over-fetch/underfill、no-match、invalid output、cancellation、Pod failure与snapshot epoch tests；
- 四项`MemoRecallOptions` named policy与boundary tests；
- blocking fake provider证明recall等待期间Pod mutation gate可继续被合法mutation取得。

本包仍可由test composition直接构造provider，不修改root config。

### WP-04：Production connection 与 per-session composition

建议写入范围：

- `prototypes/Galatea/GalateaCompletionOwner.cs`；
- `prototypes/Galatea/GalateaConfig.cs`与`GalateaServices.cs`中的strict config/composition；
- config templates、README和相关tests。

交付：

- optional exact binding `galatea.memo-recall`；
- host-wide factory exact-bind registry routing，per-session recall provider借用client并绑定Default Pod owner；
- 删除host-wide provider instance真源并把fixed-provider tests迁到factory/session seam；
- null binding、maintenance mode、missing CharacterMemory、invalid connection的closed行为；
- fresh ordinary turn production vertical真实经过query renderer -> fake Completion client -> MemoPod -> final Observation；
- selector调用发生在main Completion之前且至多一次；
- no-match继续main，configured failure阻止main；
- active reply lease仍零recall；
- session dispose与in-flight turn/resource ownership tests。

### WP-05：Closure review、文档与可选provider canary

交付：

- 独立review重点检查双真源、第二次context读取、锁跨provider await、barrier遗漏、Title缺失降级、failure被吞与disabled路径额外I/O；
- 更新`dynamic-memory-puzzle-map.md`、`text-extractor-observation-bridge.md`和Galatea README为现行状态；
- 本文状态改为Complete并追加commit IDs、tests、remaining risks与下一步入口；
- 默认验证全部provider-free。只有用户另行明确授权真实provider调用时，才执行content-free/disposable canary并记录
  model/connection、cache telemetry、latency、output tokens与人工相关性判断。

## 6. 验收矩阵

### Query 与 context

- current player text、timestamp、Reply/Failure notice与最近visible Action准确进入canonical query；
- NoteSaveReceipt不进入query；
- Action reasoning、tool-call与inline think不进入query；
- latest Action为空时向前找最近非空；完全没有时数组为空；
- query recent Action与两道barrier来自同一次pre-append materialization；
- pre/post context compatibility proof在candidate变化、stale materialization与raw head推进场景仍fail closed；
- JSON property/order/escaping/timestamp有exact golden；
- required text不截断，optional items只按whole-item policy纳入。

### Selection 与 injection

- selector返回空IDs时final Observation无recall；
- 第一相关Memo无Title、被origin barrier阻止或被recall barrier阻止时继续考察后续candidate；
- 第一eligible Memo按exact relevance order成为唯一recall；
- SourceId formatter/parser round-trip且拒绝noncanonical输入；
- body逐字符保留Title/ExactText并能通过PlayerTurnObservation render/parse/display/recovery；
- 已召回anchor在下一轮provider-visible context中被阻止；
- source Action离开raw context后，origin barrier不再凭历史猜测阻止该Memo。

### Lifecycle 与 failure

- disabled binding：零context materialization、零CharacterMemory recall read、零selector client；
- enabled no-match：一次selector、一次main Completion；
- enabled selected：一次selector、一次main Completion，main看到exact composite Observation；
- selector failure/invalid output：零main Completion且不会写入伪造recall；
- caller cancellation：无main Completion，资源可安全释放；
- provider阻塞时不持有Pod mutation gate；
- snapshot open检查store Ready、无active capture、active Planned recovery、Default Pod ID/Frozen phase与settled state identity；
- DerivedInfo publish与recall snapshot并发时，每次recall只使用一个一致epoch；
- active reply lease、inbound mail、ready-turn与maintenance mode保持当前无recall行为；
- recent display隐藏SourceId，durable canonical Observation仍保留anchor。

### Prompt cache 与隐私

- MemoPod Frozen corpus仍位于shared prefix，Galatea query只进入final Observation tail；
- `PromptCacheReuseHint.ReuseExpectedSoon`不回退；
- DerivedInfo不进入FrozenPrompt；
- 普通diagnostic不记录Memo正文、完整query、玩家正文或Action正文；只记录bounded IDs、counts、bytes、outcome与hash/usage。

## 7. No-Go 条件

- 新增必经的LLM query-builder或用TextExtractor先生成自由文本query；
- 让主角色模型生成query、MemoId、SourceId或tool call；
- 把recall塞进RecapGrid contributions或恢复已回滚的SessionJournal supplemental/Prepared v6方案；
- 从browser recent view、全durable history或不同snapshot构造query/barriers；
- query或Memo正文静默截断；
- 把Title/Gist/Summary重新加入MemoPod FrozenPrompt；
- 在Completion provider await期间持有Pod mutation gate；
- provider结果返回后重新打开current Pod并混合另一个epoch的Memo；
- 用catch-all把configured failure变成空recall；
- 为了MVP顺手实现Gist/Summary、多Pod、索引、dominance或reply lease recall；
- 默认测试连接真实网络或读取/迁移ignored live CharacterMemory state。

## 8. 验证命令

按风险从focused到full串行执行，所有dotnet测试使用fresh临时目录：

```bash
env TMPDIR="$(mktemp -d)" dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false
env TMPDIR="$(mktemp -d)" dotnet test tests/MemoPod.Tests/MemoPod.Tests.csproj --no-restore -m:1 -nr:false
env TMPDIR="$(mktemp -d)" dotnet test tests/MemoPod.PublicSurface.Tests/MemoPod.PublicSurface.Tests.csproj --no-restore -m:1 -nr:false
git diff --check
```

若修改公共surface、solution/project references或shared Completion config loader，再补对应architecture/public-surface/full solution gates。
并行worker可以各跑focused tests，最终集成者必须再串行运行受影响的完整suite，避免MSBuild/temp竞争被误判为产品回归。

## 9. Done when

只有同时满足以下条件，本工单才可标记Complete：

- production non-null `galatea.memo-recall` binding能在普通fresh player turn完成Default MemoPod真实selector调用；
- query由canonical runtime projector生成，包含完整current player text；符合Action cap且remaining budget可容纳时包含同窗latest recent
  Action，否则保留明确whole-item omission诊断；
- selector是整条query construction/selection路径中唯一新增的LLM调用；
- only Title-qualified `MemoExactText`按稳定SourceId注入，final count严格为0..1；
- RecallBarrier、CharacterNoteOriginBarrier与recent Action由同一pre-append window构造，并通过已验证的compatibility proof或pin seam
  对齐post-append final main context；
- no-match、underfill、failure、cancellation、disabled、maintenance、active lease语义均有vertical tests；
- Pod snapshot一致性与“provider await不持有mutation gate”有可执行并发测试；
- query/body bounds通过worst-case golden关闭，无静默截断；
- MemoPod、Galatea focused/full tests与diff check通过，或只剩明确记录且与本工单无关的baseline failure；
- 三份Galatea文档与实际代码一致，本文写入implementation record与remaining risks；
- 独立review无未修复的P0/P1 finding。

## 10. 实施报告格式

每个worker或最终Coding Agent至少报告：

- 完成的WP与关键设计结论；
- commit id与修改文件；
- 实际运行的验证命令、通过/失败/skip数量；
- reviewer findings与尾修；
- 是否接触ignored live state或真实provider，默认答案应为“否”；
- 仍未关闭的风险与下一轮最自然入口。

## 11. 完成后的下一步候选

MVP获得真实使用数据后，再按证据选择下一项，而不是在本工单中预埋半成品：

1. 分析precision/recall、empty rate、barrier filtering rate、Title-missing rate、query/cache tokens与latency；
2. 若over-fetch常被blocked IDs耗尽，设计MemoPod typed exclusions；
3. 若一条recent Action不足，扩大同窗bounded suffix；
4. 若raw context query持续漏召回，再设计typed `RecallCueExtractor`；
5. 内容增强质量稳定后启用`MemoGist`/`MemoSummary`与coverage dominance；
6. 多Pod与二级索引成熟后再拆routing/planner层；
7. 最后单独设计active durable reply lease与recall result的recovery/continuity语义。

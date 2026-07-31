# Galatea → SessionJournal + DerivedRecap：后续实施计划

> **状态**：Plan
> **日期**：2026-07-31
> **上位设计**：
> [DerivedRecap Host Integration](derived-recap-host-integration-target-design.md)
> **目标实例**：
> `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/`

## 0. 目标

用 `SessionJournalEngine + DerivedRecap`替换Galatea当前的`ChatSessionEngine + CompactAsync`，
保留现有单账号、connection切换、SSE streaming、用户stop与recent-turn UI，并让
`cyber-copy-upgraded/chat-session-legacy-upgrade-export.json`成为首个真实cutover实例。

迁移后：

- raw SessionJournal events是唯一会话correctness source；
- DerivedRecap提供`world-understanding`与`autobiographical`常驻前情提要；
- repo-owned planner config统一支配online与operator maintenance；
- Prepared可在进程重启后safe exact dispatch；Started被识别为uncertain并默认Refuse，显式授权后才
  restart new attempt；
- 不保留ChatSession/SessionJournal dual read、dual write或runtime fallback。

剩余工作包不自动串行施工；每个工作包先做package-local review，再实施、测试和独立review。

## 1. 当前事实

Galatea当前：

- `Galatea.Server.csproj`引用`ChatSession`、`Completion`和`Completion.Tools`；
- `GalateaHostService`按用户lazy打开一个`ChatSessionEngine`；
- 每轮按UI选择connection并重建runtime；
- `SendMessageAsync`使用`CompletionStreamObserver`支持SSE与stop；
- response发送后按旧EstimatedTokens threshold调用`CompactAsync`；
- recent-turn projector读取`ChatSessionEngine.Context`并把旧`RecapMessage`显示成伪turn；
- “撤销上一轮”使用`TryRemoveLatestCompletedTurn`；
- startup会把config system prompt同步进旧repo。

目标export及其production import projection的content-free事实：

```text
schema             atelia.chat-session.legacy-upgrade-export.v1
bytes              1,281,881
SHA-256            b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3
export events      77
import observations 71
import agent actions 71
legacy compaction  2  (import时跳过)
legacy recap       2  (import时跳过)
warnings           0
```

既有C3 fresh-import evidence：

```text
raw events         148
HistoryUnits       142
HistoryLoad        116,458
selected absorbed  98,082
selected recent    18,376
```

这些证据证明数据和DerivedRecap vertical可用，但尚未证明Galatea Server本身已切换Host。

## 2. Cutover前必须关闭的相邻缺口

### 2.1 Public recap composition

按Host Integration设计完成：

- public neutral capability snapshot；
- public pure config resolver；
- Building-first preparer；
- authority-only lifecycle factory；
- deferred maintainer registry；
- CLI先迁移到这些API并保持现有验收。

这是Galatea不得复制CLI internal逻辑的前置条件。

### 2.2 Completion target recovery binding

新turn仍使用页面选择的connection。Prepared/Started recovery则必须读取durable
`SessionCompletionTargetIdentity`，在Galatea connection registry中寻找exact matching
connection/adapter，并绑定该client。

当前`InspectExecutionBoundary()`刻意不暴露Prepared payload，也没有提供frozen target读取面；
Galatea不能靠默认connection或逐个创建client试错。SessionJournal应增加窄的read-only
`InspectRuntimeRecoveryRequirements()`（名称待定），返回
`NoRuntimeRequired / NewRequestRequired / FrozenCompletionRequired`；最后一种只携带当前phase
恢复所需的non-secret CompletionTarget、可选ToolRuntime identity与uncertainty kind，不返回request
正文。

CLI internal `CompletionTargetIdentityFactory`也不能成为Galatea依赖。connection与request-adapter
fingerprint算法应提升到`Completion`的public surface；Host再显式构造SessionJournal的target
record，避免让Completion反向依赖SessionJournal。

验收：

- 新turn从connection A切换到B合法；
- A上Prepared后重启，即使页面默认变成B，仍只用exact A恢复；
- A不再可用时返回typed recovery unavailable，不静默改用B。
- Started默认Refuse时不创建provider call、不写raw event；只有用户显式授权restart才允许新attempt。

### 2.3 Dynamic runtime setup

`CompletionRequest.ModelId`来自raw governing `RuntimeConfigSetup`，并不由
`SessionRuntime.CompletionClient`自动覆盖。因此每轮选择connection时：

- Host先解析selected connection metadata，不必先创建client；
- stable Idle/TurnFailed上比较governing ModelId/CompletionSurfaceId；
- 发生变化时显式append `RuntimeConfigSetup`，保留schema和DerivedContext ordinal；
- 再从新的raw head执行Recap preparation；
- Prepared/Started/tool-active tail绝不append setup；
- setup append本身是operator intent，即使后续Recap readiness失败也保留，但Observation和LLM调用
  仍必须是零。

否则“UI切换connection”只会换endpoint，request里仍可能使用旧model/surface。

### 2.4 Recent-turn projection

Galatea recent UI只展示raw、已完成的Observation/Action turn：

- 增加一个复用SessionJournal内部fold/recovery语义的窄public projector；
- 跳过protocol-only events；
- 正确聚合tool loop为一个visible turn；
- 不把DerivedRecap contribution作为raw conversation event显示；
- imported 71对visible turns与legacy export逐项对照。

不要让Web Host自行解码SessionEvent payload或重写reducer。

### 2.5 System prompt sync

fresh repo由`SessionCreateOptions`写入初始prompt。existing repo发生配置变化时：

- 不在startup自动append；只在真正进入下一次new-request path时检查；
- 先inspect durable phase；
- 只在稳定idle/failed boundary执行显式`AppendSystemPromptSetup`；
- Prepared/Started或active tool/completion期间不插入setup；
- 比较governing setup避免每次startup重复append；
- prompt改变只影响后续request，不改写historical Prepared commitment。

### 2.6 Failed turn与撤销上一轮

`SessionJournalEngine`目前没有与`TryRemoveLatestCompletedTurn`等价的public high-level API。
此外，SessionJournal在known completion failure/用户stop时会保留Observation、Prepared/Started和
`CompletionAttemptFailed`，而当前Galatea承诺失败turn不进入history。若直接在TurnFailed后
`SendAsync`，失败Observation仍可能进入后续Context。

应单独设计并实现CAS-protected turn abandon/rewind contracts：

- `AbandonFailedTurn`只接受exact TurnFailed head，回到该turn Observation的前驱；
- `RewindLatestCompletedTurn`首版只接受current head exact等于latest completed terminal Action，回到
  该turn Observation的前驱；
- TurnFailed先走独立abandon；setup-only suffix或其他非exact boundary返回typed unavailable；
- raw-core completed-turn locator同时供recent projector和rewind使用；
- exact expected head CAS移动当前branch ref；
- 返回被撤销的raw user/action projection供UI回填；
- 不删除raw event bytes；
- off-lineage Published/Building自然不可见，不修改或重编号sidecar；
- 不暴露通用任意`MoveRef`给Galatea。

known failure/stop必须先abandon，再允许system-prompt/runtime setup同步或下一次Send。uncertain
Started recovery则不能伪装成known failed turn；默认遵守core的`Refuse` policy，等待operator
显式决定是否restart attempt。

在这些API完成前，不应把Galatea storage cutover宣称为产品行为等价。若决定暂时删除Undo或接受
failed Observation留在Context，必须作为显式产品cut而不是伪装成实现细节。

stop/cancellation分阶段定义：

- normalization前或期间：可取消，尚无raw Observation；
- Recap maintenance期间：可取消；未完成Building保持可Resume，不开始agent dispatch；
- lifecycle/maintenance成功返回后，Host原子地从pre-dispatch CTS切换为observer-only stop；
- agent streaming期间：通过observer stop形成known terminal failure，不提交partial Action，
  随后abandon failed turn；
- crash在Started之后：属于uncertain recovery，不能自动abandon或自动重试。

Host文案应说“不会保留partial assistant response”；只有failed-turn abandon成功后，才可继续承诺
该turn不进入active history。

## 3. 工作包

### H0：Public config resolution（Done，2026-07-31）

范围：

- Planner neutral capability/config snapshot contracts；
- pure resolver与typed defects；
- CLI internal resolver迁移；
- Planner README改用public API。

非目标：

- readiness、Completion client、Galatea代码。

Gate：

- resolver focused tests；
- CLI planner-config/run reports不变；
- standalone consumer不引用CLI即可resolve。

完成结果：

- `SessionJournal.DerivedRecap.Planner`已提供public immutable config snapshot、metadata-only
  capability snapshot、Host-injected policy/estimator resolution catalog、pure resolver与typed
  defects；
- pure resolver不打开文件/Store/raw repo，不引用concrete Maintainers或Completion，也不表达
  `Unavailable`；
- CLI仅保留concrete capability投影、prompt fingerprint/report enrichment与execution catalog，
  原policy/estimator/profile/limits解析逻辑已迁出；
- Planner standalone tests覆盖custom catalog、unknown identities、estimator identity mismatch、
  duplicate active shape、hard caps、snapshot provenance与catalog invariants；CLI focused tests继续
  验证既有report与零client/Store行为。

### H1：Building-first preparation（Done，2026-08-01）

范围：

- public prepared authority/result；
- lazy active-composition source；
- catalog migration与raw-head/source race typed result；
- authority-only lifecycle factory；
- 收窄unsafe unpinned online constructor。

H0后的边界补充：repository-backed active-composition source直接产出public
`ResolvedRecapPlanningConfiguration`，只拼接loader typed result与public resolver typed result；不得
重新实现policy/estimator/profile解析。source构造仍须zero-touch，且只有preparer确认无Building后
才允许调用一次。

Gate：

- Building + throwing config source仍FrozenBuilding且source调用0次；
- no Building source调用1次；
- Prepared/Started phase tests继续zero-touch；
- NewPlanning只能使用pinned baseline。

完成结果：

- Planner新增construction-zero-touch的
  `RepositoryRecapActivePlanningConfigurationSource`，只复用public loader与H0 pure resolver；
- `DerivedRecapOperationPreparer`严格执行capture lineage → Building-first → 单次active source →
  latest Published catalog → raw-head fence，并返回public
  `PreparedRecapOperationAuthority.FrozenBuilding/NewPlanning`；
- FrozenBuilding携带exact descriptor且不携带config provenance；NewPlanning携带同一个resolved
  snapshot与head-matched baseline；两种authority都内部绑定签发时的repository/RefId，不能跨repo
  重放；raw-head/source races是typed `Retryable`；
- preparer拒绝Store/repository/RefId错配；完整capability snapshot验证frozen
  `(MaintainerId, Target)`并约束source解析出的active profiles，active roster只支配new planning；
  file-backed active snapshot还必须来自该
  repo的canonical config path，resolved active profiles必须属于同一次传入的完整capability snapshot；
- `DerivedRecapOnlineLifecycleCoordinator`的public production入口已收窄为authority-only
  `Create(...)`，unsafe unpinned constructors与descriptor-only factory降为internal；
- CLI `RecapOperationReadiness`已经迁为public preparer之上的thin concrete/report adapter，
  `run`与online lifecycle均消费public authority，没有保留第二套Building/catalog/baseline算法；
- focused tests覆盖Building + throwing source零调用、no Building source恰好一次、config
  provenance、catalog migration、source/raw-head races、cancellation、repository source与public
  lifecycle surface；CLI Prepared/Started/Building online paths继续zero-touch。

完成时验证（2026-08-01）：

- Planner suite：205/205；其中H1 preparer focused：23/23；
- CLI `ProgramRecapExecutionCommandTests`：13/13；
- CLI `ProgramDerivedRecapOnlineTurnTests`：11/11；
- CLI `ProgramRecapPlannerConfigCommandTests`：5/5；
- SessionJournal Prepared recovery integration：1/1；CLI与SessionJournal test projects build均为
  0 warning / 0 error；
- 两轮独立kernel与CLI/docs review尾复核均无blocker或medium finding。

### H2：Concrete runtime laziness与真实验收（Done，2026-08-01）

范围：

- 按exact frozen binding延迟创建Maintainer/logger；
- 提供Maintainers-neutral、具有thread-safe once-only activation的deferred registry，并让CLI
  online/run复用；
- 确保NewPlanning最终为`NoBuild`时不创建concrete Maintainer或maintenance logger；operator
  `recap run`不创建call-log目录，online turn只保留必要的agent log；
- 对目标legacy export执行deterministic real-repo acceptance；
- 再次审阅CLI的thin concrete/report adapter是否值得独立assembly；在第二个Host真正出现重复前不提前
  抽象。

Gate：

- 现有CLI focused suite；
- real export deterministic acceptance；
- NoBuild零maintenance factory；operator run零call-log目录、online零maintenance log；
- 再次审阅是否形成值得独立assembly的共享concrete adapter。

H0已经完成pure resolver adoption，H1已经完成public preparation/lifecycle adoption与readiness
duplicate删除，因此H2只关闭concrete runtime laziness和真实验收。CLI-only prompt fingerprint
enrichment继续保留在Host adapter，不进入neutral public contract；现有
`RecapOperationReadiness`只是concrete capability projection与report enrichment，不再拥有
correctness-sensitive preparation决策。

完成结果：

- Planner提供public `DeferredRecapBlockMaintainerRegistry`，使用whole-registry
  `Lazy<T>(ExecutionAndPublication)`；construction zero-touch，成功、异常或null结果均只激活一次；
- CLI `recap run`删除private duplicate并复用public helper；`run-online-turn`从eager concrete
  composition改为第一次真实Maintainer binding lookup才创建完整capability registry与maintenance
  loggers；
- `NoBuild` operator gate继续证明Completion factory调用0次且call-log目录不存在；online gate证明
  provider只收到agent call，唯一call log的context是`run-online-turn/agent`，不存在maintenance
  log；
- whole-registry activation保留同一个capability/connection/logging snapshot；没有为单一profile
  引入第二层per-binding factory、activation API或retry/reset状态机；
- 对目标`cyber-copy-upgraded/chat-session-legacy-upgrade-export.json`执行现有external
  deterministic scripted gate：source 1,281,881 bytes，SHA-256
  `b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`，fresh import
  148 events，HistoryLoad growth/absorbed/recent为116,458/98,082/18,376，2 blocks / 4 route
  endpoints，最终156 events且原148-event prefix保持；
- 此处deterministic指scripted流程与semantic assertions稳定，不承诺raw container或整份report跨
  fresh import bit-identical；
- 当前real gate先执行default config init，因此acceptance report中的`DefaultComposition`与repo
  snapshot等价；未来增加custom-config acceptance时，report必须改为记录当次实际resolved repo
  snapshot，不能继续借用code default；
- 再审阅后不提取`SessionJournal.DerivedRecap.Hosting`：在Galatea形成第二份稳定concrete adapter
  之前，CLI-only profile projection、prompt fingerprint与log context继续留在CLI composition root。

完成时验证（2026-08-01）：

- deferred registry focused：5/5；Planner suite：210/210；
- CLI `ProgramRecapExecutionCommandTests`：13/13；
- CLI `ProgramDerivedRecapOnlineTurnTests`：11/11；
- CLI project build：0 warning / 0 error；
- 目标Galatea export external real-data gate：1/1，content-free acceptance report写入
  `gitignore/session-journal/derived-recap-h2-acceptance-20260801.json`（不进git）；
- 独立code review无finding；docs/acceptance review提出的activation thread-safety措辞已尾修，复核后
  无剩余medium以上问题。

### G0A：Recovery与desired setup

范围：

- public recovery runtime requirements与completion target identity/binding；
- dynamic RuntimeConfigSetup reconciliation；
- system prompt sync规则；
- 明确unprovisioned account结果；首版选择operator显式provision，不让Galatea在第一次Send时
  auto-create raw/config/Store；

非目标：

- 切换production session path；
- 网络LLM验收。

Gate：

- Prepared/Started recovery和connection switch focused tests；
- desired runtime/system setup只在new-request stable boundary同步；
- recovery期间零current-config mutation；
- 空目录/半provisioned repo启动返回明确unavailable，不留下新的半初始化repo。

### G0B：Completed-turn projection与rewind

范围：

- shared raw-core completed-turn locator；
- `ReadRecentCompletedTurns`之类的窄public projector；它在exact captured head上复用raw
  fold/recovery语义，首版允许O(history)，但不能引用不存在的`ReplayHistory()` public API；
- failed-turn abandon与latest-completed-turn exact-terminal rewind；
- projector聚合完整tool loop，只显示terminal no-tool Action；ImportedAgentAction合法；
- text/reasoning保持结构化，Galatea的user wrapper/display normalization仍留在Host。

Gate：

- imported 71 completed turns在Galatea display normalization后逐项parity；
- failed/stop Observation不会进入下一request；
- exact terminal Undo回填正确；
- TurnFailed、setup-only suffix和非exact head不误删更早completed turn；
- 无Host级raw payload decoder。

### G0C：Galatea Host harness与preprocessor parity

范围：

- 建立`Galatea.Server.Tests`或等价的真实Host测试入口；
- 保留现有input normalizer作为Galatea application preprocessor；
- normalizer只在Recap readiness成功后运行；
- normalization失败回退原文，成功时最终normalized text进入raw Observation；
- 实现双阶段stop controller：
  - normalization与DerivedRecap lifecycle期间持有per-turn pre-dispatch CTS；
  - 一个Host wrapper在lifecycle/maintenance成功返回时原子切换为observer-only；
  - 切换后用户stop不再cancel传给`SendAsync`的token，避免在Prepared/Started边界制造新的uncertain
    tail。

Gate：

- normalizer success/fallback/stop；
- blocked Maintainer收到stop cancellation，零Observation/Prepared/agent call，partial Building可在
  下一请求Building-first Resume；
- lifecycle成功后的stop只设置observer，不cancel pre-dispatch CTS；
- config/Store invalid时normalizer调用零次；
- SSE subscribe/stop不占per-session writer lock。

### G1：Galatea SessionHost vertical

用`SessionJournalEngine`替换`UserSessionHost`中的`ChatSessionEngine`：

- startup只打开raw repo和inspect phase；
- new request执行phase gate → setup reconciliation → recap preparer → input normalization →
  runtime binding → `SendAsync`；
- recovery先读取runtime requirements，再执行exact connection binding → `ResumeAsync`；
- `DerivedRecapOnlineLifecycleCoordinator`同时提供candidate source/lifecycle；
- current in-memory `GalateaLiveTurn`继续只管理SSE subscriber与observer；
- 删除`CompactAsync`、EstimatedTokens trigger及旧compaction prompts/config。

首个slice继续使用empty ToolRegistry；tool-capable Galatea是后续独立vertical。

同一个per-session `TurnLock`必须覆盖：

```text
phase inspection
  -> failed-turn abandon / setup reconciliation
  -> recap preparation
  -> UseRuntime
  -> SendAsync / ResumeAsync
  -> resulting raw mutation
```

Undo也获取同一锁；SSE subscribe与stop signal不占写锁。

进程重启后的durable turn使用独立恢复表面：

- current-turn query返回durable `recoveryRequired` phase，而不只看in-memory `GalateaLiveTurn`；
- 新message endpoint在非Idle/TurnFailed时返回`409 recovery-required`，原message不消费；
- 独立resume endpoint不接受新message，并创建可订阅的live recovery turn；
- Prepared可safe resume；
- Started默认只报告uncertain/refused，显式restart授权后才调用`ResumeAsync`；
- AwaitingAgentAction/ObservationAccepted通过resume endpoint继续原durable Observation；
- initial empty-tool vertical对tool phase显式unsupported。

Gate：

- fresh small repo scripted completion；
- Idle send、known TurnFailed abandon + resend、Prepared reopen safe resume、Started reopen默认
  Refuse与显式restart；
- 新message不会被recovery静默替换，resume endpoint可重新建立SSE；
- stream deltas与stop不持久化partial action；
- config/Store missing在Observation和LLM前阻断；
- config/Store invalid时normalizer、maintainer和agent factory调用均为0；
- normalized text而非原始text进入raw history；
- recent UI与Undo行为通过。

### G2A：Repeatable staging acceptance

禁止原地覆盖当前ChatSession repo。使用新的sibling staging path：

```text
legacy ChatSession repo + export        (保留只读)
             |
             v
fresh sibling SessionJournal repo
  -> validate
  -> planner-config init/inspect
  -> recap create
  -> recap run using dsv4p
  -> validate Published blocks/context
  -> Galatea Host acceptance clone
```

provisioning必须使用production `import-legacy-json`，不在测试/Host中复制importer。

确定性gate：

- source export SHA-256与校准记录一致；
- import report为71 Observation + 71 Action、2 compaction + 2 recap skipped、零warning；
- raw validate为Idle；
- planner config hash和R/B为repo实际snapshot；
- 首次run产生world-understanding与autobiographical blocks；
- immediate second run为NoBuild；
- exact materialization保留recent suffix且两个contribution可用；
- 原export、旧repo文件hash不变。

Host gate：

- Galatea打开staging repo展示最近6个raw turns；
- 用scripted client完成一轮并reopen；
- 在Prepared failpoint重启后safe resume；Started failpoint默认Refuse、显式授权后restart；
- Undo新完成的一轮后UI回填正确；
- connection切换只影响新turn。

real-provider smoke：

- operator显式选择`dsv4p`；
- maintenance与agent call log放在repo外；
- 只做有界次数的recap和canary turn；
- provider失败不触发reset、reimport或dual-write。

会写raw event的scripted/real canary只运行在acceptance clone；该clone通过后丢弃，绝不直接切成
production repo。

### G2B：Quiesced exact-head activation

1. Galatea进入maintenance mode或停止旧Server，等待active turn和post-compaction结束并阻止
   新turn；
2. 捕获legacy exact branch/head，重新生成final export并证明对应此head；
3. 若export bytes改变，更新SHA-256、calibration和预期import facts；
4. 从final export重新构建一个从未运行agent canary的fresh activation repo；
5. 完成raw validate、config init/inspect、Store create、Recap run/materialize；这些步骤不得写raw
   turn；
6. 使用隔离config/port完成read-only Host open与recent projection；
7. 在停服状态原子切换新binary + `sessionDir`，以maintenance/admin-only模式启动新Host；
8. 完成read-only检查；若选择production canary，也必须在maintenance内执行并明确记录已越过
   fix-forward boundary；
9. 检查或canary通过后才解除maintenance，禁止真实用户抢先成为首个raw writer。

rollback boundary：

- 首个新SessionJournal raw write之前，可无损恢复旧binary/config/repo；
- 首个新raw write之后，没有reverse importer或dual write，切回旧ChatSession会丢失新turn；此后
  只能fix-forward，除非operator明确接受数据丢失；
- 旧repo与final export继续只读保留，用于审计/灾难恢复，不成为新程序fallback。

activation gate：

- governing runtime/system setup与Galatea实际desired config一致；
- `GET current`与recent projection正常；
- 若执行deliberate production canary，它必须发生在maintenance内；一旦写入raw即记录进入
  fix-forward boundary；
- staging/activation期间旧repo和final export hash不变。

### G3：Post-response recap warm-up（可选）

初始正确路径允许下一次request在preparation阶段执行maintenance。若真实体验证明这段延迟明显，
再复用同一Planner authority设计post-response best-effort warm-up：

- response已经durable并发送后运行；
- 仍持有per-session single-writer gate；
- 失败只留下可Resume Building，不改变已完成turn；
- 不与下一turn并发；
- 不引入background scheduler或第二套cadence。

没有延迟证据时不实施G3。

## 4. 再审阅问题

完成本文与Host Integration短设计后，独立review应重点回答：

1. 第二个Host是否已经使薄`DerivedRecap.Hosting` assembly值得创建，还是public Planner kernel +
   两个thin adapters仍更简单？
2. phase gate与preparer之间是否仍存在能让Prepared/Started提前构造Recap资源的API陷阱？
3. Galatea recovery能否从durable completion target exact选择connection？
4. recent-turn projection与Undo是否复用了raw reducer，而没有形成第二套状态机？
5. system prompt sync是否可能插入active recovery tail？
6. G2A acceptance clone与G2B fresh activation repo是否分离，且首个新raw write后的fix-forward
   boundary是否明确？
7. post-response warm-up是否应继续defer？

## 5. 完成定义

- CLI和Galatea都通过public resolver/preparer，不复制authority-sensitive逻辑；
- Galatea不再引用`ChatSession`或旧compaction配置；
- 指定legacy export导入的SessionJournal repo完成deterministic + real-provider bounded acceptance；
- streaming、stop、connection切换、reopen recovery、recent UI与Undo均有证据；
- raw、config、Recap Store、connections各自保持单一authority；
- 没有compatibility adapter、dual read/write、auto reset或hidden fallback。

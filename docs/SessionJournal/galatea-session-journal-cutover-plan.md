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
`NoRuntimeRequired / NewRequestRequired / FrozenCompletionRequired / ToolContinuationRequired`；
后两种只携带当前phase恢复所需的non-secret dispatch identity与uncertainty/start state，不返回
request正文。第四种是G0A审阅后的修正：pending tool只有frozen tool runtime，没有frozen
completion target，不应硬塞进前三种shape。

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
- exact stable Idle上比较governing ModelId/CompletionSurfaceId；
- 发生变化时显式append `RuntimeConfigSetup`，保留schema和DerivedContext ordinal；
- 再从新的raw head执行Recap preparation；
- TurnFailed必须先由G0B exact abandon；Prepared/Started/tool-active tail绝不append setup；
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
- 只在exact Idle boundary执行desired setup reconciliation；TurnFailed必须先abandon；
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
- Recap的Observation写入前lifecycle期间：可取消；未完成Building保持可Resume，不开始agent
  dispatch；
- 首次pre-append lifecycle成功返回后，Host原子地从pre-dispatch CTS切换为observer-only stop；
  `SendAsync`在Observation写入后还会执行第二次lifecycle，该阶段也必须保持observer-only，否则用户
  cancellation可能留下`AwaitingAgentAction` Observation，而当前exact abandon只接受
  `TurnFailed`；
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

### G0A：Recovery与desired setup（Done，2026-08-01）

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

完成结果：

- SessionJournal新增public `InspectRuntimeRecoveryRequirements()`四分支sealed union；Prepared/
  Started直接投影tail resolver已验证的sanitized manifest identity，不重构或暴露request；pending
  tool使用独立`ToolContinuationRequired`，不伪造frozen completion target；
- Completion新增neutral `CompletionDispatchIdentity`、稳定fingerprint factory及registry
  `BindExact`；durable connection missing/drift不fallback default，connection metadata mismatch在client
  factory前返回typed unavailable；factory自身construction fault继续作为operational exception传播；
- desired setup由raw core在exact Idle head统一reconcile，完整保留Schema/DerivedContext，按runtime再
  prompt幂等append；TurnFailed必须先由G0B abandon，active/recovery phase零setup mutation；
- 两段setup append使用第一段返回的exact committed address作为第二段CAS parent；并发Observation
  只能得到Retryable，不能把prompt插入active turn；runtime成功而prompt失败时下次只补prompt；
- public captured-head-bound `SendAsync`/`ResumeAsync`阻止Host把已组合runtime用于后来tail；CLI还
  验证Recap preparer authority的captured head与setup/recovery head完全一致；
- CLI已迁为reference Host：Idle先reconcile再Recap，AwaitingAgentAction禁止setup并校验selected
  model/surface，Prepared/Started exact-bind durable target，Started Refuse早于connections file读取和
  client创建；
- Building-first Store selection先执行read-only availability检查；valid raw但Store missing，以及
  absent/empty-shell repo都不会创建derived scaffolding、lock、config、raw event或call log；operator
  provisioning仍是唯一create/init路径；
- connection switch setup作为operator intent先落raw；后续planner config/Store readiness失败时仍
  保留该setup，但Observation/client/provider/log均为零。

完成时验证（2026-08-01）：

- Completion suite：183/183；SessionJournal suite：333/333；DerivedRecap Store suite：107/107；
  CLI suite：73 passed / 1 external acceptance skipped；
- G0A recovery + desired setup focused：12/12；CLI online focused：20/20；Store current-lineage
  Building focused：8/8；CLI/SessionJournal build为0 warning / 0 error；
- 独立审阅发现并关闭一项setup两段append并发lineage blocker、Store missing副作用和captured-head
  authority两项Host缺口；文档phase matrix尾修后无已知剩余blocker。

后续调整：G0B范围不变；G1必须按四种runtime requirement分派，并持续使用captured-head-bound
online入口。首个empty-tool vertical仍显式拒绝`ToolContinuationRequired`，不提前扩张tool Host。

### G0B：Completed-turn projection与rewind

范围：

- shared raw-core completed-turn locator；
- `ReadRecentCompletedTurns`之类的窄public projector；它在exact captured head上复用raw
  fold/recovery语义，首版允许O(history)，但不能引用不存在的`ReplayHistory()` public API；
- failed-turn abandon与latest-completed-turn exact-terminal rewind；
- projector聚合完整tool loop，只显示terminal no-tool Action；ImportedAgentAction合法；
- text/reasoning保持结构化，Galatea的user wrapper/display normalization仍留在Host。

Gate：

- imported 71 raw completed turns在core逐项parity；Galatea display normalization逐项parity移入
  G0C；
- exact TurnFailed abandon后，failed Observation不在current lineage/history projection；observer
  stop接线与下一request验证移入G0C/G1；
- exact terminal rewind返回可供Host回填的raw retracted projection；endpoint/UI回填移入G1；
- TurnFailed、setup-only suffix和非exact head不误删更早completed turn；
- 无Host级raw payload decoder。

完成结果（2026-08-01）：

- raw core新增`ReadRecentCompletedTurns` / `ReadRecentCompletedTurnsAt`，返回newest-first raw
  Observation + structured terminal `ActionMessage`；Host wrapper stripping、inline-think与reasoning
  display policy没有下沉；
- shared locator先以`SessionExecutionTailResolver`验证exact captured tail；除active tool外均
  forward-fold到captured head，只有`AwaitingToolExecution` cut到current tool Action predecessor，
  再由tail resolver覆盖Action与active tool suffix；因此既不放宽Recap reducer的
  dependency-closed规则，也不让bounded tail recovery替代完整prefix validation；
- planning unit reducer显式区分普通`ObservationMessage`与同属observation role的
  `ToolResultsMessage`，完整tool loop只关闭为一个visible turn，Imported Action遵守同一terminal
  invariant；
- `AbandonFailedTurn`与`RewindLatestCompletedTurn`共用`Moved / Unavailable / Retryable`
  exact-head result union，不引入第二套reason taxonomy或通用ref mutation API；前者只接受exact
  TurnFailed，后者只接受current head本身为terminal Action；
- 成功CAS移动branch ref到本turn Observation predecessor，保留raw bytes；head-bound governing
  setup/projection cache失效，DerivedRecap sidecar不删除、不重编号，off-lineage selection由Store
  既有membership规则处理；
- terminal Action具有权威性：即使其display text为空，也不得回退展示中间tool-call Action。

完成时验证（2026-08-01）：

- G0B focused：11/11，覆盖exact historical/read-only projection、active tool cut、multi-call +
  multi-round tool loop、empty terminal authority、tool-continuation failure abandon、setup suffix、
  stale head、rewind/abandon CAS race及malformed no-SessionCreated active tool fail-fast；
- SessionJournal suite：344/344，其中G0B focused为11/11；CLI build为0 warning / 0 error；
- 目标Galatea export external gate：1/1；production importer fresh copy后逐项验证71个raw
  Observation/terminal Action newest-first parity，并继续通过既有Recap/Restore/online/recovery flow；
- DerivedRecap off-lineage语义由既有
  `DerivedRecapStoreTests.RewindMakesPublishedAnchorInvisible` focused gate复核为1/1；
- 独立审阅发现并关闭nullable terminal半状态、`CurrentHead`命名、locator mismatch降级、
  no-terminal prefix validation gap与CAS mismatch cache invalidation；最终contract与docs/acceptance
  独立复核无新增blocker。

后续调整：

- 真实71-turn export只覆盖wrapper、纯text、数量与顺序；structured reasoning、tool loop、failed/
  stop、setup suffix与CAS race继续由synthetic focused tests承担，不把真实fixture误当成全覆盖；
- G0B real gate顺带关闭一个G0A后陈旧断言：scripted connection的desired model/surface与legacy
  governing setup不同时，首个online turn合法地先追加`RuntimeConfigSetup`；acceptance不能继续假设
  appended suffix直接从Observation开始；
- G0C明确承接Galatea raw projection adapter和display normalization，并删除recent UI中的recap
  card/boundary语义；DerivedRecap只参与provider context，不作为conversation turn；
- G1 Undo成功直接使用core返回的raw retracted projection回填，不再pre-read/match后构造empty
  assistant fallback；Undo enablement不能只由“存在visible turn”推断，setup suffix等typed
  Unavailable必须映射为明确不可撤销状态；
- known failure/observer stop在同一writer lock内只有exact abandon成功后才能报告idle；Started
  uncertain仍Refuse。G0B范围不扩张到Host wiring，该部分仍由G0C/G1实现。

### G0C：Galatea Host harness与preprocessor parity（Done，2026-08-01）

范围：

- 建立`Galatea.Server.Tests`或等价的真实Host测试入口；
- 保留现有input normalizer作为Galatea application preprocessor；
- 为G1固定调用边界：只有Recap readiness成功后才运行normalizer；G0C不提前创建concrete
  Planner/Store composition；
- normalization失败回退原文，成功时最终normalized text进入raw Observation；
- 实现双阶段stop controller：
  - normalization与pre-append DerivedRecap lifecycle期间持有per-turn pre-dispatch CTS；
  - 一个Host wrapper在首次pre-append lifecycle/maintenance成功返回时原子切换为observer-only；
  - 切换后用户stop不再cancel传给`SendAsync`的token，避免在Prepared/Started边界制造新的uncertain
    tail。

Gate：

- normalizer success/fallback/stop；
- stop先于fresh-send lifecycle transition时取消pre-dispatch token；transition成功后的stop只设置
  observer，不cancel该token；第二次post-append lifecycle保持observer-only；
- SSE subscribe/stop不占per-session writer lock；
- 建立raw-only recent display adapter：只消费`SessionCompletedTurnProjection`，验证wrapper
  normalization、ordered text/reasoning blocks与terminal-authoritative空输出；
- 删除目标recent DTO/UI中的recap card、recap boundary injection与recap-aware Undo判断；
  adapter不得读取DerivedRecap Store或把recap contribution投影成conversation turn。

设计收口：

- G0C不创建一套假的Planner/Store orchestration来模拟未来Host；只交付能被G1直接复用的application
  preprocessor、stop controller、lifecycle decorator、raw display adapter与真实`Program`测试入口；
- `SessionJournalEngine.SendAsync`具有两次lifecycle callback：第一次发生在raw Observation append前，
  第二次发生在Observation后、Prepared前。因此stop transition严格线性化在第一次
  `PendingObservation != null`且结果为`Ready/RawHistoryReady`的callback返回之后；第二次callback
  继续使用同一个未被用户stop取消的token；
- stop先于transition线性化时取消pre-dispatch token且transition抛`OperationCanceledException`；
  transition先于stop线性化时只设置同一turn的observer；application shutdown仍可独立取消linked
  token；
- concrete `config/Store invalid → normalizer zero`、blocked Maintainer留下partial Building并在下一
  request Building-first Resume，以及known stop后的exact abandon，只能在G1真正接入Planner/Store/
  `SessionJournalEngine`后做endpoint级验收；G0C只完成组件边界和测试入口，不把旧`ChatSession`
  Host伪装成已切换。

完成结果：

- 新增`Galatea.Server.Tests`并加入solution；真实`WebApplicationFactory<Program>`使用临时有效
  `config.json`/`connections.json`、authentication与严格DI replacement，确保测试不会触发环境中的
  real provider或normalizer；
- `GalateaInputPreprocessor`收拢现有normalizer policy：skip保持原文，异常或blank输出回退原文，
  caller cancellation不被吞掉，既有normalization SSE phase保持；当前legacy Host也改用同一组件和
  per-turn linked pre-dispatch token；
- `GalateaTurnStopController`以`PreDispatch / ObserverOnly / Completed`三阶段、同一lock线性化stop与
  transition；observer在turn创建时即固定，stop-before-background不会丢失；subscriber/replay gate与
  stop gate保持独立；
- `GalateaFreshSendLifecycleGate`只装饰真实`ISessionContextLifecycleCoordinator`，仅首次成功的
  pre-append callback执行transition，不复制Planner result taxonomy或maintenance算法；
- `CompletionStreamObserver.ShouldStop`改为thread-safe、monotonic flag，provider loop与stop endpoint
  之间具有正式的跨线程可见性，写`false`不能复位已发出的stop；
- `GalateaRecentTurnDisplayAdapter`的唯一业务输入是`SessionCompletedTurnProjection`：exact wrapper
  stripping留在Host，Text与Reasoning各自在原block顺序内聚合，inline-think跨Text block统一清理，
  terminal空输出仍生成空assistant DTO；adapter不读取Store、raw payload或中间tool Action；
- recent wire删除`IsRecap`与可由`ReasoningText`直接派生的`HasReasoning`；JS/CSS删除recap card、
  boundary injection与recap-aware Undo分支。G1前legacy projector遇`RecapMessage`只作为边界并跳过，
  `maxTurns`恢复严格上限；Undo的server-authoritative exact eligibility仍留给G1。
- start endpoint用显式writer-lock ownership transfer把锁交给background runner；handoff前异常会清理
  live turn并释放锁，legacy pop endpoint也用`finally`释放，避免一次composition/rewind异常把session
  永久留在busy状态。

完成时验证（2026-08-01）：

- `Galatea.Server.Tests`覆盖真实Host/auth/DI、preprocessor success/fallback/cancellation、stop race与
  lifecycle status matrix、raw display adapter/legacy recap隐藏，以及SSE subscribe/stop不等待writer
  lock；
- 两条真实legacy Host vertical分别证明normalized input进入provider request与durable Observation，
  以及normalization期间stop会取消pre-dispatch token、保持history为空并让completion dispatch为0；
- raw adapter focused覆盖exact/partial wrapper、ordered Text/Reasoning、跨block inline-think、
  reasoning-only与terminal-authoritative empty output；legacy compaction fixture证明recent response不再
  产生recap DTO且严格遵守maximum；
- `CompletionStreamObserver`的monotonic stop由focused test覆盖；Galatea与Completion.Abstractions
  build均为0 warning / 0 error；`Galatea.Server.Tests`为38/38，Completion suite为183/183；
- G1必须在同一个真实Host harness中重跑三项final gate：invalid config/Store的zero-call ordering、
  blocked Maintainer stop + next-request FrozenBuilding Resume，以及observer stop → TurnFailed → exact
  abandon → idle。

### G1：Galatea SessionHost vertical（Done，2026-08-01）

用`SessionJournalEngine`替换`UserSessionHost`中的`ChatSessionEngine`：

- startup只打开raw repo和inspect phase；
- new request执行phase gate → setup reconciliation → recap preparer → input normalization →
  runtime binding → `SendAsync`；
- recovery按public runtime requirement variant分派：`NewRequestRequired`选择current connection并
  校验governing model/surface后执行Recap preparation；`FrozenCompletionRequired` exact-bind durable
  completion identity；`ToolContinuationRequired`只exact-bind durable tool runtime（首个empty-tool
  slice显式unsupported）；随后使用captured-head-bound `ResumeAsync`；
- `DerivedRecapOnlineLifecycleCoordinator`同时提供candidate source/lifecycle；runtime的lifecycle用
  G0C `GalateaFreshSendLifecycleGate`装饰，candidate source仍是同一个coordinator实例；该decorator
  只用于fresh `SendAsync`，recovery按下述runtime requirement使用独立transition point；
- stop transition按operation mode区分：fresh Send在pre-append lifecycle成功后切换；已有
  `AwaitingAgentAction` Observation的recovery在其唯一post-observation lifecycle成功后切换；
  Prepared与显式Started restart在完成frozen runtime exact binding、即将进入provider attempt前切换；
  每种模式都必须分别覆盖stop-before/after transition；
- current in-memory `GalateaLiveTurn`继续只管理SSE subscriber、per-turn stop controller与observer；
- 删除`CompactAsync`、EstimatedTokens trigger及旧compaction prompts/config。
- recent endpoint直接消费core completed-turn snapshot并保持newest-first；Undo endpoint直接消费
  `SessionTurnRetractionResult.Moved.Turn`做wrapper normalization/回填，不保留旧
  pre-read/match/fallback路径；
- UI的`canRewindLatest`由exact boundary eligibility或typed endpoint结果驱动；存在visible turn不代表
  setup-only suffix后仍可Undo。

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
- blocked pre-append Maintainer收到stop cancellation，零Observation/Prepared/agent call；留下的partial
  Building在下一request由preparer识别为`FrozenBuilding`并只恢复缺失suffix；
- 首次pre-append lifecycle成功后，stop只设置observer且不取消传给`SendAsync`的token；known
  `TurnFailed`随后exact abandon并回到idle；
- normalized text而非原始text进入raw history；
- recent UI与Undo行为通过。

完成结果：

- `UserSessionHost`已经只持有`SessionJournalEngine`；Galatea project删除`ChatSession`引用、旧
  `CompactAsync`/EstimatedTokens触发器与三项compaction配置。startup只打开operator预先provision的
  raw repo并检查phase，不创建repo、Store、Planner config或Completion client；
- fresh endpoint在创建live turn前按public runtime requirement做phase gate；active durable tail返回
  `409 recovery-required`且不消费新message，TurnFailed仅在background writer内exact abandon成功后
  才继续。fresh runner严格串联captured Idle head → desired setup committed head → preparer authority
  head，任一不一致都在normalizer/client/Observation之前停止；
- Galatea新增thin concrete Recap composition root：复用public Building-first preparer、repo active-config
  source与authority-only lifecycle factory；完整built-in capability catalog投影留在Host，concrete
  Maintainer registry通过`DeferredRecapBlockMaintainerRegistry`首次真实binding时才创建，没有复制
  Planner/Store authority算法，也未抽取新的Hosting assembly；
- fresh Send与AwaitingAgentAction recovery分别使用`GalateaFreshSendLifecycleGate`和
  `GalateaRecoveryLifecycleGate`；前者在pre-Observation lifecycle成功后切到observer-only，后者在
  existing Observation的唯一lifecycle成功后切换。Prepared/Started不读Store/config/normalizer，完成
  frozen runtime exact binding后、`ResumeAsync`前切换；
- 独立`POST /api/chat/turns/resume`使用current DTO给出的canonical exact head授权恢复；
  AwaitingAgentAction校验selected connection的governing model/surface，Prepared exact-bind durable
  completion identity，Started默认在client创建前拒绝，只有同一exact head上的显式restart才使用
  `RestartWithNewAttempt`。frozen non-empty tool shape与ToolContinuation继续typed unsupported；
- known Completion non-success先形成TurnFailed，再调用`AbandonFailedTurn(exactHead)`；只有
  `Moved`后才向UI承诺本轮未进入active history，race/unavailable不再沿用旧成功文案；shutdown或
  Started uncertain不自动abandon；
- recent直接投影`ReadRecentCompletedTurns`。可撤销性不使用冗余bool，而是仅在captured head就是最新
  terminal Action时返回opaque `rewindLatestToken`；Undo回传该token执行exact CAS，成功DTO直接消费
  `Moved.Turn`并附带move后authoritative recent snapshot，删除pre-read/match/empty-assistant fallback与
  JS本地`slice(1)`；
- 测试Host现在显式provision raw repo、DerivedRecap Store与repo-owned planner config；legacy
  ChatSession projector/compaction fixtures已删除，raw tool-loop/fold语义继续由SessionJournal core
  suite负责。

完成时验证（2026-08-01）：

- `Galatea.Server.Tests` 47/47，Galatea build 0 warning / 0 error，JS `node --check`通过；
- durable recovery vertical 5/5：active Observation阻止fresh message且normalizer/client/provider为0，
  NewRequest恢复不重复normalization，Prepared在删除active config后仍exact恢复，Started默认拒绝且
  client factory为0，同一exact head上的显式restart可完成且仍不读取active config；
- recent/Undo真实Host vertical 4/4：newest-first exact token、Moved DTO、关闭writable Host后的
  reopen/off-lineage parity、stale token零mutation、writer busy快速409；
- readiness/failure vertical 2/2：planner config missing在normalizer/client/Observation前阻断，known
  provider non-success exact abandon回Idle；既有preprocessor、stop-controller、SSE lock topology与
  lifecycle component gates继续通过；
- 独立审阅发现并关闭三项blocker：failed-turn abandon失败时错误承诺、setup committed head未与Recap
  authority对齐、Empty/active phase在endpoint前置gate中被误当Idle；最终复核未引入第二套recovery或
  planning state machine。

G1后的调整：

- G2A继续负责目标legacy export的fresh staging import、真实built-in Maintainer/partial Building stop与
  `dsv4p`有界provider smoke；这些是real-data/real-provider acceptance，不把测试Host扩成第二套
  importer或Maintainer harness；
- G2 Host gate以`rewindLatestToken`而非`Turns.Count > 0`判断Undo，并增加sidecar publish/materialize
  前后recent/token不变、stale token不误撤新turn、Undo回到setup suffix后仍有visible turn但token为
  null的验收；
- G2B activation config删除旧compaction字段。G1完成时，ignored local `config.json`仍指向legacy
  ChatSession目录，这不代表G1 binary已经production activation；后续实际切换由G2B在G2A fresh
  staging通过后按quiesced exact-head步骤完成；
- CLI与Galatea目前只重复少量profile capability投影和completion identity mapping，logging、UI、
  recovery policy不同；第二个Host尚未形成足够稳定的重复，不提取`DerivedRecap.Hosting`；
- post-response warm-up继续defer，tool-capable recovery继续作为后续独立vertical。

### G2A：Repeatable staging acceptance（Done，2026-08-01）

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

G2A external Host direct gate：

- Galatea打开staging repo展示最近6个raw turns；
- 用scripted client完成一轮并reopen；
- Undo新完成的一轮后UI回填正确；
- DerivedRecap exact selection/materialize前后recent snapshot与`rewindLatestToken`不变；stale token不能误撤
  新turn；Undo若回到setup suffix，较早visible turns仍可显示但token必须为null；
- connection切换只影响新turn。

Prepared safe resume、Started默认Refuse/显式restart与durable original-connection binding不在external
suite复制test-only failpoint harness；它们由G1 deterministic Host tests、CLI real-data Prepared recovery
和本轮logging identity/Prepared recovery共同作为carried-forward gate。

real-provider smoke：

- operator显式选择`dsv4p`；
- maintenance与agent call log放在repo外；
- 只做有界次数的recap和canary turn；
- provider失败不触发reset、reimport或dual-write。

会写raw event的scripted/real canary只运行在acceptance clone；该clone通过后丢弃，绝不直接切成
production repo。

完成结果：

- 新增独立
  [G2A staging acceptance runbook](galatea-g2a-staging-acceptance-runbook.md)，把每轮运行拆成
  raw只读的staging与可写raw的disposable acceptance clones；run-root、reports、Completion call logs
  与acceptance config均在session repo外，禁止`--force`、auto reset/reimport和clone promotion；
- production importer新增content-free `--report-json`；同一次authoritative import report同时驱动
  Markdown/JSON。`recap materialize-inspect`提供strict-ordinal、read-only exact materialization证据，
  不读取active config/connections、不创建Store scaffolding、不调用provider；
- Galatea recent默认收口为newest-first 6轮；测试Host新增existing-repo入口但不拥有/初始化输入repo。
  opt-in external Host gate始终复制Published staging到私有clone，真实71-turn staging上4/4通过：exact
  recent 6、materialization对recent/token/raw不可见、fresh → dispose/reopen → exact Undo、setup-only
  suffix token为null，以及A→B connection只影响对应新turn；
- Galatea新增可选top-level `callLogDir`，相对config目录解析且必须与全部`sessionDir`双向non-nested。
  agent写`agent/`，Maintainer按identity写`maintenance/<maintainer-id>/`；wrapper透传client
  `Name`/`ApiSpecId`，Prepared exact binding不因是否启用logging而漂移；
- G2A实际run使用source export `1,281,881` bytes、SHA-256
  `b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`。production import得到
  148 raw events、71 Observation + 71 Action、2 compaction + 2 recap skipped、零warning、Idle；
  repo config hash为`03a5e77e506c210594901375eff86ebbaf992ff532160b471d9a1831edc4d50a`，
  `R=18,000`、`B=21,000`，完整HistoryLoad为116,458；
- 首个真实`dsv4p` run在world-understanding完成后，autobiographical调用命中本地100秒HTTP timeout，
  稳定返回`BlockFailed`并保留Building；同一anchor
  `ej1:000003e66000017f0000000100000000`上的production `recap resume`只补剩余两次调用后
  Published，没有reset/reimport或重做healthy block。总计5次有界provider attempt（其中1次timeout），
  immediate second run为`NoBuild`且0 call；
- exact materialization选中两个canonical contributions：world-understanding 6,807 UTF-8 bytes、
  autobiography 5,470 UTF-8 bytes，二者`AbsorbedThrough`均为admission anchor；recent suffix为19 raw
  events / 19 HistoryUnits。materialize前后raw fingerprint不变，source export与旧repo同轮before/after
  fingerprint不变；
- real Galatea Host canary只绑定`127.0.0.1`并使用acceptance clone：打开时展示6轮，显式`dsv4p`
  完成1次agent call，call log位于clone外；进程reopen后仍为Idle且exact rewind token不变，Undo成功移除
  canary。由于desired runtime setup保留在tail，Undo后6个较早turn仍可见而token为null；strict validate
  为Idle；
- 常规Galatea suite为51 passed / 4 external skipped，external staging gate 4/4，Galatea build 0 warning /
  0 error。Published-recap Prepared跨Host没有复制新的failpoint harness，继续由G1 Prepared exact
  recovery、CLI real-data Prepared recovery与本轮logging exact-binding组合举证。

G2A后的调整：

- G2B不复用、重命名或promote本轮staging/clone；quiesce后必须从与legacy exact head对应的final export
  fresh构建activation repo。真实LLM block payload/envelope hash不作为跨run golden，只固定raw/import、
  config、admission/absorption、block target与Published/NoBuild结构；
- G2B activation config除删除旧compaction字段外，还应显式把`callLogDir`放到session repo外；read-only
  open与materialization分别使用本轮新增的Host existing-repo路径和`recap materialize-inspect`；
- provider timeout是可恢复的Building状态，不是reset信号。G2B maintenance window必须允许有界
  Building-first resume；若最终仍未Published则保持停服并报告失败，不能通过reimport或切换到旧derived
  state绕过；
- G2A已证明real canary、reopen与Undo，G2B默认只需read-only activation检查。若operator仍选择
  production canary，必须继续在maintenance内明确越过fix-forward boundary；
- `DerivedRecap.Hosting`仍不抽取，post-response warm-up与tool-capable recovery继续defer。

### G2B：Quiesced exact-head activation（Done，2026-08-01）

最终activation流程收口为：

1. 停止旧Server并确认旧repo没有active owner；捕获legacy exact branch/head；
2. `export-json --expected-head <H>`在生成前和atomic publish前双重核对head，并证明final
   timeline/event、UTF-8 bytes和SHA-256都对应`H`；
3. 从final export直接在永久sibling path创建一个从未运行agent canary的fresh activation repo；
4. 完成import/validate、repo-owned config、Store、explicit setup-only reconciliation、Recap
   run/resume、`materialize-inspect`与immediate `NoBuild`；provider失败只允许对同一frozen Building做
   有界resume，不reset、reimport或嫁接旧derived state；
5. 使用隔离config和loopback端口以`maintenanceMode: true`启动shadow Host，验证read-only
   current/recent与typed write rejection；
6. 在停服状态保存byte-exact active config和可执行legacy rollback artifact，原子切换active
   `sessionDir`，仍以startup-time maintenance启动新Host并重复read-only gate；
7. 修改`maintenanceMode: false`并重启后才解除quiescence。没有hot reload、admin bypass或第二套
   maintenance状态机；应用内write gate和read-only engine提供defense-in-depth，loopback/ingress与operator
   credential承担admin-only边界。

rollback boundary分为三层：

- fresh import、planner config、DerivedRecap与shadow验证是可重建activation preparation；
- explicit activation `RuntimeConfigSetup` / `SystemPromptSetup`会让candidate raw head产生分叉，但不引入
  用户会话事实；此时恢复旧ChatSession不会丢conversation，后续重建时重新reconcile setup即可；
- 真正的fix-forward边界是首个post-cutover、非activation-setup的authoritative raw/ref mutation。通常是
  `ObservationAccepted`，也包括recovery append、Undo/ref move或failed-turn abandon。production canary一旦
  产生此类mutation便已越界，即使随后Undo也不能抹去物理raw/off-lineage事实；此后切回ChatSession会
  丢失新authority，默认只能fix-forward。

activation gate：

- governing runtime/system setup与Galatea实际desired config一致；
- activation config使用absolute permanent `sessionDir`，不含旧compaction字段，`callLogDir`位于repo外；
- maintenance下`GET /api/me`、current、recent正常，所有chat POST在打开session/client前返回typed 503；
- public restart后`maintenanceMode=false`且current/recent仍对应同一exact Idle head；
- staging/activation期间旧repo、final export和activation raw fingerprint保持不变；
- 默认不做production canary；若operator显式选择，必须在maintenance/受控ingress内执行并记录是否越过
  上述fix-forward边界。

完成结果：

- quiesced legacy `main` head为`seg:1:00000008f866b7af`。新增mandatory
  `export-json --expected-head`与import provenance report，final export仍为1,281,881 bytes、SHA-256
  `b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`，与既有校准export
  byte-identical；旧repo `recent/` + `refs/` before/after fingerprint一致；
- fresh import为148 events、71 Observation + 71 Action、2 compaction + 2 recap skipped、零warning、
  Idle。新增`reconcile-desired-setup`在exact Idle head只追加一条`RuntimeConfigSetup`，没有重复
  `SystemPromptSetup`；activation head成为`ej1:00000497d00000410000000100000000`、149 events、
  Idle，runtime为`deepseek-v4-pro` / `openai-chat/deepseek-v4`，system-prompt hash与history semantic
  commitment不变；
- repo config SHA仍为`03a5e77e506c210594901375eff86ebbaf992ff532160b471d9a1831edc4d50a`，
  estimator为`atelia.history-load.o200k-base.history-unit-v1`，`R=18,000`、`B=21,000`，完整历史为
  142 HistoryUnits / 116,458 HistoryLoad；
- Recap attempts 001-003都保留同一Building anchor
  `ej1:000003e66000017f0000000100000000`，其中3次provider call精确命中默认100秒transport timeout。
  这暴露的是connection operational policy，不是Planner/Store状态问题：`CompletionConnectionConfig`
  新增1..3600秒的optional `requestTimeoutSeconds`，`dsv4p`显式设为300秒；effective值进入call log但不
  进入dispatch fingerprint，timeout覆盖response headers与后续SSE body的完整streaming operation，
  因此纯等待策略变化不会破坏Prepared exact binding；
- attempt 004在同一frozen Building上Published。materialization选择world-understanding 14,994 UTF-8
  bytes与autobiography 18,918 bytes；二者都absorbed through上述anchor，recent suffix为20 raw events /
  19 HistoryUnits。immediate second run为`NoBuild`、0 provider call；Recap前后raw fingerprint一致；
- 最后一个可读取ChatSession的Host commit `c5b45c7f`已生成immutable Release artifact，并在loopback上
  对legacy repo通过login/me/recent read-only验证。shadow maintenance和active-path maintenance均通过
  `me=true`、current Idle、recent 6、chat POST typed 503、zero call-log；随后false重启通过
  `me=false`、current Idle、recent 6；
- active config已指向absolute permanent
  `/repos/focus/atelia/prototypes/Galatea/.atelia/galatea/sessions/cyber-session-journal`，external
  `callLogDir`位于repo外，旧compaction字段已删除。正式Host监听`0.0.0.0:3511`；本轮未执行production
  canary，因此截至activation验收尚未越过用户历史fix-forward边界。

G2B后的调整：

- G3继续defer。首次full-legacy build在offline maintenance window内的transport timeout，不能证明正常
  incremental rolling maintenance会造成不可接受的pre-send延迟；先在试运行中记录threshold turn的
  preparation wall time、Maintainer calls和用户感知，再决定是否实施post-response warm-up；
- `requestTimeoutSeconds=300`是当前`dsv4p` route的operational policy，不是Recap architecture或
  cross-run golden；Hosting抽取、tool-capable recovery与background scheduler仍不随G2B扩张。

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

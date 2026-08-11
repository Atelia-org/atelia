# DerivedRecap Grid WP-06：Completion Runtime、Family 与 Shared Prefix

状态：Complete；product/tests/docs、两路independent closure与final serial validation均GO；current production未切换

只需加载：目标设计、总计划、WP-04 runtime handoff、本文和 WP-07A摘要。

## Intent

把真实Completion适配到`IRecapCellBatchExecutor.ExecuteAsync(FrozenRowBatch)`，保留同row并行、family结构共享与
prefix-cache优化，同时证明
provider/runtime信息不会污染Timeline、Recipe、Cell identity或Store。

## WP-04 complete handoff lock

- Manager拥有`IRecapCellBatchExecutor`、`FrozenRowBatch`、whole-batch call-budget preflight、ordinal settlement和row barrier；WP-06
  只提供provider-neutral executor implementation。
- WP-06负责runtime timeout、route/family lane、leader/follower、provider parsing与started sibling drain；不得直接写
  Cell/RowView/Fulfilled，也不得新增durable attempt/campaign或把provider types带入Manager。
- WP-04已取得两路independent review GO；本节冻结接口责任，WP-06仍按总计划在WP-05之后施工。
- WP-05 complete handoff没有改变Family/Definition/RowBuildSpec/Store wire；其pure-read Getter只消费current active/head/fulfilled artifacts，
  neutral adapter与provider executor保持单向隔离。WP-06不得把Completion/provider types反向带入Getter或用runtime scheduler替代
  SessionJournal-owned raw-tail composition。

## In scope

- immutable FamilyDefinition owning SystemPrompt、OrderedTools、OutputProtocol、InputRenderingProtocol；
- declarative MaintainerDefinition -> bound runtime；
- exact family/lane reference affinity与lazy route resolution；
- shared prefix一次构造、leader/follower scheduling、per-lane cap；
- strict output parser：`Updated | KeepUnchanged`。`Updated`正文必须是strict UTF-8可编码、nonempty，且同时不超过exact
  `MaintainerDefinition.MaxContentUtf8Bytes`与neutral contribution 256KiB上限；不能把replacement fallback、空正文或截断当成功；
- drain后按ordered EvaluationKey返回closed per-item outcomes；单item failure/cancel保留successful siblings，deterministic primary
  只影响operation报告，不丢失结果；
- call budgets、timeouts、caller cancellation、started siblings drain；
- cache hint/usage/call-log telemetry与Cell identity分离；
- dynamic Agent-created topic/spec只能进入family允许的user/data tail。
- row首call前pre-resolve全部pending bindings；结果按EvaluationKey返回，等待remote期间不持SQLite transaction；
- identity只消费WP-02 canonical Family/Maintainer hashers，删除current runtime内第二套Semantic/Capability fingerprint算法；
- `InputRenderingProtocol`与PriorInputProjection的字段、顺序、canonical preimage同源。

## Implemented contract lock

- 唯一新product owner是`SessionJournal.RecapGrid.Runtime`，direct project references严格为
  `RecapGrid.Manager + RecapGrid.Abstractions + Completion.Abstractions`，零NuGet；没有SQLite、provider concrete、Galatea、Getter、
  old DerivedRecap或Manager以外的mutation authority。
- route key固定为`(FamilyDigest, RuntimeProtocolId, SemanticModelId?)`；`null`是exact成员而不是fallback。resolver直到首次真实
  `ExecuteAsync`才调用，结果按exact key缓存；route object reference同时是跨batch host-wide lane affinity与cap owner。
- V1 catalog固定为`tool-runtime-v1 / atelia.recap.output.v1 / atelia.recap.input.v1 /
  atelia.recap.prior.v1 / atelia.history.segment.v1`。任一Family、Definition、terminal tool、frozen row/spec/prior authority不一致都在
  whole-batch preflight拒绝，provider starts为零。
- visible prior只渲染schema marker及有序`logicalColumnId/content`；History仅保留visible observation/action/tool call/tool result，
  `ReasoningBlock`与inline think被剥离。Topic、literal UserPromptTemplate与本work target只进入per-work tail，不把Cell/View/
  Definition/Recipe/digest/outcome metadata暴露给provider。
- terminal output必须是唯一terminal tool call，arguments严格JSON且exact包含一次`outcome/content`；禁止BOM、comment、trailing、
  duplicate、unknown或case变体。`Updated`正文strict UTF-16->UTF-8、nonblank且不超过definition与256KiB双cap；
  `KeepUnchanged`必须`content:null`并存在同column prior。
- scheduler按`(route reference, FamilyDigest)`分组：所有leaders先完成不持lane的pre-admission；每个leader只在紧邻真实
  `InvokeAsync`时发布started decision，settle并释放lane后仍等待本batch全部leaders已started或形成terminal decision，才释放本组
  followers。lane acquire后且即将首次provider call时才启动runtime timeout；每item最多一次call，无warmup/retry。caller cancellation
  只让未started item成为`NotStarted`，started OCE/timeout/provider/parser错误均为stable `Failed`；batch fatal latch一旦观察fatal，
  只drain已started siblings，任何group不再dispatch未started follower。
- runtime是`IDisposable + IAsyncDisposable` operation-drain owner；route/client/lane均lazy，route显式声明invoker为Owned或Borrowed，
  `CompletionClientRecapInvoker`再显式声明underlying client ownership。Owned exact once、Borrowed zero dispose；reentrant Dispose不自锁，
  `DisposeAsync`真实等待entered operation与async-owned cleanup。WP-07B关闭顺序固定为Runtime drain，再由Host registry释放borrowed clients。
  bounded telemetry携带evaluation/family/definition/history/prior、exact route、leader/follower、leader admission wait、dispatch lane wait、
  cache hint、usage与provider settlement；resolver code/detail与provider exception detail均在runtime边界做strict UTF-8/code-owned cap，
  detail上限4KiB。全部仅属operational state，不进入任何Grid canonical identity。
- Completion generic schema同步升级：nested Object/Array/Value nullable投影为standard type union，Gemini exact改写为`nullable:true`；
  nullable root object拒绝。output-contract fingerprint采用条件式版本：没有nullable Object的既有contract继续exact V1 domain/preimage，
  只有递归schema出现nullable Object才使用提交全部Object nullability的V2；call-log schema升级V9，避免旧wire静默解释新语义。

## Reuse guidance

可以复用 current Completion abstractions/provider adapters与已经验证的lane/family算法；不得让 old
`RecapEpoch`/`RecapBlockPlan`/Published types进入新runtime contract。若机械复用需要大量adapter，优先提取provider-neutral
primitive，而不是建立长期legacy bridge。

## Out of scope

- Store schema、Timeline、ControlPlane carrier；
-改变EvaluationKey；
- Galatea production cutover；
- provider-specific payload成为durable Cell authority；
- scheduler batch/lease持久化；
- warm-up-only remote call（首版仍leader real call后followers）。

## Write scope

- new runtime adapter/family owners与tests；
- current generic Completion library仅做必要provider-neutral seam；
- call-log schema若变化必须独立version并更新goldens；
- 不改 old DerivedRecap production composition。

## Validation matrix

1. same family exact shared SystemPrompt/tools/output protocol实例；
2. two same-group cells：1 Leader + followers，prefix bytes/fingerprint exact相同、tail不同；
3. different lane/connection天然并行隔离；
4. leaders admission优先、followers不抢占未seed family；
5. provider output missing/duplicate/wrong tool/unknown fields fail closed；
6. Updated/Keep正确转换Cell success；Updated正文覆盖invalid UTF-16、empty、definition exact cap/cap+1、neutral 256KiB exact cap/cap+1，
   parser outcome与最终`RecapCellArtifact.Create`使用同一正文和限额；
7. call budget在首dispatch前完整preflight；
8. cancellation/timeout/failure选lowest ordinal primary并drain started siblings；
9. NoBuild/missing-free read path零client/lane/logger construction；
10. request/cache/call logs不进入Definition/EvaluationKey/CellDigest；
11. Anthropic/OpenAI/Gemini provider projection focused regressions；
12. optional disposable provider canary不重试并诚实记录environment-blocked。
13. provider-neutral frozen input的ProjectionDigest preimage与actual runtime renderer逐字段同源，无第二套reorder/pack；
14. Agent新column按allow-listed Family/semantic runtime key解析route，不要求config枚举每个LogicalColumnId；exact route缺失拒绝；
15. fake batch与real batch产生相同indexed success/failure contract，Manager不感知leader/follower。

## No-Go

- member覆盖family SystemPrompt/tools/parser；
- Maintainer直接持有ICompletionClient/model/options；
- route fallback或default connection；
- implicit provider retry突破call budget；
-为了cache把runtime group/family ID写入durable identity。

## Done when

- fake runtime与real adapter对同一Manager contract；
- parallel/cache/cancel/error tests green；
- affected Completion builds/tests、docs/diff green；
- reviewer确认WP-07A/07B composition只做route/control wiring。

## Implementation record（complete）

- product：新增`SessionJournal.RecapGrid.Runtime`的route/invoker、V1 protocol validator、renderer、strict parser、leader/follower lane、
  lifetime与telemetry；没有改Manager/Store/Timeline/Control canonical contract，也没有接Galatea/CLI/current production。
- Completion：`ToolSchema.Object.IsNullable`、generic/provider schema projection、conditional fingerprint V1/V2与call-log V9已显式升级；OpenAI Chat、
  OpenAI Responses、Anthropic与Gemini共用nullable regression。
- tests：新增Runtime focused与assembly-external PublicSurface；Walking锁direct graph、zero package/provider concrete/SQLite/legacy symbols、
  exported surface及Runtime.Tests-only IVT。Manager/HistoryTimeline的新增IVT仅授予
  `Atelia.SessionJournal.RecapGrid.Runtime.Tests`；Completion concrete也只给同一tests assembly精确IVT和test-only project reference，
  用于让真实Runtime request穿四个既有provider converter。production Runtime没有friend access且仍只direct reference
  `Completion.Abstractions`。
- final evidence：Runtime final full 56/56（其中最终scheduler/lifetime/parser/diagnostic targeted tail 15/15）、Runtime PublicSurface 2/2、
  Completion 471/471、Walking architecture 18/18均按
  `-m:1 -nr:false --no-restore`串行green；三个Runtime项目已进入`Atelia.sln`，最终独立串行`Atelia.sln` build为
  0 warning / 0 error。closure前曾有一次共享VS BuildHost输出锁重试，该次结果未计入证据；锁竞争消失后的上述final build才是
  solution gate。old built-in output/family fingerprint exact golden分别为
  `sha256:9a5d3eba5bb2fa1e486b48594818f3eb105caa630f6d894c310e00a20a55fd1b`与
  `sha256:beda5eddaaa15886649fc9d6d6bca96c26203b26f7e367c748e26269c781f6b6`；affected old-host focused另有
  Galatea call logging 3/3与Maintainers binding/built-in 6/6。两路independent closure命中的
  fingerprint/per-work protocol/lifetime、actual leader-start barrier、batch fatal latch及P1 findings已统一tail修复；两路independent
  closure最终均为GO（P0=0，P1=0）。WP-07A现为Ready。

## Handoff to WP-07A

候选交付exact host registry key、deferred resolver、provider-neutral invoker、shared route affinity与disposable lifetime。WP-07A只能做
composition/diagnostics wiring：不得引入route fallback、第二scheduler、warmup call或把call-log/cache/usage写入Grid identity。本文已取得
independent closure GO；WP-07A可以按该冻结handoff开工。

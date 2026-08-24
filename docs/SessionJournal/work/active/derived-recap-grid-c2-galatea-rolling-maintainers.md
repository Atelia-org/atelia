# DerivedRecap Grid C2：Galatea rolling maintainers 设计与实施边界

状态：C2A/C2B/C2C source implementation complete；RecapRewriter V3 source hard cut complete；
C2D real-provider canary与本机actual cyber activation complete（2026-08-15）

总体设计：[`derived-recap-grid-target-design.md`](derived-recap-grid-target-design.md)  
Cadence/capacity/activation 总审计：[`derived-recap-grid-cadence-capacity-and-activation-audit.md`](derived-recap-grid-cadence-capacity-and-activation-audit.md)

## 1. 目标与已锁定裁决

C2让Galatea首次真正使用RecapGrid维护两个rolling rewrite context blocks：

| ordered column | logical column | target | 职责 |
|---:|---|---|---|
| 1 | `world-understanding` | `Observation / roleplay.world-understanding` | 人物、环境、项目、事实、推断、矛盾与known unknowns的当前工作理解 |
| 2 | `autobiography` | `Action / roleplay.first-person-autobiography` | 重要经历、关系、感受、承诺、犹豫，以及Galatea当前的内在状态 |

以下需求已经锁定，不再作为实施期开放问题：

1. 两列属于一个shared Family，共用
   [`docs/Galatea/prompt/recap-maintainer-family/system-zh-cn.md`](../../../Galatea/prompt/recap-maintainer-family/system-zh-cn.md)
   的Family system prompt；专业差异分别来自现有autobiographical与world-understanding zh-CN user prompt。
2. 首版两列都使用rolling full rewrite：输入上一row的完整selected recap projection与当前History segment，只输出完整新block，
   绝不输出差量或控制envelope；Runtime以正文与同列prior的ordinal等价性推导`Updated / KeepUnchanged`。
3. 实际provider/model是运行时route policy。默认由静态配置文件选择，但以后允许换成逐调用策略；Opus 4.6只是首次部署默认，
   **不属于durable semantic identity**。
4. 两个Definition的`MaintainerCapabilitySpec.SemanticModelId`必须为显式`null`。model、connection、lane、cache、usage和
   dispatch timing都不能进入Family、Definition、Recipe、EvaluationKey、Cell或RowView identity。
5. 初始recipe使用Full，不用Overlay伪造既有compatible base；两个columns的canonical顺序固定为
   `world-understanding`、`autobiography`。
6. normal runtime不得自动创建Family/Definition/Recipe、修改Control admission或选择active recipe。所有genesis和promotion都由
   provider-free operator命令在exact SessionJournal Ref/Timeline/Control authority下显式完成。

首版采用Opus 4.6只是部署选择。切换模型不会使既有Cells自动stale，也不会静默重算过去；它只影响以后真正缺失而需要调用provider的
work。若operator希望用新模型完整重建，应执行显式derived reset/new Store或等价受控重建流程，而不是把model塞回semantic identity。

## 2. 当前抽象如何承载C2

```text
FamilyDefinition
  system prompt + FullReplacementText output mode + zero tools + input protocol
        |
        +-- Definition: world-understanding + its user prompt
        +-- Definition: autobiography       + its user prompt
                          |
                       Full Recipe
                          |
                IRecapCellBatchExecutor
                          |
             exact replacement Text
                          |
       ordinal equality => Updated / KeepUnchanged
                          |
                 immutable Cell + RowView
```

- `FamilyDefinition`是exact shared prompt/protocol equivalence class；它的digest决定可共享的prepared prefix。
- `MaintainerDefinitionRevision`只描述某一列的logical identity、成套的routing target与provider-facing
  `SemanticHeading`、readable scope、内容任务和byte bound。heading由pre-Prepared renderer生成Markdown标题，
  不混入Maintainer正文或冻结的`atelia.recap.input.v1`。
- 同row两列都读取previous selected RowView与current History segment，互相不读取同row刚生成的sibling output；跨列信息在下一row传播。
- Runtime可因相同route/cache策略按leader/follower顺序调用，但该调度顺序不是durable dependency。
- Manager独占Cell/RowView publish。provider execution可以安全重试，不承诺exactly-once远端调用。

Family system prompt负责所有成员共同的协议和证据纪律；Definition user prompt负责内容关注点。现有两份短user prompt并未承载旧
专业system prompt中的全部规则，因此实施时必须做一次语义保持型重构，而不是只替换文件引用：

- shared system prompt明确输入依次包含previous recap pack、current history segment与最后的member task；
- 明确只能维护`logicalColumnId`指定成员，sibling blocks只作为上下文；
- 明确只依据可见History与prior recap，不把推测伪装成事实；
- 明确只返回完整replacement正文，第一个与最后一个字符都属于正文，不返回tool call、控制字段、sentinel、说明或前后缀；
- 两份user prompt不再声称“上下文开头就是自己的旧文档”，而是要求从带`logicalColumnId`的prior projection中识别自己的旧block；
- 将两份旧zh-CN system prompt中的列专属规则完整迁入各自versioned user prompt；autobiography保留第一人称、心理连续性、
  重要关系/承诺与当前内在状态，world-understanding保留事实/推断/矛盾、确信程度、信息分类与known-unknown纪律；
- 两份user prompt要求始终返回完整replacement；确实无需变化时逐字返回同列旧block，由Runtime确定性识别为`KeepUnchanged`。

上述`docs/Galatea/prompt/...`文件是单一authoring source。新程序集通过`.csproj`逐文件显式`EmbeddedResource`、stable logical name
和compile-time link消费exact UTF-8/LF bytes；normal runtime只读assembly resource，绝不按filesystem相对路径加载docs，也不得在C#字符串里
复制第二份正文。loader只接受exact resource names、strict UTF-8/no BOM与bounded bytes。golden tests锁定resource SHA-256、Family digest、
两个Definition digests、registration command digest、ordered targets与V3 no-tools output mode。

## 3. Model/route配置边界

首版V3 route shape固定为：

```text
durable capability:
  (FamilyDigest, RuntimeProtocolId = "text-runtime-v3", SemanticModelId = null)

runtime route manifest:
  exact key above -> ConnectionId

connections.json:
  ConnectionId -> provider kind / ModelId / endpoint / credentials / reasoning / cache policy
```

因此一个shared Family只需要一条`semanticModelId: null`的exact route。Galatea的`recapGrid.routeManifestPath`仍是deferred、strict、
no-fallback输入；实际model默认来自`connections.json`中被选connection的`modelId`。C2允许operator修改配置后重启/重开Host切换模型，
不修改Control或derived identity。进程内hot reload与按费用、延迟、健康度或任务类型逐调用选择明确不属于C2：当前Runtime按route key
缓存resolved route，未来实现动态策略时必须一并重做Host resolver、Runtime route-cache/lifetime、admission与telemetry合同，不能只替换
一个callback。该后续协议仍须满足：

1. 输入只来自runtime config/health/operation policy；
2. 不改写durable Family/Definition/Recipe；
3. Prepared/Started recovery仍按冻结的completion/tool identity处理，不能被current route抢先遮蔽；
4. 每次调用的actual provider/model/connection必须进入bounded operation telemetry/evidence，便于质量、费用和故障审计；C2B已让
   Hosting/Runtime同时记录non-durable `ConnectionId`、model、provider与API evidence；
5. missing/no-build/readiness/pure inspection不得构造provider client。

provider-free readiness只能报告configured route/connection，不得伪称actual dispatch evidence；只有provider调用settled后的telemetry才报告
actual provider/model/connection。

`SemanticModelId`作为通用RecapGrid contract仍可保留给未来真正认为“模型本身属于求值语义”的其他domain，但Galatea C2必须使用
`null`，且测试要证明切换runtime model不改变Family/Definition/Recipe/EvaluationKey identity。

模型切换后的mixed runtime provenance是这一裁决的直接结果，并在C2中明确接受：已fulfilled Cells继续复用；partial row或后续rows的
missing Cells可以由新模型补齐。它们仍具有相同durable语义身份，因为模型被定义为执行策略而不是求值输入。实际provider/model只能由
bounded runtime telemetry审计。若某次运营要求整批内容同模型生成，必须先停服/drain，再显式reset/new derived Store并完整rebuild；
不得通过隐藏stale规则或把model重新塞入Definition来实现。

## 4. 具体程序集归属

新增Galatea-owned product assembly，建议命名：

```text
prototypes/Galatea.RecapGrid/Galatea.RecapGrid.csproj
namespace Atelia.Galatea.RecapGrid
```

它只引用：

- `SessionJournal.RecapGrid.Abstractions`：创建Family/Definitions；
- `SessionJournal.RecapGrid.Control`：输出canonical registration bundle。

它不得引用Galatea.Server、Hosting、Runtime、Manager、Store、AgentControl、Completion provider或CLI。建议公开一个很窄的code-owned catalog：

```text
GalateaRecapGridAssets
  AssetId = "galatea-rolling-rewrite-zh-cn-v4"
  TryCreateRegistrationBundle(assetId, out bundle)
  Describe(assetId) -> ordered definition digests/targets/resource digests
```

该程序集只拥有Galatea-specific canonical definitions与prompt resources，不拥有repo-bound Recipe、route、connection、admission、active state或
provider client。Full Recipe仍由通用CLI在读取exact current Timeline与已注册Definition digests后组成。

不要把这个asset加入`RecapGridAgentControlBuiltIns.AssetIds`。该catalog参与AgentControl
`ImplementationSetFingerprint`；把纯operator Galatea asset塞进去会无意义地旋转所有frozen AgentControl runtime identities，并破坏旧
ToolContinuation exact bind。C2 asset由operator-only catalog提供；Agent将来若需要主动安装它，应另立显式版本化capability，而不是复用当前
AgentControl built-in集合。

`SessionJournal.Cli`作为operator composition root可以同时引用通用AgentControl built-ins和`Galatea.RecapGrid` asset catalog。实现使用
compile-time closed、exact-ID resolver；禁止反射扫描、DI/plugin discovery或目录发现，并以测试锁定asset ID无重复。CLI继续通过
`recap-grid control provision-asset --asset ...`暴露一个明确命令；旧CLI名`provision-built-in`已删除且不保留兼容分支。
Agent-facing JSON action仍由AgentControl独立拥有`provision-built-in`，不进入operator asset catalog。

## 5. 为未来三类refiner保留的边界

不要建立一个把三者混成同一继承树的`IExperienceRefiner`。应区分“产物authority”和“执行协议”两条轴：

| 能力 | 产物authority | 执行协议 | 与RecapGrid关系 |
|---|---|---|---|
| `RecapRewriter` | immutable RecapGrid Cell | single-shot full rewrite | C2直接实现 |
| `RecapEditor` | immutable RecapGrid Cell | invocation-local pure editor tool loop | 将来新增RuntimeProtocol/executor |
| `ExperienceRefiner` | 独立external-memory artifact owner | pure proposal或tool-assisted refinement；effect由owner结算 | 不进入RecapGrid publish path |

### 5.1 RecapRewriter

当前`text-runtime-v3`是single-shot、no-tools、no-sentinel的`FullReplacementText`协议。每个missing Cell至多启动一次真实Completion；
现有Manager可据此用missing work count做调用前admission和operation evidence。Family canonical schema显式记录
`FamilyOutputMode.FullReplacementText`，并强制`OrderedTools.Count == 0`；`OrderedTools`只作为未来其他output mode的扩展入口。

V3删除V2且不提供兼容读取或route fallback。runtime/output identity分别为`text-runtime-v3`与`atelia.recap.output.v3`；
input/prior/history rendering schema仍独立保持v1。Completion projection使用empty tools、`CompletionToolChoice.ProviderDefault`和
`allowParallel=null`，使Anthropic、OpenAI Chat/Responses与Gemini wire都不声明tools或required tool name。

V3 output parser接受的exact block grammar是：零个或多个provider-native reasoning、恰好一个nonblank `ActionBlock.Text`、随后零个或多个
provider-native reasoning。Completion Aggregator负责把连续text deltas聚成一个Text block；多个Text blocks、任何ToolCall、reasoning-only、
blank、unknown/incomplete/error result与invocation mismatch全部fail closed。Parser不trim、normalize、剥离preamble或解释正文结构；
UTF-16 validity与UTF-8 byte cap都按exact正文检查。正文按ordinal恰好等于同列prior content时得到`KeepUnchanged`，否则得到`Updated`；
首row无同列prior时始终是`Updated`。Cell只保存exact Text正文。

这次hard cut由V2真实Opus canary直接触发：world-understanding response把History中的业务实体`Role-Play Agent`替换成了reserved tool name
`recap_grid_finalize_cell`。V2 reserved-token gate正确阻止了污染写入，却也证明了“让模型用协议tool name提交正文”会把控制token注入正文语义。
V3从provider request与prompt中完全移除该tool name，使控制协议不再与Galatea长期记忆争夺词义。

### 5.2 RecapEditor

未来可注册新的`RuntimeProtocolId`并提供另一个`IRecapCellBatchExecutor`实现：以prior block建立调用内draft，允许模型使用
replace/insert/delete等纯编辑工具，最后仍返回完整正文，由Manager一次性发布新的immutable Cell。editor tools必须：

- 只修改本次invocation的内存draft；
- 不写Grid/Control/raw journal/文件/网络或外部记忆；
- 有operation-total provider calls、elapsed、draft bytes与tool steps预算；
- crash/cancel后允许从相同visible input重新开始，不把内部loop伪装成exactly-once。

在实现真正的第二执行协议前不抽象空框架；届时必须让executor report实际provider call count，避免多轮loop在Manager/Online预算中被记成
“一次cell调用”。如果需求只是一次返回bounded patch list，优先实现terminal structured patch protocol，而不是完整tool loop。

### 5.3 ExperienceRefiner

动态外部记忆条目不是Recap Cell。ExperienceRefiner可以先纯计算canonical proposal/command，也可以使用tool-assisted refinement；真正的
外部副作用始终由独立artifact owner验证、应用并结算，而不是由LLM/tool loop直接获得写权限。它可以复用HistoryTimeline
segment/materialization与SessionJournal工具恢复思想，但必须先有自己的：

- canonical memory command/entry schema与identity；
- artifact Store/owner；
- `Apply(operationId, command)`及terminal receipt；
- `Applied / Replayed / Conflict / CommitIndeterminate` settlement；
- exact tool runtime composition与bounded telemetry。

RecapGrid executor是可重试的derived evaluation，严禁从中直接产生外部副作用；raw `ToolResult`不是外部记忆authority，
`operationId`本身也不提供exactly-once。AgentControl/Control只可作为receipt设计范例，不是通用副作用账本。

## 6. C2实施分包

### C2A：contract hardening + asset assembly

状态：Complete，commit `bf4beff0`。

- 给`FamilyDefinition.OrderedTools`、`IRecapCellBatchExecutor`、`RecapCellArtifact`补上述协议/纯度/immutable注释；
- 当前`text-runtime-v3` validator要求Family声明`FullReplacementText`且tools为空，禁止“声明了tools但runtime从不dispatch”的假扩展；
- C2 V3 Family锁定no-tools output mode与provider projection的empty tools / ProviderDefault / null parallel choice，并纳入canonical goldens；
- current V3 executor XML contract锁定每个work最多一次provider call；未来multi-call protocol必须先扩逐call pre-admission、actual-started
  count、cancel/drain和operation-total evidence；
- 新建`Galatea.RecapGrid` product/tests/public-surface，嵌入三份prompt资源，生成一个Family和两个Definitions；
- 两个capability的`SemanticModelId=null`，首版`MaxContentUtf8Bytes=32 KiB`；若真实canary证明不够，再以新Definition revision调整，
  不把它做成runtime override；
- exact canonical/golden、shared prefix、same-row independent input与Keep tests。

### C2B：operator/config vertical

状态：Complete，commit `eb3743dd`。

- CLI operator catalog接入Galatea asset，但不改AgentControl catalog/fingerprint；
- scaffold/admission输出允许exact Family/Definitions/targets，route manifest输出一条`semanticModelId:null` route；
- provision asset -> compose Full recipe -> put recipe -> build -> zero-call proof promotion；
- config A/B只改变connection的actual model，证明durable digests与已有fulfillment不变，新的missing work使用新model；
- Hosting/Runtime telemetry补actual`ConnectionId`；readiness的configured route evidence与调用后的actual dispatch evidence分开测试；
- no-fallback、unknown asset/route/model、partial output、stale head与CommitIndeterminate均typed fail closed。

### C2C：fake Host vertical

状态：Complete，commit `62b93f9a`。

- Galatea fresh/Observation/ToolResult路径真实使用该active Full recipe；
- row n两列共享同一prepared Family prefix，输入previous whole pack + current segment；
- partial build/restart只补missing work，切模型不会使已fulfilled rows自动重建；
- main SessionJournal的Prepared/Started/ToolContinuation顺序与provider-zero门禁保持现有合同；RecapGrid derived provider call本身没有
  durable Prepared/Started状态，仍允许未commit调用在恢复后重复；
- readiness/telemetry报告exact Family/Definition/Recipe和actual provider/model/connection evidence。

closure tail补齐formal active asset的ToolContinuation→durable ToolResultObserved→下一PreObservation两列maintenance，
以及public CLI scaffold→init→provision→world-first compose→put→activate→strict Galatea Host/readiness全链。新增tail
focused 2/2、Galatea full 93/93；frozen/provider-free阶段provider construction/dispatch均为0。

### C2D：real-provider canary + actual activation

状态：Complete。V2 canary fail closed并促成V3 hard cut；V3先在disposable repos完成rolling与内容质量门禁，随后从
immutable legacy JSON export重新导入全新的actual repo并完成显式promotion。source tests没有被当作real-provider evidence。

- 先在immutable legacy export导入的一次性repo clone上，使用
  `prototypes/Galatea/.atelia/galatea/connections.json`中的默认Opus 4.6 connection做bounded rebuild；
- canary启动前必须向用户给出并确认exact disposable clone、selected Ref/raw head、provider/model/connection、maximum calls、
  retry policy、估算费用与secret/call-log策略；不得把“费用不构成阻断”的方向性授权扩大成未绑定target与call cap的无限调用；
- 人工审阅自传连续性、world-understanding证据纪律、两列串位、identity-bearing名称保真、cache/usage、延迟与输出bytes；
- canary通过后，actual cyber activation仍需再次确认停服时点、exact target repo/Ref/raw、备份/恢复边界和首次new raw write；
  随后才可重建或替换actual cyber repo并显式provision/build/promote；
- 首次new raw append前允许回退binary/config；append后不得覆盖raw，只能forward-fix或先证明旧binary兼容新raw。

#### 6.1 C2D exact execution record

本次执行使用run root
`gitignore/galatea-grid-acceptance/20260814-c2d-cyber-b71822a/`；该目录被git忽略，包含受限call logs、operator inputs、
content-free reports与pre-activation config backups，不是tracked source。最终无正文/无secret的live evidence为
`reports/activation-live-readiness.json`。legacy authority为：

- export：`prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json`；
- source SHA-256：`b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`；
- imported Ref：`000000000400001f`；imported raw head：`ej1:00000487000004330000000100000000`；
- import得到71个Observation、71个Action、1个runtime setup、4个prompt setup；旧2个compaction与2个recap被显式跳过，warnings为0；
- calibration为HistoryLoad 116,458、142 history units、145 raw events与414,487 rendered bytes。

第一次V2 real-provider canary发现`Role-Play Agent`被模型替换成terminal tool name。V2 reserved-token gate阻止了污染Cell落盘，
但该结果证明tool name本身已经进入长期记忆语义，因此hard cut到V3 `FullReplacementText`/zero-tools，而不是重试或增加兼容分支。

V3 real-provider采用同一shared Family、world-first、`maxConcurrency=1`、Opus 4.6与runtime `reasoningEffort=low`；model与effort仍不进入
durable identity。B=45,000/R=24,000 disposable shadow真实生成2 rows/4 cells，证明row 1两个siblings读取同一whole prior pack，且其中
两列分别与row 0输出逐字相等；每row第一列创建Anthropic prefix cache、第二列读取该prefix。该结构证据来自较早的V3 member prompt
revision；最终production prompt在固定export的B=60,000/R=24,000 policy下只产生一个bootstrap row，因此不能把前者冒充为最终prompt的
第二row质量证据。

内容gate没有因wire/Store成功而自动放行。多个fresh B60 candidates依次因以下P0被拒绝且从未promotion：

1. autobiography把剧本中的Recital 132艺术映射升格为现实法律要求；
2. Timeline row以用户Observation提交文本结束时，autobiography虚构Galatea已经阅读、感受、评价或“正在读”；
3. 来源限定只在段首出现、后续可独立流通的绝对句又失去局部source/uncertainty scope。

对应prompt revisions把法律/制度/故事机制改为逐句或同一条目局部绑定source与未核验状态，并规定无Galatea Action的terminal segment只能
记录“收到/可见/尚未回应”，不得补写心理吸收、评价、选择或进行中的动作。最终fresh B60 candidate的两项P0均关闭，independent review
给出promotion GO：world与autobiography分别为7,464与7,841 UTF-8 bytes，低于32 KiB；两列无协议词污染，identity-bearing `Agent`/
`Role-Play Agent`保持原词；autobiography把法律命题限定为老刘剧本的艺术解读并注明未独立核验，末句只记录文本可见且尚未回应。

最终两次provider调用均为`opus4-6-recap` / `claude-opus-4-6` / Anthropic / low，termination为`end_turn`且errors为0。bounded usage
合计为27,341 uncached input、20,273 cache creation、144,545 cache read与6,152 output tokens；第二列的82,409 cache read包含首列新建的
20,273-token prefix，证明sequential sibling prefix reuse。repeat build为zero-call/zero-write，Timeline/Control/Grid verify、promotion与
materialization全部通过。

#### 6.2 Actual cyber activation

actual repo不是旧DerivedRecap repo的clone，而是再次从同一immutable export fresh import：

```text
prototypes/Galatea/.atelia/galatea/sessions/cyber-session-journal-recap-grid
```

在RecapGrid init之前，operator用exact old head显式reconcile governing setup到main connection `opus4-6`、Anthropic surface与当前
`prompts/cyber.md`；新raw head为`ej1:00000498c80004b10000000100000000`。随后按B=60,000/R=24,000显式创建Cadence/Timeline/
Control/Grid、sync一个row、provision V4 asset、world-first compose/put、用独立recap connection执行2-call build、zero-call复验、promotion与
materialization。actual recipe digest为`2aaade63404d086393ee3e9998f71620ae1e4748f35a3d634d6e66ba4faff0d3`；actual两份输出与已批准
的final disposable candidate逐字相等。

live config已切到该新repo和V3 profile/route。main Agent的default connection仍是`opus4-6`且保持provider-default reasoning；
RecapGrid单独使用`opus4-6-recap`的low策略。strict live connections保留原有dsv4p、opus4-6、opus5与fable5 choices，只删除retired
top-level routing field并补齐strict surface/base-address shape，没有把“recap两列使用单一模型”误解成删除main-agent choices。

正式live config复验只执行登录、`/api/me`与`/api/recent-turns`，没有提交用户消息或构造main completion；结果为
`freshness=exact`、`state=ready`、6个recent turns、上述raw head与actual recipe exact。旧repo
`prototypes/Galatea/.atelia/galatea/sessions/cyber-session-journal`未修改、未归档、未删除。

回滚边界是恢复run root中create-only保存的pre-activation `config.json`与`connections.json`，使Host重新指向旧repo；回滚绝不删除、
覆盖或rewind新repo。当前尚未在新active repo发送用户消息，因此该config rollback仍不会遗失cutover后的对话。首次新用户raw append后，
不得用回退配置隐藏新经历；应停服并forward-fix，或先制定显式的raw-preserving迁移方案。

## 7. 验收与No-Go

C2 source candidate至少满足：

1. shared prompt + two user prompts只有一份authoring source；embedded bytes与canonical digests有goldens；
2. 两Definitions同Family、同runtime protocol、`SemanticModelId=null`，ordered target exact；
3. route只通过runtime config选择connection/model；修改model不会改变任何durable identity；
4. normal Galatea不隐式provision、promote、fallback或scan旧DerivedRecap roots；
5. fake provider完整证明rolling recurrence、shared prefix、Keep、missing-only restart、model switch和recovery ordering；
6. AgentControl implementation fingerprint不因Galatea operator asset而旋转；
7. real-provider与actual cyber activation各自有独立evidence，不以fake tests冒充；
8. docs checker、Walking dependency/public-surface gates、affected suites、solution build与diff check green。

C2A-C2C source closure与C2D external execution现已满足上述八项；source、disposable real-provider和actual activation evidence仍按各自
边界记录，不能相互替代。

V3 protocol hard-cut串行affected evidence：Abstractions 15/15、Runtime 52/52、Hosting 20/20、Online 31/31、AgentControl 20/20、
Control 45/45、Manager 74/74、Getter 27/27、CLI 100/100、Galatea Server 93/93、
Galatea/Runtime/Hosting/AgentControl public-surface 1/1 + 2/2 + 2/2 + 1/1、Walking 27/27；`Atelia.sln` build
0 warning / 0 error，scoped legacy-protocol search与diff check均为green。最终source-discipline/role-boundary revisions
`c03c3776`、`ed1d146b`、`cfc04efb`之后，asset direct gates为7/7、CLI 100/100、Galatea Server 93/93，solution build仍为
0 warning / 0 error。

同一asset ID一旦进入Control receipt/canonical catalog即视为immutable。prompt、Family、Definition或output mode的canonical bytes发生变化时
必须发布新的asset revision（例如`...-v4`）；不得让同一个operator operation identity在不同binary中代表不同registration command。

No-Go条件：把Opus 4.6写入semantic identity；把prompt正文复制进C#形成双真源；Galatea normal path自动创建Control/Grid状态；
把Galatea asset塞入AgentControl catalog而未处理frozen identity；把effectful ExperienceRefiner放进可重试Cell executor；或在真实clone质量审阅前
直接改actual cyber repo。

## 8. 仍需用户澄清吗？

当前C2 source设计没有剩余的高层需求阻塞。以下均按工程决策处理，无需再打断用户：

- 首版32 KiB/列及world-first canonical order；
- C2配置变化通过Host restart/reopen生效，逐调用动态route属于独立后续协议；
- 将旧专业system规则迁入两个versioned user prompts、共享system prompt润色与golden更新；
- model切换后保留既有Cells、只由新model补missing work的mixed runtime provenance；
- 新程序集、operator catalog composition、测试/public-surface/Walking细节；
- fake provider验证范围。

上述两个execution decisions已在C2D中以exact manifest落实：bounded disposable clones先行、actual repo从clean legacy export fresh import、
pre-activation config create-only备份、无用户消息的live readiness复验。当前没有需要用户继续澄清的C2需求或关键设计决策。

# SessionJournal Derived Recap History Load：目标设计

> **状态**：Target Design / Implemented
> **日期**：2026-07-31
> **实施状态**：H0～H2与 C3 已完成。Production cadence已唯一切换到
> config V2 + `o200k_base` HistoryLoad；旧 count scheduling authority、header cadence
> prefilter与 config V1均已删除。
> **适用范围**：`prototypes/SessionJournal.DerivedRecap.Planner`
> **上位设计**：
> [Event-addressed Derived Recap V4](durable-target.md)
> **前置实现**：
> [Derived Recap Cadence](../../archive/superseded/derived-recap-cadence-target-design.md)、
> [Repo-owned RecapPlannerConfig](planner-config.md)
> **施工计划**：
> [EADR V4 实现与替换计划](../../archive/completed-plans/event-addressed-derived-recap-v4-implementation-plan.md)
>
> **章节角色**：§0～§7、§9记录accepted target/current rules；§8是H0～H2/C3的
> historical closed delivery record，不承担current implementation status。

## 0. 决策

Derived Recap 的 recent suffix reserve与 rolling build interval从
`HistoryUnitCount`迁移为 `HistoryLoadUnit`：

```text
R = MinimumRecentHistoryLoad
B = RecapBuildIntervalHistoryLoad
G = cadence baseline之后的 HistoryLoad

G < R + B  -> NoBuild
G >= R + B -> 选择 replay-safe admission：
              absorbed load >= B
              remaining recent load >= R
```

`HistoryLoadUnit`是 SessionJournal内部的动态上下文管理单位。它不表示：

- 推理模型实际 token数或 context-window占用；
- provider usage、billing或preflight token；
- Shannon information、语义密度或内容价值。

V1 estimator固定为：

```text
atelia.history-load.o200k-base.history-unit-v1
```

它以 `o200k_base`作为稳定、跨语言的正文分段尺度。Estimator identity冻结 vocabulary、
字段选择、framing与单 HistoryUnit数值语义；window additivity与boundary projection由
`RecapHistoryLoadProjector` contract冻结。任一数值语义改变都必须发布新 identity并重新校准
thresholds。不同 identity的 HistoryLoad不可比较。

## 1. Authority 与计量对象

```text
raw SessionJournal
  owns: immutable events, Parent lineage, dependency-closed HistoryUnits,
        replay-safe boundaries

DerivedRecap Planner/Host
  owns: unit-estimator registry, window projector, thresholds, trigger, admission

DerivedRecap Store
  owns: Building/Published set与blocks
  does not understand: estimator、HistoryLoad或active config

Maintainers
  consume: frozen route与prior context
  do not choose: estimator、threshold或admission
```

`IHistoryUnitLoadEstimator`只消费一个 `SessionHistoryPlanningUnit`。Observation、最终 Action和完整
ToolResults参与测量；tool execution error若形成 `ToolResultsMessage`也参与。

本文不使用“History Event”作为 contract名：在 SessionJournal中 `Event`容易被理解成 raw
`SessionEvent`，而多个 raw events可能共同投影成一个 HistoryUnit，protocol/retry events也可能
完全不形成 HistoryUnit。

`CompletionRequestPrepared`、attempt started/failed、retry、setup和其他不进入 Context的 raw
events不形成 HistoryUnit，因此不贡献 HistoryLoad。

Unit estimator不测量 Recap blocks、system prompt、tool definitions或最终 provider request。
Provider usage/context limit属于 Completion telemetry/preflight层，不进入 Recap scheduling
authority。

HistoryLoad：

- 可从 raw facts重算；
- 不写入 raw event；
- 不进入 Recap Store manifest/block；
- 不改变 Published ordinal或materialization；
- 不成为 Resume/Restore correctness input。

Building安装后，frozen admission与route就是 Resume authority。Resume、Prepared recovery和
Restore不读取 active config、不加载 estimator、不读取cache，也不重新测量 HistoryLoad。

## 2. Runtime contracts

使用 checked `long`强类型，避免 HistoryLoad、HistoryUnit count和raw-event count混用：

```csharp
public readonly record struct HistoryLoadUnit {
    public HistoryLoadUnit(long value) {
        if (value < 0) {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        Value = value;
    }

    public long Value { get; }
}
```

不得提供与 `int historyUnitCount`或 `int rawEventCount`的隐式转换。`R+B`和所有 prefix
arithmetic必须 checked；overflow是 typed config/measurement defect。

Tokenizer-facing contract只测量一个已经由 raw core完成 dependency closure的 HistoryUnit：

```csharp
public interface IHistoryUnitLoadEstimator {
    string Id { get; }

    HistoryUnitLoadMeasurement Measure(
        SessionHistoryPlanningUnit unit,
        int maxRenderedUtf8Bytes
    );
}

public sealed record HistoryUnitLoadMeasurement(
    HistoryLoadUnit Load,
    int RenderedUtf8Bytes
);
```

Estimator不理解 cadence baseline、raw lineage、replay-safe boundary、recent reserve或admission。
`SourceStartInclusive/SourceEndInclusive`是 cache/provenance信息，不进入数值输入；V1只读取
`unit.Message`。

Planner用独立的、tokenizer-neutral projector聚合 window：

```csharp
public static class RecapHistoryLoadProjector {
    public static RecapHistoryLoadMeasurement Measure(
        SessionHistoryPlanningWindow window,
        EventAddress baselineAddress,
        IHistoryUnitLoadEstimator estimator
    );
}

public sealed record RecapHistoryLoadMeasurement(
    string EstimatorId,
    EventAddress BaselineAddress,
    int BaselineCompletedUnitCount,
    HistoryLoadUnit Growth,
    int RenderedUtf8Bytes,
    IReadOnlyList<RecapHistoryLoadBoundary> ReplaySafeBoundaries
);

public sealed record RecapHistoryLoadBoundary(
    EventAddress Address,
    int HistoryUnitCountSinceBaseline,
    HistoryLoadUnit AbsorbedSinceBaseline
);
```

Unit estimator必须满足：

- `Load >= 1`；
- `maxRenderedUtf8Bytes > 0`；
- rendering过程中一旦将超过该 cap立即 typed-fail，不先构造超限字符串；
- `RenderedUtf8Bytes >= 0`且不超过传入 cap；
- 相同 message与 estimator identity产生相同结果；
- unknown message/block、invalid Unicode、overflow或超限 typed-fail，不 fallback。

`SessionHistoryPlanningWindow`可能为 lagging block cursor包含 baseline之前的旧 prefix。该 prefix只
供 source/replay route使用；projector先从 exact `baselineAddress`解析 unit offset，再从该 offset
开始调用 estimator：

- estimator不render、不计量；
- measurement byte limit不包含它；
- cache不因它增长；
- cadence growth从 baseline归零。

Projector必须满足：

- `baselineAddress == window.StartExclusive`时内部 offset严格为零；
- 否则 address必须在 input replay-safe boundaries中 exact出现一次，内部 offset取该
  boundary的 `CompletedUnitCount`；
- resolved offset必须在 `[0, Units.Count]`，否则 `CadenceBaselineInvalid`；
- result `BaselineAddress/BaselineCompletedUnitCount`只能来自这次内部解析；
- 输出包含 raw顺序上严格晚于 baseline address的 input replay-safe boundaries，即使多个
  boundaries共享同一个 completed-unit count；address/order必须 exact match；
- `Growth`与absorbed非负，且 `absorbed <= Growth`；
- recent由 `checked(Growth - absorbed)`唯一派生，不重复存储；
- `Growth`是 suffix内全部 unit load的 checked sum；
- window rendered bytes是相同 unit measurements的 checked sum；
- malformed output、overflow或异常是 typed planning unavailable，零 Building/LLM side effect。

Address到unit offset的解析由一个 Planner-internal baseline resolver作为单一实现；projector与后续
evaluator validation复用它，不各自维护 `SingleOrDefault`/ordinal算法。Resolver还必须返回
baseline boundary ordinal，以保证 baseline之后、与其共享同一 `CompletedUnitCount`的boundary
仍保留在输出中。

Range additivity由 projector定义，而不是要求 tokenizer跨消息拼接：

```text
E(unit) = estimator.Measure(
    unit,
    HistoryLoadMeasurementSafety.V1.MaxRenderedHistoryUnitUtf8Bytes
).Load
M(empty) = 0
M(A ++ B) = checked(sum(E(unit) for unit in A ++ B))
```

因此 prefix subtraction和cache是定义上的精确运算。需要跨 HistoryUnit联合测量的
non-additive算法属于另一种 contract，不能实现 `IHistoryUnitLoadEstimator`。

## 3. Exact baseline 与 cadence

Executor先确定 baseline address；projector负责把它解析成 exact planning-window unit offset：

```text
Existing Published:
  baseline address = latest Published SetAdmissionAnchor

Empty recap:
  baseline address = core验证的 EmptyReplayStartExclusive
```

规则：

- baseline等于 `window.StartExclusive`：projector解析为 `baselineCompletedUnitCount = 0`；
- 否则 projector要求 baseline在 window中对应 exact、唯一的 replay-safe boundary，并使用该
  boundary自身的 `CompletedUnitCount`；
- baseline outside、重复或非 replay-safe：`CadenceBaselineInvalid`；
- earliest Maintainer cursor不得冒充 cadence baseline。

Projector只对下列 suffix逐 unit调用 estimator：

```text
window.Units[baselineCompletedUnitCount..]
```

于是：

```text
G = measurement.Growth
C(candidate) = candidate.AbsorbedSinceBaseline
Recent(candidate) = checked(G - C(candidate))

trigger:
  G >= R + B

candidate:
  C(candidate) >= B
  Recent(candidate) >= R
```

Evaluator产生所有 cadence-legal replay-safe candidates，并在 policy返回后独立复验 selected
address与两段 load。Policy只应用 topology、route、call和raw-event budgets。

Candidate新旧顺序由 raw Parent lineage决定，不由HistoryLoad大小决定。V1
`bounded-maintain-all-v1`可以保留，因为它的算法仍是“从 evaluator授权的 candidates中选择最新且
budget-valid者”；H1必须把当前 count排序显式收口为 lineage-position排序，并用等价性测试证明
observable selection未变。若 policy本身开始解释load，才需要升 policy identity。

dependency closure可能使 recent load大于 R；Planner只承诺 `recent >= R`，不承诺 exact R。

## 4. O200k V1

### 4.1 Dependency 与生命周期

Planner直接 pin：

```text
Microsoft.ML.Tokenizers 2.0.0
Microsoft.ML.Tokenizers.Data.O200kBase 2.0.0
Microsoft.Bcl.Memory 9.0.17
```

最后一项是 tokenizer 2.0.0当前传递依赖 `Microsoft.Bcl.Memory 9.0.4`的显式安全 override；
9.0.4受 `GHSA-73j8-2gch-69rq`影响，9.0.17位于官方已修复的9.x版本线。该 pin不改变
HistoryLoad数值 identity，但必须由依赖漏洞审计守护。

创建：

```csharp
TiktokenTokenizer.CreateForEncoding("o200k_base")
```

Tokenizer实例在进程内缓存复用。具体实现建议命名为
`O200kBaseHistoryUnitLoadEstimator`。PackageReference、asset pin与golden vectors属于 H0
Craft清单；首版不提前拆新 project。

参考：
[Microsoft使用指南](https://learn.microsoft.com/en-us/dotnet/ai/how-to/use-tokenizers)、
[O200kBase 2.0.0](https://www.nuget.org/packages/Microsoft.ML.Tokenizers.Data.O200kBase/2.0.0)。

### 4.2 Canonical framing

不得使用 `ToString()`、`ActionMessage.GetFlattenedText()`、provider-native request JSON或
provider role template。

基础操作：

```text
AppendTag(tag):
  AppendLiteral("[")
  AppendLiteral(tag)
  AppendLiteral("]\n")

AppendField(tag, scalar):
  AppendTag(tag)
  AppendLiteral(scalar ?? "")
  AppendLiteral("\n")
```

`AppendLiteral`保留输入原文，不做 Unicode normalization或换行转换；生成的separator永远是
U+000A。所有 scalar先按 strict Unicode scalar value校验，unpaired surrogate typed-fail。
最终 rendering总是以 `\n`结尾。

Dispatch按下列顺序和exact runtime shape执行：

```text
ToolResultsMessage:
  AppendField("tool-results-content", Content)
  for result in Results:
    AppendField("tool-result-name", result.ToolName)
    AppendField("tool-result-status", success|failed|skipped)
    for block in result.Blocks:
      Text:
        AppendField("tool-result-text", block.Content)
      unknown:
        fail

ObservationMessage, exact base type:
  AppendField("observation", Content)

ActionMessage:
  AppendTag("action")
  for block in Blocks:
    Text:
      AppendField("action-text", block.Content)
    ToolCall:
      AppendField("tool-call-name", block.Call.ToolName)
      AppendField(
        "tool-call-arguments",
        block.Call.RawArgumentsJson ?? "{}"
      )
    ReasoningBlock:
      AppendTag("reasoning-opaque")
    unknown:
      fail

SessionContextHeader or unknown IHistoryMessage:
  fail
```

`ToolResultsMessage`必须先于其 `ObservationMessage`基类dispatch。ToolCallId、opaque reasoning
payload、provider/codec/debug metadata不计入正文；reasoning只贡献固定结构tag。

Tokenizer固定使用 `CountTokens(string)`默认路径，不启用 provider special-token处理。每个
HistoryUnit独立：

```text
UnitLoad = max(1, CountTokens(RenderV1(unit)))
RangeLoad = checked(sum(UnitLoad))
```

HistoryUnit之间不发生 BPE merge；这个数值故意不等于对完整 provider request一次 tokenize。

### 4.3 Measurement safety

V1共享 code-owned planning safety contract `HistoryLoadMeasurementSafety.V1`：

```text
MaxRenderedHistoryUnitUtf8Bytes = 4 MiB
MaxBaselineRelativeWindowUtf8Bytes = 32 MiB
```

Projector是 measurement caps的唯一调用 authority：它把4 MiB传给每次
`IHistoryUnitLoadEstimator.Measure(unit, maxRenderedUtf8Bytes)`。Unit estimator边生成边以
strict UTF-8累计 bytes，不构造超限 unit rendering。Projector再累加各
`HistoryUnitLoadMeasurement.RenderedUtf8Bytes`并执行32 MiB baseline-relative window cap。
任一超限返回 `HistoryLoadInputTooLarge`。

这些 caps不属于 estimator数值 identity，不是 provider context limit，也不写 config/manifest；
调整它们必须独立版本化、审阅并保留旧有效输入的兼容性。

## 5. Config V2 与 composition

V2 cadence fragment：

```json
{
  "cadence": {
    "historyUnitLoadEstimatorId": "atelia.history-load.o200k-base.history-unit-v1",
    "minimumRecentHistoryLoad": 100000,
    "recapBuildIntervalHistoryLoad": 120000
  }
}
```

Runtime/config validation：

```text
MinimumRecentHistoryLoad >= 0
RecapBuildIntervalHistoryLoad > 0
checked(R + B)
```

B必须为正，保证每个 Published admission都有严格load progress；R可以为零。

完整 schema：

```text
atelia.session-journal.recap-planner-config.v2
```

示例数值不是 Galatea defaults。H0必须先在真实 history fixture/repo上产出 load distribution并完成
threshold calibration，H1c才允许生成 canonical V2 init document或替换真实 repo config。

V1 count thresholds无法自动换算为 V2 load thresholds。Loader不猜测、不原地重解释，也不保留
V1/V2双 authority；operator校准后原子替换旧文件。

Host从 code-owned registry解析 unit estimator ID，并与 config、catalog、policy、limits形成一次
immutable composition snapshot。Unknown estimator在 raw payload read、provider/client创建和
Store mutation前 typed-fail。Existing Building、Prepared/Started recovery与Restore继续
active config/estimator zero-touch。

所有含 cadence measurement的 machine-readable report schema同步升版，并至少记录：

```text
historyUnitLoadEstimatorId
growthHistoryLoad
selectedAbsorbedHistoryLoad? / selectedRecentHistoryLoad?
growthHistoryUnitCount
rawGrowthEventCount
```

每个 load字段都由同一 report中的 estimator ID解释。Measurement/config hash只用于诊断，不进入
Building/Published recovery lock。

## 6. 保留与删除

| 量纲/事实 | H1后用途 | 是否 scheduling authority |
|---|---|---|
| `HistoryLoadUnit` | trigger、absorbed/recent eligibility、诊断 | 是 |
| `CompletedUnitCount` / total HistoryUnit count | baseline/cut对齐、结构验证、诊断 | 否 |
| `GrowthHistoryUnitCount` | report、回归对照 | 否 |
| raw lineage position | candidate新旧顺序、ancestry | 否 |
| `RawGrowthEventCount` | traversal backpressure、诊断 | 否 |
| per-step/per-build raw limits | bounded replay与resource safety | 否 |
| UTF-8 byte caps | estimator/request/contribution各自resource safety | 否 |
| provider token/usage | provider telemetry/preflight | 否 |

必须删除：

- `MinimumRecentHistoryUnitCount`、`RecapBuildIntervalUnitCount`、
  `BuildThresholdUnitCount`；
- `GrowthHistoryUnitCount`作为 trigger/reserve的比较；
- `HistoryUnitCountSinceBaseline`作为 candidate eligibility；
- `MaxRawGrowthEventCount >= R+B`的cross-unit validation；
- `rawGrowthEventUpperBound < R+B -> NoBuild`的header-only negative prefilter；
- `HeaderNegative` cadence diagnostics；
- 未被调用且使用 `text.Length / 3`、`GetFlattenedText()`和`ToString()` fallback的
  `SessionHistoryTokenEstimator`。

raw core中的 `SessionHistoryPlanningUnit`、`SessionHistoryPlanningBoundary.CompletedUnitCount`
和 `SessionHistoryPlanningWindow.Units`全部保留。

Exact baseline确定后先应用 `MaxRawGrowthEventCount`；raw safety gate已拒绝时不再render/tokenize。
Raw ceiling可能在 load threshold前 backpressure，这是合法结果，不再要求两种量纲“可达”。

Header validation/lineage capture仍可保留，但没有有证明力的 load upper bound时，只能进入 exact
evaluation，不能作 cadence NoBuild。不得为恢复该优化把load hint写入 raw header。

## 7. Cache

首版只有：

- process-wide tokenizer复用；
- operation-local `SessionHistoryPlanningUnit -> HistoryUnitLoadMeasurement`结果；
- projector-owned checked load/byte prefix arrays；
- evaluator、policy和diagnostics共享同一 immutable measurement。

首版不修改 raw schema，不实现 persistent cache。H0 calibration同时记录初始化时间、measurement
耗时和allocation；H2只根据真实 profile决定是否加入 bounded process cache。

若短进程 CLI仍有明确瓶颈，再另立 repo-sidecar设计。任何 future cache都必须由 Planner/Host
拥有、完全可删除重建，并以 estimator identity、raw source interval与stable rendered digest隔离；
miss/corruption只触发重算，不参与 Resume/Restore或Store correctness。

### 7.1 H2 profiling结论

H2使用 Galatea fresh-import等价的 142-unit planning window做进程内针对性测量：

| baseline-relative suffix | HistoryLoad | warm p50 | warm p90 | warm allocation p50 |
|---|---:|---:|---:|---:|
| 142 units（首次全历史） | 116,458 | 145.04 ms | 168.95 ms | 3,909,944 bytes |
| 20 units（典型 recent reserve） | 18,968 | 6.48 ms | 7.85 ms | 217,976 bytes |
| 40 units（接近下一次 build） | 36,886 | 12.94 ms | 14.55 ms | 402,336 bytes |

同一进程首次 full projection包含 tokenizer cold initialization，耗时 845.08 ms、分配
55,771,136 bytes；初始化后由 process-wide tokenizer复用吸收。steady-state cadence只测 exact
baseline之后约 20～40 units，当前成本不足以抵消 bounded cache带来的 repo隔离、digest key、
容量/逐出与敏感正文驻留复杂性。

因此 H2决定：**首版不增加 bounded process cache**，保留 process-wide tokenizer与
operation-local prefix。若未来在线 profile显示该 6～15 ms路径成为真实瓶颈，再以独立设计引入
Host-owned、estimator-scoped、可删除重建的cache；本结论不授权 persistent sidecar。

## 8. 实施 gates

H0～H2/C3当时采用的delivery order与closing evidence记录在
[EADR V4 implementation completion record](../../archive/completed-plans/event-addressed-derived-recap-v4-implementation-plan.md)：

```text
H0 unit estimator + window projector + golden vectors + Galatea calibration
  -> H1a inactive V2 contracts/codec/registry
  -> H1b Planner evaluator/policy/executor integration vertical
  -> H1c single production authority cutover
  -> H2 cache profiling decision
  -> C3 real repo acceptance
```

交付时只有 H1c切换 production authority；H1a/H1b没有留下可被 production独立选择的第二套 cadence。
H1c一次性迁移了 CLI/online/report、删除 config V1与旧 comparisons/prefilter，并保持
Building-first、phase-first和single snapshot纪律。

完成证据：

| Gate | 结果 |
|---|---|
| H0 | estimator/projector/golden vectors、Galatea calibration；`e07ff1af`、`0dbf9d6d` |
| H1a | strict inactive V2 codec/registry；`2eb7188a` |
| H1b/H1c | evaluator/policy/executor/CLI/online原子 authority cutover；`84a37cab`、`e47b635c` |
| H2 | 上述 profile决定不增加cache |
| C3 | 当前 Galatea export fresh import后完成 failure/resume、exact corruption/Restore、online与 Prepared recovery；实际 selection 为 growth 116,458 / absorbed 98,082 / recent 18,376；report schema v3（由 v2 直接切换，无 compat） |

具体 load distribution与profile环境见
[Galatea HistoryLoad calibration](../../evidence/history-load-galatea-calibration.md)。

必须覆盖：

- framing golden：中英文、emoji、special-token-like text、tool call/result、opaque reasoning、
  empty content、invalid Unicode与unknown types；
- unit estimator identity/determinism/per-unit cap；
- projector additivity、monotonicity、overflow、window cap与boundary mapping；
- exact baseline、lagging cursor旧 prefix不计量；
- baseline前后多个 boundary共享 `CompletedUnitCount`时仍按 exact address切分；
- `G=R+B-1` NoBuild，`G=R+B`并有合法 boundary时 Build；
- absorbed/recent分别满足 B/R，dependency closure可多留不可少留；
- 单个巨大 recent unit不被错误吸收；
- API failed/retry零 HistoryUnit，因此零 HistoryLoad；
- raw/load/byte/provider四种量纲不交叉比较；
- config V2 strict `Int64`、unknown estimator和V1拒绝；
- missing/invalid config零 provider、零 Building mutation；
- existing Building、Prepared/Started recovery与Restore estimator zero-touch；
- policy lineage排序与旧 observable selection等价；
- Galatea真实 load distribution、threshold calibration与end-to-end acceptance。

Production/test source中不得残留旧 scheduling authority；historical baseline docs中出现旧名称不算
失败。

## 9. Non-goals

- provider/model exact token count、context preflight、billing或usage；
- 根据当前推理模型动态切换 estimator；
- 语义重要性、surprisal或Shannon information；
- 把 Recap/system prompt/tools计入 cadence；
- raw event/schema或Store/Building/Published schema升级；
- 首版 persistent load index；
- config V1 compatibility fallback。

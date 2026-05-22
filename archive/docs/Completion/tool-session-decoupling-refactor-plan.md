# Completion 工具系统与 Session 解耦重构方案

## 背景

近期对不活跃或即将重写的程序集做归档后，`prototypes/Completion.Tools` 中现有的工具执行主链暴露出了一个更清晰的问题：当前模型侧可见的工具集合、工具执行时依赖的会话状态、以及工具注册与调度本身，仍然混杂在同一批对象里。

最典型的信号有三类：

- `CompletionRequest.Tools` 本身就是一次请求级输入，说明“当前对模型可见的工具声明”天然是 session 或 turn 视图，而不是 `ITool` 的固有属性。
- `ITool.Visible` 把“某个 session 当前是否可见”编码成了工具实例上的全局可变状态，容易在多 session、fork session、并行推理下产生语义混乱。
- `MethodToolWrapper` 允许在包装时直接绑定目标实例，这意味着一旦目标实例内部持有 session/app 状态，fork LLM session 时就会倾向于 deep clone 整组 `ITool`，从而把本来应该归属于 session 的状态耦合进工具对象图。

当前的 `ToolExecutor` 也因此承担了过多职责：

- 全局工具注册与名字冲突检测
- 当前可见工具集合投影
- 工具调度与异常治理
- 执行序号分配

这些职责属于不同生命周期，放在同一类型里会让“什么应该全局共享，什么应该按 session 派生”越来越难判断。

本方案的目标不是给现状再加一层兼容外壳，而是直接把职责边界重切清楚，为后续引入显式 `LlmSession`、tool state、session fork、以及更稳定的工具上下文注入铺路。

## 问题定义

### 1. 工具可见性被放在了错误的所有者上

`ITool.Visible` 表达的是“该工具当前是否对某个 LLM session 可见”，但 `ITool` 是工具能力本身，不应携带某个具体 session 的视图状态。

这会导致几个直接问题：

- 同一个工具实例若被多个 session 共享，则 `Visible` 无法同时表达多个 session 的不同视图。
- 若为了隔离 `Visible` 而复制整组 `ITool`，又会把本不该复制的执行逻辑与潜在状态一起复制。
- `CompletionRequest.Tools` 在请求构造时虽然是值快照，但快照来源仍依赖工具实例上的可变字段，语义上不稳定。

### 2. `ToolExecutor` 混合了 registry 与 session state

当前 `ToolExecutor` 同时持有：

- 全量工具索引
- 全量工具定义数组
- 可见工具投影逻辑
- 执行序号

其中全量工具索引属于较长生命周期的注册表；可见工具投影和执行序号属于 session 生命周期；调度、日志、异常包装则更接近一个无状态服务或轻量 session 壳。

把这些状态揉在一起后，`ToolExecutor` 变得既不像 registry，也不像纯 executor，更不像 session，演化空间被挤压。

### 3. 工具执行函数缺少显式上下文入口

当前 `ToolExecutionRequest` 只有：

- `RawToolCall`
- `ExecutionSequence`

这意味着一旦工具逻辑需要拿到当前 session、当前 turn、宿主服务、共享状态容器等信息，最自然的做法就会变成“把这些状态 capture 在 tool 或 wrapper 绑定的实例里”。

这正是 session 状态向工具对象图回流的主要渠道。

### 4. `_definitionByInstance` 没有形成稳定价值

`ITool` 自己已经暴露了 `Definition`。如果仅仅是为了从实例回取 `ToolDefinition`，那么额外维护 `ITool -> ToolDefinition` 映射并不划算。

只有在“注册表要持有一份校验后冻结的 entry 快照，而不是直接信任运行期工具对象”这个前提下，才有必要保留独立的 entry 结构；此时也应显式引入 `RegisteredTool` 或类似类型，而不是继续使用 `_definitionByInstance` 这种过渡映射。

## 重构目标

### 核心目标

- 明确区分“全局共享的工具能力”和“某个 session 当前的工具视图”。
- 让 `CompletionRequest.Tools` 明确由 session 视图投影产生，而不是从 `ITool.Visible` 之类的全局可变字段侧向推断。
- 为工具最终执行函数引入统一的显式上下文对象，避免通过 deep clone `ITool` 来复制 session 状态。
- 让 `ToolExecutor` 收缩为职责单一的调度器，或被更清晰的类型替代。
- 为后续引入显式 `LlmSession` 打下稳定边界。

### 非目标

- 不以兼容旧 API 为优先目标。
- 不在本轮强行设计完整的 agent runtime 或 profile/runtime state 体系。
- 不要求一次性把所有历史调用点迁完；允许按阶段切换。
- 不要求本轮同时解决所有 tool authorization、安全策略和跨 app 生命周期问题，但接口形态应为这些扩展留位置。

## 设计原则

### 1. 让生命周期短的状态待在短生命周期对象里

- 工具定义与调度逻辑可以长期共享。
- 工具可见性、执行序号、临时 state bag、fork lineage 等应归属于 session。

### 2. 工具对象表达能力，不表达会话视图

`ITool` 应描述“能做什么”和“如何执行”，不描述“现在给哪个 session 看见”。

### 3. 请求可见性与执行授权必须共源

构建 `CompletionRequest.Tools` 时过滤一次并不够。执行层也必须依据同一份 session policy 校验工具是否允许被当前 session 调用。

否则模型即使未看到某个工具，仍可能 hallucinate 一个隐藏工具名；若执行层不再校验，就会出现“提示面上隐藏，运行时仍可执行”的边界漏洞。

### 4. 优先显式 context 注入，而不是隐式 capture

能通过 `ToolExecutionContext` 显式传入的信息，就不要再通过 wrapper 绑定实例、闭包 capture 或 deep clone tool graph 来传递。

## 推荐目标形态

建议把现有结构拆成 5 层。

### 1. `ToolRegistry`

职责：全局、不可变、可共享的工具注册表。

建议职责范围：

- 注册 `ITool`
- 校验 `ToolDefinition`
- 检测重名冲突
- 按工具名解析工具
- 提供全量定义快照

建议不要承担：

- session 可见性
- 执行序号
- tool state
- 当前 session 授权判断

建议草图：

```csharp
public sealed class ToolRegistry {
    public ImmutableArray<ToolDefinition> AllDefinitions { get; }

    public IEnumerable<RegisteredTool> Tools { get; }

    public bool TryGet(string toolName, out RegisteredTool tool);
}

public sealed record RegisteredTool(
    string Name,
    ToolDefinition Definition,
    ITool Tool);
```

这里若仍希望保留“注册时冻结过的 definition”，应显式体现在 `RegisteredTool` 上，而不是继续用 `_definitionByInstance`。

另外，当前 `ToolExecutor` 的工具名索引用的是 `StringComparer.OrdinalIgnoreCase`。若拆出 `ToolRegistry` / `ToolAccessPolicy`，名称解析、重名检测、policy 命中规则也应保持同一套比较语义，避免出现“prompt 投影命中了、执行授权没命中”或相反的大小写漂移。

### 2. `LlmSession`

职责：承载某个 LLM session 的工具视图与最小执行状态。

建议最小内容：

- `ToolAccessPolicy`
- 执行序号分配器
- 供 tool 使用的 session state bag 或 service provider 引用

建议草图：

```csharp
public sealed class LlmSession {
    public ToolAccessPolicy ToolAccess { get; }

    public ImmutableArray<ToolDefinition> GetVisibleToolDefinitions(ToolRegistry registry);

    internal long AllocateToolExecutionSequence();
}
```

如果本轮还不想正式引入完整 `LlmSession`，也可以先落一个更窄的 `ToolSessionState`，后续再并入更完整的 session 类型。

### 3. `ToolAccessPolicy`

职责：统一表达“当前 session 允许看见/调用哪些工具”。

MVP 版本完全可以从 hidden set 起步：

```csharp
public sealed class ToolAccessPolicy {
    public ISet<string> HiddenToolNames { get; }

    public bool IsVisible(string toolName);
    public bool IsExecutable(string toolName);
}
```

但建议从命名上避免把自己锁死在 `HiddenToolNames` 上。后续可能需要：

- workflow state 决定可见工具集
- 不可见但允许内部调用的工具
- 仅在特定阶段暴露的工具
- 按 capability tag 或 app 维度做批量授权

因此更稳妥的方向是让 session 持有 policy，而不是直接裸露 `HashSet<string>` 作为长期接口。

### 4. `ToolExecutionContext`

职责：成为所有工具执行函数统一可用的上下文入口。

建议直接扩展当前 `ToolExecutionRequest`，或者新建类型取代它。推荐承载：

- 当前 `LlmSession`
- `RawToolCall`
- `ExecutionSequence`
- 宿主服务访问入口
- 可选 `Items` / `Properties`
- 后续可能加入的 turn metadata

建议草图：

```csharp
public sealed record ToolExecutionContext(
    LlmSession Session,
    RawToolCall RawToolCall,
    long ExecutionSequence,
    IServiceProvider? Services = null,
    IReadOnlyDictionary<string, object?>? Items = null);
```

如果短期内不想把 `IServiceProvider` 带进来，也可以先保留一个自定义 `ToolServices` 容器。关键点是：工具拿上下文必须有统一入口，而不是继续靠 capture。

### 5. `SessionToolExecutor` 或 `ToolDispatcher`

职责：调度、授权、日志、异常治理、耗时统计。

建议草图：

```csharp
public sealed class SessionToolExecutor {
    public SessionToolExecutor(ToolRegistry registry, LlmSession session) { ... }

    public ImmutableArray<ToolDefinition> VisibleDefinitions
        => session.GetVisibleToolDefinitions(registry);

    public ValueTask<ToolCallExecutionResult> ExecuteAsync(
        RawToolCall request,
        CancellationToken cancellationToken);
}
```

它可以是每个 session 一个轻量对象，也可以是无状态 dispatcher + 调用时传入 session。两种形态都可以，关键是不要再把 registry state 和 session state 混放。

## 对现有接口的具体建议

### 1. `ITool`

目标建议：删除 `Visible`。

建议形态：

```csharp
public interface ITool {
    ToolDefinition Definition { get; }
    ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}
```

说明：

- `Visible` 删除后，`ITool` 回到能力对象本身。
- `ExecuteAsync` 参数改成显式 context，是本轮解耦的关键接口变化。

### 2. `ToolExecutor`

建议不要继续在原类型上堆更多 session 逻辑。

有两种可接受路线：

- 直接重命名并重构成 `ToolRegistry` + `SessionToolExecutor`
- 保留 `ToolExecutor` 名称，但把它明确收缩为 session 级轻量执行壳，内部只引用外部 `ToolRegistry`

不推荐的路线：

- 继续让 `ToolExecutor` 自己同时持有 `_tools`、`AllToolDefinitions`、`GetVisibleToolDefinitions()`、`_nextExecutionSequence`

### 3. `ToolExecutionRequest`

建议升级为真正的执行上下文类型，而不是保留当前薄壳。

如果希望减少命名震荡，可以保留名称，但语义上按 context 使用：

```csharp
public sealed record ToolExecutionRequest {
    public LlmSession Session { get; }
    public RawToolCall RawToolCall { get; }
    public long ExecutionSequence { get; }
    public IServiceProvider? Services { get; }
}
```

若允许破坏式调整，则更推荐直接更名为 `ToolExecutionContext`，减少误导。

### 4. `ArtifactToolWrapper<T>`

当前 handler 签名是：

```csharp
public delegate ValidateResult ArtifactHandler<T>(T artifact, long executionSequence) where T : class;
```

建议改为：

```csharp
public delegate ValidateResult ArtifactHandler<T>(T artifact, ToolExecutionContext context) where T : class;
```

原因：

- `executionSequence` 只是上下文中的一个字段，不应成为唯一注入信息。
- artifact tool 后续若需要读取 session metadata、service、host capability，就不必再修改 delegate 形状。

### 5. `MethodToolWrapper`

这是本轮真正需要下刀的另一处。

当前它允许直接绑定目标实例，这在语义上等同于“把运行态对象图焊进 tool 对象”。如果目标实例携带 session 或 app 状态，fork session 时就会诱导出复制 tool graph 的需求。

建议分两步演进：

#### 路线 A：先支持显式上下文参数

允许被包装的方法声明一个 `ToolExecutionContext` 参数，位置建议固定在 `CancellationToken` 前面。

例如：

```csharp
[Tool("memory.append", "向当前 session 的 memory notebook 追加文本")]
public ValueTask<ToolExecuteResult> AppendAsync(
    [ToolParam("要追加的内容")] string content,
    ToolExecutionContext context,
    CancellationToken cancellationToken)
```

这样很多原本需要 capture 的 session 信息，都可以通过 context 读取。

但这里有一个实现层面的前置条件需要显式写清楚：当前 `MethodToolWrapper.FromMethodImpl(...)` 会把除最后一个 `CancellationToken` 之外的所有参数都视为 provider-visible 输入参数，并且强制要求逐个标注 `[ToolParam]`。因此如果要支持 `ToolExecutionContext`，至少还需要同步完成这几件事：

- 把 `ToolExecutionContext` 识别为基础设施参数，而不是 schema 参数
- 对该参数跳过 `[ToolParam]` 强制要求
- 让内部 invoker 不再只是接收 `object?[] + CancellationToken`，而是能把 `ToolExecutionContext` 一并传进目标方法

换句话说，路线 A 不只是“放宽方法签名”，还要求同步调整参数扫描、schema 生成、以及表达式树 invoker 的形状；否则文档里的签名虽然看起来成立，实际实现上仍然走不通。

#### 路线 B：逐步限制直接绑定带状态实例

长期建议：

- 优先包装 static method
- 或引入 `targetResolver`，按 context 延迟解析目标实例

例如：

```csharp
public static MethodToolWrapper FromMethod(
    Func<ToolExecutionContext, object?> targetResolver,
    MethodInfo method,
    params object?[] formatArgs)
```

这能把“工具能力定义”和“当前 session 下该落到哪个运行态对象上”拆开。

## 构建 `CompletionRequest` 的目标路径

目标路径应明确为：

1. 宿主创建或持有一个共享 `ToolRegistry`
2. 每个 LLM session 持有自己的 `ToolAccessPolicy`
3. 发起一次 completion 前，由 session 从 registry 投影出 `VisibleToolDefinitions`
4. 用该投影构造 `CompletionRequest.Tools`
5. 模型返回 `RawToolCall` 后，由执行层再次按同一份 policy 检查是否允许执行

示意代码：

```csharp
var visibleTools = session.GetVisibleToolDefinitions(toolRegistry);

var request = new CompletionRequest(
    ModelId: modelId,
    SystemPrompt: systemPrompt,
    Context: history,
    Tools: visibleTools);

var executor = new SessionToolExecutor(toolRegistry, session);
var result = await executor.ExecuteAsync(rawToolCall, cancellationToken);
```

这条路径有两个重要性质：

- prompt 视图与 runtime authorization 使用同一份 session policy
- tool 实例本身无需因 session fork 而复制

## 关于是否要立即引入显式 `LlmSession`

建议答案是：应该，但可以分阶段落地。

### 方案一：本轮就引入 `LlmSession`

适合场景：

- 已经明确要支持 session fork
- 未来还会把更多 turn/runtime state 收拢到 session
- 希望这次顺手把“谁拥有 executionSequence”一次性理顺

优点：

- 名义清晰
- 后续扩展点稳定

代价：

- API 变动更明显
- 需要同步调整更多调用点

### 方案二：先落 `ToolSessionState`

适合场景：

- 本轮只想优先解决 tool visibility 与 context 注入
- 还不想过早承诺完整 `LlmSession` 的公共形状

建议形态：

```csharp
public sealed class ToolSessionState {
    public ToolAccessPolicy ToolAccess { get; }
    internal long AllocateExecutionSequence();
}
```

然后把它放进 `ToolExecutionContext`。未来若引入完整 `LlmSession`，再让 `ToolSessionState` 内联或合并进去。

从工程节奏看，若当前主线还没有稳定的 session 类型，我更推荐先上 `ToolSessionState`，但文档与命名要明确它只是通往 `LlmSession` 的过渡台阶，而不是新的长期中心类型。

## 分阶段迁移建议

### Phase 1: 先把可见性从 `ITool` 上剥离

目标：

- 删除 `ITool.Visible`
- 引入 `ToolAccessPolicy`
- 让 `CompletionRequest.Tools` 通过 registry + policy 投影得到

本阶段完成后，应满足：

- `ITool` 不再携带 session 视图状态
- 同一组共享工具可被多个 session 同时使用

### Phase 2: 引入执行上下文

目标：

- 扩展 `ToolExecutionRequest` 或替换为 `ToolExecutionContext`
- 让 `ToolExecutor` 在执行时显式传入 session/context
- 调整 `ArtifactToolWrapper<T>` 的 handler 签名
- 若当前主注册路径仍以 `MethodToolWrapper` 为主，则至少打通 wrapper 对 `ToolExecutionContext` 参数的基础注入

本阶段完成后，应满足：

- 新工具不必再通过 wrapper capture session 状态
- context 注入路径稳定下来

### Phase 3: 收缩或替换 `ToolExecutor`

目标：

- 拆出共享 `ToolRegistry`
- 将执行序号迁入 session state
- 把 `ToolExecutor` 收缩为轻量 session executor，或替换成 `SessionToolExecutor`

本阶段完成后，应满足：

- registry 与 executor 的生命周期边界清楚
- `_definitionByInstance` 可以删除，或被 `RegisteredTool` 明确替代

### Phase 4: 处理 `MethodToolWrapper` 的运行态绑定问题

目标：

- 支持 `ToolExecutionContext` 参数注入
- 逐步减少对直接绑定状态实例的依赖
- 必要时引入 `targetResolver`

若 Phase 2 已经完成 `ToolExecutionContext` 的基础参数注入，这一阶段就可以更聚焦于“运行态目标解析”本身，也就是把改造重点收敛到 `targetResolver`、static method 优先、以及带状态实例的长期收口策略，而不是把 context 注入和运行态绑定两个问题拖到同一阶段一起爆开。

本阶段完成后，应满足：

- session fork 不再要求 deep clone 整组 `ITool`
- tool 绑定模型更接近“能力定义 + 运行时上下文解析”

## 风险与取舍

### 1. 引入 `LlmSession` 可能导致边界膨胀

如果 `LlmSession` 一开始就承载过多 agent/runtime 语义，容易把一个本来聚焦于工具系统的问题，扩展成更大范围的 runtime 重构。

应对方式：

- 让本轮 `LlmSession` 只承载 tool 相关最小状态
- 其余状态继续待在原有类型，后续再逐步吸纳

### 2. `ToolAccessPolicy` 过早抽象可能显得“空”

如果现在只有 hidden set，一个 policy 类型看起来会比 `HashSet<string>` 更重。

但从长期演化看，这层抽象是值得的，因为它保证可见性投影与执行授权可以共享同一条规则路径。

### 3. `MethodToolWrapper` 改造会触及较多 API

这是本方案中最容易带来连锁改动的一环，但也是避免未来继续把状态 capture 到 tool graph 中的必要步骤。

建议不要在 Phase 1 就一次性做完，而是在 Phase 4 集中收口。

## 建议的最终判断

本轮推荐方向如下：

- 明确引入 session 级工具视图，不再让 `ITool` 表达可见性
- 用 `ToolAccessPolicy` 表达“当前 session 能看见和执行哪些工具”
- 将 `CompletionRequest.Tools` 明确改为 registry + session policy 的投影结果
- 将 `ToolExecutionRequest` 升级为真正的上下文对象
- 将 `ToolExecutor` 拆成共享 registry 与轻量 session executor 两层
- 将 `MethodToolWrapper` 的未来演进方向定为“显式 context 注入 + 延迟目标解析”，而不是继续复制绑定了状态实例的工具对象

若只做最小必要落地，建议优先顺序是：

1. 删 `ITool.Visible`
2. 引入 `ToolAccessPolicy`
3. 让 request 构建与执行授权都走 policy
4. 扩展 `ToolExecutionContext`
5. 再拆 `ToolRegistry` / `SessionToolExecutor`

这样能先解决最核心的 ownership 问题，再逐步清理执行管线中的历史耦合。

## 附：一版更接近目标态的最小接口草图

```csharp
public interface ITool {
    ToolDefinition Definition { get; }
    ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

public sealed record RegisteredTool(
    string Name,
    ToolDefinition Definition,
    ITool Tool);

public sealed class ToolRegistry {
    public ImmutableArray<ToolDefinition> AllDefinitions { get; }
    public bool TryGet(string toolName, out RegisteredTool tool);
}

public sealed class ToolAccessPolicy {
    public bool IsVisible(string toolName);
    public bool IsExecutable(string toolName);
}

public sealed class LlmSession {
    public ToolAccessPolicy ToolAccess { get; }
    public ImmutableArray<ToolDefinition> GetVisibleToolDefinitions(ToolRegistry registry);
    internal long AllocateToolExecutionSequence();
}

public sealed record ToolExecutionContext(
    LlmSession Session,
    RawToolCall RawToolCall,
    long ExecutionSequence,
    IServiceProvider? Services = null);

public sealed class SessionToolExecutor {
    public SessionToolExecutor(ToolRegistry registry, LlmSession session) { ... }

    public ImmutableArray<ToolDefinition> VisibleDefinitions
        => session.GetVisibleToolDefinitions(registry);

    public ValueTask<ToolCallExecutionResult> ExecuteAsync(
        RawToolCall request,
        CancellationToken cancellationToken);
}

public delegate ValidateResult ArtifactHandler<T>(
    T artifact,
    ToolExecutionContext context)
    where T : class;
```

这版接口并不要求立刻一次性全部实现，但它清晰表达了目标边界：

- registry 管共享能力
- session 管当前视图与执行状态
- context 管执行期注入
- executor 管调度与治理
- tool 自身只表达能力与执行逻辑

# MethodToolWrapper / ArtifactToolWrapper 统一化方案

## 背景

当前 `ArtifactToolWrapper<T>` 与 `MethodToolWrapper` 的职责相近：都将一段业务逻辑包装为 `ITool`，都基于 `RawToolCall.RawArgumentsJson` 构建输入对象并执行。

两者的主要差异在于“业务输入”的建模方式：

- `ArtifactToolWrapper<T>` 直接把整个 JSON 反序列化为 `T`，然后执行对象图校验，再调用 `ArtifactHandler<T>`。
- `MethodToolWrapper` 则把方法参数列表视为 schema 真源，先把 JSON 解析成扁平参数字典，再拆回 `object?[]`，最后用反射生成的 invoker 调 method。

这导致两个 wrapper 在内部维护了两套并行但高度相似的执行主链，也让 `MethodToolWrapper` 额外承担了一套扁平参数 schema / 默认值 / 反射调用逻辑。

本次重构目标是故意牺牲一部分“直接把多参数方法包成工具”的易用性，换取统一的数据模型、统一的运行时路径和更强的 schema 表达力。

## 目标

1. 统一 `MethodToolWrapper` 与 `ArtifactToolWrapper<T>` 的核心执行路径。
2. 将工具输入统一为“一个可反序列化的对象类型”。
3. 移除 `MethodToolWrapper` 对扁平参数列表和 `ToolParamAttribute` 的依赖。
4. 复用 `ReflectedToolDefinitionBuilder` 作为 object-input tool 的 schema 真源。
5. 本轮先不引入新的默认值运行时语义，避免 object-input runtime 与 omission/default injection 语义耦合。

## 非目标

1. 不保留旧的“任意多个离散业务参数”方法签名兼容层。
2. 不尝试把 `MethodToolWrapper` 和 `ArtifactToolWrapper<T>` 合并成同一个 public 类型。
3. 不扩展到与本次重构无关的 tool metadata 系统重写。

## 新的 MethodToolWrapper 契约

`MethodToolWrapper` 目标方法的签名收紧为：

```csharp
[Tool("tool.name", "Tool description.")]
public ValueTask<ToolExecuteResult> ExecuteAsync(
    MyToolInput input,
    ToolExecutionContext context,
    CancellationToken cancellationToken
)
```

约束如下：

1. 必须带有 `[Tool]`。
2. 返回类型必须为 `ValueTask<ToolExecuteResult>`。
3. 最后一个参数必须为 `CancellationToken`。
4. 倒数第二个参数必须为 `ToolExecutionContext`。
5. 除去上述两个基础设施参数后，必须且只能剩下一个业务输入参数。
6. 业务输入参数类型必须是可被 `ReflectedToolDefinitionBuilder` 支持的 concrete class / record class。

这意味着：

- `ToolParamAttribute` 不再参与 `MethodToolWrapper`。
- 业务输入的字段说明、必填性、范围、模式、枚举等，统一写在输入 DTO 的属性上。
- `MethodToolWrapper.FromDelegate(...)` 的泛型重载会大幅缩减，只保留符合新契约的少量形状。

## 输入对象的声明方式

`MethodToolWrapper` 与 `ArtifactToolWrapper<T>` 的业务输入对象统一采用 `ReflectedToolDefinitionBuilder` 支持的声明方式：

- 根对象和嵌套对象使用 class / record class。
- 工具字段说明来自 `[Description]`。
- JSON 字段名来自 `[JsonPropertyName]`。
- 校验来自 `DataAnnotations`，例如 `[Required]`、`[Range]`、`[StringLength]`、`[RegularExpression]`。

示例：

```csharp
[Description("Replace text inside the target buffer.")]
public sealed record class ReplaceTextInput(
    [property: Description("Exact old text to replace.")]
    string OldText,
    [property: Description("New text after replacement.")]
    string NewText
);

[Tool("text.replace", "Replace text inside the target buffer.")]
public ValueTask<ToolExecuteResult> ReplaceAsync(
    ReplaceTextInput input,
    ToolExecutionContext context,
    CancellationToken cancellationToken
)
```

## `[DefaultValue]` 的处理边界

`[DefaultValue]` 本轮明确不纳入实施范围。

原因不是它做不了，而是它的语义尚未收口：

1. 当前旧 `MethodToolWrapper` 的默认值语义属于“缺参后在调用前补值”。
2. 本轮计划中的共享 runtime 主链是 `ParseArguments -> Deserialize<TInput> -> ValidateObjectGraph -> Invoke`。
3. 在这条主链下，“schema 标注了 default”并不自动等于“反序列化后的 DTO 真拿到了默认值”。

因此本轮只统一 wrapper 契约与 object-input runtime，不新增 omission/default injection 行为。

后续若单独推进 `[DefaultValue]`，需要先明确它属于以下哪一种：

1. metadata-only：只进入 `ToolSchema.Value.Default` 与渲染文本，不影响 required / omission 语义。
2. runtime default injection：缺失字段在调用前被补成默认值。

## 统一后的内部结构

抽出一个共享的 object-input runtime 内核，职责如下：

1. 记录 `ToolDefinition` 与 `ToolSchema.Object`。
2. 用 `JsonArgumentParser.ParseArguments(...)` 做 schema 级 parse。
3. 对原始 JSON 做 `System.Text.Json` 反序列化。
4. 执行对象图 `DataAnnotations` 校验。
5. 统一构造 parse failure / deserialize failure / validation failure / parse warning。
6. 在成功时把反序列化结果与 `ToolExecutionContext` 交给具体 adapter。

建议的内部抽象形状：

```csharp
internal sealed class ObjectInputToolRuntime<TInput> where TInput : class
```

其核心依赖：

- `ToolDefinition Definition`
- `ToolSchema.Object InputSchema`
- `Func<TInput, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> Invoker`

执行主链：

1. `ParseArguments`
2. `Deserialize<TInput>`
3. `ValidateObjectGraph`
4. `Invoker(input, context, cancellationToken)`
5. `AttachParseWarning`

## 两个 wrapper 的职责划分

### ArtifactToolWrapper<T>

保留其 public 语义：“接收一个结构化产物，并通过 handler 决定是否接受”。

但内部改为复用共享 runtime，并通过 adapter 把旧的 `ArtifactHandler<T>` 结果转为 `ToolExecuteResult`：

```csharp
(input, context, cancellationToken) =>
{
    var result = handler(input, context);
    ...
}
```

### MethodToolWrapper

保留其 public 语义：“把带 `[Tool]` 的方法包装为 `ITool`”。

但内部不再自己构建扁平参数 schema，也不再使用 `object?[]` invoker。改为：

1. 反射验证方法签名是否符合新契约。
2. 取出唯一业务输入参数类型 `TInput`。
3. 调 `ReflectedToolDefinitionBuilder.Build(...)` 构建 input schema。
4. 复用共享 runtime。
5. 仅在最后一跳通过编译过的 delegate 调 method。

这样 `MethodToolWrapper` 的特有逻辑只剩“方法签名验证 + 目标方法调用适配”。

## ToolAttribute 与输入对象 Description 的职责分离

为避免 tool 元数据和 DTO 元数据混淆，约定如下：

1. tool name 与 tool-level description 继续来自 `[Tool(name, description)]`。
2. 输入 DTO 根级 `[Description]` 仅用于 object schema 描述，不覆盖 tool-level description。
3. `MethodToolWrapper` 在构建 `ToolDefinition` 时使用 `[Tool]` 的 description。
4. `ArtifactToolWrapper<T>` 沿用现状，tool description 仍来自输入类型根级 `[Description]`，因为它本身没有 `[Tool]` 方法壳。

另外需要补一个 builder 收口：

1. `ReflectedToolDefinitionBuilder` 当前会把根 `[Description]` 直接写入 `ToolDefinition.Description`。
2. 本轮要避免把这种“tool 说明”和“root object schema 说明”的语义继续混在一个 API 里。
3. 更稳妥的方向是让 builder 负责返回 schema，由 wrapper 自己决定 tool-level description 的来源。

这意味着两个 wrapper 的 tool-level description 来源仍略有不同，但对象字段 schema 的生成与校验规则将完全统一。

## 测试策略

本次测试迁移按真源收口：

1. 删除 `MethodToolWrapper` 旧的“扁平参数注入”断言。
2. 新增 `MethodToolWrapper` 单输入对象路径测试：
   - schema 由 DTO 反射生成
   - `ToolExecutionContext` 注入
   - deserialize failure
   - DataAnnotations validation failure
3. 保留并调整 `ArtifactToolWrapper` 测试，确认其仍走同一共享运行时语义。
4. 为 shared runtime 增加 parse / deserialize / validation 失败路径测试覆盖。

## 迁移步骤

### 工作包 A：收对外契约

1. 收紧 `MethodToolWrapper` 支持的目标方法签名。
2. 移除 `ToolParamAttribute` 在主线代码中的参与。
3. 调整 `FromDelegate(...)` 重载集。
4. 更新 `MethodToolWrapper` 测试到单输入对象模型。

### 工作包 B：抽共享 object-input runtime

1. 从 `ArtifactToolWrapper<T>` 中抽出 parse / deserialize / validate / warning 逻辑。
2. `ArtifactToolWrapper<T>` 改为复用共享 runtime。
3. `MethodToolWrapper` 改为复用共享 runtime。

### 工作包 C：测试尾修与文档同步

1. 统一失败消息与 warning 拼接风格。
2. 更新剩余测试和文档注释。
3. 做总体验证。

## 风险与应对

### 风险 1：`MethodToolWrapper` 易用性下降

这是预期内代价。本项目当前主线调用很少，处于早期阶段，适合趁现在收紧契约。

### 风险 2：共享 runtime 只统一了执行骨架，没有偷渡新的 binding 语义

本轮显式不处理 omission/default injection，避免“schema 可接受”和“DTO 最终长相”之间出现隐式分叉。

### 风险 3：`ArtifactToolWrapper<T>` 与 `MethodToolWrapper` 仍留下两套失败消息风格

共享 runtime 必须同时统一 parse failure / deserialize failure / validation failure / warning 拼接，避免只抽了一半。

## 结论

该重构在设计上合理，在当前代码基线下也可低风险推进。

在 `[DefaultValue]` 暂不纳入本轮的前提下，高层上没有新的阻断问题，建议直接按三阶段工作包实施：

1. 先收紧 `MethodToolWrapper` 契约。
2. 再抽共享 object-input runtime。
3. 最后补测试尾修与文档同步。

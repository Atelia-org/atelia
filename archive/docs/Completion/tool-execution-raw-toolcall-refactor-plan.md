# Completion Tool Execution RawToolCall 化重构计划

> **状态**：拟实施
> **最后更新**：2026-05-21
> **直接动机**：为 `ArtifactToolWrapper<T>` 提供自然落点，同时收紧当前 `ToolExecutor -> JsonArgumentParser -> IReadOnlyDictionary<string, object?> -> 各种 tool` 这条过渡执行链。

---

## 1. 结论摘要

这次重构在设计上是合理的，在实现上也是可行的。

建议采用的主路线是：

- 将 `ITool.ExecuteAsync(...)` 的输入从 `IReadOnlyDictionary<string, object?>? arguments` 改为 `RawToolCall request`
- 让 `ToolExecutor` 退回为“按工具名查找并分发原始调用”的统一调度器
- 将参数解析、强类型绑定、声明式校验收回各个 `ITool` 实现内部
- 其中：
  - `MethodToolWrapper` 负责把 `RawToolCall.RawArgumentsJson` 直接绑定为方法实参
  - `ArtifactToolWrapper<T>` 负责把 `RawToolCall.RawArgumentsJson` 直接反序列化为结构化产物 `T`

这条路线比“新增 `IContextualTool` 等可选接口”更统一，也比继续维持 `Dictionary<string, object?>` 过渡层更贴近最终目标态。

---

## 2. 为什么这条路线合理

### 2.1 `ArtifactToolWrapper<T>` 的天然输入就是 raw JSON

`ArtifactToolWrapper<T>` 想做的事，本质不是“接收一组松散参数”，而是：

- 声明一个结构化产物类型 `T`
- 向 provider 暴露对应 schema
- 在 tool call 到来时，把 raw JSON 还原成 `T`
- 交给业务 handler 做接收、校验、落库或继续处理

如果仍沿用 `IReadOnlyDictionary<string, object?>?`：

- 内部还需要把中间态重新组装回对象图
- 嵌套对象、数组、枚举、可空性、声明式验证都会变绕
- `ArtifactToolWrapper<T>` 的实现会天然比 `MethodToolWrapper` 更别扭

改为直接传 `RawToolCall` 后，这个类型的输入模型就和它的职责完全对齐了。

### 2.2 `ToolExecutor` 现在承担了不该由它承担的 schema 绑定职责

当前 `ToolExecutor` 不只做：

- 注册工具
- 校验重名
- 按名字调度执行

它还额外做了：

- 读取 tool schema
- 调用 `JsonArgumentParser`
- 把 raw JSON 解析成 `Dictionary<string, object?>`
- 拼装 parse warning / parse error

这使它知道了过多“各类工具如何消费参数”的细节。

而从职责上说：

- `ToolExecutor` 应该知道“如何把一个 `RawToolCall` 交给某个工具”
- 不应该知道“这个工具最终是按方法参数消费、按产物对象消费，还是以后按别的方式消费”

把绑定逻辑收回 tool 内部后，`ToolExecutor` 就能重新变成真正的统一执行入口，而不是事实上的“全局参数绑定器”。

### 2.3 这条路线和当前的 schema 收束方向一致

前面已经完成的收束是：

- `ToolDefinition.InputSchema` 成为唯一 schema 真源
- provider-visible schema 全部走 `ToolSchema`
- 旧 flat metadata / `ToolParamSpec` 已经删除

下一步自然就是：

- schema 仍由统一的 `ToolDefinition.InputSchema` 提供给 provider
- 但 schema 的消费方不再是 `ToolExecutor`
- 而是每个具体 tool 的内部 binder / deserializer / validator

这不是重新分裂 schema 真源，而是进一步收紧“谁拥有输入绑定语义”。

### 2.4 这条路线能避免 `object` 中间态成为长期协议

当前 `Dictionary<string, object?>` 有两个问题：

- 它只是过渡表示，不是任何业务方真正想要的最终形态
- 它会把标量、数组、对象都压成 runtime `object` 图，后续要么装箱，要么二次转换

对 `MethodToolWrapper` 来说，这个中间态还算勉强可用，因为它的签名目前主要是 top-level flat 标量参数。
但对 `ArtifactToolWrapper<T>` 来说，这个中间态明显不自然。

因此，把 raw JSON 直接交给具体 tool，避免 `object` 中间态成为公共执行协议，是及时且正确的。

---

## 3. 这条路线可行，但要明确几个设计边界

### 3.1 `RawToolCall` 不是最终 context，只是当前阶段足够好的执行输入

本轮不必急着引入 `ArtifactExecutionContext`、`InvocationSequence` 等上下文对象。

当前阶段直接把 `ITool.ExecuteAsync(...)` 改成接收 `RawToolCall`，已经足够解决两个核心问题：

- `ArtifactToolWrapper<T>` 能自然消费 raw JSON
- `ToolExecutor` 不再承担全局参数绑定职责

未来如果确实需要：

- 统一序号
- 时间戳
- 额外 trace 信息
- 调用源上下文

再给 `ITool.ExecuteAsync(...)` 增加 context 参数，或再从 `RawToolCall` 升级到更完整的 request/context 类型，也仍然是相对独立的下一步。

### 3.2 `MethodToolWrapper` 不必追求“一步到位支持任意嵌套 CLR 方法签名”

这次重构后，`MethodToolWrapper` 仍可保持当前设计边界：

- 继续只面向带 `ToolAttribute` / `ToolParamAttribute` 的方法
- 继续主要支持 top-level flat CLR 参数签名
- 但把内部绑定入口从 `Dictionary<string, object?>` 改成 raw JSON 直接绑定

也就是说，本轮重点是：

- 更换执行协议
- 收回绑定职责

而不是立刻把 `MethodToolWrapper` 扩展成“任意复杂 schema 到任意 CLR 方法签名”的通用绑定器。

### 3.3 `JsonArgumentParser` 不一定立即删除，但应退出 `ToolExecutor` 主链

这轮之后，`JsonArgumentParser` 最理想的定位是二选一：

- 要么被 `MethodToolWrapper` 内部继续复用
- 要么被更贴近 raw JSON 的 binder 替代

但无论如何，它都不应继续挂在 `ToolExecutor` 主链上，作为所有 tool 的统一预处理器。

### 3.4 声明式验证要区分两层

对 `ArtifactToolWrapper<T>`，建议明确区分：

- schema/transport 层验证
  - JSON 是否可解析
  - 是否满足 `ToolDefinition.InputSchema`
- object/model 层验证
  - `DescriptionAttribute`
  - `Required`
  - `StringLength`
  - `Range`
  - `RegularExpression`
  - 将来若有其他 data annotations 也可扩展

这样可以避免把“provider-visible schema 约束”和“本地对象模型校验”混成一层错误信息。

---

## 4. 建议目标态

### 4.1 `ITool`

目标接口：

```csharp
public interface ITool {
    ToolDefinition Definition { get; }
    bool Visible { get; set; }
    ValueTask<ToolExecuteResult> ExecuteAsync(RawToolCall request, CancellationToken cancellationToken);
}
```

语义变化：

- `Definition` 继续提供 provider-visible metadata
- `ExecuteAsync(...)` 改为直接接收原始工具调用
- 每个 tool 对“如何消费 raw arguments JSON”各自负责

### 4.2 `ToolExecutor`

目标职责：

- 按 `Definition.Name` 注册工具
- 按 `request.ToolName` 查找 tool
- 负责缺失工具、异常、取消、耗时统计、结果封装
- 不再负责：
  - 参数解析
  - schema 到 runtime object graph 的绑定
  - parse warning 合并

也就是说，`ToolExecutor` 的主链只做调度与执行治理，不再做绑定。

### 4.3 `MethodToolWrapper`

目标职责：

- 继续通过反射和 expression tree 构造 `ToolDefinition`
- 在 `ExecuteAsync(RawToolCall, ...)` 内部完成：
  - raw JSON 解析
  - 按方法签名绑定参数
  - 缺失参数 / 类型不符 / 默认值 / 可空性处理
  - 反射 delegate 调用

优先目标不是“零装箱”，而是：

- 不再把 `Dictionary<string, object?>` 暴露为工具执行协议
- 将绑定语义内聚到 `MethodToolWrapper`

若在实现中能顺手减少部分装箱或中间态分配，这是额外收益，但不应反过来绑架设计。

### 4.4 `ArtifactToolWrapper<T>`

目标职责：

- 用声明式对象类型 `T` 生成 `ToolDefinition`
- 在 `ExecuteAsync(RawToolCall, ...)` 内部完成：
  - raw JSON 解析
  - 反序列化为 `T`
  - 基本声明式验证
  - 调用 `ArtifactHandler<T>`
  - 返回适合 tool loop 的 `ToolExecuteResult`

建议它的最小可用 handler 仍保持简单，例如：

```csharp
public delegate ValidateResult ArtifactHandler<T>(T artifact) where T : notnull;
```

或若希望保留当前“同一工具可多次产出产物”的演化空间，也可以暂时维持现有签名，后续再独立升级。

关键点不是 handler 最终长什么样，而是：

- `ArtifactToolWrapper<T>` 应自己拥有 raw JSON -> `T` 的闭环

---

## 5. 与当前方案相比的主要收益

### 5.1 对业务侧更自然

业务侧会得到两类非常直观的工具包装器：

- `MethodToolWrapper`
  - “把一个方法暴露成 tool”
- `ArtifactToolWrapper<T>`
  - “把一个结构化产物提交通道暴露成 tool”

它们都直接从原始 tool call 开始工作，各自完成内部绑定，不再共享一个对双方都不够自然的 `Dictionary<string, object?>` 协议。

### 5.2 对内部实现更收口

过去的执行链是：

`ToolExecutor -> JsonArgumentParser -> object graph -> tool`

建议目标链是：

`ToolExecutor -> tool-specific binder/deserializer -> handler/invoker`

这使“执行协议”和“绑定实现”解耦得更清楚。

### 5.3 为未来新增 tool 包装器留下空间

将来若出现新的 tool 类型，例如：

- 基于 record 的 command wrapper
- 面向 patch / AST / diff 的专用 wrapper
- 带额外 local validation 的 wrapper

它们都可以直接实现：

- `Definition`
- `ExecuteAsync(RawToolCall, ...)`

而不需要先向全局 executor 申请一种新的中间参数表示。

---

## 6. 主要风险与对应对策

### 6.1 风险：解析错误格式可能发生漂移

当前 parse failure 的报错、warning 拼接和测试断言很多都建立在 `JsonArgumentParser` 输出格式上。

对策：

- 明确把“错误文本稳定性”视为迁移项，而不是副作用
- 在第一阶段先让 `MethodToolWrapper` 尽量复用现有错误表达
- 待新主链稳定后，再判断哪些错误文本该保留、哪些可以更贴近具体 wrapper 语义

### 6.2 风险：`MethodToolWrapper` 实现复杂度会短期上升

这是事实，但也是正确的复杂度归位。

过去复杂度被放在了 `ToolExecutor` 和全局 parser 中；
现在只是把它移回拥有方法签名知识的地方。

对策：

- 先支持现有签名能力，不同时扩张功能面
- 优先迁移与现有测试覆盖一致的场景
- 让 binder 内部以“逐参数 schema + 逐参数读取”方式实现，而不是一次性追求通用对象映射器

### 6.3 风险：`ArtifactToolWrapper<T>` 若直接依赖 `JsonSerializer.Deserialize<T>`，可能与 `ToolSchema` 约束存在双轨

例如：

- `ToolSchema` 认为某字段 required
- 但 CLR 反序列化阶段只是给出默认值

对策：

- 在 `ArtifactToolWrapper<T>` 内部先执行 schema 约束校验，再执行对象反序列化
- 或反过来先反序列化，再补做基于 schema 与 data annotations 的显式校验
- 实现上允许先采用最小可行策略，但文档里要明确“双层验证”是设计目标

---

## 7. 建议工作包拆分

### 工作包 A：执行协议切换到底层接口

目标：

- 将 `ITool.ExecuteAsync(...)` 改为接收 `RawToolCall`
- 全仓修正所有 `ITool` 实现、测试替身和调用点

重点文件：

- `prototypes/Completion.Tools/ITool.cs`
- `prototypes/Completion.Tools/ToolExecutor.cs`
- `tests/Atelia.LiveContextProto.Tests/*`
- `prototypes/TextAdv/*` 中直接实现 `ITool` 的少量类型

完成定义：

- 代码库中不再存在 `ExecuteAsync(IReadOnlyDictionary<string, object?>?, ...)` 签名

### 工作包 B：将 `ToolExecutor` 收缩为纯调度器

目标：

- 删除 `ToolExecutor` 中的 `ResolveToolCall(...)`
- 删除其对 `JsonArgumentParser` / `ToolArgumentParsingResult` 的主链依赖
- 保留缺失工具、异常、取消、耗时统计、结果封装

重点文件：

- `prototypes/Completion.Tools/ToolExecutor.cs`

完成定义：

- `ToolExecutor` 不再读取 `definition.InputSchema` 来做运行时参数绑定

### 工作包 C：`MethodToolWrapper` 内聚 raw JSON 绑定

目标：

- 将 raw JSON 到方法参数的绑定迁入 `MethodToolWrapper`
- 保持当前 `ToolAttribute` / `ToolParamAttribute` 驱动的 metadata 生成方式
- 保持现有 flat CLR 方法签名支持面

建议实现顺序：

1. 先保留现有 schema 生成逻辑
2. 新增 wrapper 内部的 raw request binder
3. 删掉旧的 `ArgGetter.GetValue(arguments)` 路径
4. 更新测试从“executor 解析成功”迁到“wrapper 自行绑定成功”

完成定义：

- `MethodToolWrapper` 不再依赖外部提供的 `Dictionary<string, object?>`

### 工作包 D：落地 `ArtifactToolWrapper<T>` 的最小可用实现

目标：

- 基于声明式产物类型 `T` 构造 `ToolDefinition`
- 在 wrapper 内部完成 raw JSON -> `T` -> 基本校验 -> handler 调用

建议前置：

- 尽量复用或迁移现有 `ReflectedToolDefinitionBuilder`
- 明确本轮支持的对象声明边界

完成定义：

- `ArtifactToolWrapper<T>` 可被真实注册到 `ToolExecutor` 中并跑通 end-to-end

### 工作包 E：清理 parser 边角与文档

目标：

- 根据 `MethodToolWrapper` 的最终实现，决定：
  - `JsonArgumentParser` 是否保留为内部 helper
  - `ToolArgumentParsingResult` 是否仍需要
- 更新 `docs/Completion` 中对执行链的描述

重点文档：

- `docs/Completion/memory-notebook.md`
- `docs/Completion/quick-start.md`

完成定义：

- 文档不再把 “ToolExecutor 统一把 RawToolCall 解析为 Dictionary” 描述为现行架构

---

## 8. 测试策略

### 8.1 `MethodToolWrapper` 测试迁移重点

新增或改写的测试应覆盖：

- required / optional / default value
- nullable / non-nullable
- 类型不匹配
- 缺失必填参数
- 额外未知参数
- 嵌套 schema 若当前不支持，应断言为明确失败
- tool executor 能成功把 `RawToolCall` 原样交给 wrapper

### 8.2 `ArtifactToolWrapper<T>` 测试重点

至少覆盖：

- 正常反序列化为 `T`
- 缺失 required 字段
- data annotations 校验失败
- handler 返回验证失败
- 工具结果文本对 LLM 足够可读

### 8.3 `ToolExecutor` 回归重点

应保留的回归保证：

- 未找到工具
- 工具抛异常
- 取消
- `ToolCallId` / `ToolName` 保持原样回灌
- elapsed 统计仍正常

---

## 9. 实施建议

建议按以下顺序推进：

1. 先改 `ITool` 执行协议与 `ToolExecutor` 主链
2. 再迁 `MethodToolWrapper`
3. 然后落地 `ArtifactToolWrapper<T>`
4. 最后清理 parser 边角、测试和文档

这个顺序的好处是：

- 先把“谁负责绑定”这个核心边界改对
- 再让两个 wrapper 分别落回自己的自然职责
- 避免先写出一个依赖旧执行协议的 `ArtifactToolWrapper<T>`，随后又重改一轮

---

## 10. 结论

把 `ITool.ExecuteAsync(...)` 改成直接接收 `RawToolCall`，并把绑定逻辑下放到各个具体 tool 内部，是一条值得走的重构路线。

它的主要价值不在于“少一个中间对象”，而在于：

- 把执行协议改回真正稳定、统一的原始输入
- 把绑定复杂度移回最知道自己该如何消费输入的地方
- 为 `ArtifactToolWrapper<T>` 提供自然、长期可持续的实现基础

因此，建议把这条路线作为 `ArtifactToolWrapper<T>` 落地前的前置重构来实施。

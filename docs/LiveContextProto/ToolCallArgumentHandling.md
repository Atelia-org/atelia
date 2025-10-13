# LiveContextProto 工具参数解析策略

## 背景

LiveContextProto 的工具调用链路包含三个关键参与者：

1. **LLM 模型**：按照工具声明输出 `tool_call` JSON 参数
2. **Provider 层**：解析模型输出，构造 `ToolCallRequest`
3. **工具实现**：消费 `ToolCallRequest` 并执行具体业务逻辑

早期版本采用“逐个参数原样传入字符串”的策略，灵活但缺少统一的解析规则。为了在保持兼容性的前提下提升解析质量，我们采用“宽进严出”方案，对参数解析职责做出明确划分。

## 宽进严出方案概述

- **工具约定**：`ToolCallRequest.Arguments` 中的参数统一采用 `object?` 形式，即弱类型化数据结构，允许天然携带布尔、数值、对象、数组等值。
- **Provider 责任**：Provider 根据工具声明/约定“尽力而为”地将模型输出解析成目标类型，并在解析过程中记录问题：
  - 成功解析 → 将结构化值写入 `Arguments`
  - 轻微偏差（如字符串包裹了布尔值）→ `Arguments` 写入修正后的值，同时将说明写入 `ParseWarning`
  - 解析失败 → 保留原始 JSON 文本，写入 `ParseError`
- **工具行为**：优先读取 `Arguments` 字典中的结构化值；若发现 `ParseError`/`ParseWarning`，可选择降级到 `RawArguments` 自行解析或向上抛错。

整体目标是在不中断调用的情况下尽量提供类型安全的数据，同时保留原始输入以便调试或容错。

## 数据结构更新

`ToolCallRequest` record 改为：

```csharp
internal record ToolCallRequest(
    string ToolName,
    string ToolCallId,
    string RawArguments,
    IReadOnlyDictionary<string, object?>? Arguments,
    string? ParseError,
    string? ParseWarning
);
```

字段含义：

- **ToolName / ToolCallId**：保持不变
- **RawArguments**：模型输出的原始 JSON 字符串，永远保留
- **Arguments**：解析后的参数字典，值为 `object?`，支持嵌套结构（`Dictionary<string, object?>` / `List<object?>`）
- **ParseError**：严重错误描述（如 JSON 解析失败/结构非对象）。出现错误时，`Arguments` 可能为 `null` 或部分可用
- **ParseWarning**：轻量提示，例如从字符串提升为布尔值时的说明，多个警告以 `; ` 分隔

## Provider 解析流程

### JSON 解析与结构化构建

1. 解析 `RawArguments` 为 `JsonDocument`
2. 要求根节点必须是对象；否则写入 `ParseError`
3. 遍历属性时，将 `JsonElement` 转换为 C# 值：
   - `String` → 先尝试类型提升（bool/null 等），否则原样字符串
   - `Number` → 优先 `long`，其次 `double`，最后 `decimal`
   - `True/False` → `bool`
   - `Null` → `null`
   - `Object` → 递归为 `Dictionary<string, object?>`
   - `Array` → 递归为 `List<object?>`
4. 发生类型提升时，将提示写入警告集合，最终合并成 `ParseWarning`
5. 遇到解析异常时写入 `ParseError`，保留原始文本

### 轻量级类型提升示例

| 输入文本 | 提升后类型 | 警告内容 |
|----------|------------|----------|
| `"true"` | `bool true` | `string literal converted to boolean true` |
| `"false"` | `bool false` | `string literal converted to boolean false` |
| `"null"` | `null` | `string literal converted to null` |

后续可扩展更多策略（例如数字字符串自动转换为数值），保持“宽进严出”的风格。

## 工具端处理建议

1. **优先读取 `Arguments`：**
   ```csharp
   if (request.Arguments is { } args && args.TryGetValue("count", out var value) && value is long count) {
       // 使用解析后的类型安全值
   }
   ```
2. **检查 `ParseWarning`：** 用于记录模型输出的轻微偏差：
   ```csharp
   if (!string.IsNullOrEmpty(request.ParseWarning)) {
       // 可选择记录日志或附加到工具结果中
   }
   ```
3. **兜底 `ParseError`：** 如果需要完全保证类型安全，可在发现 `ParseError` 时拒绝执行：
   ```csharp
   if (!string.IsNullOrEmpty(request.ParseError)) {
       return ToolHandlerResult.Failed($"参数解析失败: {request.ParseError}");
   }
   ```
4. **保留 RawArguments：** 需要灵活解析或兼容旧逻辑时可回退到原始字符串：
   ```csharp
   var fallbackJson = request.RawArguments;
   ```

## 面向未来的扩展

- **Schema 驱动解析**：未来可引入工具声明（JSON Schema）强化类型提示，Provider 根据 schema 精确转换。
- **数值/日期提升**：在警告策略中增加数值与日期字符串的自动转换。
- **多 Provider 复用**：其它 Provider（OpenAI/Stub 等）应复用同一解析逻辑，保持行为一致。

## 总结

“宽进严出”策略在保持 LLM 输出的宽容度的同时，为工具实现提供结构化、可追踪的参数视图。通过 `Arguments + ParseWarning + ParseError + RawArguments` 的组合，工具可以根据自身对类型安全的需求灵活决定使用策略，从而兼顾鲁棒性与开发效率。

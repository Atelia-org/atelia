# Anthropic Messages API v1 Tool 参数规范详解

## 概述

本文档详细讲解 Claude（基于 Anthropic Messages API v1）如何理解和使用 tool 调用中的参数，以及这种参数传递机制与标准 C# 方法参数的本质区别。

## 核心差异：JSON Schema vs. 强类型签名

### 标准 C# 参数

```csharp
// C# 方法签名 - 编译时强类型检查
public async ValueTask<Result> SearchFiles(
    string pattern,           // 必填，编译时类型保证
    bool caseSensitive = true, // 可选，有编译时默认值
    int maxResults = 100,     // 可选，编译时默认值
    CancellationToken cancellationToken = default
) { ... }
```

**特点：**
- 编译时类型安全
- 参数位置固定（位置参数）
- 默认值在编译时已确定
- 类型转换由编译器保证
- 调用者必须按照签名传递参数

### Anthropic Tool 参数

```json
{
  "name": "search_files",
  "description": "在工作区中搜索文件",
  "input_schema": {
    "type": "object",
    "properties": {
      "pattern": {
        "type": "string",
        "description": "要搜索的 glob 模式"
      },
      "caseSensitive": {
        "type": "boolean",
        "description": "是否区分大小写"
      },
      "maxResults": {
        "type": "integer",
        "description": "返回的最大结果数"
      }
    },
    "required": ["pattern"]
  }
}
```

**特点：**
- 运行时 JSON 解析与验证
- 参数是键值对（named arguments only）
- 无位置概念，纯粹基于名称匹配
- 需要运行时类型转换和校验
- LLM 可能发送任何 JSON 数据

## Anthropic Tool 参数的工作原理

### 1. 参数定义（input_schema）

Anthropic Messages API 要求所有 tool 必须提供符合 JSON Schema 的 `input_schema`：

```json
{
  "type": "object",
  "properties": {
    "paramName": {
      "type": "string|number|integer|boolean|object|array",
      "description": "参数描述（必须清晰，影响 LLM 理解）",
      "enum": ["value1", "value2"]  // 可选：枚举约束
    }
  },
  "required": ["param1", "param2"]  // 必填参数列表
}
```

**关键点：**
- `type: "object"` 是根节点，所有参数都在 `properties` 中
- `description` 不是可选的 - LLM 依赖它来理解参数用途
- `required` 数组明确哪些参数必填
- 不支持参数重载或多态

### 2. 参数传递（tool_use 消息）

当 Claude 决定调用 tool 时，会生成如下消息：

```json
{
  "type": "tool_use",
  "id": "toolu_01ABC123",
  "name": "search_files",
  "input": {
    "pattern": "**/*.cs",
    "maxResults": 50
    // 注意：caseSensitive 未提供（可选参数）
  }
}
```

**传递特性：**
- `input` 始终是 JSON 对象（即使空参数也是 `{}`）
- LLM 可能省略可选参数
- LLM 可能发送未定义的参数（需要容错）
- 参数名称大小写敏感（但实现可以选择不敏感）

### 3. 参数解析（运行时）

Atelia 的 `ToolArgumentParser` 实现了完整的解析流程：

```csharp
// 1. JSON 反序列化
using var document = JsonDocument.Parse(rawArguments);

// 2. 逐个参数验证与转换
foreach (var property in element.EnumerateObject()) {
    if (!lookup.TryGetValue(property.Name, out var parameter)) {
        // 未知参数：记录警告但保留值
        warnings.Add($"unknown_parameter:{property.Name}");
        continue;
    }

    // 3. 基于参数元数据进行类型转换
    var parsed = ParseValue(parameter, property.Value);
}

// 4. 检查必填参数
foreach (var parameter in tool.Parameters) {
    if (parameter.IsRequired && !arguments.ContainsKey(parameter.Name)) {
        errors.Add($"missing_required:{parameter.Name}");
    }
}
```

## 关键差异详解

### 差异 1：类型系统

| 维度 | C# 参数 | Anthropic Tool 参数 |
|------|---------|---------------------|
| 类型定义 | CLR 类型系统 | JSON Schema 类型 |
| 类型检查 | 编译时 | 运行时 |
| 自定义类型 | 支持任意类或结构体 | 仅支持 JSON 原始类型 + object/array |
| 泛型 | 完全支持 | 不支持 |
| 可空性 | `T?` 语法 | `required` 数组控制 |

**示例：**

```csharp
// C# - 自定义类型参数
public async ValueTask<Result> UpdateConfig(
    ConfigSettings settings  // 强类型对象
) { ... }

// Anthropic - 必须扁平化或使用 JsonObject
{
  "properties": {
    "settings": {
      "type": "object",
      "description": "配置对象（需详细描述每个字段）",
      "properties": {
        "timeout": { "type": "integer" },
        "retries": { "type": "integer" }
      }
    }
  }
}
```

### 差异 2：默认值处理

| 维度 | C# 参数 | Anthropic Tool 参数 |
|------|---------|---------------------|
| 默认值定义 | 方法签名中 `param = value` | 无原生支持 |
| 默认值语义 | 编译器自动注入 | 需运行时代码处理 |
| 可选参数 | 通过默认值表达 | 通过不在 `required` 中表达 |

**Atelia 的处理策略：**

```csharp
// C# 方法
public async ValueTask<Result> Search(
    [ToolParameter] string pattern,
    [ToolParameter] bool caseSensitive = true  // 默认值
) { ... }

// 映射逻辑
record ArgGetter(string Name, bool HasDefault, object? DefaultValue) {
    public object? GetValue(IReadOnlyDictionary<string, object?>? arguments) {
        if (arguments.TryGetValue(Name, out var ret)) {
            return ret;  // LLM 提供了值
        }
        if (HasDefault) {
            return DefaultValue;  // 使用 C# 默认值
        }
        throw new ArgumentException(Name);  // 必填但缺失
    }
}
```

**重要：** JSON Schema 本身不传递默认值给 LLM，默认值仅在服务端执行时生效。

### 差异 3：参数传递方式

| 维度 | C# 参数 | Anthropic Tool 参数 |
|------|---------|---------------------|
| 位置参数 | 支持 | 不支持 |
| 命名参数 | 可选 | 强制（唯一方式） |
| 参数顺序 | 重要（位置参数） | 无关（JSON 对象无序） |
| 可变参数 | `params T[]` | 需显式数组参数 |

**示例：**

```csharp
// C# 调用
await SearchFiles("*.cs", maxResults: 10);  // 混合位置和命名

// Anthropic 调用（LLM 生成）
{
  "input": {
    "maxResults": 10,
    "pattern": "*.cs"
    // 顺序任意
  }
}
```

### 差异 4：类型强制转换

Anthropic 的 JSON 传输导致大量隐式类型转换场景：

```csharp
// ToolArgumentParser 的容错逻辑
private static ParseResult ParseInteger(JsonElement element) {
    if (element.ValueKind == JsonValueKind.Number) {
        if (element.TryGetInt64(out var integer)) {
            return ParseResult.CreateSuccess(integer);
        }

        // LLM 发送了浮点数，但参数要求整数
        if (element.TryGetDouble(out var number)) {
            var rounded = Math.Truncate(number);
            var warning = Math.Abs(number - rounded) > double.Epsilon
                ? "fractional_number_truncated_to_integer"  // 3.14 -> 3
                : "number_coerced_to_integer";              // 3.0 -> 3
            return ParseResult.CreateSuccess((long)rounded, warning);
        }
    }

    // LLM 发送了字符串 "123"
    if (element.ValueKind == JsonValueKind.String) {
        var text = element.GetString();
        if (long.TryParse(text, out var integer)) {
            return ParseResult.CreateSuccess(integer, "string_literal_converted_to_integer");
        }
    }

    return ParseResult.CreateError("unsupported_integer_literal");
}
```

**C# 不会遇到的场景：**
- LLM 可能为数字参数发送字符串 `"42"`
- LLM 可能为布尔参数发送数字 `1` / `0`
- LLM 可能为数组参数发送单个值（需要包装）
- LLM 可能发送 `null` 给非可选参数

### 差异 5：错误处理

| 场景 | C# 参数 | Anthropic Tool 参数 |
|------|---------|---------------------|
| 类型不匹配 | 编译错误 | 运行时解析错误 |
| 缺少必填参数 | 编译错误 | 运行时验证错误 |
| 多余参数 | 编译错误 | 运行时警告（可容忍） |
| 参数名拼写错误 | 编译错误 | 运行时错误（视为缺失） |

**Atelia 的错误策略：**

```csharp
public sealed record ToolCallRequest(
    string ToolName,
    string ToolCallId,
    string RawArguments,
    ImmutableDictionary<string, object?> Arguments,
    string? ParseError,   // 阻止执行的错误
    string? ParseWarning  // 不阻止执行的警告
);

// 示例错误
"missing_required:pattern"
"enum_out_of_range:invalid_mode"
"json_parse_error:Unexpected character"

// 示例警告
"unknown_parameter:extraParam"
"string_literal_converted_to_integer"
"scalar_coerced_to_list"
```

## 高级特性

### 1. 基数（Cardinality）

Atelia 扩展了 JSON Schema 的语义：

```csharp
public enum ToolParameterCardinality {
    Single,    // 标量值
    Optional,  // 可为 null 的标量
    List,      // 数组
    Map        // 对象/字典
}
```

**List 的特殊处理：**

```csharp
// LLM 可能发送单个值而非数组
{
  "fileNames": "single.txt"  // 应该是数组
}

// 自动包装
private static ParseResult ParseList(ToolParameter parameter, JsonElement element) {
    if (element.ValueKind != JsonValueKind.Array) {
        var coerced = ParseScalar(parameter, element);
        if (!coerced.IsSuccess) return coerced;
        return ParseResult.CreateSuccess(
            ImmutableArray.Create(coerced.Value),
            "scalar_coerced_to_list"  // 警告但允许
        );
    }
    // ...
}
```

### 2. 枚举约束（Enum）

```csharp
public sealed class ToolParameterEnumConstraint {
    public IReadOnlySet<string> AllowedValues { get; }
    public bool CaseSensitive { get; }

    public bool Contains(string value) {
        if (CaseSensitive) return AllowedValues.Contains(value);
        else return LoweredAllowedValues.Contains(value.ToLower());
    }
}
```

**映射到 JSON Schema：**

```json
{
  "properties": {
    "mode": {
      "type": "string",
      "enum": ["read", "write", "append"],
      "description": "文件打开模式"
    }
  }
}
```

### 3. 复杂类型（JsonObject / JsonArray）

```csharp
public enum ToolParameterValueKind {
    String,
    Boolean,
    Integer,
    Number,
    JsonObject,   // ImmutableDictionary<string, object?>
    JsonArray,    // ImmutableArray<object?>
    // ...
}
```

**使用场景：**

```csharp
// C# 方法
public async ValueTask<Result> UpdateMultiple(
    [ToolParameter(ValueKind = ToolParameterValueKind.JsonObject)]
    ImmutableDictionary<string, object?> updates
) {
    // updates 可能包含任意嵌套结构
}

// LLM 调用
{
  "input": {
    "updates": {
      "config.timeout": 5000,
      "config.retries": 3,
      "nested": {
        "key": "value"
      }
    }
  }
}
```

## 实现桥接：C# 方法 → Tool 定义

Atelia 使用反射和特性（Attribute）自动生成 tool 定义：

```csharp
[Tool("search_files", Description = "在工作区中搜索文件")]
public static async ValueTask<LodToolExecuteResult> SearchFiles(
    [ToolParameter(Description = "glob 模式")]
    string pattern,

    [ToolParameter(Description = "是否区分大小写")]
    bool caseSensitive = true,

    [ToolParameter(Description = "最大结果数")]
    int maxResults = 100,

    CancellationToken cancellationToken = default
) {
    // ...
}
```

**生成的 `ITool` 实现：**

```csharp
class MethodWrapperTool : ITool {
    private readonly Delegate _delegate;
    private readonly IReadOnlyList<ArgGetter> _argGetters;

    public async ValueTask<LodToolExecuteResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken
    ) {
        // 1. 从 arguments 字典构造 C# 参数数组
        var args = BuildArgs(_argGetters, arguments, cancellationToken);

        // 2. 通过反射调用原始方法
        var task = (ValueTask<LodToolExecuteResult>)_delegate.DynamicInvoke(args)!;
        return await task;
    }

    private static object?[] BuildArgs(
        IReadOnlyList<ArgGetter> getters,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken
    ) {
        var args = new object?[getters.Count + 1];
        for (int i = 0; i < getters.Count; ++i) {
            args[i] = getters[i].GetValue(arguments);  // 处理默认值
        }
        args[^1] = cancellationToken;
        return args;
    }
}
```

## 最佳实践

### 1. 参数设计

```csharp
// ✅ 推荐：简单、明确的参数
[Tool("read_file")]
public static async ValueTask<Result> ReadFile(
    [ToolParameter(Description = "文件的绝对路径")]
    string filePath,

    [ToolParameter(Description = "起始行号（从 1 开始）")]
    int? startLine = null,

    CancellationToken cancellationToken = default
) { ... }

// ❌ 避免：复杂嵌套结构
[Tool("complex_update")]
public static async ValueTask<Result> ComplexUpdate(
    [ToolParameter(ValueKind = ToolParameterValueKind.JsonObject)]
    ImmutableDictionary<string, object?> config  // LLM 难以正确构造
) { ... }
```

### 2. 描述质量

```csharp
// ❌ 差的描述
[ToolParameter(Description = "文件")]
string file

// ✅ 好的描述
[ToolParameter(Description = "要读取的文件的绝对路径。必须是已存在的文件。")]
string filePath
```

### 3. 容错与验证

```csharp
public static async ValueTask<Result> SearchFiles(
    string pattern,
    int maxResults = 100,
    CancellationToken cancellationToken = default
) {
    // 运行时验证（JSON Schema 无法表达的约束）
    if (maxResults < 1 || maxResults > 10000) {
        return LodToolExecuteResult.Failure(
            "maxResults_out_of_range",
            "maxResults must be between 1 and 10000"
        );
    }

    // ...
}
```

## 总结

| 特性 | C# 参数 | Anthropic Tool 参数 |
|------|---------|---------------------|
| **类型安全** | 编译时保证 | 运行时验证 |
| **传递方式** | 位置 + 命名 | 仅命名（JSON 对象） |
| **默认值** | 编译器支持 | 需运行时实现 |
| **类型转换** | 严格 | 宽松（需大量容错） |
| **复杂类型** | 任意 CLR 类型 | JSON 原始类型 |
| **错误检测** | 编译错误 | 运行时错误 |
| **描述文本** | 可选（XML 文档） | 必须（LLM 依赖） |
| **参数顺序** | 重要 | 无关 |

**关键认知：**
- Tool 参数本质上是 **松散契约**，依赖 LLM 的理解和生成能力
- 描述文本是 **第一等公民**，质量直接影响 LLM 的调用准确性
- 需要大量 **运行时容错**，因为 LLM 可能发送非预期格式
- **JSON Schema** 是人类与 LLM 之间的接口规范，不是编译器约束

这种设计是 LLM 世界的必然选择：在编译时无法预知的动态环境中，通过文本描述和运行时验证来实现函数调用。

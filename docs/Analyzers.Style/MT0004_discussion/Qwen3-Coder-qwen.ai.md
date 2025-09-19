# Qwen3-Coder @ qwen.ai non-thinking
```C#
SomeFunc(123, (x) => {
    Console.WriteLine($"DebugInfo x={x}");
    a = x ^ 0x5a5a5a5a;
    return x + a;
}, 456);
```

---

这是一个非常有价值的代码格式化问题！让我从实际经验和规则制定的角度来分析：

## 核心决策因素

### 1. **行长限制优先原则**
```csharp
// 短行 - 保持单行
SomeFunc(123, x => x * 2, 456);

// 超长行 - 换行
SomeFunc(123, 
    (x) => {
        Console.WriteLine($"DebugInfo x={x}");
        var a = x ^ 0x5a5a5a5a;
        return x + a;
    }, 
    456);
```

### 2. **复杂度阈值规则**
建议制定以下量化标准：

```csharp
// 规则建议：
// - Lambda参数 <= 1个 且 无代码块：保持单行
// - Lambda参数 > 1个 或 有代码块：换行处理
// - 总行数 > 120字符：强制换行
// - Lambda体 > 3行：强制换行
```

### 3. **语法结构感知规则**

```csharp
// 情况1：简单表达式 - 不换行
SomeFunc(123, x => x * 2, 456);

// 情况2：多语句块 - 换行
SomeFunc(123, 
    (x) => {
        // 多行代码
        return result;
    }, 
    456);

// 情况3：多个复杂参数 - 每个参数一行
SomeFunc(
    veryLongVariableName1,
    (param1, param2) => {
        // complex logic
        return computedValue;
    },
    anotherLongVariableName2
);
```

## 推荐的综合规则集

```csharp
public enum FormattingRule
{
    // 主要决策树
    KeepSingleLine,        // 保持单行
    ParameterPerLine,      // 每个参数一行
    LambdaBodyOnly,        // 仅Lambda体换行
    FullMultiLine          // 完全多行
}

// 决策逻辑伪代码：
if (totalLineLength <= maxLength && 
    lambdaParameterCount <= 1 && 
    lambdaBodyLineCount <= 1) 
{
    return KeepSingleLine;
}
else if (lambdaBodyLineCount > 3 || 
         lambdaParameterCount > 1 ||
         totalLineLength > maxLength) 
{
    if (otherParametersAreSimple)
        return LambdaBodyOnly;
    else
        return ParameterPerLine;
}
```

## 实施建议

1. **配置化**：让这些阈值可以通过配置文件调整
2. **渐进式**：从简单规则开始，逐步增加复杂度判断
3. **上下文感知**：考虑文件整体风格和周围代码格式
4. **性能考虑**：避免过度复杂的AST分析影响性能

这样的规则集既保证了代码的可读性，又具有良好的可维护性和扩展性。你觉得这个思路如何？

# MT0007 — IndentClosingParenMultilineParameterList

- ID: MT0007
- Category: Indent
- Severity: Info (enabled by default)
- CanonicalName: IndentClosingParenMultilineParameterList
- DocAlias: ClosingParenAlign

## 规则概述
当参数/实参列表为多行，且右括号 `)` 已经单独在一行时，要求该右括号的水平缩进与“构造起始行”的缩进严格对齐。

- 构造起始行（Anchor）：
  - 调用表达式：被调用表达式的首个 token 所在行（例如 `obj.Foo(` 的起始行）
  - 对象创建：`new` 关键字所在行
  - 声明（方法、局部函数、构造函数、委托、运算符、显式/隐式转换、record）：声明首 token 所在行

该规则只约束右括号的“缩进列”，不负责换行行为；是否需要把 `)` 放到独立一行由 MT0004（NewLine 维度）保证。这样将 NewLine 与 Indent 的关注点解耦，便于组合。

## 设计动机与意图
- 与 MT0004（NewLineClosingParenMultilineParameterList）配合：MT0004 负责“`)` 单独成行”，MT0007 负责“成行后的对齐”。
- 与 MT0003（IndentMultilineParameterList）互补：MT0003 约束多行列表中元素/参数的缩进层级，MT0007 仅约束右括号的对齐位置。
- 统一对齐策略：右括号与构造起始行左边界对齐，而非与 `(` 或第一个参数对齐。

## 触发条件（何时报告）
同时满足以下条件才会报告：
1. 参数/实参列表跨多行（`(` 与 `)` 不在同一行）。
2. 右括号 `)` 已经独占一行（即最后一个参数/元素不与 `)` 同行）。
3. 右括号所在行的前导空白宽度 ≠ 构造起始行的前导空白宽度。

以下情况不会报告：
- 单行列表。
- 右括号与最后一个参数同在一行（此时应由 MT0004 来决定是否需要换行）。
- 空参数/实参列表（实现上仅在元素计数 > 0 时才分析）。

## 示例
### 调用表达式（Invocation）
不合规：
```csharp
var r = DoWork(
    a,
    b,
    c
    );
```
修复后：
```csharp
var r = DoWork(
    a,
    b,
    c
);
```

### 对象创建（Object Creation）
不合规：
```csharp
var x = new Widget(
    id,
    name
    );
```
修复后：
```csharp
var x = new Widget(
    id,
    name
);
```

### 方法/局部函数声明（Declaration）
不合规：
```csharp
void M(
    int x,
    int y
    )
{
}
```
修复后：
```csharp
void M(
    int x,
    int y
)
{
}
```

### Record 声明
```csharp
// 合规
public record Pair(
    int A,
    int B
);
```

## 与其它规则的关系
- MT0004（NewLineClosingParenMultilineParameterList）
  - 负责“`)` 是否独立成行”。
  - MT0007 假定该条件已满足，专注“对齐”。
- MT0003（IndentMultilineParameterList）
  - 约束多行列表中元素/参数的缩进层级（通常比起始行多一个缩进级）。
  - MT0007 约束右括号与起始行对齐，二者互不冲突、共同形成一致的列表布局。
- MT0006（FirstMultilineArgNewLine）
  - 要求首个多行参数在新行开始；与 MT0007 正交。

## 代码修复（Code Fix）
- 标题：Align closing parenthesis indentation
- 行为：将 `)` 所在行的前导空白调整为与“构造起始行”相同的缩进。如果 `)` 未在新行（理论上此情形不会被本规则报告），修复会在需要时插入换行再对齐。

## 边界与注意事项
- 混合制表符与空格：该规则基于“行首空白字符数量”判断列宽；混用 Tab 与空格可能导致表象上的列对齐与计数不一致。建议按项目统一缩进风格。
- 仅在存在至少一个参数/元素时分析空括号外形（空列表不在范围内）。
- 当前实现临时同时覆盖“参数列表（声明）”与“实参列表（调用/创建）”，后续若政策分化，可能拆分为更细的规则。

## 元数据
- Analyzer: `MT0007ClosingParenIndentAnalyzer`
  - `DiagnosticId = "MT0007"`
  - `CanonicalName = "IndentClosingParenMultilineParameterList"`
  - Category = `Indent`
  - Severity = `Info`
- Code Fix: `MT0007ClosingParenIndentCodeFix`
  - 等价键：`AlignCloseParenIndent`

## 抑制与配置
- 按行/块抑制：
  - `#pragma warning disable MT0007` / `#pragma warning restore MT0007`
  - 或使用 `[System.Diagnostics.CodeAnalysis.SuppressMessage]`
- 默认启用（Info 级别）。可在 `.editorconfig` 或规则集配置中调整严重级别。

## 设计 rationale 摘要
- 将 NewLine（是否换行）与 Indent（如何对齐）解耦，组合更灵活、诊断更明确。
- 强制右括号与起始行对齐，减少右括号“漂移”导致的视觉锯齿，提高可读性与一致性。

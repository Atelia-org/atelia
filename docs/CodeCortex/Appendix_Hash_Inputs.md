# Appendix: Hash Inputs Specification (Version 0.5)

> 精确定义各 Hash 的输入归一化范围；确保实现与文档可验证一致性。

## 1. 通用归一化步骤
1. 文件读取为 UTF-8；换行统一为 `\n`。
2. 移除 BOM。
3. 语法解析获得 Roslyn SyntaxTree（失败 → 退回文本级 fileHash）。
4. 对需要剔除 Trivia 的阶段：忽略 `SyntaxTrivia` 中的 Whitespace/EndOfLine/SingleLineComment/BlockComment/DocumentationCommentTrivia；保留 Preprocessor Directives。
5. 成员集合排序：KindPriority(Type=0, Field=1, Property=2, Event=3, Method=4, Other=9) + Signature 字典序。

## 2. 定义表
| 名称 | 输入 | 包含 | 排除 | 说明 |
|------|------|------|------|------|
| fileHash | 原始统一换行文本 | 全部 | 无 | 快速粗粒度变化检测 |
| structureHash | 公开 API 结构（成员签名+可见性+特性） | public/protected[/internal*] 成员签名；类型 kind/泛型参数数；[可配置 includes internal] | 成员主体、XML 文档、实现细节 | 仅结构级传播基础 |
| publicImplHash | public/protected 成员语法主体（无 Trivia） | 方法体、属性访问器、字段/常量初始化、表达式体 | 私有/内部成员；注释/空白 | 行为层变化 |
| internalImplHash | internal/private 成员主体（无 Trivia） | 内部/私有方法/属性/字段初始化/局部函数 | public/protected 成员；注释/空白 | 延迟漂移监控 |
| cosmeticHash | 被剔除的 Trivia + 注释 | 注释文本、空白折叠序列 | 代码 token | 噪声分类 |
| xmlDocHash | XML 文档节点文本（折叠空白） | `<summary/>` 等节点纯文本 | 代码主体 | 文档变化分类 |
| implHash | 拼接字符串 `"v1|"+publicImplHash+"|"+internalImplHash` 后 SHA256→截断 | 上述两个 hash | cosmetic/xmlDoc | 兼容显示 |

## 3. Canonical Signature 规范
```
[Accessibility] [SortedModifiers] ReturnType DeclaringType.MemberName(<ParamTypeList>) [GenericArity] [NullableAnnotations]
```
- ParamTypeList 仅类型（含 nullable、ref/out/in 修饰）
- 省略可选参数默认值表达式
- 泛型参数个数以 `#` 或 `` 后缀表示（选一，当前采用 `#<n>` 形态，如 `Foo#2`）
- NestedType 使用 `+` 连接

## 4. 排序示例
```
class A { public void Z(); public int P {get;}; internal void M(); }
结构排序 -> [P, Z] (若 internal 未包含)
```

## 5. Hash 冲突处理
- 算法：SHA256 → 取前 8 bytes → Base32(No I L O U) → 若冲突记录 `logs/hash_conflicts.log` 并扩展到 12 字符。
- index.json 中如存在扩展，写 `"hashExtended": true`。

## 6. Partial 类型合并
1. 收集全部 partial 声明语法树。
2. 合并成员列表后再执行排序与结构/实现 hash 计算。
3. 若任一文件解析失败 -> 标记 `partialParseDegraded=true` 并回退：使用文本拼接粗粒度 hash；记录 `logs/partial_degraded.log`。

## 7. 示例
```
public class Foo { // A
  /// <summary>Do X</summary>
  public int Add(int a, int b) => a + b; // body
  private int cache; // internal impl
}

structureHash 输入: class Foo + public int Add(int,int)
publicImplHash 输入: method body tokens `a+b`
internalImplHash 输入: field initializer (无) -> 空集合 hash
xmlDocHash 输入: "Do X"
```

---
(End of Appendix – Hash Inputs v0.5)

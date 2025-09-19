# Appendix: TypeId 与 Hash 规则 (Phase 1)

> 目标：为 Phase1 （L0+L1+L2+L3+L5 Outline Only）提供可执行的、最小充分且一次定义的 TypeId / 各类 Hash / Outline 排序规范，避免实现过程中重复拍板。若与 `CodeCortex_Design.md` 0.5 设计稿存在扩展字段差异，以本附录的“Phase1 范围”子章节优先；未出现的高级字段留待 Phase2。

## 1. TypeId 生成
```
TypeId = "T_" + Base32( SHA256( FQN + "|" + Kind + "|" + Arity ) )[0:8]
```
- FQN：命名空间 + `.` + 外层类型 + `.` + 嵌套类型（使用 `.` 而非 `+`）。
- Arity：泛型参数个数（非泛型=0）。
- Base32：自定义不含易混字符（去除 O, I, L, 0, 1）。
- 冲突：若 8 字符截断在写入 index 前检测已有不同 FQN → 扩展为 12 字符；记录 `logs/hash_conflicts.log`：`<timestamp> TypeIdConflict <short> <fqn1> <fqn2>`。

## 2. 成员签名与排序（用于 Outline 公共 API 列表 & structureHash 输入）
CanonicalMemberSignature 形式：
```
<Kind>|<Accessibility>|<ReturnType>|<DeclaringTypeSimple>|<Name>|<GenericArity>|(<ParamType1,ParamType2,...>)
```
规则：
1. Kind 排序优先级：Type (仅嵌套) > Field > Property > Event > Method > Constructor.
2. Accessibility 仅保留：public/protected/internal/protected internal/private protected （合并大小写为小写，internal 在 Phase1 如未纳入结构 hash 由配置控制）。
3. 泛型：使用 `Name` 不含反引号；Arity 单独字段。
4. 参数类型：使用显示类型名（含泛型实参、可空标记 `?`）；省略 `ref/out/in` 修饰（结构 hash 不考虑行为差异）。
5. Indexer 记作 `this[Type,...]`，ReturnType = 元素类型。
6. 重载按 CanonicalMemberSignature 全字符串字典序排序。
7. partial 类型：嵌套类型在合并后出现（递归所有 partial 文件）。

## 3. Hash 分类与输入抽取
| Hash | 输入构成 | 排除 | 说明 |
|------|----------|------|------|
| structureHash | 类型声明签名 + 成员 CanonicalMemberSignature 集合（排序后逐行拼接，LF） | XML Doc 内容、成员实现体、私有成员（除非 includeInternalInStructureHash=true） | 结构失效判断基础 |
| publicImplHash | 所有 public / protected 成员的“实现体片段”串联 | 注释、空白、属性 get/set 之间的空白 | 实现体=语法节点主体（Block / ExpressionBody / 初始值表达式）归一化 |
| internalImplHash | internal / private / private protected 成员实现体（同上） | 注释、空白 | Phase1 仅用于 ImplHash 展示与潜在 Outline 重写触发 |
| cosmeticHash | 从全部声明文本中抽取的注释 (`//`,`/* */`,`///`) + 纯空白行；去除前后空白后拼接 | 代码 Token | 便于后续选择忽略纯格式变化 |
| xmlDocHash | Roslyn `GetDocumentationCommentXml()` 提供的 XML（去多余空白，单行化） | 无 | Outline 显示 XMLDOC 首行 |
| implHash | `H("v1|"+publicImplHash+"|"+internalImplHash)` | cosmeticHash, xmlDocHash | 兼容显示 |

归一化步骤：
1. 所有换行标准化为 `\n`。
2. 去除实现体中的所有 Trivia：空白、注释、行尾分号后的空白；字符串内容保持。
3. 表达式主体成员 (`=> expr;`) 展开为 `expr` 文本。
4. 属性：若 `get` 或 `set` 有 Block/ExpressionBody，则序列化各访问器主体（无访问器体忽略）。
5. Event/Field：初始化表达式（若存在）计入对应 Impl Hash（public 或 internal 分类）。
6. 连接顺序：按成员排序次序；每个成员实现体之间加入单个 `\n`。

Cosmetic 采集最小算法：
```
For each file
  Extract all trivia where IsComment OR IsWhitespace
  Collapse consecutive whitespace to single space
  Append with '\n'
Hash(concatenated)
```

## 4. Hash 算法与截断
- 原始：SHA256(UTF8(输入拼接)) → 32 bytes。
- Base32（自定义表）→ 字符串。
- 取前 8 字符；发生冲突扩展至 12；再冲突则记录并继续扩展至 16（Phase1 不实现 beyond 12 的二次扩展逻辑可留 TODO）。

## 5. Outline 排序与格式补充
- Files 列表：相对仓库根路径，按不区分大小写字典序。
- Public API 区块：使用 structureHash 参与的成员（按 2 节排序）列 `+ <signature>`；Signature 显示形式：
  `<Accessibility> <ReturnType> <Name>(<ParamType1, ParamType2>)`；若泛型：`Name<T1,T2>`；省略 private 成员。
- 嵌套类型：单独行 `+ class/struct/interface <Name<T>>`，不再展开其成员（避免爆炸）；后续需要时单独 outline。

## 6. 配置影响 (configSnapshot 相关)
影响 structure/publicImpl/internalImpl 结果的布尔位：
- includeInternalInStructureHash
- structureHashIncludesXmlDoc (Phase1 默认 false)

若上述任一从旧值变化 → 触发全量重建：
1. 删除现有 `index.json` 备份为 `index.prev.<timestamp>.json`。
2. 重新扫描并计算所有 Hash。
3. outlineVersion 从 1 重新计数（或保留旧值 +1，Phase1 采用重新从 1，实施简单）。

## 7. 触发 Outline 重写矩阵（Phase1 简化）
| 变化 | 行为 |
|------|------|
| structureHash 变 | 重写 + outlineVersion++ |
| publicImplHash 或 internalImplHash 变 | 重写 + outlineVersion++ |
| xmlDocHash 变 | 重写 + outlineVersion++ |
| cosmeticHash 变 | 默认重写（可配置 skipCosmeticRewrite=true 则忽略）|

## 8. 删除 / 重命名策略 (Phase1 无 alias)
- 文件删除：重新枚举项目编译；缺失的类型（不再出现在编译符号集中）→ 从 index.types 移除，并在 `logs/index_build.log` 追加：`Removed <TypeId> <FQN>`。
- 重命名 (FQN 变化)：旧类型视为 Removed，新类型视为 Added；不迁移 outline 文件（未来 alias 再优化）。
- 残留 outline 孤儿清理：扫描 `types/` 下文件名对应 TypeId 不在 index.types[] → 删除（记录 `logs/index_build.log`）。

## 9. 冲突 & 异常处理
- TypeId 冲突：扩展长度后仍冲突 → 保留两个，标记冲突；在 resolve 时若多 TypeId 相同前缀需用户 disambiguate。
- Hash 计算异常（Roslyn 语法损坏）：跳过该类型（不写/不覆盖旧 outline），记录 `logs/errors.log`：`HashFail <TypeId?> <FQN?> <exception>`。

## 10. 示例
示例类型：
```csharp
namespace Demo.Core;
public partial class NodeStore<T>
{
    public int Count => _list.Count; // property
    public void Add(T item) { _list.Add(item); }
    internal void Trim() { /* internal maintenance */ }
    private List<T> _list = new();
}
```
结构收集：
```
class|public|Demo.Core|NodeStore|1|()
property|public|Int32|NodeStore|Count|0|()
method|public|Void|NodeStore|Add|0|(T)
```
publicImplHash 输入（去注释 + Trivia）：
```
_list.Count
_list.Add(item)
```
internalImplHash 输入：`/* internal maintenance */` 去注释后为空 → 空字符串 hash 常量。

---
(End of Appendix_TypeId_and_HashRules_P1)

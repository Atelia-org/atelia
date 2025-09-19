# Appendix: 测试夹具计划 (Phase 1)

## 1. 目标
为 Hash / Outline / 解析 / 增量 / Prompt 窗口测试提供统一最小代码样本，降低重复造型。

## 2. 目录建议
```
tests/Fixtures/
  BaseSolution/
    BaseSolution.sln
    Core/Core.csproj
    Core/NodeStore.cs
    Core/NodeStore.Partial.cs
    Core/NestedExample.cs
    Core/GenericSample.cs
    Core/FormattingSample.cs
```

## 3. 样本说明
| 文件 | 用途 | 关键点 |
|------|------|--------|
| NodeStore.cs | Hashing / Outline 基础 | public 方法 + internal 方法 + XMLDoc |
| NodeStore.Partial.cs | partial 合并 | 另一半添加属性/嵌套类型 |
| NestedExample.cs | 嵌套类型 | class A { class B { } } |
| GenericSample.cs | 泛型 + 重载 | class Repo<T> { void Add(T x); void Add(IEnumerable<T> x); } |
| FormattingSample.cs | Cosmetic 变化 | 多空行 / 注释变化 / 无结构修改 |

## 4. 测试组断言清单
### HashingTests
- 修改 public 方法体 → publicImplHash 变, structureHash 不变。
- 添加 public 方法 → structureHash 变。
- 修改 internal 方法体 → internalImplHash 变, structureHash 不变。
- 改注释/空白 → cosmeticHash 变, 其他不变。

### OutlineTests
- partial 合并：Outline Files 行列出两个 .cs。
- 泛型：`Repo<T>` 显示为 `Repo<T>` 且 TypeId 稳定。
- 嵌套：嵌套类型仅一行列出，不展开成员。

### SymbolResolveTests
- 精确: `Core.NodeStore` 命中。
- 后缀唯一: `NodeStore` → 单一。
- 通配: `*Store` 返回 NodeStore。
- 模糊: `NodeStor` 提示建议 `NodeStore`。

### IncrementalTests
- 修改一个文件：outlineVersion++ (impl change)。
- 添加 public 方法：structureHash 变化 + outlineVersion++。
- 删除文件：该类型从 index 移除，outline 文件被清理 (或保留直到 FullRescan, Phase1 采用即时清理)。

### PromptWindowTests (Outline Only)
- 最近查询 10 次：最新 8 进入 Focus，余下进入 Recent。
- Pin 超预算：返回拒绝（模拟设置小 budget）。

### CliRpcTests
- `outline NodeStore` 首次生成，再次命中缓存 (hit ratio >0)。
- `status` 返回非空 typesIndexed。

## 5. 工具辅助
- 可为 HashingTests 添加 helper：`AssertHashChange(kind, action)`。
- 使用 xUnit；命名：`[Trait("Category","Hashing")]`。

## 6. Fixture 变更策略
- 修改 fixture 代码需同步更新预期值（基线 Outline 存入 `tests/Baselines/`）。

---
(End of Appendix_Test_Fixtures_Plan_P1)

# 嵌套类型排序问题修复报告

## 问题描述

在最近的重构中，`IndexSynchronizer` 未能正确处理嵌套类型的排序，导致在应用 delta 时抛出异常：

```
Parent type node 'Outer`1' not found when processing 'T:Ns.Outer`1+Inner'.
This violates the SymbolsDelta ordering contract: all parent types must be added before their nested types.
```

## 根本原因

问题的根本原因在 `SymbolTreeBuilder.ApplyTypeAddsSingleNode` 方法中：

1. **排序是正确的**：`SymbolsDeltaContract.Normalize` 按 `DocCommentId` 长度正确排序了类型条目
   - `T:Ns.Outer`1` (长度 12) 排在前面
   - `T:Ns.Outer`1+Inner` (长度 18) 排在后面

2. **查找逻辑有缺陷**：`FindStructuralTypeChild` 方法只查找 `Entry is null` 的"结构节点"
   ```csharp
   if (node.Kind == NodeKind.Type && node.Entry is null && ...)
   //                                  ^^^^^^^^^^^^^^^^
   //                                  只查找没有 Entry 的节点！
   ```

3. **实际情况不符合假设**：
   - 当 `Outer`1` 被添加时，它是一个完整的类型定义（带有 `Entry`），被添加为叶节点
   - 后续处理 `Outer`1+Inner` 时，需要找到 `Outer`1` 作为父节点（中间节点）
   - 但 `FindStructuralTypeChild` 无法找到带有 `Entry` 的节点，因此抛出异常

## 解决方案

添加新方法 `FindAnyTypeChild`，它可以找到任何匹配名称的类型节点（无论是否有 `Entry`）：

```csharp
/// <summary>
/// 查找任何匹配名称的类型子节点（无论是否有 Entry）。
/// 用于在处理嵌套类型时查找已经存在的父类型节点。
/// </summary>
private int FindAnyTypeChild(int parent, string name) {
    if (parent < 0 || parent >= Nodes.Count) { return -1; }
    int current = Nodes[parent].FirstChild;
    while (current >= 0) {
        var node = Nodes[current];
        if (node.Kind == NodeKind.Type && string.Equals(node.Name, name, StringComparison.Ordinal)) { 
            return current; 
        }
        current = node.NextSibling;
    }
    return -1;
}
```

然后修改查找逻辑为两步查找：
1. 首先尝试查找结构节点（`Entry is null`）
2. 如果没找到，再查找任何匹配名称的类型节点

```csharp
if (!isLast) {
    // 首先尝试查找结构节点（Entry is null）
    int intermediateNode = FindStructuralTypeChild(currentParent, nodeName);
    // 如果没找到结构节点，尝试查找任何匹配名称的类型节点（可能已经有 Entry）
    if (intermediateNode < 0) {
        intermediateNode = FindAnyTypeChild(currentParent, nodeName);
    }
    if (intermediateNode < 0) {
        throw new InvalidOperationException(...);
    }
    currentParent = intermediateNode;
    continue;
}
```

## 测试结果

修复后测试结果：
- ✅ `V2_SymbolTree_WithDelta_Tests.Removal_NestedType_RemovesSubtreeAliases` - 通过
- ✅ 所有 26 个 `V2_SymbolTree` 测试 - 通过
- ✅ 完整测试套件：175/176 通过（唯一失败的测试与命名空间搜索相关，与本次修复无关）

## 修改的文件

1. `src/CodeCortexV2/Index/SymbolTree/SymbolTreeBuilder.cs`
   - 添加 `FindAnyTypeChild` 方法
   - 修改 `ApplyTypeAddsSingleNode` 中的父节点查找逻辑

## 结论

修复已成功解决嵌套类型排序问题。排序契约本身是正确的（按 `DocCommentId` 长度排序），问题在于查找逻辑过于严格。通过允许查找任何匹配名称的类型节点（而不仅仅是结构节点），我们能够正确处理父类型本身就是完整类型定义的场景。

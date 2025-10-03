# 调查结果：CreateIntermediateTypeEntry 的必要性

## 关键发现

### 1. IndexSynchronizer 的行为

查看 `IndexSynchronizer.ComputeFullDeltaAsync` 和 `ComputeDeltaAsync`：

**在 ComputeFullDeltaAsync 中（初始构建）：**
```csharp
void WalkType(INamedTypeSymbol t) {
    // ... 为当前类型创建 entry
    typeAdds.Add(CreateTypeEntry(docId, asm, parentNs, fqnNoGlobal, fqnLeaf));

    // 递归处理嵌套类型
    foreach (var nt in t.GetTypeMembers()) {
        ct.ThrowIfCancellationRequested();
        WalkType(nt);  // <--- 递归！每个嵌套类型都会创建 entry
    }
}
```

**在 ComputeDeltaAsync 中（增量更新）：**
```csharp
foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
    var sym = model.GetDeclaredSymbol(node, ct) as INamedTypeSymbol;
    // ... 为每个声明的类型创建 entry
    declared.Add((Key: new TypeKey(sid, asm), Entry: entry));
}
```

**结论：IndexSynchronizer 确实会为所有嵌套类型（包括外层和内层）分别创建 SymbolEntry！**

### 2. SymbolsDelta 的排序契约

从 `SymbolsDeltaContract.Normalize` 可以看到：

```csharp
// TypeAdds 按 DocCommentId.Length 升序排序
if (adds.Count > 1) {
    adds.Sort(static (left, right) =>
        left.DocCommentId.Length.CompareTo(right.DocCommentId.Length));
}

// TypeRemovals 按 DocCommentId.Length 降序排序
if (removals.Count > 1) {
    removals.Sort(static (left, right) =>
        right.DocCommentId.Length.CompareTo(left.DocCommentId.Length));
}
```

**意义：**
- 对于 `Outer<T>` 和 `Outer<T>.Inner`：
  - `T:Ns.Outer`1` (长度较短) 会排在前面
  - `T:Ns.Outer`1+Inner` (长度较长) 会排在后面
- **这保证了父类型总是在子类型之前被添加！**

### 3. CreateIntermediateTypeEntry 的调用位置

在 `SymbolTreeBuilder.ApplyDelta` 中：
```csharp
for (int i = 0; i < typeSegs.Length; i++) {
    var nodeName = typeSegs[i];
    bool isLast = i == typeSegs.Length - 1;
    var targetEntry = isLast
        ? e  // 最后一段：使用传入的 entry（来自 IndexSynchronizer）
        : CreateIntermediateTypeEntry(nsSegs, typeSegs, i, assembly); // 中间段：创建中间 entry

    // ...
}
```

### 4. 问题分析

**CreateIntermediateTypeEntry 什么时候会被调用？**

只有当处理的 entry 的 `TypeSegments.Length > 1` 时，才会遍历中间段。

**理论上的情况：**
1. 如果 IndexSynchronizer 正确地为 `Outer<T>` 和 `Outer<T>.Inner` 都创建了独立的 entry
2. 由于排序契约，`Outer<T>` 会先被处理
3. 当处理 `Outer<T>.Inner` 时，父节点 `Outer<T>` 应该已经存在

**但是！如果发生以下情况：**
- `Outer<T>` 的 entry 从未被添加（bug、文档解析失败等）
- 或者 `Outer<T>` 已被删除，但 `Inner` 还在索引中
- 直接处理 `Outer<T>.Inner` 而跳过了 `Outer<T>`

**那么 `CreateIntermediateTypeEntry` 就会被调用来"填补空缺"**

### 5. 核心问题

**你说得对！CreateIntermediateTypeEntry 存在以下问题：**

1. **无法获取正确的泛型参数名称**
   - 只能生成 `Outer<T1, T2>` 这样的占位符
   - 无法生成 `Outer<TKey, TValue>` 这样的真实名称
   - 只有 `INamedTypeSymbol.ToDisplayString` 能获得正确的名称

2. **违反单一数据源原则（SRP）**
   - 类型元数据应该只由 IndexSynchronizer 从 Roslyn 获取
   - SymbolTreeBuilder 不应该"猜测"或"合成"类型信息

3. **掩盖了真正的问题**
   - 如果 IndexSynchronizer 没有正确添加父类型，这是一个 bug
   - CreateIntermediateTypeEntry 会"修复"这个 bug，但修复得不正确
   - 应该 fail-fast，暴露问题

4. **违反了排序契约的设计意图**
   - 排序契约的目的就是保证父类型先于子类型
   - 如果需要 CreateIntermediateTypeEntry，说明这个契约被违反了

## 建议

### 方案 1：删除 CreateIntermediateTypeEntry，改为 Fail-Fast

```csharp
for (int i = 0; i < typeSegs.Length; i++) {
    var nodeName = typeSegs[i];
    bool isLast = i == typeSegs.Length - 1;

    if (!isLast) {
        // 中间节点必须已经存在，否则抛异常
        int parentNode = FindStructuralTypeChild(currentParent, nodeName);
        if (parentNode < 0) {
            throw new InvalidOperationException(
                $"Parent type node '{nodeName}' not found. " +
                $"This violates the SymbolsDelta ordering contract. " +
                $"All parent types must be added before nested types.");
        }
        currentParent = parentNode;
        continue;
    }

    // 最后一段：正常处理
    var targetEntry = e;
    // ...
}
```

### 方案 2：保守方案 - 保留但添加警告

如果担心破坏性太大，可以：
1. 保留 CreateIntermediateTypeEntry
2. 添加 Debug.Assert 和日志警告
3. 计数统计有多少次调用了它

```csharp
var targetEntry = isLast
    ? e
    : CreateIntermediateTypeEntryWithWarning(nsSegs, typeSegs, i, assembly);

internal SymbolEntry CreateIntermediateTypeEntryWithWarning(...) {
    DebugUtil.Print("SymbolTree.Warning",
        $"CreateIntermediateTypeEntry called - this indicates a potential " +
        $"violation of SymbolsDelta ordering contract!");
    Debug.Assert(false, "Unexpected intermediate type creation");

    // ... 原有逻辑
}
```

## 验证步骤

建议添加以下测试来验证：

1. **测试 IndexSynchronizer 是否为所有嵌套类型创建 entry**
2. **测试排序契约是否确实保证父类型先于子类型**
3. **测试如果违反契约会发生什么**

```csharp
[Fact]
public void IndexSynchronizer_ShouldCreateEntriesForAllNestedTypes() {
    // 创建包含嵌套类型的代码
    var code = "class Outer<T> { class Inner { } }";

    // 获取 delta
    var delta = await sync.ComputeFullDeltaAsync(solution, ct);

    // 验证包含两个 entry
    Assert.Equal(2, delta.TypeAdds.Count);
    Assert.Contains(delta.TypeAdds, e => e.DocCommentId == "T:Ns.Outer`1");
    Assert.Contains(delta.TypeAdds, e => e.DocCommentId == "T:Ns.Outer`1+Inner");
}

[Fact]
public void SymbolsDelta_Normalize_ShouldSortByLength() {
    var outer = CreateEntry("T:Ns.Outer`1");
    var inner = CreateEntry("T:Ns.Outer`1+Inner");

    // 故意乱序
    var delta = SymbolsDeltaContract.Normalize([inner, outer], []);

    // 验证排序
    Assert.Equal("T:Ns.Outer`1", delta.TypeAdds[0].DocCommentId);
    Assert.Equal("T:Ns.Outer`1+Inner", delta.TypeAdds[1].DocCommentId);
}
```

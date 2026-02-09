# 03 从文本行到 JSON 对象数组

从纯文本行 (String) 到 JSON 对象 (Object) 的跨越，为 Diff 算法带来了新的维度（复杂性）也带来了新的优化空间（Key）。

## 1. 元素等价性判定 (The Equality Problem)

在文本 Diff 中，判断两行是否相等很简单：`stringA == stringB`。但在 JSON 数组中，这就变得复杂了。

### 1.1 引用相等 (Reference Equality)
- **检查**：`ptrA == ptrB`
- **速度**：极快 $O(1)$。
- **适用**：不可变数据结构（Immutable Data Structures）。如果你使用了像 Immer.js 或 .NET 的 `record`，未修改的对象引用保持不变，这将是极大的性能优势。

### 1.2 标识符相等 (Key/ID Equality)
- **检查**：`objA.id == objB.id`
- **速度**：极快 $O(1)$。
- **陷阱**：ID 相同不代表内容没变。通常用于**对齐**阶段（找到谁是谁），然后再进行内容对比（检查有没有发生 Update）。

### 1.3 深度相等 (Deep Quality)
- **检查**：递归比较所有字段。
- **速度**：慢，取决于对象深度和大小。
- **优化**：
  - **HashCode 预检**：为每个对象维护一个 Hash 值（如果你能承受碰撞风险或计算 Hash 的开销）。
  - **Version Stamp**：为每个对象维护一个版本号或最后修改时间戳。

## 2. 策略矩阵

针对不同的 JSON Array 特性，我们应选择不同策略：

| 场景特点 | 推荐策略 | 复杂度 | 备注 |
| :--- | :--- | :--- | :--- |
| **有唯一 ID** | **Key-Map Strategy** | $O(N)$ | 最优解。先用 Map 匹配 ID，再 Check 内容。 |
| **元素是基本类型 (int/str)** | **Myers Algorithm** | $O(ND)$ | 退化为文本 Diff。 |
| **无 ID，追加为主** | **Prefix/Suffix Trimming** | $O(N)$ | 前后扫描去掉相同部分，中间再 Diff。 |
| **无 ID，任意乱序** | **Heckel's Algorithm** | $O(N)$ | 尝试基于内容相似度(Hash)匹配。 |
| **微小改动 (Small Delta)** | **Optimized Myers** | $O(ND)$ | 当 D 很小时极快。 |

## 3. Key-Map Strategy (王者方案)

如果你的数据模型允许，**强制要求数组元素具有唯一 ID** 是实现快速增量序列化的捷径。

算法流程：
1. **建立索引**：遍历新数组 `NewArr`，建立 `Map<ID, Index>`。
2. **扫描旧数组**：遍历 `OldArr`。
   - 如果 `OldItem.ID` 在 Map 中不存在 -> **Delete**。
   - 如果存在 -> 标记为 **Keep** (或 Potential Update)。同时记录其在新数组的位置。
3. **扫描新数组**：遍历 `NewArr`。
   - 如果 `NewItem.ID` 没在旧数组出现过 -> **Insert**。
4. **检测移动与更新**：
   - 对于 Keep 的元素，检查 `OldItem.Content != NewItem.Content` -> **Update**。
   - 检查顺序变化（最长递增子序列 LIS 剔除乱序者） -> **Move**。
     - *注：计算最小移动次数也是一个 LIS 问题。*

这种方法完全避免了昂贵的递归 Diff 搜索，将 $O(N^2)$ 问题降维打击成 $O(N)$。

## 4. 深度 Diff 的性能陷阱 (Json Specific)

不要试图在 Diff 阶段去递归 Diff 每一个子对象。
- **层级隔离**：只 Diff 数组这一层。如果发现某个对象变了，产生一个 `Update` 操作，替换整个对象（或者对该对象再递归生成 Patch，但这通常属于 Patch 生成器的逻辑，而非 Array Diff 本身）。
- **粒度控制**：作为增量序列化，发送一个稍大的 Update 包（比如含有5个字段的对象），通常比计算出极其精细的 Patch（比如“字段A变了”）要划算，因为计算也是有 CPU 成本的。网络的瓶颈往往在于数量级，而不是微小的字节差异。

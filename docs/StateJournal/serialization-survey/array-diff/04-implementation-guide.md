# 04 实现指南：构建增量序列化器

为了实现通用且快速的 Json-Array 增量序列化，我们需要设计数据协议和选用具体的算法组合。

## 1. 增量协议设计 (Delta Protocol)

不要重新发明轮子，也不要完全照搬 JSON Patch (RFC 6902)，因为它太啰嗦。建议根据内部需求定制紧凑格式。

### 1.1 操作原语 (Primitives)
我们需要定义一组原子操作来描述变化：

| OpCode | 参数 | 描述 |
| :--- | :--- | :--- |
| `INS` (Insert) | `index`, `item` | 在 `index` 处插入 `item` |
| `DEL` (Delete) | `index` | 删除 `index` 处的元素 |
| `MOV` (Move) | `fromIndex`, `toIndex` | (可选) 将元素从 `from` 移到 `to` |
| `UPD` (Update) | `index`, `changes` | (可选) 更新 `index` 处的内容 |

*注：如果不支持 `MOV`，移动一个元素就需要 `DEL` + `INS`，这会增加数据量。支持 `MOV` 能显著减少载荷，但增加 Diff 算法复杂度。*

### 1.2 示例格式 (Compact JSON)
```json
{
  "v": 2, // 基础版本号
  "diff": [
    ["del", 5],           // 删除索引 5
    ["ins", 10, { ... }], // 在新索引 10 插入对象
    ["mov", 0, 20],       // 把原来的 0 号元素移到 20 号
  ]
}
```

## 2. 推荐实现路线

### Phase 1: 暴力 ID 匹配 (MVP)
假设所有数组项都有唯一 ID（如 `Guid` 或 `db_id`）。这是最高效的路径。

**算法步骤**:
1. 输入列表 `A` (旧) 和 `B` (新)。
2. 创建 `Map<Id, Item>` for `A`。
3. 遍历 `B`：
   - 没见过的 ID -> **Insert**。
   - 见过的 ID -> 比较内容 Hash。不同则 **Update**。标记该 ID 已处理。
4. 遍历 `A`：
   - 未被标记处理的 ID -> **Delete**。
5. **处理顺序**：
   - 此时我们有了 Insert/Update/Delete 集合。顺序是个大问题。
   - 简单做法：先做 Delete，再在正确位置做 Insert。
   - 如果想支持 Move，需要计算 **LIS (Longest Increasing Subsequence)** 来找出哪些元素保持了相对顺序，其余的生成 Move 指令。

### Phase 2: 优化的 Myers (通用型)
如果无法保证 ID 存在，或者作为通用库发布。

**算法步骤**:
1. **预处理 (Pruning)**：
   - 去除公共前缀 (Find common prefix) -> 跳过。
   - 去除公共后缀 (Find common suffix) -> 跳过。
   - 剩下中间部分 $A'$ 和 $B'$。
2. **Hash 映射**：
   - 将对象内容 Hash 为整数。比较整数列表而不是对象列表。加速比较。
3. **运行 Diff**:
   - 如果 N 很小 (< 1000)，直接跑标准 **Myers**。
   - 如果 N 很大，跑 **Linear Space Myers**。
4. **后处理**:
   - 将 Diff 结果转化为 Patch 操作。

## 3. C# 实现建议 (Performance Tips)

- **使用 `Span<T>` 和 `Memory<T>`**：避免在切片数组时产生额外的内存分配。
- **结构体代替类**：Diff 节点的计算过程中会产生大量临时小对象，使用 `ref struct` 能够零 GC。
- **EqualityComparer 缓存**：比较器不仅要快，还要能内联。
- **避免 LINQ**：在热路径上，手动写的 `for` 循环比 LINQ 快得多且不仅分配器。

## 4. 总结

| 需求 | 建议 |
| :--- | :--- |
| **追求极致速度** | 强制要求 Unique ID，走 Hask/Map 路线。 |
| **追求通用性** | 实现 Linear Space Myers，并加上 Prefix/Suffix 剪枝。 |
| **追求 Patch 最小** | 实现带 Move 检测的 Heckel 算法。 |

建议先实现 **Phase 1**，因为在大多数分布式系统中，对象实体都有 ID，这是最实用且高回报的优化点。

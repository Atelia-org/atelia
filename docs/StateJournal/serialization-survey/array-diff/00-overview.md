# JSON-Array Diff 与增量序列化调研大纲

本文档集旨在为开发通用、快速的 JSON-Array 差分（Diff）算法提供理论支持和实现路线参考。我们将从经典的文本行 Diff 算法出发，分析其原理，并探讨如何将其迁移和优化以适用于结构化的 JSON 数据，最终服务于增量序列化这一目标。

## 目录结构

### 1. [经典基础：从 LCS 到 Myers](01-classic-algorithms.md)
   - **核心概念**：编辑距离（Edit Distance）与最短编辑脚本（Shortest Edit Script, SES）。
   - **Myer's Algorithm**：
     - 贪心策略与对角线搜索。
     - 为什么它是 Git 等工具的首选？
     - 时间复杂度 $O(ND)$ 的含意与陷阱。
   - **LCS (最长公共子序列)**：Diff 问题的本质。

### 2. [进阶变种：处理移动与噪声](02-advanced-variants.md)
   - **Patience Diff**：
     - 基于“唯一最长公共子序列”的策略。
     - 为什么由于其稳定性，它产生的 Patch 更易于人类阅读？
   - **Heckel's Algorithm**：
     - 专门解决“移动（Move）”操作检测的线性时间算法。
     - 在 DOM Diff 和列表重排中的应用。
   - **Histogram Diff / Patience 变体**：处理大量重复元素的优化。

### 3. [从文本行到 JSON 对象数组](03-json-array-strategy.md)
   - **数据特性的差异**：
     - 文本行 vs 对象引用/值。
     - 相等性判定（Equality Check）：引用相等 vs 值相等 vs 键相等。
   - **策略选择**：
     - **Key-based Diff (O(N))**：当数组元素具有唯一标识符（ID）时。
     - **Heuristic Diff**：在大数据量下的近似算法。
     - **Deep Diff 的代价**：递归对比的性能陷阱。

### 4. [实现指南：构建增量序列化器](04-implementation-guide.md)
   - **增量序列化场景分析**：通常是“读多写少”还是“频繁微调”？
   - **Delta 格式设计**：
     - 扁平化操作 vs 树状补丁。
     - JSON Patch (RFC 6902) 的启示与局限。
   - **性能优化建议**：
     - 滚动哈希（Rolling Hash）预检。
     - 提前剪枝（Pruning）。
     - 混合策略（Hybrid Approach）。

---

> **阅读建议**：
> - 如果你需要**精确**的最小 Diff 且不关心移动操作，重点阅读 [01-classic-algorithms.md](01-classic-algorithms.md)。
> - 如果你需要处理**列表重排（Reordering）**，重点阅读 [02-advanced-variants.md](02-advanced-variants.md) 中的 Heckel 算法。
> - 如果你拥有数据的**唯一 ID**，直接参考 [03-json-array-strategy.md](03-json-array-strategy.md) 中的 Key-based 策略，这是最快的路径。

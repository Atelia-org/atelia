# Appendix: 符号解析算法 (Phase 1)

> 本附录描述当前已实现的 SymbolResolver 真实算法（与早期设想区别：未实现 Trie、LRU 缓存、加权打分函数；使用线性集合与简单枚举排序）。

---
## 1. 目标与约束
目标：在不建立复杂索引结构 / 引用图的条件下，提供快速、稳定（可测试）、可扩展的类型名解析。

约束：
- 单线程，纯内存结构（来自已加载索引模型）
- 结果集硬截断：limit（默认 20）
- 模糊最大编辑距离依据查询长度动态 ≤2
- 不做成员级（方法/属性）匹配（Phase1）

---
## 2. 支持的查询模式
| 模式 | 触发条件 | 示例 | 说明 |
|------|----------|------|------|
| Exact | FQN 精确大小写匹配 | `A.B.C.TypeX` | 最高优先级 |
| ExactIgnoreCase | FQN 忽略大小写匹配 | `a.b.c.typex` | 仅在 Exact 为空时 |
| Suffix | `fqn.EndsWith(query, OrdinalIgnoreCase)` | `TypeX` / `C.TypeX` | 支持简单名 & 部分命名空间后缀 |
| Wildcard | query 含 `*` 或 `?` | `*Service`, `A.*Repo` | 转换为正则匹配 FQN |
| Fuzzy | 非通配且前面累计结果 < limit | `Srvce` → `Service` | 对 simple name 做 Levenshtein |

---
## 3. 数据结构（实现态）
| 字段 | 类型 | 用途 |
|------|------|------|
| `_all` | List<(Fqn, Id, Kind, Simple)> | 线性基础集合（由索引模型构建） |
| `_fqnIgnoreCase` | Dictionary<string,string> | 小写 FQN → 原始 FQN（或 Id）快速 ExactIgnoreCase |
| `_byId` | Dictionary<string,TypeEntry> | 解析 Id → 详细条目（避免重复遍历） |

未实现：suffix Trie、LRU 缓存、复杂打分权重（原设想被推迟）。

---
## 4. 解析流程
伪代码（与实际 `SymbolResolver.Resolve` 对齐）：
```
resolve(query, limit):
  trim; if empty -> []
  detect hasWildcard
  results = list; addedIds = set

  // 1 Exact
  if fqn == query -> add(MatchKind.Exact)

  // 2 ExactIgnoreCase
  if results 空 && _fqnIgnoreCase.TryGetValue(lower(query)) -> add(MatchKind.ExactIgnoreCase)

  // 3 Suffix (收集完整集合用于歧义判定，再按 limit 加入)
  if results.Count < limit:
    allSuffix = all where fqn.EndsWith(query, OrdinalIgnoreCase)
    for each s in allSuffix 顺序 add 直到 limit

  // 4 Wildcard
  if hasWildcard && results.Count < limit:
    regex = Wildcard->Regex
    scan _all 按 FQN 测试；add 直到 limit

  // 5 Fuzzy
  if !hasWildcard && results.Count < limit:
    threshold = (query.Length > 12 ? 2 : 1)
    for each a in _all:
     if |len(a.Simple)-len(query)| > threshold -> continue
     d = BoundedLevenshtein(a.Simple, query, threshold)
     if d >=0: add(Fuzzy, RankScore=100 + d)
     if results 达 limit break

  // 歧义标记 (仅针对简单后缀查询)
  if query 不含 '.' 且 allSuffix.Count > 1:
    将所有 MatchKind.Suffix 标记 IsAmbiguous=true

  排序: MatchKind 枚举次序 → RankScore → FQN
  截断 limit
  return
```

---
## 5. 模糊匹配
阈值函数：`threshold = query.Length > 12 ? 2 : 1`。

编辑距离：使用二维 DP（O(m*n)），行最小值 > threshold 则行级剪枝早退返回 -1。

仅比较 simple name（FQN 最后一段），理由：
- 降低距离开销
- 减少命名空间差异带来的噪声

---
## 6. Wildcard 处理
转换步骤：
1. 遍历字符，普通字符经 `Regex.Escape`
2. `*` → `.*`，`?` → `.`
3. 外层不加 `^...$`（当前实现匹配任意子串/完整 FQN?）—— 实际实现采用整串匹配（具体见代码），忽略大小写。

---
## 7. 排序与 RankScore
| MatchKind | RankScore 规则 | 说明 |
|-----------|---------------|------|
| Exact / ExactIgnoreCase / Suffix / Wildcard | 0 | 主要靠枚举先后顺序 |
| Fuzzy | 100 + distance | 确保全部排在其他模式之后；同距再按 FQN 次序 |

最终排序键：`(MatchKindOrdinal, RankScore, FqnOrdinal)` → 稳定可测试。

---
## 8. 歧义标记 (IsAmbiguous)
条件：
1. 查询不含 '.' （视作简单名）
2. 收集到的 suffix 全量集合大小 > 1

实现：先不截断收集完整 suffix 列表，用其大小决定是否标记，再在排序/截断后保留标记。这样截断不会使歧义状态错误取消。

不标记场景：
- Exact / ExactIgnoreCase 命中（即使还有其他 suffix）
- Wildcard / Fuzzy 多结果（其本意是宽集合）

---
## 9. 复杂度
| 阶段 | 复杂度 | 说明 |
|------|--------|------|
| Exact / IgnoreCase | O(1) | 字典查询 |
| Suffix | O(N) | 线性 `EndsWith` |
| Wildcard | O(N * R) | R 为正则匹配单串成本（短 FQN） |
| Fuzzy | O(N * L^2) | L 为 simple 长度 (~≤20)；limit 达成后提前结束 |

N≤5k 下响应时间可接受；更大规模的优化方向见“未来增强”。

---
## 10. 关键边界条件
| 情况 | 处理 |
|------|------|
| 空 / 空白 Query | 返回空集合 |
| 只有通配 `*` | 视作 Wildcard，全表匹配后截断 |
| Fuzzy 长度差大 | 直接跳过候选（剪枝） |
| 重复 Id | HashSet 去重 |
| 查询大小写差异 | ExactIgnoreCase 捕获 |

---
## 11. 测试覆盖（已实现）
| 类别 | 代表用例 |
|------|----------|
| Exact / IgnoreCase | 精确 + 大小写不同命中 |
| Suffix & Ambiguous | 多类型同名后缀，标记 IsAmbiguous |
| Wildcard 基础 | `*Controller` 等模式匹配 |
| Fuzzy Distance=1 | 常见少字符错拼 |
| Fuzzy Distance=2 | 长名称两处差异（阈值=2） |
| Limit 截断稳定 | 排序+截断后顺序可预测 |
| Wildcard 禁用 Fuzzy | 含通配符不进入模糊阶段 |
| 阈值边界 | 长度 12 / 13 转折 |
| Ambiguous 截断仍标记 | 充分验证标记逻辑稳定 |

---
## 12. 与早期设计差异（Changelog）
| 设计项 | 原设想 | 实际 Phase1 | 原因 |
|--------|--------|-------------|------|
| 后缀 Trie | 构建反转 Trie | 未实现（线性扫描） | 规模尚小；实现成本 > 现有收益 |
| LRU 缓存 | 最近查询缓存 | 未实现 | 解析本身开销低，先延后 |
| 复杂打分公式 | distance + namespace 权重 | 简化为枚举优先 + 轻量 RankScore | 保证确定性 & 可测试性 |
| Damerau-Levenshtein | 允许相邻交换 | 使用标准 Levenshtein | 差异不显著，减少复杂度 |
| 统一 JSON 错误对象 | Ambiguous 返回 error | CLI 直接列表 & 标记 IsAmbiguous | CLI MVP 简化 |

---
## 13. 未来增强（提案）
| 方向 | 描述 | 触发条件 |
|------|------|----------|
| search 模式 | 与 resolve 区分，支持分页/评分 | 用户需要“探索”而非唯一解析 |
| 可配置阈值 | 注入 `SymbolResolverOptions` | 项目规模 / 噪声控制 |
| Trie / 倒排 | 加速 suffix / wildcard | N > 10k |
| Banded Levenshtein | 降低模糊复杂度 | 模糊瓶颈出现 |
| 结果缓存 | LRU + Size 限制 | 热点 query 重复高 |
| 统计与诊断 | 解析耗时 / 模式命中计数 | 性能回归监控 |
| Members 支持 | 方法/属性补全与 disambiguation | 用户需要更精细定位 |

---
## 14. 快速开发备忘
```
// 模糊触发前提
!hasWildcard && results.Count < limit

// 阈值
threshold = query.Length > 12 ? 2 : 1;

// 歧义标记条件
!query.Contains('.') && allSuffixCount > 1
```

---
## 15. 总结
当前实现以最少结构获得足够准确/稳定的解析能力，并通过测试矩阵保证行为可回归。后续优化点已结构化列出，可按数据规模与性能指标逐步引入。

(End of Appendix)

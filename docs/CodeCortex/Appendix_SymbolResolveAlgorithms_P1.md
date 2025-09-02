# Appendix: 符号解析算法 (Phase 1)

## 1. 目标
提供统一的 query → TypeId 解析与候选搜索逻辑，使 CLI/RPC/Prompt 使用同一实现，减少歧义。

## 2. 输入与模式
Query 允许：
- 完整 FQN：`Namespace.Sub.Namespace.TypeName`
- 部分后缀：`TypeName` / `Sub.TypeName`
- 通配：`*Store`, `Demo.*.Node*`
- 模糊：编辑距离 (Damerau-Levenshtein) 阈值 ≤1（长度≤12），>12 长度则阈值 2。
- 大小写不敏感；内部存储使用小写索引。

## 3. 索引结构
- nameIndex: `lowerSimpleName -> List<TypeId>`
- fqnIndex: `lowerFqn -> TypeId`
- suffixIndex: 反转 FQN（含 `.`）构建 Trie，用于尾部匹配。
- cache (LRU, TTL=5m): 最近解析结果加速重复请求。

## 4. 解析顺序
```
1. 精确 FQN 匹配 (fqnIndex)
2. 后缀唯一匹配：查 suffixIndex，若命中集合大小=1
3. 通配符：query 含 * 或 ? → 转正则 (锚定 ^...$)；在 nameIndex + fqnIndex 扫描
4. 模糊：对所有 simple name（或限制候选<=500）计算编辑距离，取 distance<=阈值，排序
```

## 5. 排序与打分
候选评分：
```
score = 1 / (1 + distance) * (1 + namespaceDepth*0.05)
```
- distance=0 → 精确。
- namespaceDepth = FQN 中 `.` 分隔数量。
排序：distance ASC → namespaceDepth DESC → FQN 长度 ASC。

## 6. 歧义处理
若在步骤 1/2 仍出现多个（理论仅 2 不会）或步骤 3/4 有多个高分：
- 返回 `{ error: { code: "AmbiguousSymbol", candidates:[ { path, typeId, score }...] } }` （RPC）
- CLI 显示表格并提示用户选择（Phase1 可简化为提示并退出）。

## 7. 通配到正则转换
```
Esc(expr): 对正则保留字符 . + ? ^ $ { } ( ) [ ] | \ 进行反斜杠转义
Then: '*' -> '.*', '?' -> '.'
Regex = ^(converted)$, RegexOptions.IgnoreCase
```

## 8. 缓存策略
- Key: 原始 query 字符串（小写）
- 值：解析成功的 `ResolveResult` 或 Ambiguous 列表。
- 失效：LRU 容量默认 512；过期 TTL=5 分钟。

## 9. ResolveResult JSON 结构
```
{
  "resolved": { "path": "Demo.Core.NodeStore", "typeId": "T_ab12cd34" },
  "candidates": [ { "path": "Demo.Core.NodeStore", "typeId": "T_ab12cd34", "score": 1.0 } ],
  "redirectedFrom": null
}
```
Ambiguous 示例：
```
{
  "error": { "code": "AmbiguousSymbol", "message": "Query 'NodeStore' matches 2 symbols", "candidates": [ {"path": "A.NodeStore","typeId":"T_x1"}, {"path": "B.NodeStore","typeId":"T_y2"} ] }
}
```
NotFound 示例：
```
{ "error": { "code": "SymbolNotFound", "message": "'NodeStor' not found", "suggestions": ["NodeStore"] } }
```

## 10. 实现要点
- 构建索引阶段从 Roslyn Compilation 枚举 `INamedTypeSymbol`（仅 public/internal? 皆可，统一加入）。
- 后缀匹配：反转 FQN（`Namespace.Type` → `epyT.ecapseman`) 插入 Trie；查询时同样反转用户后缀。
- 模糊限制：若 simple names > 10k，先用首字符前缀桶过滤。
- 建议将简单编辑距离实现内置（O(nm)）; 短字符串常量优化：若 |len1-len2| > 阈值 → 直接跳过。

## 11. 统计与指标
- 解析成功计数 + 缓存命中计数：供 outlineCacheHitRatio 外单独统计 (phase1 可选)。
- P95 解析耗时：后续进入 Status。

---
(End of Appendix_SymbolResolveAlgorithms_P1)

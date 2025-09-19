# Appendix: State & Transition Specification (Version 0.5)

## 1. Entities
- semanticState: none | pending | generating | cached | stale
- changeClass: Structure | PublicBehavior | Internal | Docs | Cosmetic
- propagatedCause: DependencyStructure | null
- internalDriftState: { count:int, firstAt:timestamp|null, lastAt:timestamp|null }

## 2. Lifecycle
```
none -> (enqueue) pending -> (dispatch) generating -> (success) cached
cached -> (invalidate) stale -> (enqueue) pending
cached -> (Internal change) cached + internalChanged=true (unless immediate) -> (drift threshold) stale
```

## 3. Invalidation Rules
| 条件 | changeClass | propagatedCause | 动作 |
|------|-------------|-----------------|------|
| structureHash 变 | Structure | null | semanticState=stale; outlineVersion++ |
| publicImplHash 变 | PublicBehavior | null | semanticState=stale |
| internalImplHash 变 | Internal | null | internalChanged=true; drift++ |
| xmlDocHash 变 (semanticDocsAffect=true) | Docs | null | semanticState=stale; outlineVersion++ |
| xmlDocHash 变 (semanticDocsAffect=false) | Docs | null | outlineVersion++ |
| cosmeticHash 变 | Cosmetic | null | 无操作 |
| 依赖 Structure 变 | (保持原) | DependencyStructure | semanticState=stale |
| Internal 漂移阈值达成 | Internal | null | semanticState=stale; internalChanged=false; drift reset |

## 4. Drift Evaluation
在每次批处理结束或调度心跳：
```
if changeClass=Internal and !internalImplAffectsSemantic:
  increment drift count
  if drift.count >= cfg.internalDrift.count or (now-firstAt)>=cfg.internalDrift.hours:
     semanticState = stale
```

## 5. Priority Calculation
```
base = baseWeights[changeClass or propagatedCause mapping]
if propagatedCause=DependencyStructure and base < baseWeights.DependencyStructure:
  base = baseWeights.DependencyStructure
raw = (fanInWeight * fanIn + base) / (1 + depth)
priority = max(prevPriority * priorityDecay, raw)
```

## 6. Queue Persistence
- Snapshot 文件: `.codecortex/queue.snapshot.json`
- 字段: tasks:[{typeId, priority, state, attempts, lastError?, lastEnqueueAt}]
- 重启: generating->pending; stale 保持 stale; attempts 保留。

## 7. Semantic Task Result Handling
| 结果 | 动作 |
|------|------|
| NO SEMANTIC CHANGE | 更新 lastSemanticBase（structure/public/internal）; semanticState=cached; internalChanged=false; drift reset |
| 更新语义 | 覆盖/增量合并文件; 更新 lastSemanticBase; semanticState=cached; internalChanged=false; drift reset |
| 失败 | attempts++ ; error 记录; state=pending (若 attempts<3) else mark errorPersistent |

## 8. Outline Atomic Write
临时写入: `types/<id>.outline.md.tmp` -> fsync -> rename 替换; 更新 outlineVersion; 若 semanticState=generating 不阻塞（读取使用上一个稳定版本）。

## 9. SCC Group Handling
- 组 hash: H(sorted(TypeIds))
- 组任何成员 changeClass=Structure/PublicBehavior 使组 stale
- Internal 漂移: 成员 Internal 变化累计到阈值合并处理
- 拆分: 重算拓扑 -> 若组减少 -> 旧组语义文件移动到 `semantic/archive/<oldGroupHash>.md`

## 10. Conflict Resolution Order
```
Structure > PublicBehavior > Internal > Docs > Cosmetic
```

## 11. Propagation Guard
若类型已在本批次被标记 Structure / PublicBehavior，则忽略来自 propagatedCause=DependencyStructure 的重复 stale 标记。

---
(End of Appendix – State Transitions v0.5)

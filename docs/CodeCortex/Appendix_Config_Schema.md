# Appendix: Config Schema (Version 0.5)

> 该文件规范所有可持久化配置字段、默认值、作用点与生效阶段。运行时可在 `index.json.configSnapshot` 中查看快照。

## 1. 顶层结构
```jsonc
{
  "hashVersion": "1",
  "structureHashIncludesXmlDoc": false,
  "includeInternalInStructureHash": false,
  "internalImplAffectsSemantic": false,
  "semanticDocsAffect": false,
  "internalDrift": { "count": 5, "hours": 24 },
  "fanInWeight": 0.5,
  "baseWeights": {
    "Structure": 4.0,
    "PublicBehavior": 3.0,
    "Internal": 1.0,
    "DependencyStructure": 2.5
  },
  "priorityDecay": 0.7,
  "promptWindow": {
    "maxChars": 45000,
    "pinnedSoftRatio": 0.6,
    "degradeThresholdRatio": 0.15
  },
  "pack": {
    "maxDependencyDepth": 2,
    "tokenBudget": 120000
  }
}
```

## 2. 字段说明
| 字段 | 类型 | 默认 | 作用 | 影响阶段 |
|------|------|------|------|----------|
| hashVersion | string | "1" | hash 计算逻辑版本 | Hash 计算 |
| structureHashIncludesXmlDoc | bool | false | 是否将 XML 文档纳入 structureHash | 增量分类 |
| includeInternalInStructureHash | bool | false | 内部成员签名是否纳入结构 | 结构失效传播 |
| internalImplAffectsSemantic | bool | false | Internal 改动是否立即语义 stale | 分类→状态机 |
| semanticDocsAffect | bool | false | Docs 改动是否触发语义 stale | 分类→状态机 |
| internalDrift.count | int | 5 | Internal 漂移次数阈值 | Internal 延迟刷新 |
| internalDrift.hours | int | 24 | Internal 漂移最大静默时长 | Internal 延迟刷新 |
| fanInWeight | number | 0.5 | fanIn 在优先级中的权重 | 调度优先级 |
| baseWeights.Structure | number | 4.0 | Structure 基础权重 | 调度优先级 |
| baseWeights.PublicBehavior | number | 3.0 | PublicBehavior 权重 | 调度优先级 |
| baseWeights.Internal | number | 1.0 | Internal 刷新权重 | 调度优先级 |
| baseWeights.DependencyStructure | number | 2.5 | 依赖结构传播权重 | 调度优先级 |
| priorityDecay | number | 0.7 | 历史优先级衰减系数 | 调度稳定性 |
| promptWindow.maxChars | int | 45000 | Prompt 窗口字符预算 | Prompt 构建 |
| promptWindow.pinnedSoftRatio | number | 0.6 | Pinned 软上限比例 | Prompt 构建 |
| promptWindow.degradeThresholdRatio | number | 0.15 | 进入降级策略剩余比例 | Prompt 构建 |
| pack.maxDependencyDepth | int | 2 | Pack 依赖补全深度 | Pack 构建 |
| pack.tokenBudget | int | 120000 | Pack token 预算估算上限 | Pack 构建 |

## 3. 修改策略
1. 更改需写入 `.codecortex/config.json`（未来实现），当前阶段通过 CLI 设置并写快照。
2. 变更后需 bump `configSnapshot.generatedAt` 并将差异记录在 `logs/config_changes.log`。
3. 破坏性（影响 hashVersion）需触发全量重算：清除 types/*.outline.md 与 semantic 缓存（保留旧版本归档）。

## 4. 校验规则
- 数值范围：权重 > 0；maxChars ≥ 8000；tokenBudget ≥ 10000。
- 漂移阈值：count ≥1 且 hours ∈ [1,168]。
- 若 includeInternalInStructureHash = true 则须记录该事实在 index 根部 `flags` 集合。

## 5. 变更示例
```
codecortex config set internalImplAffectsSemantic true
codecortex config set internalDrift.count 3
```
(以上 CLI 计划中的接口样例)

---
(End of Appendix – Config Schema v0.5)

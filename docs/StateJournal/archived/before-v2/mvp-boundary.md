# StateJournal MVP 边界与 MVP-2 Backlog

> **创建日期**：2025-12-26
> **来源**：L1 全量审阅反馈 ([L1-Full-Review-Feedback.md](review/L1-Full-Review-Feedback.md))
> **状态**：草案，待畅谈会确认

---

## 📋 边界划分原则

根据监护人反馈，MVP 阶段的边界原则是：

| 范围 | MVP | MVP-2 |
|:-----|:---:|:-----:|
| **核心功能** | ✅ | — |
| **持久化语义** | ✅ | — |
| **崩溃恢复** | ✅ | — |
| **API 易用性** | ❌ | ✅ |
| **类型扩展设计** | ❌ | ✅ |
| **Detached 对象详细语义** | ❌ | ✅ |

---

## ✅ MVP 范围内（已修复）

### Fix-1: DiscardChanges Detached 行为 ✅

**来源**：V-1

**问题**：`DiscardChanges()` 在 Detached 时抛异常，规范要求 no-op（幂等）

**决策**：按规范修正实现

**完成**：
- [x] 修改 `DurableDict.DiscardChanges()` 的 Detached case 为 `return;`
- [x] 更新测试 `DiscardChanges_Detached_ThrowsException` → `DiscardChanges_Detached_IsNoop`
- [x] 测试通过：605/605

**修复日期**：2025-12-26

---

## 🔜 MVP-2 范围（记录待处理）

### MVP2-1: AteliaResult 适用边界细化

**来源**：V-2 + 监护人反馈

**问题**：`TryGetValue` 返回类型——规范要求 `AteliaResult<object>`，实现用 `bool + out`

**监护人观点**：
> 对于只有{是/否}/{有/无}这样简单情况，而没有"函数向调用方反馈——为何没有"这样的复杂情况的函数，应该允许简单的 `bool TryGetSomeValue(<params>, out <Type> ret)` 形式。
> 而需要返回"为何没有返回值"的复杂情况，则应用 `AteliaResult<T> GetSomeValue(<params>)` 形式。

**待办**：
- [ ] 组织 **畅谈会**：AteliaResult 适用边界
- [ ] 修订 `AteliaResult-Specification.md` §5.1
- [ ] 审阅所有 API 签名的一致性

**相关条款**：`[A-DURABLEDICT-API-SIGNATURES]`

---

### MVP2-2: DurableDict 外观设计

**来源**：U-1

**问题**：DurableDict 泛型形式 `DurableDict<TValue>` vs 非泛型

**监护人观点**：
> - 内部实现选择不成为类型特化容器，Value 是混杂集合（类似 JSON）
> - 不建议用泛型外观，因为我们不保存集合的泛型参数信息
> - 逻辑上是个 key 是整数的 JSON Object
> - 建议借鉴强类型语言中 JSON 读写库的外观设计经验

**待办**：
- [ ] 组织 **畅谈会**：DurableDict API 外观设计
- [ ] 调研：Newtonsoft.Json (JObject), System.Text.Json, BSON 等库的设计
- [ ] 设计决策：泛型外观 vs 非泛型 + 强类型 accessor

**相关问题**：
- LazyRef 是否应该是泛型的？（可合并讨论）

---

### MVP2-3: Enumerate vs Entries 命名

**来源**：U-2

**问题**：规范使用 `Enumerate()` 方法名，实现使用 `Entries` 属性

**归类**：API 易用性设计问题，MVP 阶段未覆盖

**待办**：
- [ ] 在 MVP2-2 畅谈会中一并讨论
- [ ] 统一 API 命名风格

---

### MVP2-4: Detached 对象成员访问语义

**来源**：U-3 + 监护人反馈

**问题**：`HasChanges` 在 Detached 时的行为归类

**监护人观点**：
> `HasChanges` 是个属性，暗含了可以无痛读取的语义，抛异常可能太重了。
> 建议方案：对于 Detached Object，访问函数就抛异常，访问属性则返回"延拓值"（类似给数学函数补上某些点的定义）。

**待办**：
- [ ] 组织 **畅谈会**：Detached 对象成员访问语义
- [ ] 定义"延拓值"规则（哪些属性返回什么值）
- [ ] 更新 `[S-DETACHED-ACCESS-TIERING]` 条款

---

### MVP2-5: LazyRef 集成策略

**来源**：U-4 + 监护人反馈

**问题**：LazyRef 与 DurableDict 集成是否为 MVP 范围

**监护人观点**：
> 对于 DurableDict，我认为不适用 LazyRef，因为 DurableDict 的内部实现如果用某种 `IDictionary<ulong, object>` 的话，就地用 Value 的槽位实现可能更自然——目标对象未 Load 之前存 ObjectId，Load 之后存 Load 出来的实例。

**待办**：
- [ ] 在 MVP2-2 畅谈会中讨论 LazyRef 定位
- [ ] 决策：LazyRef 是独立工具类 vs DurableDict 内部实现细节

---

## 📅 畅谈会计划

| # | 主题 | 关联问题 | 优先级 | 状态 |
|:-:|:-----|:---------|:------:|:----:|
| 1 | AteliaResult 适用边界 | MVP2-1 | P1 | ✅ [已完成](../../../agent-team/meeting/StateJournal/2025-12-26-ateliaresult-boundary.md) |
| 2 | DurableDict API 外观设计 | MVP2-2, MVP2-3, MVP2-5 | P2 | ✅ [已完成](../../../agent-team/meeting/StateJournal/2025-12-26-durabledict-api-design.md) |
| 3 | Detached 对象成员访问语义 | MVP2-4 | P2 | ✅ [已完成](../../../agent-team/meeting/StateJournal/2025-12-26-detached-access-semantics.md) |
| 4 | Detached 诊断作用域 | MVP2-4 延续 | P2 | ✅ [已完成](../../../agent-team/meeting/StateJournal/2025-12-26-diagnostic-scope.md) |

### 畅谈会 #1 结论

**主题**：AteliaResult 适用边界

**共识**：
- `DurableDict.TryGetValue` 使用 `bool + out` 是正确的（实现无需修改）
- 规范需要从"二分"升级为"三分"（`bool+out` / `AteliaResult` / 异常）
- 命名约定：`TryX(out)` vs `TryX(): Result` 通过签名区分

**已执行（2025-12-26 监护人批准）**：
- [x] 修订 `AteliaResult-Specification.md` §5.1 — 版本升级为 v1.1
- [x] 新增 §5.2 命名约定条款
- [x] 更新 `mvp-design-v2.md` `[A-DURABLEDICT-API-SIGNATURES]` 条款

### 畅谈会 #2 结论

**主题**：DurableDict API 外观设计

**已批准并实施（2025-12-26）**：
- [x] 改为非泛型 `DurableDict`（"文档容器"而非"类型容器"）✅
- [x] 引入 `ObjectId` 类型避免 `ulong` 语义不可判定 ✅
- [x] `DurableDict` 中不使用 `LazyRef` ✅
- [x] 测试全部通过 605/605 ✅

**写入 backlog（待后续畅谈会）**：
- [ ] DurableDict 与 Workspace 绑定方式（两层架构 vs 直接绑定）
- [ ] API 成员正式命名（先实施基础再定名字）

**监护人反馈要点**：
> 反对现在就确定两层架构设计，因为把 DurableObject 与 Workspace 分离还需要处理跨 Workspace 迁移问题。倾向于不新建 `DurableDictAccessor` 而是直接让 `DurableDict` 与 Workspace 绑定并提供易用性 Accessor。

### 畅谈会 #3 结论

**主题**：Detached 对象成员访问语义

**核心问题**：三条规则的冲突
- R1 (属性惯例)：属性不应抛异常
- R2 (Fail-Fast)：Detached 应抛异常
- R3 (幂等性)：DiscardChanges 应幂等

**共识**：
- **O2 (监护人直觉版) 不可接受**：延拓值与真值域碰撞导致规范不可判定
- **O1 作为底层规范**：Meta 成员不抛，Semantic 成员抛 `ObjectDetachedException`
- **规则优先级**：R2 > R3 > R1
- **延拓值的合法路径**：改类型 (O3) 或应用层 `SafeXxx()` 扩展 (O5)

**关键洞见——判据 D (Disjointness)**：
> 调用方仅通过返回值即可判定"这次返回是否来自 Detached 分支"。
> 如果延拓值与正常值域碰撞，则规范不可判定、测试不可判定。

**待监护人批准后执行**：
- [ ] 采纳 GPT 的条款草案更新 `[S-DETACHED-ACCESS-TIERING]`
- [ ] 可选：在应用层提供 `SafeXxx()` 扩展

---

## 📝 规范修订追踪

| 文档 | 待修订内容 | 关联畅谈会 |
|:-----|:-----------|:-----------|
| `AteliaResult-Specification.md` | §5.1 适用边界细化 | #1 |
| `mvp-design-v2.md` | `[A-DURABLEDICT-API-SIGNATURES]` 条款 | #2 |
| `mvp-design-v2.md` | `[S-DETACHED-ACCESS-TIERING]` 条款 | #3 |

---

## 变更日志

| 日期 | 变更 |
|:-----|:-----|
| 2025-12-26 | 初稿：从 L1 审阅结果提取 MVP-2 项目 |

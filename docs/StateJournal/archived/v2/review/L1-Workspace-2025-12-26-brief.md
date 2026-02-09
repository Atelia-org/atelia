# L1 审阅任务包：Workspace 模块

> **briefId**: L1-Workspace-2025-12-26-001
> **reviewType**: L1
> **createdBy**: Team Leader
> **createdAt**: 2025-12-26

---

## 🎯 焦点

**模块**：`atelia/src/StateJournal/Workspace/`

**specRef**:
- commit: HEAD (main branch)
- files:
  - `atelia/docs/StateJournal/mvp-design-v2.md` — §3.1.0.1 (对象状态管理), §3.1.2 (LoadObject), §3.1.3 (LazyRef)

---

## 📋 条款清单

### Group E: Identity Map & Dirty Set

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[S-DIRTYSET-OBJECT-PINNING]` | Dirty Set 强引用 | MUST 持有强引用直到 Commit 成功或 DiscardChanges |
| `[S-IDENTITY-MAP-KEY-COHERENCE]` | Key 一致性 | key 等于对象 ObjectId |
| `[S-DIRTY-OBJECT-GC-PROHIBIT]` | Dirty 对象不被 GC | 由强引用保证 |
| `[S-NEW-OBJECT-AUTO-DIRTY]` | 新建对象自动 Dirty | CreateObject 后立即加入 Dirty Set |
| `[S-STATE-TRANSITION-MATRIX]` | 状态转换矩阵 | 遵循规范定义的转换规则 |

**规范原文摘要**：

> **[S-DIRTYSET-OBJECT-PINNING]** Dirty Set MUST 持有对象实例的强引用，直到该对象的变更被 Commit Point 确认成功或被显式 `DiscardChanges`

> **[S-IDENTITY-MAP-KEY-COHERENCE]** Identity Map 与 Dirty Set 的 key 必须等于对象自身 `ObjectId`

> **[S-DIRTY-OBJECT-GC-PROHIBIT]** Dirty 对象不得被 GC 回收（由 Dirty Set 的强引用保证）

> **[S-NEW-OBJECT-AUTO-DIRTY]** 新建对象 MUST 在创建时立即加入 Dirty Set（强引用），以防止在首次 Commit 前被 GC 回收

### Group F: LazyRef

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[A-OBJREF-TRANSPARENT-LAZY-LOAD]` | 透明 Lazy Load | 读取 ObjRef 时自动 LoadObject |
| `[A-OBJREF-BACKFILL-CURRENT]` | 回填 _current | Lazy Load 后回填实例 |

**规范原文摘要**：

> **[A-OBJREF-TRANSPARENT-LAZY-LOAD]**：当 `TryGetValue`/索引器/枚举读取 value 且内部存储为 `ObjectId` 时，MUST 自动调用 `LoadObject(ObjectId)` 并返回 `IDurableObject` 实例。

> **[A-OBJREF-BACKFILL-CURRENT]**：Lazy Load 成功后，SHOULD 将实例回填到 `_current`（替换 `ObjectId`），避免重复触发 LoadObject。回填不改变 dirty 状态。

### Group G: Workspace API

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[A-LOADOBJECT-RETURN-RESULT]` | LoadObject 返回 Result | 不返回 null 或抛异常 |
| `[S-CREATEOBJECT-IMMEDIATE-ALLOC]` | CreateObject 立即分配 | 立即分配 ObjectId |
| `[S-OBJECTID-RESERVED-RANGE]` | ObjectId 保留区 | 0..15 保留，Allocator MUST NOT 分配 |
| `[S-OBJECTID-MONOTONIC-BOUNDARY]` | ObjectId 单调递增 | 对已提交对象集合单调递增 |
| `[S-TRANSIENT-DISCARD-OBJECTID-QUARANTINE]` | ObjectId 隔离 | Detached 对象 ObjectId 进程内不重用 |

**规范原文摘要**：

> **[A-LOADOBJECT-RETURN-RESULT]** LoadObject MUST 返回 `AteliaResult<T>` 而非 `null` 或抛异常

> **[S-CREATEOBJECT-IMMEDIATE-ALLOC]** `CreateObject<T>()` MUST 立即分配 ObjectId（从 `NextObjectId` 计数器获取并递增）

> **[S-OBJECTID-RESERVED-RANGE]** Allocator MUST NOT 分配 `ObjectId` in `0..15`

> **[S-OBJECTID-MONOTONIC-BOUNDARY]** ObjectId 对"已提交对象集合"MUST 单调递增

> **[S-TRANSIENT-DISCARD-OBJECTID-QUARANTINE]** Detached 对象的 ObjectId 在同一进程生命周期内 MUST NOT 被重新分配

---

## 🔍 代码入口

| 文件 | 职责 | 条款关联 |
|:-----|:-----|:---------|
| `Workspace/IdentityMap.cs` | ObjectId → WeakRef 映射 | S-IDENTITY-MAP-KEY-COHERENCE |
| `Workspace/DirtySet.cs` | Dirty 对象强引用集合 | S-DIRTYSET-OBJECT-PINNING, S-DIRTY-OBJECT-GC-PROHIBIT, S-NEW-OBJECT-AUTO-DIRTY |
| `Workspace/LazyRef.cs` | 延迟加载引用 | A-OBJREF-TRANSPARENT-LAZY-LOAD, A-OBJREF-BACKFILL-CURRENT |
| `Workspace/Workspace.cs` | Workspace API | A-LOADOBJECT-RETURN-RESULT, S-CREATEOBJECT-*, S-OBJECTID-* |

**相关测试**：
- `Workspace/IdentityMapTests.cs`
- `Workspace/DirtySetTests.cs`
- `Workspace/LazyRefTests.cs`
- `Workspace/WorkspaceTests.cs`

---

## 📚 依赖上下文

**前置条款**（来自 Core）：
- `IDurableObject` 接口定义
- `DurableObjectState` 枚举

**前置条款**（来自 Objects）：
- `DurableDict` 实现

---

## 📋 审阅指令

**角色**：L1 符合性法官

### MUST DO

1. 逐条款检查代码是否满足规范语义
2. 每个 Finding 必须引用：条款原文 + 代码位置 + 复现方式
3. 遇到规范未覆盖的行为 → 标记为 `U`（Underspecified），不是 `V`

### MUST NOT

1. 不评论代码风格（那是 L3）
2. 不假设规范未写的约束
3. 不产出无法复现的 Finding

### 特别关注

- **IdentityMap 使用 WeakReference**：确认 Clean 对象可被 GC
- **DirtySet 使用强引用**：确认 Dictionary<ulong, IDurableObject>
- **LazyRef 回填**：检查 `_storage = result.Value` 是否实现
- **Workspace.CreateObject**：检查是否同时加入 IdentityMap 和 DirtySet
- **ObjectId 分配**：检查 `_nextObjectId` 初始值是否为 16
- **ObjectId 隔离**：检查 Discard 后是否有机制防止重用

---

## 📤 输出格式

**文件**：`atelia/docs/StateJournal/review/L1-Workspace-2025-12-26-findings.md`

**格式**：EVA-v1（参见 Recipe）

---

## ⚠️ 注意事项

1. **ObjectId 隔离**：规范要求 Detached 对象的 ObjectId 进程内不重用，但实现可能没有显式的隔离机制。需要检查 `_nextObjectId` 是否只增不减。

2. **LazyRef 与 DurableDict 集成**：LazyRef 是独立类，但规范描述了与 DurableDict 的集成。需要检查 DurableDict 是否使用 LazyRef 处理 ObjRef 类型的值。

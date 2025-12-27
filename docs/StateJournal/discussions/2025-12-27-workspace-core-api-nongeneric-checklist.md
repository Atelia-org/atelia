# Workspace 核心 API 非泛型化：施工任务书（可执行清单，交付 Implementer）

日期：2025-12-27
状态：🟡 待实施

目标：在“保留 IDurableObject 作为协议面”的前提下，将 Workspace 的核心创建/加载/注册 API 收敛为**非泛型**，并将类型化与类型检查上移为便捷层 API。

范围：atelia/src/StateJournal（Workspace/IdentityMap/DirtySet/相关错误类型）与对应测试。

不考虑兼容：允许破坏性调整 API/类型/测试。

---

## A. 设计锚点（本任务遵循的意图）

- 设计示意文档（本次新增）：
  - [interpretations/workspace-core-api-nongeneric.md](../interpretations/workspace-core-api-nongeneric.md)
- 既有规范（SSOT）：
  - [mvp-design-v2.md](../mvp-design-v2.md)（Workspace/LoadObject/Materialize/Shallow/Lazy Load 相关条款）
  - [workspace-binding-spec.md](../workspace-binding-spec.md)

---

## B. 核心决策（Implementer 必须按此落实）

1. **Core Create API 非泛型化**：不再提供 `CreateObject<T>()` 作为核心入口。
2. **Core Load API 非泛型化**：新增/替换为 `LoadObject(ulong objectId) -> AteliaResult<DurableObjectBase>`。
3. **类型化与类型检查上移**：提供 `LoadDict`（以及未来 `LoadArray`）作为便捷层。
4. **收敛 Workspace 核心约束**：IdentityMap/DirtySet/RegisterDirty/ObjectLoaderDelegate 等核心路径只接入/返回 `DurableObjectBase`。
5. **保留 IDurableObject**：作为协议定义与测试替身的接口，不再作为 Workspace 核心 API 的接入类型。

---

## C. 文件级改动点（按优先级）

### C1. Workspace：引入非泛型 Core API

目标文件：[atelia/src/StateJournal/Workspace/Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs)

- 新增：
  - `public DurableDict CreateDict()`：
    - 分配 objectId（从 16 起，保留区规则保持不变）
    - 直接 `new DurableDict(this, objectId)`（避免反射 Activator）
    - 加入 IdentityMap + DirtySet
    - 返回 DurableDict
  - `public AteliaResult<DurableObjectBase> LoadObject(ulong objectId)`：
    - 先查 IdentityMap
    - miss → 走 loader/materialize（MVP 暂仍可用现有 delegate，但签名要收敛，见 C3）
    - 加入 IdentityMap（不加入 DirtySet）

- 新增便捷层（可在同文件或 partial）：
  - `public AteliaResult<DurableDict> LoadDict(ulong objectId)`：内部调用 `LoadObject` 后做类型检查
  - 可选：`public AteliaResult<T> LoadAs<T>(ulong objectId) where T : DurableObjectBase`

- 迁移处理（两种选一）：
  - 方案 A（推荐，破坏性最小但代码更多）：保留旧方法作 wrapper，并标注 `[Obsolete]`
    - `CreateObject<T>()` → 分流到 `CreateDict()`（MVP 仅支持 Dict）
    - `LoadObject<T>()` → `LoadObject(id)` + `LoadAs<T>(id)`
  - 方案 B（更干净）：直接删除旧泛型 API，并全仓库改调用点

### C2. IdentityMap / DirtySet：元素类型收敛到 DurableObjectBase

目标文件（按实际位置搜索）：
- atelia/src/StateJournal/Workspace/IdentityMap.cs
- atelia/src/StateJournal/Workspace/DirtySet.cs

改动要求：
- 内部集合从 `IDurableObject` 收敛为 `DurableObjectBase`（或等价基类）。
- `Add/TryGet/GetAll/Clear` 等 API 同步调整。

### C3. ObjectLoaderDelegate：签名收敛

目标文件：[atelia/src/StateJournal/Workspace/Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs)

- 将
  - `public delegate AteliaResult<IDurableObject> ObjectLoaderDelegate(ulong objectId);`
- 收敛为（择一）：
  - `public delegate AteliaResult<DurableObjectBase> ObjectLoaderDelegate(ulong objectId);`
  - 或更底层的“record/payload loader”（若 Implementer 正在推进 Phase 5，可提前为 materialize 做铺垫）

理由：避免把“只实现协议但不满足 workspace-binding”的对象塞进 Workspace 核心路径。

### C4. RegisterDirty：参数类型收敛

目标文件：[atelia/src/StateJournal/Workspace/Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs)

- `internal void RegisterDirty(IDurableObject obj)` → `internal void RegisterDirty(DurableObjectBase obj)`

### C5. 错误类型：类型错配错误可诊断

目标文件（按实际位置搜索）：
- ObjectTypeMismatchError

要求：
- `LoadDict/LoadAs<T>` 的类型错配错误必须包含：ObjectId + ExpectedType + ActualType（或 ActualKind）。

---

## D. 测试改动清单（必须同步）

目标：所有编译错误修复、单测全绿。

建议处理方式：

1. 搜索替换：所有 `CreateObject<DurableDict>()` → `CreateDict()`
2. 搜索替换：所有 `LoadObject<DurableDict>(id)` → `LoadDict(id)`
3. 若保留 `LoadAs<T>`：可用它替换原 `LoadObject<T>` 的断言式写法
4. `FakeDurableObject : IDurableObject` 测试若不再进入 Workspace 核心路径，可保留原样；若测试确实需要进入 Workspace，则让 fake 继承 DurableObjectBase（按测试目的选择）

---

## E. 风险与边界（Implementer 必须注意）

1. **API 可发现性**：非泛型化后必须提供至少 `CreateDict/LoadDict`，否则用户会到处 cast。
2. **暂时仍有 ObjectLoaderDelegate**：若短期无法推进 Phase 5，delegate 仍可用，但签名必须收敛到 `DurableObjectBase`。
3. **未来 ObjectKind 路由**：MVP 可 hardcode Dict，但不要把泛型 API 作为“路由机制”。

---

## F. 验收（Definition of Done）

- 代码层面：
  - Workspace 有 `CreateDict()` 与 `LoadObject(ulong)`（返回 `AteliaResult<DurableObjectBase>`）。
  - 存在类型化便捷入口 `LoadDict(ulong)`（至少一个）。
  - IdentityMap/DirtySet/RegisterDirty/ObjectLoaderDelegate 的核心路径类型已收敛（不再以 `IDurableObject` 作为接入类型）。

- 测试层面：
  - `dotnet test` 全绿。
  - 至少新增/改造 1 个用例覆盖：LoadObject 返回非预期类型时，LoadDict 给出可诊断错误。


# Workspace 核心 API 非泛型化（Design Sketch）

> 目的：给 Implementer/Reviewer 一个短文档，说明为什么以及如何将 Workspace 的 **核心创建/加载 API** 从“泛型 + 反射构造”收敛为“非泛型工厂 + 非泛型加载”，并把类型化/类型检查上移为便捷层。
>
> 适用范围：StateJournal MVP（当前实现仍可破坏性重构，不考虑兼容）。

---

## 1. 背景与动机

当前实现的核心问题不是“泛型写法不好”，而是**语义层级错位**：

- **Create**：目标类型应由“调用方选择的工厂入口”决定（例如 CreateDict/CreateArray），而不是 `CreateObject<T>()` 把类型参数传给 Workspace 再反射构造。
- **Load**：实例类型应由“底层数据（FrameTag/ObjectKind）”决定，而不是 `LoadObject<T>()` 让调用方先假设类型，加载后再做类型校验。

因此：

- 核心机制层（allocator / identity map / dirty set / deserialize+materialize）应尽量 **非泛型、少假设**。
- 类型化与类型检查应作为更表层的 **convenience API**（例如 LoadDict/LoadArray 或 LoadAs<T>）。

---

## 2. 术语（本页新增）

- **核心层（Core API）**：Workspace 的底层能力入口，负责“做事”，不负责“让用户写得爽”。
- **便捷层（Convenience API）**：对核心层做薄封装，提供类型化返回、类型断言、错误消息增强等 DX 功能。

---

## 3. 设计目标（Goals）

1. Workspace 的“创建”入口以非泛型工厂表达：CreateDict / CreateArray（未来扩展）。
2. Workspace 的“加载”底层入口以非泛型表达：LoadObject 返回 durable 基类实例（由数据决定类型）。
3. 类型化与类型检查上移为便捷层：LoadDict / LoadArray / LoadAs<T>。
4. 收敛不变量：Workspace 的核心路径只接收/返回 `DurableObjectBase`（或等价的“绑定 Workspace 的基类”）。
5. 仍保留 `IDurableObject` 作为“最小协议面（protocol surface）”，但不再作为 Workspace 核心 API 的接入类型。

---

## 4. 非目标（Non-Goals）

- 不在本变更中完成 RBF 的端到端加载（Phase 5 仍待实现）。
- 不在本变更中引入复杂的多态注册系统（但会为未来的 ObjectKind 路由留接口位）。
- 不在本变更中承诺向后兼容旧 API（允许破坏性重构）。

---

## 5. 建议的 API 形状

### 5.1 Core API（非泛型）

建议在 Workspace 提供（示意）：

```csharp
public sealed class Workspace {
    // Create
    public DurableDict CreateDict();
    public DurableArray CreateArray(); // 未来

    // Load (底层：由数据决定类型)
    public AteliaResult<DurableObjectBase> LoadObject(ulong objectId);
}
```

语义要点：

- CreateDict/CreateArray：由调用方选择入口 → 目标类型确定。
- LoadObject：按 objectId 查 identity map；miss 则按存储数据物化，最终返回“实际类型的实例”。

### 5.2 Convenience API（可选泛型/类型化）

建议提供薄封装：

```csharp
public sealed partial class Workspace {
    public AteliaResult<DurableDict> LoadDict(ulong objectId);
    public AteliaResult<DurableArray> LoadArray(ulong objectId); // 未来

    public AteliaResult<T> LoadAs<T>(ulong objectId) where T : DurableObjectBase;
}
```

语义要点：

- LoadDict/LoadAs<T> 不改变“加载路径”；只做类型检查、错误信息增强。
- 便捷层的错误必须包含 objectId + 实际 kind/type + 期望 type（可诊断、可行动）。

---

## 6. 不变量与边界（收敛点）

### 6.1 Workspace 核心路径只接入 DurableObjectBase

Core API 只处理 `DurableObjectBase`，理由：

- Workspace 绑定条款要求对象绑定 Owning Workspace；这属于基类可以强制的结构性不变量。
- 避免把测试替身/适配器（只实现协议但不满足绑定）注入生产路径。

`IDurableObject` 仍保留用于：

- 作为协议定义（状态/两阶段提交）
- 测试替身（不进入 Workspace 核心路径）
- 未来装饰器/代理或跨层边界的抽象（如存储 materialize 层）

### 6.2 由数据决定类型：ObjectKind 路由

未来接入 RBF 时，LoadObject 必须根据“磁盘记录携带的 kind/tag”决定创建哪个类型实例。

因此需要一个最小的路由位（示意）：

```csharp
internal interface IObjectKindRegistry {
    DurableObjectBase Materialize(Workspace ws, ulong objectId, ObjectKind kind, /* payload */);
}
```

MVP 可先 hardcode：kind=Dict → DurableDict。

---

## 7. 迁移策略（建议）

- 允许破坏性变更：直接删除 `CreateObject<T>` / `LoadObject<T>` 等核心泛型 API。
- 如果希望降低一次性重构摩擦，可临时保留旧泛型方法作为 wrapper（标注 Obsolete）：
  - `CreateObject<T>` 内部 switch 到对应的 CreateDict/CreateArray
  - `LoadObject<T>` 内部调用 `LoadObject` + `LoadAs<T>`
  - 待全仓库迁移完成后再移除 wrapper

---

## 8. 验收口径（Design-level DoD）

- Workspace 核心创建/加载 API 不依赖泛型 + 反射构造路径。
- Core LoadObject 返回 DurableObjectBase（由数据决定类型）。
- 存在至少一个类型化便捷入口（LoadDict），且类型错配错误可诊断。
- IdentityMap/DirtySet 等内部集合的元素类型收敛为 DurableObjectBase（或等价基类）。


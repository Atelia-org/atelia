# Workspace 绑定机制设计（增补规范）

> **状态**：已批准（监护人 2025-12-27）
> **前置**：[畅谈会 #5](../../../agent-team/meeting/StateJournal/2025-12-27-workspace-binding.md)
> **作者**：刘德智 (Team Leader)

---

## 1. 问题背景

### 1.1 设计缺口

`mvp-design-v2.md` §3.1.3 定义了透明 Lazy Loading 要求：

> **[A-OBJREF-TRANSPARENT-LAZY-LOAD]**：当读取 value 且内部存储为 `ObjectId` 时，
> MUST 自动调用 `LoadObject(ObjectId)` 并返回 `IDurableObject` 实例。

**但规范未定义**：`DurableDict` 如何获取 Workspace 引用以调用 `LoadObject`？

### 1.2 畅谈会共识

畅谈会 #5 达成核心共识：

1. 每个对象 MUST 绑定到且仅绑定到一个 **Owning Workspace**
2. 绑定不可变（对象生命周期内不改变）
3. Lazy Load 按 Owning Workspace 分派（不是按调用点 ambient）

### 1.3 监护人决策：分层设计

监护人在畅谈会后提出更清晰的分层设计，将问题拆分为：

- **Layer 1（核心）**：构造函数需要显式传入 Workspace
- **Layer 2（工厂）**：Workspace 提供工厂方法
- **Layer 3（便利，可选）**：Ambient Context 简化 Workspace 获取

---

## 2. 分层 API 设计

### 2.1 Layer 1：核心绑定（MUST）

**原则**：DurableObject 构造函数 MUST 接收 Workspace 引用。

```csharp
public abstract class DurableObjectBase : IDurableObject
{
    private readonly Workspace _owningWorkspace;
    
    /// <summary>
    /// 内部构造函数，由 Workspace 工厂方法调用。
    /// </summary>
    protected internal DurableObjectBase(Workspace workspace, ObjectId objectId)
    {
        _owningWorkspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ObjectId = objectId;
    }
    
    // Lazy Load 按 Owning Workspace 分派
    protected T LoadObject<T>(ObjectId id) where T : IDurableObject
    {
        return _owningWorkspace.LoadObject<T>(id);
    }
}

public class DurableDict : DurableObjectBase
{
    /// <summary>
    /// 内部构造函数。用户应通过 <see cref="Workspace.CreateObject{T}"/> 创建。
    /// </summary>
    internal DurableDict(Workspace workspace, ObjectId objectId)
        : base(workspace, objectId)
    {
        // ...
    }
}
```

**关键设计决策**：

| 决策 | 理由 |
|:-----|:-----|
| 构造函数 `internal` | 禁止用户直接 `new DurableDict()`，强制通过工厂 |
| `_owningWorkspace` 为 `readonly` | 绑定不可变 |
| 无默认构造函数 | 避免"无归属对象"的存在 |

### 2.2 Layer 2：工厂方法（MUST）

**原则**：用户通过 Workspace 实例的工厂方法创建/加载对象。

```csharp
public class Workspace : IDisposable
{
    /// <summary>
    /// 创建新的持久化对象。
    /// </summary>
    public T CreateObject<T>() where T : IDurableObject
    {
        var objectId = AllocateObjectId();
        var instance = ActivateDurableObject<T>(this, objectId);
        RegisterInIdentityMap(instance);
        RegisterInDirtySet(instance);  // 新对象自动 dirty
        return instance;
    }
    
    /// <summary>
    /// 加载已存在的持久化对象。
    /// </summary>
    public AteliaResult<T> LoadObject<T>(ObjectId objectId) where T : IDurableObject
    {
        // 1. 查 Identity Map
        if (TryGetFromIdentityMap(objectId, out T? cached))
            return AteliaResult<T>.Success(cached);
        
        // 2. 从磁盘加载
        var committed = Materialize(objectId);
        if (committed.IsFailure)
            return AteliaResult<T>.Failure(committed.Error);
        
        // 3. 创建实例并绑定
        var instance = ActivateDurableObject<T>(this, objectId, committed.Value);
        RegisterInIdentityMap(instance);
        return AteliaResult<T>.Success(instance);
    }
    
    /// <summary>
    /// 内部：激活 DurableObject 实例。
    /// </summary>
    private T ActivateDurableObject<T>(Workspace workspace, ObjectId id, ...)
    {
        // 使用反射或预注册的工厂创建实例
        // 关键：传入 workspace 引用
    }
}
```

**用户代码示例**：

```csharp
// 打开 Workspace
using var workspace = Workspace.Open("path/to/journal");

// 创建对象（通过工厂）
var dict = workspace.CreateObject<DurableDict>();

// 加载对象（通过工厂）
var result = workspace.LoadObject<DurableDict>(existingId);
if (result.IsSuccess)
{
    var loaded = result.Value;
}

// 提交
workspace.Commit();
```

### 2.3 Layer 3：Ambient Context（MAY，可选便利层）

**原则**：提供 Ambient Workspace 简化 API，但这是**可选特性**。

```csharp
/// <summary>
/// 可选的 Ambient Workspace 上下文。
/// </summary>
public static class StateJournalContext
{
    private static readonly AsyncLocal<Workspace?> _current = new();
    
    /// <summary>
    /// 当前执行上下文的 Workspace（可能为 null）。
    /// </summary>
    public static Workspace? Current => _current.Value;
    
    internal static void Set(Workspace? workspace) => _current.Value = workspace;
}

/// <summary>
/// 可选的作用域控制器。
/// </summary>
public readonly struct WorkspaceScope : IDisposable
{
    private readonly Workspace? _previous;
    
    public WorkspaceScope(Workspace workspace)
    {
        _previous = StateJournalContext.Current;
        StateJournalContext.Set(workspace);
    }
    
    public void Dispose() => StateJournalContext.Set(_previous);
}

/// <summary>
/// 可选的静态工厂（便利层）。
/// </summary>
public static class StateJournal
{
    /// <summary>
    /// 使用当前 Ambient Workspace 创建对象。
    /// </summary>
    /// <exception cref="InvalidOperationException">无 Ambient Workspace。</exception>
    public static T Create<T>() where T : IDurableObject
    {
        var workspace = StateJournalContext.Current
            ?? throw new InvalidOperationException(
                "No active WorkspaceScope. Use 'using (new WorkspaceScope(workspace))' " +
                "or call 'workspace.CreateObject<T>()' directly.");
        return workspace.CreateObject<T>();
    }
}
```

**Layer 3 使用示例**（可选）：

```csharp
// 用户选择使用 Ambient（便利但非必需）
using (new WorkspaceScope(workspace))
{
    var dict = StateJournal.Create<DurableDict>();  // 从 ambient 获取 workspace
}

// 或者，用户可以完全不用 Layer 3，直接用 Layer 2
var dict = workspace.CreateObject<DurableDict>();  // 显式传递，无魔法
```

---

## 3. 分层职责对照

| 层次 | 职责 | 实现复杂度 | 测试依赖 |
|:-----|:-----|:-----------|:---------|
| **Layer 1** | 对象持有 Workspace 引用 | 低 | 无 |
| **Layer 2** | 工厂方法封装创建/加载 | 中 | 需要 Mock Workspace |
| **Layer 3** | Ambient Context 便利 API | 低 | 无（或可选） |

**测试策略**：

- **单元测试**：主要测试 Layer 1/2，直接 Mock Workspace
- **集成测试**：可使用 Layer 3 简化 setup

```csharp
// 单元测试示例（不依赖 Layer 3）
[Fact]
public void LazyLoad_UsesOwningWorkspace()
{
    // Arrange
    var mockWorkspace = new Mock<Workspace>();
    var dict = new DurableDict(mockWorkspace.Object, new ObjectId(1));
    
    // ... 设置 mock ...
    
    // Act
    var value = dict["key"];
    
    // Assert
    mockWorkspace.Verify(w => w.LoadObject<DurableDict>(It.IsAny<ObjectId>()), Times.Once);
}
```

---

## 4. 规范条款（Normative）

### 4.1 Owning Workspace 绑定

**[S-WORKSPACE-OWNING-EXACTLY-ONE]（MUST）**

每个对外可见的 `IDurableObject` 实例 MUST 绑定到且仅绑定到一个 Workspace（*Owning Workspace*）。

**[S-WORKSPACE-OWNING-IMMUTABLE]（MUST）**

Owning Workspace 在对象生命周期内 MUST NOT 改变。

**[S-WORKSPACE-CTOR-REQUIRES-WORKSPACE]（MUST）**

`DurableObjectBase` 的构造函数 MUST 接收 `Workspace` 参数：
- 参数为 `null` 时 MUST 抛出 `ArgumentNullException`
- 构造函数 SHOULD 为 `internal` 或 `protected internal`，禁止用户直接调用

### 4.2 工厂方法

**[A-WORKSPACE-FACTORY-CREATE]（MUST）**

`Workspace.CreateObject<T>()` MUST：
1. 分配 ObjectId
2. 创建对象实例并传入 `this`（Workspace）
3. 注册到 Identity Map 和 Dirty Set
4. 返回对象实例

**[A-WORKSPACE-FACTORY-LOAD]（MUST）**

`Workspace.LoadObject<T>(ObjectId)` MUST：
1. 先查 Identity Map
2. 未命中则从磁盘 Materialize
3. 创建对象实例并传入 `this`（Workspace）
4. 注册到 Identity Map
5. 返回 `AteliaResult<T>`

### 4.3 Lazy Load 分派

**[S-LAZYLOAD-DISPATCH-BY-OWNER]（MUST）**

当触发透明 Lazy Load 时，MUST 使用对象的 `_owningWorkspace` 调用 `LoadObject`，
MUST NOT 使用调用点的 Ambient Workspace。

### 4.4 Ambient Context（可选）

**[A-WORKSPACE-AMBIENT-OPTIONAL]（MAY）**

实现 MAY 提供 `StateJournalContext.Current` 和 `WorkspaceScope` 作为便利层。

**[A-WORKSPACE-AMBIENT-NOT-REQUIRED]（MUST NOT）**

核心 API（Layer 1/2）MUST NOT 依赖 Ambient Context 的存在。
用户 MUST 能在不使用 Ambient 的情况下完成所有操作。

---

## 5. 类型层次

```
IDurableObject                    // 接口：对外契约
    │
    ├── DurableObjectBase         // 抽象基类：共享实现
    │       │
    │       ├── DurableDict       // 具体类型
    │       └── DurableArray      // 未来扩展
    │
Workspace                         // 工厂 + 生命周期管理
    │
    ├── CreateObject<T>()
    ├── LoadObject<T>(ObjectId)
    └── Commit()

StateJournalContext               // 可选：Ambient Context
WorkspaceScope                    // 可选：作用域控制
StateJournal                      // 可选：静态便利 API
```

---

## 6. 实施路线

### Phase 1：核心绑定（MUST）

1. 创建 `DurableObjectBase` 抽象基类
   - `internal` 构造函数接收 `(Workspace, ObjectId)`
   - `readonly _owningWorkspace` 字段
   - `protected LoadObject<T>()` 方法

2. 重构 `DurableDict` 继承自 `DurableObjectBase`
   - 移除 `public` 构造函数
   - 添加 `internal` 构造函数

3. 实现 `Workspace.CreateObject<T>()` 和 `Workspace.LoadObject<T>()`

### Phase 2：Lazy Loading

4. 在 `DurableDict` 读取路径实现透明 Lazy Load
   - `TryGetValue` 检查 value 是否为 `ObjectId`
   - 如果是，调用 `_owningWorkspace.LoadObject()`
   - 回填到 `_current`

### Phase 3：便利层（MAY）

5. 实现 `StateJournalContext` + `WorkspaceScope`
6. 实现 `StateJournal.Create<T>()` 静态工厂

---

## 7. 与现有规范的对齐

本文档为 `mvp-design-v2.md` 的**增补规范**，填补以下缺口：

| 原规范条款 | 本文档补充 |
|:-----------|:-----------|
| `[A-OBJREF-TRANSPARENT-LAZY-LOAD]` | 定义了 Workspace 获取机制 |
| `[A-OBJREF-BACKFILL-CURRENT]` | 通过 `_owningWorkspace` 实现 |
| §3.1.0.1 对象状态机 | 不引入新状态（无 Unbound） |

---

## 附录 A：护照模式隐喻

畅谈会中 Gemini 提出的隐喻，有助于理解设计：

| 概念 | 隐喻 | 实现 |
|:-----|:-----|:-----|
| **Owning Workspace** | 国籍 | `_owningWorkspace` |
| **构造时绑定** | 出生地原则 | 构造函数参数 |
| **绑定不可变** | 护照颁发后国籍不变 | `readonly` 字段 |
| **跨 Scope 访问** | 持证旅行 | 按 Owning 分派 Lazy Load |
| **无 Workspace 构造** | 真空窒息 | `ArgumentNullException` |

---

## 附录 B：设计权衡记录

### B.1 为什么不用"构造时捕获 Ambient"？

畅谈会原方案：`new DurableDict(id)` 在构造函数内部从 `StateJournalContext.Current` 捕获。

**问题**：
1. 测试必须设置 Ambient（即使只测试 Layer 1）
2. 忘记设置 Ambient 会导致运行时错误而非编译时错误
3. 构造函数有"隐藏依赖"，代码可读性差

**监护人决策**：分层设计，核心层不依赖 Ambient。

### B.2 为什么 Layer 3 是可选的？

- 高级用户可能有自己的 Workspace 管理策略
- 某些场景（如多 Workspace 同时操作）Ambient 反而增加复杂度
- 保持核心 API 的纯粹性

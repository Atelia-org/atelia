# Mixed Value Generator 备忘

> 日期：2026-03-23
> 状态：已与当前代码对齐（共享 catalog + 单一 generator）

---

## 0. 这份笔记回答什么问题

这份笔记是给后续 AI 会话和未来自己看的快速入口，目标是用最短路径回答下面几件事：

- Mixed Deque / Mixed Dict 现在的泛型分发是怎么做的？
- Source Generator 的元数据入口在哪，生成器主逻辑在哪？
- 哪些代码已经交给生成器，哪些仍然是手写保留区？
- 如果要新增 `Int128`、`Guid` 一类底层支持，应该改哪里？
- 如果生成结果不对，第一时间该看哪些文件？

---

## 1. 背景与目标

Mixed 容器原本有两类明显痛点：

- `DurableDeque` / `DurableDict<TKey>` 对外泛型入口大量依赖 `this is IDeque<TValue>`、`this is IDict<TKey, TValue>` 一类运行时接口分发。
- `DurableDeque.Mixed.cs`、`DurableDict.Mixed.cs` 中存在大段按类型展开的样板实现，新增一个受支持类型时很容易复制粘贴出错。

这轮重构的目标有两条：

- 用 `typeof(TValue) == typeof(...)` 的按类型分支替代接口探测，让 JIT 在泛型具现化后尽量常量折叠，走更直接的 `PushCore/GetCore/UpsertCore` 路径。
- 用 Source Generator 接管 typed view 和泛型分发样板，把“支持哪些 mixed value 类型”收敛到一个共享 catalog。

结果是：

- Mixed Deque 和 Mixed Dict 现在共用一套类型目录。
- 原先两个容器各自的 generator 已合并成一个 `MixedValueContainerGenerator`。
- 后续新增类型时，原则上优先只改 catalog，而不是再手写几十行接口实现。

---

## 2. 当前架构总览

### 2.1 一句话版

现在的结构是：

`MixedValueCatalog` 声明支持类型
→ `DurableDeque` / `DurableDict<TKey>` 通过 `[UseMixedValueCatalog(...)]` 挂接
→ `MixedValueContainerGenerator` 读取 catalog 元数据
→ 为不同容器生成泛型分发和 `IDeque<T>` / `IDict<TKey, TValue>` 实现。

### 2.2 角色分工

#### A. 共享类型目录

`src/StateJournal/MixedValueCatalog.cs`

- 这里是 mixed value 支持矩阵的单一事实来源。
- 每个 `[MixedValueType(...)]` 描述一个受支持值类型。
- `ValueType` 指外部 API 暴露的类型。
- `FaceType` 指内部 `ValueBox` 的 typed face。
- `PropertySuffix` 决定生成出来的 `OfInt32`、`OfString` 这类属性名后缀。
- `UseDurableObjectHelpers = true` 表示该类型不能直接套普通 `ValueBox.ITypedFace<T>` 路径，而要走手写的 `DurableObject` 装载/引用辅助逻辑。

#### B. 容器侧挂接点

`src/StateJournal/DurableDeque.Mixed.cs`

`src/StateJournal/DurableDict.Mixed.cs`

- 这两个文件现在都只是“手写骨架 + attribute 挂接 + 少量特殊逻辑”。
- 核心入口分别是：
  - `[UseMixedValueCatalog(typeof(MixedValueCatalog), MixedContainers.Deque)]`
  - `[UseMixedValueCatalog(typeof(MixedValueCatalog), MixedContainers.Dict)]`
- 生成器据此判断“要给哪个容器生成哪一套代码”。

#### C. 生成器入口

`src/StateJournal.Generators/MixedValueContainerGenerator.cs`

- 当前唯一的 mixed 容器生成器。
- 同时负责 deque 和 dict，两者只是在模板分支上不同。
- 主要生成两大块内容：
  - 泛型分发入口，如 `PushFront<TValue>`、`GetCore<TValue>`、`Upsert<TValue>`
  - typed view / 接口实现，如 `OfInt32`、`IDeque<int>`、`IDict<TKey, int>`

#### D. 生成器公共层

`src/StateJournal.Generators/MixedTypeGenerationCommon.cs`

- 放 generator 共享的 attribute 定义与元数据解析逻辑。
- 这里会在 post-init 阶段注入：
  - `MixedValueTypeAttribute`
  - `UseMixedValueCatalogAttribute`
  - `MixedContainers`
- 同时负责：
  - 把 catalog attribute 解析成 `TypeSpec`
  - 把容器类解析成 `TargetSpec`
  - 提供生成代码时复用的文件头、namespace、class header 拼接逻辑

---

## 3. 泛型分发现在是怎么工作的

### 3.1 Deque / Dict 的共同思路

生成后的对外泛型入口不再优先走：

```csharp
this is IDeque<TValue> typed
```

或：

```csharp
this is IDict<TKey, TValue> typed
```

而是按类型展开成：

```csharp
if (typeof(TValue) == typeof(int)) { ... }
if (typeof(TValue) == typeof(string)) { ... }
...
```

对于值类型分支，生成器会直接发出 `Unsafe.As<TValue, TExact>(ref value)`，把参数无装箱地喂给 `PushCore/SetCore/UpsertCore`。

这类代码的预期收益是：

- 避免 `isinst` / 接口查表的运行时开销
- 避免后续 interface dispatch
- 给 JIT 一个更容易常量折叠和内联的形状

### 3.2 DurableObject 是特殊分支

`DurableObject` 及其子类型不是纯 `ValueBox.ITypedFace<T>` 问题，而是“引用存储 + 延迟装载”问题，所以仍然保留手写 helper：

- `ToDurableRef(...)`
- `GetDurableObject(...)`
- `GetDurableObjectAt(...)`
- `PeekDurableObject(...)`

生成器只负责把 `DurableObject` 这个分支接回这些 helper，不自己生成对象装载语义。

---

## 4. 生成与手写的边界

### 4.1 已交给生成器的部分

- `DurableDeque` 的 `IDeque<T>` typed view 实现
- `DurableDeque` 的泛型写入/读取分发
- `DurableDict<TKey>` 的 `IDict<TKey, TValue>` typed view 实现
- `DurableDict<TKey>` 的泛型 `Upsert<TValue>` / `GetCore<TValue>` 分发
- `OfInt32`、`OfString`、`OfDurableObject` 这类重复属性

### 4.2 仍然手写保留的部分

- `ExactDouble` 专用 API
  - deque: `PushFrontExactDouble` / `PushBackExactDouble` / `SetFrontExactDouble` / `SetBackExactDouble`
  - dict: `UpsertExactDouble`
- `DurableObject` 的引用检查、`DurableRef` 转换、对象装载 helper
- 容器自身与 change tracker 相关的核心行为
  - `PushCore`
  - `SetCore`
  - `PopCore`
  - `UpsertCore`
  - `OnCurrentValueRemoved`
  - `OnCurrentValueUpserted`

保留这些手写区的原因很简单：

- 它们不是纯样板，而是带语义的策略点。
- 这些逻辑和 `Revision` / `DurableRef` / `ExactDouble` 语义绑定更深，不适合硬塞进一层通用模板。

---

## 5. 关键代码索引

优先看下面这些文件：

- `src/StateJournal/MixedValueCatalog.cs`
  - mixed value 支持矩阵
- `src/StateJournal/DurableDeque.Mixed.cs`
  - deque 的手写骨架与特殊 helper
- `src/StateJournal/DurableDict.Mixed.cs`
  - dict 的手写骨架与特殊 helper
- `src/StateJournal.Generators/MixedValueContainerGenerator.cs`
  - 生成器主模板
- `src/StateJournal.Generators/MixedTypeGenerationCommon.cs`
  - generator 公共元数据层

行为回归优先看测试：

- `tests/StateJournal.Tests/DurableDequeApiTests.cs`
- `tests/StateJournal.Tests/DurableDictApiTests.cs`

如果想理解底层 face 语义，看：

- `src/StateJournal/Internal/ValueBox*.cs`

---

## 6. 新增一个支持类型时怎么做

以新增 `Int128` 或 `Guid` 为例，建议按下面顺序检查。

### 6.1 先确认底层是否已有可用的 `ValueBox` face

先回答两个问题：

- `ValueBox` 能不能表达这个类型？
- 是否已经存在 `ValueBox.SomeFace : ITypedFace<T>`？

如果没有，先补底层存储语义，再谈 mixed 容器接入。

### 6.2 再把类型登记进 catalog

通常只需要在 `src/StateJournal/MixedValueCatalog.cs` 加一条：

```csharp
[MixedValueType(typeof(Int128), typeof(ValueBox.Int128Face), "Int128")]
```

如果这个类型只该出现在某一类容器，可以用 `Containers = ...` 约束。

如果这个类型需要像 `DurableObject` 一样走特殊 helper，则需要额外扩展 generator/common metadata；当前代码里只有 `UseDurableObjectHelpers` 这一个特殊语义开关。

### 6.3 最后补测试

至少补两类测试：

- API 行为测试：mixed deque / dict 是否能正确写入、读取、typed view 访问
- 语义测试：底层 `ValueBox` face 的 roundtrip / equality / diff 行为是否符合预期

一个重要经验是：

- 如果只是“新增普通标量类型”，理想状态下不应该再手改 `DurableDeque.Mixed.cs` 或 `DurableDict.Mixed.cs` 里的 typed view 样板。
- 一旦你发现自己在复制 `OfXxx`、`PushXxx`、`IDict<TKey, Xxx>` 这类代码，多半说明没有走对这套生成器路径。

---

## 7. 生成器出问题时先看哪里

### 7.1 先看元数据是否被正确读取

第一优先级检查：

- `MixedValueCatalog` 上的 attribute 是否写对
- `DurableDeque` / `DurableDict<TKey>` 上的 `[UseMixedValueCatalog(...)]` 是否写对
- `MixedTypeGenerationCommon.CreateTarget(...)` 是否成功解析出 `TargetSpec`
- `MixedTypeGenerationCommon.ParseCatalogTypes(...)` 是否正确过滤出目标类型

如果这里出错，后面的生成模板再对也没用。

### 7.2 再看模板分支是否走到了正确容器

`MixedValueContainerGenerator.RenderTarget(...)` 是主入口：

- `Deque` 走 `EmitDequeGenericDispatch(...)` 与 `EmitDequeTypedViews(...)`
- `Dict` 走 `EmitDictGenericDispatch(...)` 与 `EmitDictTypedViews(...)`

如果出现“deque 代码长得像 dict”或“某一侧少生成一段方法”，这里是首查点。

### 7.3 如果是 DurableObject 行为不对

先不要怀疑 catalog，优先看手写 helper：

- deque: `ToDurableRef`、`GetDurableObjectAt`、`PeekDurableObject`
- dict: `ToDurableRef`、`GetDurableObject`

因为 `DurableObject` 不是单纯模板展开问题。

### 7.4 如果是 double 精度语义不对

先区分你走的是哪条路径：

- 默认 `double` 走 `RoundedDoubleFace`
- `ExactDouble` API 走 `ExactDoubleFace`

这块刻意没有完全“模板化统一”，因为两条语义路径就是不同的。

---

## 8. 演进简史与当前取舍

这套实现不是一步到位的，当前形态大致经历了两次收敛：

### 8.1 从“每个容器一套样板”到“共享 catalog”

最初问题是 deque 和 dict 各自维护一大段类型展开代码，修改成本高，出错也隐蔽。

收敛后的关键思路是：

- 支持哪些类型，不应该分散写在多个容器文件里
- 应该有一个共享的 mixed value catalog 作为单一事实来源

### 8.2 从“两个 generator”到“一个 generator”

后续进一步发现：

- `MixedDequeGenerator`
- `MixedDictGenerator`

虽然目标容器不同，但大部分元数据读取和模板骨架高度相似。

所以当前又进一步合并为：

- 一个 `MixedValueContainerGenerator`
- 一个 `MixedTypeGenerationCommon`

这样做的收益是：

- 入口更少，AI 更容易建立全局心智模型
- 修改点更集中
- 后续如果要继续抽象成更高层的小 DSL，也更自然

代价也很明确：

- 当前 generator 里仍然保留了 deque / dict 两套模板分支
- 它还不是“完全声明式 DSL”，只是已经把重复度压到一个更合适的层级

这个取舍目前是合理的，因为它在“减少样板”和“不过度抽象”之间比较平衡。

---

## 9. 给后续 AI 会话的最短路线

如果你是新会话，建议按下面顺序建立上下文：

1. 先看 `src/StateJournal/MixedValueCatalog.cs`
2. 再看 `src/StateJournal/DurableDeque.Mixed.cs`
3. 再看 `src/StateJournal/DurableDict.Mixed.cs`
4. 然后看 `src/StateJournal.Generators/MixedTypeGenerationCommon.cs`
5. 最后看 `src/StateJournal.Generators/MixedValueContainerGenerator.cs`

这样能先理解“业务骨架和边界”，再理解“生成器如何把样板补齐”。

如果任务是“加一个新类型”，先从 catalog 和 `ValueBox` face 下手。
如果任务是“修生成器 bug”，先从 `CreateTarget / ParseCatalogTypes / RenderTarget` 三个点开始。

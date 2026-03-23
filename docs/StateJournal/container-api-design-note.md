# Container API Design Note

> 日期：2026-03-23
> 状态：已与当前 `DurableDeque` / `DurableDict` 实现对齐

---

## 0. 这份笔记回答什么问题

这份 note 用来回答后续容器 API 设计时最容易反复摇摆的几个问题：

- 什么应该算“核心原语”，应该留在接口/实例上？
- 什么只是 convenience，应该优先下沉到 static extension？
- 哪些 convenience 因为 C# 语言限制，反而应该保留为实例成员？
- `DurableDeque` 和 `DurableDict` 当前分别落在什么位置？
- 后续如果新增 `Stack`、`ListMap`、`PriorityDeque` 一类容器，应该套什么判断模板？

---

## 1. 总体设计原则

当前容器 API 采用三层结构：

### 1.1 核心原语层

这层应该满足：

- 是容器真正的原生能力，而不是包装语法糖。
- 失败语义明确、稳定，不依赖异常做常规控制流。
- 可以被 extension 机械组合成 throwing / try / default-value 变体。

典型形态：

- `GetIssue Get(..., out value)`
- `bool TrySet...(...)`
- `GetIssue Pop...(..., out value)`
- `UpsertStatus Upsert(...)`

### 1.2 Convenience 层

这层应该满足：

- 纯粹由公开原语组合而成。
- 不引入新的状态语义。
- 优先放到 static extension，减少接口和实例表面积。

典型形态：

- `GetOrThrow(...)`
- `TryGet(...)`
- `TryPeek...(...)`
- `GetOr(defaultValue)`

### 1.3 语义特化层

这层不是“方便写”的语法糖，而是带额外语义承诺的专用入口。

典型例子：

- `UpsertExactDouble(...)`
- `PushFrontExactDouble(...)`
- `TrySetFrontExactDouble(...)`
- `TryGetValueKind(...)`

这类 API 通常应保留为实例成员，因为它们不是从普通公开原语机械拼出来的。

---

## 2. 什么时候该下沉到 extension

满足下面三个条件时，优先下沉：

1. 该 API 只是公开原语的直接包装。
2. extension 版本不会明显损害调用语法。
3. 下沉后不会让错误语义变得更模糊。

`IDeque<TValue>` 当前就是这一型：

- 接口只保留 `GetAt/Peek/Pop/TrySet`
- `GetAtOrThrow/GetFrontOrThrow/TryPeekFront/TryPopBack/SetAtOrThrow` 都在 extension

`IDict<TKey, TValue>` 现在也走这一路线：

- 接口只保留 `Get` 与 `Upsert`
- `Get/GetOrThrow/TryGet/GetOr` 都在 extension

---

## 3. 什么时候必须保留实例成员

主要有两类情况。

### 3.1 C# 语言约束导致 extension 调用体验失真

这是这次 `DurableDict<TKey>` 收口里最关键的经验。

假设我们尝试写：

```csharp
public static TValue? GetOrThrow<TKey, TValue>(this DurableDict<TKey> dict, TKey key)
    where TKey : notnull
    where TValue : notnull
```

直觉上希望调用：

```csharp
dict.GetOrThrow<int>("count")
```

但这里 extension method 实际有两个泛型参数：`TKey` 和 `TValue`。  
C# 不支持“只显式写一部分泛型参数，剩下的继续推断”。一旦显式写了 `<int>`，编译器会把它当成“只给了第一个类型参数”，于是这类 API 在调用端就会变得别扭甚至不可用。

所以对 `DurableDict<TKey>` 这类“receiver 自身已带一个泛型、方法又要再引入一个泛型值类型参数”的场景：

- `GetOrThrow<TValue>(key)`
- `TryGet<TValue>(key, out value)`
- `Of<TValue>()`

如果想保留 `dict.GetOrThrow<int>("count")`、`dict.TryGet<double>(2, out v)`、`dict.Of<int>()` 这种调用形状，就应保留实例成员。

### 3.2 该能力没有足够的公开原语可供 extension 组合

比如：

- `TryGetValueKind(...)`
- `TryPeekFrontValueKind(...)`

这类 API 的底层依赖 `ValueBox` / `ValueKind` 的 mixed-specific 内部表示，而当前公共接口上没有一个“先拿 raw value kind 再包装”的原语，因此不能合理地下沉成 extension。

---

## 4. 当前裁决：DurableDeque

### 4.1 应保留在接口/实例上的核心原语

- `IDeque<TValue>.GetAt(int, out TValue?)`
- `IDeque<TValue>.PeekFront(out TValue?)`
- `IDeque<TValue>.PeekBack(out TValue?)`
- `IDeque<TValue>.TrySetAt(...)`
- `IDeque<TValue>.TrySetFront(...)`
- `IDeque<TValue>.TrySetBack(...)`
- `IDeque<TValue>.PopFront(out TValue?)`
- `IDeque<TValue>.PopBack(out TValue?)`
- `DurableDeque.GetAt<TValue>(int, out TValue?)`
- `DurableDeque.PeekFront<TValue>(out TValue?)`
- `DurableDeque.PeekBack<TValue>(out TValue?)`
- `DurableDeque.PopFront<TValue>(out TValue?)`
- `DurableDeque.PopBack<TValue>(out TValue?)`

### 4.2 应优先放在 extension 的 convenience

- `IDeque<TValue>.GetAt(index)` 的 throwing 变体
- `GetFrontOrThrow / GetBackOrThrow`
- `TryGetAt / TryPeekFront / TryPeekBack / TryPopFront / TryPopBack`
- `SetAtOrThrow / SetFrontOrThrow / SetBackOrThrow`
- mixed deque 的 `Get<TValue>(index) / GetFront<TValue>() / GetBack<TValue>()`

这些 API 现在都已经基本对齐到 extension 风格。

### 4.3 当前仍保留实例化的成员，且合理

- `DurableDeque.Of<TValue>()`
- `DurableDeque.OfInt32 / OfString / ...`
- `DurableDeque.TryPeekFrontValueKind(...)`
- `DurableDeque.TryPeekBackValueKind(...)`
- `DurableDeque.PushFrontExactDouble(...)`
- `DurableDeque.PushBackExactDouble(...)`
- `DurableDeque.TrySetFrontExactDouble(...)`
- `DurableDeque.TrySetBackExactDouble(...)`

理由：

- `OfInt32` 这类属性本来就不可能是 extension property。
- `Of<TValue>()` 更像“能力视图选择器”，而不是包装语法糖。
- `ValueKind` 读取没有合适的公开原语可下沉。
- `ExactDouble` 是特化语义，不是普通 wrapper。

### 4.4 仍可进一步考虑下沉的实例 convenience

这类 API 不是“必须实例化”，只是当前还没统一收口：

- `DurableDeque.TryGetAt<TValue>(...)`
- `DurableDeque.TryPeekFront<TValue>(...)`
- `DurableDeque.TryPeekBack<TValue>(...)`
- `DurableDeque.TryPopFront<TValue>(...)`
- `DurableDeque.TryPopBack<TValue>(...)`

因为 `DurableDeque` 自身不是泛型类型，所以这些 API 完全可以在未来下沉到 extension，而不会遭遇 `DurableDict<TKey>` 那种泛型参数推断问题。

---

## 5. 当前裁决：DurableDict

### 5.1 应保留在接口/实例上的核心原语

- `IDict<TKey, TValue>.Get(TKey, out TValue?)`
- `IDict<TKey, TValue>.Upsert(TKey, TValue?)`
- `IDict<TKey>.ContainsKey(TKey)`
- `IDict<TKey>.Remove(TKey)`
- `IDict<TKey>.Keys`
- `DurableDict<TKey>.Get<TValue>(TKey, out TValue?)`

说明：

- `Upsert` 不应 Try 化。它不是“状态不满足时可能失败”的操作，`UpsertStatus` 比 `bool` 更有表达力。
- `DurableDict<TKey>.Get<TValue>(..., out ...)` 是 mixed dict 的关键原语，它把“按任意支持类型读取”的能力显式化了。

### 5.2 应优先放在 extension 的 convenience

- `IDict<TKey, TValue>.Get(key)` throwing 变体
- `IDict<TKey, TValue>.GetOrThrow(key)`
- `IDict<TKey, TValue>.TryGet(key, out value)`
- `IDict<TKey, TValue>.GetOr(key, defaultValue/factory)`
- `DurableDict<TKey>.GetOr<TValue>(key, defaultValue/factory)`

这些 API 都能由公开原语稳定组合出来，继续放在 extension 最合适。

### 5.3 必须保留实例化的 convenience

下面这些是这次盘点里最重要的“语言例外”：

- `DurableDict<TKey>.TryGet<TValue>(TKey, out TValue?)`
- `DurableDict<TKey>.GetOrThrow<TValue>(TKey)`
- `DurableDict<TKey>.Of<TValue>()`
- `DurableDict<TKey>.OfInt32 / OfString / ...`

理由：

- 如果把 `TryGet<TValue>` / `GetOrThrow<TValue>` / `Of<TValue>()` 改成 extension，会引入 `TKey + TValue` 双泛型参数的显式调用问题，损害调用可用性。
- `OfInt32` 这类属性天生只能是实例成员。

### 5.4 必须保留实例化的语义特化成员

- `DurableDict<TKey>.TryGetValueKind(TKey, out ValueKind)`
- `DurableDict<TKey>.UpsertExactDouble(TKey, double)`

理由：

- `TryGetValueKind` 依赖 mixed 内部表示，没有可公开复用的原语。
- `UpsertExactDouble` 不是普通 `Upsert<double>` 的语法糖，而是“写入语义不同”的专用入口。

### 5.5 已经明确不应保留在接口层的东西

- `IDict<TKey, TValue>` 默认索引器

原因不是单纯“想缩减表面积”，而是它会把：

- `TypeMismatch`
- `UnsupportedType`
- `LoadFailed`

等错误错误地折叠为 `KeyNotFoundException`，造成语义失真。

---

## 6. 给后续新容器 API 的判断模板

设计新容器时，建议按下面顺序判断每个候选 API：

### 6.1 先问：这是原语还是包装？

如果它只是：

- 把 `GetIssue` 变成 `bool`
- 把 `bool` 失败变成异常
- 给失败补一个默认值

那它大概率应该是 extension，而不是接口/实例成员。

### 6.2 再问：extension 会不会损害调用形状？

重点看 receiver 的泛型形状：

- receiver 若已经完全确定，如 `IDeque<TValue>`、`IDict<TKey, TValue>`，extension 通常很合适。
- receiver 若仍带未固定的泛型参数，如 `DurableDict<TKey>`，而方法本身还要引入新的显式泛型参数，则要警惕 C# 泛型推断限制。

### 6.3 再问：这是不是特化语义，而不是包装？

如果一个 API 表示：

- 精确浮点写入
- 读取 value kind
- 裸值视图选择
- durable subtype 访问策略

那它通常不应该被归类为 extension 语法糖，而应视为容器自身的实例能力。

### 6.4 最后问：是否会扭曲错误语义？

如果某个 convenience 会把多个失败原因压扁成一种错误，那宁可删掉，也不要留在接口层。

---

## 7. 当前可执行结论

面向后续重构和新增容器，当前建议作为项目内默认规则：

- 接口只放核心原语，不放 throwing sugar。
- 能由公开原语机械组合出来的 convenience，优先下沉到 extension。
- mixed 容器如果因为 C# 泛型调用限制而无法优雅 extension 化，可以保留少量实例 convenience。
- `ValueKind` / `ExactDouble` 一类 API 视为语义特化层，不按普通 convenience 处理。
- 遇到会扭曲错误语义的默认索引器或 throwing 包装，应优先删除，而不是兼容保留。

这套规则已经在 `Deque` 与 `Dict` 两条线上验证过，后续新容器建议直接沿用，而不是重新从“接口要不要带糖”“try 要不要和 throw 并存”开始讨论。

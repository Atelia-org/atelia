# StateJournal DurableObject Freeze/Fork Roadmap

> 目标：为 `StateJournal` 的各类 `DurableObject` 补齐更完整的对象级 `Freeze/Frozen` 与 committed clone 能力，作为未来对象图级 `frozen snapshot` / `mutable fork` 的技术基础。  
> 服务对象：`docs/Agent/agent-core-branching-infrastructure-backlog.md` 中的 branch workspace、fork context、旁路推演、retained result 等上层能力。  
> 状态：近期路线图草案。  
> 相关文档：
> - [`frozen-durable-object-design.md`](frozen-durable-object-design.md)
> - [`fork-as-mutable-design.md`](fork-as-mutable-design.md)
> - [`replay-based-fork-design.md`](replay-based-fork-design.md)
> - [`../Agent/agent-core-branching-infrastructure-backlog.md`](../Agent/agent-core-branching-infrastructure-backlog.md)

---

## 1. 一句话结论

近期主线建议是：

1. 先把各类 `DurableObject` 的 `Freeze/Frozen` 能力补齐到“能降内存就降内存，不能降也至少有正确语义”的程度。
2. 再逐类补齐对象实例级 `ForkCommittedAsMutable()` 快路径。
3. 与此同时保留并继续依赖已有的 `Repository.ReplayCommitted(...)` 作为统一 fallback。
4. 最终把这些对象级能力组合成对象图级 `frozen snapshot` / `mutable fork`。

这条路线在设计上是合理的，在实现上也是可行的，而且与 Agent.Core 的 branching backlog 是对齐的。

---

## 2. 为什么这条路线值得做

未来上层真正需要的不是“某几个容器碰巧能 freeze/fork”，而是一套更完整的对象级能力栈：

- readonly 模板对象能长期驻留，且内存比可变工作态更轻
- committed 对象能稳定派生出新的 mutable 工作副本
- 对象图级 branch workspace / fork context 能建立在统一的 frozen / mutable materialization 语义上
- crash recovery / audit / replay 时，底层 snapshot 语义更清楚

换句话说，这不是单个容器 API 的小修，而是在给未来这些上层能力打地基：

- `frozen snapshot from root`
- `mutable fork from root`
- branch-local workspace
- 旁路推演后只带结果回主上下文

---

## 3. 当前基础与现实边界

当前主线里，已经有几块重要基础设施到位：

- `DurableObject` 已有 object-level `IsFrozen` / object flags / mutability dirty 骨架
- `ForkCommittedAsMutable()` 快路径已覆盖 `DurableDict` / `DurableHashSet` / `DurableDeque`
- `LoadMaterializationMode` 已打通，load/replay 路能按 `Normal` / `ForceMutable` / `ForceFrozen` 物化
- `Repository.ReplayCommitted(...)` 已提供统一 committed clone fallback
- `DurableOrderedDict` / `DurableText` 已可通过 replay 路做 committed mutable clone

这意味着接下来的重点不再是“先把 clone 勉强做出来”，而是：

- 为每种对象做出更真实的 frozen 形态
- 再逐步把高频对象的 fast-path fork 补到位

---

## 4. 总体原则

### 4.1 先追求对象级真实 frozen shape，再追求 fork 快路径全覆盖

`Freeze()` 的价值不应只停留在：

- `IsFrozen == true`
- mutating API 抛 `ObjectFrozenException`

更重要的是：

- 能拆掉 dirty tracking
- 能拆掉 captured originals / edit staging
- 能拆掉只服务于编辑态的辅助结构

如果某个类型确实能在 frozen 后明显省内存，就应该优先实现这种“真 frozen shape”。

### 4.2 committed clone 先保证“可用”，再追求“快”

不要把“所有类型都必须先有对象实例级 fast fork”当成上层 branching 的前置阻塞。

因为当前已经有：

```csharp
repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable)
```

它虽然慢一些，但语义正确、类型更统一，足以作为基线能力。

因此更合理的节奏是：

- 先保证每类对象至少有一条 committed clone 路径可用
- 高频类型再补对象实例级快路径 `ForkCommittedAsMutable()`

### 4.3 graph-level snapshot/fork 是下一层合同，不要从对象级能力自动外推

即使每个对象都支持：

- `Freeze()`
- committed clone

也**不等于**对象图级 `frozen snapshot` / `mutable fork` 已经天然成立。

对象图层还要单独定义：

- shallow 还是 deep
- child refs 是共享还是复制
- shared subgraph 如何处理
- root reachability 如何收边界

所以近期路线图应明确分层：

- 先做对象级能力
- 再做对象图级组合能力

### 4.4 `OrderedDict` / `DurableText` 的 frozen 形态坚持“只拆不新增集合实现”

这是本路线图的一个明确约束。

对 `DurableOrderedDict` / `DurableText`，近期不建议：

- 再发明一套“只读专用集合实现”
- 维护 mutable/frozen 两套并行核心结构

建议策略是：

- 继续沿用现有核心实现
- 在 frozen 形态下，把只服务于编辑能力的内部结构拆空或置 `null`
- 让读取能力继续复用现有主结构
- 如果某类结构实在难以安全拆掉，就退化为：
  - 只保留 `IsFrozen` 语义
  - 所有 mutating API 拦截
  - 内存收益有限但合同正确

也就是说：

> **只拆，不新增集合实现；能瘦身就瘦身，不能瘦身就接受软拦截。**

---

## 5. 对各类型的近期判断

### 5.1 `DurableDict`

当前状态：

- freeze/frozen 已有较完整实现
- fast-path `ForkCommittedAsMutable()` 已有

近期任务：

- 继续作为其他类型的参考样板
- 保持 replay / fast-path / freeze 三条语义的一致性

角色：

- 参考实现
- 语义基线

### 5.2 `DurableHashSet`

当前状态：

- freeze/frozen 已有
- fast-path fork 已有

近期任务：

- 继续作为“非 map 型 tracker”样板
- 关注 typed-only 路线的长期边界即可

### 5.3 `DurableDeque`

当前状态：

- freeze/frozen 已有
- fast-path fork 已有

近期任务：

- 继续作为 sequence 型 tracker 样板
- 对 wrap-around / owned payload / child refs 继续保持测试密度

### 5.4 `DurableOrderedDict`

当前状态：

- 还没有 public `ForkCommittedAsMutable()`
- 已支持 `Repository.ReplayCommitted(..., ForceMutable)`
- `Freeze()` / true frozen shape 仍未实现

近期策略：

- 先补 `Freeze/Frozen`
- frozen 形态坚持“只拆不新增集合实现”
- 不强求一步到位获得和 dict 同级别的内存收益
- 如果某些编辑辅助结构可安全置空，就置空
- 如果很难拆，就先保留主结构，仅用 `IsFrozen` 做语义拦截

之后再做：

- 对象实例级 `ForkCommittedAsMutable()` 快路径

优先级判断：

- frozen 先于 fast fork

### 5.5 `DurableText`

当前状态：

- 没有 public `ForkCommittedAsMutable()`
- 已支持 `Repository.ReplayCommitted(..., ForceMutable)`
- `Freeze()` / true frozen shape 仍未实现

近期策略：

- 与 `OrderedDict` 相同，优先补 frozen 语义和可行的瘦身
- 不引入两套 text core
- 只拆掉明确只服务于编辑的辅助结构
- 若拆不动，就退化为软 frozen

之后再做：

- 对象实例级 committed fork 快路径

优先级判断：

- frozen 先于 fast fork

---

## 6. 推荐实施顺序

### Phase A：把“frozen 形态路线”从个别类型推广成统一方法论

目标：

- 明确每类对象 frozen 后哪些结构可丢
- 明确哪些类型只能先做软 frozen
- 确保 `Normal` / `ForceMutable` / `ForceFrozen` 的 load materialization 合同始终一致

交付物：

- 每个对象类型一份最小 frozen 策略
- 补对应 roundtrip / reopen / replay / error 语义测试

### Phase B：优先补 `DurableOrderedDict` 的 `Freeze/Frozen`

原因：

- 它更接近将来 branch/index/workspace 的结构需求
- 目前已经有 replay clone fallback，可先不急着补快路径 fork
- 其 frozen shape 难度高，值得尽早摸清边界

要求：

- 只拆，不引入第二套 ordered structure
- 能 `null` 掉的编辑辅助结构就 `null`
- 拆不动的部分允许保留，只要 frozen 合同明确

### Phase C：补 `DurableText` 的 `Freeze/Frozen`

原因：

- 在 Agent / branch workspace 语境下，文本对象很容易成为只读历史或模板
- 但它和 `OrderedDict` 一样，不值得为 frozen 单独再做一套新 core

要求：

- 只拆，不新增第二套 text structure
- 允许先实现“语义正确但收益有限”的软 frozen

### Phase D：再逐类补对象实例级 `ForkCommittedAsMutable()` 快路径

优先建议：

1. `DurableOrderedDict`
2. `DurableText`

原因：

- 这两类现在已有 replay fallback，功能上不堵
- 补 fast-path 后，模板 spawn / branch-local draft 的成本会进一步下降

但要牢记：

- replay fallback 已经存在
- fast-path 是优化，不是唯一正确路径

### Phase E：在对象级能力稳定后，再设计 graph-level `frozen snapshot` / `mutable fork`

届时再正式回答：

- 是否递归遍历 root 可达子图
- child refs 是共享还是复制
- 如何处理 shared subgraph
- snapshot/fork 的结果怎样回挂到同一 revision 或新工作区

---

## 7. 对 `Repository.ReplayCommitted(...)` 的定位

这条能力不应被视为“过渡脏方案”，而应被明确保留：

- 它是统一 fallback
- 它不依赖具体对象暴露 committed tracker clone
- 它非常适合作为 graph-level branch/snapshot 原语的底层保底路径

因此近期路线不是“等 fast fork 全补齐后废掉 replay”，而是：

- replay 长期保留
- fast-path 按收益逐类补

这会让整体系统更稳，也更容易分阶段推进。

---

## 8. 成功标准

如果这条路线走对了，近期应看到下面这些结果：

1. 所有主要 `DurableObject` 类型都具备清晰的 frozen 合同。
2. 能明显省内存的类型，在 frozen 后确实会释放编辑态辅助结构。
3. `OrderedDict` / `DurableText` 即使不能大幅瘦身，也至少具备正确且一致的 frozen 语义。
4. 所有主要类型至少都有一条 committed clone 路径：
   - fast-path，或
   - `Repository.ReplayCommitted(...)`
5. 高收益类型逐步补齐对象实例级 `ForkCommittedAsMutable()`。
6. 后续设计对象图级 `frozen snapshot` / `mutable fork` 时，不需要再倒回头重做对象级合同。

---

## 9. 当前不建议做的事

- 不要为 `OrderedDict` / `DurableText` 新发明 frozen 专用集合实现。
- 不要为了“API 矩阵整齐”而硬把所有类型同时拉进 fast-path fork。
- 不要在对象级路线尚未稳定前，就把 graph-level deep snapshot/fork 语义草率写死。
- 不要把“内存没明显下降”直接视为失败；对某些复杂类型，只要 frozen 合同正确、结构没有明显恶化，就是可接受的第一步。

---

## 10. 一句话收束

这条近期路线的核心不是“把所有类型的 API 表面补齐”，而是：

> **先让每种 DurableObject 都拥有可信的 readonly / mutable 物化语义，再把高收益类型逐步优化成更轻的 frozen shape 和更快的 committed fork，从而为未来对象图级 branching 能力打下稳定基础。**

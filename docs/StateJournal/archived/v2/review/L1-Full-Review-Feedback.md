本我是对“atelia/docs/StateJournal/review/L1-Full-Review-Summary.md”的反馈

## AI Team 监护人--刘世超的发言：

关于“V-1: [A-DISCARDCHANGES-REVERT-COMMITTED] — Detached 时抛异常”
  我认为应该按规范修正，改为`DiscardChanges()` 在 Detached 时为 **no-op（幂等）**，实施文档中的修复建议。

关于“V-2: [A-DURABLEDICT-API-SIGNATURES] — TryGetValue 返回类型”
  我认为应该细化“atelia/docs/AteliaResult-Specification.md”中的适用边界，对于只有{是/否}/{有/无}这样简单情况，而没有“函数向调用方反馈--为何没有”这样的复杂情况（相当于替代了throw Exception）的函数，应该运行简单的`bool TryGetSomeValue(<params>, out <Type> ret)`形式。而需要返回“为何没有返回值”的复杂复杂情况，则应用`AteliaResult<T> GetSomeValue(<params>)`形式。这是我的个人观点，算1票。
  但是任何规范修订，我认为应该征询Advisor组的意见，有难以弥合的争议时再投票，我也只有1票不能强推某个提案通过，但是我有否决权。也许这该形成我们项目内的成文制度。

关于“U-1: DurableDict 泛型形式”
  在实现方面，我们的DurableDict确实选择了不成为类型特化容器，Value是混杂集合。这很大程度上受JSON的影响，因为库的最初动机之一是对位替代json lines。混在类型实现简单，用起来像duck type，比较灵活。
  在易用性方面，暴露怎样的外观是个API设计问题。我不建议用泛型外观，因为我们许诺容器的类型特化，在序列化和反序列化时目前也不保存集合的泛型参数信息。逻辑上是个key是整数的JSON Object。而暴露C#外观方面，我认为可以借鉴前人在强类型语言中设计JSON读写库的外观设计经验，我们博采众长制定最适合我们的方案，这很可能需要一次**畅谈会**。
  关于未来可能的泛型化演进，可以参考JVM和dotnet早期引入泛型时的路径，参考我们的需求选择在序列化层添加何种级别的类型信息。
  `LazyRef<T>`是否应该也是泛型的问题，可以在此合并。

关于“U-2: Enumerate vs Entries 命名”
  同样是API设计问题，是之前MVP阶段不覆盖的易用性设计。

关于“U-3: HasChanges Detached 行为”
  这也是之前MVP阶段为了聚焦核心问题，而没有展开探讨的。`HasChanges`是个属性，暗含了可以无痛读取的语义，抛异常可能态重了。我提议专门就 Detached Object 的成员访问问题问题再安排一次**畅谈会**。我建议的方案是对于 Detached Object, 访问函数就抛异常，访问属性则返回“延拓值”，类似给数学函数补上某些点的定义。

关于“U-4: LazyRef 与 DurableDict 集成”
  这同样是源于MVP阶段聚焦骨架，而未展开易用性和类型扩展方面的深入分析。`LazyRef`被设想为可复用的value slot，但目的是简化和优化容器类型的开发，不能机械的为了使用工具而使用工具，而应该只在确实能起效的地方使用。
  具体来说对于`DurableDict`， 我认为不适用`LazyRef`, 因为`DurableDict`的内部实现如果用某种`IDictionary<ulong, object>`的话，就地用Value的槽位实现可能更自然，目标对象未Load之前存ObjectId，Load之后存Load出来的实例。Upsert Transient DurableObject时直接存实例。
  以上是我的初步分析，有可能不全面和准确，供抛砖引玉。
  
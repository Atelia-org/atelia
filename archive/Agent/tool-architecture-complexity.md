# Completion Tools 架构复杂性备忘

> **读者**：正在讨论 `Atelia.Completion.Tools` / `prototypes/Agent.Core` 中 tool runtime API 形状的人。
> **文档性质**：讨论稿，不是最终 RFC；目标是把复杂性来源、候选方案与取舍讲清楚，便于继续征询意见。
> **最后更新**：2026-06-08

---

## 1. 问题背景

当前 `Completion.Tools` 主线大致是这条链：

```text
ITool / ToolDefinition
    ↓ 注册
ToolRegistry
    ↓ session 绑定
ToolSessionState + ToolAccessPolicy
    ↓ 执行壳
ToolExecutor
    ↓ 单次调用
ToolExecutionContext
```

这套拆分并不是偶然“长复杂了”，而是服务于一个真实目标：

- **工具能力（capability）** 尽量静态、可共享、可缓存。
- **工具状态（state / context）** 按 agent session / turn 绑定，避免污染共享实例。
- **执行边界** 需要携带本轮可见性、`services`、`items`、执行序号等运行时信息。

如果只看语义，这样拆是合理的。
但如果只看使用体验，就会出现两个常见感受：

- “为什么我要同时理解 `ToolRegistry`、`ToolSessionState`、`ToolExecutor` 三个概念？”
- “`ToolAccessPolicy` 究竟是临时的 hidden-list，还是未来会扩展成复杂策略系统？”

换句话说，**复杂性并不主要来自 tool schema 反射，而是来自‘静态能力与动态状态分离’这一优化导向设计**。

---

## 2. 复杂性究竟从哪里来

这个问题可以拆成 4 个来源。

### 2.1 双重真相源的风险

如果把“工具能力”和“当前 session 能不能看见/执行它”混在同一个 `ITool` 实例里，就会很快出现：

- 多个 agent session 共享同一实例时相互污染
- 本轮模型可见性与下一轮执行授权不一致
- 为了性能缓存了 registry 后，又不得不在实例上打补丁

因此拆成“静态注册表 + 动态绑定态”几乎是必然的。

### 2.2 高性能路径希望少做重复工作

对于大量 agent / 高频 tool loop，通常会希望：

- `ToolDefinition` 只反射一次
- 工具名查找尽量走预编译索引
- 可见工具集合不要每次调用都重新扫描/解释规则
- 单次调用只构造最薄的 `ToolExecutionContext`

这会自然把系统推向“编译产物”和“运行时快照”分离。

### 2.3 对外 API 与内部优化关注点不同

框架内部关心的是：

- 哪些对象可共享
- 哪些对象可复用
- 哪些结构适合缓存或按需编译

使用方关心的是：

- 我如何拿到当前 session 的 `VisibleToolDefinitions`
- 我如何执行一个 `RawToolCall`
- 我如何把 `services/items` 塞进去

如果直接把内部结构原封不动暴露给使用方，API 就会显得“比业务真实需要更碎”。

### 2.4 “Policy” 一词容易引入额外想象

`ToolAccessPolicy` 这个名字本身会让人预期：

- 未来可能有 allow/deny precedence
- 可能有规则解释、动态谓词、环境条件
- 可能区分 visibility / executability / cost / priority

但当前实现其实只是“隐藏若干工具名”的极简快照。
这会带来命名和预期的不对齐。

---

## 3. 这类复杂性在业内常见吗

很常见，而且并不只出现在 LLM tool calling。

### 3.1 常见同构问题

很多成熟系统都在处理“静态描述 + 动态绑定 + 单次调用”三层问题：

- **数据库**：prepared statement / connection / command
- **Web 框架**：route table / request scope / handler invocation
- **RPC / gRPC**：service descriptor / channel / call
- **编译器/VM**：compiled artifact / execution frame / instruction dispatch

这些系统的经验通常不是“把三层硬揉成一层”，而是：

1. **内部继续分层**
2. **对外只暴露一个更顺手的绑定态主概念**

这是一条非常成熟的消解套路。

### 3.2 常见套路一：Compiled Descriptor + Bound Session + Per-call Context

这是最常见、也最稳的分层：

```text
compiled/static descriptor
    ↓ bind runtime scope
bound session / scope
    ↓ invoke
per-call context
```

翻译到当前语境里，大致对应：

- `ToolRegistry`：compiled/static descriptor
- `ToolSessionState`：bound runtime scope
- `ToolExecutionContext`：per-call context

从“架构纯度”看，当前设计已经很接近这个成熟模型。

### 3.3 常见套路二：Policy 不直接进高频路径，而是先编译成 Snapshot

很多系统对外允许“规则式描述”，但进入高频路径时不会每次解释规则，而会先编译成快照：

- SQL planner 编译成执行计划
- route constraints 编译成 matcher
- authorization rules 编译成 decision cache / bitset

对 tool 来说，成熟做法通常是：

- authoring 阶段可以叫 `Policy` / `Rule`
- runtime 阶段更像 `Snapshot` / `AccessSet` / `Mask`

---

## 4. 对当前代码现状的判断

目前主线设计的优点：

- `ToolRegistry` 把静态 schema / capability 明确收口
- `ToolExecutionContext` 已经把 runtime-only 信息隔离得比较清楚
- `MethodToolWrapper` / `ArtifactToolWrapper<T>` 没把 session state 混回 `ITool`
- `Agent.Core` 也已经开始把 app 投影结果一次性编译成 `ToolAccessPolicy`

目前主要问题不在“方向错了”，而在**公开 API 没有把内部复杂性充分包起来**：

- 使用方会直接接触 `ToolSessionState + ToolExecutor + ToolAccessPolicy`
- `ToolAccessPolicy` 的命名比它当前实际职责更大
- 高频路径未来若继续优化，`HashSet<string>` 级别的访问控制可能不够“最终形态”

因此，更像是**需要继续做 API 收口**，而不是推翻“能力/状态分离”本身。

---

## 5. 候选方案

下面列出几条现实可行的路线。

### 方案 A：保留现有分层，但公开主概念改为 `ToolSession`

核心思路：

- 内部仍保留 `ToolRegistry`
- 内部仍保留 `ToolSessionState`
- 内部仍保留 `ToolExecutionContext`
- 对外不再要求使用方手动拼 `ToolExecutor`

示意 API：

```csharp
var session = registry.Bind(
    toolAccess: ...,
    services: ...,
    items: ...
);

var visible = session.VisibleToolDefinitions;
var result = await session.ExecuteAsync(rawToolCall, cancellationToken);
```

也可以更进一步：

```csharp
var session = registry.CreateSession(
    hiddenToolNames: ["ctx_compress"],
    services: services,
    items: items
);
```

#### 优点

- 使用方只理解一个绑定态主概念
- 内部分层与性能优化空间几乎不受损
- 未来做缓存、mask、`ToolId` 等优化时对外 API 变化最小

#### 缺点

- 需要决定 `ToolExecutor` 是删除、内化，还是仅保留为内部/高级 API
- 需要明确 `ToolSessionState` 还是否保留为公开类型

#### 适配度

**很高。**
这是当前代码最自然的演化方向。

---

### 方案 B：保留 `ToolAccessPolicy`，但把它重新定位为“运行时访问快照”

核心思路：

- 不急着支持复杂规则系统
- 承认当前主线真正需要的是“当前 session 的访问快照”
- 让名字和职责更对齐

可以考虑的命名：

- `ToolAccessSnapshot`
- `ToolAvailability`
- `ToolAccessSet`

若未来真要支持复杂规则，再单独引入 compiler：

```csharp
var snapshot = ToolAccessCompiler.Compile(registry, rule);
```

#### 优点

- 减少“Policy 会不会越来越大”的概念焦虑
- 让高频路径更容易朝 compiled snapshot 演进
- 和方案 A 可以自然叠加

#### 缺点

- 如果未来确实很快就要支持复杂策略，改名可能显得短期来回摆动

#### 适配度

**高。**
尤其适合当前草稿阶段。

---

### 方案 C：把访问控制进一步编译成 `ToolId + mask`

核心思路：

- registry 构建时为每个工具分配稳定 `ToolId`
- session 绑定时把访问控制编译成 `bool[]` / bitset / mask
- 高频路径主要按 id 和 mask 判定，而非字符串 `HashSet`

示意：

```text
ToolRegistry:
  ToolId 0 -> ctx_compress
  ToolId 1 -> fs.read
  ToolId 2 -> grep.search

ToolSession:
  visibleMask = [false, true, true]
```

#### 优点

- 面向大量 agent / 高频执行时更像“最终性能形态”
- 非常适合做 `VisibleToolDefinitions` 缓存
- 更方便未来附加 cost / priority / class-of-service 等元信息

#### 缺点

- 复杂度明显高于当前真实需求
- 如果公开 API 太早暴露 `ToolId`，会把内部优化泄漏给上层

#### 适配度

**中高。**
适合作为内部演进方向，但不必现在就公开到 API 表面。

---

### 方案 D：重新把状态塞回 `ITool` 实例

核心思路：

- 每个 session 拿一组独立工具实例
- 可见性、services、items 等都绑定在实例上
- 执行时只依赖实例内部状态

#### 优点

- 表面 API 看起来最简单

#### 缺点

- 容易重新引入共享实例污染问题
- 工具定义与 session 状态重新纠缠
- 反射/包装/注册缓存收益下降
- 与当前 `MethodToolWrapper` / `ArtifactToolWrapper<T>` 的无状态方向相冲突

#### 适配度

**低。**
不建议回头走这条路。

---

### 方案 E：继续维持现状，不改公开主概念

#### 优点

- 短期零迁移成本

#### 缺点

- API 使用者继续承担内部复杂性
- 文档解释成本会长期偏高
- 后续继续讨论 “`ToolSessionState` 和 `ToolExecutor` 究竟谁是主概念” 时会反复绕圈

#### 适配度

**中低。**
可以暂时停留，但不像草稿期应有的收口姿态。

---

## 6. 一个更具体的推荐方向

如果只追求“当前阶段最稳、最不容易走偏”的路线，我倾向于：

### 6.1 对外收口为 `ToolSession`

对使用方：

- `ToolRegistry`：静态、共享、不可变
- `ToolSession`：当前 session 的唯一公开绑定态主概念

内部保留：

- `ToolSessionState`
- `ToolExecutionContext`
- 也许还有内部版 `ToolExecutor`

### 6.2 把 `ToolAccessPolicy` 改造为“快照语义优先”

有两种可接受路径：

1. **保留类型，改名**
2. **保留名字，但在文档里明确它是 runtime snapshot，不是规则解释器**

如果没有明显外部兼容负担，倾向于前者。

### 6.3 访问控制先做 API 收口，再做性能编译

推荐顺序：

1. 先让使用方 API 更自然
2. 再把内部实现从字符串集合逐步演进到 `ToolId + mask`

这样不会把“内部优化结构”过早泄漏到表面 API。

---

## 7. 一个可能的 API 草案

这里只给方向，不代表必须按这个名字落地。

```csharp
public sealed class ToolRegistry {
    public ToolSession CreateSession(
        ToolAccessSnapshot? access = null,
        IServiceProvider? services = null,
        IReadOnlyDictionary<string, object?>? items = null
    );
}

public sealed class ToolSession {
    public ImmutableArray<ToolDefinition> VisibleToolDefinitions { get; }

    public ValueTask<ToolCallExecutionResult> ExecuteAsync(
        RawToolCall request,
        CancellationToken cancellationToken = default
    );
}
```

如果希望给使用方保留“高级逃生口”，也可以继续保留较底层类型，但不把它们放在 README 的主路径里。

---

## 8. 讨论时值得重点征询的几个问题

这份文档最希望外部意见帮助回答下面这些问题：

1. `ToolSessionState + ToolExecutor` 是否应该继续同时作为公开一等概念存在？
2. `ToolAccessPolicy` 这个名字，是否已经在误导大家高估其设计目标？
3. 若未来明确要做大量 agent 并发，是否应尽早在内部引入 `ToolId + mask`，还是先保持简单实现？
4. `ToolSession` 作为公开主概念，是否已经足以覆盖当前 `Agent.Core` 与未来宿主的主要装配需求？
5. 是否存在我们尚未考虑到的更成熟类比对象，例如 actor runtime、capability security、ECS、prepared execution 等范式中的更贴切套路？

---

## 9. 当前结论

当前阶段最重要的结论不是“必须立刻做某个性能优化”，而是：

- **复杂性来源是真实的，不是偶然写复杂了。**
- **这类复杂性在业内很常见，成熟做法通常是‘内部继续分层，对外暴露绑定态主概念’。**
- **对当前代码最合适的方向，像是继续保留‘能力/状态分离’，但把使用方面向的主概念收口成 `ToolSession`，并把访问控制显式定位为编译后的 session 快照。**

这条路既不否定当前设计，也不要求现在就把所有性能优化一次做满。
它更像是一次 API 边界澄清：把不可避免的内部复杂性留在框架里，而不是让每个使用方反复重新理解。

---

## 10. 后续

本文是第一轮讨论稿（摆开复杂性来源与候选方案 A–E）。第二轮收敛见 [tool-architecture-redesign.md](tool-architecture-redesign.md)：它把痛点从「概念数量」重新定位为「join 节点无归属 + 两种生命周期被合并」，用行业三条共识给出方向支撑，并把推荐方向坐实成一份已验证可编译的骨架（方案 F），配套可运行实验位于 `gitignore/tool-session-redesign/`。

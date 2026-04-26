# PersistentAgentProto

> **目的**：用 `prototypes/Completion(.Abstractions)` + `src/StateJournal` 拼一个最简持久化 multi-turn 对话原型，**不引入 `prototypes/Agent.Core`**。
> **结论先行**：跑通了，但实操中能清楚感受到 StateJournal 当前 API 的几个不便点。InlineString 的公开**应该做，但不应该是第一刀**——先补 1~3 个零成本可用性 API，再决定是否暴露 InlineString。详见 §4。

## 1. 用法

```bash
# 烟测
dotnet run --project prototypes/PersistentAgentProto -- smoke
dotnet run --project prototypes/PersistentAgentProto -- smoke-v2

# 体积压测：1000 轮对话，每条 200B 高熵内容
for mode in stress stress-json stress-inline stress-v2; do
  dotnet run --project prototypes/PersistentAgentProto -c Release -- $mode 1000 200
done

# SymbolTable happy-path：所有 turn 反复写入相同内容
dotnet run --project prototypes/PersistentAgentProto -c Release -- stress-dup 1000 200

# 真实交互（需要本地 LLM endpoint，默认 http://localhost:8000/）
dotnet run --project prototypes/PersistentAgentProto -- ./.atelia-state
dotnet run --project prototypes/PersistentAgentProto -- v2 ./.atelia-state-v2
```

## 2. Schema

```text
root: DurableDict<string>
  ├─ schema: int
  ├─ systemPrompt: string
  ├─ createdAt: long
  └─ messages: DurableDeque<DurableDict<string>>
       (each msg)  role: string ("user" | "assistant")
                   content: string
                   ts: long
```

`PersistentSession.BuildContext()` 把上述 durable graph 投影成 `IReadOnlyList<IHistoryMessage>` 喂给 `ICompletionClient`。`assistant` 一侧用一个本地的 `TextOnlyAction` record 当 `IRichActionMessage`（无 tool_call、无 thinking）。

## 3. 实测体积（1000 轮，每轮 1 user + 1 assistant，每条 200B 随机字符）

> 说明：本节现分为 **fix 前** 与 **fix 后** 两组数据。
> 2026-04-26 起，`StateJournal` 已开始修复 `SymbolTable` rebase/deltify cost 估算把“条目数”误当“字节成本”的问题。
> 因此旧数据仍有历史价值，但不再代表当前主线行为。

### 3.1 fix 前（历史基线）

| 路线 | 总 RBF 字节 | bytes/turn | 放大率 (vs raw 400B/turn) |
|---|---:|---:|---:|
| mixed `DurableDict<string>` per message | 2,262,600 | ~2263 | **5.66×** |
| typed `DurableDeque<string>` + JSON 整体 | 2,181,948 | ~2182 | **5.46×** |
| **typed `DurableDict<string, InlineString>` per message** | **782,512** | **~782** | **1.96×** |
| **`PersistentSessionV2`：structured message + nested InlineString text** | **868,920** | **~869** | **2.17×** |

### 3.2 fix 后（A' cost model 修复后）

| 路线 | 总 RBF 字节 | bytes/turn | 放大率 (vs raw 400B/turn) |
|---|---:|---:|---:|
| mixed `DurableDict<string>` per message | **826,408** | **~826** | **2.07×** |
| typed `DurableDeque<string>` + JSON 整体 | **806,100** | **~806** | **2.02×** |
| **typed `DurableDict<string, InlineString>` per message** | **744,400** | **~744** | **1.86×** |
| **`PersistentSessionV2`：structured message + nested InlineString text** | **825,380** | **~825** | **2.06×** |

fix 后可以看到，**随机高熵文本走 SymbolTable 的异常膨胀已经基本消失**：`stress` / `stress-json` / `stress-v2` 都回到了 `~0.8 MB` 区间。说明之前 `2.2 MB` 级别的主要来源，确实不是“intern 天生就比 InlineString 臃肿”，而是 `SymbolTable` 路径的 rebase cost heuristic 存在系统性低估。

额外旁证：`stress-dup 1000 200`（所有 turn 重复写入相同内容）在 fix 后约为 **319,892 bytes / ~320 bytes-per-turn**。这说明当 SymbolTable 命中复用路径时，symbol-backed `string` 仍然非常有竞争力。

这也改变了原来的结论力度：`InlineString` 仍然有价值，但它不再是“救火式体积优化”的唯一来源。更准确的定位是：

- `InlineString`：适合表达**长、高熵、低复用** payload
- `string` / SymbolTable：在 cost model 正常时，对有复用机会的文本并不天然吃亏

`PersistentSessionV2` 的意义也随之变化：它仍然是一个很强的正式方案候选，但优势更多来自 **schema 清晰度与演进性**，而不只是“避开 SymbolTable”。

估计 100k 轮场景：

- mixed dict（fix 前历史基线）: ~226 MB
- mixed dict（fix 后当前主线）: ~83 MB
- InlineString（fix 后当前主线）: ~74 MB

100k 轮仍然是 long-running Agent 的现实量级；但在 fix 后，方案选择的关键已经从“谁能避开异常膨胀”转向“谁的 schema 更适合长期演进”。

## 4. 实操中遇到的 API 痛点（已落地 / 待办）

按"修起来收益最高"排序：

### 4.1 ~~`Repository` 缺"OpenOrCreate" / branch 探测~~ ✅ **已完成**

现在 `Repository` 提供：
- `Repository.OpenOrCreate(dir)` — 不存在/空目录 → Create，合法 repo → Open，其他 → 明确失败，**不会误覆盖**
- `Repository.HasBranch(name)` — 纯内存查询，非法名返 false（便于"探测后决定"）
- `Repository.GetOrCreateBranch(name)` — 存在 → Checkout，不存在 → Create

现在 reopen 套路只需：
```csharp
var repo = Repository.OpenOrCreate(dir).Unwrap();
var rev = repo.GetOrCreateBranch("main").Unwrap();
```

### 4.2 ~~`AteliaResult<T>.Value` 的 NRT 体验~~ ✅ **已完成**

`AteliaResult<T>` / `AsyncAteliaResult<T>` / `DisposableAteliaResult<T>` 现在统一提供：
- `IsSuccess` / `IsFailure` 带 `[MemberNotNullWhen]` — `if (r.IsSuccess) use(r.Value);` 零 NRT 警告
- `r.Unwrap()` — 失败抛 `InvalidOperationException`（带 error code/message）
- `r.TryUnwrap(out value, out error)` — 状态机风格用法
- `r.ValueOr(fallback)` — 失败返回默认值

本原型里原本的本地 `Unwrap<T>(AteliaResult<T>)` helper 已全部删除，调用点换成 `result.Unwrap()`。

### 4.3 ~~`rev.GraphRoot` 没有 typed checkout~~ ✅ **已完成**

`Revision.GetGraphRoot<T>() where T : DurableObject` 返回 `AteliaResult<T>`：unborn / 类型不匹配 → 明确错误。

```csharp
var root = rev.GetGraphRoot<DurableDict<string>>().Unwrap();
// 不再是 (DurableDict<string>)rev.GraphRoot!
```

### 4.4 ~~公开 `InlineString`~~ ✅ **已完成**

现在在 `Atelia.StateJournal.InlineString`（从 `Internal/` 提升为 public）。实测表明，重点字段从 `string` 换成 `InlineString`，体积节省 ~65%（见 §3 表格与 `InlineSession.cs`）。

### 4.5 留下的 ⏳ 待办

**A. `IRichActionMessage` 的 durable schema**。当前原型只处理纯文本，要持久化 tool_call/thinking 的完整 `AggregatedAction` 还需要：
- `DurableActionMessage` / `DurableObservationMessage` / `DurableToolResultsMessage` 三个 record，位于某个新模块（或 `Atelia.Agent.PersistentHistory`）。
- 互转函数：`IRichActionMessage → DurableActionMessage`（写入时）与反向（重现上下文时）。
- 这个项依赖对 `ActionBlock.Thinking.OpaquePayload` 的持久化布局（`byte[]` 直接进 DurableDict？还是包装进 InlineString-of-base64？）进一步实验后再决定。

**B. `DurableObject` 可能需要 `IsDirty` / `Snapshot()` 的中间状态 API**。如果 Agent step 中途出错，希望可以"在下一轮重试前回滚本轮未 commit 的脓脏"，现在没有这个能力（会脱靠 head）。优先级比上面 A 低。

## 6. 文件

- `PersistentSession.cs`：mixed dict per message 路线（读/写均走 SymbolTable intern）
- `JsonStringSession.cs`：typed deque + 整条 JSON 路线（体积对照）
- `InlineSession.cs`：typed `DurableDict<string, InlineString>` 路线（InlineString 绕过 SymbolTable，体积赢家）
- `PersistentSessionV2.cs`：结构化 message + 内层 `InlineString` 文本字段（面向正式方案的递进对照）
- `Program.cs`：`smoke` / `smoke-v2` / `stress` / `stress-json` / `stress-inline` / `stress-v2` / interactive 模式

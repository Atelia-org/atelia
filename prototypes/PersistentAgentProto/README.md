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

| 路线 | 总 RBF 字节 | bytes/turn | 放大率 (vs raw 400B/turn) |
|---|---:|---:|---:|
| mixed `DurableDict<string>` per message | 2,262,600 | ~2263 | **5.66×** |
| typed `DurableDeque<string>` + JSON 整体 | 2,181,948 | ~2182 | **5.46×** |
| **typed `DurableDict<string, InlineString>` per message** | **782,512** | **~782** | **1.96×** |
| **`PersistentSessionV2`：structured message + nested InlineString text** | **868,920** | **~869** | **2.17×** |

**前两者几乎等价**——只要走 `string` 字段，无论 typed 还是 mixed 都会被 `SymbolTable` intern。

**InlineString 路线体积减半以上**（1000 轮 2.3 MB → 0.78 MB，节省 65%）。原因是 content/role/ts 全走 InlineString，**完全绕过了 SymbolTable**——文件里只剩下“裸 UTF-8/UTF-16 payload + dict frame increment”。

`PersistentSessionV2` 的目标则是验证另一件事：**不放弃结构化 message schema，也能把最贵的高熵文本迁出 SymbolTable**。实测它只比极限压缩取向的 `InlineSession` 多出约 11% 体积，但比原始 `PersistentSession` 小了约 62%，这个折中已经很接近正式方案候选。

估计 100k 轮场景：

- mixed dict: ~226 MB
- InlineString: ~78 MB

100k 轮是 long-running Agent 的现实量级（一天上千次交互 × 几个月），InlineString 的差距足以决定是否需要重同。

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

# PersistentAgentProto

> **目的**：用 `prototypes/Completion(.Abstractions)` + `src/StateJournal` 拼一个最简持久化 multi-turn 对话原型，作为后续 ToolCalls / Thinking / Tool-Result 持久化等富 schema 实验的基础。
> **当前状态**：单一正式方案 `PersistentSession`（原 `PersistentSessionV2`），多轮历史可断点续跑，每条消息落盘后立即 freeze。

## 1. 用法

```bash
# 烟测（不依赖外部 LLM）
dotnet run --project prototypes/PersistentAgentProto -- smoke

# 体积压测：1000 轮对话，每条 200B 高熵内容
dotnet run --project prototypes/PersistentAgentProto -c Release -- stress 1000 200

# 真实交互（需要本地 LLM endpoint，默认 http://localhost:8000/）
dotnet run --project prototypes/PersistentAgentProto -- ./.atelia-state
```

## 2. Schema

```text
root: DurableDict<string>
  ├─ schema: int (= 1)
  ├─ systemPrompt: string
  ├─ createdAt: long (unix ms)
  └─ messages: DurableDeque<DurableDict<string>>            ← 仍然 mutable（继续 PushBack）
       (each msg, frozen after append)
         ├─ text: DurableDict<string, string>                ← frozen
         │    ├─ role: string (typed value string)
         │    └─ content: string
         └─ ts: long (unix ms)
```

设计要点：

- **外层 message 走 mixed `DurableDict<string>`**：未来加任意 typed 元数据字段（`cost: double` / `tokenUsage: int` / `toolCallId: string` / `latencyMs: long` …）零破坏。
- **高熵文本集中在 `text` 子字典**：明确"长 + 高熵 + 不可复用"的语义；同时绕过 SymbolTable intern 的固有开销。
- **每条消息写入后 `Freeze()`**：把"消息一旦落盘即不可篡改"这条业务承诺写进对象状态。frozen 路径在下次 commit 时会触发一次 `force-rebase` 完整快照，体积代价 ~10%，换语义清晰度。
- **messages 这一条 deque 不 freeze**：要持续 PushBack。整链归档语义留待将来另开 branch + full-rebase。

`PersistentSession.BuildContext()` 把上述 durable graph 投影成 `IReadOnlyList<IHistoryMessage>` 喂给 `ICompletionClient`。`assistant` 一侧用一个本地的 `TextOnlyAction` record 当 `IActionMessage`（无 tool_call、无 thinking）——这是下一刀的扩展点。

## 3. 实测体积

1000 轮 × 每轮 (1 user + 1 assistant) × 每条 200B 高熵随机字符 = 名义 raw 400 bytes/turn。

| 阶段 | bytes/turn | 备注 |
|---|---:|---|
| **当前主线**（freeze + cost-model fix） | **~907** | content 高熵随机；含每条消息 freeze 的 force-rebase 开销 |
| 同 schema 但不 freeze | ~831 | 历史对照：去掉 freeze 后基线 |
| 历史 SymbolTable happy path（content 100% 复用） | ~320 | 仅适用于旧 symbol-backed / mixed string 对照；当前 typed `string` schema 不会因 content 复用自动进入 SymbolTable |
| **历史 buggy 基线**（cost-model fix 之前的同 schema） | ~869 | 当时是体积赢家是因为 mixed dict 受 bug 影响膨胀 5.6× |

百万轮估算：当前 typed `string` schema 约 ~907 MB；~320 MB 只适用于显式选择 symbol-backed 且内容高度复用的模型。

cost-model fix 经过：参见 `gitignore/persistent-proto/{claude,gpt5}-sendbox/` 中的 Claude × GPT5 联合定位 + 修复 `ITypeHelper<T>.EstimateBareSize` / `VersionChainStatus` 字节单位错配的 3 轮通信记录。

## 4. 下一刀：富 schema 持久化

要把当前的"纯文本 message"扩展到完整 `IActionMessage`（OpenAI/Anthropic converter 的全集），需要把以下结构持久化进 message dict：

```text
toolCalls: DurableDeque<DurableDict<string>>?
   each: { id: string, name: string, args: string }
blocks: DurableDeque<DurableDict<string>>?
   each: { kind: string, payload: string | bytes? }
   kind ∈ { "text", "thinking-redacted", "thinking-summary", … }
opaqueThinking: bytes?                ← Anthropic redacted thinking 原文
```

**待决策**：
- `ActionBlock.Thinking.OpaquePayload` 是 `byte[]` —— 直接进 mixed value box（如果支持 byte[]）？还是包成 base64 string？前者结构更准；后者付 ~33% base64 膨胀。
- `Blocks` 顺序：deque 自然保序；`IActionMessage.Blocks` 本来就是 `IReadOnlyList<ActionBlock>`，deque 是自然映射。
- `ToolCalls` 是不是直接复用同一个 ordered structure？可以，但 tool_call_id 是稳定 join key，建议独立 deque + per-id 索引。

这些将在 `RichPersistentSession`（同名文件 / 渐进替换）中验证。

## 5. 文件

- `PersistentSession.cs`：当前唯一正式方案。
- `Program.cs`：`smoke` / `stress` / interactive。
- `README.md`：本文档。

历史对比方案（`InlineSession` / `JsonStringSession` / 旧版 mixed `PersistentSession` / `IPersistentChatSession` 接口）已删除——它们的数据点保留在 §3 表格与 git 历史里。

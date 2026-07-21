# Task 07: 单次完整重写版 Autobiographical Maintainer

> 状态：Todo
> 建议执行者：熟悉 `prototypes/ChatSession.Memory` maintainer 体系与 `ChatSession.BacktestCli` preset 装配的会话
> 依赖：无硬依赖；与既有 `autobiographical-two-stage`（编辑 Agent 版）并存，不替换

## 问题背景

现有 `autobiographical-two-stage` preset 采用「两阶段文本编辑 Agent」实现自传维护：

- Recording 阶段：`AutobiographicalRecordingMemoryMaintainer` 跑一个 tool loop，用 `memory_document_*` 工具把新经历增量写入自传。
- Compression 阶段：`AutobiographicalCompressionMemoryMaintainer` 再跑一个 tool loop，用 block-ID 作为锚点逐块 condense 到 target token 预算内。

设计初衷是：以 TextBlock ID 为编辑锚点，让 maintainer「只改动少量块」，避免把未改动文本作为 output token 重复吐出，从而省力省钱。

**实测结果与预期严重相悖**（回放命令见下）：即便最终产物只有 2~3K tokens，一次维护也要 ~30 次工具调用、运行数分钟、单次约 \$7。

### 回放命令（复现）

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --preset autobiographical-two-stage \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 32000 --compression-high-watermark 2000 --compression-target-tokens 1500 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json --connection opus4-6 \
  --output gitignore/backtest/autobiographical-two-stage/9/result-opus4-6.jsonl \
  --call-log-dir gitignore/backtest/autobiographical-two-stage/9/calls-opus4-6 --max-epochs 9
```

调用日志样例：`gitignore/backtest/autobiographical-two-stage/9/calls-opus4-6/0033.json`。

### 分析结论（为何编辑 Agent 在短产物下打不过完整重写）

1. **工程层问题（可单独修复，见 Task：cache_control）**：Anthropic 客户端此前未发送 `cache_control` 断点，多轮 tool loop 每轮以全价重发稳定前缀（system + 文档视图 + tools），浪费约 85~90% 的 input 成本。
2. **架构层的本质问题（本 Task 要解决的）**：编辑锚点方案的**唯一**优势是「不必把未改动文本作为 output 重吐」。该优势只有在**文档很大**（经验值 >50~100K tokens）、**改动占比很小**、**编辑批量化**、且**有前缀缓存**时才成立。
   - 对 1500~3000 tokens 的自传目标：把全文当 output 重吐一遍只需 ~1500 output tokens（≈ \$0.11），比编辑 Agent 多跑一轮 reasoning 还便宜。
   - 完整重写：1 次调用，input ≈ system + 全文（~4.5K），output ≈ 目标全文（~1.5K），总成本 ≈ \$0.18，一次往返、数秒完成。
   - 编辑 Agent（现状）：~30 次往返、每轮重复前缀 + 每轮 reasoning，合计 ≈ \$7。差距约 **40×**。
   - 即便修好 prompt caching，编辑 Agent 仍需付 N 段不可缓存的 reasoning 输出 + N 次往返延迟，短产物下依旧数倍于完整重写。

**决策**：对 <16K tokens 的自传类短产物，正确架构是**单次完整重写**（recording + compression 合并为一次 completion）。编辑 Agent 版保留给将来「很大且只做局部增量」的记忆块场景。

## 当前实现上下文

关键文件：

| 文件 | 作用 |
|---|---|
| `prototypes/ChatSession.Memory/AutobiographicalMemoryMaintainer.cs` | 现有两阶段编排（recording → 按 watermark 触发 compression） |
| `prototypes/ChatSession.Memory/AutobiographicalRecordingMemoryMaintainer.cs` | Recording 阶段（编辑 Agent） |
| `prototypes/ChatSession.Memory/AutobiographicalCompressionMemoryMaintainer.cs` | Compression 阶段（编辑 Agent） |
| `prototypes/ChatSession.Memory/AutobiographicalRecordingPrompts.cs` | Recording 的 system / user prompt |
| `prototypes/ChatSession.Memory/AutobiographicalCompressionPrompts.cs` | Compression 的 system / user prompt（含 `FormatUserPrompt(before, target)`） |
| `prototypes/ChatSession.Memory/MemoryDocumentAgentLoop.cs` | 编辑 Agent 的 tool loop（每轮重发 working context 是主要成本来源） |
| `prototypes/ChatSession.Memory/MemoryDocumentTools.cs` | `memory_document_*` 工具定义 |
| `prototypes/ChatSession.Memory/MemoryDocumentCompressionPolicy.cs` | `ShouldCompress` / high watermark / target token 策略 |
| `prototypes/ChatSession.Memory/MemoryDocumentTokenEstimator.cs` | token 估算（重写版仍可复用于判断是否需要压缩） |
| `prototypes/ChatSession.Memory/RolePlayMemoryMaintainers.cs` | 既有 `FirstPersonAutobiographyMemoryMaintainer`（见下，已是单次文本输出形态） |
| `prototypes/ChatSession/MemorySubstrate.cs` | `CompletionMemoryBlockMaintainer`：单次/多轮文本输出 maintainer 的通用实现 |
| `prototypes/ChatSession.Memory/RolePlayMemoryBlockPaths.cs` | `FirstPersonAutobiography` target 路径 |
| `prototypes/ChatSession.BacktestCli/Program.cs` | `CreateReplayMaintainerProfile`：preset → maintainer 装配（新增 preset 的接入点） |
| `prototypes/ChatSession.BacktestCli/README.md` | preset 列表文档，新增 preset 后需同步 |

**重要既有资产**：`CompletionMemoryBlockMaintainer`（`MemorySubstrate.cs`）已经是「把模型这一轮的 flattened text 作为新块」的单次输出实现——当传入空 tool session 时，它本质上就是一次性完整重写。现有 `first-person-autobiography` preset 已经用它，但：

- 用的是 `RolePlayMemoryMaintainerPrompts.SharedSystemPrompt` / `FirstPersonAutobiographyUserPrompt`，只做「维护自传」，**没有把 recording（融入新经历）+ compression（控制在预算内）合并表达**；
- 不感知 `--compression-target-tokens` / `--compression-high-watermark`，无法在提示里给出目标长度约束。

因此本 Task 的主要工作量在**提示词设计**与**preset 装配**，而非重写底层 loop。

## 目标

新增一个单次完整重写版自传 maintainer，与两阶段编辑 Agent 版并存：

1. **合并职责的提示词**：设计一份 system + user prompt，使模型在**一次 completion**内同时完成：
   - 把 `RecentHistory`（本次待记录的新经历）融入现有自传；
   - 在保留承重记忆（人物的原话、首次时刻、未决张力、时间骨架、当下内心状态）的前提下，将全文控制在 target token 预算附近；
   - 直接输出**完整的新自传全文**（不使用编辑工具，不输出 diff）。
   - 复用 `AutobiographicalRecordingPrompts` / `AutobiographicalCompressionPrompts` 中已验证的「压缩优先级 / 抗压内容 / 声音」表述，避免重造。

2. **maintainer 实现**：优先直接复用 `CompletionMemoryBlockMaintainer`（空工具 = 单次重写），或封装一个薄类型 `AutobiographicalRewriteMemoryMaintainer`：
   - Target = `RolePlayMemoryBlockPaths.FirstPersonAutobiography`；
   - 可选：仅当 `MemoryDocumentTokenEstimator.Estimate(current) + estimate(newExperience)` 超过 high watermark 时才触发「重写压缩」，否则走更廉价的「仅追加」路径（沿用现有 `MemoryDocumentCompressionPolicy` 语义）；首版也可无条件重写，先验证成本/质量。

3. **CLI preset 接入**：在 `CreateReplayMaintainerProfile` 增加新 preset（建议名 `autobiographical-rewrite`），支持 `--compression-target-tokens`（作为提示里的目标长度）与 prompt override；同步更新 `ChatSession.BacktestCli/README.md` 的 preset 表与用法行。

4. **验收**：用与上文相同的回放输入对比 `autobiographical-two-stage` 与 `autobiographical-rewrite`：
   - 成本应从 ~\$7 降到 ~\$0.2 量级；
   - 墙钟从数分钟降到数秒；
   - 产物质量（承重记忆是否保留、声音是否一致、是否落在 target token 附近）人工/抽样评估不劣于编辑 Agent 版。
   - 记录一份简短对比结果（成本 / 轮数 / token / 质量）供后续决定默认 preset。

## 非目标

- 不删除 / 不重构现有 `autobiographical-two-stage` 与编辑 Agent 基础设施；它们保留给未来大文档局部增量场景。
- 不改动 `MemoryDocumentAgentLoop` / `MemoryDocumentTools` 的行为。
- Prompt caching 的客户端支持属于独立改动（已单独实施），本 Task 不重复处理。

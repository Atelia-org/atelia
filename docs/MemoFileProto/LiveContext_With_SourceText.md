# Memo: SourceText 驱动的 Live Context 构建方案

*版本：0.1  ·  更新日期：2025-10-09*

## 背景与目标

我们希望让 LLM 在文本编辑场景中始终看到“接近实时”的工作区状态，同时避免把大量细枝末节囤积在聊天历史里。目标是：

- **保持 Chat History 精简**：历史消息只保留语义重要的对话，不堆叠大块文本快照。
- **引入 Live Context**：在每次调用前，由代理从本地编辑器状态动态构建一份结构化上下文，描述最新全文、最近几次增量变更等。
- **对齐人类认知**：Live Context 里的信息布局要模拟开发者的短期记忆（最新全量 + 最近几次小改动 + 一个旧的参考点）。
- **选择易集成的文本模型**：基于 Roslyn `SourceText` 快照体系管理文本、行视图和增量历史。

## Live Context 概念模型

```
Chat History (精简) + Live Context (实时注入)
```

- **Chat History**：只保留必要的系统指令、LLM 产出的操作计划、用户的高层意图。
- **Live Context**：由代理在每次请求前临时生成，包含：
  - `current_document`：基于最新 `SourceText` 快照的全文。
  - `recent_diffs`：按时间倒序列出最近 N 次 `TextChange` 的摘要或 Patch。
  - `anchor_diff`：选取一个较旧版本和当前版本的 diff（帮助模型建立全局记忆）。
  - `editor_state`：光标、选区、活动文件等元信息。
  - `plan_hints`：可选，记录模型上一次提交的操作计划及执行结果（成功/失败）。

Live Context 不写入历史，下一次请求会重新生成，因此不会造成 token 膨胀。

## 关键技术选型：Roslyn `SourceText`

`Microsoft.CodeAnalysis.Text.SourceText` 提供：

- **行访问**：`sourceText.Lines` 可枚举 `TextLine`，支持行号定位。
- **增量变更**：`SourceText.WithChanges(TextChange...)` 创建新快照，同时保留旧引用。
- **差异获取**：`SourceText.GetChangeRanges(otherText)` 用于生成 diff。
- **不可变快照**：天然保存历史版本，无需额外拷贝，便于回溯。

### 数据结构约定

```csharp
record LiveContextPayload(
    string CurrentDocument,
    IReadOnlyList<DiffSummary> RecentDiffs,
    DiffSummary? AnchorDiff,
    EditorStateMetadata EditorState,
    IReadOnlyList<PlanHint> PlanHints
);

record DiffSummary(
    string VersionId,
    DateTimeOffset Timestamp,
    string PatchSnippet,
    int? AffectedLineStart,
    int? AffectedLineEnd
);
```

- `VersionId`：可使用 `Guid.NewGuid()` 或基于编辑序列号。
- `PatchSnippet`：推荐统一使用统一 Format（例如 unified diff，或自定义 JSON 指令）。
- `EditorStateMetadata`：包含光标、选区、活动文件路径等。

### 快照生命周期

1. 初始化 `SourceText`（从磁盘读或作为空文档）。
2. 每次工具执行完模型的编辑操作后：
   - 应用 `TextChange` 并获取新的 `SourceText`。
   - 将旧快照及变更摘要存入环形缓冲区（用于 recent diffs）。
3. 构建下一次 Live Context 时：
   - `current_document` = 最新快照全文。
   - `recent_diffs` = 从环形缓冲区取最近 N 条（例如 3）。
   - `anchor_diff` = 选定的旧版本（如一小时前的快照）与当前的差异。

## Live Context 构建流程

```
┌────────────┐      ┌──────────────────┐      ┌─────────────────────┐
│ Chat Store │─────▶│ 代理合成器/Builder │─────▶│ 发送至 LLM (聊天请求) │
└────────────┘      └──────────────────┘      └─────────────────────┘
        ▲                    │                             │
        │                    ▼                             │
        │           ┌────────────────┐                     │
        └───────────│ SourceText 管理器 │◀────── 工具执行结果 ─────┘
                    └────────────────┘
```

### 合成步骤

1. **收集语义历史**：读取最近几条必要的 chat 消息（系统提示、用户目标、模型计划）。
2. **提取编辑器状态**：向 `SourceText` 管理器查询：
   - 最新 `SourceText` & 字符串内容。
   - 最近 N 条 `TextChange` 摘要。
   - 定期采样的旧快照（用于 anchor diff）。
   - 当前光标与选区信息（如果存在 UI 事件源）。
3. **构造 Payload**：序列化为 JSON 或 Markdown，嵌入在发送给 LLM 的最后一条 `user` 消息中。
4. **发送请求**：LLM 读到 Live Context 后，生成下一步编辑指令。
5. **执行与记录**：
   - 代理解析输出，应用到 `SourceText`。
   - 记录结果（成功/失败 + 新版本 ID）。
   - 更新历史缓冲区。

## 示例消息结构

```json
{
  "role": "user",
  "content": [
    { "type": "text", "text": "# Live Context\n" },
    { "type": "text", "text": "## Current Document\n" },
    { "type": "text", "text": "```\n" + currentDocument + "\n```" },
    { "type": "text", "text": "## Recent Diffs (new→old)\n" + diffSummaryMd },
    { "type": "text", "text": "## Anchor Diff\n" + anchorDiffMd },
    { "type": "text", "text": "## Editor State\n" + editorStateMd }
  ]
}
```

- `currentDocument` 以 fenced code block 形式提供。
- `diffSummaryMd`：每条 diff 使用统一格式，例如：
  ```
  - Version: v20251009-123456
    Time: 2025-10-09T12:34:56Z
    Patch:
    ```diff
    @@ -20,3 +20,4 @@
    ...
    ```
  ```

## 历史与回溯策略

- **短期历史**：`recent_diffs` 只保留 3~5 条，减少噪音。
- **长期追溯**：
  - 可按时间间隔（如 30 分钟）存储锚点快照，作为 `anchor_diff` 来源。
  - 若 LLM 需要更旧状态，可在提示中说明访问方式（例如请求某版本 ID 的详情）。
- **错误恢复**：当工具应用失败时，生成一个带 `is_error` 标记的 `DiffSummary`，供模型参考和重试。

## 与 LLM 的交互惯例

- **系统提示**：明确告知模型：
  1. `current_document` 才代表最新真相。
  2. `recent_diffs` 用于参考近期修改轨迹。
  3. 输出时请使用结构化编辑指令（例如 `apply_patch` 或 JSON 命令）。
- **确认回路**：模型输出后，代理执行并在下一次 Live Context 中反馈“已完成 / 失败 + 失败原因”。
- **节流机制**：限制 `current_document` 过长时的 token 消耗（例如对超长文件只提供窗口 + 索引信息）。

## 初步技术路线

1. **基础设施**
   - 引入 `Microsoft.CodeAnalysis` 包（若项目尚未包含）。
   - 编写 `SourceTextManager` 服务：
     - `SourceText Current { get; private set; }`
     - `ImmutableQueue<DiffSummary> RecentDiffs`
     - 方法：`ApplyChanges(TextChange[] changes)`, `CreateAnchorSnapshot()`, `BuildLiveContextPayload(...)`。
2. **LLM 代理层**
   - 定义消息模板，掺入 Live Context。
   - 实现工具执行结果到 `TextChange` 的映射。
   - 建立错误处理与重试机制。
3. **实验阶段**
   - 针对简单编辑脚本（例如 Markdown 文档）做闭环测试。
   - 观察模型对 diff 的理解是否准确，必要时调整提示格式。
4. **扩展方向**
   - 引入更细粒度的行/列锚点（`TextSpan`）辅助定位。
   - 结合 `SourceText` 的 `Encoding` 信息处理多语言文本。
   - 追加语法信息（可选）：例如基于 Roslyn AST 或其他解析器。

## 风险与待决问题

| 主题 | 风险 | 缓解方案 |
| --- | --- | --- |
| Token 体积 | 大文件的 `current_document` 可能超出限制 | 按需截断、提供分块视图或按请求加载局部片段 |
| Diff 可读性 | 模型可能误解复杂 diff | 尝试统一 diff 样式，必要时提供原文片段 + 指令式描述 |
| 并发编辑 | 多工具同时写入时可能出现版本冲突 | 为 `SourceText` 操作加乐观锁，失败时回滚并提示重试 |
| 历史丢失 | 仅保留少量 diff，模型若需要更长链条可能不足 | 允许模型显式请求历史版本，或动态扩展 diff 数量 |

## 下一步行动

1. 在 `MemoFileProto` 原型中落地 `SourceTextManager`（最简实现：内存噪音 + 环形缓冲区）。
2. 编写 Live Context 序列化与消息模板，跑通“模型提出编辑 → 工具执行 → Live Context 回传”的闭环。
3. 收集 LLM 行为数据，观察其对 Live Context 的利用程度，迭代提示和结构。
4. 评估是否需要将 Live Context 持久化（用于审计或回放）。
5. 记录并量化 read/modify/read 流程的 token 消耗，验证节流策略的实际收益。

---

*附注：本文档定位为初步方案，后续可在实验数据基础上补充格式示例、错误案例以及与多模态扩展的兼容设计。*

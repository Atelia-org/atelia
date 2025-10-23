## 历史注入与多层级呈现

为了减少 App 维护历史副本的负担，同时让上下文可以针对不同模型动态调整细节，我们在“写入阶段”一次性生成多层级的视图，在“渲染阶段”再决定展示粒度。

本设计借鉴了 3D 图形领域的 **Level of Detail (LOD)** 概念：在渲染资源有限的情况下优先保留关键轮廓、按需补充细节。同理，LLM 的上下文预算有限，需要在“全貌”“近期细节”“长期摘要”之间动态取舍。后文统一使用 “LevelOfDetail (LOD)” 来指代这些细节档位，便于与 GPU 场景建立类比，同时保持类型命名的语义直接。

- **固定快照**：每个 App 在变更发生时，更新自身持有的“最新状态”对象，并将该快照注入到最近一次 `ModelInputEntry` 的扩展字段中。代理在 Append 阶段将最新的 Window 渲染结果追加为 `Live` 档中的 `"[Window]"` 段，渲染层不再依赖额外的接口装饰器；Provider 或调试工具可以按需读取该段落，其余历史消息默认隐藏但仍保留数据，便于回放或高保真调试。
- **分层信息**：App 在处理工具调用时，同时生成 `Live / Summary / Gist` 三种粒度的数据：
    - `Live`（对应 `LevelOfDetail.Live`）：在呈现给模型前，需与 App 的 `[Window]` 输出协同。如果 App 已将信息 *I* 渲染到 `[Window]`，则 `Live` 档应避免再次产出 *I* 的完整副本，以免同一条上下文消息出现两份相同内容。可以改为提供 diff、补充说明或直接省略这部分数据。
    - `Summary`：针对近几次操作的精细 diff 或上下文（可含局部文本对比、锚点信息等），不受 `[Window]` 约束。
    - `Gist`：更长远历史的摘要或确认信息，例如“第 3 次编辑已完成”，同样独立于 `[Window]`。
 这些数据可以直接写入关联的 `ToolResultsEntry.Metadata` 或专用的 App 历史条目，作为后续渲染的原料。
- **渲染时选择细节**：`RenderLiveContext()` 根据 Provider 或调用方传入的参数（例如上下文窗口大小、任务场景），在 `Live / Summary / Gist` 之间按需裁剪：
    - 上下文预算充足 → 展示 `Live + Summary`。
    - 预算紧张 → 仅保留 `Live + Gist` 或只保留 `Live`。
    - 未来可扩展为支持动态阈值（如“最近 N 次操作”）。
- **历史压缩（可选）**：旧的 `Summary`/`Gist` 数据在超过阈值后，可以由 AgentState 主动压缩或丢弃，防止 Metadata 无限增长。由于写入阶段已经生成多层级数据，压缩操作不会影响最新状态展示。

这一方案使 App 专注于“生成最新状态 + 多层级描述”，无需维护时间序列；而动态呈现策略保留在 AgentState 的渲染阶段，实现 FIR 式的“渐淡记忆”。

### LevelOfDetailSections 抽象

为避免在不同细节层级之间复制文本，我们引入共用的数据结构 `LevelOfDetailSections`，同时存放三档内容：

```csharp
internal enum LevelOfDetail {
    Live,
    Summary,
    Gist
}

internal sealed class LevelOfDetailSections {
    public IReadOnlyList<KeyValuePair<string, string>> Live { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Summary { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Gist { get; }

    public IReadOnlyList<KeyValuePair<string, string>> GetSections(LevelOfDetail lod) => lod switch {
        LevelOfDetail.Live => Live,
        LevelOfDetail.Summary => Summary,
        LevelOfDetail.Gist => Gist,
        _ => Live
    };
}
```

- 三个列表可共享同一批字符串实例（内部约定由构造逻辑保证），减少不同层级之间的内存复制。
- `ModelInputEntry` 与 `HistoryToolCallResult` 改为持有 `LevelOfDetailSections` 字段，原有的 `ContentSections`/`Result` 字段则由包装层 (`ModelInputMessage` / `ToolResultsMessage`) 通过调用 `GetSections(lod)` 动态映射为目标粒度。
- 渲染阶段调用 `GetSections(lod)` 获得对应粒度的内容，Provider 无需感知内部细节划分；若仅需等价的单档数据，可使用 `LevelOfDetailSections.CreateUniform()` 进行兼容包装。

### 渲染策略

`RenderLiveContext()` 在遍历历史条目时，根据目标模型的上下文预算、任务类型等因素决定使用的 `LevelOfDetail`，并将选中的 `Section` 暂存于轻量包装对象 (`ModelInputMessage` / `ToolResultsMessage`) 中：

- 最后一条准备发送给模型的用户输入 → `LevelOfDetail.Live`。
- 最近几条输入/工具结果 → `LevelOfDetail.Summary`（diff、上下文补丁）。
- 更早的条目 → `LevelOfDetail.Gist`（摘要、确认信息）。

包装对象依旧实现 `IModelInputMessage` / `IToolResultsMessage` 接口，只是将 `ContentSections` 映射为对应层级，保持历史条目的不可变性。

### 写入端协作

- App 在工具调用成功后构造 `LevelOfDetailSections`：
    - `Live`：为 `LevelOfDetail.Live` 预留的段落，通常用于补充实时说明、局部差异或与 `[Window]` 不重复的附加信息。
    - `Summary`：数次操作的 diff 或局部上下文。
    - `Gist`：较旧操作的概述（“第 5 次编辑成功”）。
- `ToolHandlerResult.Metadata` 可继续记录操作类型、长度变化等诊断信息，与 `LevelOfDetailSections` 的 textual 数据互补。
- AgentState 统一负责将 `LevelOfDetailSections` 写入 `ModelInputEntry` / `ToolResultsEntry`，并在渲染阶段选择合适的 LOD，调用方无需关心细节裁剪与 Window 注入逻辑。

### `[Window]` 协同策略

- App 渲染 `[Window]` 时可输出完整快照或聚合视图，以确保最新状态始终在最新上下文消息中呈现。
- 同一 App 的工具调用结果在填写 `Live` 档位时，应当首先判断哪些信息已经由 `[Window]` 呈现，并以“去重”后的形式表达。例如：
    - 仅返回本次操作的 diff、告知摘要或成功提示；
    - 使用 `LevelOfDetailSectionExtensions.WithoutWindow` 过滤后再补充额外段落。
- `Summary` / `Gist` 档位可以继续输出更早期的摘要或结构化信息，无需考虑 `[Window]` 是否重复。

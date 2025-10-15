## 历史注入与多层级呈现

为了减少 Widget 维护历史副本的负担，同时让上下文可以针对不同模型动态调整细节，我们在“写入阶段”一次性生成多层级的视图，在“渲染阶段”再决定展示粒度。

- **固定快照**：每个 Widget 在变更发生时，更新自身持有的“最新状态”对象，并将该快照注入到最近一次 `ModelInputEntry` 的扩展字段中。渲染层只在“最后一条待发给模型的用户消息”上，通过 `LiveScreenDecoratedMessage` 暴露此全量快照，其余历史消息默认隐藏但仍保留数据，便于回放或高保真调试。
- **分层信息**：Widget 在处理工具调用时，同时生成 `Live / Recent / Gist` 三种粒度的数据：
    - `Live`：与当前快照一致的全量信息（通常作为 Live Screen 片段）。
    - `Recent`：针对近几次操作的精细 diff 或上下文（可含局部文本对比、锚点信息等）。
    - `Gist`：更长远历史的摘要或确认信息，例如“第 3 次编辑已完成”。
 这些数据可以直接写入关联的 `ToolResultsEntry.Metadata` 或专用的 Widget 历史条目，作为后续渲染的原料。
- **渲染时选择细节**：`RenderLiveContext()` 根据 Provider 或调用方传入的参数（例如上下文窗口大小、任务场景），在 `Live / Recent / Gist` 之间按需裁剪：
    - 上下文预算充足 → 展示 `Live + Recent`。
    - 预算紧张 → 仅保留 `Live + Gist` 或只保留 `Live`。
    - 未来可扩展为支持动态阈值（如“最近 N 次操作”）。
- **历史压缩（可选）**：旧的 `Recent`/`Gist` 数据在超过阈值后，可以由 AgentState 主动压缩或丢弃，防止 Metadata 无限增长。由于写入阶段已经生成多层级数据，压缩操作不会影响最新状态展示。

这一方案使 Widget 专注于“生成最新状态 + 多层级描述”，无需维护时间序列；而动态呈现策略保留在 AgentState 的渲染阶段，实现 FIR 式的“渐淡记忆”。

### MultiLevelSections 抽象

为避免在不同细节层级之间复制文本，我们引入共用的数据结构 `MultiLevelSections`，同时存放三档内容：

```csharp
internal enum DetailLevel {
    Live,
    Recent,
    Gist
}

internal sealed class MultiLevelSections {
    public IReadOnlyList<KeyValuePair<string, string>> Live { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Recent { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Gist { get; }

    public IReadOnlyList<KeyValuePair<string, string>> GetSections(DetailLevel level) => level switch {
        DetailLevel.Live => Live,
        DetailLevel.Recent => Recent,
        DetailLevel.Gist => Gist,
        _ => Live
    };
}
```

- 三个列表可共享同一批字符串实例（内部约定由构造逻辑保证），减少不同层级之间的内存复制。
- `ModelInputEntry` 与 `ToolCallResult` 改为持有 `MultiLevelSections Sections` 字段，原有的 `ContentSections`/`Result` 字段可逐步退场或转为 `Live` 层级的兼容包装。
- 渲染阶段调用 `GetSections(level)` 获得对应粒度的内容，Provider 无需感知内部细节划分。

### 渲染策略

`RenderLiveContext()` 在遍历历史条目时，根据目标模型的上下文预算、任务类型等因素决定使用的 `DetailLevel`，并将选中的 `Section` 暂存于轻量包装对象中：

- 最后一条准备发送给模型的用户输入 → `DetailLevel.Live`。
- 最近几条输入/工具结果 → `DetailLevel.Recent`（diff、上下文补丁）。
- 更早的条目 → `DetailLevel.Gist`（摘要、确认信息）。

包装对象依旧实现 `IModelInputMessage` / `IToolResultsMessage` 接口，只是将 `ContentSections` 映射为对应层级，保持历史条目的不可变性。

### 写入端协作

- Widget 在工具调用成功后构造 `MultiLevelSections`：
  - `Live`：全量最新状态（Memory Notebook 为整本文本）。
  - `Recent`：数次操作的 diff 或局部上下文。
  - `Gist`：较旧操作的概述（“第 5 次编辑成功”）。
- `ToolHandlerResult.Metadata` 可继续记录操作类型、长度变化等诊断信息，与 `MultiLevelSections` 的 textual 数据互补。
- AgentState 统一负责将 `MultiLevelSections` 写入 `ModelInputEntry` / `ToolResultsEntry`，调用方无需关心渲染策略细节。

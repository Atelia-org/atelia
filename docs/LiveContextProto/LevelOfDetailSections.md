## 历史注入与双层级呈现

为了减少 App 维护历史副本的负担，同时让上下文可以针对不同模型动态调整细节，我们在“写入阶段”一次性生成多层级（目前为双层）的视图，在“渲染阶段”再决定展示粒度。

本设计借鉴了 3D 图形领域的 **Level of Detail (LOD)** 概念：在渲染资源有限的情况下优先保留关键轮廓、按需补充细节。在 LiveContext 原型中，LOD 已经简化为两个档位：`LevelOfDetail.Basic` 与 `LevelOfDetail.Detail`。`Basic` 提供面向紧凑上下文预算的最小描述，`Detail` 则保留完整上下文或详尽注释。得益于这种划分，我们仍能复用“多层级”策略，同时减少写入端的分支逻辑。

- **固定快照**：每个 App 在变更发生时，仍需更新自身持有的“最新状态”对象，并将该快照注入到最近一次 `ModelInputEntry` 的扩展字段中。代理在 Append 阶段将最新的 Window 渲染结果追加为 `"[Window]"` 段，该段通常会映射到 `Detail` 档位；Provider 或调试工具可以按需读取该段落，其余历史消息默认隐藏但仍保留数据，便于回放或高保真调试。
- **双层信息**：App 在处理工具调用时，同时生成 `Basic / Detail` 两种粒度的数据：
    - `Basic`（对应 `LevelOfDetail.Basic`）：提供紧凑、摘要化的信息，例如“提交成功”“已重命名 3 个文件”等。若 App 已在 `[Window]` 中呈现了同一份内容，应当在 `Basic` 中避免重复，可改为简化说明或指向 diff。
    - `Detail`（对应 `LevelOfDetail.Detail`）：保留完整上下文、详细 diff 以及附加诊断信息。该档位为需要“全量回放”的渲染或调试模式服务。
  这两档数据可以写入关联的 `ToolResultsEntry.Metadata` 或专用的 App 历史条目，作为后续渲染的原料。
- **渲染时选择细节**：`RenderLiveContext()` 根据 Provider 或调用方传入的参数（例如上下文窗口大小、任务场景），在 `Basic / Detail` 之间按需裁剪：
    - 上下文预算充足 → 直接采用 `Detail`。
    - 预算紧张 → 保留 `Basic`，仅对最新或最关键的条目切换到 `Detail`。
    - 未来可扩展出动态阈值（如“最近 N 条使用 Detail”）。
- **历史压缩（可选）**：旧的 `Detail` 数据在超过阈值后，可以由 AgentState 主动压缩或丢弃，防止 Metadata 无限增长。由于写入阶段已经生成双层数据，压缩操作不会影响最新状态展示。

这一方案使 App 专注于“生成最新状态 + 双层描述”，无需维护时间序列；而动态呈现策略保留在 AgentState 的渲染阶段，实现 FIR 式的“渐淡记忆”。

### LevelOfDetailContent 抽象

为避免在不同细节层级之间复制文本，本方案引入轻量的 `LevelOfDetailContent` 容器，同时存放 `Basic` 与 `Detail` 两档内容。写入端负责构造这两个版本的文案，渲染端在需要时按目标档位取用即可。

- `Basic` 与 `Detail` 始终成对出现，写入时即完成去重与压缩，渲染阶段无需再比对差异。
- 历史条目（如 `ModelInputEntry`、`HistoryToolCallResult` 等）仅保留 `LevelOfDetailContent` 的引用，保持数据不可变；实际呈现由外层根据上下文预算选择具体档位。
- 针对需要聚合多条通知的场景，提供了配套的合并辅助方法，确保 `Basic` 与 `Detail` 始终同步扩展；具体实现细节留在代码层处理，文档不再展开。

### 渲染策略

`RenderLiveContext()` 在遍历历史条目时，根据目标模型的上下文预算、任务类型等因素决定使用的 `LevelOfDetail`，并将选中的内容暂存于轻量包装对象 (`ModelInputMessage` / `ToolResultsMessage`) 中：

- 最后一条准备发送给模型的用户输入 → 优先 `LevelOfDetail.Detail`，确保模型感知完整上下文。
- 最近几条输入/工具结果 → 默认 `LevelOfDetail.Basic`，必要时再切换至 `Detail` 补全信息。
- 更早的条目 → 保持 `LevelOfDetail.Basic`，仅在调试模式或回放需求下请求 `Detail`。

包装对象依旧实现 `IModelInputMessage` / `IToolResultsMessage` 接口，只是将 `ContentSections` 映射为对应档位，保持历史条目的不可变性。

### 写入端协作

- App 在工具调用成功后构造 `LevelOfDetailContent`：
    - `Basic`：为 `LevelOfDetail.Basic` 预留的段落，通常用于补充轻量说明、局部差异或与 `[Window]` 不重复的附加信息。
    - `Detail`：包含全文、结构化 diff、日志片段等，用于高保真回放。
- `ToolHandlerResult.Metadata` 可继续记录操作类型、长度变化等诊断信息，与 `LevelOfDetailContent` 的 textual 数据互补。
- AgentState 统一负责将 `LevelOfDetailContent` 写入 `ModelInputEntry` / `ToolResultsEntry`，并在渲染阶段选择合适的 LOD，调用方无需关心细节裁剪与 Window 注入逻辑。

### `[Window]` 协同策略

- App 渲染 `[Window]` 时可输出完整快照或聚合视图，以确保最新状态始终在最新上下文消息中呈现；该内容通常会作为 `Detail` 的核心组成。
- 同一 App 的工具调用结果在填写 `Basic` 档位时，应当首先判断哪些信息已经由 `[Window]` 呈现，并以“去重”后的形式表达。例如：
    - 仅返回本次操作的 diff、成功提示或高层摘要；
    - 通过 App 层的 diff/过滤逻辑剥离 `[Window]` 已覆盖的片段后，再补充额外段落。
- `Detail` 档位可以继续输出更早期的补充信息或结构化数据，无需考虑 `[Window]` 是否重复。

## 架构鸟瞰
- **AgentEngine（状态机核心）**：围绕 `AgentRunState` 驱动 Sense–Think–Act 循环，按状态分派等待输入、模型调用、工具执行、汇总结果等分支，并通过一组事件（`WaitingInput`/`BeforeModelCall`/`AfterModelCall`/`BeforeToolExecute`/`AfterToolExecute`/`StateTransition`）开放扩展点。
- **AgentState（历史存储）**：维护 `HistoryEntry` 列表、系统提示词与待注入的通知队列。各条目分为 `ObservationEntry`、`ActionEntry`、`ToolEntry`，都带时间戳和 `LevelOfDetailContent`，可按 Basic/Detail 两档输出。`RenderLiveContext()` 会回溯历史，优先给最近一条 Observation 使用 Detail，其余默认 Basic，并只在最合适的那一条注入由 App 输出的 `[Window]` 片段。
- **Tool 与 App 生态**：`MethodToolWrapper` 负责把带 Attribute 的方法自动包装成 `ITool`；`DefaultAppHost` 聚合 App，并把每个 App 渲染的 Window 拼成统一视图。当前默认装配 `MemoryNotebookApp`，提供 `memory_notebook_replace` / `_replace_span` 工具和窗口快照。
- **通知机制**：主机侧通过 `AppendNotification` 写入待处理通知，下一条 Observation 或 Tool 结果会自动附带，并可在模型失败重试前保留（TODO 文档还计划补强 ID 与确认语义）。

## 历史管理现状
- **写入即生成双 LOD**：所有新增条目都要求在写入时就准备好 Basic/Detail，两档内容可通过 `LevelOfDetailContent.Join` 聚合，避免渲染期做昂贵裁剪。
- **上下文渲染策略**：`AgentEngine.RenderLiveContext()` 先向各 App 拉取 `[Window]`，再委托 `AgentState` 将历史投影成 `IHistoryMessage` 列表。Detail 主要保留最近一条 Observation，其余条目走 Basic，适配不同模型上下文预算。
- **模型调用与工具回填**：`CompletionAccumulator` 聚合流式 delta；工具调用流程会按照模型给出的 `ParsedToolCall` 顺序执行，结果暂存 `_pendingToolResults`，等全部到齐再生成一条 `ToolEntry`。失败会从 Basic 内容中摘取摘要写入 `ExecuteError`。

## Memory Notebook 与潜在 Recap 接入点
- Memory Notebook 作为内建 App，窗口渲染在最新上下文中始终可见，工具调用返回的 LOD 文案也能提醒操作差异（长度、锚点等）。
- 目前 RecapMaintainer 仅有占位类（RecapMaintainer.cs），它可以通过注册新 App 或在事件钩子里追加 Observation/ToolEntry 的方式参与历史变更。
- AgentEngine 暂未限制历史长度，Recap 可以在 `WaitingInput` 事件阶段插入补充 Observation 或通过新的工具调用对 Recap 文本做增量摘要，再将老条目裁剪出主历史。

## 现实差距（阻塞 Recap 接入的两个前置条件）
1. **历史截断/裁剪机制缺位**
   - TODO-History裁剪与反射.md 明确计划引入 `HistoryLimitOptions`（限制条数/字节数）与缓存策略，但代码里还没有任何实现。
   - 辅助数据结构（如 `src/Data/SlidingQueue`）已经存在，可作为按需压缩的工具，但尚未与 AgentState 打通。
2. **历史反射/编辑能力未落地**
   - 目前历史只支持 append，不支持“回溯编辑/摘取”。RecapMaintainer 想变更旧条目或将其转录进 Recap，需要新增 API：比如按索引读取批量条目、标记“已摘要”的条目、替换条目的细节等级等。
   - TODO 文档提到要让 RecapMaintainer、Daemon/Analyzer SubAgent 可以直接操作历史，这意味着 AgentState 需要暴露受控的编辑接口（并考虑持久化、并发与事件协调）。

## 下一步建议
- **优先补全 HistoryLimitOptions**：确定单位（条数、Token 估算或原始字节），并决定触发点（模型调用前 / WaitingInput 事件中）。可以先实现简单策略（最近 N 条保留 Detail，超过阈值转 Basic，最旧条目转 Recap 队列）。
- **设计历史反射 API**：
  - 读取：按时间或 Kind 分片获取历史片段，并附带条目的 LOD / Tool metadata。
  - 编辑：支持将旧 Observation 转换成“已收录”标记、从主历史中移除或降级为更紧凑的摘要条目。
  - 审计：为 Recap 工具链记录操作来源和时间，便于回放。
- **明确 RecapMaintainer 的触发时机与资源**：可能在 `WaitingInput` 中检测历史长度超阈值，或监听 `AfterModelCall` / `ProcessToolResultsReady` 完成后异步触发。需要评估其使用的模型/工具预算，与 Memory Notebook 协作策略（Recap 是否写入 Notebook 或独立文件）。
- **完善通知清理逻辑**：TODO 文档建议给 `_pendingNotifications` 加 ID，Recap 在多轮尝试时也要避免重复注入旧通知。

目前没有代码改动，因此尚未触发构建/测试。后续若开始实现裁剪和反射，请同步补上单测（例如针对 `RenderLiveContext` 的 LOD 选择、Recap 摘录流程、裁剪后再渲染的稳定性），以便在进入 RecapMaintainer 正式接入前把地基打牢。
历史裁剪与缓存：引入 HistoryLimitOptions，支持“最大条数/总字节数”；

引入对历史的反射编辑能力，用以支持RecapMaintainer和Daemon/Analyzer SubAgents

可选的性能优化：同时缓存最近构建好的 IHistoryMessage，只在有新 Entry 时增量更新。

刚收到新的模型切换，我这就接着评估你提出的“RecentHistory + 持久历史 + RecapEntry”分层方案，看它是否稳妥。

## 思路拆解与合理性评估

### 1. 内存中的 `RecentHistory` vs. 落盘的只读追加历史

- **意图**：把内存只承担“最近活跃上下文”的责任，较远历史沉到磁盘，避免内存膨胀，也让历史反射范围有明确上限。
- **优势**：
  - ✅ 帮 Recap 和 Meta 级分析明确操作边界，知道哪些条目仍可编辑或裁剪。
  - ✅ 启动时按照“从近到远”加载，遇到 RecapEntry 即停止，能快速恢复最近状态，不必一次性读全量日志。
  - ✅ 便于实现“短期记忆/中期记忆/长期记忆”的分层：RecentHistory 作为 RNN-style working memory，持久层 + Recap 作为长期记忆。
- **注意点**：
  - 需要定义 `RecentHistory` 的容量策略（条数、Token、时间窗口），以及与 LOD 降级的协同逻辑。
  - 内存历史与落盘历史的断点需要可靠记录（可能用 checkpoint/offset 文件或 RecapEntry 标记）。
  - 设计数据结构时要考虑重启后恢复顺序：读取落盘条目 → 遇到 RecapEntry → 将其作为“初始 recap”加载 → 再继续从最近持久条目开始构建 `RecentHistory`。

整体来看，这一步能强化资源控制和反射边界，合理且必要。

### 2. Recap 建模为 `ObservationEntry` 的派生类 `RecapEntry`

- **语义定位**：Recap 不是工具输出，也不是 Agent 动作；它属于环境对 Agent 的补充观测，因此继承 `ObservationEntry` 是符合语义的。
- **设计好处**：
  - ✅ RecapEntry 既保留了所有历史序列的连续性，又在类型层面清晰区分出“这是压缩摘要，而非原始消息”。
  - ✅ 可以给 `RecapEntry` 额外元数据（覆盖范围、生成时间、RecapMaintainer ID），便于调试和回放。
  - ✅ `ProjectContext` 能针对 RecapEntry 调整呈现逻辑，比如把 Basic 版当成 timeline 引导语，Detail 版保留更丰富摘要。
  - ✅ 序列化时自然融入历史文件格式，重启时遍历历史时遇到 `RecapEntry` 就知道“从这里往前的内容已归档可读不可写”。
- **潜在挑战**：
  - 需要定义 RecapEntry 与其“覆盖的原始条目范围”的映射。这可以通过在 RecapEntry 中记录 `CoveredUntilEntryId` 或时间戳，或者写入一份 meta 文件。
  - 确保 RecapEntry 插入时与持久历史的一致性：在落盘时可能要先 flush 原始条目，再写 RecapEntry，再标记内存历史的起点。

总体而言，把 Recap 具象成独立条目是很合理的。它既保留了时间线，又避免让“Recap 文本”漂浮在各类配置或 Notebook 里。

### 3. 明确反射机制的范围

- **你提出的边界**：RecapEntry 之前的历史在磁盘，只读不可改；RecapEntry 之后的 RecentHistory 在内存，可被 SubAgent 编辑（裁剪、降级、再 Recap）。
- **优势**：
  - ✅ 明确反射 API 的“安全区”：不会误编辑已归档历史。
  - ✅ 可以允许 RecapMaintainer 在必要时加载一小段“落盘历史”做二次压缩，但默认不回写，以保持历史不可变性。
- **落地建议**：
  - 在 AgentState 中区分两个集合：`RecentHistory`（可编辑）与 `ArchivedHistoryOffset`（落盘位置），并提供只读游标遍历磁盘历史。
  - 为 RecapEntry 附带“源段摘要”+“归档校验信息”（如条目数量或 hash），防止漏记与重复 Recap。
  - 若未来需要“把特定旧段重新加载回内存”做深度分析，需定义明确的 API，且保持写保护。

整体上，这个方案为“反射可以操作的范围”“Recap 的语义位置”“历史加载与持久化策略”都给出了很清晰的框架。我要说，它既解决了之前的模糊点，又不影响现有 Sense-Think-Act 流程，是一个合理且值得推进的方向。

## 后续落地建议

- **数据结构升级**：给 `AgentState` 增加 `RecentHistory` + 落盘游标；实现 `RecapEntry : ObservationEntry`，补上序列化与反序列化逻辑。
- **持久化协议**：在历史文件中约定 RecapEntry 的格式，让加载器遇到它时切换模式。
- **反射 API**：在内存层提供“仅限 RecentHistory”的编辑接口，并输出清晰的错误（试图跨边界操作时抛异常）。
- **RecapMaintainer 更新路径**：当生成 RecapEntry 时，先写入落盘，再从 `RecentHistory` 中剔除对应原始条目，最后插入 RecapEntry（确保时间线一致）。
- **测试保障**：构建 restart 场景单测，验证从磁盘（含 RecapEntry）加载后，`RecentHistory` 与 Recap 状态都恢复正确。

综上，我认为这条路线非常合理，解决了历史分层与 Recap 语义的问题，也为后续引入更多长期记忆机制打好了基础。接下来我们可以围绕这套设计，开始分阶段实现与验证。
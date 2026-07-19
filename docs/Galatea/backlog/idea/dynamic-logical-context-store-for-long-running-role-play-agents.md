更新了待办事项列表

我同意先不做 task-05。它原来的方向本质是“给现有 projection 旁边补一条 raw log”，这能改善可恢复性和导出，但对你现在描述的长期 Role-Play Agent 目标，能力增量确实不够大。它解决的是账本问题，不是“动态逻辑上下文”的问题。

我现在会把问题重新拆成三层：

1. **Raw Event Substrate**
   原始消息序列必须存在，但它只是事实源。它应该尽量不可变、append-only、可定位、可引用。每个 user/action/tool/world-state/system-update 都是 event，有稳定 id、时间、角色、来源、内容块、可选 embedding/hash。

2. **Derived Memory Artifacts**
   在 raw event 上生成各种派生产物，而不是把它们混进同一个 deque：
   - scene / episode summary
   - character state
   - relationship state
   - open threads / promises / unresolved hooks
   - world facts
   - emotional trajectory
   - style / tone / continuity constraints
   - symbolic entities and aliases
   - embeddings / keyword index / temporal index
   - recap with provenance

   关键是每个 artifact 都要带 provenance：它来自哪些 raw event range、哪些旧 artifact、由哪个 analyzer 版本生成、置信度如何、是否已被后续内容 supersede。

3. **Context Assembly Engine**
   真正喂给模型的上下文不是“取最近 N 条消息”，而是一次 query/recomposition：
   - 固定前缀：system prompt、角色合同、当前任务
   - short-term window：最近若干 raw messages
   - active scene memory：当前场景相关摘要
   - seeded recall：根据当前 user input / scene / entities 找回相关 episode
   - continuity constraints：人物关系、未完成事件、世界状态
   - anti-drift reminders：风格、禁忌、长期承诺

   这层才是“动态逻辑上下文”的核心。

我觉得一个合适的数据模型不是 `DurableDeque<Message>`，而更像：

```text
SessionMemoryStore
├── RawEvents               append-only sequence
├── EventIndex              eventId -> offset / timestamp / role / kind
├── IntervalAnnotations     [startEvent, endEvent) -> tags / summary / scene / topic
├── EntityGraph             entity -> facts / aliases / relationships / mentions
├── EpisodeGraph            episodes with temporal + causal + thematic links
├── ArtifactStore           summaries, extracted facts, classifiers, embeddings
├── RecallIndex             vector / keyword / symbolic / recency indexes
└── ProjectionHeads         current context views, checkpoints, analyzer cursors
```

这里比较重要的设计原则：

- **Raw log 是地基，但不是产品形态**。不要指望 raw message sequence 自己承担高级记忆。
- **摘要不是替代原文，而是带 provenance 的索引节点**。可以从摘要追溯到原始片段。
- **多时间尺度并存**。最近消息、当前场景、最近一章、角色长期记忆、世界设定，更新频率和召回方式都不同。
- **分析结果可重算**。Analyzer 版本升级后，可以从 raw events 重新生成 memory artifacts。
- **记忆不是单一向量库**。向量召回适合语义相似，但 Role-Play 还需要时间顺序、角色关系、承诺/伏笔、状态一致性，这些更偏 symbolic/graph。
- **上下文组装要有预算模型**。不是“召回越多越好”，而是按 token budget 分配：最近上下文、场景摘要、角色状态、相关回忆各占多少。

技术路线我会倾向于渐进式：

**第一阶段：不要急着造完整 Memory OS。**
保留现在的 ChatSession/StateJournal，但把 task-05 从“raw message log 实现任务”降级成“长期记忆架构研究笔记”。现在 exporter 已经能恢复旧历史，raw log 不再急迫。

**第二阶段：先做 Memory Artifact schema。**
比如定义：

```text
MemoryArtifact
- id
- kind: scene-summary | character-state | relationship | world-fact | open-thread | episode
- content
- sourceEventRange
- sourceArtifactIds
- createdByAnalyzer
- confidence
- validFromEvent
- supersededBy?
- tags/entities
```

这一步不必绑定最终存储。先把“什么信息值得保存”摸清楚。

**第三阶段：做一个离线 analyzer pipeline。**
从现有 `chat-session-legacy-upgrade-export.json` 或纯文本 markdown 读入历史，生成：
- scenes
- episode summaries
- entity mentions
- relationship facts
- unresolved threads

这可以先跑在文件上，不嵌进运行时。好处是你能用真实 Galatea 历史观察这些 artifact 是否有用。

**第四阶段：再决定存储结构。**
到那时再比较：
- StateJournal repo 内对象图
- 独立 RBF artifact store
- SQLite/FTS/vector index
- 混合方案

我现在的直觉是：长期 Role-Play Agent 的 memory store 可能不该只靠 StateJournal durable containers。StateJournal 很适合保存一致性的 session state/projection/checkpoint，但全文检索、向量召回、图查询、artifact 重算索引，可能更适合外部专门 store。StateJournal 可以保存“权威头指针和版本状态”，而不是承担所有查询结构。

所以 task-05 暂停是对的。更好的下一步不是实现 raw log，而是新开一个更高层的设计任务，例如：

```text
Task 07: Dynamic Logical Context Store for Long-Running Role-Play Agents
```

核心问题从“消息怎么 append-only 保存”升级为：

> 如何把无限长交互历史转化为可追溯、可重算、可召回、可组装的多层记忆结构？

这个方向更贴近你真正想要的东西。

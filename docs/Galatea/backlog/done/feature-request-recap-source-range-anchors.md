# Feature Request: Recap Source Range Anchors

> 状态：Backlog / Todo
> 范围：`prototypes/ChatSession` 与复用它的 `prototypes/Galatea`
> 动机：让维护工具能够从当前会话 HEAD 出发，可靠追溯 recap 对应的原始消息范围。

## 背景

Galatea 当前复用 `Atelia.ChatSession` 持久化对话历史。普通 turn 会把 user observation、assistant action、tool results 追加到 `messages` deque；当上下文过长时，`CompactAsync(...)` 会选择一个 split point，把旧前缀交给 summarizer，再用一条 `RecapMessage` 替换这段旧前缀。

这能让活跃上下文保持较短，但也带来一个维护工具层面的缺口：最新 HEAD 中只保留 recap 文本和剩余 recent history，不再直接记录这条 recap 对应了原始消息序列中的哪个范围。

## 需求

在 compaction / recap 过程中记录 recap 与原始消息序列的范围映射关系。

最低要求：每条 recap record 应能回答：

- 它替代的是哪一次 compaction 前的消息序列。
- 它对应原始消息序列的哪个区间。
- 离线维护工具应如何从该 recap 回到原始消息片段。

典型使用场景：导出 Galatea / ChatSession 的原始历史为 Markdown 时，枚举器可以选择：

- 跳过 recap，仅导出当前 HEAD 中仍可达的普通消息。
- 把 recap 当作单独消息导出。
- 展开 recap，递归追溯并导出它对应的原始消息区间。
- 同时导出 recap 与其关联的原始消息片段，供人工审阅摘要质量。

## 当前缺口

StateJournal 底层并非完全没有历史信息：branch ref / reflog 中有 old head 与 new head，object version frame 中也有 per-object parent ticket。但这些属于存储层演化信息，不表达 ChatSession 语义层的“本条 recap 替代了哪段消息”。

当前 recap record 主要记录：

- `kind = recap`
- `content = summary`
- `timestampUtc`

它缺少：

- compaction 前的 `CommitAddress`。
- compaction 前消息总数。
- 被 recap 替换的起止索引。
- 可用于递归展开的稳定来源描述。

因此，从最新 HEAD root 单独出发时，工具只能看到“这里有一条 recap”，不能可靠知道“这条 recap 对应旧历史中的哪段”。

## 建议方案 A: 在 Recap Record 中记录 Source Range

在 `ChatSessionEngine.ExecuteCompactionCoreAsync(...)` 中，删除旧前缀之前捕获 compaction source 信息，并写入 `MessageRecord.PrependRecap(...)` 创建的 recap record。

建议字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `sourceHeadBeforeCompaction` | string | compaction 开始前 branch HEAD 的 `CommitAddress.ToString()` |
| `sourceBranchName` | string | 通常为 `main`，用于诊断与工具提示 |
| `sourceStartIndex` | int | 被替换范围起点，MVP 固定为 `0` |
| `sourceEndExclusive` | int | 被替换范围终点，通常等于 `splitIndex` |
| `sourceMessageCountBefore` | int | compaction 前完整消息数 |
| `compactionKind` | string | MVP 可为 `prefix-summary` |
| `createdAtUtc` | string 或 ticks | recap anchor 创建时间；可复用或补充现有 `timestampUtc` |

有了这些字段，展开算法可以是：

1. 打开当前 ChatSession repo，checkout `main`。
2. 枚举当前 `messages`。
3. 遇到带 source anchor 的 recap。
4. 从 `sourceHeadBeforeCompaction` 创建只读/临时 branch 或 checkout 视图。
5. 读取 source revision 的 `messages[sourceStartIndex..sourceEndExclusive)`。
6. 若 source range 内还有更早 recap，则按策略递归展开或作为关联序列保留。

这个方案的优点是语义精确、导出工具简单，且不会要求 StateJournal 暴露过多存储内部细节。

## 备选方案 B: StateJournal 层增加 Commit 父链遍历接口

也可以在 StateJournal 层增加一个离线遍历接口，沿着 commit / branch reflog / object parent 信息回溯历史，再由工具比较每个旧 HEAD 的 root 状态，推断完整消息演化。

这个方向的吸引力：

- 工具不需要一开始就理解 ChatSession compaction 细节。
- 对其他基于 StateJournal 的上层 schema 也可能有价值。
- 离线工具可以接受较高的时间、内存和 IO 成本。

但需要注意：

- StateJournal 的 object parent ticket 是 per-object version chain，不等同于“ChatSession root 每次 commit 的语义父提交”。
- branch reflog 有 old/new head，但 reflog 是元数据恢复辅助，不是完整语义事件日志。
- 即便能枚举旧 HEAD，也仍要通过比较不同 root 的 `messages` 才能猜测哪条 recap 替换了哪段前缀。
- 对已有历史做 best-effort 恢复可以接受；对未来功能最好仍由 ChatSession 写入显式 source anchor。

因此，StateJournal 父链遍历更适合作为维护工具的增强能力，而不应替代 ChatSession 语义层 source range 记录。

## 初步取舍

MVP 推荐先做方案 A。

理由：recap 的范围映射是 ChatSession 的业务语义，应该在产生 recap 的同一事务附近记录下来。这样未来导出、审计、摘要质量评估、记忆重吸收都能拿到稳定锚点。

方案 B 可作为后续离线工具能力继续评估，尤其用于：

- 迁移或抢救没有 source anchor 的旧会话。
- 构建 StateJournal repo 级诊断器。
- 比较不同 commit 的 root 图变化。

## 验收草案

- 新 compaction 生成的 recap record 包含 source range anchor。
- ChatSession 历史枚举 API 能读出 recap anchor 元数据。
- Markdown 导出器可以选择 include / skip / expand recap。
- 对没有 anchor 的旧 recap，导出器明确标记为 `unresolved-recap`，不假装已经展开。
- 单元测试覆盖：一次 compaction 后，recap anchor 指向 compaction 前 HEAD，且 source range 与被替换 prefix 一致。

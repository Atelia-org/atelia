# Task 05: Append-Only Raw Message Log for ChatSession

> 状态：Idea / Future Design
> 建议执行者：适合做 Shape/Plan 层设计的新会话
> 优先级：中。它是长期架构整理，不阻塞近期 recap anchor。

## 背景

当前 ChatSession 把“原始消息账本”和“当前上下文工作态”都放在 StateJournal root 的 `messages` deque 中。compaction 会直接把旧前缀替换成 recap，因此活跃上下文变短，但原始消息流不再是一个永不缩短的 append-only log。

长期来看，可以把两者拆开：

- StateJournal root 保存当前 projection：recent messages、recap、Memory Pack、索引、维护状态。
- 独立 raw message log 保存完整原始事件流：user observation、assistant action、tool results、maintenance events。

这样 recap 只需要记录 raw log range，导出完整历史可以直接扫描 raw log。

## 关键文件

- [`prototypes/ChatSession/ChatSessionEngine.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.cs)
- [`prototypes/ChatSession/ChatSessionEngine.State.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.State.cs)
- [`prototypes/ChatSession/MessageRecord.cs`](../../../../prototypes/ChatSession/MessageRecord.cs)
- [`prototypes/Completion.Abstractions/ActionMessageSerialization.cs`](../../../../prototypes/Completion.Abstractions/ActionMessageSerialization.cs)
- [`docs/Rbf/rbf-guide.md`](../../../Rbf/rbf-guide.md)
- [`docs/Rbf/rbf-interface.md`](../../../Rbf/rbf-interface.md)
- [`docs/StateJournal/usage-guide.md`](../../../StateJournal/usage-guide.md)

## 目标

产出一份设计方案，评估是否为 ChatSession 引入 append-only raw message log。实现可后置。

需要回答：

- raw log 使用独立 RBF 文件、StateJournal repo 内 durable deque，还是 JSONL-like 文件？
- raw log entry 的稳定 schema 是什么？
- 每条 projected message 如何引用 raw log offset/range？
- compaction、rewind、memory maintenance 如何影响 raw log 与 projection？
- crash consistency 如何处理？
- 旧 repo 如何迁移？

## 设计倾向

优先考虑独立 append-only RBF 文件，原因：

- 与项目现有 RBF 基础设施一致。
- 可以用 frame ticket / offset 作为稳定 anchor。
- 比 JSONL 更适合保存 action blocks、tool calls、未来二进制 payload。
- 不必为了导出完整原始历史反复 checkout StateJournal 历史 commits。

但也要比较 JSONL-like 方案：

- 人类可读。
- 简单，易于调试。
- 对早期原型可能更快落地。

## 非目标

- 不要求本任务直接实现 raw log。
- 不要求替换现有 StateJournal `messages` projection。
- 不要求设计跨进程并发写入。
- 不要求支持任意 provider-native reasoning replay；只需保留当前 `ActionMessageSerialization` 能稳定表达的内容。

## 验收

- 产出一份设计文档，包含至少两种候选方案比较。
- 明确 raw log 与 StateJournal projection 的一致性边界。
- 明确 recap 如何引用 raw log range。
- 明确导出 Markdown 时如何选择 raw log、projection、recap summary 三种来源。
- 给出推荐 MVP 路线和暂缓项。

## 风险点

- 双写一致性是主要复杂度。必须设计 crash 后如何判断 raw log 已写但 projection 未提交，或 projection 已提交但 raw log 缺失。
- raw log 一旦成为长期档案，schema 演进要比当前 prototype 更谨慎。
- 如果 raw log 存在 repo 外部，备份和迁移工具必须把它作为 session repo 的组成部分。

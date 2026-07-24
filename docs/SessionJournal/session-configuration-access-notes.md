# SessionJournal Configuration Access Notes

> 状态：Design Note / Open Question
> 日期：2026-07-24
> 相关文档：[SessionJournal 主干设计基线](session-journal-trunk-design.md)、[ChatSession 事件源与长期上下文架构路线图](../ChatSession/event-sourced-session-architecture-roadmap.md)

## 1. 背景

`SessionJournal` 的配置事实目前由 `session-created` 与后续 `session-configuration-changed`
事件表达。已倾向拍板的一点是：`session-configuration-changed` 保存**完整有效配置快照**，
而不是一组 partial patch。这样 reducer 读取到配置事件时可以直接替换当前 config，避免字段新增、
默认值变化和 patch 链重放带来的复杂性。

仍未拍板的问题有两个：

1. 是否把 system prompt 变化拆成独立 `system-prompt-changed`。
2. 加载一个很长的 session 时，如何快速找到最近一次配置快照。

## 2. 使用场景假设

长期运行的 LLM Agent session 可能远大于一次 LLM context window。实际恢复时，常见路径不是全量读完
所有 raw events，而是从尾部读取一段 raw suffix，并结合 recap / artifact / retrieval 等派生产物构造
当前上下文。

如果配置长期稳定，最后一次 `session-configuration-changed` 与 journal head 之间可能隔着大量事件。
单纯从 head 沿 parent chain 反向扫描直到找到配置事件，正确但在冷启动时可能过慢。

## 3. System Prompt 是否独立成事件

暂不定稿。

保持统一 `session-configuration-changed` 的优势：

- model id、completion surface、system prompt、schema/profile 等共同组成 completion request 的有效配置。
- 每次配置变更写完整快照，reducer 和审计语义简单。
- 不需要为 system prompt 建第二套事件语义和恢复规则。

拆出 `system-prompt-changed` 的潜在理由：

- system prompt 里可能包含 Agent 自己可编辑的核心 belief / self policy，变更频率和治理方式可能不同于
  model id 或 completion surface。
- 将来如果需要对核心 belief 做独立 lineage、权限、审阅或冲突处理，单独事件可能更清晰。

当前建议：在 importer / CS-2 阶段先实现统一 `session-configuration-changed`。若后续确认 system prompt
中的可自编辑区域需要独立治理，再以新的 EventKind 或上层 artifact/config partition 重新收口。

## 4. 快速找到最近配置的候选方案

### 4.1 反向扫描 parent chain

从当前 head 开始读 header，遇到 `session-configuration-changed` 或 `session-created` 即停止。

优点：

- 不需要额外文件或格式。
- cache 损坏问题不存在。
- 适合作为永远可用的 fallback。

缺点：

- 配置长期稳定时，冷启动可能需要扫描大量 frame。
- 只解决 latest config，不解决后续 projection 的其他快速入口。

### 4.2 可重建 SessionJournal projection cache

在 journal repo 内维护一个可丢弃 cache，例如：

```text
cache/session-projection/main.json
```

记录：

```json
{
  "schema": "atelia.session-journal.projection-cache.v1",
  "branch": "main",
  "head": "<EventAddress>",
  "latestConfigurationEvent": "<EventAddress>",
  "eventCount": 123456
}
```

加载时：

1. 读取 branch head。
2. 若 cache head 等于当前 head，直接读取 `latestConfigurationEvent` payload。
3. 若 cache 缺失或不匹配，fallback 到反向扫描或 forward replay 重建，并重写 cache。
4. 后续可做 tail merge：若 cache head 是当前 head 的祖先，只扫描 cache head 到当前 head 的新增尾部。

优点：

- 正常 reopen 可 O(1) 找到最新完整 config。
- cache 可删除、可重建，不改变 raw event 正确性。
- 不污染 EventFrame wire format，也不要求每个 raw event 冗余携带 config pointer。

缺点：

- 需要 cache invalidation 与 branch/head 校验。
- 第一版若不做 tail merge，cache miss 仍可能退回较慢路径。

### 4.3 在 recap / artifact metadata 上记录 config address

recap 或 artifact 生成时记录当时 as-of 的 `session-configuration-changed` / `session-created`
地址。Context planner 选择 artifact anchor 时，可同时拿到该 artifact 对应的 config snapshot。

优点：

- 很适合回答“这个 recap 是在什么配置下生成的”。
- 适合 request manifest / artifact provenance，避免用今天的 system prompt 解释昨天生成的 artifact。

缺点：

- 不适合作为 SessionJournal load 的唯一入口。artifact 可能尚未生成、生成失败、被禁用，或落后于 raw tail 很远。
- raw journal 的恢复正确性不应依赖 derived artifact。

### 4.4 EventJournal 通用 nearest-kind sparse index

在 EventJournal 或 SessionJournal 层维护 “head -> nearest ancestor of kind X” 的稀疏索引。

优点：

- 可推广到其他快速查询，例如最近 checkpoint、最近 artifact set、最近 special control event。
- 比单一 config cache 更通用。

缺点：

- 当前需求还小，过早下沉到 EventJournal 可能增加通用接口复杂性。
- 需要定义索引文件格式、失效规则和 branch/rewind 语义。

## 5. 当前倾向

短期推荐：

- `session-configuration-changed` 保存完整配置快照。
- 正确性 fallback 使用 parent chain 反向扫描或 full replay。
- 若冷启动性能成为问题，优先在 SessionJournal 层加可重建 projection cache，记录
  `latestConfigurationEvent`。
- recap / artifact metadata 可以记录 config address，但只作为 context/artifact provenance 与 planner 优化，
  不作为 raw journal 恢复的必要依赖。

长期可再评估：

- system prompt 是否需要独立事件，取决于 Agent 自编辑核心 belief 的频率、治理方式和审计需求。
- nearest-kind sparse index 是否值得抽成 EventJournal 通用能力，取决于类似查询是否反复出现。

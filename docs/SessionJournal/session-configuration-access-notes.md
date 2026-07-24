# SessionJournal Configuration Access Notes

> 状态：Design Note / Decision Updated
> 日期：2026-07-24
> 相关文档：[SessionJournal 主干设计基线](session-journal-trunk-design.md)、[ChatSession 事件源与长期上下文架构路线图](../ChatSession/event-sourced-session-architecture-roadmap.md)

## 1. 背景

`SessionJournal` 已将 runtime config 与 system prompt 分离：

- `runtime-config-setup` 保存完整 runtime config snapshot：model id、completion surface、schema 等运行时配置。
- `system-prompt-setup` 保存完整 system prompt snapshot。system prompt 是上下文事实，是 Agent 隐状态的一部分，不再混入 runtime config。
- `session-created` 只作为初始化完成 marker，空 body。初始化顺序为 `runtime-config-setup` -> `system-prompt-setup` -> `session-created`。

仍需长期观察的问题：加载一个很长的 session 时，如何快速找到最近一次 runtime config 与 system prompt snapshot。

## 2. 使用场景假设

长期运行的 LLM Agent session 可能远大于一次 LLM context window。实际恢复时，常见路径不是全量读完
所有 raw events，而是从尾部读取一段 raw suffix，并结合 recap / artifact / retrieval 等派生产物构造
当前上下文。

如果 runtime config 或 system prompt 长期稳定，最后一次 setup event 与 journal head 之间可能隔着大量事件。
单纯从 head 沿 parent chain 反向扫描直到找到两个 setup event，正确但在冷启动时可能过慢。

## 3. System Prompt 独立事件决议

已选择拆出 `system-prompt-setup`，原因是 system prompt 更接近 context fact，而不是 runtime config。
它可能包含 Agent 自己可编辑的核心 belief / self policy，后续治理、审阅、provenance、压缩和上下文规划
都可能与 model id / completion surface 不同。

`system-prompt-setup` 不是 diff，也不叫 `system-prompt-changed`。它表达“从此位置起生效的完整 system
prompt snapshot”，既覆盖初始化，也覆盖后续重设。

## 4. Tail Projection 与 Recap Anchor

下一步 tail-only projection 不应优先围绕固定 turn 边界做复杂设计。长期目标里，rolling summary /
recap、自传和 world understanding 都是可重建的 derived artifacts；它们会成为 raw suffix 的自然
anchor。恢复或构造请求时，Planner 优先选择一个靠近 head 的 recap/artifact anchor，把 anchor 之前的
历史 materialize 为 `ContextHeader` 形态的 observation header，并可选附带 action header；随后只读取
anchor 之后的 raw suffix。

在 recap artifact 尚未落地时，可以保留朴素 raw suffix fallback 用于 bootstrap、小历史或审计，但它不应
被打磨成长期主要机制。对于 autonomous / role-play Agent，执行历史可能长期处在连续 tool-loop 中，
并不总是自然分成传统 user turn；边界设计应先满足局部依赖闭合，再由 recap anchor 承担长程连续性。

## 5. 快速找到最近配置的候选方案

### 5.1 反向扫描 parent chain

从当前 head 开始读 header，直到找到最近的 `runtime-config-setup` 与最近的 `system-prompt-setup`。

优点：

- 不需要额外文件或格式。
- cache 损坏问题不存在。
- 适合作为永远可用的 fallback。

缺点：

- 配置长期稳定时，冷启动可能需要扫描大量 frame。
- 需要找两个 latest setup event，不解决后续 projection 的其他快速入口。

### 5.2 可重建 SessionJournal projection cache

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
  "latestRuntimeConfigSetup": "<EventAddress>",
  "latestSystemPromptSetup": "<EventAddress>",
  "eventCount": 123456
}
```

加载时：

1. 读取 branch head。
2. 若 cache head 等于当前 head，直接读取两个 latest setup event payload。
3. 若 cache 缺失或不匹配，fallback 到反向扫描或 forward replay 重建，并重写 cache。
4. 后续可做 tail merge：若 cache head 是当前 head 的祖先，只扫描 cache head 到当前 head 的新增尾部。

优点：

- 正常 reopen 可 O(1) 找到最新 runtime config 与 system prompt。
- cache 可删除、可重建，不改变 raw event 正确性。
- 不污染 EventFrame wire format，也不要求每个 raw event 冗余携带 config pointer。

缺点：

- 需要 cache invalidation 与 branch/head 校验。
- 第一版若不做 tail merge，cache miss 仍可能退回较慢路径。

### 5.3 在 recap / artifact metadata 上记录 config address

recap 或 artifact 生成时记录当时 as-of 的 `runtime-config-setup` 与 `system-prompt-setup` 地址。
Context planner 选择 artifact anchor 时，可同时拿到该 artifact 对应的 runtime config 与 prompt snapshot。

优点：

- 很适合回答“这个 recap 是在什么 runtime config / system prompt 下生成的”。
- 适合 request manifest / artifact provenance，避免用今天的 system prompt 解释昨天生成的 artifact。

缺点：

- 不适合作为 governing setup resolver 的唯一入口。artifact 可能尚未生成、生成失败、被禁用，或落后于 raw tail 很远。
- raw journal 的恢复正确性不应依赖 derived artifact。

### 5.4 EventJournal 通用 nearest-kind sparse index

在 EventJournal 或 SessionJournal 层维护 “head -> nearest ancestor of kind X” 的稀疏索引。

优点：

- 可推广到其他快速查询，例如最近 checkpoint、最近 artifact set、最近 special control event。
- 比单一 config cache 更通用。

缺点：

- 当前需求还小，过早下沉到 EventJournal 可能增加通用接口复杂性。
- 需要定义索引文件格式、失效规则和 branch/rewind 语义。

## 6. 当前倾向

短期推荐：

- `runtime-config-setup` 与 `system-prompt-setup` 都保存完整 snapshot。
- 已在 SessionJournal 层提供无持久 hint 的 governing setup resolver：给定 `head`，沿 parent chain
  只读 EventJournal header preview，直到拿到最近的 `runtime-config-setup` 与 `system-prompt-setup`
  地址；随后只读取这两个 setup event 的 payload 并解码。缺少任一 setup 时 fail-fast。
- 这个 resolver 是当前正确性基线。它只信当前 `head` 的 Parent 链，不新增 raw event 字段、
  不新增 cache 文件、不改变 wire format。
- 若冷启动性能成为问题，优先给该 resolver 增加可重建 near-head hint。trunk 阶段 hint 可来自
  SessionJournal projection cache，记录 `validForHead`、`latestRuntimeConfigSetup` 与
  `latestSystemPromptSetup`；CS-3 后 hint 可来自 `ContextPlan` / request manifest 的引用式
  provenance；CS-5 后也可来自 artifact anchor / fold checkpoint。
- hint 只是 parent-chain resolver 的提前退出票，不是权威源。hint 缺失、过期或被删除时必须退化为
  纯 parent-chain 回溯；raw journal 恢复正确性不依赖 derived cache / artifact / manifest。
- tail-only projection 的优先前置是建立 recap 类 derived artifact store。这样切分点可以来自真实
  recap anchor，而不是临时硬造 turn window；没有 anchor 时只保留朴素 raw suffix fallback。

长期可再评估：

- nearest-kind sparse index 是否值得抽成 EventJournal 通用能力，取决于类似查询是否反复出现。

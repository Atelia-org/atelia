# Legacy ChatSession Export / SessionJournal CLI 拆分

> 状态：Implemented
> 日期：2026-07-27

## 结论

原 `prototypes/ChatSession.BacktestCli` 同时依赖旧 `ChatSession`、新
`SessionJournal`、concrete maintainers 和 Completion provider，已经成为新旧架构之间的
隐式耦合点。现已拆成两个 composition root：

```text
legacy ChatSession repo
  -> ChatSession.LegacyExportCli
  -> atelia.chat-session.legacy-upgrade-export.v1 JSON
  -> SessionJournal.Cli
  -> current SessionJournal raw repo
  -> addressed ReplayHistory
  -> MemoryMaintainer + derived artifact
```

## 项目边界

### `ChatSession.LegacyExportCli`

- 只依赖 `ChatSession`。
- `export-json` 导出版本化升级 JSON。
- `export-markdown` 导出供人工/LLM 阅读的 transcript。
- 不导入 SessionJournal，不加载 Completion，不运行 maintainer。

旧 storage 的读取与 DTO 生成仍由 `ChatSessionLegacyUpgradeExporter` /
`ChatSessionLegacyUpgradeMarkdownExporter` 完成，因为它们直接理解旧 storage schema；
CLI 只负责参数、路径安全和 atomic publish。

### `SessionJournal.Cli`

- 依赖 `SessionJournal`、`SessionJournal.Maintainers` 与 `Completion`。
- 产品引用图中没有 `ChatSession`。
- `import-legacy-json` 通过 CLI 自有 anti-corruption DTO 读取交换 JSON。
- `run-memory-maintainer` 使用 `SessionJournalEngine.ReplayHistory()` 的 addressed
  provenance、`MemoryMaintenanceOrchestrator` 与
  `SessionJournal.DerivedMemory.DerivedRecapStore`。
- `validate`、`llm-smoke` 继续作为 SessionJournal 离线开发支持；历史 raw
  `checkpoint-artifact-set` 已在 DM-3B 删除，后续 derived-only publish/list 属于 DM-3C。

升级 JSON 是两个产品程序集之间唯一的正式边界。DTO 不放进 SessionJournal raw core，
也不建立共享 legacy-contract assembly；producer/consumer 的兼容性由 schema id 和
端到端测试锁定。

## 被退役的实验入口

以下原 BacktestCli 命令不再保留：

- `inspect`
- `replay-pattern-count`
- legacy `replay-rolling-summary`

它们已经完成 early replay/substrate 验证，但继续保留会让新 CLI 反向依赖旧 ChatSession
event source 和 split policy。历史设计与实现可从拆分前 commit 查阅。

## 有意的 identity 切换

新的 maintainer runner 使用：

- command：`run-memory-maintainer`
- record schema：`atelia.session-journal.memory-maintainer-run.v1`
- producer：`SessionJournal.Cli/run-memory-maintainer`
- producer fingerprint schema：
  `atelia.session-journal.memory-maintainer-producer-fingerprint.v1`

这是一轮有意的开发工具 identity 切换。旧 BacktestCli sidecar artifact 不会被当作新
producer 的可续写 lineage；需要重跑时只删除精确的
`derived/recaps/v1/`，不改 SessionJournal raw events。

## 安全与失败语义

- legacy export 的 input/output，以及 SessionJournal import/validate/runner 的 repo、
  report、JSONL 与 call-log 路径链均拒绝 symlink/reparse point。
- 输出报告不会覆盖 migration input，并位于目标 repo 外；报告使用同目录临时文件
  atomic publish。
- `import-legacy-json --force` 先完整预检 event/message kind、ordinal、initial setup
  和 action block decode，再在目标同级 staging repository 完整导入并 reopen 验证；
  只有成功后才替换目标，发布失败时尝试恢复旧目录。
- `revert-turn` 等未设计映射的事件 fail fast，不会静默丢失，也不会先删目标。
- legacy tool-call/tool-results 同样 fail fast；它们需要专门映射 SessionJournal 的
  execution/correlation/checkpoint raw facts，不能只复制 transcript。
- 当前 importer 明确跳过 legacy compaction/recap，因为它们是可重建 derived 信息。
- raw SessionJournal events 仍是 correctness source。

## 后续自然入口

当前 `run-memory-maintainer` 是单 profile、full replay、runner-local synthetic
sliding-prefix 的开发工具。shared epoch planner、multi-maintainer provisioning、
candidate selection 和 coherent publication 仍属于未来 `DerivedMemory` 子系统，不应继续
扩张进 SessionJournal raw core。

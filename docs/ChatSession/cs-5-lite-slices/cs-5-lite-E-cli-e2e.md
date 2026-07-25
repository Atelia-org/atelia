# CS-5-lite-E: CLI 与端到端验收

> 状态：Implemented / CS-5-lite Complete
> 日期：2026-07-26
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 1. 目标

收口 `ChatSession.BacktestCli` 的命令面、README 和端到端验收，使 A–D 已形成的 addressed replay、
Derived Recap Store 与 artifact writer 可以通过正式 CLI 直接运行。

E 不重新定义 artifact schema、anchor、lineage、fingerprint 或两阶段提交语义。

## 2. 命令边界

采用独立命令：

```text
replay-rolling-summary                 legacy export JSON；无 artifact 副作用
replay-rolling-summary-session-journal SessionJournal repo；写 Derived Recap Artifact
```

不让 `--input` 自动探测 JSON/目录，也不移除 legacy 命令。新命令显式组装：

```text
SessionJournalRollingSummaryReplaySource
+ SessionJournalDerivedRecapWriter
+ RollingSummaryReplayRunner
```

source 与 writer 必须指向同一 repo。SessionJournal source 单独使用时仍保持无副作用；是否写 artifact
由 composition root 决定。

正式命令示例：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/session-journal-rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/session-journal-rolling-summary-calls \
  --max-epochs 1 \
  --preset autobiographical-rewrite
```

## 3. 可测试 composition root

`Program.Main` 使用真实 `DefaultCompletionClientFactory`；内部 `MainCore` 接受
`ICompletionClientFactory`。注入点位于 connection registry 的 factory 边界，而不是绕过 registry
直接注入 client，因此自动测试仍覆盖：

```text
CLI command/options
-> connections file loader
-> CompletionConnectionRegistry
-> replay source/runner/writer composition
-> Completion call log
-> DerivedRecapStore
-> replay JSONL
```

`scripted` completion 只存在于测试 factory，不增加生产 provider kind。所有创建 registry 的 CLI 路径
都使用同一个显式 factory，避免测试入口和真实入口分叉。

runner 的 call-log command 由 composition root 显式传入：

- legacy：`replay-rolling-summary`
- SessionJournal：`replay-rolling-summary-session-journal`

不能从 source kind 临时推导，否则命令诊断信息会与真实入口脱节。

JSONL 使用输出目录内的唯一临时文件流式写入，只在 replay 正常结束并关闭文件后替换最终路径。这样
existing-lineage preflight、输入错误或 cancellation 不会先截断已有报告；runner 正常产生 failed
record 时仍会发布该诊断 JSONL 并返回 exit code 1。

SessionJournal 命令还会在读取 prompt/connection、打开 source 或创建输出目录以前，拒绝把
`--output` / `--call-log-dir` 放在输入 repo 内。CLI 自有报告和调用日志必须位于 repo 外；
repo 内只有 writer 管理的 `derived/recaps/v1/` 可变。为避免 lexical containment 被 alias 绕过，
`--input`、`--output`、`--call-log-dir` 的现有路径链也不能包含 symlink/reparse point。

## 4. Bootstrap 与重新生成

当前 runner 从 raw history 起点和空 `MemoryPack` 开始 full replay。artifact-producing 命令要求目标
`profile + target` lineage 为空：

- 已有 latest 时在首次 LLM 调用前 fail-fast。
- 不把 existing latest 直接接成 `previousArtifact`，避免重复吸收 raw prefix。
- 不自动删除 derived store。
- 不实现从 latest materialize 后的 tail-only continuation。

需要重新生成时，只删除目标 repo 的：

```text
derived/recaps/v1/
```

保留 raw repo，再重新运行 full replay。artifact identity 会受到实际 call-log provenance 影响，验收只要求
产物可重新生成和重新加载，不要求新旧 `artifactId` 相同。

不同 profile/target 使用不同 lineage，因此 `autobiographical-rewrite` 与
`world-understanding-rewrite` 可以在同一空 derived store 上各运行一次；第二条 lineage 的 root
`previousArtifact` 仍为 null。

## 5. 自动端到端验收

自动 E2E 不访问网络：

1. 在临时目录写最小 legacy export。
2. 通过实际 `import-session-journal` CLI 导入 repo。
3. 记录 raw head 与 addressed history snapshot。
4. 通过测试 connections JSON 和 injected scripted factory 调用新命令。
5. 验证 JSONL 的 SessionJournal source range、成功状态、artifact link 与 call-log link。
6. reopen `DerivedRecapStore`，验证 artifact provenance、target、invocation 和内容。
7. 解析 call log，验证 `context.command` 是新命令。
8. 验证 replay 前后 raw head/history 不变。
9. 不删除 derived 直接重跑，验证在 completion 前拒绝。
10. 删除准确的 `derived/recaps/v1/` 后，用新输出路径重跑并重新加载 artifact。

## 6. 真实 LLM 验收

真实回测不进入自动测试。使用一个全新 imported repo 和 `dsv4p`，两个 preset 各运行
`--max-epochs 1`，并使用互不重叠的 JSONL/call-log 目录：

```text
gitignore/backtest/cs5-lite-e-real/autobiographical/
gitignore/backtest/cs5-lite-e-real/world-understanding/
```

每条命令至少核验：

- exit code 为 0，JSONL 恰有一条 succeeded record。
- `artifactPath` 与 `callLogPaths` 指向实际文件。
- `anchorRawEvent == sourceEndInclusive`。
- call log 的 command、maintainer 与 target 正确。
- 两条 latest lineage 同时存在，且各自 root 的 `previousArtifact` 为 null。
- 运行前后 raw `SourceRawHead` 不变。

call log 包含完整 prompt、history 和模型输出，只能保存在 `gitignore/` 下，不进入源码提交。

## 7. 非目标

- 不移除或自动复用 legacy 命令。
- 不实现 Context Planner、tail-only projection 或 existing-artifact continuation。
- 不加入自动清理 derived store 的危险参数。
- 不新增生产 `scripted` provider。
- 不复活已从实现中移除的 `rolling-summary` preset 或 `--target-*` 参数；当前正式 preset 是
  `autobiographical-rewrite` 与 `world-understanding-rewrite`。

## 8. 验收结果

自动测试：

- `ChatSession.BacktestCli.Tests`：27/27 通过，skipped 0。
- 覆盖 `llm-smoke`、legacy rolling 和 SessionJournal rolling 三条 LLM 命令的 factory 注入与准确
  call-log command。
- 同 output 的 existing-lineage 重跑返回 1、completion count 不增加，原 JSONL byte-for-byte
  保持不变。
- repo 内 raw `.rbf` output alias 与 repo 内 call-log directory 在任何写入前拒绝。
- repo symlink、指回 raw 目录的 output parent symlink 和 call-log symlink 同样在写入前拒绝。
- malformed connections JSON 转换为 CLI exit code 1，不从 `MainCore` 逃逸。

真实 `dsv4p` 验收使用：

```text
repo: gitignore/session-journal/cyber-copy-cs5-lite-e-20260726
autobiographical: gitignore/backtest/cs5-lite-e-real/autobiographical/
world understanding: gitignore/backtest/cs5-lite-e-real/world-understanding/
```

结果：

- legacy fixture 导入 71 条 observation、71 条 action；跳过 2 条 compaction 和 2 条 recap。
- 两个 preset 均以 `--threshold-tokens 24000 --max-epochs 1` 成功产生一条 JSONL、一个 call log
  和一个 artifact。
- autobiographical target 为 `action/roleplay.first-person-autobiography`；
  world-understanding target 为 `observation/roleplay.world-understanding`。
- 两个 artifact 均为各自 lineage 的 root，`previousArtifact == null`，且
  `anchorRawEvent == sourceEndInclusive`。
- latest index 同时包含两条 lineage。
- 运行前后的 raw `events/` 与 `refs/` 文件 SHA-256 完全一致；两个 record 的
  `sourceRawHead` 都是 `ej1:00000463f40004120000000100000000`。
- 在真实 repo 上用相同 autobiographical output 重跑会在 completion 前拒绝，旧 JSONL SHA-256
  `6458b44a…d0962` 保持不变，且未产生新 call log。

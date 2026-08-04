# CS-5-lite-D：LLM 结果写入 Derived Recap Artifact

> 状态：Implemented / Ready for E Handoff
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 1. 目标与结论

当 rolling summary maintainer 成功产生候选 `MemoryPack` 后，D 通过一个显式 artifact writer 边界把它写入
B 分片提供的 `DerivedRecapStore`，再把 artifact link 写入 replay JSONL。

实现采用两阶段提交：

```text
maintainer candidate
-> 收集 call logs
-> DerivedRecapStore.WriteProducedAsync
-> store 成功返回
-> 提交 runner MemoryPack / 移除 sliding prefix
-> 输出 succeeded replay record
```

artifact 写入成功以前，候选 `MemoryPack` 不成为 runner 当前状态，selected fragment 也不从 active history
移除。这样 replay record、artifact lineage 与下一 epoch 的输入不会出现“artifact 未落盘但 runner 已前进”的
半提交状态。

## 2. 实现边界

主要实现位于：

```text
prototypes/ChatSession.BacktestCli/
  RollingSummaryReplay.cs
  RollingSummaryArtifactWriting.cs
```

`IRollingSummaryArtifactWriter` 是 runner 的可选副作用边界：

- 未配置 writer 时，legacy 与 SessionJournal source 都可以继续做无 artifact 的纯 backtest/诊断。
- 配置 writer 时，其 `RequiredSourceKind` 必须与 replay source 一致。
- 当前 concrete writer `SessionJournalDerivedRecapWriter` 只接受
  `sourceKind == "session-journal"`。
- concrete SessionJournal source 与 writer 都携带 canonical repo path；runner 组装时要求两者指向同一
  repo，不能只凭相同 `SourceKind` 配对。
- E 分片的 artifact-producing CLI 组装必须显式创建 writer；source 类型本身不隐式产生副作用。

writer 在读取到第一个 replay step 后、首次 LLM 调用前执行 `PrepareAsync(sourceRawHead)`：

1. 用 `SessionJournalEngine.ResolveGoverningSetup(sourceRawHead)` 固定 governing runtime config/system
   prompt setup。
2. 计算目标 lineage：
   `rolling-summary + RewriteProfile.Id + RewriteProfile.Target`。
3. 要求这个 lineage 尚无 latest artifact。

每次写入还会在 derived store root 获取跨 writer instance/process 的 exclusive write lock，把“重读
latest + 写 artifact + rebuild latest index”放进同一临界区。两个从空 lineage 同时启动的 D writer
至多一个能提交 root artifact；另一个在锁内重读到已变化的 latest 后拒绝，不会静默形成双 root。

## 3. Provenance 与 lineage

只有同时具备下列条件的成功 candidate 才能写 artifact：

- source kind 是 `session-journal`。
- replay snapshot 有 `sourceRawHead`。
- selected fragment 有完整 `sourceStartInclusive` / `sourceEndInclusive`。
- maintainer 已成功返回 candidate `UpdatedMemoryPack`。

artifact 字段固定为：

- `sourceRawHead`：本次 replay snapshot 的 raw head。
- `sourceEndInclusive`：selected fragment 实际吸收的最后一个 raw event。
- `anchorRawEvent`：等于本 epoch 的 `sourceEndInclusive`。
- `sourceStartExclusive`：上一 artifact 的 `anchorRawEvent`；首 epoch 为 null。
- `previousArtifact`：本次 run 内上一成功 artifact；首 epoch 为 null。
- `inputArtifacts`：首 epoch为空；后续为 `[previousArtifact]`。
- governing setup：从 `sourceRawHead` resolve 一次，不从 trigger 或 fragment end 推断。

`sourceStartInclusive` 用于 runner 的 candidate 完整性检查和 replay 诊断，但 B 的 artifact 合同用上一 anchor
表达 `sourceStartExclusive`，两者不能互换。

### Full replay bootstrap policy

C 的 SessionJournal source 当前从 raw history 起点全量 replay，runner 也从空 `MemoryPack` 启动。因此 D
明确要求目标 lineage 在 run 开始时为空：

- 若已有 usable latest artifact，`PrepareAsync` 在首次 LLM 调用前 fail-fast。
- 不自动把 existing latest 设为 `previousArtifact`，否则会重复吸收旧 raw prefix。
- 同一次 run 的多个 epoch 可以形成 run-local lineage。
- 从 existing artifact materialize `MemoryPack` 并只 replay anchor 后 tail，属于后续工作，不在 D 内伪造。

## 4. Producer fingerprint

producer 固定为：

```text
ChatSession.BacktestCli/replay-rolling-summary-session-journal
```

`producerFingerprint` 使用 versioned、固定字段顺序的 canonical DTO 做 UTF-8 SHA-256，格式为
`sha256:<64 lowercase hex>`。

fingerprint 包含会改变 producer 变换语义的配置：

- producer/fingerprint schema version。
- addressed replay adapter、split policy、token estimator 版本。
- preset、profile id、target、完整 system/user prompt。
- completion kind、model、surface、resolved base address、max tokens。
- completion client provider id / API spec id。

它排除 secret、connection id、文件路径、call-log 路径、时间、随机值、`thresholdTokens` 和
`maxEpochs`。后两者决定何时调度 epoch，但最终 fragment range 已由 artifact provenance 明确，不属于对给定
fragment 的 producer 变换身份。

completion call log 由 `LoggingCompletionClient` 用 `FileMode.CreateNew` 原子保留唯一 numeric path；
runner 从该 client 实例读取实际成功写入的 paths，不再用共享目录的 before/after max-id 差值推断。

## 5. Replay record 与失败语义

replay record 新增 nullable 字段：

```text
artifactId
artifactPath
anchorRawEvent
previousArtifact
```

成功写入时这些字段链接到实际 artifact；legacy、maintainer failure 和 artifact write failure 均为 null。
`artifactPath` 使用绝对路径，与现有 `callLogPaths` 风格一致，但不参与 artifact identity。

失败分为两类：

1. maintainer failure：
   - 不调用 `WriteProducedAsync`。
   - 不写 produced artifact。
   - 不提交 runner 状态。
   - 输出 failed record 并停止 replay。
2. artifact operational failure：
   - concrete writer 把 `IOException` / `UnauthorizedAccessException` 包装成
     `RollingSummaryArtifactWriteException`。
   - runner 输出 failed record，保留候选 `NewBlock`、invocation、errors 与 call logs 供诊断。
   - artifact link 为 null，runner 状态不提交，随后停止。

用户 cancellation 原样传播。lineage 非空、source/provenance 不合法等前置条件错误 fail-fast，不伪装成
某个 epoch 的 failed record。

`DerivedRecapStore` 的原子边界仍是其已有的单 artifact 写入流程；D 不声称 artifact 文件、latest index、
runner 内存和 replay JSONL 之间存在跨文件事务。若 store 在 artifact 文件落盘后、latest index 更新期间发生
I/O 故障，runner 会停止且不前进；derived sidecar 可通过 B 的 rebuild/清理机制恢复。

## 6. 验收覆盖

受影响测试集覆盖：

- 单 epoch 成功写 artifact，并验证 record link、raw range、anchor、profile、target、candidate
  `MemoryPack`、invocation、governing setup 与 call logs。
- 多 epoch run-local lineage 的 `previousArtifact` / `sourceStartExclusive` / `inputArtifacts` 链。
- maintainer failure 不写 artifact、不移除 prefix。
- artifact operational failure 生成 failed record，不提交 prefix，保留候选诊断信息。
- existing target lineage 在首次 completion call 前拒绝。
- 两个 concrete writer 并发从空 lineage 写 root 时只允许一个提交。
- source repo 与 writer repo 错配在组装时拒绝。
- 多个 logging client 共享 call-log 目录时仍得到唯一文件和各自精确 path。
- legacy source 与 SessionJournal writer 的错误组装被拒绝。
- producer fingerprint 的稳定性、敏感字段与排除字段。
- 写 derived sidecar 前后 raw SessionJournal head/history 不变。

## 7. E handoff

E 可以直接组装：

```text
SessionJournalRollingSummaryReplaySource
+ SessionJournalDerivedRecapWriter
+ RollingSummaryReplayRunner
```

并新增正式 SessionJournal CLI 命令、参数帮助、JSONL/README 示例与真实 imported repo smoke。E 不应改变
D 已固定的 bootstrap、anchor、lineage 或两阶段提交语义；若要支持 existing latest，则必须同时实现
`MemoryPack` materialize 与 anchor 后 tail-only replay，而不是放宽空-lineage 检查。

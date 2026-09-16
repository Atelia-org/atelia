# SessionJournal 外部效果与恢复安全合同

状态：当前源码合同，随 Completion 自动重试重构更新；最终集成验收与部署状态见
[实施方案](../../../Galatea/completion-auto-retry-refactor-plan.md)。旧 `9860bc33` 基线记录的是
Started/Refuse 模型，不证明本次变更已验收。本文区分纯生成、真正工具效果与本地提交不确定。

## 纯生成：保留 Prepared，允许重新计算

新执行先提交 `CompletionRequestPrepared`，再在内存调用 Completion；完整结果校验通过后才提交
`AgentActionProduced` v2。不再写新的 `CompletionAttemptStarted` 或 `CompletionAttemptFailed`，
也不再要求 `Refuse` / `RestartWithNewAttempt` 策略。Prepared 与合法历史 Started 尾统一为
`AwaitingCompletion`，`FrozenCompletionRequired` 绑定原 source Prepared 与 runtime identity。

纯生成断流后可重算，但可能重复计算或计费，不保证远端 exactly-once。Core 不提供 provider
结果找回、跨进程 lease 或通用 retry loop；Host 决定退避、期限及错误分类。调用退出并完成资源
清理后才能开始下一次调用；不以共享 client 的全局连接状态判断，也不放弃旧调用后重叠补偿。
partial 结果不能作为成功 Action，工具只在完整 Action 已提交之后执行。

### 当前语义计划与旧 exact 请求

[Prepared v9](../contracts/completion-request-prepared-v9.md)冻结内容选择和执行边界，使用当前
projector 在内存生成请求。投影、限额或环境错误保留原 Prepared；重试不能重新接纳输入、
重新选择上下文或换连接。[v7/v8](../contracts/completion-request-prepared-v7.md)继续按原
commitment exact 重构；v5 仅验证，不开放执行。

历史 Started/Failed 原字节和地址不改写，Started 的版本、摘要及 parent 归属仍严格验证。
Action v1 只接 Started；新 v2 接 Prepared 或合法历史 Started 尾，不接 Failed。
旧 Failed 尾保持 blocked，不自动生成；完整验证来源与工具批次后可显式结束为 Stopped。

### 非成功结果与业务结束

`CompletionRequestRejectedException` 或非成功 `CompletionResult` 可由 Core 转成
`SessionJournalTurnAbortedException` 交给 Host 分类，但不持久化逐次失败。
环境/认证/协议错误保持 pending；它们不是“用户任务已经结束”的事实。
明确拒绝、完整 terminal 的输出上限及用户 Stop 可在安全边界调用
`EndPendingTurn(expectedHead, reason)`，追加 `TurnEnded(Rejected/Incomplete/Stopped)`。
历史 Failed 只能以 Stopped 显式收口。

TurnEnded 不回退输入或已提交工具事实。`AwaitingToolExecution` 不能结束，即使 head 已是一个
ToolResult，也须确认同一 Action 的整个工具批次已闭合。业务结束通过 typed history marker
进入 planning 与 closed-turn projection；Terminated 不等于 Completed(Action)。
服务 shutdown 不等于用户 Stop，不应把待恢复任务记为 Stopped。

## 本地提交不确定：poison 与 reopen

Action、TurnEnded 或其他 ref 发布路径异常时，物理 ref 可能已经发布，即使内存 cache 仍显示
旧 head。可能已发布的写入异常立即令 engine 进入 reopen-required；同一实例不可继续
repository-bound 读取、恢复或写入，dispose 仍允许。已物化且不再访问 repository 的快照不受影响。

Host 必须 dispose/reopen，由 selected lineage 裁决：若 Action 已提交，就不能再次生成该 Action；
若仍为 Prepared，才按 pending 恢复。孤立事件、日志和 global scan 不能提升为 selected authority。
明确的提交前 CAS 冲突只失效相关 head-bound 状态并重新读取，不声称发生网络失败或 ref 发布不确定。

## ToolExecutionStarted：独立的副作用恢复义务

`ToolExecutionStarted` 持久化 exact `SessionToolRuntimeIdentity`、`operationId` 与
`executionSequence`；恢复要求 runtime identity 匹配，并使用同一 operation id 和 reserved sequence。
Core 不创建第二个 Started reservation，也不把纯生成重试政策应用到工具。

这只提供稳定的去重/查询关联，不证明工具副作用 exactly-once。自动恢复的工具必须天然幂等，
或由 Host/tool backend 按 durable operationId 去重、查询既有结果。不可查询的非幂等未知效果
仍须阻断；`CapabilitySetFingerprint` 是身份绑定，不是结果证明。

## Current owners 与复核入口

| Concern | Owning code / focused tests |
|:--|:--|
| Prepared、Action 与 poison | [`SessionJournalEngine.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.cs)、[`SessionPreparedCompletionRecoveryEngineTests.cs`](../../../../tests/SessionJournal.Tests/SessionPreparedCompletionRecoveryEngineTests.cs) |
| 严格 lineage 与完整工具批次 | [`SessionExecutionTailResolver.cs`](../../../../prototypes/SessionJournal/SessionExecutionTailResolver.cs)、[`SessionExecutionTailResolverTests.cs`](../../../../tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs) |
| 业务结束 | [`SessionJournalEngine.TurnEnded.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.TurnEnded.cs)、[`SessionTurnEndContracts.cs`](../../../../prototypes/SessionJournal/SessionTurnEndContracts.cs) |
| 原计划 runtime 绑定 | [`SessionRuntimeRecoveryRequirements.cs`](../../../../prototypes/SessionJournal/SessionRuntimeRecoveryRequirements.cs)、[`SessionJournalEngine.RuntimeRecovery.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.RuntimeRecovery.cs) |

上游失败事实、transport 清理及 terminal 收口的源码基线为 atelia-completion `0847bf3`，
不是已发布 preview.1 的行为保证；包消费身份以仓内 `eng/CompletionDependency.props` 为准。
provider/tool result lookup、自动认证修复和通用 capability-aware 工具恢复不由本次重构提供。

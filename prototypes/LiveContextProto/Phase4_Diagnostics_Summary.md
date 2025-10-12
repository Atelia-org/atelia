# LiveContextProto Phase 4 诊断能力总结
*更新日期：2025-10-13*

> 本文记录 Phase 4（工具链与诊断）的落地结果、关键经验以及回流 MemoFileProto 主线时的迁移建议。

## 交付回顾
- Stub Provider 已扩展为默认只声明工具调用，通过 `ModelOutputDelta` 将工具计划与 TokenUsage 一并回传。
- `ModelOutputAccumulator` 聚合 delta 后，为 `ModelOutputEntry`/`ToolResultsEntry` 注入 `token_usage` 元数据，保持 Provider → AgentState 的闭环。
- 新版 `ToolExecutor` 统一处理工具调用（成功/失败/取消），输出 `ToolExecutionRecord`，并使用 `ToolResultMetadataHelper` 汇总耗时、失败计数与 per-call 元数据。
- 控制台循环现支持直接查看 Metadata：`/history` 会展示各消息的摘要，实时调用结果也会打印耗时与诊断字典，便于调试。
- 新增文档化总结（本文）与路线图更新，使 Phase 4 的验收标准、经验和下一步动作均可追踪。

## 关键经验
1. **Metadata 统一入口**：所有诊断数据均挂载在 `ImmutableDictionary<string, object?>`，避免引入额外结构；在 UI 层（Console）统一做格式化输出即可复用。
2. **工具流水的幂等回写**：如果 Provider 未提供 `ToolResultsEntry`，Orchestrator 会触发 `ToolExecutor` 并再次通过 `ToolResultMetadataHelper` 写入统计信息，历史层只看到一条标准化的工具结果。
3. **DebugUtil 类别划分**：`Provider` 类别负责路由、聚合日志；`Tools` 类别记录执行情况；`History` 类别追踪追加式写入。通过 `ATELIA_DEBUG_CATEGORIES=History,Provider,Tools` 可以完整复现一次调用。
4. **控制台诊断展示**：为调试高效，需要在消息渲染时跳过 `token_usage` 重复打印，同时支持字典/列表递归展开，便于观察 per-call 细节。

## MemoFileProto 迁移建议
- **AgentState 差异对比**：
  - 引入 `ToolResultMetadataHelper` 汇总逻辑，确保工具执行的统计数据不在上层手写。
  - 在 MemoFileProto 的历史渲染中加入 Metadata 打印或导出接口，保持调试体验一致。
- **Orchestrator 回写路径**：
  - 拆分 Provider 聚合与工具执行逻辑，使用与原型一致的 `ModelInvocationAggregate` 结果进行回写。
  - 复用 `ToolExecutor`，并将现有工具处理器注册成 `IToolHandler` 实现，便于统一耗时统计。
- **控制台/日志集成**：
  - 若主线有 CLI 或调试入口，可移植本次的 Metadata 打印 helper，保证诊断信息在文本界面也可用。
  - 建议将 DebugUtil 类别与 LiveContextProto 对齐，以便跨项目共享日志过滤配置。
- **Checklist（建议顺序）**：
  1. 为 MemoFileProto 引入 `ToolExecutionRecord` 与 `ToolResultMetadataHelper`；
  2. 调整 Orchestrator，确保 Provider delta → ModelOutputEntry → ToolResultsEntry 的流程一致；
  3. 为历史视图/CLI 添加 Metadata 展示，验证样例包括成功/失败/取消三种工具结果；
  4. 更新项目文档，明确新的调试方式与环境变量开关。

## 后续补充
- 持续扩充 Stub JSON 脚本，覆盖多工具并行、长延迟、执行异常等场景，验证 Metadata 格式在复杂情况下的稳定性。
- 结合真实 Provider（OpenAI/Anthropic）评估工具声明与执行配对是否需要额外字段，例如 TraceId/InvocationId。
- 观察 Metadata 的体量增长，必要时考虑添加裁剪或分层序列化策略，确保历史条目可安全持久化。

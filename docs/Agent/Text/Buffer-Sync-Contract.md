# Buffer ↔ Sync Contract (2025 战略版)

> **文档目的**：定义 `TextEditor2Widget`（独占缓存）与 `DataSourceBindingWidget`（同步组件）之间的统一契约，包括版本号、事件负载、操作结果以及异步票据管理，作为实现双 Pass 调度的基础。
> **适用范围**：LiveContextProto 体系内所有同时使用编辑与同步 Widget 的应用；历史组件可参考“迁移指引”部分逐步对齐。

> **兼容性说明**：该契约面向即将实现的统一版本，当前不存在历史接口或指纹方案需要兼容，所有实现以本文规范为准。

---

## 1. 契约概览

| 项目 | 约定 | 备注 |
| --- | --- | --- |
| 版本号 | 单调递增字符串化整数（如 `"42"`） | 替代 Phase 1 的指纹哈希,所有实现统一采用版本号 |
| 事件触发 | 编辑成功或下层变更后同步触发 `ContentChanged` | 禁止跨线程回调，除非显式调度 |
| 操作结果 | 统一使用 `OperationResult<T>` / `OperationResult` | 封装成功、错误码、文案与诊断提示 |
| 异步操作 | 通过 `OperationTicket` 跟踪 Flush / Refresh 等长耗时任务 | `UpdateAsyncOperationStatusAsync` 负责回收 |
| 工具矩阵 | 共享 `ToolMatrix<TState, TFlag>` 生成可见工具、默认 Guidance | 编辑 / 同步 Widget 共用基础设施 |

---

## 2. 版本号统一策略

### 2.1 接口规范

| 角色 | 接口 | 说明 |
| --- | --- | --- |
| 缓存 | `string GetVersion()` | 返回当前缓冲区版本，供同步组件判定是否存在未提交修改 |
| 数据源 | `ValueTask<string> GetVersionAsync(CancellationToken)` | 返回下层内容版本，供同步组件检测外部变更 |
| 事件 | `BufferChangedEventArgs.Version` / `DataSourceChangedEventArgs.Version` | 必填字段，统一传递字符串化整数版本号 |

> **初始化**：建议以 `"0"` 作为初始版本；每次成功写入或刷新后立即递增（例如 `int.Parse(version) + 1`）。
>

### 2.2 选区与缓存联动

- `TextEditor2Widget` 在多匹配时记录 `SelectionState.BufferVersion`，仅当版本一致时允许 `_replace_selection`。
- 任何 `TryUpdateContentAsync`（含刷新、接受下层变更）成功执行后必须递增 `BufferVersion` 并清空旧选区。
- 缓存内部可以额外保存 `SnapshotChecksum` 以用于诊断，但在逻辑判断上以版本号为准。

---

## 3. 事件负载合同

### 3.1 BufferChangedEventArgs

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `Version` | `string` | ✅ | 改动后的缓冲区版本号 |
| `TimestampUtc` | `DateTimeOffset` | ✅ | 事件产生时间（UTC） |
| `Delta` | `int` | ⭕ | 文本长度差，便于同步组件渲染指标 |
| `SelectionCount` | `int?` | ⭕ | 多选区数量，用于提示 `_replace_selection` |
| `OperationId` | `string?` | ⭕ | 工具执行标识，与日志串联 |

### 3.2 DataSourceChangedEventArgs

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `Version` | `string` | ✅ | 下层数据源最新版本号 |
| `TimestampUtc` | `DateTimeOffset` | ✅ | 事件产生时间（UTC） |
| `Source` | `string?` | ⭕ | 变更来源（文件监控、外部 API 等） |
| `DeltaSummary` | `string?` | ⭕ | 变化量摘要（如 `"+128/-64"`） |

> **触发顺序**：
> 1. 缓存更新 → TextEditor2Widget 内部更新版本号 → 触发 `ContentChanged`；
> 2. 下层写入成功 → 数据源实现更新版本号 → 触发 `ContentChanged`；
> 3. 事件必须在同一线程同步触发，避免 orchestrator 监听时遭遇竞态。
>
> **去抖动建议**：频繁外部变更可在数据源层进行 100–200ms 防抖，确保 `UpdateAsync` 每帧最多处理一次。

---

## 4. OperationResult 契约

### 4.1 结构定义

```csharp
public sealed record OperationResult<T>(
    bool Success,
    string? ErrorCode,
    string? Message,
    string? DiagnosticHint,
    T? Payload);

public sealed record OperationResult(
    bool Success,
    string? ErrorCode,
    string? Message,
    string? DiagnosticHint);
```

| 字段 | 说明 |
| --- | --- |
| `Success` | 操作是否成功 |
| `ErrorCode` | 失败原因的枚举值（如 `BufferRejected`, `SourceReadOnly`, `ConflictDetected`, `IOException`） |
| `Message` | 面向 LLM 的简短说明，建议使用简洁陈述句 |
| `DiagnosticHint` | 面向调试的补充信息，可指向日志分类或下一步操作 |
| `Payload` | 成功路径下返回的数据（如写入结果统计、差异信息等） |

### 4.2 使用约定

- `IExclusiveBuffer.TryUpdateContentAsync` 返回 `OperationResult<bool>`，`Payload` 为是否应用成功；失败时 `ErrorCode` 必填。
- `IDataSource.WriteAsync` 返回 `OperationResult<WriteReceipt>`（示例类型），包含写入字节数、耗时等信息。
- Flush / Refresh 工具根据 `OperationResult` 生成 `LodToolExecuteResult`，并在 Guidance 中引用标准文案（参见 ToolMatrix）。
- 所有失败都必须在 `DebugUtil` 中记录对应分类日志，并附带 `OperationId`。

---

## 5. 异步操作票据（OperationTicket）

### 5.1 数据结构

```csharp
public sealed record OperationTicket(
    string OperationId,
    Task UnderlyingTask,
    DateTimeOffset StartedAt,
    TimeSpan Timeout,
    string OperationName);
```

### 5.2 使用流程

1. Flush / Refresh 工具在发起异步写入/读取时创建 `OperationTicket`，存储于 Widget。
2. 立即更新状态机：`SyncState = Flushing/Refreshing`，附加 `SyncFlag.FlushInProgress` 或 `SyncFlag.RefreshInProgress`。
3. `UpdateAsyncOperationStatusAsync` 在每次 Update pass 中检查 `UnderlyingTask`：
   - 已完成且成功 → 清除票据、递增版本号、重置 Flags；
   - 已完成但失败 → 清除票据、`SyncState = Failed`，将 `ErrorCode` 注入下一帧 Guidance；
   - 超时（`StartedAt + Timeout < now`）→ 取消或标记失败，输出 `DiagnosticHint` 提示检查日志。
4. `OperationTicket` 清除后，Widget 根据最新状态刷新工具可见性。

> **默认超时**：建议 30 秒，可通过构造参数或配置覆盖。
>
> **扩展**：如需要更复杂的状态跟踪，可为 `OperationTicket` 添加 `CancellationTokenSource` 与进度报告接口。

---

## 6. ToolMatrix 基础设施

### 6.1 抽象基类

```csharp
public abstract class ToolMatrix<TState, TFlag>
    where TState : Enum
    where TFlag : Enum
{
    public abstract ToolMatrixEntry Resolve(TState state, TFlag flags);
}

public sealed record ToolMatrixEntry(
    IReadOnlyList<ITool> VisibleTools,
    string? DefaultGuidance,
    IReadOnlyList<string>? AdditionalHints);
```

- `TextEditToolMatrix` 与 `SyncToolMatrix` 均继承基类，维护 `Dictionary<(TState, TFlagMask), ToolMatrixEntry>`。
- `Resolve` 的结果为不可变集合，防止外部修改。
- Orchestrator 在合并工具集合前使用 `ToolMatrix.ValidateUniqueNames()` 检查命名冲突。

### 6.2 Guidance 模板

- 通过 `ErrorCode` 映射到 Guidance 模板（如 `BufferRejected → "调用 _refresh(confirm=true) 或重新匹配"`）。
- ToolMatrix 可提供 `DefaultGuidance`，当工具返回 `OperationResult.Success` 时使用；失败时由工具层根据 `ErrorCode` 覆盖。

---

## 7. 迁移与实施 checklist

- [ ] 更新两个 Widget 的接口实现，添加 `GetVersion()` / `GetVersionAsync()`。
- [ ] 事件参数中补齐 `Version`、`TimestampUtc` 字段,并按需提供差异摘要。
- [ ] 引入 `OperationResult` 与 `OperationTicket` 基础设施，替换布尔返回值。
- [ ] 实现共享 `ToolMatrix` 与 `WidgetResponseFormatter`，并添加快照测试。
- [ ] Orchestrator 合并工具前执行 `ValidateUniqueNames()` 并在失败时记录 `AppHost.Orchestrator` 日志。
- [ ] 为 Flush / Refresh 等异步任务设置超时与失败诊断。
- [ ] 编写端到端测试覆盖 BufferDirty、SourceDirty、冲突、超时等场景。

---

## 8. 延伸阅读

- [`Refactor-TextEditor2Widget`](./Refactor-TextEditor2Widget.md)：编辑 Widget 的职责与状态机。
- [`DataSourceBindingWidget`](./DataSourceBindingWidget.md)：同步 Widget 的状态机与工具矩阵。
- [`IApp-Orchestrator-Guide`](./IApp-Orchestrator-Guide.md)：组合两个 Widget 的 orchestrator 流程。
- [`AgentEngine`](../../prototypes/Agent.Core/AgentEngine.cs)：事件生命周期与 BeforeModelCall 钩子。

> **维护信息**：2025-11-09 创建，维护者 Atelia 开发团队。文档更新后请同步调整相关单元测试与集成测试，确保契约演进得到验证。

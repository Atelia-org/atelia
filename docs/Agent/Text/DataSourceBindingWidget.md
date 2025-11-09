# DataSourceBindingWidget 设计文档(2025 战略版)

> **文档性质**: 本文档描述 `DataSourceBindingWidget` 的架构设计，该组件专注于独占缓存与下层数据源之间的同步/绑定职责。
> **核心理念**: 通过抽象接口与双 pass 调度模式，实现编辑与同步的完全解耦，类似 IDE 中的 Git Merge 工具体验。
> **配套契约**：版本号、事件与操作结果等跨组件合同详见 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md)。

> **创建日期**: 2025-11-09

> **兼容性说明**: 本组件为全新实现,目前不存在历史版本或遗留接口,本文档即为唯一契约依据。

### 1.1 设计动因
在 `TextEditor2Widget` 专注于独占缓存编辑能力的基础上，需要一个独立组件来处理：

    // Event: 下层内容变更通知（文件监听等）
    event EventHandler<DataSourceChangedEventArgs> ContentChanged;
- **双向同步**: 在独占缓存与下层数据源（文件、内存、远端）之间建立桥梁
- **冲突检测**: 识别外部数据源变更与缓存修改的不一致
- **差异展示**: 为 LLM 提供结构化的差异信息与处理工具
- **事务管理**: 管理 Flush/Refresh/Merge 等同步操作的生命周期
> **事件负载建议**：推荐在 `BufferChangedEventArgs` 与 `DataSourceChangedEventArgs` 中至少携带最新版本号与时间戳,必要时追加差异摘要,便于绑定组件在 `UpdateAsync` pass 中无需重新计算大文本即可判断状态是否变化。
- **独立组件**: 两者通过抽象接口协作，互不引用/依赖
- **职责互补**: TextEditor2Widget 负责"怎么编辑"，DataSourceBindingWidget 负责"怎么同步"
- **协同调度**: 由外部调度者（如 `MemoryNotebookApp`）统一管理双 pass 循环

---

## 2. 核心概念与术语

### 2.1 三类互操作接口

#### 2.1.1 独占缓存接口 (`IExclusiveBuffer`)
DataSourceBindingWidget 通过此接口与独占缓存交互：

```csharp
public interface IExclusiveBuffer
{
    // Getter: 读取当前缓存快照
    string GetSnapshot();

    // Getter: 获取当前版本号（Phase 2 起为主接口）
    string GetVersion();

    // Setter: 请求更新缓存内容（仅在 Refresh 等场景）
    ValueTask<OperationResult<bool>> TryUpdateContentAsync(string newContent, CancellationToken ct);

    // Event: 缓存内容变更通知
    event EventHandler<BufferChangedEventArgs> ContentChanged;
}
```

#### 2.1.2 下层数据源接口 (`IDataSource`)
通过此接口访问底层存储（文件、数据库、远端服务等）：

```csharp
public interface IDataSource
{
    // Getter: 读取下层当前内容
    ValueTask<string> ReadAsync(CancellationToken ct);

    // Getter: 获取下层内容版本号（Phase 2 起使用 Version 命名）
    ValueTask<string> GetVersionAsync(CancellationToken ct);

    // Setter: 写入内容到下层
    ValueTask<OperationResult<WriteReceipt>> WriteAsync(string content, CancellationToken ct);

    // Event: 下层内容变更通知（文件监听等）
    event EventHandler<DataSourceChangedEventArgs> ContentChanged;
}
```

> **写入回执说明**：`WriteReceipt` 为示例类型,通常包含写入字节数、耗时、版本号等信息；具体字段由数据源实现定义,但必须通过 `OperationResult` 的 `Payload` 返回。
> **事件负载建议**：推荐在 `BufferChangedEventArgs` 与 `DataSourceChangedEventArgs` 中至少携带最新版本号与时间戳,必要时追加差异摘要,便于绑定组件在 `UpdateAsync` pass 中无需重新计算大文本即可判断状态是否变化。

### 2.2 事件负载合同

为保证同步判断稳定且高效,双方事件负载需遵循以下约定：

| 事件 | 必填字段 | 可选字段 | 说明 |
| --- | --- | --- | --- |
| `BufferChangedEventArgs` | `Version`、`TimestampUtc` | `Delta`, `SelectionCount`, `OperationId` | 由 TextEditor2Widget 在缓存成功写入后触发,`Version` 为单调递增的缓冲区版本号(字符串化整数) |
| `DataSourceChangedEventArgs` | `Version`, `TimestampUtc` | `Source`, `FilePath`, `DeltaSummary` | 由数据源实现触发,`Version` 对应数据源维护的单调递增版本 |

- **Version**: Phase 2 起统一使用字符串化的数字版本号(如 `"42"`),以便同步 Widget 在单次 Update pass 中通过整数比较快速判定是否需要进入 `BufferDirty`/`SourceDirty` 状态。
- **Timestamp**: 统一使用 UTC,避免跨时区日志混淆。
- **Delta/DeltaSummary**: 如能提供简要改动信息,可用于在 `Render()` 输出中生成 “最近一次变更来源” 说明,但不是进入状态机的前置条件。
- **OperationId**: 来自编辑 Widget 的写入操作标识,有助于在 Debug 日志中关联具体工具调用。

若事件无法即时提供版本号,数据源/缓存实现必须在同一 Update pass 内补偿一次版本号查询,并在日志中记录原因(推荐写入 `Sync.State` 分类),避免长时间处于“未知脏状态”。

> **性能提示**：如版本号或差异统计计算成本较高,可在事件触发方缓存最近一次值与长度,供下一次 `UpdateAsync` 直接读取,必要时再回退到完整计算,以控制大文本场景的 CPU 开销。

### 2.3 同步状态机 (`SyncState`)

| 状态 | 描述 | 典型转入条件 |
| --- | --- | --- |
| `Synced` | 缓存与下层内容一致 | 初始化完成、Flush 成功、Refresh 成功 |
| `BufferDirty` | 缓存有未提交修改 | 缓存 ContentChanged 事件触发 |
| `SourceDirty` | 下层有新变更 | 下层 ContentChanged 事件触发 |
| `Conflicted` | 缓存与下层双向不一致 | BufferDirty + SourceDirty 同时存在 |
| `Flushing` | 正在执行提交操作 | 调用 `_flush` 工具时 |
| `Refreshing` | 正在刷新缓存 | 调用 `_refresh` 工具时 |
| `Failed` | 同步操作失败 | Flush/Refresh 异常或权限错误 |

### 2.4 同步标志位 (`SyncFlag`)

```csharp
[Flags]
public enum SyncFlag
{
    None             = 0,
    BufferDirty      = 1 << 0,  // 缓存有未提交修改
    SourceDirty      = 1 << 1,  // 下层有新变更
    Conflicted       = 1 << 2,  // 双向冲突
    ReadOnlySource   = 1 << 3,  // 下层数据源只读
    FlushInProgress  = 1 << 4,  // 正在提交
    RefreshInProgress= 1 << 5,  // 正在刷新
    DiagnosticHint   = 1 << 6   // 建议查看诊断日志
}
```

### 2.5 状态与 Flag 联动
- `SyncState` 由最近一次 Update pass 驱动的事件推导得出，`SyncFlag` 作为补充信息记录“正在进行中的操作”与“需要额外注意的事实”。
- 任何状态转换后都必须重新计算 Flag，推荐实现 `DeriveFlagsForState(SyncState state)` 与 `ApplyStatusFlags(SyncFlag additional)` 两个辅助方法，避免在业务代码中散布位运算。
- Flush/Refresh 等异步操作在启动时应显式追加 `FlushInProgress`/`RefreshInProgress`，由 `UpdateAsync` 在检测到操作完成后统一清除；这样可以避免工具在操作进行时重新曝光。
- 进入 `Flushing` 或 `Refreshing` 状态时，应立即记录对应的进行中标志，并把标志清除与状态回收逻辑集中在 `UpdateAsyncOperationStatusAsync` 中处理，保证工具可见性只在下一帧恢复。

> **实现提示**：`UpdateAsyncOperationStatusAsync` 负责检测 `_flush`、`_refresh` 等异步操作的完成情况，并在完成后统一更新 `SyncState` 与 `SyncFlag`。这样可以保证 Flush/Refresh 期间工具处于隐藏状态，完成后在下一轮 Update pass 恢复可用；票据结构与超时策略详见 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md#5-异步操作票据operationticket)。
## 3. 双 Pass 调度模式
> **调度建议**：如果 orchestrator 仍使用同步接口，可提供简单的包装方法：
> ```csharp
> public void Update() => UpdateAsync().GetAwaiter().GetResult();
> ```
> 但在 Agent 主流程中仍推荐全链路异步，减少 I/O 阻塞风险。

> **统一顺序调度**：无论同步还是异步模式，外部 orchestrator 必须在每轮 LLM 调用前按固定顺序执行 `TextEditor2Widget.Update → DataSourceBindingWidget.Update → TextEditor2Widget.Render → DataSourceBindingWidget.Render`，避免竞争性写入与状态抖动。
```csharp
public async ValueTask UpdateAsync(CancellationToken ct = default)
{
    // 1. 读取最新版本号（一次调用缓存结果，避免重复 I/O）
    var sourceVersion = await _dataSource.GetVersionAsync(ct); // 返回字符串化版本号
    var bufferVersion = _buffer.GetVersion();

    // 2. 检查下层数据源是否有变更
    if (!string.Equals(sourceVersion, _lastSourceVersion, StringComparison.Ordinal))
    {
        _syncState = DeriveNewState(SyncEvent.SourceChanged);
        _pendingFlags |= SyncFlag.SourceDirty;
    }

    // 3. 检查缓存是否有变更
    if (!string.Equals(bufferVersion, _lastBufferVersion, StringComparison.Ordinal))
    {
        _syncState = DeriveNewState(SyncEvent.BufferChanged);
        _pendingFlags |= SyncFlag.BufferDirty;
    }

    // 4. 更新内部版本缓存
    _lastSourceVersion = sourceVersion;
    _lastBufferVersion = bufferVersion;

    // 5. 处理异步操作完成（Flush/Refresh）
    await UpdateAsyncOperationStatusAsync(ct);
}
```

> **实现提示**：`UpdateAsyncOperationStatusAsync` 负责检测 `_flush`、`_refresh` 等异步操作的完成情况，并在完成后统一更新 `SyncState` 与 `SyncFlag`。这样可以保证 Flush/Refresh 期间工具处于隐藏状态，完成后在下一轮 Update pass 恢复可用。

> **术语说明**：示例中的 `_pendingFlags` 为内部累积寄存器，用于在单次 Update pass 中收集异步结果产生的额外标志，最终通过 `ApplyStatusFlags` 合并到对外可见的 `SyncFlag`。

> **版本号策略**：`_lastSourceVersion` 与 `_lastBufferVersion` 初始值为 `"0"`, 每次成功刷新或写入后即刻更新,并与事件参数中的 `Version` 保持一致,实现 O(1) 级别的脏值检测。

### 3.2 Render Pass
在 Update 后，调用 `Render()` 生成面向 LLM 的 TUI 输出：

```csharp
public LevelOfDetailContent Render()
{
    var basic = RenderBasicStatus();   // 状态摘要
    var detail = RenderDetailedView(); // 差异信息、可用工具

    return new LevelOfDetailContent(basic, detail);
}
```

### 3.3 调度时序图

```
[MemoryNotebookApp]
    │
    ├─→ TextEditor2Widget.Update()
    │   └─ 更新编辑状态、虚拟选区等
    │
    ├─→ DataSourceBindingWidget.Update()
    │   └─ 检测缓存/下层变更、更新同步状态
    │
    ├─→ TextEditor2Widget.Render()
    │   └─ 生成编辑工具面板 TUI
    │
    ├─→ DataSourceBindingWidget.Render()
    │   └─ 生成同步状态与差异展示 TUI
    │
    └─→ AgentEngine.InvokeModel(mergedContext)
```

---

## 4. 工具集定义

### 4.1 核心工具

#### 4.1.1 `_flush` - 提交缓存到下层
```csharp
[Tool("sync.flush", "将缓存内容写入下层数据源")]
public async ValueTask<LodToolExecuteResult> FlushAsync(
    [ToolParam("是否强制覆盖（忽略下层变更）")] bool force = false,
    CancellationToken ct = default)
{
    if (_syncState == SyncState.Synced)
        return Success("缓存与下层已同步，无需提交");

    if (_syncState == SyncState.Conflicted && !force)
        return Failed("检测到冲突，请先调用 _diff 查看差异，或使用 force=true 强制覆盖");

    _syncState = SyncState.Flushing;
    _pendingFlags |= SyncFlag.FlushInProgress;

    var content = _buffer.GetSnapshot();
    var writeResult = await _dataSource.WriteAsync(content, ct);

    if (writeResult.Success)
    {
        _syncState = SyncState.Synced;
        return Success($"已成功提交 {content.Length} 字符到下层数据源");
    }

    _syncState = SyncState.Failed;
    _pendingFlags |= SyncFlag.DiagnosticHint;
    return Failed($"提交失败: {writeResult.ErrorCode ?? "Unknown"} {writeResult.Message}");
}
```

> **说明**：写入操作在发起时切换至 `Flushing` 状态并设置 `FlushInProgress`，由后续的 `UpdateAsyncOperationStatusAsync` 统一清除标志、恢复工具可见性。`_dataSource.WriteAsync` 返回 `OperationResult<WriteReceipt>`（命名示例），失败路径需提供 `ErrorCode`/`Message` 供 Formatter 与日志使用。

> **返回助手说明**：示例中的 `Success(...)` / `Failed(...)` 代表封装 `LodToolExecuteResult` 的辅助方法，实际实现可复用公共基类或在当前 Widget 内定义统一的格式化逻辑。

#### 4.1.2 `_refresh` - 从下层刷新缓存
```csharp
[Tool("sync.refresh", "从下层数据源刷新缓存（丢弃未提交修改）")]
public async ValueTask<LodToolExecuteResult> RefreshAsync(
    [ToolParam("是否确认丢弃缓存修改")] bool confirm = false,
    CancellationToken ct = default)
{
    if (_syncState == SyncState.BufferDirty && !confirm)
        return Failed("缓存有未提交修改，使用 confirm=true 确认丢弃");

    _syncState = SyncState.Refreshing;
    _pendingFlags |= SyncFlag.RefreshInProgress;

    var sourceContent = await _dataSource.ReadAsync(ct);
    var updateResult = await _buffer.TryUpdateContentAsync(sourceContent, ct);

    if (updateResult.Success)
    {
        _syncState = SyncState.Synced;
        return Success($"已从下层刷新 {sourceContent.Length} 字符");
    }

    _syncState = SyncState.Failed;
    _pendingFlags |= SyncFlag.DiagnosticHint;
    return Failed($"刷新失败: {updateResult.ErrorCode ?? "BufferRejected"} {updateResult.Message}");
}
```

> **说明**：刷新同样通过进入 `Refreshing` 状态与设置 `RefreshInProgress` 来阻止工具重复曝光，最终状态由 `UpdateAsync` 统一结算；`TryUpdateContentAsync` 返回的 `OperationResult<bool>` 需在失败时提供诊断信息（例如 `BufferRejected`）。

#### 4.1.3 `_diff` - 对比缓存与下层差异
```csharp
[Tool("sync.diff", "对比缓存与下层数据源的差异")]
public async ValueTask<LodToolExecuteResult> DiffAsync(
    [ToolParam("差异上下文行数")] int contextLines = 3,
    CancellationToken ct = default)
{
    var bufferContent = _buffer.GetSnapshot();
    var sourceContent = await _dataSource.ReadAsync(ct);

    var diff = GenerateUnifiedDiff(sourceContent, bufferContent, contextLines);

    return Success($"差异统计: {diff.Stats}\n\n{diff.Patch}");
}
```

> **渲染提示**：工具返回中仅包含原始 diff 文本，`Render()` 方法会在 Detail 级别的 Markdown 中用 `### [Diff]` 区块嵌入补充展示，保持结构化输出与工具结果一致。

#### 4.1.4 `_accept_source` - 接受下层变更（合并到缓存）
```csharp
[Tool("sync.accept_source", "接受下层数据源的变更并合并到缓存")]
public async ValueTask<LodToolExecuteResult> AcceptSourceAsync(
    [ToolParam("合并策略: overwrite | merge_lines")] string strategy = "overwrite",
    CancellationToken ct = default)
{
    var sourceContent = await _dataSource.ReadAsync(ct);

    if (strategy == "overwrite")
    {
        var updateResult = await _buffer.TryUpdateContentAsync(sourceContent, ct);
        if (updateResult.Success)
        {
            _syncState = SyncState.Synced;
            _pendingFlags = SyncFlag.None;
            return Success("已用下层内容覆盖缓存");
        }

        _syncState = SyncState.Conflicted;
        _pendingFlags |= SyncFlag.Conflicted;
        return Failed($"缓存拒绝覆盖，下层变更仍待处理: {updateResult.ErrorCode ?? "BufferRejected"}");
    }
    else if (strategy == "merge_lines")
    {
        // 实现行级合并逻辑（Phase 2 扩展）
        throw new NotImplementedException();
    }
    else
    {
        return Failed($"未知合并策略: {strategy}");
    }
}
```

### 4.2 工具可见性矩阵

| SyncState | `_flush` | `_refresh` | `_diff` | `_accept_source` | 备注 |
| --- | --- | --- | --- | --- | --- |
| `Synced` | [Block] | [Yes] | [Block] | [Block] | `UpdateAsync` 将 Flag/reset 回到 `None` |
| `BufferDirty` | [Yes] | [Yes] | [Yes] | [Block] | Update pass 会附加 `BufferDirty` Flag，用于提示可提交 |
| `SourceDirty` | [Block] | [Yes] | [Yes] | [Yes] | 渲染时强调“下层有新版本” |
| `Conflicted` | [Yes]* | [Yes] | [Yes] | [Yes] | `_flush` 需 `force=true`，且 Render 必须展示冲突说明 |
| `Flushing` | [Block] | [Block] | [Block] | [Block] | 异步写入完成后由 `UpdateAsync` 恢复可见性 |
| `Refreshing` | [Block] | [Block] | [Block] | [Block] | 缓存刷新成功后恢复到 `Synced`/`SourceDirty` |
| `Failed` | [Yes] | [Yes] | [Yes] | [Block] | Render 输出诊断提示、建议查看日志 |

> **实现提示**：最终的可见工具集合由 `SyncToolMatrix` 计算，继承 `ToolMatrix<TState, TFlag>` 并返回不可变列表。默认 Guidance 与错误码 → Guidance 映射需要与编辑侧共享配置，具体规范见 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md#6-toolmatrix-基础设施)。

---

## 5. Markdown 响应规范

### 5.1 状态头部
```markdown
sync_state: `BufferDirty`
sync_flags: `BufferDirty`, `DiagnosticHint`
```

### 5.2 概览段
```markdown
### [Warning] 同步状态
- summary: 缓存有未提交修改（+128 字符）
- guidance: 调用 `_flush` 提交，或 `_refresh` 放弃修改
```

### 5.3 差异指标
```markdown
### [Metrics] 差异统计
| 指标 | 值 |
| --- | --- |
| buffer_size | 2048 |
| source_size | 1920 |
| delta | +128 |
| last_sync | 2025-11-09 10:23:15 |
```

### 5.4 差异预览（仅 Detail 级别）
```markdown
### [Diff] 差异预览
```diff
@@ -10,3 +10,5 @@
 existing line A
 existing line B
+added line C
+added line D
```
```

### 5.5 结构化响应载体与 Formatter (Phase 2 执行项)

- 定义 `SyncResponse` 记录类型,字段对齐编辑侧的 `TextEditResponse`,新增同步特有指标字段(如 `BufferVersion`, `SourceVersion`, `LastSyncAt`)。

```csharp
public sealed record SyncResponse(
    SyncState State,
    SyncFlag Flags,
    string Summary,
    string? Guidance,
    SyncMetrics Metrics,
    IReadOnlyList<SyncDiffEntry>? DiffPreview
);
```

- `WidgetResponseFormatter.Render(SyncResponse response)` 负责生成 Markdown 与 `LevelOfDetailContent`,并与编辑侧共享基础模板(状态头部/指标表)。
- `Basic` 视图由 `Summary` 与 `Guidance` 直接拼装,保持与 `TextEditResponse` 一致,方便 IApp 合并两个 Widget 的窗口。
- 快照测试覆盖 `Synced`、`BufferDirty`、`SourceDirty`、`Conflicted` 等典型场景,确保 Formatter 结构稳定。

> **一致性目标**: 通过统一 DTO + Formatter,确保编辑/同步两侧的响应都能以同一数据结构传递给 IApp、日志与未来的 UI 渲染。

---

## 6. 典型交互流程

### 6.1 缓存修改后提交
1. LLM 通过 TextEditor2Widget 修改缓存
2. DataSourceBindingWidget.UpdateAsync() 检测到 `BufferDirty`
3. Render() 输出提示："缓存有未提交修改"
4. LLM 调用 `_flush`，成功后状态回到 `Synced`

### 6.2 外部文件变更处理
1. 文件监听器触发 `DataSource.ContentChanged` 事件
2. UpdateAsync() 检测版本号变化，状态切换到 `SourceDirty`
3. Render() 提示："下层数据源有新变更"
4. LLM 调用 `_diff` 查看差异
5. LLM 决定调用 `_accept_source` 或 `_refresh`

### 6.3 冲突场景
1. 缓存有修改（`BufferDirty`），外部文件同时变更
2. UpdateAsync() 识别双向冲突，状态切换到 `Conflicted`
3. Render() 输出红色警告："检测到同步冲突"
4. LLM 调用 `_diff` 对比
5. LLM 选择：
   - `_flush(force=true)` 强制覆盖下层
   - `_refresh(confirm=true)` 丢弃缓存
   - 人工介入处理

---

## 7. 状态流转合同

| 事件 | 前置 SyncState | 新 SyncState | Flag 变化 | 工具可见性变化 |
| --- | --- | --- | --- | --- |
| 缓存编辑 | `Synced` | `BufferDirty` | 添加 `BufferDirty` | `_flush` 可见 |
| 下层变更 | `Synced` | `SourceDirty` | 添加 `SourceDirty` | `_accept_source` 可见 |
| 下层变更 | `BufferDirty` | `Conflicted` | 添加 `Conflicted` | 全部差异工具可见 |
| `_flush` 成功 | `BufferDirty` | `Synced` | 移除 `BufferDirty` | 恢复默认 |
| `_flush` 失败 | `BufferDirty` | `Failed` | 添加 `DiagnosticHint` | 允许重试 |
| `_refresh` 成功 | 任意 | `Synced` | 清空所有 Flags | 恢复默认 |
| `_refresh` 失败 | 任意 | `Failed` | 添加 `DiagnosticHint` | 允许重试 |

---

### 4.3 工具可见性配置化落地 (Phase 2 执行项)

- 引入 `SyncToolMatrix` 静态配置,以 `SyncState × CurrentOperation → ToolVisibility` 映射的形式驱动工具显隐与默认 Guidance。`CurrentOperation` 取值 `None | Flushing | Refreshing`。
- `ToolVisibility` 结构应包含 `VisibleTools`、`DefaultGuidance`、`AdditionalFlags` 等字段,供 Widget 与 IApp 直接消费。
- Update pass 每次状态迁移时统一从映射刷新 `VisibleTools`,替代手写 `switch`/`if`。
- 单元测试只需针对有限矩阵断言 `VisibleTools` 集合与 Guidance 文案,快速验证新增工具或状态组合。

> **扩展性**: 当 `_accept_source` 引入行级合并等策略时,只需在矩阵中扩展对应状态与 Guidance,无需改动 Widget 主流程。

---

## 8. 接口抽象与测试策略

### 8.1 接口设计原则
- **最小依赖**: 仅依赖 Getter/Setter/Event 三类操作
- **可替换**: 支持文件、内存、Mock 等多种实现
- **异步友好**: 所有 I/O 操作返回 `ValueTask`

### 8.2 测试矩阵
- **单元测试**: 使用 Mock 实现验证状态机转换、工具逻辑
- **集成测试**: 组合真实 FileDataSource + MemoryBuffer 验证端到端流程
- **边界测试**: 覆盖并发冲突、I/O 超时、权限错误等异常场景

### 8.3 Mock 示例
```csharp
public class MockDataSource : IDataSource
{
    private string _content = "";

    public ValueTask<string> ReadAsync(CancellationToken ct)
        => ValueTask.FromResult(_content);

    public void SimulateExternalChange(string newContent)
    {
        _content = newContent;
        ContentChanged?.Invoke(this, new DataSourceChangedEventArgs());
    }

    public event EventHandler<DataSourceChangedEventArgs>? ContentChanged;
}
```

---

## 9. 诊断分类 (`DebugUtil` 类别)

| 类别 | 用途 |
| --- | --- |
| `Sync.State` | 记录状态机转换、版本变化、事件触发 |
| `Sync.Flush` | 记录提交操作的参数、耗时、结果 |
| `Sync.Refresh` | 记录刷新操作的数据量、耗时、失败原因 |
| `Sync.Conflict` | 记录冲突检测详情、差异统计 |
| `Sync.External` | 记录下层数据源事件、防抖处理 |

> 与旧版 `TextEdit.Persistence` / `TextEdit.External` 日志对应时,可在 `Sync.*` 分类的消息中追加来源标记,方便跨组件排查历史问题。
> 所有 `OperationResult` 的失败路径需写入对应日志分类,并附带 `OperationId` 与版本号,实现端到端诊断链路。

---

## 10. 实施路线图

| 阶段 | 目标 | 关键交付 |
| --- | --- | --- |
| **Phase 0** | 接口定义 | `IExclusiveBuffer`、`IDataSource` 接口与文档 |
| **Phase 1** | 最小 MVP | 实现 `Synced`/`BufferDirty`/`SourceDirty` 三态 + `_flush`/`_refresh` 工具 |
| **Phase 2** | 结构化响应与显隐矩阵 | 落地 `SyncResponse` DTO、统一 Formatter、`SyncToolMatrix`、版本号事件负载、`OperationResult` 契约 |
| **Phase 3** | 异步票据与冲突处理 | 引入 `OperationTicket`、完善超时/诊断机制、增加 `Conflicted` 状态与 `_diff`/`_accept_source` 工具 |
| **Phase 4** | 文件监听 | 集成 FileSystemWatcher + 防抖逻辑 |
| **Phase 5** | 高级合并 | 支持行级合并策略、三向合并 |
| **Phase 6** | 性能优化 | 异步化、增量 diff、大文件支持 |

---

## 11. 与 TextEditor2Widget 的协同

### 11.1 接口契约
- TextEditor2Widget 实现 `IExclusiveBuffer` 接口
- DataSourceBindingWidget 通过接口订阅 `ContentChanged` 事件
- 两侧均遵循 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md) 中的版本号、事件与 `OperationResult` 约定
- 双方不直接引用对方的具体类型

### 11.2 外部调度者职责
> **调度建议**：合并后的 TUI 需保证编辑与同步信息的并列呈现，避免任一组件的反馈被折叠或丢失。调度者必须严格保持 Update→Render 的顺序调用，确保两个 Widget 的状态在同一帧内一致。
```csharp
public class MemoryNotebookApp
{
    private TextEditor2Widget _editor;
    private DataSourceBindingWidget _sync;

    public async Task UpdateBeforeLLMCallAsync(CancellationToken ct)
    {
        // Pass 1: Update
        await _editor.UpdateAsync(ct);
        await _sync.UpdateAsync(ct);

        // Pass 2: Render
        var editorUI = _editor.Render();
        var syncUI = _sync.Render();

        // 合并 TUI 输出
        var mergedContext = MergeTUI(editorUI, syncUI);

        // 调用 LLM
        await AgentEngine.InvokeModelAsync(mergedContext, ct);
    }
}
```

### 11.3 工具命名空间隔离
- TextEditor2Widget 工具: `edit.replace`、`edit.replace_selection`
- DataSourceBindingWidget 工具: `sync.flush`、`sync.refresh`、`sync.diff`

---

## 12. 后续扩展方向

### 12.1 多数据源支持
- 文件系统 (`FileDataSource`)
- 数据库 (`DbDataSource`)
- 远端 API (`RemoteDataSource`)
- Git 仓库 (`GitDataSource`)

### 12.2 高级合并策略
- 三向合并（base + theirs + ours）
- 语义感知合并（AST 级别）
- 用户自定义合并规则

### 12.3 历史与撤销
- 维护同步操作历史
- 支持回滚到特定快照

### 12.4 性能优化
- 增量 diff 算法
- 大文件分块处理
- 异步后台同步

---

## 附录 A: 术语对照表

| 术语 | 说明 |
| --- | --- |
| `IExclusiveBuffer` | 独占缓存接口，由 TextEditor2Widget 实现 |
| `IDataSource` | 下层数据源接口，可有多种实现（文件、内存等） |
| `SyncState` | 同步状态枚举 |
| `SyncFlag` | 同步标志位枚举 |
| `Flush` | 提交缓存到下层数据源 |
| `Refresh` | 从下层数据源刷新缓存 |
| `Diff` | 差异对比 |
| `Conflicted` | 双向冲突状态 |
| `OperationResult` | 统一的操作返回结构，封装成功状态、错误码与诊断提示 |
| `OperationTicket` | 跟踪 Flush/Refresh 等异步操作的票据，用于 Update pass 回收 |

---

**文档版本**: 战略版-v1.0
**最后更新**: 2025年11月9日
**维护者**: Atelia 开发团队

本文档定义了 `DataSourceBindingWidget` 的完整设计，通过与 `TextEditor2Widget` 的职责分离与接口协作，实现编辑与同步的完全解耦，为 LLM 提供类似 IDE Merge 工具的同步体验。

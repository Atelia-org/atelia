# IApp Orchestrator 使用指南（TextEditor2Widget × DataSourceBindingWidget）

> **文档目的**：面向 `IApp` 实现者，说明如何在单个应用中组合 `TextEditor2Widget`（编辑）与 `DataSourceBindingWidget`（同步），并与 `AgentEngine` / `DefaultAppHost` 协同，实现双 Pass 顺序调度。
> **读者前提**：熟悉 `IApp` 接口与 `AgentEngine` 执行模型，了解两个 Widget 的各自职责（参见 [`Refactor-TextEditor2Widget`](./Refactor-TextEditor2Widget.md) 与 [`DataSourceBindingWidget`](./DataSourceBindingWidget.md)）。

> **快照回传**：在每次 `UpdateAsync` 之后，orchestrator 应立即调用 `app.UpdateSnapshot(host.Snapshot())` 等方法把最新快照同步回 IApp，保证工具集合与渲染窗口保持最新。
> **契约参照**：跨组件的版本号、事件与操作结果合同详见 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md)，orchestrator 需确保自身逻辑与该文档保持一致。
---

## 1. IApp 快速回顾

| 成员 | 说明 | 与 Widget 的关系 |
| --- | --- | --- |
| `string Name` | App 名称，需唯一 | 用于 `DefaultAppHost` 管理与调试日志输出 |
| `string Description` | 简要描述 | 可在 UI 或提示中呈现 |
| `IReadOnlyList<ITool> Tools` | App 暴露的全部工具 | 必须合并编辑与同步工具集（例如 `edit.*` 与 `sync.*`） |
| `string? RenderWindow()` | 返回面向 LLM 的 TUI | 负责将两个 Widget 渲染结果合并为单个窗口 |

`DefaultAppHost` 会聚合所有 App 的工具与窗口文本，`AgentEngine` 在调用模型前通过 `RenderLiveContext()` 将窗口文本注入上下文。因此，IApp 实现应保证：

1. 工具列表实时反映两个 Widget 的可见工具；
2. `RenderWindow()` 输出的 Markdown/TUI 始终与最新状态一致；
3. 不直接在 IApp 内部访问底层数据源，所有 I/O 由 Widget 层负责。

---

## 2. 推荐的类骨架

```csharp
public sealed record AppSnapshot(
    LevelOfDetailContent Editor,
    LevelOfDetailContent Sync,
    IReadOnlyList<ITool> Tools)
{
    public static AppSnapshot Empty { get; } = new(
        new LevelOfDetailContent(string.Empty, string.Empty),
        new LevelOfDetailContent(string.Empty, string.Empty),
        Array.Empty<ITool>());

    public bool HasContent
        => !string.IsNullOrEmpty(Editor.Detail) || !string.IsNullOrEmpty(Sync.Detail);
}

public sealed class MemoryNotebookApp : IApp
{
    private AppSnapshot _snapshot = AppSnapshot.Empty;

    public string Name => "memory-notebook";
    public string Description => "独占缓存编辑 + 数据源同步";

    public IReadOnlyList<ITool> Tools => _snapshot.Tools;

    public void UpdateSnapshot(AppSnapshot snapshot)
        => _snapshot = snapshot;

    public string? RenderWindow()
    {
        if (!_snapshot.HasContent)
        {
            return null;
        }

        return MergeTui(_snapshot.Editor, _snapshot.Sync);
    }

    private static string MergeTui(LevelOfDetailContent editor,
                                   LevelOfDetailContent sync)
    {
        // 以统一窗口格式拼接两个 Widget 的 Detail 内容。
        var builder = new StringBuilder();
        builder.AppendLine("## [Summary] 状态摘要");
        builder.AppendLine(editor.Basic);
        builder.AppendLine(sync.Basic);
        builder.AppendLine();
        builder.AppendLine("## [Edit] 缓存编辑");
        builder.AppendLine(editor.Detail);
        builder.AppendLine();
        builder.AppendLine("## [Sync] 数据源同步");
        builder.Append(sync.Detail);
        return builder.ToString().TrimEnd();
    }
}
```

> **缓存策略**：IApp 不直接调用 Widget 的 `Render()`，而是等待 orchestrator 在 Update pass 结束后注入 `AppSnapshot`。Snapshot 既缓存渲染结果，也携带工具列表，避免 IApp 再做额外推导。
>
> **工具可见性**：`AppSnapshot.Tools` 建议由 orchestrator 合并 `TextEditToolMatrix.VisibleToolsFor(...)` 与 `SyncToolMatrix.VisibleToolsFor(...)` 的结果，并按名称排序后写入，确保 UI 与 Widget 一致。

---

## 3. 双 Pass 调度：Update → Render

### 3.1 调度职责

外层 orchestrator（例如 `MemoryNotebookAppHost` 或 `NotebookShell`）应负责：

1. 在每轮 LLM 调用前依次执行两个 Widget 的 `UpdateAsync`；
2. 在 `UpdateAsync` 完成后调用 `Render()`，并把结果缓存在 App 内；
3. 确保整个流程单线程、顺序执行，避免并发写入；
4. 在 Update pass 中轮询各 Widget 的 `OperationTicket`（比如 Flush/Refresh）是否完成，并根据 `OperationResult` 更新快照与 Guidance。

> `GetVisibleTools()` 建议由各 Widget 基于 `TextEditToolMatrix` / `SyncToolMatrix` 返回不可变视图,防止 orchestrator 误改内部集合。

### 3.2 典型 orchestrator 伪代码

```csharp
public sealed class MemoryNotebookAppHost
{
    private readonly TextEditor2Widget _editor;
    private readonly DataSourceBindingWidget _binding;
    private AppSnapshot _latestSnapshot = AppSnapshot.Empty;

    public MemoryNotebookAppHost(TextEditor2Widget editor,
                                 DataSourceBindingWidget binding)
    {
        _editor = editor;
        _binding = binding;
    }

    public async ValueTask UpdateAsync(CancellationToken ct = default)
    {
        // Pass 1: Update
        await _editor.UpdateAsync(ct);
        await _binding.UpdateAsync(ct);

        // Pass 2: Render + 合并工具
        var editorView = _editor.Render();
        var syncView = _binding.Render();

        var tools = MergeTools(
            _editor.GetVisibleTools(),
            _binding.GetVisibleTools());

        _latestSnapshot = new AppSnapshot(editorView, syncView, tools);
    }

    public AppSnapshot Snapshot() => _latestSnapshot;

    private static IReadOnlyList<ITool> MergeTools(
        IReadOnlyList<ITool> editorTools,
        IReadOnlyList<ITool> syncTools)
    {
        var combined = editorTools.Concat(syncTools).ToArray();
        var duplicates = combined
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            var conflictList = string.Join(", ", duplicates);
            DebugUtil.Print("AppHost.Orchestrator", $"Tool name conflict detected: {conflictList}");
            throw new InvalidOperationException($"Duplicated tool names: {conflictList}");
        }

        return combined
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

> **首帧初始化**：应用启动时即可调用一次 `UpdateAsync` + `Snapshot()`，把初始快照注入 IApp，避免第一轮 `RenderWindow()` 读到空白内容。
> **工具冲突监测**：在合并前建议调用 `ToolMatrixUtilities.ValidateUniqueNames(editorTools, syncTools)`（示例方法）或自定义校验逻辑，并将冲突写入 `AppHost.Orchestrator` 日志，避免模型访问到重复命名的工具。
```

> **顺序要求**：始终先更新编辑 Widget，再更新绑定 Widget。同步 Widget 会读取编辑侧的 `ContentChanged` / 版本号信息判断是否需要刷新状态。

### 3.3 OperationTicket 轮询

- `TextEditor2Widget` 与 `DataSourceBindingWidget` 在 Flush/Refresh 等长耗时操作时会生成 `OperationTicket`（详见 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md#5-异步操作票据operationticket)）。
- orchestrator 应在每次 `UpdateAsync` 调用后读取 Widget 暴露的票据状态（例如通过 `TryDequeueCompletedOperations()` 或显式的 `OperationTicket? CurrentFlush` 属性）。
- 当票据完成时，读取其 `OperationResult`，并在下一帧快照中更新 Guidance、状态以及 `AppHost.Orchestrator` 日志；若超时或失败，需将 `DiagnosticHint` 透传给模型并提示查看对应日志分类。
- 未完成的票据保持 `FlushInProgress` / `RefreshInProgress` 标志，防止工具重复暴露；由下一次 `UpdateAsync` 继续轮询。

---

## 4. 将 orchestrator 嵌入 AgentEngine 生命周期

### 4.1 在模型调用前驱动 Update

最简单的做法是勾住 `AgentEngine.BeforeModelCall` 事件，在事件处理器中调用 orchestrator 的 `UpdateAsync`：

```csharp
var app = new MemoryNotebookApp(editorWidget, bindingWidget);
var host = new MemoryNotebookAppHost(editorWidget, bindingWidget);
var engine = new AgentEngine(initialApps: new[] { app });

engine.BeforeModelCall += async (_, args) =>
{
    await host.UpdateAsync(args.CancellationToken);

    // 将最新渲染结果同步回 IApp（例如通过公开的 UpdateSnapshot 方法）。
    app.UpdateSnapshot(host.Snapshot());
};
```

> **线程模型**：`AgentEngine` 明确标注“非线程安全”。事件处理器必须与引擎的 `StepAsync` 在同一线程/上下文顺序执行，禁止并发。

### 4.2 RenderWindow 读取缓存

`RenderWindow()` 不应直接访问 Widget,而是读取第 2 节示例中 `UpdateSnapshot` 预先注入的 `AppSnapshot`。当 `AppSnapshot.HasContent` 为 `false` 时返回 `null` 或空窗口,以避免模型读取到陈旧内容。

---

### 4.3 快照组装流程

1. Widget 工具执行完成后返回 `OperationResult<TextEditResponse>` / `OperationResult<SyncResponse>`（或成功时的直接 DTO），再由共享的 `WidgetResponseFormatter` 转换为 `LevelOfDetailContent`。
2. `GetVisibleTools()` 依托映射(`TextEditToolMatrix`、`SyncToolMatrix`) 返回不可变工具列表。
3. Orchestrator 在 Pass 结束时把两份 `LevelOfDetailContent` 与合并工具装配为 `AppSnapshot`,并注入 IApp。
4. IApp 在渲染窗口时仅消费快照数据,避免直接访问 Widget,确保双 Pass 序列的封闭性。

> **兜底策略**：若任一 Formatter 产出为空,使用 `LevelOfDetailContent(string.Empty, string.Empty)` 占位,保持快照结构完整。

---

## 5. 工具集合同步要点

1. **命名空间隔离**：约定 `edit.*` 归编辑 Widget，`sync.*` 归同步 Widget，避免名称冲突。
2. **工具清单校验**：在 orchestrator 合并工具(`MergeTools`)时对名称去重，若检测到重复命名（含大小写变体）应立即抛出诊断，防止后续模型调用落到错误的实现上，并将冲突写入 `AppHost.Orchestrator` 日志。
3. **可见性回传**：Widget 应在 `GetVisibleTools()` 中直接反映最新矩阵结果，orchestrator 合并后写入 `AppSnapshot.Tools`，无需 IApp 额外缓存。
4. **调试分类**：
   - 编辑 Widget 使用 `TextEdit.*` 日志分类；
   - 同步 Widget 使用 `Sync.*` 日志分类。
    调试时可在 IApp 中提供快捷指引（例如在 Render 中提示查看对应日志），并在需要关联历史 `TextEdit.Persistence` / `TextEdit.External` 记录时于 `Sync.*` 日志中追加来源标记。
5. **OperationResult 串联**：orchestrator 需要读取最新快照中的 `OperationResult` 元信息（例如 `ErrorCode`、`DiagnosticHint`），并在输出 Guidance 或日志时保持一致，避免编辑/同步两侧提示不统一。
6. **契约复核**：定期对照 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md) 检查版本号、事件与工具矩阵是否和文档同步演进。

---

## 6. 常见陷阱与建议

| 场景 | 风险 | 建议 |
| --- | --- | --- |
| 并发调用 `UpdateAsync` | 状态竞态，版本号不一致 | 严格在事件循环中顺序执行，禁止多线程同时访问 |
| `RenderWindow()` 直接调用 Widget.Render | 破坏双 Pass 模式，可能出现旧状态 | 始终通过 orchestrator 缓存好的最新快照 |
| IApp.Tools 未合并 | 某些工具缺失，模型无法调用 | 构造工具集合时合并两个 Widget 的可见工具 |
| `RenderWindow()` 返回空字符串 | 模型误判“窗口仍存在但为空” | 如需暂时隐藏窗口请返回 `null`，返回空字符串表示窗口仍可见只是没有正文 |
| 版本号不同步 | 同步 Widget 误判状态 | 确保 `ContentChanged` / `DataSourceChanged` 事件携带最新版本号，并在 Update pass 后刷新快照 |
| 忽略错误恢复 | Flush/Refresh 失败后状态悬挂 | 依赖同步 Widget 的 `SyncState.Failed` + Flags，并在 TUI 中给出重试指引 |

---

## 7. 验证 checklist

- [ ] 双 Widget 在单元测试中独立通过 Update/Render 回合。
- [ ] orchestrator 单测覆盖“缓存变更”“外部变更”“冲突”三个入口。
- [ ] IApp.Tools 在工具状态变更后能正确刷新。
- [ ] AgentEngine.BeforeModelCall 事件处理器已串联 orchestrator。
- [ ] RenderWindow 输出符合 Markdown/TUI 规范。

---

## 8. 延伸阅读

- [`Refactor-TextEditor2Widget.md`](./Refactor-TextEditor2Widget.md)：编辑 Widget 的目标架构与响应契约。
- [`DataSourceBindingWidget.md`](./DataSourceBindingWidget.md)：同步 Widget 的状态机、工具集与双 Pass 细节。
- `prototypes/Agent.Core/IApp.cs`：IApp/DefaultAppHost 接口定义。
- `prototypes/Agent.Core/AgentEngine.cs`：AgentEngine 生命周期、事件与模型调用流程。

> **后续扩展**：若需要在 IApp 中组合更多 Widget，可复用本文档的 orchestrator 框架，只需扩展 Update / Render 顺序与工具聚合策略即可。

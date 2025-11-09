# IApp Orchestrator 使用指南（TextEditor2Widget × DataSourceBindingWidget）

> **文档目的**：面向 `IApp` 实现者，说明如何在单个应用中组合 `TextEditor2Widget`（编辑）与 `DataSourceBindingWidget`（同步），并与 `AgentEngine` / `DefaultAppHost` 协同，实现双 Pass 顺序调度。
> **读者前提**：熟悉 `IApp` 接口与 `AgentEngine` 执行模型，了解两个 Widget 的各自职责（参见 [`Refactor-TextEditor2Widget`](./Refactor-TextEditor2Widget.md) 与 [`DataSourceBindingWidget`](./DataSourceBindingWidget.md)）。

> **快照回传**：在每次 `UpdateAsync` 之后，orchestrator 应立即调用 `app.UpdateSnapshot(host.Snapshot())` 等方法把最新快照同步回 IApp，保证工具集合与渲染窗口保持最新。
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
public sealed class MemoryNotebookApp : IApp
{
    private readonly TextEditor2Widget _editor;
    private readonly DataSourceBindingWidget _binding;

    private LevelOfDetailContent? _editorSnapshot;
    private LevelOfDetailContent? _bindingSnapshot;
    private IReadOnlyList<ITool>? _tools;

    private static readonly LevelOfDetailContent EmptyContent = new(string.Empty, string.Empty);

    public MemoryNotebookApp(TextEditor2Widget editor, DataSourceBindingWidget binding)
    {
        _editor = editor;
        _binding = binding;
    }

    public string Name => "memory-notebook";
    public string Description => "独占缓存编辑 + 数据源同步";

    public IReadOnlyList<ITool> Tools => _tools ?? Array.Empty<ITool>();

    public void UpdateSnapshot((LevelOfDetailContent editor,
                                LevelOfDetailContent binding) snapshot)
    {
        _editorSnapshot = snapshot.editor;
        _bindingSnapshot = snapshot.binding;
        _tools = BuildToolCache();
    }

    public string? RenderWindow()
    {
        if (_editorSnapshot is null && _bindingSnapshot is null)
        {
            return null;
        }

        return MergeTui(_editorSnapshot ?? EmptyContent,
                        _bindingSnapshot ?? EmptyContent);
    }

    private IReadOnlyList<ITool> BuildToolCache()
        => _editor.Tools.Concat(_binding.Tools)
                        .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                        .ToArray();

    private static string MergeTui(LevelOfDetailContent editor,
                                   LevelOfDetailContent binding)
    {
        // 以统一窗口格式拼接两个 Widget 的 Detail 内容。
        var builder = new StringBuilder();
        builder.AppendLine("## [Summary] 状态摘要");
        builder.AppendLine(editor.Basic);
        builder.AppendLine(binding.Basic);
        builder.AppendLine();
        builder.AppendLine("## [Edit] 缓存编辑");
        builder.AppendLine(editor.Detail);
        builder.AppendLine();
        builder.AppendLine("## [Sync] 数据源同步");
        builder.Append(binding.Detail);
        return builder.ToString().TrimEnd();
    }
}
```

> **缓存策略**：IApp 不直接调用 Widget 的 `Render()`，而是等待 orchestrator 在 Update pass 结束后注入最新快照。若工具可见性依赖 Widget 内部状态，请在 `UpdateSnapshot` 中同步刷新 `_tools` 缓存，或在 Widget 内提供可观测的工具列表更新事件。
>
> **工具可见性**：示例中通过 `_editor.Tools` / `_binding.Tools` 读取工具集合，Widget 需要提供只读视图或变更事件，避免 IApp 直接持有可变引用；若内部仍以列表维护，请返回 `IReadOnlyList<ITool>` 的快照以防越权修改。

---

## 3. 双 Pass 调度：Update → Render

### 3.1 调度职责

外层 orchestrator（例如 `MemoryNotebookAppHost` 或 `NotebookShell`）应负责：

1. 在每轮 LLM 调用前依次执行两个 Widget 的 `UpdateAsync`；
2. 在 `UpdateAsync` 完成后调用 `Render()`，并把结果缓存在 App 内；
3. 确保整个流程单线程、顺序执行，避免并发写入。

### 3.2 典型 orchestrator 伪代码

```csharp
public sealed class MemoryNotebookAppHost
{
    private readonly TextEditor2Widget _editor;
    private readonly DataSourceBindingWidget _binding;
    private static readonly LevelOfDetailContent EmptyContent = new(string.Empty, string.Empty);

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

        // Pass 2: Render（若 IApp 需要缓存渲染结果，可在此处存储）
        _latestEditorView = _editor.Render();
        _latestBindingView = _binding.Render();
    }

    public (LevelOfDetailContent editor, LevelOfDetailContent binding) Snapshot()
        => (_latestEditorView ?? EmptyContent, _latestBindingView ?? EmptyContent);

    private LevelOfDetailContent? _latestEditorView;
    private LevelOfDetailContent? _latestBindingView;
}

> **首帧初始化**：应用启动时即可调用一次 `UpdateAsync` + `Snapshot()`，把初始快照注入 IApp，避免第一轮 `RenderWindow()` 读到空白内容。
```

> **顺序要求**：始终先更新编辑 Widget，再更新绑定 Widget。同步 Widget 会读取编辑侧的 `ContentChanged` / 指纹信息判断是否需要刷新状态。

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

`RenderWindow()` 不应直接访问 Widget,而是读取第 2 节示例中 `UpdateSnapshot` 预先注入的 `_editorSnapshot` / `_bindingSnapshot`。如果 orchestrator 尚未写入快照,返回 `null` 或空窗口,以避免模型读取到陈旧内容。

---

## 5. 工具集合同步要点

1. **命名空间隔离**：约定 `edit.*` 归编辑 Widget，`sync.*` 归同步 Widget，避免名称冲突。
2. **工具清单校验**：在 `BuildToolCache()` 或 orchestrator 总成阶段对工具名称去重，若检测到重复命名（含大小写变体）应立即抛出诊断，防止后续模型调用落到错误的实现上。
3. **可见性回传**：若任一 Widget 在 Update pass 中改变工具可见性，应在 `UpdateSnapshot` 调用中触发 `_tools` 缓存更新，或让 Widget 暴露 `GetVisibleTools()` 以备 IApp 查询。
4. **调试分类**：
   - 编辑 Widget 使用 `TextEdit.*` 日志分类；
   - 同步 Widget 使用 `Sync.*` 日志分类。
    调试时可在 IApp 中提供快捷指引（例如在 Render 中提示查看对应日志），并在需要关联历史 `TextEdit.Persistence` / `TextEdit.External` 记录时于 `Sync.*` 日志中追加来源标记。

---

## 6. 常见陷阱与建议

| 场景 | 风险 | 建议 |
| --- | --- | --- |
| 并发调用 `UpdateAsync` | 状态竞态，指纹不一致 | 严格在事件循环中顺序执行，禁止多线程同时访问 |
| `RenderWindow()` 直接调用 Widget.Render | 破坏双 Pass 模式，可能出现旧状态 | 始终通过 orchestrator 缓存好的最新快照 |
| IApp.Tools 未合并 | 某些工具缺失，模型无法调用 | 构造工具集合时合并两个 Widget 的可见工具 |
| `RenderWindow()` 返回空字符串 | 模型误判“窗口仍存在但为空” | 如需暂时隐藏窗口请返回 `null`，返回空字符串表示窗口仍可见只是没有正文 |
| 未同步指纹 | 同步 Widget 误判状态 | 确保 `ContentChanged` 事件携带指纹/版本号 |
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

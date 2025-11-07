using System;
using System.Collections.Generic;
using System.Text;
using Atelia.Diagnostics;
using Atelia.Agent.Core;
using Atelia.Agent.Core.Tool;
using Atelia.Agent.Text;

namespace Atelia.Agent.Apps;

public sealed class MemoryNotebookApp : IApp {
    private const string DebugCategory = "MemoryNotebookApp";

    private readonly TextEditorWidget _editor;
    private string _notebookContent = string.Empty;

    public MemoryNotebookApp() {
        _editor = new TextEditorWidget(
            TargetTextName,
            BaseToolName,
            () => _notebookContent,
            ApplyNotebookContent
        );
    }

    public string Name => "MemoryNotebook";

    public string Description => "封装管理 Memory Notebook 状态的 App，提供工具化替换与 Window 渲染能力。";

    public IReadOnlyList<ITool> Tools => _editor.Tools;

    public string? RenderWindow() {
        var builder = new StringBuilder();
        // 为了全项目内统一使用'\n'行尾，有意避免使用AppendLine / Environment.NewLine
        builder.Append("## Memory Notebook\n\n");

        builder.Append(_editor.RenderSnapshot());

        return builder.ToString();
    }

    public void ReplaceNotebookFromHost(string content) {
        _editor.UpdateFromHost(content);
        DebugUtil.Print(DebugCategory, $"[HostUpdate] length={_editor.GetRawSnapshot().Length}");
    }

    public string GetSnapshot() => _editor.GetRawSnapshot();

    private void ApplyNotebookContent(string content) {
        _notebookContent = content;
        DebugUtil.Print(DebugCategory, $"[State] notebook updated length={content.Length}");
    }

    internal const string TargetTextName = "MemoryNotebook";
    internal const string BaseToolName = "memory_notebook";
}

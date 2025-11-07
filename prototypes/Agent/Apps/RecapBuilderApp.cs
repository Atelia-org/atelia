using System;
using System.Collections.Generic;
using System.Text;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Agent.Text;
namespace Atelia.Agent.Apps;

public sealed class RecapBuilderApp : IApp {
    private readonly RecapBuilder _builder;
    private readonly TextEditorWidget _recapEditor;

    public RecapBuilderApp(RecapBuilder builder) {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _recapEditor = new TextEditorWidget(
            TargetTextName,
            BaseToolName,
            () => _builder.RecapText,
            ApplyRecapContent
        );
    }

    public string Name => "RecapBuilder";

    public string Description => "封装管理 Recap 文本编辑的 App，提供工具化替换能力。";

    public IReadOnlyList<ITool> Tools => _recapEditor.Tools;

    public string? RenderWindow() {
        var builder = new StringBuilder();
        // 为了全项目内统一使用'\n'行尾，有意避免使用AppendLine / Environment.NewLine
        builder.Append("## Recap\n\n");

        builder.Append(_recapEditor.RenderSnapshot());

        return builder.ToString();
    }

    private void ApplyRecapContent(string content) {
        _builder.UpdateRecap(content);
    }

    internal const string TargetTextName = "Recap";
    internal const string BaseToolName = "recap";
}

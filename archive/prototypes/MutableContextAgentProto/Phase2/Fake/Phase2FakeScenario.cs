namespace Atelia.MutableContextAgentProto.Phase2.Fake;

public static class Phase2FakeScenario {
    public const string TaskText =
        "请阅读这个 workspace 的说明，并回答：如何使用 WidgetClient 配置自定义 timeout 和 retry policy？请给出一段示例代码。";

    public const string WorkspaceRoot =
        "prototypes/MutableContextAgentProto/fixtures/phase2-widget-workspace";

    public static IReadOnlyList<string> RecommendedFiles { get; } =
    [
        "README.md",
        "src/WidgetClient.cs",
        "src/WidgetOptions.cs",
        "src/WidgetRetryPolicy.cs",
        "src/InternalNotes.cs",
    ];
}

namespace WidgetFixture;

internal static class InternalNotes {
    public const string Owner = "Fixture Team";
    public const string MigrationHint = "The legacy WidgetGateway name was retired.";

    public static IReadOnlyList<string> Backlog { get; } =
    [
        "Consider adding telemetry tags for cache hits.",
        "Do not document the experimental batch endpoint yet.",
        "Retry behavior is configured in WidgetRetryPolicy, not here.",
    ];
}

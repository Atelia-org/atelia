namespace Atelia.MutableContextAgentProto.Phase2.Fake;

public static class FakeSelectionPolicy {
    public static FakeSelectedMemory Select(
        string path,
        string intention,
        Phase2FakeNumberedFileView view
    ) {
        ArgumentNullException.ThrowIfNull(view);

        return Select(path, intention, view.Lines);
    }

    public static FakeSelectedMemory Select(
        string path,
        string intention,
        IReadOnlyList<string> lineTexts
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(intention);
        ArgumentNullException.ThrowIfNull(lineTexts);

        var normalizedPath = path.Replace('\\', '/');
        var ranges = normalizedPath switch {
            "README.md" => ["8-17", "19-35"],
            "src/WidgetClient.cs" => ["8-12", "14-42"],
            "src/WidgetOptions.cs" => ["3-9"],
            "src/WidgetRetryPolicy.cs" => ["3-13", "15-24"],
            _ => SelectByKeywords(lineTexts),
        };

        return new FakeSelectedMemory(
            normalizedPath,
            intention,
            ranges,
            SummaryFor(normalizedPath),
            NotesFor(normalizedPath)
        );
    }

    private static IReadOnlyList<string> SelectByKeywords(IReadOnlyList<string> lineTexts) {
        var selected = new List<string>();

        for (var index = 0; index < lineTexts.Count; index++) {
            var text = lineTexts[index];
            if (text.Contains("WidgetClient", StringComparison.OrdinalIgnoreCase)
                || text.Contains("WidgetOptions", StringComparison.OrdinalIgnoreCase)
                || text.Contains("WidgetRetryPolicy", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                || text.Contains("RetryCount", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Delay", StringComparison.OrdinalIgnoreCase)) {
                selected.Add((index + 1).ToString());
            }
        }

        return selected.Count == 0 ? ["1"] : selected;
    }

    private static string SummaryFor(string normalizedPath) {
        return normalizedPath switch {
            "README.md" => "README gives the task-facing map and a complete sample that constructs WidgetOptions, WidgetRetryPolicy, and WidgetClient.",
            "src/WidgetClient.cs" => "WidgetClient accepts WidgetOptions plus an optional WidgetRetryPolicy, uses options.Timeout for cancellation, and retries when the policy allows.",
            "src/WidgetOptions.cs" => "WidgetOptions exposes Timeout, defaulting to 30 seconds, alongside Endpoint and UserAgent.",
            "src/WidgetRetryPolicy.cs" => "WidgetRetryPolicy exposes RetryCount and Delay, plus helpers used by WidgetClient between attempts.",
            _ => "Selected lines mention WidgetClient timeout or retry-policy configuration.",
        };
    }

    private static string NotesFor(string normalizedPath) {
        return normalizedPath switch {
            "src/InternalNotes.cs" => "This file is mostly distractor material and should not be retained for the target answer.",
            _ => "Keep only the configuration-related snippets needed to answer with example code.",
        };
    }
}

public sealed record Phase2FakeNumberedFileView(
    string Path,
    string Intention,
    IReadOnlyList<string> Lines
);

public sealed record FakeSelectedMemory(
    string Path,
    string Intention,
    IReadOnlyList<string> Ranges,
    string Summary,
    string? Notes
);

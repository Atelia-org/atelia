using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.ChatSession;

public sealed record ChatSessionLegacyUpgradeMarkdownExportOptions(
    bool IncludeWarnings = true
);

public static class ChatSessionLegacyUpgradeMarkdownExporter {
    private const int MinimumFenceLength = 6;

    private static readonly JsonWriterOptions ChangeJsonWriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true
    };

    public static string ExportMarkdown(
        string repoDir,
        string branchName = "main",
        ChatSessionLegacyUpgradeMarkdownExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        string json = ChatSessionLegacyUpgradeExporter.ExportJson(repoDir, branchName);
        return ExportMarkdownFromJson(json, options);
    }

    public static string ExportMarkdownFromJson(
        string upgradeExportJson,
        ChatSessionLegacyUpgradeMarkdownExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(upgradeExportJson);
        options ??= new ChatSessionLegacyUpgradeMarkdownExportOptions();

        using var document = JsonDocument.Parse(upgradeExportJson);
        return ExportMarkdown(document.RootElement, options);
    }

    public static void WriteMarkdownFile(
        string repoDir,
        string outputPath,
        string branchName = "main",
        ChatSessionLegacyUpgradeMarkdownExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        WriteText(outputPath, ExportMarkdown(repoDir, branchName, options));
    }

    public static void WriteMarkdownFileFromJsonFile(
        string inputJsonPath,
        string outputPath,
        ChatSessionLegacyUpgradeMarkdownExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        WriteText(outputPath, ExportMarkdownFromJson(File.ReadAllText(inputJsonPath), options));
    }

    private static string ExportMarkdown(JsonElement root, ChatSessionLegacyUpgradeMarkdownExportOptions options) {
        var builder = new StringBuilder();
        builder.AppendLine("# ChatSession Legacy Upgrade Event Source");
        builder.AppendLine();
        AppendScalar(builder, "schema", GetOptionalString(root, "schema"));
        AppendScalar(builder, "branchName", GetOptionalString(root, "branchName"));
        AppendScalar(builder, "generatedAtUtc", GetOptionalString(root, "generatedAtUtc"));

        var events = root.TryGetProperty("events", out var eventsElement) && eventsElement.ValueKind == JsonValueKind.Array
            ? eventsElement.EnumerateArray().ToArray()
            : throw new InvalidDataException("Upgrade export JSON is missing an events array.");

        AppendScalar(builder, "events", events.Length.ToString(CultureInfo.InvariantCulture));
        if (root.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array) {
            AppendScalar(builder, "timeline", timeline.GetArrayLength().ToString(CultureInfo.InvariantCulture));
        }

        if (options.IncludeWarnings && root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0) {
            builder.AppendLine("- warnings:");
            foreach (var warning in warnings.EnumerateArray()) {
                builder.Append("  - ");
                builder.AppendLine(ToSingleLine(warning.GetString()));
            }
        }

        for (int eventIndex = 0; eventIndex < events.Length; eventIndex++) {
            AppendEvent(builder, events[eventIndex], eventIndex);
        }

        return builder.ToString();
    }

    private static void AppendEvent(StringBuilder builder, JsonElement replayEvent, int eventIndex) {
        int ordinal = GetOptionalInt32(replayEvent, "ordinal") ?? eventIndex;
        string kind = GetOptionalString(replayEvent, "kind") ?? "unknown";
        string? commit = GetOptionalString(replayEvent, "commit");
        string changeJson = FormatChangeJson(replayEvent);
        string fence = CreateFence(changeJson);

        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"## {ordinal:D5} {kind}");
        if (!string.IsNullOrWhiteSpace(commit)) {
            builder.Append(' ');
            builder.Append(commit);
        }
        builder.AppendLine();
        AppendScalar(builder, "ordinal", ordinal.ToString(CultureInfo.InvariantCulture));
        AppendScalar(builder, "kind", kind);
        AppendScalar(builder, "commit", commit);
        if (replayEvent.TryGetProperty("commitMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object) {
            AppendScalar(builder, "commitKind", GetOptionalString(metadata, "commitKind"));
            AppendScalar(builder, "commitReason", GetOptionalString(metadata, "commitReason"));
            AppendScalar(builder, "metadataSource", GetOptionalString(metadata, "metadataSource"));
        }

        builder.AppendLine();
        builder.Append(fence);
        builder.AppendLine("json");
        builder.AppendLine(changeJson);
        builder.AppendLine(fence);
    }

    private static string FormatChangeJson(JsonElement replayEvent) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, ChangeJsonWriterOptions)) {
            writer.WriteStartObject();
            foreach (var property in replayEvent.EnumerateObject()) {
                if (IsEventMetadataProperty(property.Name) || property.Value.ValueKind == JsonValueKind.Null) { continue; }

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsEventMetadataProperty(string propertyName)
        => propertyName is "ordinal" or "commit" or "kind" or "commitMetadata";

    private static string CreateFence(string content) {
        int longestRun = 0;
        int currentRun = 0;
        foreach (char ch in content) {
            if (ch == '~') {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else {
                currentRun = 0;
            }
        }

        return new string('~', Math.Max(MinimumFenceLength, longestRun + 1));
    }

    private static void AppendScalar(StringBuilder builder, string name, string? value) {
        if (string.IsNullOrWhiteSpace(value)) { return; }

        builder.Append("- ");
        builder.Append(name);
        builder.Append(": ");
        builder.AppendLine(ToSingleLine(value));
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static string ToSingleLine(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Replace('\r', ' ').Replace('\n', ' ');

    private static void WriteText(string outputPath, string text) {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        File.WriteAllText(outputPath, text);
    }
}

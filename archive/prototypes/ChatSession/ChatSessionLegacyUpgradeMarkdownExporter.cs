using System.Text;
using System.Text.Json;

namespace Atelia.ChatSession;

public sealed record ChatSessionLegacyUpgradeMarkdownExportOptions(
    bool IncludeWarnings = true
);

public static class ChatSessionLegacyUpgradeMarkdownExporter {
    private const int MinimumFenceLength = 6;

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
        var events = root.TryGetProperty("events", out var eventsElement) && eventsElement.ValueKind == JsonValueKind.Array
            ? eventsElement.EnumerateArray().ToArray()
            : throw new InvalidDataException("Upgrade export JSON is missing an events array.");

        if (options.IncludeWarnings && root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0) {
            foreach (var warning in warnings.EnumerateArray()) {
                AppendFence(builder, "warning", warning.GetString());
            }
        }

        foreach (var replayEvent in events) {
            AppendEvent(builder, replayEvent);
        }

        return builder.ToString();
    }

    private static void AppendEvent(StringBuilder builder, JsonElement replayEvent) {
        string kind = GetOptionalString(replayEvent, "kind") ?? "unknown";

        switch (kind) {
            case "initial-state":
                if (replayEvent.TryGetProperty("root", out var root) && root.ValueKind == JsonValueKind.Object) {
                    AppendFence(builder, "system-prompt", GetOptionalString(root, "systemPrompt"));
                }
                AppendMessages(builder, replayEvent, "messages");
                break;
            case "model-turn":
                AppendMessages(builder, replayEvent, "appendedMessages");
                break;
            case "compaction":
                if (replayEvent.TryGetProperty("recapMessage", out var recapMessage) && recapMessage.ValueKind == JsonValueKind.Object) {
                    AppendFence(builder, "recap", GetOptionalString(recapMessage, "content"));
                }
                break;
            case "update-system-prompt":
                if (replayEvent.TryGetProperty("systemPromptChange", out var change) && change.ValueKind == JsonValueKind.Object) {
                    AppendFence(builder, "system-prompt", GetOptionalString(change, "newSystemPrompt"));
                }
                break;
        }
    }

    private static void AppendMessages(StringBuilder builder, JsonElement replayEvent, string propertyName) {
        if (!replayEvent.TryGetProperty(propertyName, out var messages) || messages.ValueKind != JsonValueKind.Array) { return; }

        foreach (var message in messages.EnumerateArray()) {
            AppendMessage(builder, message);
        }
    }

    private static void AppendMessage(StringBuilder builder, JsonElement message) {
        string kind = GetOptionalString(message, "kind") ?? "unknown";
        switch (kind) {
            case "observation":
                AppendFence(builder, "observation", GetOptionalString(message, "content"));
                break;
            case "action":
                if (message.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object) {
                    AppendFence(builder, "action", GetOptionalString(action, "flattenedText"));
                }
                break;
            case "recap":
                AppendFence(builder, "recap", GetOptionalString(message, "content"));
                break;
            case "tool-results":
                AppendFence(builder, "tool-results", GetOptionalString(message, "content"));
                break;
            case "context-header":
                if (message.TryGetProperty("contextHeader", out var contextHeader) && contextHeader.ValueKind == JsonValueKind.Object) {
                    AppendFence(builder, "context-header", GetOptionalString(contextHeader, "systemPromptFragment"));
                    AppendFence(builder, "observation", GetOptionalString(contextHeader, "userMessage"));
                    if (contextHeader.TryGetProperty("assistantMessage", out var assistantMessage) && assistantMessage.ValueKind == JsonValueKind.Object) {
                        AppendFence(builder, "action", GetOptionalString(assistantMessage, "flattenedText"));
                    }
                }
                break;
        }
    }

    private static void AppendFence(StringBuilder builder, string label, string? content) {
        if (string.IsNullOrEmpty(content)) { return; }

        string normalizedContent = NormalizeLineEndings(content);
        string fence = CreateFence(normalizedContent);
        if (builder.Length > 0) { builder.AppendLine(); }

        builder.Append(fence);
        builder.AppendLine(label);
        builder.Append(normalizedContent);
        if (!normalizedContent.EndsWith('\n')) { builder.AppendLine(); }
        builder.AppendLine(fence);
    }

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

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static void WriteText(string outputPath, string text) {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        File.WriteAllText(outputPath, text);
    }
}

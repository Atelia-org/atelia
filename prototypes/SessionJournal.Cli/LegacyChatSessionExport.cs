using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.Cli;

internal static class LegacyChatSessionExportSchema {
    public const string SchemaId = "atelia.chat-session.legacy-upgrade-export.v1";
}

internal static class LegacyChatSessionEventKinds {
    public const string InitialState = "initial-state";
    public const string ModelTurn = "model-turn";
    public const string Compaction = "compaction";
    public const string UpdateSystemPrompt = "update-system-prompt";
    public const string RedundantSave = "redundant-save";
}

internal sealed record LegacyChatSessionExport {
    public string? Schema { get; init; }
    public string? BranchName { get; init; }
    public IReadOnlyList<LegacyChatSessionEvent> Events { get; init; } = [];
}

internal sealed record LegacyChatSessionEvent {
    public int Ordinal { get; init; }
    public string? Commit { get; init; }
    public string Kind { get; init; } = string.Empty;
    public LegacyChatSessionRoot? Root { get; init; }
    public IReadOnlyList<LegacyChatSessionMessage>? Messages { get; init; }
    public IReadOnlyList<LegacyChatSessionMessage>? AppendedMessages { get; init; }
    public LegacyChatSessionMessage? RecapMessage { get; init; }
    public LegacyChatSessionSystemPromptChange? SystemPromptChange { get; init; }
}

internal sealed record LegacyChatSessionExportDocument(
    LegacyChatSessionExport Export,
    long InputByteCount,
    string InputSha256
);

internal sealed record LegacyChatSessionRoot {
    public string? ApiSpecId { get; init; }
    public string? CompletionSurfaceId { get; init; }
    public string? ModelId { get; init; }
    public string? SystemPrompt { get; init; }
}

internal sealed record LegacyChatSessionMessage {
    public string Kind { get; init; } = string.Empty;
    public string? Content { get; init; }
    public LegacyChatSessionAction? Action { get; init; }
}

internal sealed record LegacyChatSessionAction {
    public IReadOnlyList<SerializedActionBlock> Blocks { get; init; } = [];
}

internal sealed record LegacyChatSessionSystemPromptChange {
    public string? NewSystemPrompt { get; init; }
}

internal static class LegacyChatSessionExportReader {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static LegacyChatSessionExport Read(string inputJsonPath) {
        return ReadDocument(inputJsonPath).Export;
    }

    public static LegacyChatSessionExportDocument ReadDocument(
        string inputJsonPath
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJsonPath);
        byte[] bytes = File.ReadAllBytes(inputJsonPath);
        LegacyChatSessionExport export =
            JsonSerializer.Deserialize<LegacyChatSessionExport>(
                bytes,
                JsonOptions
            ) ?? throw new InvalidDataException(
                "Legacy ChatSession export JSON is empty."
            );
        if (!string.Equals(
                export.Schema,
                LegacyChatSessionExportSchema.SchemaId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Unsupported legacy ChatSession export schema '{export.Schema}'."
            );
        }
        if (export.Events.Count == 0) {
            throw new InvalidDataException(
                "Legacy ChatSession export has no events."
            );
        }
        return new LegacyChatSessionExportDocument(
            export,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant()
        );
    }
}

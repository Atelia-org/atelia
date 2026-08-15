using System.Text.Json;
using System.Text.Json.Serialization;
using static Atelia.ChatSession.ChatSessionStorageSchema;

namespace Atelia.ChatSession;

public enum ChatSessionCommitKind {
    InitialState,
    ModelTurn,
    Compaction,
    RevertTurn,
    UpdateSystemPrompt,
    UpdateContextHeader,
    RedundantSave,
}

public sealed record ChatSessionExplicitCommitMetadata(
    ChatSessionCommitKind Kind,
    string? Reason,
    long? ChatSessionSchemaVersion
);

public static class ChatSessionCommitMetadata {
    public const string SchemaId = "atelia.chat-session.commit-note.v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string EncodeNote(ChatSessionCommitKind kind, string? reason) {
        var dto = new CommitNoteDto {
            Schema = SchemaId,
            CommitKind = FormatKind(kind),
            CommitReason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            ChatSessionSchemaVersion = SchemaVersion,
        };

        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static bool TryDecodeNote(string? note, out ChatSessionExplicitCommitMetadata metadata) {
        metadata = null!;
        if (string.IsNullOrWhiteSpace(note)) { return false; }

        CommitNoteDto? dto;
        try {
            dto = JsonSerializer.Deserialize<CommitNoteDto>(note, JsonOptions);
        }
        catch (JsonException) {
            return false;
        }

        if (dto is null || !string.Equals(dto.Schema, SchemaId, StringComparison.Ordinal)) { return false; }
        if (!TryParseKind(dto.CommitKind, out var kind)) { return false; }

        metadata = new ChatSessionExplicitCommitMetadata(kind, dto.CommitReason, dto.ChatSessionSchemaVersion);
        return true;
    }

    public static string FormatKind(ChatSessionCommitKind kind)
        => kind switch {
            ChatSessionCommitKind.InitialState => "initial-state",
            ChatSessionCommitKind.ModelTurn => "model-turn",
            ChatSessionCommitKind.Compaction => "compaction",
            ChatSessionCommitKind.RevertTurn => "revert-turn",
            ChatSessionCommitKind.UpdateSystemPrompt => "update-system-prompt",
            ChatSessionCommitKind.UpdateContextHeader => "update-context-header",
            ChatSessionCommitKind.RedundantSave => "redundant-save",
            _ => kind.ToString()
        };

    public static bool TryParseKind(string? text, out ChatSessionCommitKind kind) {
        switch (text) {
            case "initial-state":
                kind = ChatSessionCommitKind.InitialState;
                return true;
            case "model-turn":
                kind = ChatSessionCommitKind.ModelTurn;
                return true;
            case "compaction":
                kind = ChatSessionCommitKind.Compaction;
                return true;
            case "revert-turn":
                kind = ChatSessionCommitKind.RevertTurn;
                return true;
            case "update-system-prompt":
                kind = ChatSessionCommitKind.UpdateSystemPrompt;
                return true;
            case "update-context-header":
                kind = ChatSessionCommitKind.UpdateContextHeader;
                return true;
            case "redundant-save":
                kind = ChatSessionCommitKind.RedundantSave;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private sealed class CommitNoteDto {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("commitKind")]
        public string? CommitKind { get; set; }

        [JsonPropertyName("commitReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CommitReason { get; set; }

        [JsonPropertyName("chatSessionSchemaVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ChatSessionSchemaVersion { get; set; }
    }
}

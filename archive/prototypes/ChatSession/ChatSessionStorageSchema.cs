using Atelia.StateJournal;

namespace Atelia.ChatSession;

internal static class ChatSessionStorageSchema {
    public const string RootKind = "chat-session";
    public const long SchemaVersion = 2L;

    public const string KeyKind = "kind";
    public const string KeySchemaVersion = "schemaVersion";
    public const string KeyApiSpecId = "apiSpecId";
    public const string KeyCompletionSurfaceId = "completionSurfaceId";
    public const string KeyModelId = "modelId";
    public const string KeySystemPrompt = "systemPrompt";
    public const string KeyMessages = "messages";

    public static void ValidateRoot(DurableDict<string> root) {
        if (root.Get<string>(KeyKind, out var kind) != GetIssue.None || kind != RootKind) { throw new InvalidDataException("Root is not a chat-session."); }

        if (root.Get<long>(KeySchemaVersion, out var version) != GetIssue.None || version != SchemaVersion) { throw new InvalidDataException($"Unsupported schema version. Expected {SchemaVersion}."); }
    }

    public static DurableDeque GetMessages(DurableDict<string> root) {
        if (!root.TryGet<DurableDeque>(KeyMessages, out var messages) || messages is null) { throw new InvalidDataException("Root is missing messages deque."); }

        return messages;
    }
}

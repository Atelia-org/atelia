using Atelia.Completion.Abstractions;

namespace Atelia.PersistentAgentProto;

internal interface IPersistentChatSession : IDisposable {
    string SystemPrompt { get; }
    int MessageCount { get; }
    void AppendUser(string content);
    void AppendAssistant(string content);
    IReadOnlyList<IHistoryMessage> BuildContext();
    IEnumerable<(string Role, string Content, long TimestampMs)> EnumerateMessages();
}

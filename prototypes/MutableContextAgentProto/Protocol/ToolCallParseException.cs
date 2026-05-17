namespace Atelia.MutableContextAgentProto.Protocol;

public sealed class ToolCallParseException : Exception {
    public ToolCallParseException(string message, string rawText, Exception? innerException = null)
        : base(message, innerException) {
        RawText = rawText;
    }

    public string RawText { get; }
}

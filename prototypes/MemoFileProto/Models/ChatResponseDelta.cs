namespace MemoFileProto.Models;

public class ChatResponseDelta {
    public string? Content { get; set; }

    public List<ToolCall>? ToolCalls { get; set; }

    public string? FinishReason { get; set; }
}

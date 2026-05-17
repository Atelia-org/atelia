namespace Atelia.MutableContextAgentProto.Core;

public enum EventLogEntryKind {
    Note,
    UserInput,
    ModelOutput,
    ToolCall,
    ToolResult,
    FileRead,
    FileWrite,
    Error,
}

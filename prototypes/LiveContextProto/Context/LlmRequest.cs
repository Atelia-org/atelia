using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.LiveContextProto.Context;

internal sealed record LlmRequest(
    string ModelId,
    string SystemInstruction,
    IReadOnlyList<IContextMessage> Context,
    ImmutableArray<ToolDefinition> Tools
);

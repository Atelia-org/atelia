using Atelia.ChatSession;

namespace Atelia.ChatSession.Memory;

public static class RolePlayMemoryBlockPaths {
    public const string WorldUnderstandingBlockKey = "roleplay.world-understanding";
    public const string FirstPersonAutobiographyBlockKey = "roleplay.first-person-autobiography";

    public static MemoryPackBlockPath WorldUnderstanding { get; } = new(
        MemoryPackCarrier.Observation,
        WorldUnderstandingBlockKey
    );

    public static MemoryPackBlockPath FirstPersonAutobiography { get; } = new(
        MemoryPackCarrier.Action,
        FirstPersonAutobiographyBlockKey
    );
}

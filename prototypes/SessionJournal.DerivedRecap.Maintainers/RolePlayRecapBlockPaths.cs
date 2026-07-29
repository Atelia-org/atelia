using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public static class RolePlayRecapBlockPaths {
    public const string WorldUnderstandingBlockKey = "roleplay.world-understanding";
    public const string FirstPersonAutobiographyBlockKey = "roleplay.first-person-autobiography";

    public static ContextHeaderBlockPath WorldUnderstanding { get; } = new(
        ContextHeaderCarrier.Observation,
        WorldUnderstandingBlockKey
    );

    public static ContextHeaderBlockPath FirstPersonAutobiography { get; } = new(
        ContextHeaderCarrier.Action,
        FirstPersonAutobiographyBlockKey
    );
}

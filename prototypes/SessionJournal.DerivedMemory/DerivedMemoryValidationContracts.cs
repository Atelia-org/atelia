namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Successful strict validation summary. Validation throws <see cref="InvalidDataException"/>
/// for malformed, inconsistent, forked, incomplete, or stale derived-memory state.
/// </summary>
public sealed record DerivedMemoryValidationReport(
    int ArtifactCount,
    int ArtifactSetCount,
    int LatestPointerCount,
    int ExactArtifactSetKeyCount
);

namespace Atelia.StateJournal;

public record struct GlobalId(EpochId EpochId, LocalId LocalId);

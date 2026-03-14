namespace Atelia.StateJournal;

public record struct GlobalId(CommitId CommitId, LocalId LocalId);

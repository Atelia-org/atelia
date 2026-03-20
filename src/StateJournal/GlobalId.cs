namespace Atelia.StateJournal;

public record struct GlobalId(CommitTicket CommitTicket, LocalId LocalId);

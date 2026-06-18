namespace Atelia.StateJournal;

/// <summary>
/// Exposes the ability to fork a durable object's committed state into a new mutable object.
/// </summary>
public interface ICommittedMutableForkable<out TFork>
    where TFork : DurableObject {
    TFork ForkCommittedAsMutable();
}

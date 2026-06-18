namespace Atelia.StateJournal;

/// <summary>
/// Exposes the in-memory fast path for forking a durable object's committed state into a new mutable object.
/// This is an instance-level optimization path, distinct from Repository-level replay/load based committed cloning.
/// </summary>
public interface ICommittedMutableForkable<out TFork>
    where TFork : DurableObject {
    TFork ForkCommittedAsMutable();
}

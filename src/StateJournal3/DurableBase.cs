namespace Atelia.StateJournal3;

public abstract class DurableBase : IEquatable<DurableBase> {
    public abstract Type ContentType { get; }
    public abstract bool Equals(DurableBase? other);
}

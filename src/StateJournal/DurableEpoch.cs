namespace Atelia.StateJournal;

/// <summary>一个内存对象图状态快照</summary>
public class DurableEpoch {
    public EpochId ParentId { get; }
    public EpochId Id { get; }
    public DurableRepo Repo { get; }
    public AteliaResult<DurableObject> Load(LocalId id) {
        throw new NotImplementedException();
    }

    internal void Materialize(DurableObject durableObject) {
        throw new NotImplementedException();
    }

    internal void RegisterDirty(DurableObject durableObject) {
        throw new NotImplementedException();
    }
}

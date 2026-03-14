namespace Atelia.StateJournal;

/// <summary>一个内存对象图状态快照</summary>
public class Revision {
    public CommitId ParentId { get; }
    public CommitId Id { get; }
    public Repository Repo { get; }
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

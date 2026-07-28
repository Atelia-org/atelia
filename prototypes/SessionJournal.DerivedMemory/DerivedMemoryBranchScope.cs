using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed class DerivedMemoryBranchScope {
    internal DerivedMemoryBranchScope(
        DerivedMemoryRepository repository,
        string branchName,
        RefId branchRefId
    ) {
        Repository = repository;
        BranchName = branchName;
        BranchRefId = branchRefId;
    }

    internal DerivedMemoryRepository Repository { get; }

    public string BranchName { get; }

    public RefId BranchRefId { get; }
}

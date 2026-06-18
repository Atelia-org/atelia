using Atelia.Rbf;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public sealed partial class Repository {
    /// <summary>
    /// 基于 <paramref name="source"/> 当前 committed snapshot 重新 replay/load，返回一个绑定到同一 Revision 的新对象身份。
    /// 此能力不复制 source 的未提交 working changes。
    /// </summary>
    public AteliaResult<TDurable> ReplayCommitted<TDurable>(
        TDurable source,
        LoadMaterializationMode materializationMode
    )
        where TDurable : DurableObject {
        using var scope = _gate.EnterScope();
        if (!EnsureUsable(out var err)) { return err; }
        ArgumentNullException.ThrowIfNull(source);

        var revision = source.BoundRevision;
        if (revision is null) {
            return new SjRepositoryError(
                "The specified source is not bound to any Revision.",
                RecoveryHint: "Create or load the object from a Revision managed by this Repository before replaying."
            );
        }

        var branchName = revision.BranchName;
        if (branchName is null || !_branches.TryGetValue(branchName, out var branchState) || !ReferenceEquals(branchState.LoadedRevision, revision)) {
            return new SjRepositoryError(
                "The specified source does not belong to a loaded Revision managed by this Repository.",
                RecoveryHint: "Checkout the owning branch through this Repository and replay from that loaded Revision."
            );
        }

        try {
            if (revision.HeadSegmentNumber == _segments.ActiveSegmentNumber) { return revision.ReplayCommittedCore(source, _segments.ActiveFile, materializationMode); }

            using var historicalFile = _segments.OpenHistoricalFile(revision.HeadSegmentNumber);
            return revision.ReplayCommittedCore(source, historicalFile, materializationMode);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) {
            return new SjRepositoryError(
                $"Failed to replay committed state for LocalId={source.LocalId.Value}: {ex.Message}",
                RecoveryHint: "Check that the referenced segment file still exists and can be opened."
            );
        }
    }
}

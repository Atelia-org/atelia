using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public interface IRecapBlockMaintainerRegistry {
    bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        out IRecapBlockMaintainer maintainer
    );
}

public sealed class RecapBlockMaintainerRegistry
    : IRecapBlockMaintainerRegistry {
    private readonly IReadOnlyDictionary<
        (string Id, ContextHeaderBlockPath Target),
        IRecapBlockMaintainer
    > _maintainers;

    public RecapBlockMaintainerRegistry(
        IReadOnlyList<IRecapBlockMaintainer> maintainers
    ) {
        ArgumentNullException.ThrowIfNull(maintainers);
        var index = new Dictionary<
            (string Id, ContextHeaderBlockPath Target),
            IRecapBlockMaintainer
        >();
        foreach (IRecapBlockMaintainer? maintainer in maintainers) {
            ArgumentNullException.ThrowIfNull(maintainer);
            if (string.IsNullOrWhiteSpace(maintainer.Id)
                || maintainer.Target is null) {
                throw new ArgumentException(
                    "Maintainer Id and Target must be present.",
                    nameof(maintainers)
                );
            }
            if (!index.TryAdd(
                    (maintainer.Id, maintainer.Target),
                    maintainer
                )) {
                throw new ArgumentException(
                    "Maintainer registry contains a duplicate "
                    + $"('{maintainer.Id}', '{maintainer.Target}').",
                    nameof(maintainers)
                );
            }
        }
        _maintainers = index;
    }

    public bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        out IRecapBlockMaintainer maintainer
    ) => _maintainers.TryGetValue(
        (maintainerId, target),
        out maintainer!
    );
}

public sealed record DerivedRecapExecutionDefect(
    string Code,
    string Detail
);

public abstract record DerivedRecapExecutionResult {
    private DerivedRecapExecutionResult() {
    }

    public sealed record NoBuild(string Reason)
        : DerivedRecapExecutionResult;

    public sealed record Published(PublishedRecapDescriptor Descriptor)
        : DerivedRecapExecutionResult;

    public sealed record Unavailable(
        IReadOnlyList<DerivedRecapExecutionDefect> Defects
    ) : DerivedRecapExecutionResult;

    public sealed record Retryable(string Code, string Detail)
        : DerivedRecapExecutionResult;

    public sealed record BlockFailed(
        EventAddress SetAdmissionAnchor,
        RecapBlockId RecapBlockId,
        string Code,
        string Detail
    ) : DerivedRecapExecutionResult;
}

public static class DerivedRecapExecutionDefectCodes {
    public const string StoreUnavailable = nameof(StoreUnavailable);
    public const string PublishedSourceUnavailable =
        nameof(PublishedSourceUnavailable);
    public const string BuildingInvalid = nameof(BuildingInvalid);
    public const string ManifestConfigMismatch =
        nameof(ManifestConfigMismatch);
    public const string RawPlanningUnavailable =
        nameof(RawPlanningUnavailable);
    public const string RawHeadChanged = nameof(RawHeadChanged);
    public const string SourceChanged = nameof(SourceChanged);
    public const string BuildingRace = nameof(BuildingRace);
    public const string ConcurrentBuildingChange =
        nameof(ConcurrentBuildingChange);
    public const string MaintainerFailed = nameof(MaintainerFailed);
    public const string MaintainerResultInvalid =
        nameof(MaintainerResultInvalid);
    public const string PublicationUnavailable =
        nameof(PublicationUnavailable);
}

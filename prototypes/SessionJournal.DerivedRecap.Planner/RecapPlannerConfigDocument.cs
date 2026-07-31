using System.Collections.Immutable;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapCadenceConfigDocument(
    string HistoryUnitLoadEstimatorId,
    long MinimumRecentHistoryLoad,
    long RecapBuildIntervalHistoryLoad
);

public sealed record RecapPlannerCatalogEntryDocument(
    string MaintainerProfile,
    int MaxContentUtf8Bytes
);

public sealed record RecapPlannerLimitsDocument(
    int MaxRawGrowthEventCount,
    int MaxRouteEndpointsPerBlock,
    int MaxMaintainerCallsPerBuild,
    int MaxRawEventsPerStep,
    int MaxRawEventsPerBuild
);

public sealed record RecapPlannerConfigDocument {
    private IReadOnlyList<RecapPlannerCatalogEntryDocument> _catalog =
        null!;

    public RecapPlannerConfigDocument(
        string schema,
        string planningPolicy,
        RecapCadenceConfigDocument cadence,
        IReadOnlyList<RecapPlannerCatalogEntryDocument> catalog,
        RecapPlannerLimitsDocument limits
    ) {
        Schema = schema;
        PlanningPolicy = planningPolicy;
        Cadence = cadence;
        Catalog = catalog;
        Limits = limits;
    }

    public string Schema { get; init; }
    public string PlanningPolicy { get; init; }
    public RecapCadenceConfigDocument Cadence { get; init; }
    public IReadOnlyList<RecapPlannerCatalogEntryDocument> Catalog {
        get => _catalog;
        init => _catalog = value is null
            ? null!
            : Array.AsReadOnly([.. value]);
    }
    public RecapPlannerLimitsDocument Limits { get; init; }
}

public sealed record RecapPlannerConfigDefect(
    string Code,
    string Detail
);

public static class RecapPlannerConfigDefectCodes {
    public const string UnsupportedSchema = nameof(UnsupportedSchema);
    public const string Malformed = nameof(Malformed);
    public const string SizeLimitExceeded =
        nameof(SizeLimitExceeded);
    public const string DuplicateProfileName =
        nameof(DuplicateProfileName);
    public const string InvalidCatalog = nameof(InvalidCatalog);
    public const string InvalidLimit = nameof(InvalidLimit);
    public const string UnsafePath = nameof(UnsafePath);
}

public abstract record RecapPlannerConfigDecodeResult {
    private RecapPlannerConfigDecodeResult() {
    }

    public sealed record Valid(
        RecapPlannerConfigDocument Document,
        ImmutableArray<byte> CanonicalBytes,
        string ConfigSha256
    ) : RecapPlannerConfigDecodeResult;

    public sealed record Invalid(
        IReadOnlyList<RecapPlannerConfigDefect> Defects
    ) : RecapPlannerConfigDecodeResult;
}

public abstract record RecapPlannerConfigLoadResult {
    private RecapPlannerConfigLoadResult() {
    }

    public sealed record Available(
        string Path,
        RecapPlannerConfigDocument Document,
        ImmutableArray<byte> CanonicalBytes,
        string ConfigSha256
    ) : RecapPlannerConfigLoadResult;

    public sealed record Missing(string Path)
        : RecapPlannerConfigLoadResult;

    public sealed record Invalid(
        string Path,
        IReadOnlyList<RecapPlannerConfigDefect> Defects
    ) : RecapPlannerConfigLoadResult;

    public sealed record Unavailable(string Path, string Reason)
        : RecapPlannerConfigLoadResult;
}

public abstract record RecapPlannerConfigInitializeResult {
    private RecapPlannerConfigInitializeResult() {
    }

    public sealed record Initialized(
        string Path,
        string ConfigSha256
    ) : RecapPlannerConfigInitializeResult;

    public sealed record AlreadyExists(string Path)
        : RecapPlannerConfigInitializeResult;

    public sealed record Invalid(
        string Path,
        IReadOnlyList<RecapPlannerConfigDefect> Defects
    ) : RecapPlannerConfigInitializeResult;

    public sealed record Unavailable(string Path, string Reason)
        : RecapPlannerConfigInitializeResult;
}

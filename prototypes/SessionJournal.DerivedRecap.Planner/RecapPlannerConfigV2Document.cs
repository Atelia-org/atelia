using System.Collections.Immutable;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapCadenceConfigV2Document(
    string HistoryUnitLoadEstimatorId,
    long MinimumRecentHistoryLoad,
    long RecapBuildIntervalHistoryLoad
);

/// <summary>
/// Inactive H1a wire contract for the HistoryLoad-based planner config.
/// Production loading and composition remain V1-only until H1c.
/// </summary>
public sealed record RecapPlannerConfigV2Document {
    private IReadOnlyList<RecapPlannerCatalogEntryDocument> _catalog =
        null!;

    public RecapPlannerConfigV2Document(
        string schema,
        string planningPolicy,
        RecapCadenceConfigV2Document cadence,
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
    public RecapCadenceConfigV2Document Cadence { get; init; }
    public IReadOnlyList<RecapPlannerCatalogEntryDocument> Catalog {
        get => _catalog;
        init => _catalog = value is null
            ? null!
            : Array.AsReadOnly([.. value]);
    }
    public RecapPlannerLimitsDocument Limits { get; init; }
}

public abstract record RecapPlannerConfigV2DecodeResult {
    private RecapPlannerConfigV2DecodeResult() {
    }

    public sealed record Valid(
        RecapPlannerConfigV2Document Document,
        ImmutableArray<byte> CanonicalBytes,
        string ConfigSha256
    ) : RecapPlannerConfigV2DecodeResult;

    public sealed record Invalid(
        IReadOnlyList<RecapPlannerConfigDefect> Defects
    ) : RecapPlannerConfigV2DecodeResult;
}

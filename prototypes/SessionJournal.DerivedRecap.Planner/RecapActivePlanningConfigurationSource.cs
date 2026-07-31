namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapActivePlanningConfigurationDefect(
    string Code,
    string Detail
);

public abstract record RecapActivePlanningConfigurationLoadResult {
    private RecapActivePlanningConfigurationLoadResult() {
    }

    public sealed record Available
        : RecapActivePlanningConfigurationLoadResult {
        public Available(
            ResolvedRecapPlanningConfiguration configuration
        ) {
            Configuration = configuration
                ?? throw new ArgumentNullException(
                    nameof(configuration)
                );
        }

        public ResolvedRecapPlanningConfiguration Configuration {
            get;
        }
    }

    public sealed record Missing
        : RecapActivePlanningConfigurationLoadResult {
        public Missing(string path) {
            Path = RequirePath(path, nameof(path));
        }

        public string Path { get; }
    }

    public sealed record Invalid
        : RecapActivePlanningConfigurationLoadResult {
        public Invalid(
            string path,
            IReadOnlyList<RecapActivePlanningConfigurationDefect>
                defects,
            RecapPlannerConfigSnapshot? snapshot = null
        ) {
            Path = RequirePath(path, nameof(path));
            Defects = CopyDefects(defects, nameof(defects));
            Snapshot = snapshot;
        }

        public string Path { get; }
        public IReadOnlyList<RecapActivePlanningConfigurationDefect>
            Defects { get; }
        public RecapPlannerConfigSnapshot? Snapshot { get; }
    }

    public sealed record Unavailable
        : RecapActivePlanningConfigurationLoadResult {
        public Unavailable(
            string path,
            string reason,
            RecapPlannerConfigSnapshot? snapshot = null
        ) {
            Path = RequirePath(path, nameof(path));
            Reason = string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException(
                    "Unavailable reason cannot be empty.",
                    nameof(reason)
                )
                : reason;
            Snapshot = snapshot;
        }

        public string Path { get; }
        public string Reason { get; }
        public RecapPlannerConfigSnapshot? Snapshot { get; }
    }

    private static string RequirePath(string path, string parameterName)
        => string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException(
                "Planner config path cannot be empty.",
                parameterName
            )
            : path;

    private static IReadOnlyList<
        RecapActivePlanningConfigurationDefect
    > CopyDefects(
        IReadOnlyList<RecapActivePlanningConfigurationDefect> defects,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(defects, parameterName);
        if (defects.Count == 0
            || defects.Any(static defect => defect is null)) {
            throw new ArgumentException(
                "Invalid config result requires non-null defects.",
                parameterName
            );
        }
        return Array.AsReadOnly([.. defects]);
    }
}

/// <summary>
/// Lazy active-config seam. Hosts call it only after current-lineage Building
/// selection proves that new planning is required.
/// </summary>
public interface IRecapActivePlanningConfigurationSource {
    RecapActivePlanningConfigurationLoadResult Load();
}

/// <summary>
/// Construction-zero-touch repository source that stitches the public config
/// loader to the pure resolver. The repository is not read until Load().
/// </summary>
public sealed class RepositoryRecapActivePlanningConfigurationSource
    : IRecapActivePlanningConfigurationSource {
    private readonly string _repositoryRoot;
    private readonly RecapPlannerConfigResolutionCatalog
        _resolutionCatalog;
    private readonly RecapMaintainerCapabilitySnapshot _capabilities;

    public RepositoryRecapActivePlanningConfigurationSource(
        string repositoryRoot,
        RecapMaintainerCapabilitySnapshot capabilities
    ) : this(
        repositoryRoot,
        RecapPlannerConfigResolutionCatalog.BuiltIn,
        capabilities
    ) {
    }

    public RepositoryRecapActivePlanningConfigurationSource(
        string repositoryRoot,
        RecapPlannerConfigResolutionCatalog resolutionCatalog,
        RecapMaintainerCapabilitySnapshot capabilities
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _resolutionCatalog = resolutionCatalog
            ?? throw new ArgumentNullException(
                nameof(resolutionCatalog)
            );
        _capabilities = capabilities
            ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public RecapActivePlanningConfigurationLoadResult Load() {
        RecapPlannerConfigLoadResult loaded =
            RecapPlannerConfigLoader.Load(_repositoryRoot);
        return loaded switch {
            RecapPlannerConfigLoadResult.Available available =>
                Resolve(available),
            RecapPlannerConfigLoadResult.Missing missing =>
                new RecapActivePlanningConfigurationLoadResult.Missing(
                    missing.Path
                ),
            RecapPlannerConfigLoadResult.Invalid invalid =>
                new RecapActivePlanningConfigurationLoadResult.Invalid(
                    invalid.Path,
                    Map(invalid.Defects)
                ),
            RecapPlannerConfigLoadResult.Unavailable unavailable =>
                new RecapActivePlanningConfigurationLoadResult
                    .Unavailable(
                        unavailable.Path,
                        unavailable.Reason
                    ),
            _ => throw new InvalidDataException(
                "Unknown planner config load result."
            )
        };
    }

    private RecapActivePlanningConfigurationLoadResult Resolve(
        RecapPlannerConfigLoadResult.Available available
    ) {
        RecapPlannerConfigSnapshot snapshot =
            RecapPlannerConfigSnapshot.FromAvailable(available);
        RecapPlannerConfigResolveResult resolved =
            RecapPlannerConfigResolver.Resolve(
                snapshot,
                _resolutionCatalog,
                _capabilities
            );
        return resolved switch {
            RecapPlannerConfigResolveResult.Resolved success =>
                new RecapActivePlanningConfigurationLoadResult.Available(
                    success.Configuration
                ),
            RecapPlannerConfigResolveResult.Invalid invalid =>
                new RecapActivePlanningConfigurationLoadResult.Invalid(
                    available.Path,
                    Map(invalid.Defects),
                    snapshot
                ),
            _ => throw new InvalidDataException(
                "Unknown planner config resolve result."
            )
        };
    }

    private static IReadOnlyList<
        RecapActivePlanningConfigurationDefect
    > Map(IEnumerable<RecapPlannerConfigDefect> defects)
        => Array.AsReadOnly([
            .. defects.Select(static defect =>
                new RecapActivePlanningConfigurationDefect(
                    defect.Code,
                    defect.Detail
                )
            )
        ]);

    private static IReadOnlyList<
        RecapActivePlanningConfigurationDefect
    > Map(IEnumerable<RecapPlannerConfigResolveDefect> defects)
        => Array.AsReadOnly([
            .. defects.Select(static defect =>
                new RecapActivePlanningConfigurationDefect(
                    defect.Code,
                    defect.Detail
                )
            )
        ]);
}

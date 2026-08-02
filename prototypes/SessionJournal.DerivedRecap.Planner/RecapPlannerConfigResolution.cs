using System.Collections.Immutable;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Maintainer-neutral metadata needed to resolve one configured profile for
/// planning and to recognize its frozen execution identity later.
/// </summary>
public sealed record RecapProfilePlanningDescriptor {
    public RecapProfilePlanningDescriptor(
        string profileName,
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        string maintainerCapabilityFingerprint
    ) {
        ProfileName = string.IsNullOrWhiteSpace(profileName)
            ? throw new ArgumentException(
                "Recap profile name cannot be empty.",
                nameof(profileName)
            )
            : profileName;
        RecapBlockId = recapBlockId
            ?? throw new ArgumentNullException(nameof(recapBlockId));
        Target = target
            ?? throw new ArgumentNullException(nameof(target));
        if (!Enum.IsDefined(Target.Carrier)) {
            throw new ArgumentException(
                "Recap profile target carrier is invalid.",
                nameof(target)
            );
        }
        MaintainerId = string.IsNullOrWhiteSpace(maintainerId)
            ? throw new ArgumentException(
                "Maintainer ID cannot be empty.",
                nameof(maintainerId)
            )
            : maintainerId;
        MaintainerCapabilityFingerprint =
            RecapMaintainerCapabilityFingerprintSyntax.Require(
                maintainerCapabilityFingerprint,
                nameof(maintainerCapabilityFingerprint)
            );
    }

    public string ProfileName { get; }
    public RecapBlockId RecapBlockId { get; }
    public ContextHeaderBlockPath Target { get; }
    public string MaintainerId { get; }
    public string MaintainerCapabilityFingerprint { get; }
}

/// <summary>
/// Immutable, metadata-only capability snapshot supplied by a Host. The
/// complete snapshot is deliberately separate from the active config roster.
/// </summary>
public sealed class RecapMaintainerCapabilitySnapshot {
    private readonly IReadOnlyDictionary<
        string,
        RecapProfilePlanningDescriptor
    > _byProfileName;
    private readonly IReadOnlyDictionary<
        (string MaintainerId, ContextHeaderBlockPath Target,
            string CapabilityFingerprint),
        RecapProfilePlanningDescriptor
    > _byFrozenIdentity;

    public RecapMaintainerCapabilitySnapshot(
        IReadOnlyList<RecapProfilePlanningDescriptor> descriptors
    ) {
        ArgumentNullException.ThrowIfNull(descriptors);

        RecapProfilePlanningDescriptor[] snapshot = [.. descriptors];
        var byProfileName = new Dictionary<
            string,
            RecapProfilePlanningDescriptor
        >(StringComparer.Ordinal);
        var byFrozenIdentity = new Dictionary<
            (string MaintainerId, ContextHeaderBlockPath Target,
                string CapabilityFingerprint),
            RecapProfilePlanningDescriptor
        >();

        foreach (RecapProfilePlanningDescriptor? descriptor
            in snapshot) {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!byProfileName.TryAdd(
                    descriptor.ProfileName,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap capability snapshot contains duplicate "
                    + $"profile name '{descriptor.ProfileName}'.",
                    nameof(descriptors)
                );
            }

            var frozenIdentity = (
                descriptor.MaintainerId,
                descriptor.Target,
                descriptor.MaintainerCapabilityFingerprint
            );
            if (!byFrozenIdentity.TryAdd(
                    frozenIdentity,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap capability snapshot contains duplicate "
                    + "frozen identity "
                    + $"('{descriptor.MaintainerId}', "
                    + $"'{descriptor.Target}', "
                    + $"'{descriptor.MaintainerCapabilityFingerprint}').",
                    nameof(descriptors)
                );
            }
        }

        All = Array.AsReadOnly(snapshot);
        _byProfileName = byProfileName;
        _byFrozenIdentity = byFrozenIdentity;
    }

    public IReadOnlyList<RecapProfilePlanningDescriptor> All {
        get;
    }

    public bool TryResolveProfileName(
        string? profileName,
        out RecapProfilePlanningDescriptor descriptor
    ) {
        if (profileName is not null
            && _byProfileName.TryGetValue(
                profileName,
                out descriptor!
            )) {
            return true;
        }

        descriptor = null!;
        return false;
    }

    public bool TryResolveFrozen(
        string? maintainerId,
        ContextHeaderBlockPath? target,
        string? maintainerCapabilityFingerprint,
        out RecapProfilePlanningDescriptor descriptor
    ) {
        if (maintainerId is not null
            && target is not null
            && maintainerCapabilityFingerprint is not null
            && _byFrozenIdentity.TryGetValue(
                (
                    maintainerId,
                    target,
                    maintainerCapabilityFingerprint
                ),
                out descriptor!
            )) {
            return true;
        }

        descriptor = null!;
        return false;
    }

    public bool SupportsFrozen(
        string? maintainerId,
        ContextHeaderBlockPath? target,
        string? maintainerCapabilityFingerprint
    ) => TryResolveFrozen(
        maintainerId,
        target,
        maintainerCapabilityFingerprint,
        out _
    );
}

public sealed record RecapPlanningPolicyRegistration {
    public RecapPlanningPolicyRegistration(
        string policyId,
        IRecapPlanningPolicy policy
    ) {
        PolicyId = string.IsNullOrWhiteSpace(policyId)
            ? throw new ArgumentException(
                "Planning policy ID cannot be empty.",
                nameof(policyId)
            )
            : policyId;
        Policy = policy
            ?? throw new ArgumentNullException(nameof(policy));
        if (!string.Equals(PolicyId, Policy.Id, StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"Planning policy registration '{PolicyId}' does not "
                + $"match policy identity '{Policy.Id}'.",
                nameof(policy)
            );
        }
    }

    public string PolicyId { get; }
    public IRecapPlanningPolicy Policy { get; }
}

public sealed record HistoryUnitLoadEstimatorRegistration {
    public HistoryUnitLoadEstimatorRegistration(
        string estimatorId,
        IHistoryUnitLoadEstimator estimator
    ) {
        EstimatorId = string.IsNullOrWhiteSpace(estimatorId)
            ? throw new ArgumentException(
                "History-unit load estimator ID cannot be empty.",
                nameof(estimatorId)
            )
            : estimatorId;
        Estimator = estimator
            ?? throw new ArgumentNullException(nameof(estimator));
        if (!string.Equals(
                EstimatorId,
                Estimator.Id,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                $"History-unit load estimator registration "
                + $"'{EstimatorId}' does not match estimator identity "
                + $"'{Estimator.Id}'.",
                nameof(estimator)
            );
        }
    }

    public string EstimatorId { get; }
    public IHistoryUnitLoadEstimator Estimator { get; }
}

/// <summary>
/// Immutable code-capability catalog used by pure config resolution. Hosts
/// may inject custom policies and estimators without relying on global state.
/// </summary>
public sealed class RecapPlannerConfigResolutionCatalog {
    private static readonly Lazy<
        RecapPlannerConfigResolutionCatalog
    > BuiltInSnapshot = new(
        static () => new RecapPlannerConfigResolutionCatalog(
            [
                new RecapPlanningPolicyRegistration(
                    RecapPlanningPolicyIds.BoundedMaintainAllV1,
                    new BoundedMaintainAllRecapPlanningPolicy()
                )
            ],
            [
                new HistoryUnitLoadEstimatorRegistration(
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new O200kBaseHistoryUnitLoadEstimator()
                )
            ]
        ),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private readonly IReadOnlyDictionary<
        string,
        IRecapPlanningPolicy
    > _policies;
    private readonly IReadOnlyDictionary<
        string,
        IHistoryUnitLoadEstimator
    > _estimators;

    public RecapPlannerConfigResolutionCatalog(
        IReadOnlyList<RecapPlanningPolicyRegistration> policies,
        IReadOnlyList<HistoryUnitLoadEstimatorRegistration> estimators
    ) {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(estimators);

        RecapPlanningPolicyRegistration[] policySnapshot = [.. policies];
        HistoryUnitLoadEstimatorRegistration[] estimatorSnapshot = [
            .. estimators
        ];
        var policyMap = new Dictionary<
            string,
            IRecapPlanningPolicy
        >(StringComparer.Ordinal);
        var estimatorMap = new Dictionary<
            string,
            IHistoryUnitLoadEstimator
        >(StringComparer.Ordinal);

        foreach (RecapPlanningPolicyRegistration? registration
            in policySnapshot) {
            ArgumentNullException.ThrowIfNull(registration);
            if (!policyMap.TryAdd(
                    registration.PolicyId,
                    registration.Policy
                )) {
                throw new ArgumentException(
                    "Resolution catalog contains duplicate policy ID "
                    + $"'{registration.PolicyId}'.",
                    nameof(policies)
                );
            }
        }
        foreach (HistoryUnitLoadEstimatorRegistration? registration
            in estimatorSnapshot) {
            ArgumentNullException.ThrowIfNull(registration);
            if (!estimatorMap.TryAdd(
                    registration.EstimatorId,
                    registration.Estimator
                )) {
                throw new ArgumentException(
                    "Resolution catalog contains duplicate estimator ID "
                    + $"'{registration.EstimatorId}'.",
                    nameof(estimators)
                );
            }
        }

        Policies = Array.AsReadOnly(policySnapshot);
        Estimators = Array.AsReadOnly(estimatorSnapshot);
        _policies = policyMap;
        _estimators = estimatorMap;
    }

    public static RecapPlannerConfigResolutionCatalog BuiltIn =>
        BuiltInSnapshot.Value;

    public IReadOnlyList<RecapPlanningPolicyRegistration> Policies {
        get;
    }
    public IReadOnlyList<HistoryUnitLoadEstimatorRegistration>
        Estimators { get; }

    public bool TryResolvePolicy(
        string? policyId,
        out IRecapPlanningPolicy policy
    ) {
        if (policyId is not null
            && _policies.TryGetValue(policyId, out policy!)) {
            return true;
        }
        policy = null!;
        return false;
    }

    public bool TryResolveEstimator(
        string? estimatorId,
        out IHistoryUnitLoadEstimator estimator
    ) {
        if (estimatorId is not null
            && _estimators.TryGetValue(estimatorId, out estimator!)) {
            return true;
        }
        estimator = null!;
        return false;
    }
}

/// <summary>
/// Immutable canonical config document and its provenance for one operation.
/// </summary>
public sealed class RecapPlannerConfigSnapshot {
    private RecapPlannerConfigSnapshot(
        string? canonicalPath,
        RecapPlannerConfigDocument document,
        ImmutableArray<byte> canonicalBytes,
        string configSha256
    ) {
        CanonicalPath = canonicalPath;
        Document = document;
        CanonicalBytes = canonicalBytes;
        ConfigSha256 = configSha256;
    }

    public string? CanonicalPath { get; }
    public RecapPlannerConfigDocument Document { get; }
    public ImmutableArray<byte> CanonicalBytes { get; }
    public string ConfigSha256 { get; }

    public static RecapPlannerConfigSnapshot FromDocument(
        RecapPlannerConfigDocument document
    ) {
        ArgumentNullException.ThrowIfNull(document);
        byte[] canonical =
            RecapPlannerConfigCodec.EncodeCanonical(document);
        return new RecapPlannerConfigSnapshot(
            null,
            document,
            ImmutableArray.CreateRange(canonical),
            RecapPlannerConfigCodec.ComputeSha256(canonical)
        );
    }

    public static RecapPlannerConfigSnapshot FromAvailable(
        RecapPlannerConfigLoadResult.Available available
    ) {
        ArgumentNullException.ThrowIfNull(available);
        if (available.CanonicalBytes.IsDefault) {
            throw new ArgumentException(
                "Available config has no canonical bytes.",
                nameof(available)
            );
        }
        byte[] expected = RecapPlannerConfigCodec.EncodeCanonical(
            available.Document
        );
        string expectedSha256 =
            RecapPlannerConfigCodec.ComputeSha256(expected);
        if (!available.CanonicalBytes.AsSpan().SequenceEqual(expected)
            || !string.Equals(
                available.ConfigSha256,
                expectedSha256,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Available config provenance does not match its document.",
                nameof(available)
            );
        }

        return new RecapPlannerConfigSnapshot(
            available.Path,
            available.Document,
            ImmutableArray.CreateRange(expected),
            expectedSha256
        );
    }
}

public sealed record ResolvedActiveRecapProfile {
    internal ResolvedActiveRecapProfile(
        string profileName,
        RecapProfilePlanningDescriptor capability,
        RecapBlockCatalogEntry catalogEntry
    ) {
        ProfileName = profileName;
        Capability = capability;
        CatalogEntry = catalogEntry;
    }

    public string ProfileName { get; }
    public RecapProfilePlanningDescriptor Capability { get; }
    public RecapBlockCatalogEntry CatalogEntry { get; }
}

public sealed class ResolvedRecapPlanningConfiguration {
    internal ResolvedRecapPlanningConfiguration(
        RecapPlannerConfigSnapshot snapshot,
        RecapPlanningInputs planningInputs,
        RecapPlanningLimits planningLimits,
        IReadOnlyList<ResolvedActiveRecapProfile> activeProfiles
    ) {
        Snapshot = snapshot
            ?? throw new ArgumentNullException(nameof(snapshot));
        PlanningInputs = planningInputs
            ?? throw new ArgumentNullException(nameof(planningInputs));
        PlanningLimits = planningLimits
            ?? throw new ArgumentNullException(nameof(planningLimits));
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ActiveProfiles = Array.AsReadOnly([.. activeProfiles]);
    }

    public RecapPlannerConfigSnapshot Snapshot { get; }
    public RecapPlanningInputs PlanningInputs { get; }
    public RecapPlanningLimits PlanningLimits { get; }
    public IReadOnlyList<ResolvedActiveRecapProfile> ActiveProfiles {
        get;
    }
}

public sealed record RecapPlannerConfigResolveDefect(
    string Code,
    string Detail
);

public static class RecapPlannerConfigResolveDefectCodes {
    public const string UnknownPolicy = nameof(UnknownPolicy);
    public const string PolicyIdentityMismatch =
        nameof(PolicyIdentityMismatch);
    public const string UnknownEstimator = nameof(UnknownEstimator);
    public const string EstimatorIdentityMismatch =
        nameof(EstimatorIdentityMismatch);
    public const string UnknownProfile = nameof(UnknownProfile);
    public const string DuplicateResolvedBlock =
        nameof(DuplicateResolvedBlock);
    public const string DuplicateResolvedTarget =
        nameof(DuplicateResolvedTarget);
    public const string InvalidPlanningAuthority =
        nameof(InvalidPlanningAuthority);
}

public abstract record RecapPlannerConfigResolveResult {
    private RecapPlannerConfigResolveResult() {
    }

    public sealed record Resolved(
        ResolvedRecapPlanningConfiguration Configuration
    ) : RecapPlannerConfigResolveResult;

    public sealed record Invalid(
        IReadOnlyList<RecapPlannerConfigResolveDefect> Defects
    ) : RecapPlannerConfigResolveResult;
}

/// <summary>
/// Pure config resolver. It performs no file, Store, raw-repository,
/// Completion-client, or concrete Maintainer access.
/// </summary>
public static class RecapPlannerConfigResolver {
    public static RecapPlannerConfigResolveResult Resolve(
        RecapPlannerConfigSnapshot snapshot,
        RecapMaintainerCapabilitySnapshot capabilities
    ) => Resolve(
        snapshot,
        RecapPlannerConfigResolutionCatalog.BuiltIn,
        capabilities
    );

    public static RecapPlannerConfigResolveResult Resolve(
        RecapPlannerConfigSnapshot snapshot,
        RecapPlannerConfigResolutionCatalog resolutionCatalog,
        RecapMaintainerCapabilitySnapshot capabilities
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolutionCatalog);
        ArgumentNullException.ThrowIfNull(capabilities);

        RecapPlannerConfigDocument document = snapshot.Document;
        if (!resolutionCatalog.TryResolvePolicy(
                document.PlanningPolicy,
                out IRecapPlanningPolicy policy
            )) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes.UnknownPolicy,
                $"Unknown recap planning policy "
                + $"'{document.PlanningPolicy}'."
            );
        }
        string resolvedPolicyId = policy.Id;
        if (!string.Equals(
                document.PlanningPolicy,
                resolvedPolicyId,
                StringComparison.Ordinal
            )) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes
                    .PolicyIdentityMismatch,
                $"Recap planning policy registration "
                + $"'{document.PlanningPolicy}' resolves to policy "
                + $"'{resolvedPolicyId}'."
            );
        }

        string estimatorId =
            document.Cadence.HistoryUnitLoadEstimatorId;
        if (!resolutionCatalog.TryResolveEstimator(
                estimatorId,
                out IHistoryUnitLoadEstimator estimator
            )) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes.UnknownEstimator,
                $"Unknown history-unit load estimator "
                + $"'{estimatorId}'."
            );
        }
        if (!string.Equals(
                estimatorId,
                estimator.Id,
                StringComparison.Ordinal
            )) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes
                    .EstimatorIdentityMismatch,
                $"History-unit load estimator registration "
                + $"'{estimatorId}' resolves to estimator "
                + $"'{estimator.Id}'."
            );
        }

        var active = new List<ResolvedActiveRecapProfile>(
            document.Catalog.Count
        );
        var blockIds = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
        foreach (RecapPlannerCatalogEntryDocument entry
            in document.Catalog) {
            if (!capabilities.TryResolveProfileName(
                    entry.MaintainerProfile,
                    out RecapProfilePlanningDescriptor descriptor
                )) {
                return Invalid(
                    RecapPlannerConfigResolveDefectCodes.UnknownProfile,
                    $"Unknown recap maintainer profile "
                    + $"'{entry.MaintainerProfile}'."
                );
            }
            if (!blockIds.Add(descriptor.RecapBlockId)) {
                return Invalid(
                    RecapPlannerConfigResolveDefectCodes
                        .DuplicateResolvedBlock,
                    $"Profile '{entry.MaintainerProfile}' resolves "
                    + "to duplicate RecapBlockId "
                    + $"'{descriptor.RecapBlockId}'."
                );
            }
            if (!targets.Add(descriptor.Target)) {
                return Invalid(
                    RecapPlannerConfigResolveDefectCodes
                        .DuplicateResolvedTarget,
                    $"Profile '{entry.MaintainerProfile}' resolves "
                    + $"to duplicate target '{descriptor.Target}'."
                );
            }

            var catalogEntry = new RecapBlockCatalogEntry(
                descriptor.RecapBlockId,
                descriptor.Target,
                descriptor.MaintainerId,
                descriptor.MaintainerCapabilityFingerprint,
                entry.MaxContentUtf8Bytes
            );
            active.Add(new ResolvedActiveRecapProfile(
                entry.MaintainerProfile,
                descriptor,
                catalogEntry
            ));
        }

        try {
            var cadence = new RecapCadenceConfig(
                estimatorId,
                new HistoryLoadUnit(
                    document.Cadence.MinimumRecentHistoryLoad
                ),
                new HistoryLoadUnit(
                    document.Cadence.RecapBuildIntervalHistoryLoad
                )
            );
            var inputs = new RecapPlanningInputs(
                Array.AsReadOnly([
                    .. active.Select(static binding =>
                        binding.CatalogEntry
                    )
                ]),
                cadence,
                estimator,
                policy
            );
            RecapPlannerLimitsDocument sourceLimits = document.Limits;
            var limits = new RecapPlanningLimits(
                sourceLimits.MaxRawGrowthEventCount,
                sourceLimits.MaxRouteEndpointsPerBlock,
                sourceLimits.MaxMaintainerCallsPerBuild,
                sourceLimits.MaxRawEventsPerStep,
                sourceLimits.MaxRawEventsPerBuild
            );
            RecapProtocolHardCaps.V4.ValidatePlanningAuthority(
                inputs,
                limits
            );
            return new RecapPlannerConfigResolveResult.Resolved(
                new ResolvedRecapPlanningConfiguration(
                    snapshot,
                    inputs,
                    limits,
                    active
                )
            );
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or OverflowException
        ) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes
                    .InvalidPlanningAuthority,
                exception.Message
            );
        }
    }

    private static RecapPlannerConfigResolveResult.Invalid Invalid(
        string code,
        string detail
    ) => new(Array.AsReadOnly([
        new RecapPlannerConfigResolveDefect(code, detail)
    ]));
}

using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed record DerivedArtifactSetRoleRequirement(
    string RoleId,
    ContextHeaderBlockPath Target,
    bool Required = true
);

/// <summary>
/// Application-owned coherence policy used to publish and select one derived ArtifactSet lineage.
/// Concrete role names are deliberately data, not SessionJournal core policy.
/// </summary>
public sealed record DerivedArtifactSetPolicy(
    string PolicyId,
    string PolicyFingerprint,
    string CoherenceGroup,
    IReadOnlyList<DerivedArtifactSetRoleRequirement> Roles
) {
    internal const int MaxRoleCount = 128;

    internal IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement>
        ValidateAndSnapshot() {
        ValidateToken(PolicyId, nameof(PolicyId));
        ValidateToken(PolicyFingerprint, nameof(PolicyFingerprint));
        ValidateToken(CoherenceGroup, nameof(CoherenceGroup));
        ArgumentNullException.ThrowIfNull(Roles);

        var roles =
            new Dictionary<string, DerivedArtifactSetRoleRequirement>(
                StringComparer.Ordinal
            );
        var targets =
            new HashSet<(ContextHeaderCarrier Carrier, string BlockKey)>();
        foreach (DerivedArtifactSetRoleRequirement role in Roles) {
            if (roles.Count == MaxRoleCount) {
                throw new ArgumentException(
                    $"Artifact-set policy supports at most {MaxRoleCount} roles.",
                    nameof(Roles)
                );
            }
            ArgumentNullException.ThrowIfNull(role);
            ValidateToken(role.RoleId, nameof(role.RoleId));
            ValidateTarget(role.Target, nameof(role.Target));
            if (!roles.TryAdd(role.RoleId, role)) {
                throw new ArgumentException(
                    $"Artifact-set policy contains duplicate role '{role.RoleId}'.",
                    nameof(Roles)
                );
            }
            if (!targets.Add((
                    role.Target.Carrier,
                    role.Target.BlockKey
                ))) {
                throw new ArgumentException(
                    "Artifact-set policy role targets must be unique.",
                    nameof(Roles)
                );
            }
        }
        if (roles.Count == 0 || !roles.Values.Any(static role => role.Required)) {
            throw new ArgumentException(
                "Artifact-set policy requires at least one role and at least one required role.",
                nameof(Roles)
            );
        }
        return roles;
    }

    internal static void ValidateBranchRefId(RefId branchRefId) {
        if (branchRefId == default) {
            throw new ArgumentException(
                "Derived-memory branchRefId cannot be the default RefId.",
                nameof(branchRefId)
            );
        }
    }

    internal static void ValidateToken(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) {
            throw new ArgumentException(
                "Derived-memory identity tokens must contain 1 through 256 characters.",
                parameterName
            );
        }
        if (value.Contains('\0', StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Derived-memory identity tokens cannot contain NUL.",
                parameterName
            );
        }
    }

    internal static void ValidateTarget(
        ContextHeaderBlockPath target,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(target.Carrier)
            || string.IsNullOrWhiteSpace(target.BlockKey)
            || target.BlockKey.Length > 256
            || target.BlockKey.Contains('\0', StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Artifact-set target is invalid.",
                parameterName
            );
        }
    }
}

public sealed record DerivedArtifactSetMemberSelection(
    string RoleId,
    string ArtifactId
);

public sealed record DerivedArtifactSetMember(
    string RoleId,
    string ArtifactId,
    string ArtifactKind,
    ContextHeaderBlockPath Target,
    string ContentCodecId,
    string ContentSha256,
    EventAddress AbsorbedThrough,
    string Outcome
);

public sealed record DerivedArtifactSet(
    string SetId,
    string TransactionId,
    string JobFingerprint,
    string EpochId,
    string EpochPlanFingerprint,
    RefId BranchRefId,
    string CoherenceGroup,
    string TopologyVersion,
    string PolicyId,
    string PolicyFingerprint,
    IReadOnlyList<DerivedArtifactSetRoleRequirement> RoleRequirements,
    IReadOnlyList<DerivedMemoryRoleProvisioning> RoleProvisioning,
    string? PreviousSetId,
    EventAddress CommonAnchor,
    SessionContextAnchorSetupReferences AnchorSetups,
    IReadOnlyList<DerivedArtifactSetMember> Members
);

public sealed record DerivedArtifactSetPublicationRequest(
    DerivedArtifactSetPolicy Policy,
    DerivedMemoryOrchestrationTransaction Transaction,
    SessionContextAnchorSetupReferences AnchorSetups,
    IReadOnlyList<DerivedArtifactSetMemberSelection> Members,
    string? ExpectedPreviousSetId
) {
    public RefId BranchRefId => Transaction.BranchRefId;
}

/// <summary>
/// Stable, content-free inventory of every persisted ArtifactSet and latest pointer.
/// The inventory is diagnostic: graph and pointer completeness are enforced by
/// <see cref="DerivedMemoryRepository.ValidateAsync"/>, not by inventory reads.
/// </summary>
public sealed record DerivedArtifactSetInventory(
    IReadOnlyList<DerivedArtifactSet> Sets,
    IReadOnlyList<DerivedArtifactSetLatestPointer> LatestPointers
);

public sealed record DerivedArtifactSetLatestPointer(
    RefId BranchRefId,
    string CoherenceGroup,
    string PolicyId,
    string PolicyFingerprint,
    string SetId
);

public sealed class DerivedArtifactSetConcurrencyException
    : InvalidOperationException {
    public DerivedArtifactSetConcurrencyException(string message)
        : base(message) {
    }
}

using Atelia.SessionJournal.DerivedRecap.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

/// <summary>
/// Host-visible profile metadata around one immutable member definition.
/// </summary>
public sealed class RecapMaintainerProfileDescriptor {
    public RecapMaintainerProfileDescriptor(
        string profileName,
        string recapBlockIdValue,
        RecapMaintainerDefinition definition
    ) {
        ProfileName = string.IsNullOrWhiteSpace(profileName)
            ? throw new ArgumentException(
                "Recap maintainer profile name cannot be empty.",
                nameof(profileName)
            )
            : profileName;
        RecapBlockIdValue = IsValidRecapBlockIdValue(
            recapBlockIdValue
        )
            ? recapBlockIdValue
            : throw new ArgumentException(
                "RecapBlockIdValue must match [a-z0-9][a-z0-9._-]{0,127}.",
                nameof(recapBlockIdValue)
            );
        Definition = definition
            ?? throw new ArgumentNullException(nameof(definition));
    }

    public string ProfileName { get; }

    public string RecapBlockIdValue { get; }

    public RecapMaintainerDefinition Definition { get; }

    public string MaintainerId => Definition.MaintainerId;

    public ContextHeaderBlockPath Target => Definition.Target;

    public string ImplementationId => Definition.ImplementationId;

    public string FamilyFingerprint =>
        Definition.Family.SemanticFingerprint;

    public string CapabilityFingerprint =>
        Definition.CapabilityFingerprint;

    private static bool IsValidRecapBlockIdValue(string? value) {
        if (string.IsNullOrEmpty(value) || value.Length > 128) {
            return false;
        }
        if (!IsLowerAlphaNumeric(value[0])) {
            return false;
        }
        for (int index = 1; index < value.Length; index++) {
            char ch = value[index];
            if (!IsLowerAlphaNumeric(ch)
                && ch is not ('.' or '_' or '-')) {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerAlphaNumeric(char ch)
        => (ch >= 'a' && ch <= 'z')
            || (ch >= '0' && ch <= '9');
}

public sealed class RecapMaintainerProfileCatalog {
    public const string AutobiographicalRewrite =
        "autobiographical-rewrite";
    public const string WorldUnderstandingRewrite =
        "world-understanding-rewrite";
    private static readonly Lazy<RecapMaintainerProfileCatalog>
        BuiltInSnapshot = new(
            CreateBuiltIn,
            LazyThreadSafetyMode.ExecutionAndPublication
        );

    private readonly IReadOnlyDictionary<
        string,
        RecapMaintainerProfileDescriptor
    > _byProfileName;
    private readonly IReadOnlyDictionary<
        (string MaintainerId, ContextHeaderBlockPath Target,
            string CapabilityFingerprint),
        RecapMaintainerProfileDescriptor
    > _byFrozenIdentity;

    public RecapMaintainerProfileCatalog(
        IReadOnlyList<RecapMaintainerProfileDescriptor> descriptors
    ) {
        ArgumentNullException.ThrowIfNull(descriptors);
        RecapMaintainerProfileDescriptor[] snapshot = [.. descriptors];
        var byProfileName = new Dictionary<
            string,
            RecapMaintainerProfileDescriptor
        >(StringComparer.Ordinal);
        var byFrozenIdentity = new Dictionary<
            (string MaintainerId, ContextHeaderBlockPath Target,
                string CapabilityFingerprint),
            RecapMaintainerProfileDescriptor
        >();
        var familyBySemanticFingerprint = new Dictionary<
            string,
            RecapMaintainerFamilyDefinition
        >(StringComparer.Ordinal);

        foreach (RecapMaintainerProfileDescriptor? descriptor
            in snapshot) {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!byProfileName.TryAdd(
                    descriptor.ProfileName,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap maintainer profile catalog contains duplicate profile name "
                        + $"'{descriptor.ProfileName}'.",
                    nameof(descriptors)
                );
            }

            var frozenIdentity = (
                descriptor.MaintainerId,
                descriptor.Target,
                descriptor.CapabilityFingerprint
            );
            if (!byFrozenIdentity.TryAdd(
                    frozenIdentity,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap maintainer profile catalog contains duplicate frozen identity "
                        + $"('{descriptor.MaintainerId}', '{descriptor.Target}', "
                        + $"'{descriptor.CapabilityFingerprint}').",
                    nameof(descriptors)
                );
            }

            RecapMaintainerFamilyDefinition family =
                descriptor.Definition.Family;
            if (familyBySemanticFingerprint.TryGetValue(
                    family.SemanticFingerprint,
                    out RecapMaintainerFamilyDefinition? existingFamily
                )) {
                if (!ReferenceEquals(existingFamily, family)) {
                    throw new ArgumentException(
                        "Recap maintainer profile catalog contains distinct family instances with the same semantic fingerprint.",
                        nameof(descriptors)
                    );
                }
            }
            else {
                familyBySemanticFingerprint.Add(
                    family.SemanticFingerprint,
                    family
                );
            }
        }

        All = Array.AsReadOnly(snapshot);
        _byProfileName = byProfileName;
        _byFrozenIdentity = byFrozenIdentity;
    }

    public static RecapMaintainerProfileCatalog BuiltIn =>
        BuiltInSnapshot.Value;

    public IReadOnlyList<RecapMaintainerProfileDescriptor> All {
        get;
    }

    public bool TryResolveProfileName(
        string? profileName,
        out RecapMaintainerProfileDescriptor descriptor
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
        string? capabilityFingerprint,
        out RecapMaintainerProfileDescriptor descriptor
    ) {
        if (maintainerId is not null
            && target is not null
            && capabilityFingerprint is not null
            && _byFrozenIdentity.TryGetValue(
                (maintainerId, target, capabilityFingerprint),
                out descriptor!
            )) {
            return true;
        }
        descriptor = null!;
        return false;
    }

    public RecapMaintainerProfileDescriptor Resolve(
        string profileName
    ) {
        ArgumentNullException.ThrowIfNull(profileName);
        return TryResolveProfileName(profileName, out var descriptor)
            ? descriptor
            : throw new ArgumentException(
                $"Unsupported recap maintainer profile '{profileName}'.",
                nameof(profileName)
            );
    }

    private static RecapMaintainerProfileCatalog CreateBuiltIn()
        => new([
            new(
                WorldUnderstandingRewrite,
                RolePlayRecapBlockPaths.WorldUnderstandingBlockKey,
                WorldUnderstandingRecapMaintainers.Default
            ),
            new(
                AutobiographicalRewrite,
                RolePlayRecapBlockPaths
                    .FirstPersonAutobiographyBlockKey,
                AutobiographicalRecapMaintainers.Default
            )
        ]);
}

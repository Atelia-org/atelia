using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

/// <summary>
/// Stable application-level identity and factory for one concrete memory-maintainer profile.
/// Role identity is deliberately owned here rather than by SessionJournal raw core.
/// </summary>
public sealed record RecapMaintainerProfileDescriptor(
    string ProfileName,
    string RoleId,
    string RecapBlockIdValue,
    RecapRewriteProfile RewriteProfile
) {
    public const string PromptFingerprintSchema =
        "atelia.session-journal.memory-maintainer-prompt.v1";

    public string ProfileName { get; init; } =
        string.IsNullOrWhiteSpace(ProfileName)
            ? throw new ArgumentException(
                "Recap maintainer profile name cannot be empty.",
                nameof(ProfileName)
            )
            : ProfileName;

    public string RoleId { get; init; } =
        string.IsNullOrWhiteSpace(RoleId)
            ? throw new ArgumentException(
                "Recap maintainer role id cannot be empty.",
                nameof(RoleId)
            )
            : RoleId;

    public string RecapBlockIdValue { get; init; } =
        IsValidRecapBlockIdValue(RecapBlockIdValue)
            ? RecapBlockIdValue
            : throw new ArgumentException(
                "RecapBlockIdValue must match "
                + "[a-z0-9][a-z0-9._-]{0,127}.",
                nameof(RecapBlockIdValue)
            );

    public RecapRewriteProfile RewriteProfile { get; init; } =
        RewriteProfile
        ?? throw new ArgumentNullException(nameof(RewriteProfile));

    public string MaintainerId => RewriteProfile.Id;
    public ContextHeaderBlockPath Target => RewriteProfile.Target;

    public string PromptFingerprint =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(
                new PromptFingerprintDto(
                    PromptFingerprintSchema,
                    RewriteProfile.SystemPrompt,
                    RewriteProfile.UserPrompt
                )
            )
        ))}";

    public IRecapBlockMaintainer Create(
        ICompletionClient completionClient,
        string modelId
    ) => new RewriteRecapBlockMaintainer(
        RewriteProfile,
        completionClient,
        modelId
    );

    public RecapMaintainerProfileDescriptor WithPromptOverrides(
        string? systemPrompt,
        string? userPrompt
    ) => this with {
        RewriteProfile = RewriteProfile with {
            SystemPrompt =
                systemPrompt ?? RewriteProfile.SystemPrompt,
            UserPrompt = userPrompt ?? RewriteProfile.UserPrompt
        }
    };

    private sealed record PromptFingerprintDto(
        [property: JsonPropertyOrder(0)] string Schema,
        [property: JsonPropertyOrder(1)] string SystemPrompt,
        [property: JsonPropertyOrder(2)] string UserPrompt
    );

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
    public const string AutobiographyRole = "autobiography";
    public const string WorldUnderstandingRole =
        "world-understanding";

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
        (string MaintainerId, ContextHeaderBlockPath Target),
        RecapMaintainerProfileDescriptor
    > _byFrozenIdentity;

    public RecapMaintainerProfileCatalog(
        IReadOnlyList<RecapMaintainerProfileDescriptor> descriptors
    ) {
        ArgumentNullException.ThrowIfNull(descriptors);

        RecapMaintainerProfileDescriptor[] snapshot =
            [.. descriptors];
        var byProfileName = new Dictionary<
            string,
            RecapMaintainerProfileDescriptor
        >(StringComparer.Ordinal);
        var byFrozenIdentity = new Dictionary<
            (string MaintainerId, ContextHeaderBlockPath Target),
            RecapMaintainerProfileDescriptor
        >();

        foreach (RecapMaintainerProfileDescriptor? descriptor
            in snapshot) {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!byProfileName.TryAdd(
                    descriptor.ProfileName,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap maintainer profile catalog contains "
                    + "duplicate profile name "
                    + $"'{descriptor.ProfileName}'.",
                    nameof(descriptors)
                );
            }

            var frozenIdentity = (
                descriptor.MaintainerId,
                descriptor.Target
            );
            if (!byFrozenIdentity.TryAdd(
                    frozenIdentity,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap maintainer profile catalog contains "
                    + "duplicate frozen identity "
                    + $"('{descriptor.MaintainerId}', "
                    + $"'{descriptor.Target}').",
                    nameof(descriptors)
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
        out RecapMaintainerProfileDescriptor descriptor
    ) {
        if (maintainerId is not null
            && target is not null
            && _byFrozenIdentity.TryGetValue(
                (maintainerId, target),
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
                $"Unsupported recap maintainer profile "
                + $"'{profileName}'.",
                nameof(profileName)
            );
    }

    private static RecapMaintainerProfileCatalog CreateBuiltIn()
        => new([
            new(
                WorldUnderstandingRewrite,
                WorldUnderstandingRole,
                RolePlayRecapBlockPaths
                    .WorldUnderstandingBlockKey,
                WorldUnderstandingRewriteProfiles.Default
            ),
            new(
                AutobiographicalRewrite,
                AutobiographyRole,
                RolePlayRecapBlockPaths
                    .FirstPersonAutobiographyBlockKey,
                AutobiographicalRewriteProfiles.Default
            )
        ]);
}

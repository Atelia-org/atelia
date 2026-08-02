using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    public string ImplementationId =>
        RewriteRecapBlockMaintainer.ImplementationId;

    public string PromptFingerprint =>
        RecapMaintainerCapabilityFingerprint.ComputePrompt(
            PromptFingerprintSchema,
            RewriteProfile.SystemPrompt,
            RewriteProfile.UserPrompt
        );

    public string CapabilityFingerprint =>
        RecapMaintainerCapabilityFingerprint.Compute(
            ImplementationId,
            MaintainerId,
            Target,
            PromptFingerprint
        );

    public IRecapBlockMaintainer Create(
        ICompletionClient completionClient,
        string modelId
    ) => new RewriteRecapBlockMaintainer(
        RewriteProfile,
        CapabilityFingerprint,
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

public static class RecapMaintainerCapabilityFingerprint {
    public const string Schema =
        "atelia.session-journal.recap-maintainer-capability.v1";

    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    public static string Compute(
        string implementationId,
        string maintainerId,
        ContextHeaderBlockPath target,
        string promptFingerprint
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(implementationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(maintainerId);
        ArgumentNullException.ThrowIfNull(target);
        RequireFingerprint(promptFingerprint, nameof(promptFingerprint));
        byte[] preimage = Write(writer => {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("implementationId", implementationId);
            writer.WriteString("maintainerId", maintainerId);
            writer.WriteStartObject("target");
            writer.WriteString(
                "carrier",
                ContextHeaderCarrierTokens.ToStorageToken(
                    target.Carrier
                )
            );
            writer.WriteString("blockKey", target.BlockKey);
            writer.WriteEndObject();
            writer.WriteString(
                "promptFingerprint",
                promptFingerprint
            );
            writer.WriteEndObject();
        });
        return Hash(preimage);
    }

    internal static string ComputePrompt(
        string schema,
        string systemPrompt,
        string userPrompt
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);
        byte[] preimage = Write(writer => {
            writer.WriteStartObject();
            writer.WriteString("schema", schema);
            writer.WriteString("systemPrompt", systemPrompt);
            writer.WriteString("userPrompt", userPrompt);
            writer.WriteEndObject();
        });
        return Hash(preimage);
    }

    private static byte[] Write(Action<Utf8JsonWriter> action) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   WriterOptions
               )) {
            action(writer);
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    internal static string RequireFingerprint(
        string value,
        string parameterName
    ) {
        const string Prefix = "sha256:";
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 64
            || value.AsSpan(Prefix.Length).ContainsAnyExcept(
                "0123456789abcdef"
            )) {
            throw new ArgumentException(
                "Fingerprint must be sha256: followed by lowercase "
                + "SHA-256 hex.",
                parameterName
            );
        }
        return value;
    }
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
        (string MaintainerId, ContextHeaderBlockPath Target,
            string CapabilityFingerprint),
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
            (string MaintainerId, ContextHeaderBlockPath Target,
                string CapabilityFingerprint),
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
                descriptor.Target,
                descriptor.CapabilityFingerprint
            );
            if (!byFrozenIdentity.TryAdd(
                    frozenIdentity,
                    descriptor
                )) {
                throw new ArgumentException(
                    "Recap maintainer profile catalog contains "
                    + "duplicate frozen identity "
                    + $"('{descriptor.MaintainerId}', "
                    + $"'{descriptor.Target}', "
                    + $"'{descriptor.CapabilityFingerprint}').",
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

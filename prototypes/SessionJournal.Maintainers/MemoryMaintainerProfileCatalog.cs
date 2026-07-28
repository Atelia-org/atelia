using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.Maintainers;

/// <summary>
/// Stable application-level identity and factory for one concrete memory-maintainer profile.
/// Role identity is deliberately owned here rather than by SessionJournal raw core.
/// </summary>
public sealed record MemoryMaintainerProfileDescriptor(
    string ProfileName,
    string RoleId,
    MemoryRewriteProfile RewriteProfile
) {
    public const string PromptFingerprintSchema =
        "atelia.session-journal.memory-maintainer-prompt.v1";

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

    public IMemoryBlockMaintainer Create(
        ICompletionClient completionClient,
        string modelId
    ) => new RewriteMemoryBlockMaintainer(
        RewriteProfile,
        completionClient,
        modelId
    );

    public MemoryMaintainerProfileDescriptor WithPromptOverrides(
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
}

public static class MemoryMaintainerProfileCatalog {
    public const string AutobiographicalRewrite =
        "autobiographical-rewrite";
    public const string WorldUnderstandingRewrite =
        "world-understanding-rewrite";
    public const string AutobiographyRole = "autobiography";
    public const string WorldUnderstandingRole =
        "world-understanding";

    public static MemoryMaintainerProfileDescriptor Resolve(
        string profileName
    ) => profileName switch {
        AutobiographicalRewrite => new(
            AutobiographicalRewrite,
            AutobiographyRole,
            AutobiographicalRewriteProfiles.Default
        ),
        WorldUnderstandingRewrite => new(
            WorldUnderstandingRewrite,
            WorldUnderstandingRole,
            WorldUnderstandingRewriteProfiles.Default
        ),
        _ => throw new ArgumentException(
            $"Unsupported memory maintainer profile '{profileName}'.",
            nameof(profileName)
        )
    };
}

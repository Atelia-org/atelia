using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedMemory;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class MemoryMaintainerProducerIdentity {
    public const string Producer =
        "SessionJournal.Cli/run-memory-maintainer";
    public const string IdentityProducer =
        "Atelia.SessionJournal.DerivedMemory/identity-maintainer";
    public const string FingerprintSchema =
        "atelia.session-journal.epoch-memory-maintainer-producer.v2";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ComputeProducerFingerprint(
        RecapMaintainerProfileDescriptor profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(new ProducerFingerprintDto(
            FingerprintSchema,
            Producer,
            DerivedMemoryArtifactStore.ArtifactSchema,
            profile.ProfileName,
            profile.RoleId,
            profile.RewriteProfile.Id,
            SJ.ContextHeaderCarrierTokens.ToStorageToken(
                profile.RewriteProfile.Target.Carrier
            ),
            profile.RewriteProfile.Target.BlockKey,
            profile.PromptFingerprint,
            connection.Kind,
            connection.ModelId,
            connection.CompletionSurfaceId,
            connection.BaseAddress,
            connection.MaxTokens,
            client.Name,
            client.ApiSpecId
        ));
    }

    public static string ComputeModelFingerprint(
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) => ComputeFingerprint(new ModelFingerprintDto(
        connection.Kind,
        connection.ModelId,
        connection.CompletionSurfaceId,
        connection.BaseAddress,
        connection.MaxTokens,
        client.Name,
        client.ApiSpecId
    ));

    public static string ComputeIdentityProducerFingerprint(
        RecapMaintainerProfileDescriptor profile
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        return ComputeFingerprint(new IdentityProducerFingerprintDto(
            "atelia.session-journal.derived-memory-identity-producer.v1",
            IdentityProducer,
            DerivedMemoryArtifactStore.ArtifactSchema,
            profile.ProfileName,
            profile.RoleId,
            profile.RewriteProfile.Id,
            SJ.ContextHeaderCarrierTokens.ToStorageToken(
                profile.RewriteProfile.Target.Carrier
            ),
            profile.RewriteProfile.Target.BlockKey
        ));
    }

    public static string ComputeIdentityModelFingerprint() =>
        ComputeFingerprint(new IdentityModelFingerprintDto(
            "atelia.session-journal.derived-memory-no-model.v1"
        ));

    public static string ComputeConnectionFingerprint(
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(
            new ConnectionFingerprintDto(
                connection.Id,
                connection.Kind,
                connection.ModelId,
                connection.CompletionSurfaceId,
                connection.BaseAddress,
                connection.MaxTokens
            )
        );
    }

    public static string ComputeRequestAdapterFingerprint(
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(
            new RequestAdapterFingerprintDto(
                client.Name,
                client.ApiSpecId,
                connection.Kind,
                connection.CompletionSurfaceId
            )
        );
    }

    private static string ComputeFingerprint<T>(T value) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions
        );
        return $"sha256:{Convert.ToHexStringLower(
            SHA256.HashData(bytes)
        )}";
    }

    private sealed record ProducerFingerprintDto(
        [property: JsonPropertyOrder(0)] string Schema,
        [property: JsonPropertyOrder(1)] string Producer,
        [property: JsonPropertyOrder(2)] string ArtifactSchema,
        [property: JsonPropertyOrder(3)] string ProfileName,
        [property: JsonPropertyOrder(4)] string RoleId,
        [property: JsonPropertyOrder(5)] string MaintainerId,
        [property: JsonPropertyOrder(6)] string TargetCarrier,
        [property: JsonPropertyOrder(7)] string TargetBlockId,
        [property: JsonPropertyOrder(8)] string PromptFingerprint,
        [property: JsonPropertyOrder(9)] string ConnectionKind,
        [property: JsonPropertyOrder(10)] string ModelId,
        [property: JsonPropertyOrder(11)] string CompletionSurfaceId,
        [property: JsonPropertyOrder(12)] string BaseAddress,
        [property: JsonPropertyOrder(13)] int? MaxTokens,
        [property: JsonPropertyOrder(14)] string ClientName,
        [property: JsonPropertyOrder(15)] string ClientApiSpecId
    );

    private sealed record ModelFingerprintDto(
        [property: JsonPropertyOrder(0)] string ConnectionKind,
        [property: JsonPropertyOrder(1)] string ModelId,
        [property: JsonPropertyOrder(2)] string CompletionSurfaceId,
        [property: JsonPropertyOrder(3)] string BaseAddress,
        [property: JsonPropertyOrder(4)] int? MaxTokens,
        [property: JsonPropertyOrder(5)] string ClientName,
        [property: JsonPropertyOrder(6)] string ClientApiSpecId
    );

    private sealed record IdentityProducerFingerprintDto(
        [property: JsonPropertyOrder(0)] string Schema,
        [property: JsonPropertyOrder(1)] string Producer,
        [property: JsonPropertyOrder(2)] string ArtifactSchema,
        [property: JsonPropertyOrder(3)] string ProfileName,
        [property: JsonPropertyOrder(4)] string RoleId,
        [property: JsonPropertyOrder(5)] string MaintainerId,
        [property: JsonPropertyOrder(6)] string TargetCarrier,
        [property: JsonPropertyOrder(7)] string TargetBlockId
    );

    private sealed record IdentityModelFingerprintDto(
        [property: JsonPropertyOrder(0)] string Schema
    );

    private sealed record ConnectionFingerprintDto(
        string ConnectionId,
        string Kind,
        string ModelId,
        string CompletionSurfaceId,
        string BaseAddress,
        int? MaxTokens
    );

    private sealed record RequestAdapterFingerprintDto(
        string ClientName,
        string ClientApiSpecId,
        string ConnectionKind,
        string CompletionSurfaceId
    );
}

internal sealed record MemoryMaintainerRunRecord(
    string Schema,
    string BranchName,
    string BranchRefId,
    string EpochId,
    string EpochPlanFingerprint,
    string RoleId,
    string ProfileName,
    string MaintainerId,
    string CandidateId,
    string AttemptId,
    [property: JsonPropertyName("sourceRawHead")]
        string AbsorbedThrough,
    string SourceStartExclusive,
    string SourceEndInclusive,
    string? InputSetId,
    string? PreviousRoleArtifact,
    string TargetCarrier,
    string TargetBlockId,
    MemoryBlockTextPreview OldBlock,
    MemoryBlockTextPreview NewBlock,
    string ArtifactId,
    string ArtifactPath,
    IReadOnlyList<string> CallLogPaths,
    long HeaderVisits,
    long PayloadReads,
    long DecodedPayloadBytes,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string>? Errors
) {
    public static MemoryMaintainerRunRecord FromResult(
        DerivedMemoryBranchScope branchScope,
        RecapMaintainerProfileDescriptor profile,
        DerivedMemoryMaintainerRunResult result,
        string artifactsDirectory
    ) {
        DerivedMemoryArtifact artifact = result.Artifact;
        return new MemoryMaintainerRunRecord(
            "atelia.session-journal.memory-maintainer-run.v3",
            branchScope.BranchName,
            branchScope.BranchRefId.ToHexString(),
            artifact.EpochId,
            artifact.EpochPlanFingerprint,
            artifact.RoleId,
            profile.ProfileName,
            artifact.ProfileId,
            artifact.CandidateId,
            artifact.AttemptId,
            EventAddressTextCodec.Format(artifact.AbsorbedThrough),
            EventAddressTextCodec.Format(artifact.SourceStartExclusive),
            EventAddressTextCodec.Format(artifact.SourceEndInclusive),
            artifact.InputSetId,
            artifact.PreviousRoleArtifact,
            SJ.ContextHeaderCarrierTokens.ToStorageToken(
                artifact.Target.Carrier
            ),
            artifact.Target.BlockKey,
            MemoryMaintainerOutputUtil.CreateBlockPreview(
                result.OldBlock.Text
            )!,
            MemoryMaintainerOutputUtil.CreateBlockPreview(
                result.MaintenanceResult.NewBlock.Text
            )!,
            artifact.ArtifactId,
            Path.GetFullPath(Path.Combine(
                artifactsDirectory,
                $"{artifact.ArtifactId}.json"
            )),
            artifact.CallLogPaths,
            result.ReadDiagnostics.HeaderVisits,
            result.ReadDiagnostics.PayloadReads,
            result.ReadDiagnostics.DecodedPayloadBytes,
            result.MaintenanceResult.Invocation,
            result.MaintenanceResult.Errors
        );
    }
}

internal sealed record DerivedMemoryOrchestrationRunRecord(
    string Schema,
    string BranchName,
    string BranchRefId,
    string Status,
    string TransactionId,
    string JobFingerprint,
    string EpochId,
    string? PublishedSetId,
    IReadOnlyList<DerivedMemoryRoleSettlement> Settlements,
    IReadOnlyList<DerivedMemoryRoleFailure> Failures
);

internal sealed record OnlineTurnRunRecord(
    string Schema,
    string BranchName,
    string BranchRefId,
    string? Head,
    string Phase,
    string ProviderId,
    string ApiSpecId,
    string Model,
    string ActionSha256,
    int ErrorCount
);

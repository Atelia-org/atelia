using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedMemory;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal interface IMemoryMaintainerArtifactWriter {
    string RequiredSourceKind { get; }

    ValueTask PrepareAsync(EventAddress sourceRawHead, CancellationToken ct);

    ValueTask<MemoryMaintainerArtifactLink> WriteProducedAsync(
        MemoryMaintainerArtifactCandidate candidate,
        CancellationToken ct
    );
}

internal sealed record MemoryMaintainerArtifactCandidate(
    EventAddress SourceEndInclusive,
    SJ.MemoryPack UpdatedMemoryPack,
    SJ.MemoryBlockMaintenanceResult MaintenanceResult,
    IReadOnlyList<string> CallLogPaths
);

internal sealed record MemoryMaintainerArtifactLink(
    string ArtifactId,
    string ArtifactPath,
    EventAddress AnchorRawEvent,
    string? PreviousArtifact
);

internal sealed class MemoryMaintainerArtifactWriteException : Exception {
    public MemoryMaintainerArtifactWriteException(string message, Exception innerException)
        : base(message, innerException) {
    }
}

internal sealed class SessionJournalDerivedRecapWriter
    : IMemoryMaintainerArtifactWriter, IMemoryMaintainerRepositoryBound {
    public const string Producer = "SessionJournal.Cli/run-memory-maintainer";
    public const string FingerprintSchema =
        "atelia.session-journal.memory-maintainer-producer-fingerprint.v1";
    public const string AddressedReplayAdapterVersion = "session-journal-addressed-replay-v1";
    public const string SplitPolicyVersion =
        "memory-maintainer-half-context-v1";
    public const string TokenEstimatorVersion =
        "memory-maintainer-text-estimator-v1";

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _repoPath;
    private readonly MemoryMaintainerRunProfile _profile;
    private readonly DerivedRecapStore _store;
    private readonly DerivedRecapLineageKey _lineageKey;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _producerFingerprint;
    private int _prepareState;
    private EventAddress _sourceRawHead;
    private MemoryMaintainerArtifactLink? _previous;

    private SessionJournalDerivedRecapWriter(
        string repoPath,
        MemoryMaintainerRunProfile profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        _repoPath = repoPath;
        _profile = profile;
        _store = DerivedMemoryRepository.Open(repoPath).Recaps;
        _lineageKey = DerivedRecapLineageKey.Create(
            DerivedRecapArtifactKinds.RollingSummary,
            profile.MaintainerId,
            profile.Target
        );
        _producerFingerprint = ComputeProducerFingerprint(profile, client, connection);
    }

    public string RequiredSourceKind => MemoryMaintainerReplaySourceKinds.SessionJournal;
    public string RepositoryPath => _repoPath;

    public static SessionJournalDerivedRecapWriter Open(
        string sessionJournalRepoPath,
        MemoryMaintainerRunProfile profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        if (string.IsNullOrWhiteSpace(sessionJournalRepoPath)) {
            throw new ArgumentException("SessionJournal repo path cannot be empty.", nameof(sessionJournalRepoPath));
        }

        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);
        return new SessionJournalDerivedRecapWriter(
            Path.GetFullPath(sessionJournalRepoPath),
            profile,
            client,
            connection
        );
    }

    public async ValueTask PrepareAsync(EventAddress sourceRawHead, CancellationToken ct) {
        if (Interlocked.CompareExchange(ref _prepareState, 1, 0) != 0) {
            throw new InvalidOperationException(
                "Memory maintainer artifact writer can only be prepared once."
            );
        }

        ct.ThrowIfCancellationRequested();
        using (var engine = SJ.SessionJournalEngine.Open(_repoPath)) {
            _ = engine.ResolveGoverningSetup(sourceRawHead, ct);
        }

        DerivedRecapArtifact? latest = await _store.TryReadLatestAsync(_lineageKey, ct).ConfigureAwait(false);
        if (latest is not null) {
            throw new InvalidOperationException(
                $"Memory maintainer artifact writer requires an empty target lineage '{_lineageKey}', "
                + $"but found usable latest artifact '{latest.ArtifactId}'."
            );
        }

        _sourceRawHead = sourceRawHead;
        Volatile.Write(ref _prepareState, 2);
    }

    public async ValueTask<MemoryMaintainerArtifactLink> WriteProducedAsync(
        MemoryMaintainerArtifactCandidate candidate,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Volatile.Read(ref _prepareState) != 2) {
            throw new InvalidOperationException(
                "Memory maintainer artifact writer must be prepared before writing."
            );
        }

        ValidateCandidate(candidate);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            DerivedRecapArtifact? latest = await _store.TryReadLatestAsync(_lineageKey, ct).ConfigureAwait(false);
            string? expectedPreviousArtifact = _previous?.ArtifactId;
            if (!string.Equals(latest?.ArtifactId, expectedPreviousArtifact, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"Memory maintainer artifact lineage '{_lineageKey}' latest artifact changed. "
                    + $"Expected '{expectedPreviousArtifact ?? "<none>"}', got '{latest?.ArtifactId ?? "<none>"}'."
                );
            }

            SJ.SessionGoverningSetup governingSetup;
            using (var engine = SJ.SessionJournalEngine.Open(_repoPath)) {
                governingSetup = engine.ResolveGoverningSetup(
                    candidate.SourceEndInclusive,
                    ct
                );
            }
            var request = new DerivedRecapWriteRequest(
                ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
                ProfileId: _profile.MaintainerId,
                Producer: Producer,
                ProducerFingerprint: _producerFingerprint,
                SourceRawHead: _sourceRawHead,
                SourceStartExclusive: _previous?.AnchorRawEvent,
                SourceEndInclusive: candidate.SourceEndInclusive,
                AnchorRawEvent: candidate.SourceEndInclusive,
                GoverningRuntimeConfigSetup: governingSetup.RuntimeConfigSetupAddress,
                GoverningSystemPromptSetup: governingSetup.SystemPromptSetupAddress,
                PreviousArtifact: expectedPreviousArtifact,
                Target: _profile.Target,
                MemoryPack: candidate.UpdatedMemoryPack,
                Invocation: candidate.MaintenanceResult.Invocation,
                InputArtifacts: expectedPreviousArtifact is null ? [] : [expectedPreviousArtifact],
                CallLogPaths: candidate.CallLogPaths
            );

            DerivedRecapArtifact artifact;
            try {
                artifact = await _store.WriteProducedAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                throw new MemoryMaintainerArtifactWriteException(
                    $"Failed to write memory maintainer artifact for source end '{EventAddressTextCodec.Format(candidate.SourceEndInclusive)}'.",
                    ex
                );
            }

            var link = new MemoryMaintainerArtifactLink(
                ArtifactId: artifact.ArtifactId,
                ArtifactPath: Path.GetFullPath(Path.Combine(_store.ArtifactsDirectory, $"{artifact.ArtifactId}.json")),
                AnchorRawEvent: artifact.AnchorRawEvent,
                PreviousArtifact: artifact.PreviousArtifact
            );
            _previous = link;
            return link;
        }
        finally {
            _writeGate.Release();
        }
    }

    internal static string ComputeProducerFingerprint(
        MemoryMaintainerRunProfile profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);

        var dto = new MemoryMaintainerProducerFingerprintDto(
            Schema: FingerprintSchema,
            Producer: Producer,
            ArtifactSchema: DerivedRecapStore.ArtifactSchema,
            ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
            AddressedReplayAdapterVersion: AddressedReplayAdapterVersion,
            SplitPolicyVersion: SplitPolicyVersion,
            TokenEstimatorVersion: TokenEstimatorVersion,
            ProfileName: profile.ProfileName,
            MaintainerId: profile.MaintainerId,
            TargetCarrier: SJ.MemoryPackCarrierTokens.ToStorageToken(profile.Target.Carrier),
            TargetBlockId: profile.Target.BlockKey,
            SystemPrompt: profile.RewriteProfile.SystemPrompt,
            UserPrompt: profile.RewriteProfile.UserPrompt,
            ConnectionKind: connection.Kind,
            ModelId: connection.ModelId,
            CompletionSurfaceId: connection.CompletionSurfaceId,
            ResolvedBaseAddress: connection.BaseAddress,
            MaxTokens: connection.MaxTokens,
            ClientName: client.Name,
            ClientApiSpecId: client.ApiSpecId
        );
        byte[] canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(dto, FingerprintJsonOptions);
        string hash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        return $"sha256:{hash}";
    }

    private void ValidateCandidate(MemoryMaintainerArtifactCandidate candidate) {
        ArgumentNullException.ThrowIfNull(candidate.UpdatedMemoryPack);
        ArgumentNullException.ThrowIfNull(candidate.MaintenanceResult);
        ArgumentNullException.ThrowIfNull(candidate.CallLogPaths);
        if (!string.Equals(candidate.MaintenanceResult.MaintainerId, _profile.MaintainerId, StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"Candidate maintainer '{candidate.MaintenanceResult.MaintainerId}' does not match writer maintainer '{_profile.MaintainerId}'.",
                nameof(candidate)
            );
        }

        if (candidate.MaintenanceResult.Target != _profile.Target) {
            throw new ArgumentException("Candidate maintenance target does not match writer target.", nameof(candidate));
        }

        if (!candidate.UpdatedMemoryPack.TryGetBlock(_profile.Target, out SJ.MemoryPackBlock? updatedBlock)) {
            throw new ArgumentException("Candidate updated MemoryPack does not contain the writer target block.", nameof(candidate));
        }

        if (!string.Equals(updatedBlock.Text, candidate.MaintenanceResult.NewBlock.Text, StringComparison.Ordinal)) {
            throw new ArgumentException("Candidate updated MemoryPack target block does not match maintenance result.", nameof(candidate));
        }
    }
}

internal sealed record MemoryMaintainerProducerFingerprintDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string Producer,
    [property: JsonPropertyOrder(2)] string ArtifactSchema,
    [property: JsonPropertyOrder(3)] string ArtifactKind,
    [property: JsonPropertyOrder(4)] string AddressedReplayAdapterVersion,
    [property: JsonPropertyOrder(5)] string SplitPolicyVersion,
    [property: JsonPropertyOrder(6)] string TokenEstimatorVersion,
    [property: JsonPropertyOrder(7)] string ProfileName,
    [property: JsonPropertyOrder(8)] string MaintainerId,
    [property: JsonPropertyOrder(9)] string TargetCarrier,
    [property: JsonPropertyOrder(10)] string TargetBlockId,
    [property: JsonPropertyOrder(11)] string SystemPrompt,
    [property: JsonPropertyOrder(12)] string UserPrompt,
    [property: JsonPropertyOrder(13)] string ConnectionKind,
    [property: JsonPropertyOrder(14)] string ModelId,
    [property: JsonPropertyOrder(15)] string CompletionSurfaceId,
    [property: JsonPropertyOrder(16)] string ResolvedBaseAddress,
    [property: JsonPropertyOrder(17)] int? MaxTokens,
    [property: JsonPropertyOrder(18)] string ClientName,
    [property: JsonPropertyOrder(19)] string ClientApiSpecId
);

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Derived;
using SJ = Atelia.SessionJournal;

namespace ChatSessionBacktestCli;

internal interface IRollingSummaryArtifactWriter {
    string RequiredSourceKind { get; }

    ValueTask PrepareAsync(EventAddress sourceRawHead, CancellationToken ct);

    ValueTask<RollingSummaryArtifactLink> WriteProducedAsync(
        RollingSummaryArtifactCandidate candidate,
        CancellationToken ct
    );
}

internal sealed record RollingSummaryArtifactCandidate(
    EventAddress SourceEndInclusive,
    SJ.MemoryPack UpdatedMemoryPack,
    SJ.MemoryBlockMaintenanceResult MaintenanceResult,
    IReadOnlyList<string> CallLogPaths
);

internal sealed record RollingSummaryArtifactLink(
    string ArtifactId,
    string ArtifactPath,
    EventAddress AnchorRawEvent,
    string? PreviousArtifact
);

internal sealed class RollingSummaryArtifactWriteException : Exception {
    public RollingSummaryArtifactWriteException(string message, Exception innerException)
        : base(message, innerException) {
    }
}

internal sealed class SessionJournalDerivedRecapWriter
    : IRollingSummaryArtifactWriter, IRollingSummaryRepositoryBound {
    public const string Producer = "ChatSession.BacktestCli/replay-rolling-summary-session-journal";
    public const string FingerprintSchema = "atelia.chat-session.rolling-summary-producer-fingerprint.v1";
    public const string AddressedReplayAdapterVersion = "session-journal-addressed-replay-v1";
    public const string SplitPolicyVersion = "history-window-half-context-v1";
    public const string TokenEstimatorVersion = "backtest-text-estimator-v1";

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _repoPath;
    private readonly ReplayMemoryMaintainerProfile _profile;
    private readonly DerivedRecapStore _store;
    private readonly DerivedRecapLineageKey _lineageKey;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _producerFingerprint;
    private int _prepareState;
    private EventAddress _sourceRawHead;
    private SJ.SessionGoverningSetup? _governingSetup;
    private RollingSummaryArtifactLink? _previous;

    private SessionJournalDerivedRecapWriter(
        string repoPath,
        ReplayMemoryMaintainerProfile profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        _repoPath = repoPath;
        _profile = profile;
        _store = DerivedRecapStore.Open(repoPath);
        _lineageKey = DerivedRecapLineageKey.Create(
            DerivedRecapArtifactKinds.RollingSummary,
            profile.MaintainerId,
            profile.Target
        );
        _producerFingerprint = ComputeProducerFingerprint(profile, client, connection);
    }

    public string RequiredSourceKind => RollingSummaryReplaySourceKinds.SessionJournal;
    public string RepositoryPath => _repoPath;

    public static SessionJournalDerivedRecapWriter Open(
        string sessionJournalRepoPath,
        ReplayMemoryMaintainerProfile profile,
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
            throw new InvalidOperationException("Rolling summary artifact writer can only be prepared once.");
        }

        ct.ThrowIfCancellationRequested();
        SJ.SessionGoverningSetup governingSetup;
        using (var engine = SJ.SessionJournalEngine.Open(_repoPath)) {
            governingSetup = engine.ResolveGoverningSetup(sourceRawHead, ct);
        }

        DerivedRecapArtifact? latest = await _store.TryReadLatestAsync(_lineageKey, ct).ConfigureAwait(false);
        if (latest is not null) {
            throw new InvalidOperationException(
                $"Rolling summary artifact writer requires an empty target lineage '{_lineageKey}', "
                + $"but found usable latest artifact '{latest.ArtifactId}'."
            );
        }

        _sourceRawHead = sourceRawHead;
        _governingSetup = governingSetup;
        Volatile.Write(ref _prepareState, 2);
    }

    public async ValueTask<RollingSummaryArtifactLink> WriteProducedAsync(
        RollingSummaryArtifactCandidate candidate,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Volatile.Read(ref _prepareState) != 2) {
            throw new InvalidOperationException("Rolling summary artifact writer must be prepared before writing.");
        }

        ValidateCandidate(candidate);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            await using FileStream storeWriteLock = await AcquireStoreWriteLockAsync(ct).ConfigureAwait(false);
            DerivedRecapArtifact? latest = await _store.TryReadLatestAsync(_lineageKey, ct).ConfigureAwait(false);
            string? expectedPreviousArtifact = _previous?.ArtifactId;
            if (!string.Equals(latest?.ArtifactId, expectedPreviousArtifact, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"Rolling summary artifact lineage '{_lineageKey}' latest artifact changed. "
                    + $"Expected '{expectedPreviousArtifact ?? "<none>"}', got '{latest?.ArtifactId ?? "<none>"}'."
                );
            }

            SJ.SessionGoverningSetup governingSetup = _governingSetup
                ?? throw new InvalidOperationException("Rolling summary artifact writer governing setup is unavailable.");
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
                throw new RollingSummaryArtifactWriteException(
                    $"Failed to write rolling summary artifact for source end '{EventAddressTextCodec.Format(candidate.SourceEndInclusive)}'.",
                    ex
                );
            }

            var link = new RollingSummaryArtifactLink(
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

    private async ValueTask<FileStream> AcquireStoreWriteLockAsync(CancellationToken ct) {
        string lockPath = Path.Combine(_store.StoreRoot, ".rolling-summary-writer.lock");
        try {
            Directory.CreateDirectory(_store.StoreRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new RollingSummaryArtifactWriteException(
                $"Failed to prepare the derived recap store write lock '{lockPath}'.",
                ex
            );
        }

        IOException? lastContention = null;
        const int maxAttempts = 400;
        for (int attempt = 0; attempt < maxAttempts; attempt++) {
            ct.ThrowIfCancellationRequested();
            try {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (UnauthorizedAccessException ex) {
                throw new RollingSummaryArtifactWriteException(
                    $"Access was denied while acquiring the derived recap store write lock '{lockPath}'.",
                    ex
                );
            }
            catch (IOException ex) {
                lastContention = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }

        throw new RollingSummaryArtifactWriteException(
            $"Timed out while acquiring the derived recap store write lock '{lockPath}'.",
            lastContention ?? new IOException("The derived recap store write lock was unavailable.")
        );
    }

    internal static string ComputeProducerFingerprint(
        ReplayMemoryMaintainerProfile profile,
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);

        var dto = new RollingSummaryProducerFingerprintDto(
            Schema: FingerprintSchema,
            Producer: Producer,
            ArtifactSchema: DerivedRecapStore.ArtifactSchema,
            ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
            AddressedReplayAdapterVersion: AddressedReplayAdapterVersion,
            SplitPolicyVersion: SplitPolicyVersion,
            TokenEstimatorVersion: TokenEstimatorVersion,
            ProfilePresetName: profile.PresetName,
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

    private void ValidateCandidate(RollingSummaryArtifactCandidate candidate) {
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

internal sealed record RollingSummaryProducerFingerprintDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string Producer,
    [property: JsonPropertyOrder(2)] string ArtifactSchema,
    [property: JsonPropertyOrder(3)] string ArtifactKind,
    [property: JsonPropertyOrder(4)] string AddressedReplayAdapterVersion,
    [property: JsonPropertyOrder(5)] string SplitPolicyVersion,
    [property: JsonPropertyOrder(6)] string TokenEstimatorVersion,
    [property: JsonPropertyOrder(7)] string ProfilePresetName,
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

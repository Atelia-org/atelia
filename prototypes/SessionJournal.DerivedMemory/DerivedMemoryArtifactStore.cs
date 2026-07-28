using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Append-only store for epoch-bound memory-block candidates. ArtifactSet publication is the only
/// operation that makes a candidate selectable; this store deliberately has no "latest artifact"
/// pointer or role-local cursor.
/// </summary>
public sealed class DerivedMemoryArtifactStore {
    public const string ArtifactSchema =
        "atelia.session-journal.derived-memory-artifact.v2";
    public const string MemoryPackSnapshotSchema =
        "atelia.session-journal.memory-pack.snapshot.v2";
    public const long MaxArtifactFileBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions StrictJsonOptions = new(
        JsonOptions
    ) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly DerivedMemoryRepository _repository;

    internal DerivedMemoryArtifactStore(DerivedMemoryRepository repository) {
        _repository = repository;
        ArtifactsDirectory = Path.Combine(repository.MemoryRoot, "artifacts");
    }

    public string ArtifactsDirectory { get; }

    /// <summary>
    /// Persists an append-only candidate staging artifact. A candidate carries no branch ref and
    /// cannot become selectable until an engine-bound orchestration finalization and ArtifactSet
    /// publication prove its durable epoch and raw-lineage closure.
    /// </summary>
    public async ValueTask<DerivedMemoryArtifact> WriteCandidateAsync(
        DerivedMemoryArtifactWriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.MemoryPack.TryGetBlock(
                request.Target,
                out MemoryPackBlock? targetBlock
            )) {
            throw new ArgumentException(
                "Candidate MemoryPack does not contain its target block.",
                nameof(request)
            );
        }

        DerivedMemoryArtifactInputMemberDto[] inputMembers = [
            .. request.InputMembers
                .OrderBy(static member => member.RoleId, StringComparer.Ordinal)
                .Select(static member =>
                    DerivedMemoryArtifactInputMemberDto.FromContract(member))
        ];
        var identity = new DerivedMemoryArtifactIdentityDto(
            ArtifactSchema,
            DerivedMemoryArtifactKinds.MemoryBlock,
            request.EpochId,
            request.EpochPlanFingerprint,
            request.RoleId,
            request.ProfileId,
            request.Producer,
            request.ProducerFingerprint,
            request.PromptFingerprint,
            request.ModelFingerprint,
            request.CandidateId,
            request.AttemptId,
            EventAddressTextCodec.Format(request.SourceRawHead),
            EventAddressTextCodec.Format(request.SourceStartExclusive),
            EventAddressTextCodec.Format(request.SourceEndInclusive),
            EventAddressTextCodec.Format(request.AnchorRawEvent),
            DerivedMemoryArtifactSetupReferencesDto.FromContract(
                request.RawStartSetups
            ),
            DerivedMemoryArtifactSetupReferencesDto.FromContract(
                request.AnchorSetups
            ),
            request.InputSetId,
            request.PreviousRoleArtifact,
            inputMembers,
            DerivedMemoryArtifactTarget.FromMemoryPackBlockPath(
                request.Target
            ),
            MemoryPackSnapshotDto.FromMemoryPack(request.MemoryPack),
            DerivedMemoryArtifactContentDto.Inline(targetBlock.Text),
            request.Invocation,
            FreezeStrings(request.CallLogPaths),
            request.Outcome,
            DerivedMemoryArtifactStatus.Produced
        );

        string identityHash = ComputeCanonicalSha256Hex(identity);
        string artifactId = BuildArtifactId(identityHash);
        DateTimeOffset createdUtc =
            request.CreatedUtc ?? DateTimeOffset.UtcNow;
        EnsureArtifactSerializedSize(Serialize(
            DerivedMemoryArtifactDto.FromIdentity(
                artifactId,
                createdUtc,
                identity
            )
        ));

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(ArtifactsDirectory);

        var dto = DerivedMemoryArtifactDto.FromIdentity(
            artifactId,
            createdUtc,
            identity
        );
        string json = Serialize(dto);
        EnsureArtifactSerializedSize(json);
        string path = GetArtifactPath(artifactId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            await _repository.WriteFileAtomicallyAsync(
                    path,
                    json,
                    overwrite: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return Materialize(dto);
        }

        DerivedMemoryArtifactDto existing =
            await ReadArtifactDtoStrictAsync(path, cancellationToken)
                .ConfigureAwait(false);
        if (string.Equals(
                ComputeIdentityHash(existing),
                identityHash,
                StringComparison.Ordinal
            )) {
            return Materialize(existing);
        }
        throw new InvalidDataException(
            $"Immutable derived-memory artifact collision at '{artifactId}'."
        );
    }

    public async ValueTask<DerivedMemoryArtifact?> TryReadArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default
    ) {
        ValidateArtifactId(artifactId);
        string path = GetArtifactPath(artifactId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            return null;
        }
        DerivedMemoryArtifactDto? dto =
            await TryReadArtifactDtoAsync(path, cancellationToken)
                .ConfigureAwait(false);
        return dto is null ? null : Materialize(dto);
    }

    internal async ValueTask<IReadOnlyList<DerivedMemoryArtifact>>
        ReadInventoryStrictAsync(CancellationToken cancellationToken) {
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            ArtifactsDirectory
        );
        if (!Directory.Exists(ArtifactsDirectory)) {
            return Array.Empty<DerivedMemoryArtifact>();
        }

        var artifacts = new List<DerivedMemoryArtifact>();
        foreach (string path in Directory
                     .EnumerateFiles(ArtifactsDirectory)
                     .OrderBy(
                         static value => Path.GetFileName(value),
                         StringComparer.Ordinal
                     )) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Derived-memory artifact directory contains an unexpected file: {path}"
                );
            }

            DerivedMemoryArtifactDto dto =
                await ReadArtifactDtoStrictAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            if (!string.Equals(
                    Path.GetFileName(path),
                    $"{dto.ArtifactId}.json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Derived-memory artifact filename does not match its id: {path}"
                );
            }
            artifacts.Add(Materialize(dto));
        }
        return Array.AsReadOnly([
            .. artifacts.OrderBy(
                static artifact => artifact.ArtifactId,
                StringComparer.Ordinal
            )
        ]);
    }

    private async ValueTask<DerivedMemoryArtifactDto>
        ReadArtifactDtoStrictAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            if (stream.Length > MaxArtifactFileBytes) {
                throw new InvalidDataException(
                    $"Derived-memory artifact exceeds its {MaxArtifactFileBytes}-byte limit: {path}"
                );
            }
            DerivedMemoryArtifactDto? dto =
                await JsonSerializer.DeserializeAsync<
                        DerivedMemoryArtifactDto
                    >(stream, StrictJsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            if (!IsUsableArtifact(dto)) {
                throw new InvalidDataException(
                    $"Derived-memory artifact is invalid: {path}"
                );
            }
            return dto!;
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or IOException
                or UnauthorizedAccessException
        ) {
            throw new InvalidDataException(
                $"Derived-memory artifact is unreadable or malformed: {path}",
                exception
            );
        }
    }

    private async ValueTask<DerivedMemoryArtifactDto?>
        TryReadArtifactDtoAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        try {
            return await ReadArtifactDtoStrictAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or JsonException
        ) {
            return null;
        }
    }

    private static bool IsUsableArtifact(
        DerivedMemoryArtifactDto? dto
    ) {
        if (dto is null
            || !string.Equals(
                dto.Schema,
                ArtifactSchema,
                StringComparison.Ordinal
            )
            || !string.Equals(
                dto.ArtifactKind,
                DerivedMemoryArtifactKinds.MemoryBlock,
                StringComparison.Ordinal
            )
            || !IsHashId(dto.ArtifactId, "dma_")
            || !IsHashId(dto.EpochId, "dae_")
            || !IsSha256Fingerprint(dto.EpochPlanFingerprint)
            || !IsRequiredToken(dto.RoleId)
            || !IsRequiredToken(dto.ProfileId)
            || !IsRequiredToken(dto.Producer)
            || !IsSha256Fingerprint(dto.ProducerFingerprint)
            || !IsSha256Fingerprint(dto.PromptFingerprint)
            || !IsSha256Fingerprint(dto.ModelFingerprint)
            || !IsRequiredToken(dto.CandidateId)
            || !IsRequiredToken(dto.AttemptId)
            || dto.Target is null
            || dto.MemoryPack is null
            || dto.Content is null
            || dto.InputMembers is null
            || dto.CallLogPaths is null
            || !DerivedMemoryArtifactOutcomes.IsDefined(dto.Outcome)
            || !string.Equals(
                dto.Status,
                DerivedMemoryArtifactStatus.Produced,
                StringComparison.Ordinal
            )) {
            return false;
        }

        if (!TryParseAddresses(dto)
            || !IsUsableSetupReferences(dto.RawStartSetups)
            || !IsUsableSetupReferences(dto.AnchorSetups)
            || dto.SourceEndInclusive != dto.AnchorRawEvent
            || !IsUsableTarget(dto.Target)
            || !IsUsableMemoryPack(dto.MemoryPack)
            || !string.Equals(
                dto.Content.Storage,
                DerivedMemoryArtifactContentStorage.Inline,
                StringComparison.Ordinal
            )
            || dto.Content.Text is null
            || !IsLowerSha256(dto.Content.Sha256)
            || !string.Equals(
                dto.Content.Sha256,
                ComputeSha256Hex(dto.Content.Text),
                StringComparison.Ordinal
            )
            || !TryGetSnapshotBlockText(
                dto.MemoryPack,
                dto.Target,
                out string? targetText
            )
            || !string.Equals(
                targetText,
                dto.Content.Text,
                StringComparison.Ordinal
            )
            || !ValidateInputMembers(dto)
            || !ArtifactIdMatchesIdentity(dto)) {
            return false;
        }
        return true;
    }

    private static bool TryParseAddresses(DerivedMemoryArtifactDto dto) =>
        EventAddressTextCodec.TryParse(dto.SourceRawHead, out _)
        && EventAddressTextCodec.TryParse(dto.SourceStartExclusive, out _)
        && EventAddressTextCodec.TryParse(dto.SourceEndInclusive, out _)
        && EventAddressTextCodec.TryParse(dto.AnchorRawEvent, out _);

    private static bool IsUsableSetupReferences(
        DerivedMemoryArtifactSetupReferencesDto? references
    ) => references is not null
        && IsUsableSetupReference(references.RuntimeConfig)
        && IsUsableSetupReference(references.SystemPrompt)
        && !string.Equals(
            references.RuntimeConfig.Address,
            references.SystemPrompt.Address,
            StringComparison.Ordinal
        );

    private static bool IsUsableSetupReference(
        DerivedMemoryArtifactSetupReferenceDto? reference
    ) => reference is not null
        && EventAddressTextCodec.TryParse(reference.Address, out _)
        && reference.BodySchemaVersion > 0
        && IsLowerSha256(reference.PayloadSha256);

    private static bool ValidateInputMembers(
        DerivedMemoryArtifactDto dto
    ) {
        if (dto.InputSetId is null) {
            return dto.PreviousRoleArtifact is null
                && dto.InputMembers.Count == 0;
        }
        if (!IsHashId(dto.InputSetId, "das_")
            || dto.InputMembers.Count == 0) {
            return false;
        }
        if (dto.PreviousRoleArtifact is not null
            && !IsHashId(dto.PreviousRoleArtifact, "dma_")) {
            return false;
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<(string Carrier, string BlockKey)>();
        string? previousRole = null;
        int matchingPrevious = 0;
        foreach (DerivedMemoryArtifactInputMemberDto member
                 in dto.InputMembers) {
            if (member is null
                || !IsRequiredToken(member.RoleId)
                || !IsHashId(member.ArtifactId, "dma_")
                || member.Target is null
                || !IsUsableTarget(member.Target)
                || !IsLowerSha256(member.ContentSha256)
                || !roles.Add(member.RoleId)
                || !artifacts.Add(member.ArtifactId)
                || !targets.Add((
                    member.Target.Carrier,
                    member.Target.BlockKey
                ))
                || previousRole is not null
                    && string.CompareOrdinal(
                        previousRole,
                        member.RoleId
                    ) >= 0) {
                return false;
            }
            previousRole = member.RoleId;
            if (string.Equals(
                    member.RoleId,
                    dto.RoleId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    member.ArtifactId,
                    dto.PreviousRoleArtifact,
                    StringComparison.Ordinal
                )
                && Equals(member.Target, dto.Target)) {
                matchingPrevious++;
            }
        }
        return dto.PreviousRoleArtifact is null
            ? matchingPrevious == 0
                && !dto.InputMembers.Any(member =>
                    string.Equals(
                        member.RoleId,
                        dto.RoleId,
                        StringComparison.Ordinal
                    ))
            : matchingPrevious == 1;
    }

    private static DerivedMemoryArtifact Materialize(
        DerivedMemoryArtifactDto dto
    ) => new(
        dto.ArtifactId,
        dto.ArtifactKind,
        dto.CreatedUtc,
        dto.EpochId,
        dto.EpochPlanFingerprint,
        dto.RoleId,
        dto.ProfileId,
        dto.Producer,
        dto.ProducerFingerprint,
        dto.PromptFingerprint,
        dto.ModelFingerprint,
        dto.CandidateId,
        dto.AttemptId,
        EventAddressTextCodec.Parse(dto.SourceRawHead),
        EventAddressTextCodec.Parse(dto.SourceStartExclusive),
        EventAddressTextCodec.Parse(dto.SourceEndInclusive),
        EventAddressTextCodec.Parse(dto.AnchorRawEvent),
        dto.RawStartSetups.ToContract(),
        dto.AnchorSetups.ToContract(),
        dto.InputSetId,
        dto.PreviousRoleArtifact,
        Array.AsReadOnly([
            .. dto.InputMembers.Select(
                static member => member.ToContract()
            )
        ]),
        dto.Target.ToMemoryPackBlockPath(),
        dto.MemoryPack.ToMemoryPack(),
        dto.Content.Text,
        dto.Invocation,
        dto.CallLogPaths,
        dto.Outcome,
        dto.Status
    );

    private string GetArtifactPath(string artifactId) =>
        Path.Combine(ArtifactsDirectory, $"{artifactId}.json");

    private static string BuildArtifactId(string identitySha256) =>
        $"dma_{identitySha256}";

    private static bool ArtifactIdMatchesIdentity(
        DerivedMemoryArtifactDto dto
    ) {
        string expected = BuildArtifactId(ComputeIdentityHash(dto));
        return string.Equals(
            dto.ArtifactId,
            expected,
            StringComparison.Ordinal
        );
    }

    private static string ComputeIdentityHash(
        DerivedMemoryArtifactDto dto
    ) => ComputeCanonicalSha256Hex(
        new DerivedMemoryArtifactIdentityDto(
            dto.Schema,
            dto.ArtifactKind,
            dto.EpochId,
            dto.EpochPlanFingerprint,
            dto.RoleId,
            dto.ProfileId,
            dto.Producer,
            dto.ProducerFingerprint,
            dto.PromptFingerprint,
            dto.ModelFingerprint,
            dto.CandidateId,
            dto.AttemptId,
            dto.SourceRawHead,
            dto.SourceStartExclusive,
            dto.SourceEndInclusive,
            dto.AnchorRawEvent,
            dto.RawStartSetups,
            dto.AnchorSetups,
            dto.InputSetId,
            dto.PreviousRoleArtifact,
            dto.InputMembers,
            dto.Target,
            dto.MemoryPack,
            dto.Content,
            dto.Invocation,
            dto.CallLogPaths,
            dto.Outcome,
            dto.Status
        )
    );

    private static bool IsUsableMemoryPack(
        MemoryPackSnapshotDto snapshot
    ) => string.Equals(
            snapshot.Schema,
            MemoryPackSnapshotSchema,
            StringComparison.Ordinal
        )
        && snapshot.System is not null
        && snapshot.Observation is not null
        && snapshot.Action is not null
        && IsUsableCarrier(snapshot.System)
        && IsUsableCarrier(snapshot.Observation)
        && IsUsableCarrier(snapshot.Action);

    private static bool IsUsableCarrier(
        IReadOnlyList<MemoryPackBlockDto> blocks
    ) {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return blocks.All(
            block => block is not null
                && IsRequiredToken(block.Key)
                && block.Text is not null
                && keys.Add(block.Key)
        );
    }

    private static bool TryGetSnapshotBlockText(
        MemoryPackSnapshotDto snapshot,
        DerivedMemoryArtifactTarget target,
        out string? text
    ) {
        IReadOnlyList<MemoryPackBlockDto> blocks =
            target.Carrier switch {
                MemoryPackCarrierTokens.System => snapshot.System,
                MemoryPackCarrierTokens.Observation =>
                    snapshot.Observation,
                MemoryPackCarrierTokens.Action => snapshot.Action,
                _ => []
            };
        MemoryPackBlockDto? block = blocks.SingleOrDefault(
            value => string.Equals(
                value.Key,
                target.BlockKey,
                StringComparison.Ordinal
            )
        );
        text = block?.Text;
        return block is not null;
    }

    private static bool IsUsableTarget(
        DerivedMemoryArtifactTarget target
    ) => MemoryPackCarrierTokens.TryParseStorageToken(
            target.Carrier,
            out _
        )
        && IsRequiredToken(target.BlockKey);

    private static bool IsRequiredToken(string? value) =>
        value is { Length: > 0 and <= 256 }
        && !string.IsNullOrWhiteSpace(value)
        && !value.Contains('\0', StringComparison.Ordinal);

    private static bool IsHashId(
        string? value,
        string prefix
    ) {
        if (value is null
            || value.Length != 68
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.AsSpan(4).ToString().All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            return false;
        }
        return true;
    }

    private static bool IsSha256Fingerprint(string? value) =>
        value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && IsLowerSha256(value[7..]);

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 }
        && value.All(
            static ch => ch is >= '0' and <= '9'
                or >= 'a' and <= 'f'
        );

    private static void ValidateArtifactId(string artifactId) {
        if (!IsHashId(artifactId, "dma_")) {
            throw new ArgumentException(
                "Derived-memory artifact id is invalid.",
                nameof(artifactId)
            );
        }
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static string ComputeCanonicalSha256Hex<T>(T value) =>
        ComputeSha256Hex(Serialize(value));

    internal static string ComputeSha256Fingerprint(string value) =>
        $"sha256:{ComputeSha256Hex(value)}";

    internal static string ComputeSha256Hex(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))
        );

    private static void EnsureArtifactSerializedSize(string json) {
        if (Encoding.UTF8.GetByteCount(json) > MaxArtifactFileBytes) {
            throw new InvalidDataException(
                $"Derived-memory artifact exceeds its {MaxArtifactFileBytes}-byte limit."
            );
        }
    }

    private static IReadOnlyList<string> FreezeStrings(
        IReadOnlyList<string>? values
    ) => values is null || values.Count == 0
        ? Array.AsReadOnly(Array.Empty<string>())
        : Array.AsReadOnly(values.ToArray());
}

public sealed record DerivedMemoryArtifactInputMember(
    string RoleId,
    string ArtifactId,
    MemoryPackBlockPath Target,
    string ContentSha256
);

public sealed record DerivedMemoryArtifactWriteRequest(
    string EpochId,
    string EpochPlanFingerprint,
    string RoleId,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    string PromptFingerprint,
    string ModelFingerprint,
    string CandidateId,
    string AttemptId,
    EventAddress SourceRawHead,
    EventAddress SourceStartExclusive,
    EventAddress SourceEndInclusive,
    EventAddress AnchorRawEvent,
    SessionContextAnchorSetupReferences RawStartSetups,
    SessionContextAnchorSetupReferences AnchorSetups,
    string? InputSetId,
    string? PreviousRoleArtifact,
    IReadOnlyList<DerivedMemoryArtifactInputMember> InputMembers,
    MemoryPackBlockPath Target,
    MemoryPack MemoryPack,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? CallLogPaths = null,
    DateTimeOffset? CreatedUtc = null,
    string Outcome = DerivedMemoryArtifactOutcomes.Changed
) {
    public void Validate() {
        RequireHashId(EpochId, "dae_", nameof(EpochId));
        RequireFingerprint(EpochPlanFingerprint, nameof(EpochPlanFingerprint));
        RequireToken(RoleId, nameof(RoleId));
        RequireToken(ProfileId, nameof(ProfileId));
        RequireToken(Producer, nameof(Producer));
        RequireFingerprint(
            ProducerFingerprint,
            nameof(ProducerFingerprint)
        );
        RequireFingerprint(PromptFingerprint, nameof(PromptFingerprint));
        RequireFingerprint(ModelFingerprint, nameof(ModelFingerprint));
        RequireToken(CandidateId, nameof(CandidateId));
        RequireToken(AttemptId, nameof(AttemptId));
        if (!DerivedMemoryArtifactOutcomes.IsDefined(Outcome)) {
            throw new ArgumentException(
                "Derived-memory artifact outcome is invalid.",
                nameof(Outcome)
            );
        }
        RequireAddress(SourceRawHead, nameof(SourceRawHead));
        RequireAddress(SourceStartExclusive, nameof(SourceStartExclusive));
        RequireAddress(SourceEndInclusive, nameof(SourceEndInclusive));
        RequireAddress(AnchorRawEvent, nameof(AnchorRawEvent));
        ValidateSetupReferences(RawStartSetups, nameof(RawStartSetups));
        ValidateSetupReferences(AnchorSetups, nameof(AnchorSetups));
        if (SourceEndInclusive != AnchorRawEvent) {
            throw new ArgumentException(
                "Candidate anchor must equal the exact epoch end.",
                nameof(AnchorRawEvent)
            );
        }
        ArgumentNullException.ThrowIfNull(InputMembers);
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(MemoryPack);
        ValidateInputMembers();
        if (InputSetId is null) {
            if (PreviousRoleArtifact is not null
                || InputMembers.Count != 0) {
                throw new ArgumentException(
                    "Genesis candidate requires an empty input snapshot."
                );
            }
        }
        else {
            RequireHashId(InputSetId, "das_", nameof(InputSetId));
            if (InputMembers.Count == 0) {
                throw new ArgumentException(
                    "Non-genesis candidate requires input members.",
                    nameof(InputMembers)
                );
            }
            if (PreviousRoleArtifact is not null) {
                RequireHashId(
                    PreviousRoleArtifact,
                    "dma_",
                    nameof(PreviousRoleArtifact)
                );
            }
        }
    }

    private void ValidateInputMembers() {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        int matchingPrevious = 0;
        foreach (DerivedMemoryArtifactInputMember member in InputMembers) {
            ArgumentNullException.ThrowIfNull(member);
            RequireToken(member.RoleId, nameof(InputMembers));
            RequireHashId(
                member.ArtifactId,
                "dma_",
                nameof(InputMembers)
            );
            ArgumentNullException.ThrowIfNull(member.Target);
            if (!Enum.IsDefined(member.Target.Carrier)
                || !IsToken(member.Target.BlockKey)
                || member.ContentSha256 is not { Length: 64 }
                || !member.ContentSha256.All(
                    static ch => ch is >= '0' and <= '9'
                        or >= 'a' and <= 'f'
                )
                || !roles.Add(member.RoleId)
                || !artifacts.Add(member.ArtifactId)
                || !targets.Add((
                    member.Target.Carrier,
                    member.Target.BlockKey
                ))) {
                throw new ArgumentException(
                    "Input members require unique role/artifact/target identities and canonical content hashes.",
                    nameof(InputMembers)
                );
            }
            if (string.Equals(
                    member.RoleId,
                    RoleId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    member.ArtifactId,
                    PreviousRoleArtifact,
                    StringComparison.Ordinal
                )
                && member.Target == Target) {
                matchingPrevious++;
            }
        }
        if (InputSetId is null) {
            return;
        }
        bool containsCurrentRole = InputMembers.Any(member =>
            string.Equals(member.RoleId, RoleId, StringComparison.Ordinal));
        if (PreviousRoleArtifact is null) {
            if (containsCurrentRole) {
                throw new ArgumentException(
                    "A new-role candidate cannot omit previousRoleArtifact when its input set already contains that role.",
                    nameof(PreviousRoleArtifact)
                );
            }
        }
        else if (matchingPrevious != 1) {
            throw new ArgumentException(
                "previousRoleArtifact must identify the exact same-role, same-target input member.",
                nameof(PreviousRoleArtifact)
            );
        }
    }

    private static void RequireToken(string value, string parameterName) {
        if (!IsToken(value)) {
            throw new ArgumentException(
                "Identity token must contain 1 through 256 non-NUL characters.",
                parameterName
            );
        }
    }

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 256 }
        && !string.IsNullOrWhiteSpace(value)
        && !value.Contains('\0', StringComparison.Ordinal);

    private static void RequireFingerprint(
        string value,
        string parameterName
    ) {
        if (value is not { Length: 71 }
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || !value[7..].All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                "Fingerprint must be canonical lowercase sha256.",
                parameterName
            );
        }
    }

    private static void RequireHashId(
        string? value,
        string prefix,
        string parameterName
    ) {
        if (value is null
            || value.Length != 68
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.AsSpan(4).ToString().All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                $"Identity must be a canonical {prefix} hash id.",
                parameterName
            );
        }
    }

    private static void RequireAddress(
        EventAddress address,
        string parameterName
    ) {
        if (address == default) {
            throw new ArgumentException(
                "EventAddress cannot be default.",
                parameterName
            );
        }
    }

    private static void ValidateSetupReferences(
        SessionContextAnchorSetupReferences references,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(references, parameterName);
        ValidateSetupReference(references.RuntimeConfig, parameterName);
        ValidateSetupReference(references.SystemPrompt, parameterName);
        if (references.RuntimeConfig.Address
            == references.SystemPrompt.Address) {
            throw new ArgumentException(
                "Runtime and system-prompt setup references must be distinct.",
                parameterName
            );
        }
    }

    private static void ValidateSetupReference(
        SessionContextSetupReference reference,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(reference, parameterName);
        RequireAddress(reference.Address, parameterName);
        if (reference.BodySchemaVersion <= 0
            || reference.PayloadSha256 is not { Length: 64 }
            || !reference.PayloadSha256.All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                "Setup reference must have a positive schema version and canonical lowercase sha256 payload hash.",
                parameterName
            );
        }
    }
}

public sealed record DerivedMemoryArtifact(
    string ArtifactId,
    string ArtifactKind,
    DateTimeOffset CreatedUtc,
    string EpochId,
    string EpochPlanFingerprint,
    string RoleId,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    string PromptFingerprint,
    string ModelFingerprint,
    string CandidateId,
    string AttemptId,
    EventAddress SourceRawHead,
    EventAddress SourceStartExclusive,
    EventAddress SourceEndInclusive,
    EventAddress AnchorRawEvent,
    SessionContextAnchorSetupReferences RawStartSetups,
    SessionContextAnchorSetupReferences AnchorSetups,
    string? InputSetId,
    string? PreviousRoleArtifact,
    IReadOnlyList<DerivedMemoryArtifactInputMember> InputMembers,
    MemoryPackBlockPath Target,
    MemoryPack MemoryPack,
    string Content,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string> CallLogPaths,
    string Outcome,
    string Status
);

public static class DerivedMemoryArtifactKinds {
    public const string MemoryBlock = "memory-block";
}

public static class DerivedMemoryArtifactStatus {
    public const string Produced = "produced";
}

public static class DerivedMemoryArtifactOutcomes {
    public const string Changed = "changed";
    public const string Unchanged = "unchanged";
    public const string Identity = "identity";

    internal static bool IsDefined(string? value) =>
        value is Changed or Unchanged or Identity;
}

internal sealed record DerivedMemoryArtifactDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string ArtifactId,
    [property: JsonPropertyOrder(2)] string ArtifactKind,
    [property: JsonPropertyOrder(3)] DateTimeOffset CreatedUtc,
    [property: JsonPropertyOrder(4)] string EpochId,
    [property: JsonPropertyOrder(5)] string EpochPlanFingerprint,
    [property: JsonPropertyOrder(6)] string RoleId,
    [property: JsonPropertyOrder(7)] string ProfileId,
    [property: JsonPropertyOrder(8)] string Producer,
    [property: JsonPropertyOrder(9)] string ProducerFingerprint,
    [property: JsonPropertyOrder(10)] string PromptFingerprint,
    [property: JsonPropertyOrder(11)] string ModelFingerprint,
    [property: JsonPropertyOrder(12)] string CandidateId,
    [property: JsonPropertyOrder(13)] string AttemptId,
    [property: JsonPropertyOrder(14)] string SourceRawHead,
    [property: JsonPropertyOrder(15)] string SourceStartExclusive,
    [property: JsonPropertyOrder(16)] string SourceEndInclusive,
    [property: JsonPropertyOrder(17)] string AnchorRawEvent,
    [property: JsonPropertyOrder(18)]
        DerivedMemoryArtifactSetupReferencesDto RawStartSetups,
    [property: JsonPropertyOrder(19)]
        DerivedMemoryArtifactSetupReferencesDto AnchorSetups,
    [property: JsonPropertyOrder(20)] string? InputSetId,
    [property: JsonPropertyOrder(21)] string? PreviousRoleArtifact,
    [property: JsonPropertyOrder(22)]
        IReadOnlyList<DerivedMemoryArtifactInputMemberDto> InputMembers,
    [property: JsonPropertyOrder(23)] DerivedMemoryArtifactTarget Target,
    [property: JsonPropertyOrder(24)] MemoryPackSnapshotDto MemoryPack,
    [property: JsonPropertyOrder(25)] DerivedMemoryArtifactContentDto Content,
    [property: JsonPropertyOrder(26)] CompletionDescriptor? Invocation,
    [property: JsonPropertyOrder(27)] IReadOnlyList<string> CallLogPaths,
    [property: JsonPropertyOrder(28)] string Outcome,
    [property: JsonPropertyOrder(29)] string Status
) {
    public static DerivedMemoryArtifactDto FromIdentity(
        string artifactId,
        DateTimeOffset createdUtc,
        DerivedMemoryArtifactIdentityDto identity
    ) => new(
        identity.Schema,
        artifactId,
        identity.ArtifactKind,
        createdUtc,
        identity.EpochId,
        identity.EpochPlanFingerprint,
        identity.RoleId,
        identity.ProfileId,
        identity.Producer,
        identity.ProducerFingerprint,
        identity.PromptFingerprint,
        identity.ModelFingerprint,
        identity.CandidateId,
        identity.AttemptId,
        identity.SourceRawHead,
        identity.SourceStartExclusive,
        identity.SourceEndInclusive,
        identity.AnchorRawEvent,
        identity.RawStartSetups,
        identity.AnchorSetups,
        identity.InputSetId,
        identity.PreviousRoleArtifact,
        identity.InputMembers,
        identity.Target,
        identity.MemoryPack,
        identity.Content,
        identity.Invocation,
        identity.CallLogPaths,
        identity.Outcome,
        identity.Status
    );
}

internal sealed record DerivedMemoryArtifactIdentityDto(
    string Schema,
    string ArtifactKind,
    string EpochId,
    string EpochPlanFingerprint,
    string RoleId,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    string PromptFingerprint,
    string ModelFingerprint,
    string CandidateId,
    string AttemptId,
    string SourceRawHead,
    string SourceStartExclusive,
    string SourceEndInclusive,
    string AnchorRawEvent,
    DerivedMemoryArtifactSetupReferencesDto RawStartSetups,
    DerivedMemoryArtifactSetupReferencesDto AnchorSetups,
    string? InputSetId,
    string? PreviousRoleArtifact,
    IReadOnlyList<DerivedMemoryArtifactInputMemberDto> InputMembers,
    DerivedMemoryArtifactTarget Target,
    MemoryPackSnapshotDto MemoryPack,
    DerivedMemoryArtifactContentDto Content,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string> CallLogPaths,
    string Outcome,
    string Status
);

internal sealed record DerivedMemoryArtifactSetupReferencesDto(
    [property: JsonPropertyOrder(0)]
        DerivedMemoryArtifactSetupReferenceDto RuntimeConfig,
    [property: JsonPropertyOrder(1)]
        DerivedMemoryArtifactSetupReferenceDto SystemPrompt
) {
    public static DerivedMemoryArtifactSetupReferencesDto FromContract(
        SessionContextAnchorSetupReferences references
    ) => new(
        DerivedMemoryArtifactSetupReferenceDto.FromContract(
            references.RuntimeConfig
        ),
        DerivedMemoryArtifactSetupReferenceDto.FromContract(
            references.SystemPrompt
        )
    );

    public SessionContextAnchorSetupReferences ToContract() => new(
        RuntimeConfig.ToContract(),
        SystemPrompt.ToContract()
    );
}

internal sealed record DerivedMemoryArtifactSetupReferenceDto(
    [property: JsonPropertyOrder(0)] string Address,
    [property: JsonPropertyOrder(1)] int BodySchemaVersion,
    [property: JsonPropertyOrder(2)] string PayloadSha256
) {
    public static DerivedMemoryArtifactSetupReferenceDto FromContract(
        SessionContextSetupReference reference
    ) => new(
        EventAddressTextCodec.Format(reference.Address),
        reference.BodySchemaVersion,
        reference.PayloadSha256
    );

    public SessionContextSetupReference ToContract() => new(
        EventAddressTextCodec.Parse(Address),
        BodySchemaVersion,
        PayloadSha256
    );
}

internal sealed record DerivedMemoryArtifactInputMemberDto(
    string RoleId,
    string ArtifactId,
    DerivedMemoryArtifactTarget Target,
    string ContentSha256
) {
    public static DerivedMemoryArtifactInputMemberDto FromContract(
        DerivedMemoryArtifactInputMember member
    ) => new(
        member.RoleId,
        member.ArtifactId,
        DerivedMemoryArtifactTarget.FromMemoryPackBlockPath(member.Target),
        member.ContentSha256
    );

    public DerivedMemoryArtifactInputMember ToContract() => new(
        RoleId,
        ArtifactId,
        Target.ToMemoryPackBlockPath(),
        ContentSha256
    );
}

internal sealed record DerivedMemoryArtifactTarget(
    string Carrier,
    string BlockKey
) {
    public static DerivedMemoryArtifactTarget FromMemoryPackBlockPath(
        MemoryPackBlockPath path
    ) => new(
        MemoryPackCarrierTokens.ToStorageToken(path.Carrier),
        path.BlockKey
    );

    public MemoryPackBlockPath ToMemoryPackBlockPath() {
        if (!MemoryPackCarrierTokens.TryParseStorageToken(
                Carrier,
                out MemoryPackCarrier carrier
            )) {
            throw new InvalidDataException(
                $"Unknown memory pack carrier token '{Carrier}'."
            );
        }
        return new MemoryPackBlockPath(carrier, BlockKey);
    }
}

internal sealed record DerivedMemoryArtifactContentDto(
    string Storage,
    string Text,
    string Sha256
) {
    public static DerivedMemoryArtifactContentDto Inline(string text) =>
        new(
            DerivedMemoryArtifactContentStorage.Inline,
            text,
            DerivedMemoryArtifactStore.ComputeSha256Hex(text)
        );
}

internal static class DerivedMemoryArtifactContentStorage {
    public const string Inline = "inline";
}

internal sealed record MemoryPackSnapshotDto(
    string Schema,
    IReadOnlyList<MemoryPackBlockDto> System,
    IReadOnlyList<MemoryPackBlockDto> Observation,
    IReadOnlyList<MemoryPackBlockDto> Action
) {
    public static MemoryPackSnapshotDto FromMemoryPack(
        MemoryPack memoryPack
    ) => new(
        DerivedMemoryArtifactStore.MemoryPackSnapshotSchema,
        FromCarrier(memoryPack.System),
        FromCarrier(memoryPack.Observation),
        FromCarrier(memoryPack.Action)
    );

    public MemoryPack ToMemoryPack() {
        var memoryPack = new MemoryPack();
        CopyCarrier(System, memoryPack.System);
        CopyCarrier(Observation, memoryPack.Observation);
        CopyCarrier(Action, memoryPack.Action);
        return memoryPack;
    }

    private static IReadOnlyList<MemoryPackBlockDto> FromCarrier(
        OrderedDictionary<string, MemoryPackBlock> carrier
    ) {
        var blocks = new MemoryPackBlockDto[carrier.Count];
        int index = 0;
        foreach ((string key, MemoryPackBlock block) in carrier) {
            blocks[index++] = new MemoryPackBlockDto(key, block.Text);
        }
        return Array.AsReadOnly(blocks);
    }

    private static void CopyCarrier(
        IReadOnlyList<MemoryPackBlockDto> source,
        OrderedDictionary<string, MemoryPackBlock> destination
    ) {
        foreach (MemoryPackBlockDto block in source) {
            destination.Add(
                block.Key,
                new MemoryPackBlock(block.Text)
            );
        }
    }
}

internal sealed record MemoryPackBlockDto(string Key, string Text);

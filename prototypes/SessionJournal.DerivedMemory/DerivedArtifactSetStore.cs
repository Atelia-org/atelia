using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed class DerivedArtifactSetStore {
    public const string SetSchema =
        "atelia.session-journal.derived-artifact-set.v1";
    public const string LatestPointerSchema =
        "atelia.session-journal.derived-artifact-set.latest-pointer.v1";
    public const long MaxSetFileBytes = 1024 * 1024;
    public const long MaxLatestPointerFileBytes = 64 * 1024;

    private const string SetIdDomain =
        "atelia.session-journal.derived-artifact-set-id.v1";
    private const string LatestKeyDomain =
        "atelia.session-journal.derived-artifact-set-latest-key.v1";
    private const int MaxMemberCount = 128;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions IdentityJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly DerivedMemoryRepository _repository;

    internal DerivedArtifactSetStore(DerivedMemoryRepository repository) {
        _repository = repository;
        SetsDirectory = Path.Combine(repository.MemoryRoot, "sets");
        LatestPointersDirectory = Path.Combine(
            repository.MemoryRoot,
            "indexes",
            "latest-sets"
        );
    }

    public string SetsDirectory { get; }

    public string LatestPointersDirectory { get; }

    public async ValueTask<DerivedArtifactSet> PublishAsync(
        DerivedArtifactSetPublicationRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.AnchorSetups);
        ArgumentNullException.ThrowIfNull(request.Members);
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles =
            request.Policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateLineageKey(request.LineageKey);
        ValidateSetupReferences(request.AnchorSetups);
        if (request.ExpectedPreviousSetId is not null) {
            ValidateSetId(request.ExpectedPreviousSetId);
        }
        DerivedArtifactSetMemberSelection[] selections =
            SnapshotSelections(request.Members);

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(SetsDirectory);
        _repository.EnsureDirectory(LatestPointersDirectory);

        DerivedArtifactSetLatestPointerDto? currentPointer =
            await TryReadLatestPointerDtoAsync(
                    request.Policy,
                    request.LineageKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? currentSetId = currentPointer?.SetId;

        DerivedArtifactSet candidate = await CreateSetAsync(
                request,
                roles,
                selections,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (string.Equals(
                currentSetId,
                candidate.SetId,
                StringComparison.Ordinal
            )) {
            DerivedArtifactSet existing = await ReadSetRequiredAsync(
                    candidate.SetId,
                    request.Policy,
                    request.LineageKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!SetsEquivalent(existing, candidate)) {
                throw new InvalidDataException(
                    $"ArtifactSet idempotent retry found a non-identical set '{candidate.SetId}'."
                );
            }
            return existing;
        }

        if (!string.Equals(
                currentSetId,
                request.ExpectedPreviousSetId,
                StringComparison.Ordinal
            )) {
            throw new DerivedArtifactSetConcurrencyException(
                "Derived ArtifactSet latest pointer changed. "
                + $"Expected '{request.ExpectedPreviousSetId ?? "<none>"}', "
                + $"observed '{currentSetId ?? "<none>"}'."
            );
        }

        string setPath = GetSetPath(candidate.SetId);
        var dto = ToDto(candidate);
        string serialized = JsonSerializer.Serialize(dto, JsonOptions);
        EnsureSerializedSize(
            serialized,
            MaxSetFileBytes,
            "Derived ArtifactSet"
        );
        if (File.Exists(setPath)) {
            DerivedArtifactSet existing = await ReadSetRequiredAsync(
                    candidate.SetId,
                    request.Policy,
                    request.LineageKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!SetsEquivalent(existing, candidate)) {
                throw new InvalidDataException(
                    $"Immutable ArtifactSet collision at '{candidate.SetId}'."
                );
            }
        }
        else {
            await _repository.WriteFileAtomicallyAsync(
                    setPath,
                    serialized,
                    overwrite: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var pointer = new DerivedArtifactSetLatestPointerDto(
            LatestPointerSchema,
            request.LineageKey,
            request.Policy.CoherenceGroup,
            request.Policy.PolicyId,
            request.Policy.PolicyFingerprint,
            candidate.SetId
        );
        string pointerJson = JsonSerializer.Serialize(pointer, JsonOptions);
        EnsureSerializedSize(
            pointerJson,
            MaxLatestPointerFileBytes,
            "Derived ArtifactSet latest pointer"
        );
        await _repository.WriteFileAtomicallyAsync(
                GetLatestPointerPath(
                    request.Policy,
                    request.LineageKey
                ),
                pointerJson,
                overwrite: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return candidate;
    }

    public async ValueTask<DerivedArtifactSet?> TryReadLatestAsync(
        DerivedArtifactSetPolicy policy,
        string lineageKey,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateLineageKey(lineageKey);
        DerivedArtifactSetLatestPointerDto? pointer =
            await TryReadLatestPointerDtoAsync(
                    policy,
                    lineageKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return pointer is null
            ? null
            : await ReadSetRequiredAsync(
                    pointer.SetId,
                    policy,
                    lineageKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactSet?> TryReadAsync(
        string setId,
        DerivedArtifactSetPolicy policy,
        string lineageKey,
        CancellationToken cancellationToken = default
    ) {
        ValidateSetId(setId);
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateLineageKey(lineageKey);
        string path = GetSetPath(setId);
        if (!File.Exists(path)) {
            return null;
        }
        return await ReadSetRequiredAsync(
                setId,
                policy,
                lineageKey,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactSetInventory> ReadInventoryAsync(
        CancellationToken cancellationToken = default
    ) => await ReadInventoryAsync(
            strictArtifactsById: null,
            cancellationToken
        )
        .ConfigureAwait(false);

    /// <summary>
    /// Reads one exact persisted set while validating its identity and every referenced artifact.
    /// This is intentionally independent of a current policy pointer.
    /// </summary>
    public async ValueTask<DerivedArtifactSet?> TryReadExactAsync(
        string setId,
        CancellationToken cancellationToken = default
    ) {
        ValidateSetId(setId);
        DerivedArtifactSetInventory inventory =
            await ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        return inventory.Sets.SingleOrDefault(
            set => string.Equals(
                set.SetId,
                setId,
                StringComparison.Ordinal
            )
        );
    }

    internal async ValueTask<DerivedArtifactSetInventory> ReadInventoryAsync(
        IReadOnlyDictionary<string, DerivedRecapArtifact>?
            strictArtifactsById,
        CancellationToken cancellationToken
    ) {
        var sets = new List<DerivedArtifactSet>();
        var memberArtifactCache =
            strictArtifactsById is null
                ? new Dictionary<string, DerivedRecapArtifact>(
                    StringComparer.Ordinal
                )
                : null;
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            SetsDirectory
        );
        if (Directory.Exists(SetsDirectory)) {
            foreach (string path in EnumeratePersistedJsonFiles(
                         SetsDirectory,
                         "Derived ArtifactSet"
                     )) {
                cancellationToken.ThrowIfCancellationRequested();
                DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                        path,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                DerivedArtifactSet set = MaterializeAndValidateSelf(dto);
                RequireExactSetFileName(path, set.SetId);
                foreach (DerivedArtifactSetMember member in set.Members) {
                    DerivedRecapArtifact artifact;
                    if (strictArtifactsById is not null) {
                        artifact = strictArtifactsById.TryGetValue(
                            member.ArtifactId,
                            out DerivedRecapArtifact? strictArtifact
                        )
                            ? strictArtifact
                            : throw new InvalidDataException(
                                $"ArtifactSet member '{member.ArtifactId}' is missing."
                            );
                    }
                    else if (!memberArtifactCache!.TryGetValue(
                                 member.ArtifactId,
                                 out artifact!
                             )) {
                        artifact = await _repository.Recaps
                            .TryReadArtifactAsync(
                                member.ArtifactId,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                            ?? throw new InvalidDataException(
                                $"ArtifactSet member '{member.ArtifactId}' is missing or unusable."
                            );
                        memberArtifactCache.Add(
                            member.ArtifactId,
                            artifact
                        );
                    }
                    ValidateArtifactAgainstMember(
                        artifact,
                        member,
                        set.CommonAnchor,
                        set.AnchorSetups
                    );
                }
                sets.Add(set);
            }
        }

        var pointers = new List<DerivedArtifactSetLatestPointer>();
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            LatestPointersDirectory
        );
        if (Directory.Exists(LatestPointersDirectory)) {
            foreach (string path in EnumeratePersistedJsonFiles(
                         LatestPointersDirectory,
                         "Derived ArtifactSet latest pointer"
                     )) {
                cancellationToken.ThrowIfCancellationRequested();
                pointers.Add(
                    await ReadAndValidatePointerSelfAsync(
                            path,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            }
        }

        return new DerivedArtifactSetInventory(
            Array.AsReadOnly([
                .. sets
                    .OrderBy(static set => set.LineageKey, StringComparer.Ordinal)
                    .ThenBy(static set => set.CoherenceGroup, StringComparer.Ordinal)
                    .ThenBy(static set => set.PolicyId, StringComparer.Ordinal)
                    .ThenBy(static set => set.PolicyFingerprint, StringComparer.Ordinal)
                    .ThenBy(static set => set.SetId, StringComparer.Ordinal)
            ]),
            Array.AsReadOnly([
                .. pointers
                    .OrderBy(static pointer => pointer.LineageKey, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.CoherenceGroup, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.PolicyId, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.PolicyFingerprint, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.SetId, StringComparer.Ordinal)
            ])
        );
    }

    public async ValueTask<DerivedArtifactSet?> RebuildLatestPointerAsync(
        DerivedArtifactSetPolicy policy,
        string lineageKey,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateLineageKey(lineageKey);

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(SetsDirectory);
        _repository.EnsureDirectory(LatestPointersDirectory);

        var matching = new Dictionary<string, DerivedArtifactSet>(
            StringComparer.Ordinal
        );
        foreach (string path in Directory.EnumerateFiles(
                     SetsDirectory,
                     "das_*.json"
                 )) {
            cancellationToken.ThrowIfCancellationRequested();
            DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                    path,
                    cancellationToken
                )
                .ConfigureAwait(false);
            RequireExactSetFileName(path, dto.SetId);
            if (!string.Equals(
                    dto.LineageKey,
                    lineageKey,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    dto.CoherenceGroup,
                    policy.CoherenceGroup,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    dto.PolicyId,
                    policy.PolicyId,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    dto.PolicyFingerprint,
                    policy.PolicyFingerprint,
                    StringComparison.Ordinal
                )) {
                continue;
            }
            DerivedArtifactSet set = MaterializeAndValidate(
                dto,
                policy,
                lineageKey
            );
            if (!matching.TryAdd(set.SetId, set)) {
                throw new InvalidDataException(
                    $"Duplicate ArtifactSet id '{set.SetId}'."
                );
            }
        }
        if (matching.Count == 0) {
            return null;
        }

        var predecessorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (DerivedArtifactSet set in matching.Values) {
            if (set.PreviousSetId is { } previous) {
                if (!matching.ContainsKey(previous)) {
                    throw new InvalidDataException(
                        $"ArtifactSet '{set.SetId}' references missing previous set '{previous}'."
                    );
                }
                predecessorIds.Add(previous);
            }
        }
        DerivedArtifactSet[] tips = [
            .. matching.Values.Where(
                set => !predecessorIds.Contains(set.SetId)
            )
        ];
        if (tips.Length != 1) {
            throw new InvalidDataException(
                "Cannot rebuild latest ArtifactSet pointer from a forked or cyclic lineage."
            );
        }
        ValidateCompleteAcyclicLineage(tips[0], matching);

        var pointer = new DerivedArtifactSetLatestPointerDto(
            LatestPointerSchema,
            lineageKey,
            policy.CoherenceGroup,
            policy.PolicyId,
            policy.PolicyFingerprint,
            tips[0].SetId
        );
        string pointerJson = JsonSerializer.Serialize(pointer, JsonOptions);
        EnsureSerializedSize(
            pointerJson,
            MaxLatestPointerFileBytes,
            "Derived ArtifactSet latest pointer"
        );
        await _repository.WriteFileAtomicallyAsync(
                GetLatestPointerPath(policy, lineageKey),
                pointerJson,
                overwrite: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return tips[0];
    }

    internal async ValueTask<DerivedRecapArtifact>
        ReadAndValidateMemberArtifactAsync(
        DerivedArtifactSet set,
        DerivedArtifactSetMember member,
        CancellationToken cancellationToken
    ) {
        DerivedRecapArtifact artifact = await _repository.Recaps
            .TryReadArtifactAsync(member.ArtifactId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"ArtifactSet member '{member.ArtifactId}' is missing or unusable."
            );
        ValidateArtifactAgainstMember(
            artifact,
            member,
            set.CommonAnchor,
            set.AnchorSetups
        );
        return artifact;
    }

    private async ValueTask<DerivedArtifactSet> CreateSetAsync(
        DerivedArtifactSetPublicationRequest request,
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles,
        IReadOnlyList<DerivedArtifactSetMemberSelection> selections,
        CancellationToken cancellationToken
    ) {
        var selectedRoles = new HashSet<string>(StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        var members = new List<DerivedArtifactSetMember>(selections.Count);
        EventAddress? commonAnchor = null;
        foreach (DerivedArtifactSetMemberSelection selection in selections) {
            if (!roles.TryGetValue(
                    selection.RoleId,
                    out DerivedArtifactSetRoleRequirement? requirement
                )) {
                throw new ArgumentException(
                    $"Artifact-set role '{selection.RoleId}' is not declared by policy.",
                    nameof(request)
                );
            }
            if (!selectedRoles.Add(selection.RoleId)
                || !artifactIds.Add(selection.ArtifactId)) {
                throw new ArgumentException(
                    "Artifact-set roles and exact artifact ids must be unique.",
                    nameof(request)
                );
            }
            DerivedRecapArtifact artifact = await _repository.Recaps
                .TryReadArtifactAsync(
                    selection.ArtifactId,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Exact derived artifact '{selection.ArtifactId}' is missing or unusable."
                );
            if (artifact.Target != requirement.Target) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' target does not match role '{selection.RoleId}'."
                );
            }
            if (!targets.Add((
                    artifact.Target.Carrier,
                    artifact.Target.BlockKey
                ))) {
                throw new InvalidDataException(
                    "Artifact-set member targets must be unique."
                );
            }
            commonAnchor ??= artifact.AnchorRawEvent;
            if (artifact.AnchorRawEvent != commonAnchor
                || artifact.SourceEndInclusive != commonAnchor) {
                throw new InvalidDataException(
                    "Artifact-set members require one exact common coverage anchor."
                );
            }
            if (artifact.GoverningRuntimeConfigSetup
                    != request.AnchorSetups.RuntimeConfig.Address
                || artifact.GoverningSystemPromptSetup
                    != request.AnchorSetups.SystemPrompt.Address) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' governing setup does not match publication anchor setup."
                );
            }
            if (!artifact.MemoryPack.TryGetBlock(
                    artifact.Target,
                    out MemoryPackBlock block
                )
                || !string.Equals(
                    block.Text,
                    artifact.Content,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' target content is inconsistent."
                );
            }
            members.Add(new DerivedArtifactSetMember(
                selection.RoleId,
                artifact.ArtifactId,
                artifact.ArtifactKind,
                artifact.Target,
                SessionContextContributionHasher.CodecId,
                SessionContextContributionHasher.ComputeSha256(block.Text),
                artifact.SourceRawHead
            ));
        }
        foreach (DerivedArtifactSetRoleRequirement required in
                 roles.Values.Where(static role => role.Required)) {
            if (!selectedRoles.Contains(required.RoleId)) {
                throw new InvalidDataException(
                    $"Artifact-set publication is missing required role '{required.RoleId}'."
                );
            }
        }
        if (commonAnchor is null) {
            throw new InvalidDataException(
                "Artifact-set publication requires at least one member."
            );
        }

        DerivedArtifactSetMember[] canonicalMembers = [
            .. members.OrderBy(
                static member => member.RoleId,
                StringComparer.Ordinal
            )
        ];
        DerivedArtifactSetRoleRequirement[] canonicalRoleRequirements =
            CanonicalizeRoleRequirements(roles.Values);
        var identity = CreateIdentityDto(
            request.LineageKey,
            request.Policy,
            canonicalRoleRequirements,
            request.ExpectedPreviousSetId,
            commonAnchor.Value,
            request.AnchorSetups,
            canonicalMembers
        );
        string setId = ComputeSetId(identity);
        return new DerivedArtifactSet(
            setId,
            request.LineageKey,
            request.Policy.CoherenceGroup,
            request.Policy.PolicyId,
            request.Policy.PolicyFingerprint,
            Array.AsReadOnly(canonicalRoleRequirements),
            request.ExpectedPreviousSetId,
            commonAnchor.Value,
            request.AnchorSetups,
            Array.AsReadOnly(canonicalMembers)
        );
    }

    private static DerivedArtifactSetMemberSelection[] SnapshotSelections(
        IReadOnlyList<DerivedArtifactSetMemberSelection> selections
    ) {
        var result = new List<DerivedArtifactSetMemberSelection>();
        foreach (DerivedArtifactSetMemberSelection selection in selections) {
            if (result.Count == MaxMemberCount) {
                throw new ArgumentException(
                    $"Artifact-set publication supports at most {MaxMemberCount} members.",
                    nameof(selections)
                );
            }
            ArgumentNullException.ThrowIfNull(selection);
            DerivedArtifactSetPolicy.ValidateToken(
                selection.RoleId,
                nameof(selection.RoleId)
            );
            ValidateArtifactId(selection.ArtifactId);
            result.Add(selection);
        }
        if (result.Count == 0) {
            throw new ArgumentException(
                "Artifact-set publication requires at least one member.",
                nameof(selections)
            );
        }
        return [.. result];
    }

    private async ValueTask<DerivedArtifactSetLatestPointerDto?>
        TryReadLatestPointerDtoAsync(
        DerivedArtifactSetPolicy policy,
        string lineageKey,
        CancellationToken cancellationToken
    ) {
        string path = GetLatestPointerPath(policy, lineageKey);
        if (!File.Exists(path)) {
            return null;
        }
        DerivedArtifactSetLatestPointerDto? pointer;
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            EnsureStreamWithinLimit(
                stream,
                MaxLatestPointerFileBytes,
                "Derived ArtifactSet latest pointer",
                path
            );
            pointer = await JsonSerializer.DeserializeAsync<
                    DerivedArtifactSetLatestPointerDto
                >(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer is malformed: {path}",
                exception
            );
        }
        if (pointer is null
            || !string.Equals(
                pointer.Schema,
                LatestPointerSchema,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pointer.LineageKey,
                lineageKey,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pointer.CoherenceGroup,
                policy.CoherenceGroup,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pointer.PolicyId,
                policy.PolicyId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pointer.PolicyFingerprint,
                policy.PolicyFingerprint,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer does not match its exact lineage/policy key: {path}"
            );
        }
        ValidateSetId(pointer.SetId);
        return pointer;
    }

    private async ValueTask<DerivedArtifactSetLatestPointer>
        ReadAndValidatePointerSelfAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        DerivedArtifactSetLatestPointerDto? dto;
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            EnsureStreamWithinLimit(
                stream,
                MaxLatestPointerFileBytes,
                "Derived ArtifactSet latest pointer",
                path
            );
            dto = await JsonSerializer.DeserializeAsync<
                    DerivedArtifactSetLatestPointerDto
                >(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer is malformed: {path}",
                exception
            );
        }
        if (dto is null
            || !string.Equals(
                dto.Schema,
                LatestPointerSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer schema is invalid: {path}"
            );
        }
        try {
            DerivedArtifactSetPolicy.ValidateLineageKey(dto.LineageKey);
            DerivedArtifactSetPolicy.ValidateToken(
                dto.CoherenceGroup,
                nameof(dto.CoherenceGroup)
            );
            DerivedArtifactSetPolicy.ValidateToken(
                dto.PolicyId,
                nameof(dto.PolicyId)
            );
            DerivedArtifactSetPolicy.ValidateToken(
                dto.PolicyFingerprint,
                nameof(dto.PolicyFingerprint)
            );
            ValidateSetId(dto.SetId);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer identity is invalid: {path}",
                exception
            );
        }
        string expectedFileName =
            ComputeLatestKey(
                dto.LineageKey,
                dto.CoherenceGroup,
                dto.PolicyId,
                dto.PolicyFingerprint
            ) + ".json";
        if (!string.Equals(
                Path.GetFileName(path),
                expectedFileName,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer filename does not match its exact key: {path}"
            );
        }
        return new DerivedArtifactSetLatestPointer(
            dto.LineageKey,
            dto.CoherenceGroup,
            dto.PolicyId,
            dto.PolicyFingerprint,
            dto.SetId
        );
    }

    private async ValueTask<DerivedArtifactSet> ReadSetRequiredAsync(
        string setId,
        DerivedArtifactSetPolicy policy,
        string lineageKey,
        CancellationToken cancellationToken
    ) {
        string path = GetSetPath(setId);
        if (!File.Exists(path)) {
            throw new InvalidDataException(
                $"Derived ArtifactSet '{setId}' is missing."
            );
        }
        DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                path,
                cancellationToken
            )
            .ConfigureAwait(false);
        DerivedArtifactSet set = MaterializeAndValidate(
            dto,
            policy,
            lineageKey
        );
        if (!string.Equals(set.SetId, setId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Derived ArtifactSet filename/id mismatch for '{setId}'."
            );
        }
        return set;
    }

    private async ValueTask<DerivedArtifactSetDto> ReadDtoRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            EnsureStreamWithinLimit(
                stream,
                MaxSetFileBytes,
                "Derived ArtifactSet",
                path
            );
            return await JsonSerializer.DeserializeAsync<DerivedArtifactSetDto>(
                    stream,
                    JsonOptions,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Derived ArtifactSet is empty: {path}"
                );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet is malformed: {path}",
                exception
            );
        }
    }

    private static DerivedArtifactSet MaterializeAndValidate(
        DerivedArtifactSetDto dto,
        DerivedArtifactSetPolicy policy,
        string lineageKey
    ) {
        DerivedArtifactSet set = MaterializeAndValidateSelf(dto);
        if (!string.Equals(set.LineageKey, lineageKey, StringComparison.Ordinal)
            || !string.Equals(
                set.CoherenceGroup,
                policy.CoherenceGroup,
                StringComparison.Ordinal
            )
            || !string.Equals(set.PolicyId, policy.PolicyId, StringComparison.Ordinal)
            || !string.Equals(
                set.PolicyFingerprint,
                policy.PolicyFingerprint,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet schema, lineage, or policy mismatch."
            );
        }
        DerivedArtifactSetRoleRequirement[] callerRoleRequirements =
            CanonicalizeRoleRequirements(
                policy.ValidateAndSnapshot().Values
            );
        if (!set.RoleRequirements.SequenceEqual(
                callerRoleRequirements
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet role requirements do not match the caller policy."
            );
        }
        return set;
    }

    private static DerivedArtifactSet MaterializeAndValidateSelf(
        DerivedArtifactSetDto dto
    ) {
        try {
            return MaterializeAndValidateSelfCore(dto);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException
        ) {
            throw new InvalidDataException(
                "Derived ArtifactSet contains an invalid persisted value.",
                exception
            );
        }
    }

    private static DerivedArtifactSet MaterializeAndValidateSelfCore(
        DerivedArtifactSetDto dto
    ) {
        if (!string.Equals(dto.Schema, SetSchema, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Derived ArtifactSet schema is invalid."
            );
        }
        DerivedArtifactSetRoleRequirement[] persistedRoleRequirements =
            MaterializeRoleRequirements(dto.RoleRequirements);
        var persistedPolicy = new DerivedArtifactSetPolicy(
            dto.PolicyId,
            dto.PolicyFingerprint,
            dto.CoherenceGroup,
            persistedRoleRequirements
        );
        try {
            DerivedArtifactSetPolicy.ValidateLineageKey(dto.LineageKey);
            _ = persistedPolicy.ValidateAndSnapshot();
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                "Derived ArtifactSet lineage or policy identity is invalid.",
                exception
            );
        }
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles =
            persistedRoleRequirements.ToDictionary(
                static role => role.RoleId,
                StringComparer.Ordinal
            );
        ValidateSetId(dto.SetId);
        if (dto.PreviousSetId is not null) {
            ValidateSetId(dto.PreviousSetId);
        }
        EventAddress commonAnchor =
            EventAddressTextCodec.Parse(dto.CommonAnchor);
        SessionContextAnchorSetupReferences setups =
            MaterializeSetupReferences(dto.AnchorSetups);
        ValidateSetupReferences(setups);
        if (dto.Members is null
            || dto.Members.Count is 0 or > MaxMemberCount) {
            throw new InvalidDataException(
                $"Derived ArtifactSet must contain 1 through {MaxMemberCount} members."
            );
        }

        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        var members = new List<DerivedArtifactSetMember>(
            dto.Members.Count
        );
        string? previousRole = null;
        foreach (DerivedArtifactSetMemberDto member in dto.Members) {
            if (member is null) {
                throw new InvalidDataException(
                    "Derived ArtifactSet contains a null member."
                );
            }
            DerivedArtifactSetPolicy.ValidateToken(
                member.RoleId,
                nameof(member.RoleId)
            );
            ValidateArtifactId(member.ArtifactId);
            DerivedArtifactSetPolicy.ValidateToken(
                member.ArtifactKind,
                nameof(member.ArtifactKind)
            );
            if (!roles.TryGetValue(
                    member.RoleId,
                    out DerivedArtifactSetRoleRequirement? role
                )) {
                throw new InvalidDataException(
                    $"Derived ArtifactSet contains undeclared role '{member.RoleId}'."
                );
            }
            MemoryPackBlockPath target = MaterializeTarget(member.Target);
            if (target != role.Target
                || !roleIds.Add(member.RoleId)
                || !artifactIds.Add(member.ArtifactId)
                || !targets.Add((target.Carrier, target.BlockKey))) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role, artifact, or target membership is invalid."
                );
            }
            if (previousRole is not null
                && string.CompareOrdinal(previousRole, member.RoleId) >= 0) {
                throw new InvalidDataException(
                    "Derived ArtifactSet members are not in canonical role order."
                );
            }
            previousRole = member.RoleId;
            if (!string.Equals(
                    member.ContentCodecId,
                    SessionContextContributionHasher.CodecId,
                    StringComparison.Ordinal
                )
                || !IsLowerSha256(member.ContentSha256)) {
                throw new InvalidDataException(
                    "Derived ArtifactSet member content identity is invalid."
                );
            }
            EventAddress sourceRawHead =
                EventAddressTextCodec.Parse(member.SourceRawHead);
            members.Add(new DerivedArtifactSetMember(
                member.RoleId,
                member.ArtifactId,
                member.ArtifactKind,
                target,
                member.ContentCodecId,
                member.ContentSha256,
                sourceRawHead
            ));
        }
        foreach (DerivedArtifactSetRoleRequirement required in
                 roles.Values.Where(static role => role.Required)) {
            if (!roleIds.Contains(required.RoleId)) {
                throw new InvalidDataException(
                    $"Derived ArtifactSet is missing required role '{required.RoleId}'."
                );
            }
        }
        DerivedArtifactSetMember[] frozenMembers = [.. members];
        var identity = CreateIdentityDto(
            dto.LineageKey,
            persistedPolicy,
            persistedRoleRequirements,
            dto.PreviousSetId,
            commonAnchor,
            setups,
            frozenMembers
        );
        if (!string.Equals(
                dto.SetId,
                ComputeSetId(identity),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet '{dto.SetId}' identity hash is invalid."
            );
        }
        return new DerivedArtifactSet(
            dto.SetId,
            dto.LineageKey,
            dto.CoherenceGroup,
            dto.PolicyId,
            dto.PolicyFingerprint,
            Array.AsReadOnly(persistedRoleRequirements),
            dto.PreviousSetId,
            commonAnchor,
            setups,
            Array.AsReadOnly(frozenMembers)
        );
    }

    private static void ValidateArtifactAgainstMember(
        DerivedRecapArtifact artifact,
        DerivedArtifactSetMember member,
        EventAddress commonAnchor,
        SessionContextAnchorSetupReferences anchorSetups
    ) {
        if (!string.Equals(
                artifact.Status,
                DerivedRecapArtifactStatus.Produced,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ArtifactId,
                member.ArtifactId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ArtifactKind,
                member.ArtifactKind,
                StringComparison.Ordinal
            )
            || artifact.Target != member.Target
            || artifact.SourceRawHead != member.SourceRawHead
            || artifact.AnchorRawEvent != commonAnchor
            || artifact.SourceEndInclusive != commonAnchor
            || artifact.GoverningRuntimeConfigSetup
                != anchorSetups.RuntimeConfig.Address
            || artifact.GoverningSystemPromptSetup
                != anchorSetups.SystemPrompt.Address
            || !artifact.MemoryPack.TryGetBlock(
                artifact.Target,
                out MemoryPackBlock block
            )
            || !string.Equals(
                artifact.Content,
                block.Text,
                StringComparison.Ordinal
            )
            || !string.Equals(
                member.ContentCodecId,
                SessionContextContributionHasher.CodecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                member.ContentSha256,
                SessionContextContributionHasher.ComputeSha256(block.Text),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet member '{member.ArtifactId}' does not match its exact artifact."
            );
        }
    }

    private static void ValidateCompleteAcyclicLineage(
        DerivedArtifactSet tip,
        IReadOnlyDictionary<string, DerivedArtifactSet> sets
    ) {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        DerivedArtifactSet? cursor = tip;
        while (cursor is not null) {
            if (!visited.Add(cursor.SetId)) {
                throw new InvalidDataException(
                    "Derived ArtifactSet lineage contains a cycle."
                );
            }
            cursor = cursor.PreviousSetId is { } previous
                ? sets[previous]
                : null;
        }
        if (visited.Count != sets.Count) {
            throw new InvalidDataException(
                "Derived ArtifactSet lineage is disconnected or forked."
            );
        }
    }

    private static bool SetsEquivalent(
        DerivedArtifactSet left,
        DerivedArtifactSet right
    ) =>
        string.Equals(left.SetId, right.SetId, StringComparison.Ordinal)
        && string.Equals(
            left.LineageKey,
            right.LineageKey,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.CoherenceGroup,
            right.CoherenceGroup,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.PolicyId,
            right.PolicyId,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.PolicyFingerprint,
            right.PolicyFingerprint,
            StringComparison.Ordinal
        )
        && left.RoleRequirements.SequenceEqual(right.RoleRequirements)
        && string.Equals(
            left.PreviousSetId,
            right.PreviousSetId,
            StringComparison.Ordinal
        )
        && left.CommonAnchor == right.CommonAnchor
        && left.AnchorSetups == right.AnchorSetups
        && left.Members.SequenceEqual(right.Members);

    private static void RequireExactSetFileName(
        string path,
        string setId
    ) {
        try {
            ValidateSetId(setId);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet id is invalid in persisted file: {path}",
                exception
            );
        }
        if (!string.Equals(
                Path.GetFileName(path),
                $"{setId}.json",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet filename does not exactly match its set id: {path}"
            );
        }
    }

    private static void EnsureStreamWithinLimit(
        FileStream stream,
        long maximumBytes,
        string description,
        string path
    ) {
        if (stream.Length > maximumBytes) {
            throw new InvalidDataException(
                $"{description} exceeds its {maximumBytes}-byte limit: {path}"
            );
        }
    }

    private static void EnsureSerializedSize(
        string serialized,
        long maximumBytes,
        string description
    ) {
        if (Encoding.UTF8.GetByteCount(serialized) > maximumBytes) {
            throw new InvalidDataException(
                $"{description} exceeds its {maximumBytes}-byte limit."
            );
        }
    }

    private string GetSetPath(string setId) {
        ValidateSetId(setId);
        return Path.Combine(SetsDirectory, $"{setId}.json");
    }

    private string GetLatestPointerPath(
        DerivedArtifactSetPolicy policy,
        string lineageKey
    ) => Path.Combine(
        LatestPointersDirectory,
        $"{ComputeLatestKey(policy, lineageKey)}.json"
    );

    private static string ComputeLatestKey(
        DerivedArtifactSetPolicy policy,
        string lineageKey
    ) => ComputeLatestKey(
        lineageKey,
        policy.CoherenceGroup,
        policy.PolicyId,
        policy.PolicyFingerprint
    );

    private static string ComputeLatestKey(
        string lineageKey,
        string coherenceGroup,
        string policyId,
        string policyFingerprint
    ) {
        string identity = string.Join(
            '\0',
            lineageKey,
            coherenceGroup,
            policyId,
            policyFingerprint
        );
        return "latest_" + ComputeDomainHash(LatestKeyDomain, identity);
    }

    private static IEnumerable<string> EnumeratePersistedJsonFiles(
        string directory,
        string description
    ) {
        foreach (string path in Directory
                     .EnumerateFiles(directory)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)) {
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"{description} directory contains an unexpected file: {path}"
                );
            }
            yield return path;
        }
    }

    private static string ComputeSetId(
        DerivedArtifactSetIdentityDto identity
    ) {
        byte[] canonical =
            JsonSerializer.SerializeToUtf8Bytes(
                identity,
                IdentityJsonOptions
            );
        return "das_" + ComputeDomainHash(SetIdDomain, canonical);
    }

    private static string ComputeDomainHash(string domain, string value) =>
        ComputeDomainHash(domain, Encoding.UTF8.GetBytes(value));

    private static string ComputeDomainHash(
        string domain,
        ReadOnlySpan<byte> value
    ) {
        byte[] prefix = Encoding.UTF8.GetBytes(domain + "\0");
        byte[] input = new byte[checked(prefix.Length + value.Length)];
        prefix.CopyTo(input, 0);
        value.CopyTo(input.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static DerivedArtifactSetDto ToDto(DerivedArtifactSet set) =>
        new(
            SetSchema,
            set.SetId,
            set.LineageKey,
            set.CoherenceGroup,
            set.PolicyId,
            set.PolicyFingerprint,
            set.RoleRequirements.Select(ToDto).ToArray(),
            set.PreviousSetId,
            EventAddressTextCodec.Format(set.CommonAnchor),
            ToDto(set.AnchorSetups),
            set.Members.Select(ToDto).ToArray()
        );

    private static DerivedArtifactSetIdentityDto CreateIdentityDto(
        string lineageKey,
        DerivedArtifactSetPolicy policy,
        IReadOnlyList<DerivedArtifactSetRoleRequirement> roleRequirements,
        string? previousSetId,
        EventAddress commonAnchor,
        SessionContextAnchorSetupReferences setups,
        IReadOnlyList<DerivedArtifactSetMember> members
    ) => new(
        SetSchema,
        lineageKey,
        policy.CoherenceGroup,
        policy.PolicyId,
        policy.PolicyFingerprint,
        roleRequirements.Select(ToDto).ToArray(),
        previousSetId,
        EventAddressTextCodec.Format(commonAnchor),
        ToDto(setups),
        members.Select(ToDto).ToArray()
    );

    private static DerivedArtifactSetMemberDto ToDto(
        DerivedArtifactSetMember member
    ) => new(
        member.RoleId,
        member.ArtifactId,
        member.ArtifactKind,
        new DerivedArtifactSetTargetDto(
            MemoryPackCarrierTokens.ToStorageToken(member.Target.Carrier),
            member.Target.BlockKey
        ),
        member.ContentCodecId,
        member.ContentSha256,
        EventAddressTextCodec.Format(member.SourceRawHead)
    );

    private static DerivedArtifactSetRoleRequirementDto ToDto(
        DerivedArtifactSetRoleRequirement requirement
    ) => new(
        requirement.RoleId,
        new DerivedArtifactSetTargetDto(
            MemoryPackCarrierTokens.ToStorageToken(
                requirement.Target.Carrier
            ),
            requirement.Target.BlockKey
        ),
        requirement.Required
    );

    private static DerivedArtifactSetRoleRequirement[]
        CanonicalizeRoleRequirements(
        IEnumerable<DerivedArtifactSetRoleRequirement> requirements
    ) => [
        .. requirements
            .OrderBy(static role => role.RoleId, StringComparer.Ordinal)
            .Select(static role =>
                new DerivedArtifactSetRoleRequirement(
                    role.RoleId,
                    new MemoryPackBlockPath(
                        role.Target.Carrier,
                        role.Target.BlockKey
                    ),
                    role.Required
                )
            )
    ];

    private static DerivedArtifactSetRoleRequirement[]
        MaterializeRoleRequirements(
        IReadOnlyList<DerivedArtifactSetRoleRequirementDto>? dtos
    ) {
        if (dtos is null
            || dtos.Count is 0 or > DerivedArtifactSetPolicy.MaxRoleCount) {
            throw new InvalidDataException(
                $"Derived ArtifactSet must persist 1 through {DerivedArtifactSetPolicy.MaxRoleCount} role requirements."
            );
        }
        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        var requirements = new List<DerivedArtifactSetRoleRequirement>(
            dtos.Count
        );
        string? previousRole = null;
        bool hasRequiredRole = false;
        foreach (DerivedArtifactSetRoleRequirementDto dto in dtos) {
            if (dto is null) {
                throw new InvalidDataException(
                    "Derived ArtifactSet contains a null role requirement."
                );
            }
            if (dto.Required is null) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role requirement is missing required."
                );
            }
            try {
                DerivedArtifactSetPolicy.ValidateToken(
                    dto.RoleId,
                    nameof(dto.RoleId)
                );
            }
            catch (ArgumentException exception) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role requirement id is invalid.",
                    exception
                );
            }
            MemoryPackBlockPath target = MaterializeTarget(dto.Target);
            if (!roleIds.Add(dto.RoleId)
                || !targets.Add((target.Carrier, target.BlockKey))
                || previousRole is not null
                    && string.CompareOrdinal(
                        previousRole,
                        dto.RoleId
                    ) >= 0) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role requirements are duplicate or not in canonical order."
                );
            }
            previousRole = dto.RoleId;
            hasRequiredRole |= dto.Required.Value;
            requirements.Add(new DerivedArtifactSetRoleRequirement(
                dto.RoleId,
                target,
                dto.Required.Value
            ));
        }
        if (!hasRequiredRole) {
            throw new InvalidDataException(
                "Derived ArtifactSet role requirements need at least one required role."
            );
        }
        return [.. requirements];
    }

    private static DerivedArtifactSetSetupReferencesDto ToDto(
        SessionContextAnchorSetupReferences references
    ) => new(
        ToDto(references.RuntimeConfig),
        ToDto(references.SystemPrompt)
    );

    private static DerivedArtifactSetSetupReferenceDto ToDto(
        SessionContextSetupReference reference
    ) => new(
        EventAddressTextCodec.Format(reference.Address),
        reference.BodySchemaVersion,
        reference.PayloadSha256
    );

    private static SessionContextAnchorSetupReferences
        MaterializeSetupReferences(
        DerivedArtifactSetSetupReferencesDto dto
    ) {
        if (dto is null
            || dto.RuntimeConfig is null
            || dto.SystemPrompt is null) {
            throw new InvalidDataException(
                "Derived ArtifactSet anchor setup references are missing."
            );
        }
        return new SessionContextAnchorSetupReferences(
            MaterializeSetupReference(dto.RuntimeConfig),
            MaterializeSetupReference(dto.SystemPrompt)
        );
    }

    private static SessionContextSetupReference MaterializeSetupReference(
        DerivedArtifactSetSetupReferenceDto dto
    ) => new(
        EventAddressTextCodec.Parse(dto.Address),
        dto.BodySchemaVersion,
        dto.PayloadSha256
    );

    private static MemoryPackBlockPath MaterializeTarget(
        DerivedArtifactSetTargetDto dto
    ) {
        if (dto is null
            || !MemoryPackCarrierTokens.TryParseStorageToken(
                dto.Carrier,
                out MemoryPackCarrier carrier
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet member target carrier is invalid."
            );
        }
        var target = new MemoryPackBlockPath(carrier, dto.BlockKey);
        DerivedArtifactSetPolicy.ValidateTarget(target, nameof(dto));
        return target;
    }

    private static void ValidateSetupReferences(
        SessionContextAnchorSetupReferences references
    ) {
        ArgumentNullException.ThrowIfNull(references.RuntimeConfig);
        ArgumentNullException.ThrowIfNull(references.SystemPrompt);
        ValidateSetupReference(references.RuntimeConfig);
        ValidateSetupReference(references.SystemPrompt);
        if (references.RuntimeConfig.Address
            == references.SystemPrompt.Address) {
            throw new ArgumentException(
                "Runtime and system-prompt setup references must be distinct.",
                nameof(references)
            );
        }
    }

    private static void ValidateSetupReference(
        SessionContextSetupReference reference
    ) {
        if (reference.Address == default
            || reference.BodySchemaVersion <= 0
            || !IsLowerSha256(reference.PayloadSha256)) {
            throw new ArgumentException(
                "Derived ArtifactSet setup reference is invalid.",
                nameof(reference)
            );
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 }
        && value.All(
            static ch => ch is >= '0' and <= '9'
                or >= 'a' and <= 'f'
        );

    private static void ValidateArtifactId(string artifactId) {
        if (string.IsNullOrWhiteSpace(artifactId)
            || artifactId.Length > 256
            || artifactId.Any(
                static ch => !(char.IsAsciiLetterOrDigit(ch)
                    || ch is '_' or '-' or '.')
            )) {
            throw new ArgumentException(
                "Derived artifact id is invalid.",
                nameof(artifactId)
            );
        }
    }

    private static void ValidateSetId(string setId) {
        if (setId is null
            || setId.Length != 68
            || !setId.StartsWith("das_", StringComparison.Ordinal)
            || !setId[4..].All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                "Derived ArtifactSet id is invalid.",
                nameof(setId)
            );
        }
    }
}

internal sealed record DerivedArtifactSetDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string SetId,
    [property: JsonPropertyOrder(2)] string LineageKey,
    [property: JsonPropertyOrder(3)] string CoherenceGroup,
    [property: JsonPropertyOrder(4)] string PolicyId,
    [property: JsonPropertyOrder(5)] string PolicyFingerprint,
    [property: JsonPropertyOrder(6)]
        IReadOnlyList<DerivedArtifactSetRoleRequirementDto> RoleRequirements,
    [property: JsonPropertyOrder(7)] string? PreviousSetId,
    [property: JsonPropertyOrder(8)] string CommonAnchor,
    [property: JsonPropertyOrder(9)]
        DerivedArtifactSetSetupReferencesDto AnchorSetups,
    [property: JsonPropertyOrder(10)]
        IReadOnlyList<DerivedArtifactSetMemberDto> Members
);

internal sealed record DerivedArtifactSetIdentityDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string LineageKey,
    [property: JsonPropertyOrder(2)] string CoherenceGroup,
    [property: JsonPropertyOrder(3)] string PolicyId,
    [property: JsonPropertyOrder(4)] string PolicyFingerprint,
    [property: JsonPropertyOrder(5)]
        IReadOnlyList<DerivedArtifactSetRoleRequirementDto> RoleRequirements,
    [property: JsonPropertyOrder(6)] string? PreviousSetId,
    [property: JsonPropertyOrder(7)] string CommonAnchor,
    [property: JsonPropertyOrder(8)]
        DerivedArtifactSetSetupReferencesDto AnchorSetups,
    [property: JsonPropertyOrder(9)]
        IReadOnlyList<DerivedArtifactSetMemberDto> Members
);

internal sealed record DerivedArtifactSetRoleRequirementDto(
    [property: JsonPropertyOrder(0)] string RoleId,
    [property: JsonPropertyOrder(1)] DerivedArtifactSetTargetDto Target,
    [property: JsonPropertyOrder(2)] bool? Required
);

internal sealed record DerivedArtifactSetMemberDto(
    [property: JsonPropertyOrder(0)] string RoleId,
    [property: JsonPropertyOrder(1)] string ArtifactId,
    [property: JsonPropertyOrder(2)] string ArtifactKind,
    [property: JsonPropertyOrder(3)] DerivedArtifactSetTargetDto Target,
    [property: JsonPropertyOrder(4)] string ContentCodecId,
    [property: JsonPropertyOrder(5)] string ContentSha256,
    [property: JsonPropertyOrder(6)] string SourceRawHead
);

internal sealed record DerivedArtifactSetTargetDto(
    [property: JsonPropertyOrder(0)] string Carrier,
    [property: JsonPropertyOrder(1)] string BlockKey
);

internal sealed record DerivedArtifactSetSetupReferencesDto(
    [property: JsonPropertyOrder(0)]
        DerivedArtifactSetSetupReferenceDto RuntimeConfig,
    [property: JsonPropertyOrder(1)]
        DerivedArtifactSetSetupReferenceDto SystemPrompt
);

internal sealed record DerivedArtifactSetSetupReferenceDto(
    [property: JsonPropertyOrder(0)] string Address,
    [property: JsonPropertyOrder(1)] int BodySchemaVersion,
    [property: JsonPropertyOrder(2)] string PayloadSha256
);

internal sealed record DerivedArtifactSetLatestPointerDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string LineageKey,
    [property: JsonPropertyOrder(2)] string CoherenceGroup,
    [property: JsonPropertyOrder(3)] string PolicyId,
    [property: JsonPropertyOrder(4)] string PolicyFingerprint,
    [property: JsonPropertyOrder(5)] string SetId
);

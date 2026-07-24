using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.Diagnostics;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Derived;

public sealed class DerivedRecapStore {
    public const string ArtifactSchema = "atelia.session-journal.derived-recap.v1";
    public const string MemoryPackSnapshotSchema = "atelia.session-journal.memory-pack.snapshot.v1";
    public const string LatestIndexSchema = "atelia.session-journal.derived-recap.latest-index.v1";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _repoRoot;

    private DerivedRecapStore(string repoRoot) {
        _repoRoot = repoRoot;
        StoreRoot = Path.Combine(_repoRoot, "derived", "recaps", "v1");
        ArtifactsDirectory = Path.Combine(StoreRoot, "artifacts");
        IndexesDirectory = Path.Combine(StoreRoot, "indexes");
        LatestIndexPath = Path.Combine(IndexesDirectory, "latest-by-profile.json");
    }

    public string StoreRoot { get; }

    public string ArtifactsDirectory { get; }

    public string IndexesDirectory { get; }

    public string LatestIndexPath { get; }

    public static DerivedRecapStore Open(string sessionJournalRepoPath) {
        if (string.IsNullOrWhiteSpace(sessionJournalRepoPath)) {
            throw new ArgumentException("SessionJournal repo path cannot be empty.", nameof(sessionJournalRepoPath));
        }

        return new DerivedRecapStore(Path.GetFullPath(sessionJournalRepoPath));
    }

    public async ValueTask<DerivedRecapArtifact> WriteProducedAsync(
        DerivedRecapWriteRequest request,
        CancellationToken ct = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(ArtifactsDirectory);
        Directory.CreateDirectory(IndexesDirectory);

        var target = DerivedRecapTarget.FromMemoryPackBlockPath(request.Target);
        string lineageKey = DerivedRecapLineageKey.Create(
            request.ArtifactKind,
            request.ProfileId,
            target
        ).Value;
        var memoryPack = MemoryPackSnapshotDto.FromMemoryPack(request.MemoryPack);
        if (!request.MemoryPack.TryGetBlock(request.Target, out var targetBlock)) {
            throw new ArgumentException(
                $"MemoryPack target block does not exist: {target.Carrier}/{target.BlockKey}",
                nameof(request)
            );
        }

        var content = DerivedRecapContentDto.Inline(targetBlock.Text);
        var identity = new DerivedRecapArtifactIdentityDto(
            Schema: ArtifactSchema,
            ArtifactKind: request.ArtifactKind,
            LineageKey: lineageKey,
            ProfileId: request.ProfileId,
            Producer: request.Producer,
            ProducerFingerprint: request.ProducerFingerprint,
            SourceRawHead: EventAddressTextCodec.Format(request.SourceRawHead),
            SourceStartExclusive: EventAddressTextCodec.FormatNullable(request.SourceStartExclusive),
            SourceEndInclusive: EventAddressTextCodec.Format(request.SourceEndInclusive),
            AnchorRawEvent: EventAddressTextCodec.Format(request.AnchorRawEvent),
            GoverningRuntimeConfigSetup: EventAddressTextCodec.Format(request.GoverningRuntimeConfigSetup),
            GoverningSystemPromptSetup: EventAddressTextCodec.Format(request.GoverningSystemPromptSetup),
            PreviousArtifact: request.PreviousArtifact,
            InputArtifacts: FreezeStrings(request.InputArtifacts),
            Target: target,
            MemoryPack: memoryPack,
            Content: content,
            Invocation: request.Invocation,
            CallLogPaths: FreezeStrings(request.CallLogPaths),
            Status: DerivedRecapArtifactStatus.Produced
        );

        string sourceEndShort = EventAddressTextCodec.Format(request.SourceEndInclusive)[4..16];
        string hashShort = ComputeCanonicalSha256Hex(identity)[..16];
        string baseArtifactId = $"{GetArtifactKindPrefix(request.ArtifactKind)}_{sourceEndShort}_{hashShort}";
        string artifactId = baseArtifactId;
        string json;
        DerivedRecapArtifactDto dto;
        int suffix = 2;
        while (true) {
            dto = DerivedRecapArtifactDto.FromIdentity(
                ArtifactId: artifactId,
                CreatedUtc: request.CreatedUtc ?? DateTimeOffset.UtcNow,
                identity
            );
            json = Serialize(dto);
            string finalPath = GetArtifactPath(artifactId);
            if (!File.Exists(finalPath)) { break; }

            var existingDto = await TryReadArtifactDtoAsync(finalPath, ct).ConfigureAwait(false);
            if (existingDto is not null &&
                string.Equals(ComputeIdentityHash(existingDto), ComputeCanonicalSha256Hex(identity), StringComparison.Ordinal)) {
                await RebuildLatestIndexAsync(ct).ConfigureAwait(false);
                return Materialize(existingDto);
            }

            artifactId = $"{baseArtifactId}_{suffix.ToString(CultureInfo.InvariantCulture)}";
            suffix++;
        }

        await WriteFileAtomicallyAsync(GetArtifactPath(artifactId), json, overwrite: false, ct).ConfigureAwait(false);
        await RebuildLatestIndexAsync(ct).ConfigureAwait(false);
        return Materialize(dto);
    }

    public async ValueTask<DerivedRecapArtifact?> TryReadLatestAsync(
        DerivedRecapLineageKey lineageKey,
        CancellationToken ct = default
    ) {
        if (string.IsNullOrWhiteSpace(lineageKey.Value)) { throw new ArgumentException("Lineage key cannot be empty.", nameof(lineageKey)); }

        var indexDto = await TryReadLatestIndexFileAsync(ct).ConfigureAwait(false);
        if (indexDto is not null &&
            indexDto.Items.TryGetValue(lineageKey.Value, out var entry) &&
            await TryReadArtifactAsync(entry.ArtifactId, ct).ConfigureAwait(false) is { } indexedArtifact) {
            return indexedArtifact;
        }

        var rebuilt = await RebuildLatestIndexAsync(ct).ConfigureAwait(false);
        if (!rebuilt.Items.TryGetValue(lineageKey, out var rebuiltEntry)) { return null; }
        return await TryReadArtifactAsync(rebuiltEntry.ArtifactId, ct).ConfigureAwait(false);
    }

    public async ValueTask<DerivedRecapArtifact?> TryReadArtifactAsync(
        string artifactId,
        CancellationToken ct = default
    ) {
        if (!IsSafeArtifactId(artifactId)) { throw new ArgumentException("Artifact id contains invalid characters.", nameof(artifactId)); }

        string path = GetArtifactPath(artifactId);
        if (!File.Exists(path)) { return null; }

        var dto = await TryReadArtifactDtoAsync(path, ct).ConfigureAwait(false);
        return dto is null ? null : Materialize(dto);
    }

    public async ValueTask<DerivedRecapLatestIndex> RebuildLatestIndexAsync(
        CancellationToken ct = default
    ) {
        Directory.CreateDirectory(ArtifactsDirectory);
        Directory.CreateDirectory(IndexesDirectory);

        var artifacts = new List<DerivedRecapArtifactDto>();
        foreach (string path in Directory.EnumerateFiles(ArtifactsDirectory, "*.json")) {
            ct.ThrowIfCancellationRequested();
            var dto = await TryReadArtifactDtoAsync(path, ct).ConfigureAwait(false);
            if (dto is not null) { artifacts.Add(dto); }
        }

        var items = new SortedDictionary<string, DerivedRecapLatestIndexItemDto>(StringComparer.Ordinal);
        foreach (var group in artifacts.GroupBy(static artifact => artifact.LineageKey, StringComparer.Ordinal)) {
            var latest = SelectLatest(group.ToArray());
            if (latest is null) { continue; }

            items[group.Key] = new DerivedRecapLatestIndexItemDto(
                ArtifactId: latest.ArtifactId,
                ArtifactPath: $"../artifacts/{latest.ArtifactId}.json",
                SourceRawHead: latest.SourceRawHead,
                AnchorRawEvent: latest.AnchorRawEvent,
                SourceEndInclusive: latest.SourceEndInclusive,
                CreatedUtc: latest.CreatedUtc,
                ProducerFingerprint: latest.ProducerFingerprint
            );
        }

        var indexDto = new DerivedRecapLatestIndexDto(
            Schema: LatestIndexSchema,
            RebuiltUtc: DateTimeOffset.UtcNow,
            Items: items
        );
        await WriteFileAtomicallyAsync(LatestIndexPath, Serialize(indexDto), overwrite: true, ct).ConfigureAwait(false);
        return Materialize(indexDto);
    }

    private async ValueTask<DerivedRecapLatestIndexDto?> TryReadLatestIndexFileAsync(CancellationToken ct) {
        if (!File.Exists(LatestIndexPath)) { return null; }

        try {
            await using var stream = File.OpenRead(LatestIndexPath);
            var dto = await JsonSerializer.DeserializeAsync<DerivedRecapLatestIndexDto>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (!IsUsableLatestIndex(dto)) { return null; }
            return dto;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            DebugUtil.Warning("DerivedRecap", $"Latest index is unreadable and will be rebuilt: {ex.Message}");
            return null;
        }
    }

    private async ValueTask<DerivedRecapArtifactDto?> TryReadArtifactDtoAsync(string path, CancellationToken ct) {
        try {
            await using var stream = File.OpenRead(path);
            var dto = await JsonSerializer.DeserializeAsync<DerivedRecapArtifactDto>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (IsUsableArtifact(dto)) { return dto; }
            DebugUtil.Warning("DerivedRecap", $"Skipping malformed artifact '{path}'.");
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            DebugUtil.Warning("DerivedRecap", $"Skipping unreadable artifact '{path}': {ex.Message}");
            return null;
        }
    }

    private static bool IsUsableArtifact(DerivedRecapArtifactDto? dto) {
        if (dto is null) { return false; }
        if (!string.Equals(dto.Schema, ArtifactSchema, StringComparison.Ordinal)) { return false; }
        if (!string.Equals(dto.Status, DerivedRecapArtifactStatus.Produced, StringComparison.Ordinal)) { return false; }
        if (!IsSafeArtifactId(dto.ArtifactId)) { return false; }
        if (string.IsNullOrWhiteSpace(dto.LineageKey)) { return false; }
        if (dto.Content is null) { return false; }
        if (dto.MemoryPack is null) { return false; }
        if (dto.Target is null) { return false; }
        if (!string.Equals(dto.MemoryPack.Schema, MemoryPackSnapshotSchema, StringComparison.Ordinal)) { return false; }
        if (dto.MemoryPack.System is null || dto.MemoryPack.Observation is null || dto.MemoryPack.Action is null) { return false; }
        if (!string.Equals(dto.Content.Storage, DerivedRecapContentStorage.Inline, StringComparison.Ordinal)) { return false; }
        if (dto.Content.Text is null || dto.Content.Sha256 is null) { return false; }
        if (!string.Equals(dto.Content.Sha256, ComputeSha256Hex(dto.Content.Text), StringComparison.Ordinal)) { return false; }
        if (!TryGetSnapshotBlockText(dto.MemoryPack, dto.Target, out string? targetText)) { return false; }
        if (!string.Equals(targetText, dto.Content.Text, StringComparison.Ordinal)) { return false; }
        return EventAddressTextCodec.TryParse(dto.SourceRawHead, out _) &&
               EventAddressTextCodec.TryParseNullable(dto.SourceStartExclusive, out _) &&
               EventAddressTextCodec.TryParse(dto.SourceEndInclusive, out _) &&
               EventAddressTextCodec.TryParse(dto.AnchorRawEvent, out _) &&
               EventAddressTextCodec.TryParse(dto.GoverningRuntimeConfigSetup, out _) &&
               EventAddressTextCodec.TryParse(dto.GoverningSystemPromptSetup, out _);
    }

    private static bool IsUsableLatestIndex(DerivedRecapLatestIndexDto? dto) {
        if (dto is null) { return false; }
        if (!string.Equals(dto.Schema, LatestIndexSchema, StringComparison.Ordinal)) { return false; }
        if (dto.Items is null) { return false; }

        foreach (var pair in dto.Items) {
            if (string.IsNullOrWhiteSpace(pair.Key)) { return false; }
            var item = pair.Value;
            if (item is null) { return false; }
            if (!IsSafeArtifactId(item.ArtifactId)) { return false; }
            if (!EventAddressTextCodec.TryParse(item.SourceRawHead, out _)) { return false; }
            if (!EventAddressTextCodec.TryParse(item.AnchorRawEvent, out _)) { return false; }
            if (!EventAddressTextCodec.TryParse(item.SourceEndInclusive, out _)) { return false; }
            if (string.IsNullOrWhiteSpace(item.ProducerFingerprint)) { return false; }
        }

        return true;
    }

    private static bool TryGetSnapshotBlockText(
        MemoryPackSnapshotDto memoryPack,
        DerivedRecapTarget target,
        out string? text
    ) {
        text = null;
        IReadOnlyList<MemoryPackBlockDto> blocks = target.Carrier switch {
            MemoryPackCarrierTokens.System => memoryPack.System,
            MemoryPackCarrierTokens.Observation => memoryPack.Observation,
            MemoryPackCarrierTokens.Action => memoryPack.Action,
            _ => []
        };

        foreach (var block in blocks) {
            if (block is null) { return false; }
            if (string.Equals(block.Key, target.BlockKey, StringComparison.Ordinal)) {
                text = block.Text;
                return true;
            }
        }

        return false;
    }

    private static DerivedRecapArtifactDto? SelectLatest(IReadOnlyList<DerivedRecapArtifactDto> artifacts) {
        if (artifacts.Count == 0) { return null; }

        var byId = artifacts.ToDictionary(static artifact => artifact.ArtifactId, StringComparer.Ordinal);
        var predecessorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts) {
            string? cursor = artifact.PreviousArtifact;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrWhiteSpace(cursor) && byId.ContainsKey(cursor) && seen.Add(cursor)) {
                predecessorIds.Add(cursor);
                cursor = byId[cursor].PreviousArtifact;
            }
        }

        var noSuccessor = artifacts.Where(artifact => !predecessorIds.Contains(artifact.ArtifactId)).ToArray();
        if (noSuccessor.Length == 1) { return noSuccessor[0]; }
        if (noSuccessor.Length > 1) {
            DebugUtil.Warning("DerivedRecap", $"Lineage '{artifacts[0].LineageKey}' has ambiguous previousArtifact DAG; falling back to deterministic tie-break.");
            artifacts = noSuccessor;
        }

        return artifacts
            .OrderBy(static artifact => EventAddressTextCodec.GetPhysicalCoordinateSortKey(artifact.SourceEndInclusive), StringComparer.Ordinal)
            .ThenBy(static artifact => artifact.CreatedUtc)
            .ThenBy(static artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .Last();
    }

    private static DerivedRecapArtifact Materialize(DerivedRecapArtifactDto dto)
        => new(
            ArtifactId: dto.ArtifactId,
            ArtifactKind: dto.ArtifactKind,
            CreatedUtc: dto.CreatedUtc,
            LineageKey: new DerivedRecapLineageKey(dto.LineageKey),
            ProfileId: dto.ProfileId,
            Producer: dto.Producer,
            ProducerFingerprint: dto.ProducerFingerprint,
            SourceRawHead: EventAddressTextCodec.Parse(dto.SourceRawHead),
            SourceStartExclusive: EventAddressTextCodec.ParseNullable(dto.SourceStartExclusive),
            SourceEndInclusive: EventAddressTextCodec.Parse(dto.SourceEndInclusive),
            AnchorRawEvent: EventAddressTextCodec.Parse(dto.AnchorRawEvent),
            GoverningRuntimeConfigSetup: EventAddressTextCodec.Parse(dto.GoverningRuntimeConfigSetup),
            GoverningSystemPromptSetup: EventAddressTextCodec.Parse(dto.GoverningSystemPromptSetup),
            PreviousArtifact: dto.PreviousArtifact,
            InputArtifacts: dto.InputArtifacts,
            Target: dto.Target.ToMemoryPackBlockPath(),
            MemoryPack: dto.MemoryPack.ToMemoryPack(),
            Content: dto.Content.Text,
            Invocation: dto.Invocation,
            CallLogPaths: dto.CallLogPaths,
            Status: dto.Status
        );

    private static DerivedRecapLatestIndex Materialize(DerivedRecapLatestIndexDto dto)
        => new(
            RebuiltUtc: dto.RebuiltUtc,
            Items: dto.Items.ToDictionary(
                static pair => new DerivedRecapLineageKey(pair.Key),
                static pair => new DerivedRecapLatestIndexItem(
                    ArtifactId: pair.Value.ArtifactId,
                    SourceRawHead: EventAddressTextCodec.Parse(pair.Value.SourceRawHead),
                    AnchorRawEvent: EventAddressTextCodec.Parse(pair.Value.AnchorRawEvent),
                    SourceEndInclusive: EventAddressTextCodec.Parse(pair.Value.SourceEndInclusive),
                    CreatedUtc: pair.Value.CreatedUtc,
                    ProducerFingerprint: pair.Value.ProducerFingerprint
                )
            )
        );

    private string GetArtifactPath(string artifactId)
        => Path.Combine(ArtifactsDirectory, $"{artifactId}.json");

    private static async Task WriteFileAtomicallyAsync(
        string finalPath,
        string content,
        bool overwrite,
        CancellationToken ct
    ) {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ".");
        string tempPath = finalPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
        try {
            File.Move(tempPath, finalPath, overwrite);
        }
        catch {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch {
            // Best-effort cleanup.
        }
    }

    private static string GetArtifactKindPrefix(string artifactKind)
        => string.Equals(artifactKind, DerivedRecapArtifactKinds.RollingSummary, StringComparison.Ordinal)
            ? "rr"
            : SanitizeArtifactIdPart(artifactKind);

    private static string SanitizeArtifactIdPart(string value) {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value) {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) {
                builder.Append(ch);
            }
            else if (ch is '-' or '_' or '.') {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "artifact" : builder.ToString();
    }

    private static bool IsSafeArtifactId(string? artifactId) {
        if (string.IsNullOrWhiteSpace(artifactId)) { return false; }
        foreach (char ch in artifactId) {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch is '_' or '-' or '.') {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private static string ComputeCanonicalSha256Hex<T>(T value)
        => ComputeSha256Hex(Serialize(value));

    private static string ComputeIdentityHash(DerivedRecapArtifactDto dto)
        => ComputeCanonicalSha256Hex(new DerivedRecapArtifactIdentityDto(
            Schema: dto.Schema,
            ArtifactKind: dto.ArtifactKind,
            LineageKey: dto.LineageKey,
            ProfileId: dto.ProfileId,
            Producer: dto.Producer,
            ProducerFingerprint: dto.ProducerFingerprint,
            SourceRawHead: dto.SourceRawHead,
            SourceStartExclusive: dto.SourceStartExclusive,
            SourceEndInclusive: dto.SourceEndInclusive,
            AnchorRawEvent: dto.AnchorRawEvent,
            GoverningRuntimeConfigSetup: dto.GoverningRuntimeConfigSetup,
            GoverningSystemPromptSetup: dto.GoverningSystemPromptSetup,
            PreviousArtifact: dto.PreviousArtifact,
            InputArtifacts: dto.InputArtifacts,
            Target: dto.Target,
            MemoryPack: dto.MemoryPack,
            Content: dto.Content,
            Invocation: dto.Invocation,
            CallLogPaths: dto.CallLogPaths,
            Status: dto.Status
        ));

    private static string ComputeSha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlyList<string> FreezeStrings(IReadOnlyList<string>? values)
        => values is null || values.Count == 0
            ? Array.AsReadOnly(Array.Empty<string>())
            : Array.AsReadOnly(values.ToArray());
}

public sealed record DerivedRecapWriteRequest(
    string ArtifactKind,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    EventAddress SourceRawHead,
    EventAddress? SourceStartExclusive,
    EventAddress SourceEndInclusive,
    EventAddress AnchorRawEvent,
    EventAddress GoverningRuntimeConfigSetup,
    EventAddress GoverningSystemPromptSetup,
    string? PreviousArtifact,
    MemoryPackBlockPath Target,
    MemoryPack MemoryPack,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? InputArtifacts = null,
    IReadOnlyList<string>? CallLogPaths = null,
    DateTimeOffset? CreatedUtc = null
) {
    public void Validate() {
        ValidateRequired(ArtifactKind, nameof(ArtifactKind));
        ValidateRequired(ProfileId, nameof(ProfileId));
        ValidateRequired(Producer, nameof(Producer));
        ValidateRequired(ProducerFingerprint, nameof(ProducerFingerprint));
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(MemoryPack);
        ValidateAddress(SourceRawHead, nameof(SourceRawHead));
        ValidateAddress(SourceEndInclusive, nameof(SourceEndInclusive));
        ValidateAddress(AnchorRawEvent, nameof(AnchorRawEvent));
        ValidateAddress(GoverningRuntimeConfigSetup, nameof(GoverningRuntimeConfigSetup));
        ValidateAddress(GoverningSystemPromptSetup, nameof(GoverningSystemPromptSetup));
        if (SourceStartExclusive is { } startExclusive) { ValidateAddress(startExclusive, nameof(SourceStartExclusive)); }
        ValidateLineagePart(ArtifactKind, nameof(ArtifactKind));
        ValidateLineagePart(ProfileId, nameof(ProfileId));
        ValidateLineagePart(Target.BlockKey, nameof(Target));
        if (PreviousArtifact is not null && string.IsNullOrWhiteSpace(PreviousArtifact)) {
            throw new ArgumentException("Previous artifact cannot be empty.", nameof(PreviousArtifact));
        }
    }

    private static void ValidateRequired(string value, string paramName) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value cannot be empty.", paramName); }
    }

    private static void ValidateLineagePart(string value, string paramName) {
        if (value.Contains('|', StringComparison.Ordinal)) {
            throw new ArgumentException("Lineage part cannot contain '|'.", paramName);
        }
    }

    private static void ValidateAddress(EventAddress address, string paramName) {
        if (address.Ticket.Packed == 0 || address.SegmentNumber == 0) {
            throw new ArgumentException("EventAddress cannot be default or half-empty.", paramName);
        }
    }
}

public sealed record DerivedRecapArtifact(
    string ArtifactId,
    string ArtifactKind,
    DateTimeOffset CreatedUtc,
    DerivedRecapLineageKey LineageKey,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    EventAddress SourceRawHead,
    EventAddress? SourceStartExclusive,
    EventAddress SourceEndInclusive,
    EventAddress AnchorRawEvent,
    EventAddress GoverningRuntimeConfigSetup,
    EventAddress GoverningSystemPromptSetup,
    string? PreviousArtifact,
    IReadOnlyList<string> InputArtifacts,
    MemoryPackBlockPath Target,
    MemoryPack MemoryPack,
    string Content,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string> CallLogPaths,
    string Status
);

public readonly record struct DerivedRecapLineageKey(string Value) {
    public static DerivedRecapLineageKey Create(
        string artifactKind,
        string profileId,
        MemoryPackBlockPath target
    ) => Create(artifactKind, profileId, DerivedRecapTarget.FromMemoryPackBlockPath(target));

    internal static DerivedRecapLineageKey Create(
        string artifactKind,
        string profileId,
        DerivedRecapTarget target
    ) => new($"{artifactKind}|profile:{profileId}|target:{target.Carrier}/{target.BlockKey}");

    public override string ToString() => Value;
}

public sealed record DerivedRecapLatestIndex(
    DateTimeOffset RebuiltUtc,
    IReadOnlyDictionary<DerivedRecapLineageKey, DerivedRecapLatestIndexItem> Items
);

public sealed record DerivedRecapLatestIndexItem(
    string ArtifactId,
    EventAddress SourceRawHead,
    EventAddress AnchorRawEvent,
    EventAddress SourceEndInclusive,
    DateTimeOffset CreatedUtc,
    string ProducerFingerprint
);

public static class DerivedRecapArtifactKinds {
    public const string RollingSummary = "rolling-summary";
}

public static class DerivedRecapArtifactStatus {
    public const string Produced = "produced";
}

public static class EventAddressTextCodec {
    public const string Prefix = "ej1:";
    public const int HexLength = 32;
    public const int TextLength = 36;

    public static string Format(EventAddress address)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{address.Ticket.Packed:x16}{address.SegmentNumber:x8}{address.Hint.Packed:x8}"
        );

    public static string? FormatNullable(EventAddress? address)
        => address is null ? null : Format(address.Value);

    public static EventAddress Parse(string value)
        => TryParse(value, out var address)
            ? address
            : throw new FormatException($"Invalid EventAddress text '{value}'.");

    public static EventAddress? ParseNullable(string? value)
        => TryParseNullable(value, out var address)
            ? address
            : throw new FormatException($"Invalid nullable EventAddress text '{value}'.");

    public static bool TryParse(string? value, out EventAddress address) {
        address = default;
        if (value is null ||
            value.Length != TextLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        ReadOnlySpan<char> hex = value.AsSpan(Prefix.Length);
        if (!IsLowerHex(hex)) { return false; }
        if (!ulong.TryParse(hex[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong ticketPacked) ||
            !uint.TryParse(hex[16..24], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint segmentNumber) ||
            !uint.TryParse(hex[24..32], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hintPacked)) {
            return false;
        }

        if (ticketPacked == 0 || segmentNumber == 0) { return false; }

        address = new EventAddress(SizedPtr.FromPacked(ticketPacked), segmentNumber, new AddressHint(hintPacked));
        return true;
    }

    public static bool TryParseNullable(string? value, out EventAddress? address) {
        address = null;
        if (value is null) { return true; }
        if (!TryParse(value, out var parsed)) { return false; }
        address = parsed;
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text) {
        foreach (char ch in text) {
            if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')) { continue; }
            return false;
        }

        return true;
    }

    internal static string GetPhysicalCoordinateSortKey(string value) {
        if (!TryParse(value, out var address)) { return string.Empty; }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{address.SegmentNumber:x8}:{address.Ticket.Offset:x16}:{address.Ticket.Length:x8}:{address.Hint.Packed:x8}"
        );
    }
}

internal sealed record DerivedRecapArtifactDto(
    string Schema,
    string ArtifactId,
    string ArtifactKind,
    DateTimeOffset CreatedUtc,
    string LineageKey,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    string SourceRawHead,
    string? SourceStartExclusive,
    string SourceEndInclusive,
    string AnchorRawEvent,
    string GoverningRuntimeConfigSetup,
    string GoverningSystemPromptSetup,
    string? PreviousArtifact,
    IReadOnlyList<string> InputArtifacts,
    DerivedRecapTarget Target,
    MemoryPackSnapshotDto MemoryPack,
    DerivedRecapContentDto Content,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string> CallLogPaths,
    string Status
) {
    public static DerivedRecapArtifactDto FromIdentity(
        string ArtifactId,
        DateTimeOffset CreatedUtc,
        DerivedRecapArtifactIdentityDto identity
    ) => new(
        identity.Schema,
        ArtifactId,
        identity.ArtifactKind,
        CreatedUtc,
        identity.LineageKey,
        identity.ProfileId,
        identity.Producer,
        identity.ProducerFingerprint,
        identity.SourceRawHead,
        identity.SourceStartExclusive,
        identity.SourceEndInclusive,
        identity.AnchorRawEvent,
        identity.GoverningRuntimeConfigSetup,
        identity.GoverningSystemPromptSetup,
        identity.PreviousArtifact,
        identity.InputArtifacts,
        identity.Target,
        identity.MemoryPack,
        identity.Content,
        identity.Invocation,
        identity.CallLogPaths,
        identity.Status
    );
}

internal sealed record DerivedRecapArtifactIdentityDto(
    string Schema,
    string ArtifactKind,
    string LineageKey,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    string SourceRawHead,
    string? SourceStartExclusive,
    string SourceEndInclusive,
    string AnchorRawEvent,
    string GoverningRuntimeConfigSetup,
    string GoverningSystemPromptSetup,
    string? PreviousArtifact,
    IReadOnlyList<string> InputArtifacts,
    DerivedRecapTarget Target,
    MemoryPackSnapshotDto MemoryPack,
    DerivedRecapContentDto Content,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string> CallLogPaths,
    string Status
);

internal sealed record DerivedRecapTarget(
    string Carrier,
    string BlockKey
) {
    public static DerivedRecapTarget FromMemoryPackBlockPath(MemoryPackBlockPath path)
        => new(MemoryPackCarrierTokens.ToStorageToken(path.Carrier), path.BlockKey);

    public MemoryPackBlockPath ToMemoryPackBlockPath() {
        if (!MemoryPackCarrierTokens.TryParseStorageToken(Carrier, out var carrier)) {
            throw new InvalidDataException($"Unknown memory pack carrier token '{Carrier}'.");
        }

        return new MemoryPackBlockPath(carrier, BlockKey);
    }
}

internal sealed record DerivedRecapContentDto(
    string Storage,
    string Text,
    string Sha256
) {
    public static DerivedRecapContentDto Inline(string text)
        => new(DerivedRecapContentStorage.Inline, text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
}

internal static class DerivedRecapContentStorage {
    public const string Inline = "inline";
}

internal sealed record MemoryPackSnapshotDto(
    string Schema,
    IReadOnlyList<MemoryPackBlockDto> System,
    IReadOnlyList<MemoryPackBlockDto> Observation,
    IReadOnlyList<MemoryPackBlockDto> Action
) {
    public static MemoryPackSnapshotDto FromMemoryPack(MemoryPack memoryPack)
        => new(
            DerivedRecapStore.MemoryPackSnapshotSchema,
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

    private static IReadOnlyList<MemoryPackBlockDto> FromCarrier(OrderedDictionary<string, MemoryPackBlock> carrier) {
        var blocks = new MemoryPackBlockDto[carrier.Count];
        int index = 0;
        foreach (var pair in carrier) {
            blocks[index] = new MemoryPackBlockDto(pair.Key, pair.Value.Text);
            index++;
        }

        return Array.AsReadOnly(blocks);
    }

    private static void CopyCarrier(
        IReadOnlyList<MemoryPackBlockDto> source,
        OrderedDictionary<string, MemoryPackBlock> destination
    ) {
        foreach (var block in source) {
            destination.Add(block.Key, new MemoryPackBlock(block.Text));
        }
    }
}

internal sealed record MemoryPackBlockDto(string Key, string Text);

internal sealed record DerivedRecapLatestIndexDto(
    string Schema,
    DateTimeOffset RebuiltUtc,
    IReadOnlyDictionary<string, DerivedRecapLatestIndexItemDto> Items
);

internal sealed record DerivedRecapLatestIndexItemDto(
    string ArtifactId,
    string ArtifactPath,
    string SourceRawHead,
    string AnchorRawEvent,
    string SourceEndInclusive,
    DateTimeOffset CreatedUtc,
    string ProducerFingerprint
);

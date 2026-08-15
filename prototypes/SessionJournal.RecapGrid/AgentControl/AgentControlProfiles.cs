using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.RecapGrid.AgentControl;

/// <summary>
/// One immutable, operator-provisioned Agent Control capability profile.
/// The profile owns the exact admission bytes used by the durable tool runtime
/// identity; a host must bind frozen continuations by that identity, never by
/// a current/default profile fallback.
/// </summary>
public sealed class RecapGridAgentControlProfile {
    private const int MaximumProfileIdUtf8Bytes = 128;
    private const int MaximumCanonicalUtf8Bytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _canonicalBytes;

    private RecapGridAgentControlProfile(
        string profileId,
        RecapGridControlAdmission admission,
        byte[] canonicalBytes
    ) {
        ProfileId = profileId;
        Admission = admission;
        RuntimeIdentity = RecapGridAgentControlFactory.Identity(admission);
        _canonicalBytes = canonicalBytes;
    }

    public string ProfileId { get; }
    public RecapGridControlAdmission Admission { get; }
    public SessionToolRuntimeIdentity RuntimeIdentity { get; }

    public static RecapGridAgentControlProfile Create(
        string profileId,
        RecapGridControlAdmission admission
    ) {
        ArgumentNullException.ThrowIfNull(admission);
        RequireProfileId(profileId);
        string admissionBase64 = Convert.ToBase64String(
            admission.ToCanonicalBytes()
        );
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDto(1, profileId, admissionBase64),
            AgentControlJson.Options
        );
        if (bytes.Length > MaximumCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(admission),
                "Agent Control profile exceeds its canonical byte cap."
            );
        }
        return new RecapGridAgentControlProfile(
            profileId,
            admission,
            bytes
        );
    }

    public static RecapGridAgentControlProfile DecodeCanonical(
        ReadOnlySpan<byte> bytes
    ) {
        if (bytes.Length is < 1 or > MaximumCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "Agent Control profile bytes are empty or exceed the V1 cap."
            );
        }
        if (bytes.Length >= 3
            && bytes[0] == 0xef
            && bytes[1] == 0xbb
            && bytes[2] == 0xbf) {
            throw new InvalidDataException(
                "Agent Control profile must not contain a UTF-8 BOM."
            );
        }
        try {
            _ = StrictUtf8.GetString(bytes);
            ProfileDto dto = JsonSerializer.Deserialize<ProfileDto>(
                bytes,
                AgentControlJson.Options
            ) ?? throw new InvalidDataException(
                "Agent Control profile decoded to null."
            );
            if (dto.V != 1) {
                throw new InvalidDataException(
                    "Agent Control profile schema is unsupported."
                );
            }
            RequireProfileId(dto.ProfileId);
            byte[] admissionBytes = Convert.FromBase64String(
                dto.AdmissionCanonicalBase64
            );
            if (!string.Equals(
                    Convert.ToBase64String(admissionBytes),
                    dto.AdmissionCanonicalBase64,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "Agent Control admission base64 is not canonical."
                );
            }
            RecapGridAgentControlProfile value = Create(
                dto.ProfileId,
                RecapGridControlAdmission.DecodeCanonical(admissionBytes)
            );
            if (!bytes.SequenceEqual(value._canonicalBytes)) {
                throw new InvalidDataException(
                    "Agent Control profile bytes are not canonical."
                );
            }
            return value;
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or ArgumentException
            or DecoderFallbackException) {
            throw new InvalidDataException(
                "Agent Control profile is not a strict V1 value.",
                exception
            );
        }
    }

    public byte[] ToCanonicalBytes() => _canonicalBytes.ToArray();

    private static void RequireProfileId(string value) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Agent Control profile id must be canonical nonblank text.",
                nameof(value)
            );
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > MaximumProfileIdUtf8Bytes
                || value.Any(char.IsControl)) {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Agent Control profile id exceeds its text cap."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Agent Control profile id is not strict UTF-8 text.",
                nameof(value),
                exception
            );
        }
    }

    private sealed record ProfileDto(
        int V,
        string ProfileId,
        string AdmissionCanonicalBase64
    );
}

/// <summary>
/// Immutable exact profile lookup for one candidate host. There is no default
/// or wildcard route: fresh work names a profile id and frozen work supplies
/// the exact durable tool runtime identity.
/// </summary>
public sealed class RecapGridAgentControlProfileRegistry {
    private readonly IReadOnlyDictionary<string,
        RecapGridAgentControlProfile> _byId;
    private readonly IReadOnlyDictionary<SessionToolRuntimeIdentity,
        RecapGridAgentControlProfile> _byRuntime;

    public RecapGridAgentControlProfileRegistry(
        IEnumerable<RecapGridAgentControlProfile> profiles
    ) {
        ArgumentNullException.ThrowIfNull(profiles);
        RecapGridAgentControlProfile[] frozen = profiles
            .Take(257)
            .ToArray();
        if (frozen.Length is < 1 or > 256
            || frozen.Any(static value => value is null)) {
            throw new ArgumentException(
                "Agent Control profile count must be between 1 and 256.",
                nameof(profiles)
            );
        }
        Dictionary<string, RecapGridAgentControlProfile> byId =
            new(StringComparer.Ordinal);
        Dictionary<SessionToolRuntimeIdentity,
            RecapGridAgentControlProfile> byRuntime = [];
        foreach (RecapGridAgentControlProfile profile in frozen) {
            if (!byId.TryAdd(profile.ProfileId, profile)
                || !byRuntime.TryAdd(profile.RuntimeIdentity, profile)) {
                throw new ArgumentException(
                    "Agent Control profile ids and runtime identities must be unique.",
                    nameof(profiles)
                );
            }
        }
        _byId = new ReadOnlyDictionary<string,
            RecapGridAgentControlProfile>(byId);
        _byRuntime = new ReadOnlyDictionary<SessionToolRuntimeIdentity,
            RecapGridAgentControlProfile>(byRuntime);
    }

    public IReadOnlyCollection<string> ProfileIds =>
        Array.AsReadOnly(_byId.Keys.Order(StringComparer.Ordinal).ToArray());

    public bool TryGet(
        string profileId,
        out RecapGridAgentControlProfile profile
    ) => _byId.TryGetValue(profileId, out profile!);

    public bool TryBindExact(
        SessionToolRuntimeIdentity runtimeIdentity,
        out RecapGridAgentControlProfile profile
    ) {
        ArgumentNullException.ThrowIfNull(runtimeIdentity);
        return _byRuntime.TryGetValue(runtimeIdentity, out profile!);
    }
}

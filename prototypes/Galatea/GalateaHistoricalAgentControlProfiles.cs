using System.Threading;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.AgentControl;

namespace Atelia.Galatea.Server;

/// <summary>
/// Host-lifetime, exact-only access to historical Agent Control profiles.
/// Configuration loading has already validated the canonical paths and their
/// metadata. Content remains unread until a frozen recovery asks for an exact
/// profile id or durable runtime identity.
/// </summary>
internal sealed class GalateaHistoricalAgentControlProfiles
    : IRecapGridAgentControlProfileLookup {
    internal const int MaximumProfileUtf8Bytes = 128 * 1024;
    private readonly IReadOnlyList<string> _canonicalPaths;
    private readonly Func<string, byte[]> _reader;
    private readonly Lazy<RecapGridAgentControlProfileRegistry?> _registry;

    internal GalateaHistoricalAgentControlProfiles(
        IReadOnlyList<string> canonicalPaths
    ) : this(canonicalPaths, path =>
        GalateaStrictConfigReader.ReadBoundedRegularFile(
            path, MaximumProfileUtf8Bytes, "historical Agent Control profile")) {
    }

    internal GalateaHistoricalAgentControlProfiles(
        IReadOnlyList<string> canonicalPaths,
        Func<string, byte[]> reader
    ) {
        ArgumentNullException.ThrowIfNull(canonicalPaths);
        ArgumentNullException.ThrowIfNull(reader);
        _canonicalPaths = Array.AsReadOnly(canonicalPaths.ToArray());
        _reader = reader;
        _registry = new Lazy<RecapGridAgentControlProfileRegistry?>(
            LoadAll,
            LazyThreadSafetyMode.ExecutionAndPublication
        );
    }

    public bool TryGet(
        string profileId,
        out RecapGridAgentControlProfile profile
    ) {
        RecapGridAgentControlProfileRegistry? registry = _registry.Value;
        if (registry is null) {
            profile = null!;
            return false;
        }
        return registry.TryGet(profileId, out profile!);
    }

    public bool TryBindExact(
        SessionToolRuntimeIdentity runtimeIdentity,
        out RecapGridAgentControlProfile profile
    ) {
        ArgumentNullException.ThrowIfNull(runtimeIdentity);
        RecapGridAgentControlProfileRegistry? registry = _registry.Value;
        if (registry is null) {
            profile = null!;
            return false;
        }
        return registry.TryBindExact(runtimeIdentity, out profile!);
    }

    private RecapGridAgentControlProfileRegistry? LoadAll() {
        if (_canonicalPaths.Count == 0) {
            return null;
        }
        var profiles = new List<RecapGridAgentControlProfile>(
            _canonicalPaths.Count);
        foreach (string path in _canonicalPaths) {
            byte[] bytes = _reader(path);
            profiles.Add(RecapGridAgentControlProfile.DecodeCanonical(bytes));
        }
        // Construct only after every configured file has decoded, so duplicate
        // ids/runtime identities are never hidden by an exact-identity early
        // return.
        return new RecapGridAgentControlProfileRegistry(profiles);
    }
}

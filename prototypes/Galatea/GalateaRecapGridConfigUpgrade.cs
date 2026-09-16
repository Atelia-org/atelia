using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Hosting;

namespace Atelia.Galatea.Server;

/// <summary>
/// Explicit, provider-free V11 to V12 config conversion. Normal host startup
/// accepts V12 only; this command is the only reader of the retired live route
/// selection fields.
/// </summary>
internal static class GalateaRecapGridConfigUpgrade {
    internal const string CommandName = "upgrade-recap-grid-config-v12";

    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == CommandName;

    internal static int Run(string[] args, TextWriter output, TextWriter error) {
        try {
            Invocation invocation = Parse(args);
            UpgradeResolution resolution = Upgrade(invocation);
            IReadOnlyList<MaintenanceCandidate> candidates = resolution switch {
                UpgradeResolution.Ready readyResolution
                    => readyResolution.Result.Candidates,
                UpgradeResolution.MaintenanceRouteSelectionRequired required
                    => required.Candidates,
                _ => throw new InvalidOperationException("Unknown config upgrade resolution.")
            };
            foreach (MaintenanceCandidate candidate in candidates) {
                output.WriteLine(
                    "candidate=" + candidate.Index
                    + " connectionId=" + candidate.ConnectionId
                    + " maximumConcurrency=" + candidate.MaximumConcurrency
                    + " dispatchTimeoutMilliseconds="
                    + candidate.DispatchTimeoutMilliseconds
                );
            }
            if (resolution is UpgradeResolution.Ready ready) {
                output.WriteLine("outcome=" + ready.Result.Outcome);
                if (ready.Result.BackupPath is not null) {
                    output.WriteLine("backup=" + ready.Result.BackupPath);
                }
                return 0;
            }
            var selectionRequired = (UpgradeResolution.MaintenanceRouteSelectionRequired)resolution;
            error.WriteLine("code=maintenance-route-selection-required");
            error.WriteLine("hint=rerun with --maintenance-route-index <index> (0-"
                + (selectionRequired.Candidates.Count - 1) + ").");
            return 3;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            error.WriteLine("RecapGrid config upgrade failed: "
                + exception.Message);
            return 2;
        }
    }

    internal static UpgradeResolution Upgrade(Invocation invocation) {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!Path.IsPathFullyQualified(invocation.ConfigPath)) {
            throw Usage();
        }
        string configPath = Path.GetFullPath(invocation.ConfigPath);
        byte[] source = GalateaStrictConfigReader.ReadBoundedRegularFile(
            configPath,
            GalateaStrictConfigReader.MaximumConfigUtf8Bytes,
            "Galatea config"
        );
        JsonObject root = ParseV11(source);
        JsonObject recap = RequireObject(
            RequireObject(root, "runtime"), "recapGrid");
        string routeManifestPath = RequireString(recap, "routeManifestPath");
        JsonArray profileFiles = RequireStringArray(
            recap,
            "agentControlProfileFiles",
            minimumCount: 1
        );
        string currentProfileId = RequireString(
            recap, "currentAgentControlProfileId");
        string configDirectory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException(
                "Cannot determine the config directory."
            );
        string routePath = Path.GetFullPath(routeManifestPath, configDirectory);
        RecapGridRouteManifest manifest = GalateaConfigLoader.LoadRouteManifest(
            routePath
        );
        var profiles = new List<RecapGridAgentControlProfile>(
            profileFiles.Count);
        var resolvedProfilePaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal
        );
        foreach (JsonNode? item in profileFiles) {
            string relative = item!.GetValue<string>();
            string profilePath = Path.GetFullPath(relative, configDirectory);
            if (!resolvedProfilePaths.Add(profilePath)) {
                throw new InvalidDataException(
                    "V11 agentControlProfileFiles has duplicate canonical "
                    + "paths."
                );
            }
            profiles.Add(RecapGridAgentControlProfile.DecodeCanonical(
                GalateaStrictConfigReader.ReadBoundedRegularFile(
                    profilePath,
                    128 * 1024,
                    "V11 Agent Control profile"
                )
            ));
        }
        var profileRegistry = new RecapGridAgentControlProfileRegistry(
            profiles);
        if (!profileRegistry.TryGet(currentProfileId, out _)) {
            throw new InvalidDataException(
                "V11 currentAgentControlProfileId does not name a configured "
                + "profile."
            );
        }
        MaintenanceCandidate[] candidates = manifest.Routes
            .Select(static route => new MaintenanceCandidate(
                route.ConnectionId,
                route.MaximumConcurrency,
                checked((long)route.DispatchTimeout.TotalMilliseconds)
            ))
            .Distinct()
            .OrderBy(static value => value.ConnectionId, StringComparer.Ordinal)
            .ThenBy(static value => value.MaximumConcurrency)
            .ThenBy(static value => value.DispatchTimeoutMilliseconds)
            .Select((value, index) => value with { Index = index })
            .ToArray();
        if (candidates.Length == 0) {
            throw new InvalidDataException(
                "The V11 route manifest has no maintenance-route candidate."
            );
        }
        if (invocation.MaintenanceRouteIndex is null && candidates.Length > 1) {
            return new UpgradeResolution.MaintenanceRouteSelectionRequired(
                candidates);
        }
        MaintenanceCandidate selected = invocation.MaintenanceRouteIndex switch {
            null => candidates[0],
            int index when index >= 0 && index < candidates.Length
                => candidates[index],
            _ => throw new InvalidOperationException(
                "--maintenance-route-index does not name a candidate."
            )
        };
        JsonObject converted = root.DeepClone().AsObject();
        converted["v"] = GalateaStrictConfigReader.CurrentConfigVersion;
        JsonObject convertedRecap = RequireObject(
            RequireObject(converted, "runtime"), "recapGrid");
        convertedRecap.Clear();
        convertedRecap["maintenance"] = new JsonObject {
            ["connectionId"] = selected.ConnectionId,
            ["maximumConcurrency"] = selected.MaximumConcurrency,
            ["dispatchTimeoutMilliseconds"] = selected.DispatchTimeoutMilliseconds
        };
        convertedRecap["historicalAgentControlProfileFiles"] =
            profileFiles.DeepClone();
        byte[] destination = Encoding.UTF8.GetBytes(converted.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }
        ) + Environment.NewLine);
        GalateaStrictConfigReader.ValidateRoot(destination);
        if (!invocation.Apply) {
            return new UpgradeResolution.Ready(
                new UpgradeResult("DryRunReady", candidates, null));
        }
        string backupPath = configPath + ".v11-backup-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ",
                System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".json";
        File.Copy(configPath, backupPath, overwrite: false);
        string temporaryPath = configPath + ".v12-writing-"
            + Guid.NewGuid().ToString("N");
        try {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None)) {
                stream.Write(destination);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, configPath, overwrite: true);
        }
        finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
        byte[] reopened = GalateaStrictConfigReader.ReadBoundedRegularFile(
            configPath,
            GalateaStrictConfigReader.MaximumConfigUtf8Bytes,
            "Galatea config"
        );
        GalateaStrictConfigReader.ValidateRoot(reopened);
        return new UpgradeResolution.Ready(
            new UpgradeResult("Upgraded", candidates, backupPath));
    }

    private static Invocation Parse(string[] args) {
        if (!IsInvocation(args)) { throw Usage(); }
        string? configPath = null;
        int? routeIndex = null;
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            switch (args[index]) {
                case "--config" when configPath is null
                    && index + 1 < args.Length:
                    configPath = args[++index];
                    break;
                case "--maintenance-route-index" when routeIndex is null
                    && index + 1 < args.Length
                    && int.TryParse(args[++index], out int parsed):
                    routeIndex = parsed;
                    break;
                case "--apply" when !apply:
                    apply = true;
                    break;
                default:
                    throw Usage();
            }
        }
        if (string.IsNullOrWhiteSpace(configPath)) { throw Usage(); }
        return new Invocation(configPath, routeIndex, apply);
    }

    private static JsonObject ParseV11(ReadOnlySpan<byte> source) {
        try {
            JsonNode? node = JsonNode.Parse(source, documentOptions:
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonObject root = node as JsonObject ?? throw new InvalidDataException(
                "V11 Galatea config must be a JSON object."
            );
            if (root["v"] is not JsonValue version
                || !version.TryGetValue<int>(out int value)
                || value != 11) {
                throw new InvalidDataException(
                    "This operator command accepts exact V11 config only."
                );
            }
            return root;
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "V11 Galatea config is not strict JSON.", exception);
        }
    }

    private static JsonObject RequireObject(JsonObject parent, string name)
        => parent[name] as JsonObject ?? throw new InvalidDataException(
            "V11 config requires object '" + name + "'."
        );

    private static string RequireString(JsonObject parent, string name) {
        if (parent[name] is not JsonValue value
            || !value.TryGetValue<string>(out string? text)
            || string.IsNullOrWhiteSpace(text)) {
            throw new InvalidDataException(
                "V11 config requires non-empty string '" + name + "'."
            );
        }
        return text;
    }

    private static JsonArray RequireStringArray(
        JsonObject parent,
        string name,
        int minimumCount
    ) {
        JsonArray array = parent[name] as JsonArray
            ?? throw new InvalidDataException(
                "V11 config requires array '" + name + "'."
            );
        if (array.Count < minimumCount || array.Count > 256
            || array.Any(static item => item is not JsonValue value
                || !value.TryGetValue<string>(out string? text)
                || string.IsNullOrWhiteSpace(text))) {
            throw new InvalidDataException(
                "V11 config has invalid '" + name + "'."
            );
        }
        return array;
    }

    private static InvalidDataException Usage() => new(
        "Usage: Galatea.Server operator " + CommandName
        + " --config <absolute-path> [--maintenance-route-index <index>] "
        + "[--apply]. The default is dry-run."
    );

    internal sealed record Invocation(
        string ConfigPath,
        int? MaintenanceRouteIndex,
        bool Apply
    );

    internal sealed record MaintenanceCandidate(
        string ConnectionId,
        int MaximumConcurrency,
        long DispatchTimeoutMilliseconds,
        int Index = -1
    );

    internal sealed record UpgradeResult(
        string Outcome,
        IReadOnlyList<MaintenanceCandidate> Candidates,
        string? BackupPath
    );

    internal abstract record UpgradeResolution {
        private UpgradeResolution() { }

        internal sealed record Ready(UpgradeResult Result) : UpgradeResolution;

        internal sealed record MaintenanceRouteSelectionRequired(
            IReadOnlyList<MaintenanceCandidate> Candidates
        ) : UpgradeResolution;
    }
}

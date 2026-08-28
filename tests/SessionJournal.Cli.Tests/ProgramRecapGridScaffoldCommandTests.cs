using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramRecapGridScaffoldCommandTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-scaffold-tests",
        Guid.NewGuid().ToString("N")
    );
    private readonly TrackingFactory _factory = new();

    [Fact]
    public void ScaffoldIsDeterministicCanonicalAndProviderFree() {
        Directory.CreateDirectory(_root);
        ScaffoldPaths first = Paths("first");
        ScaffoldPaths second = Paths("second");

        (int firstCode, JsonElement report) = RunCaptured(
            ScaffoldArguments(first)
        );
        Assert.Equal(0, firstCode);
        Assert.Equal("created", report.GetProperty("status").GetString());
        Assert.Equal(0, _factory.CreateCallCount);

        Assert.Equal(0, Run(ScaffoldArguments(second)));
        Assert.Equal(
            File.ReadAllBytes(first.Admission),
            File.ReadAllBytes(second.Admission)
        );
        Assert.Equal(
            File.ReadAllBytes(first.Profile),
            File.ReadAllBytes(second.Profile)
        );
        Assert.Equal(
            File.ReadAllBytes(first.Route),
            File.ReadAllBytes(second.Route)
        );

        RecapGridControlAdmission admission =
            RecapGridControlAdmission.DecodeCanonical(
                File.ReadAllBytes(first.Admission)
            );
        RecapGridAgentControlProfile profile =
            RecapGridAgentControlProfile.DecodeCanonical(
                File.ReadAllBytes(first.Profile)
            );
        RecapGridRouteManifest route =
            RecapGridRouteManifest.DecodeCanonical(
                File.ReadAllBytes(first.Route)
            );
        Assert.Equal("operator-profile", profile.ProfileId);
        Assert.Equal(
            admission.ToCanonicalBytes(),
            profile.Admission.ToCanonicalBytes()
        );
        RecapGridRouteManifestEntry entry = Assert.Single(route.Routes);
        Assert.Null(entry.Key.SemanticModelId);
        Assert.Equal("agent-connection", entry.ConnectionId);
        Assert.Equal(2, entry.MaximumConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(30), entry.DispatchTimeout);
        Assert.Equal(2048, entry.MaximumOutputTokens);

        JsonElement detail = report.GetProperty("detail");
        Assert.Equal(64, detail.GetProperty("admission")
            .GetProperty("sha256").GetString()!.Length);
        Assert.Equal(64, detail.GetProperty("profile")
            .GetProperty("sha256").GetString()!.Length);
        Assert.Equal(64, detail.GetProperty("route")
            .GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public void GalateaOperatorAssetScaffoldUsesExactCharacterNameAndIsProviderFree() {
        Directory.CreateDirectory(_root);
        ScaffoldPaths paths = Paths("galatea");
        const string CharacterName = "阿特丽娅";
        string[] arguments = ScaffoldArguments(paths)
            .ReplaceOption(
                "asset",
                GalateaRecapGridAssets.RollingRewriteZhCnV6
            )
            .ReplaceOption("logical-column-prefix", "world-understanding")
            .AppendOptions(
                "--logical-column-prefix", "autobiography",
                "--character-name", CharacterName,
                "--player-name", "刘世超"
            );

        (int exitCode, JsonElement report) = RunCaptured(arguments);

        Assert.Equal(0, exitCode);
        Assert.Equal("created", report.GetProperty("status").GetString());
        Assert.Equal(0, _factory.CreateCallCount);
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
            new GalateaRecapGridAssetParameters(
                new GalateaCharacterName(CharacterName),
                new GalateaPlayerName("刘世超")
            ),
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);
        RecapGridRouteManifestEntry route = Assert.Single(
            RecapGridRouteManifest.DecodeCanonical(
                File.ReadAllBytes(paths.Route)
            ).Routes
        );
        Assert.Equal(bundle.Families[0].Digest, route.Key.FamilyDigest);
        Assert.Equal(RecapRewriterProtocolV3.RuntimeProtocolId,
            route.Key.RuntimeProtocolId);
        Assert.Null(route.Key.SemanticModelId);
        Assert.Equal("agent-connection", route.ConnectionId);
        Assert.Equal(
            ["world-understanding", "autobiography"],
            bundle.Definitions.Select(static value =>
                value.LogicalColumnId.Value)
        );
        Assert.Equal(
            [
                "galatea.world-understanding",
                "galatea.first-person-autobiography"
            ],
            bundle.Definitions.Select(static value => value.Target.BlockKey)
        );
        JsonElement detail = report.GetProperty("detail");
        Assert.Equal(
            bundle.CanonicalCommandDigest,
            detail.GetProperty("registrationCommandDigest").GetString()
        );
        Assert.Equal(
            bundle.Families.Select(static value => value.Digest.Value),
            detail.GetProperty("families").EnumerateArray()
                .Select(static value => value.GetProperty("digest").GetString())
        );
        JsonElement[] definitions = [.. detail.GetProperty("definitions")
            .EnumerateArray()];
        Assert.Equal([0, 1], definitions.Select(static value =>
            value.GetProperty("ordinal").GetInt32()));
        Assert.Equal(
            bundle.Definitions.Select(static value => value.Digest.Value),
            definitions.Select(static value =>
                value.GetProperty("digest").GetString())
        );
        Assert.Equal(
            ["world-understanding", "autobiography"],
            definitions.Select(static value =>
                value.GetProperty("logicalColumnId").GetString())
        );
        Assert.Equal(
            [ContextHeaderCarrierTokens.Observation,
                ContextHeaderCarrierTokens.Action],
            definitions.Select(static value =>
                value.GetProperty("targetCarrier").GetString())
        );
        Assert.Equal(
            [
                "galatea.world-understanding",
                "galatea.first-person-autobiography"
            ],
            definitions.Select(static value =>
                value.GetProperty("targetBlockKey").GetString())
        );
        Assert.Equal(
            [
                "galatea.world-understanding 阿特丽娅积累的世界理解：",
                "galatea.first-person-autobiography 阿特丽娅积累的第一人称自传："
            ],
            definitions.Select(static value =>
                value.GetProperty("semanticHeading").GetString())
        );
        Assert.Equal(0, _factory.CreateCallCount);
    }

    [Fact]
    public void ExistingOrInvalidInputRejectsBeforeAnyOutputOrProvider() {
        Directory.CreateDirectory(_root);
        ScaffoldPaths paths = Paths("existing");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Profile)!);
        File.WriteAllBytes(paths.Profile, [4, 2]);
        Assert.Equal(1, Run(ScaffoldArguments(paths)));
        Assert.False(File.Exists(paths.Admission));
        Assert.False(File.Exists(paths.Route));
        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(paths.Profile));

        string[] duplicateOutputs = ScaffoldArguments(Paths("same-output"));
        duplicateOutputs = duplicateOutputs.ReplaceOption(
            "profile-output",
            OptionValue(duplicateOutputs, "admission-output")
        );
        foreach (string[] invalid in new[] {
                     ScaffoldArguments(Paths("unknown"))
                         .ReplaceOption("asset", "unknown-asset"),
                     ScaffoldArguments(Paths("galatea-missing-name"))
                         .ReplaceOption(
                             "asset",
                             GalateaRecapGridAssets.RollingRewriteZhCnV6
                         ),
                     ScaffoldArguments(Paths("galatea-invalid-name"))
                         .ReplaceOption(
                             "asset",
                             GalateaRecapGridAssets.RollingRewriteZhCnV6
                         )
                         .AppendOptions(
                             "--character-name", "[invalid]",
                             "--player-name", "刘世超"
                         ),
                     ScaffoldArguments(Paths("galatea-missing-player"))
                         .ReplaceOption(
                             "asset",
                             GalateaRecapGridAssets.RollingRewriteZhCnV6
                         )
                         .AppendOptions("--character-name", "Galatea"),
                     ScaffoldArguments(Paths("galatea-invalid-player"))
                         .ReplaceOption(
                             "asset",
                             GalateaRecapGridAssets.RollingRewriteZhCnV6
                         )
                         .AppendOptions(
                             "--character-name", "Galatea",
                             "--player-name", "[invalid]"
                         ),
                     ScaffoldArguments(Paths("non-parameterized-name"))
                         .AppendOptions("--character-name", "Galatea"),
                     ScaffoldArguments(Paths("non-parameterized-player"))
                         .AppendOptions("--player-name", "Player"),
                     [.. ScaffoldArguments(Paths("duplicate")),
                         "--permission", "create"],
                     ScaffoldArguments(Paths("malformed"))
                         .ReplaceOption("max-concurrency", "0"),
                     ScaffoldArguments(Paths("semantic"))
                         .AppendOptions("--semantic-model-id", "model-a"),
                     duplicateOutputs
                 }) {
            Assert.NotEqual(0, Run(invalid));
            Assert.False(File.Exists(OptionValue(invalid, "admission-output")));
            Assert.False(File.Exists(OptionValue(invalid, "profile-output")));
            Assert.False(File.Exists(OptionValue(invalid, "route-output")));
        }
        Assert.Equal(0, _factory.CreateCallCount);
    }

    [Fact]
    public void ScaffoldAdmissionCanInitializeFreshRepository() {
        Directory.CreateDirectory(_root);
        ScaffoldPaths paths = Paths("init");
        Assert.Equal(0, Run(ScaffoldArguments(paths)));
        string repository = Path.Combine(_root, "repository");
        string refId;
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
                   repository,
                   new SessionCreateOptions("model", "surface", "system"))) {
            refId = engine.BranchRefId.ToHexString();
        }

        Assert.Equal(0, Run([
            "init",
            "--input", repository,
            "--confirm-ref", refId,
            "--admission", paths.Admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--minimum-recent-history-load", "1",
            "--target-history-load", "1",
            "--max-raw-events", "64",
            "--max-rendered-bytes", "65536"
        ]));
        Assert.Equal(0, _factory.CreateCallCount);
    }

    [Fact]
    public void NestedOutputPathsRejectBeforeWritingAnyFile() {
        Directory.CreateDirectory(_root);
        string parentAsFile = Path.Combine(_root, "nested", "admission.json");
        var paths = new ScaffoldPaths(
            parentAsFile,
            Path.Combine(parentAsFile, "profile.json"),
            Path.Combine(_root, "nested", "routes.json")
        );

        Assert.Equal(1, Run(ScaffoldArguments(paths)));
        Assert.False(File.Exists(paths.Admission));
        Assert.False(File.Exists(paths.Profile));
        Assert.False(File.Exists(paths.Route));
        Assert.False(Directory.Exists(parentAsFile));
        Assert.Equal(0, _factory.CreateCallCount);
    }

    private string[] ScaffoldArguments(ScaffoldPaths paths) => [
        "scaffold",
        "--asset", RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
        "--profile-id", "operator-profile",
        "--connection-id", "agent-connection",
        "--permission", "create",
        "--permission", "register-family",
        "--permission", "register-definition",
        "--permission", "register-recipe",
        "--permission", "activate",
        "--permission", "promote",
        "--logical-column-prefix", "case.",
        "--max-bootstrap-rows", "64",
        "--max-projected-calls", "1024",
        "--max-concurrency", "2",
        "--dispatch-timeout-ms", "30000",
        "--max-output-tokens", "2048",
        "--admission-output", paths.Admission,
        "--profile-output", paths.Profile,
        "--route-output", paths.Route
    ];

    private ScaffoldPaths Paths(string name) {
        string directory = Path.Combine(_root, name);
        return new ScaffoldPaths(
            Path.Combine(directory, "admission.json"),
            Path.Combine(directory, "profile.json"),
            Path.Combine(directory, "routes.json")
        );
    }

    private int Run(string[] arguments) => Program.MainCore(
        ["recap-grid", .. arguments],
        _factory
    );

    private (int ExitCode, JsonElement Report) RunCaptured(
        string[] arguments
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int code = Run(arguments);
            using JsonDocument document = JsonDocument.Parse(
                output.ToString().Split(
                    Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries
                )[^1]
            );
            return (code, document.RootElement.Clone());
        }
        finally {
            Console.SetOut(original);
        }
    }

    private static string OptionValue(string[] arguments, string key) {
        int index = Array.IndexOf(arguments, "--" + key);
        Assert.InRange(index, 0, arguments.Length - 2);
        return arguments[index + 1];
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TrackingFactory : ICompletionClientFactory {
        private int _createCallCount;
        internal int CreateCallCount => Volatile.Read(ref _createCallCount);

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            _ = connection;
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "Scaffold and init must not create a Completion client."
            );
        }
    }

    private sealed record ScaffoldPaths(
        string Admission,
        string Profile,
        string Route
    );
}

internal static class ScaffoldArgumentTestExtensions {
    internal static string[] ReplaceOption(
        this string[] arguments,
        string key,
        string value
    ) {
        string[] copy = (string[])arguments.Clone();
        int index = Array.IndexOf(copy, "--" + key);
        if (index < 0 || index + 1 >= copy.Length) {
            throw new InvalidOperationException("Test option was absent.");
        }
        copy[index + 1] = value;
        return copy;
    }

    internal static string[] AppendOptions(
        this string[] arguments,
        params string[] appended
    ) => [.. arguments, .. appended];
}

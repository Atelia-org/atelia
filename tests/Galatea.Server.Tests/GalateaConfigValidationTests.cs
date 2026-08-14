using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConfigValidationTests {
    [Fact]
    public async Task StrictRecapGridConfigDefersRouteReadAndClientCreation() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "missing-session"))]
            );
            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);
            var factory = new TrackingFactory();
            await using var service = new GalateaHostService(
                loaded,
                factory,
                DisabledGalateaUserMessageNormalizer.Instance
            );

            Assert.Equal(0, factory.CreateCallCount);
            Assert.Equal("test", service.DefaultConnectionId);
            Assert.Single(service.Connections);
            Assert.False(File.Exists(Path.Combine(root, "routes.json")));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScaffoldArtifactsLoadThroughStrictGalateaConfig() {
        string root = NewRoot();
        try {
            string admission = Path.Combine(root, "admission.json");
            string profile = Path.Combine(root, "profile.json");
            string routes = Path.Combine(root, "routes.json");
            var provider = new TrackingFactory();
            int scaffold = Atelia.SessionJournal.Cli.Program.MainCore(
                [
                    "recap-grid", "scaffold",
                    "--asset",
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV2,
                    "--profile-id", "test-profile",
                    "--connection-id", "test",
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
                    "--admission-output", admission,
                    "--profile-output", profile,
                    "--route-output", routes
                ],
                provider
            );
            Assert.Equal(0, scaffold);
            Assert.Equal(0, provider.CreateCallCount);

            string configPath = Path.Combine(root, "config.json");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(
                    new GalateaUsersFileConfig(
                        [User("alice", Path.Combine(root, "session"))],
                        RecapGrid: new GalateaRecapGridFileConfig(
                            "routes.json",
                            ["profile.json"],
                            "test-profile"
                        )
                    ),
                    GalateaJson.Options
                )
            );
            File.WriteAllText(
                Path.Combine(root, GalateaConfigLoader.ConnectionsFileName),
                JsonSerializer.Serialize(
                    new CompletionConnectionsFileConfig(
                        Connections,
                        "test"
                    ),
                    GalateaJson.Options
                )
            );

            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);
            Assert.NotNull(loaded.RecapGrid);
            Assert.True(loaded.RecapGrid.AgentControlProfiles.TryGet(
                "test-profile",
                out RecapGridAgentControlProfile _
            ));
            Assert.Single(GalateaConfigLoader.LoadRouteManifest(
                loaded.RecapGrid.RouteManifestPath
            ).Routes);
            Assert.Equal(0, provider.CreateCallCount);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StrictConfigRejectsUnknownAndCaseInsensitiveDuplicates() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string originalConfig = File.ReadAllText(configPath);
            string connectionsPath = Path.Combine(
                root,
                GalateaConfigLoader.ConnectionsFileName
            );
            string originalConnections = File.ReadAllText(connectionsPath);

            string[] invalidConfigs = [
                originalConfig.Replace(
                    "{\"users\"",
                    "{\"unknown\":1,\"users\"",
                    StringComparison.Ordinal
                ),
                originalConfig.Replace(
                    "\"users\":",
                    "\"users\":[],\"Users\":",
                    StringComparison.Ordinal
                ),
                originalConfig.Replace(
                    "\"userId\":",
                    "\"nestedUnknown\":1,\"userId\":",
                    StringComparison.Ordinal
                ),
                originalConfig.Replace(
                    "\"routeManifestPath\":",
                    "\"RouteManifestPath\":\"other\",\"routeManifestPath\":",
                    StringComparison.Ordinal
                )
            ];
            foreach (string invalid in invalidConfigs) {
                File.WriteAllText(configPath, invalid);
                Assert.Throws<InvalidDataException>(
                    () => GalateaConfigLoader.Load(configPath)
                );
            }
            File.WriteAllText(configPath, originalConfig);

            string[] invalidConnections = [
                originalConnections.Replace(
                    "{\"connections\"",
                    "{\"unknown\":1,\"connections\"",
                    StringComparison.Ordinal
                ),
                originalConnections.Replace(
                    "\"connections\":",
                    "\"connections\":[],\"Connections\":",
                    StringComparison.Ordinal
                ),
                originalConnections.Replace(
                    "\"modelId\":",
                    "\"nestedUnknown\":1,\"modelId\":",
                    StringComparison.Ordinal
                ),
                originalConnections.Replace(
                    "\"id\":",
                    "\"Id\":\"other\",\"id\":",
                    StringComparison.Ordinal
                )
            ];
            foreach (string invalid in invalidConnections) {
                File.WriteAllText(connectionsPath, invalid);
                Assert.Throws<InvalidDataException>(
                    () => GalateaConfigLoader.Load(configPath)
                );
            }
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StrictConfigCapsAreExactAndPathsAreNoFollow() {
        if (!OperatingSystem.IsLinux()) { return; }
        string root = NewRoot();
        string external = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            PadToExactBytes(
                configPath,
                GalateaStrictConfigReader.MaximumConfigUtf8Bytes
            );
            Assert.NotNull(GalateaConfigLoader.Load(configPath));
            File.AppendAllText(configPath, " ");
            Assert.Throws<InvalidDataException>(
                () => GalateaConfigLoader.Load(configPath)
            );

            configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string profile = Path.Combine(root, "profile.json");
            string externalProfile = Path.Combine(external, "profile.json");
            File.Move(profile, externalProfile);
            File.CreateSymbolicLink(profile, externalProfile);
            Assert.Throws<InvalidOperationException>(
                () => GalateaConfigLoader.Load(configPath)
            );

            File.Delete(profile);
            File.Move(externalProfile, profile);
            string route = Path.Combine(root, "routes.json");
            string externalRoute = Path.Combine(external, "routes.json");
            File.WriteAllText(externalRoute, "{}");
            File.CreateSymbolicLink(route, externalRoute);
            Assert.Throws<InvalidOperationException>(
                () => GalateaConfigLoader.Load(configPath)
            );

            File.Delete(route);
            string prompt = Path.Combine(root, "prompt.txt");
            File.WriteAllText(prompt, "prompt");
            configPath = WriteConfig(
                root,
                [new GalateaUserConfig(
                    "alice",
                    "pw",
                    Path.Combine(root, "session"),
                    SystemPrompt: "",
                    SystemPromptFile: "prompt.txt"
                )]
            );
            PadToExactBytes(
                prompt,
                GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
            );
            Assert.NotNull(GalateaConfigLoader.Load(configPath));
            File.AppendAllText(prompt, "x");
            Assert.Throws<InvalidDataException>(
                () => GalateaConfigLoader.Load(configPath)
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void StrictFileAndBootstrapPathsRejectAncestorSymlink() {
        if (!OperatingSystem.IsLinux()) { return; }
        string root = NewRoot();
        string external = NewRoot();
        try {
            string externalFile = Path.Combine(external, "value.json");
            File.WriteAllText(externalFile, "{}");
            string link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, external);

            foreach (string kind in new[] {
                         "config",
                         "connections",
                         "Agent Control profile",
                         "RecapGrid route manifest",
                         "systemPromptFile"
                     }) {
                Assert.Throws<InvalidDataException>(() =>
                    GalateaStrictConfigReader.ReadBoundedRegularFile(
                        Path.Combine(link, "value.json"),
                        1024,
                        kind
                    ));
            }

            Assert.Throws<InvalidDataException>(() =>
                GalateaConfigBootstrapper.EnsureExistsOrBootstrap(
                    Path.Combine(link, "new", "config.json")
                ));
            Assert.False(Directory.Exists(Path.Combine(external, "new")));
        }
        finally {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void ExactDuplicateSessionDirectoryIsRejectedByLoaderAndHost() {
        string root = NewRoot();
        string session = Path.Combine(root, "missing-session");
        try {
            AssertRejectedByLoaderAndHost(
                root,
                [User("alice", session), User("bob", session)],
                session
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DotAndDotDotAliasesAreRejectedByLoaderAndHost() {
        string root = NewRoot();
        string canonical = Path.Combine(root, "sessions", "alice");
        string alias = Path.Combine(
            root,
            "sessions",
            ".",
            "temporary",
            "..",
            "alice",
            "."
        );
        try {
            AssertRejectedByLoaderAndHost(
                root,
                [User("alice", canonical), User("bob", alias)],
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(canonical)
                )
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionDirectoryCaseComparisonMatchesPlatform() {
        string root = NewRoot();
        string upper = Path.Combine(root, "sessions", "Alice");
        string lower = Path.Combine(root, "sessions", "alice");
        GalateaUserConfig[] users = [
            User("alice", upper),
            User("bob", lower)
        ];
        try {
            if (OperatingSystem.IsWindows()) {
                AssertRejectedByLoaderAndHost(
                    root,
                    users,
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(upper)
                    )
                );
                return;
            }

            string configPath = WriteConfig(root, users);
            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);
            var factory = new TrackingFactory();
            await using var service = new GalateaHostService(
                loaded,
                factory,
                DisabledGalateaUserMessageNormalizer.Instance
            );

            Assert.Equal(0, factory.CreateCallCount);
            Assert.False(Directory.Exists(upper));
            Assert.False(Directory.Exists(lower));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertRejectedByLoaderAndHost(
        string root,
        IReadOnlyList<GalateaUserConfig> users,
        string expectedNormalizedPath
    ) {
        string configPath = WriteConfig(root, users);
        InvalidOperationException loadFailure = Assert.Throws<
            InvalidOperationException
        >(() => GalateaConfigLoader.Load(configPath));
        AssertDuplicateDetail(loadFailure, expectedNormalizedPath);

        var factory = new TrackingFactory();
        var config = new GalateaConfig(
            users,
            Connections,
            "test"
        );
        InvalidOperationException constructionFailure = Assert.Throws<
            InvalidOperationException
        >(() => new GalateaHostService(
            config,
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        ));
        AssertDuplicateDetail(
            constructionFailure,
            expectedNormalizedPath
        );

        Assert.Equal(0, factory.CreateCallCount);
        Assert.All(
            users,
            static user => Assert.False(
                Directory.Exists(Path.GetFullPath(user.SessionDir))
            )
        );
    }

    private static void AssertDuplicateDetail(
        InvalidOperationException exception,
        string expectedNormalizedPath
    ) {
        Assert.Contains("'alice'", exception.Message);
        Assert.Contains("'bob'", exception.Message);
        Assert.Contains(expectedNormalizedPath, exception.Message);
        Assert.Contains("same lexical session path", exception.Message);
    }

    private static string WriteConfig(
        string root,
        IReadOnlyList<GalateaUserConfig> users
    ) {
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new GalateaUsersFileConfig(
                    users,
                    RecapGrid: new GalateaRecapGridFileConfig(
                        "routes.json",
                        ["profile.json"],
                        "test-profile"
                    )
                ),
                GalateaJson.Options
            )
        );
        File.WriteAllBytes(
            Path.Combine(root, "profile.json"),
            CreateProfile().ToCanonicalBytes()
        );
        File.WriteAllText(
            Path.Combine(root, GalateaConfigLoader.ConnectionsFileName),
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    Connections,
                    "test"
                ),
                GalateaJson.Options
            )
        );
        return configPath;
    }

    private static RecapGridAgentControlProfile CreateProfile() {
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV2,
                out RecapGridControlRegistrationBundle? builtIn
            ));
        return RecapGridAgentControlProfile.Create(
            "test-profile",
            new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                [builtIn!.Families[0].Digest],
                builtIn.Definitions.Select(static value =>
                    value.Capability.CapabilityFingerprint),
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: 64,
                maximumProjectedCalls: 1_024
            )
        );
    }

    private static CompletionConnectionRegistry CreateRegistry(
        TrackingFactory factory
    ) => new(
        new CompletionConnectionsFileConfig(Connections, "test"),
        factory
    );

    private static GalateaUserConfig User(
        string userId,
        string sessionDirectory
    ) => new(
        userId,
        "pw",
        sessionDirectory,
        SystemPrompt: "prompt"
    );

    private static string NewRoot() {
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-config-validation-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        return root;
    }

    private static void PadToExactBytes(string path, int exactBytes) {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length < exactBytes);
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None
        );
        byte[] padding = new byte[exactBytes - bytes.Length];
        Array.Fill(padding, (byte)' ');
        stream.Write(padding);
    }

    private static readonly CompletionConnectionConfig[] Connections = [
        new(
            "test",
            "openai-chat",
            "model-a",
            "openai-chat/strict",
            "http://localhost:8000/",
            ApiKey: "test-key"
        )
    ];

    private sealed class TrackingFactory : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            _ = connection;
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "Config validation must not create a Completion client."
            );
        }
    }
}

using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecapMaintainerConnectionConfigTests {
    [Fact]
    public async Task MissingMapping_PreservesLegacyAgentConnectionRouting() {
        string root = NewRoot();
        try {
            string configPath = WriteConfiguration(
                root,
                recapMaintainerConnections: null
            );

            GalateaConfig config = GalateaConfigLoader.Load(configPath);

            Assert.Null(config.RecapMaintainerConnections);
            var factory = new RejectingFactory();
            using var registry = new CompletionConnectionRegistry(
                new CompletionConnectionsFileConfig(
                    config.Connections,
                    config.DefaultConnectionId
                ),
                factory
            );
            await using var service = new GalateaHostService(
                config,
                registry,
                DisabledGalateaUserMessageNormalizer.Instance
            );
            Assert.Equal(0, factory.CreateCallCount);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteMapping_LoadsAsOrdinalReadOnlyMapWithoutCreatingClients() {
        string root = NewRoot();
        try {
            string configPath = WriteConfiguration(
                root,
                CompleteBindings()
            );

            GalateaConfig config = GalateaConfigLoader.Load(configPath);

            IReadOnlyDictionary<string, string> routes = Assert.IsAssignableFrom<
                IReadOnlyDictionary<string, string>
            >(config.RecapMaintainerConnections);
            Assert.Equal(
                "world",
                routes[WorldUnderstandingRewriteProfiles.MaintainerId]
            );
            Assert.Equal(
                "autobiography",
                routes[AutobiographicalRewriteProfiles.MaintainerId]
            );
            Assert.False(routes.ContainsKey(
                WorldUnderstandingRewriteProfiles.MaintainerId
                    .ToUpperInvariant()
            ));
            Assert.Throws<NotSupportedException>(() =>
                ((IDictionary<string, string>)routes).Add("x", "agent")
            );

            var factory = new RejectingFactory();
            using var registry = new CompletionConnectionRegistry(
                new CompletionConnectionsFileConfig(
                    config.Connections,
                    config.DefaultConnectionId
                ),
                factory
            );
            await using var service = new GalateaHostService(
                config,
                registry,
                DisabledGalateaUserMessageNormalizer.Instance
            );
            Assert.Equal(0, factory.CreateCallCount);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    public static TheoryData<
        IReadOnlyList<GalateaRecapMaintainerConnectionBinding>,
        string
    > InvalidMappings => new() {
        { [], "missing:" },
        {
            [new(
                WorldUnderstandingRewriteProfiles.MaintainerId,
                "world"
            )],
            AutobiographicalRewriteProfiles.MaintainerId
        },
        {
            [
                .. CompleteBindings(),
                new("unknown.rewrite", "world")
            ],
            "unknown maintainerId 'unknown.rewrite'"
        },
        {
            [
                .. CompleteBindings(),
                new(
                    WorldUnderstandingRewriteProfiles.MaintainerId,
                    "world"
                )
            ],
            "duplicate maintainerId"
        },
        {
            [
                new(" ", "world"),
                new(
                    AutobiographicalRewriteProfiles.MaintainerId,
                    "autobiography"
                )
            ],
            "non-empty maintainerId"
        },
        {
            [
                new(
                    WorldUnderstandingRewriteProfiles.MaintainerId,
                    " "
                ),
                new(
                    AutobiographicalRewriteProfiles.MaintainerId,
                    "autobiography"
                )
            ],
            "non-empty connectionId"
        },
        {
            [
                new(
                    WorldUnderstandingRewriteProfiles.MaintainerId,
                    "missing-connection"
                ),
                new(
                    AutobiographicalRewriteProfiles.MaintainerId,
                    "autobiography"
                )
            ],
            "unknown connectionId 'missing-connection'"
        }
    };

    [Theory]
    [MemberData(nameof(InvalidMappings))]
    public void PresentMapping_MustBeExactAndComplete(
        IReadOnlyList<GalateaRecapMaintainerConnectionBinding> bindings,
        string expectedDetail
    ) {
        string root = NewRoot();
        try {
            string configPath = WriteConfiguration(root, bindings);

            InvalidOperationException failure = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigLoader.Load(configPath));

            Assert.Contains(expectedDetail, failure.Message);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitNullMapping_IsNotLegacyAndIsRejected() {
        string root = NewRoot();
        try {
            string configPath = WriteConfiguration(
                root,
                recapMaintainerConnections: null
            );
            string connectionsPath = Path.Combine(
                root,
                GalateaConfigLoader.ConnectionsFileName
            );
            using JsonDocument existing = JsonDocument.Parse(
                File.ReadAllText(connectionsPath)
            );
            File.WriteAllText(
                connectionsPath,
                $$"""
                {
                  "defaultConnectionId": "agent",
                  "connections": {{existing.RootElement.GetProperty("connections").GetRawText()}},
                  "recapMaintainerConnections": null
                }
                """
            );

            InvalidOperationException failure = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigLoader.Load(configPath));

            Assert.Contains("must be an array", failure.Message);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteConfiguration(
        string root,
        IReadOnlyList<GalateaRecapMaintainerConnectionBinding>?
            recapMaintainerConnections
    ) {
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new GalateaUsersFileConfig([
                    new GalateaUserConfig(
                        "alice",
                        "pw",
                        Path.Combine(root, "session"),
                        SystemPrompt: "prompt"
                    )
                ]),
                GalateaJson.Options
            )
        );
        File.WriteAllText(
            Path.Combine(root, GalateaConfigLoader.ConnectionsFileName),
            JsonSerializer.Serialize(
                new GalateaConnectionsFileConfig(
                    Connections,
                    "agent",
                    recapMaintainerConnections
                ),
                GalateaJson.Options
            )
        );
        return configPath;
    }

    private static GalateaRecapMaintainerConnectionBinding[]
        CompleteBindings() => [
            new(
                WorldUnderstandingRewriteProfiles.MaintainerId,
                "world"
            ),
            new(
                AutobiographicalRewriteProfiles.MaintainerId,
                "autobiography"
            )
        ];

    private static string NewRoot() {
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-recap-route-config-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        return root;
    }

    private static readonly CompletionConnectionConfig[] Connections = [
        Connection("agent", "agent-model"),
        Connection("world", "world-model"),
        Connection("autobiography", "autobiography-model")
    ];

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId
    ) => new(
        id,
        "openai-chat",
        modelId,
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private sealed class RejectingFactory : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                $"Config validation must not create '{connection.Id}'."
            );
        }
    }
}

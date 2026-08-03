using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConfigValidationTests {
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
            using CompletionConnectionRegistry connections =
                CreateRegistry(factory);
            await using var service = new GalateaHostService(
                loaded,
                connections,
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
        using CompletionConnectionRegistry connections =
            CreateRegistry(factory);
        var config = new GalateaConfig(
            users,
            Connections,
            "test"
        );
        InvalidOperationException constructionFailure = Assert.Throws<
            InvalidOperationException
        >(() => new GalateaHostService(
            config,
            connections,
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
                new GalateaUsersFileConfig(users),
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
        return configPath;
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

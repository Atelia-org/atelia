using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConfigValidationTests {
    [Fact]
    public void FileConfigDtosAreInternalWhileRuntimeAndHttpDtosRemainPublic() {
        Type assemblyMarker = typeof(GalateaConfig);
        Type[] exported = assemblyMarker.Assembly.GetExportedTypes();

        Assert.False(typeof(GalateaUsersFileConfig).IsPublic);
        Assert.False(typeof(GalateaUserFileConfig).IsPublic);
        Assert.False(typeof(GalateaRecapGridFileConfig).IsPublic);
        Assert.DoesNotContain(typeof(GalateaUsersFileConfig), exported);
        Assert.DoesNotContain(typeof(GalateaUserFileConfig), exported);
        Assert.DoesNotContain(typeof(GalateaRecapGridFileConfig), exported);

        Assert.Contains(typeof(GalateaConfig), exported);
        Assert.Contains(typeof(GalateaUserConfig), exported);
        Assert.Contains(typeof(GalateaMeDto), exported);
        Assert.Contains(typeof(RecentTurnsResponseDto), exported);
    }

    [Fact]
    public void RootConfigTemplateStartsWithExactV5AndRoundTrips() {
        byte[] template = JsonSerializer.SerializeToUtf8Bytes(
            GalateaConfigTemplateFactory.CreateUsersFile(),
            GalateaJson.Options
        );

        GalateaStrictConfigReader.ValidateUsers(template);
        using JsonDocument document = JsonDocument.Parse(template);
        JsonProperty first = document.RootElement
            .EnumerateObject()
            .First();
        Assert.Equal("v", first.Name);
        Assert.Equal("5", first.Value.GetRawText());

        GalateaUsersFileConfig? decoded = JsonSerializer.Deserialize(
            template,
            GalateaJsonContext.Default.GalateaUsersFileConfig
        );
        Assert.NotNull(decoded);
        Assert.Equal(
            GalateaStrictConfigReader.CurrentConfigVersion,
            decoded.Version
        );
        Assert.Equal(
            ["sessions/alice", "sessions/bob"],
            decoded.Users.Select(static user => user.SessionDir)
        );
        Assert.Equal(
            ["delegation-state/alice", "delegation-state/bob"],
            decoded.Users.Select(static user => user.DelegationStateDir)
        );
        Assert.Equal(
            ["Alice", "Bob"],
            decoded.Users.Select(static user => user.CharacterName)
        );
        Assert.Equal(
            ["Alex", "Blair"],
            decoded.Users.Select(static user => user.PlayerName)
        );
        Assert.All(decoded.Users, static user => Assert.Equal(
            string.Empty,
            user.CharacterContextTemplate
        ));
        Assert.All(decoded.Users, static user => Assert.Equal(
            GalateaDefaults.CharacterContextTemplateFile,
            user.CharacterContextTemplateFile
        ));
        Assert.All(
            decoded.Users,
            static user => Assert.Equal(
                GalateaSessionProvisioning.CreateIfMissing,
                user.SessionProvisioning
            )
        );
        Assert.Null(typeof(GalateaConfig).GetProperty("Version"));
    }

    [Fact]
    public void ProductionBootstrapWritesBomlessRootConfigThatStrictLoaderReads() {
        string root = NewRoot();
        try {
            string configPath = Path.Combine(root, "config.json");
            File.WriteAllBytes(
                Path.Combine(
                    root,
                    "recap-grid-agent-control-profile.json"
                ),
                CreateProfile("default").ToCanonicalBytes()
            );

            Assert.Throws<InvalidOperationException>(() =>
                GalateaConfigBootstrapper.EnsureExistsOrBootstrap(configPath)
            );

            byte[] usersBytes = File.ReadAllBytes(configPath);
            Assert.False(usersBytes.AsSpan().StartsWith(
                Encoding.UTF8.GetPreamble()
            ));
            GalateaStrictConfigReader.ValidateUsers(usersBytes);

            string delegatesPath = Path.Combine(
                root,
                GalateaConfigLoader.DelegatesFileName
            );
            Assert.Contains(
                "REPLACE_WITH_CANONICAL_NODE_EXECUTABLE",
                File.ReadAllText(delegatesPath),
                StringComparison.Ordinal
            );
            GalateaTestHost.WriteDelegatesFile(root);

            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);
            Assert.Equal(["alice", "bob"],
                loaded.Users.Select(static user => user.UserId));
            Assert.Equal(
                ["Alice", "Bob"],
                loaded.Users.Select(static user =>
                    user.CharacterName.Value)
            );
            Assert.Equal(
                ["Alex", "Blair"],
                loaded.Users.Select(static user =>
                    user.PlayerName.Value)
            );
            string generatedPrompt = Path.Combine(
                root,
                GalateaDefaults.CharacterContextTemplateFile
            );
            Assert.Equal(
                GalateaBuiltInCharacterContextTemplate.Utf8.ToArray(),
                File.ReadAllBytes(generatedPrompt)
            );
            Assert.Equal(
                GalateaConfigTemplateFactory.DefaultConnectionId,
                loaded.DefaultConnectionId
            );
            Assert.Equal(
                [
                    Path.Combine(root, "sessions", "alice"),
                    Path.Combine(root, "sessions", "bob")
                ],
                loaded.Users.Select(static user => user.SessionDir)
            );
            Assert.Equal(
                [
                    Path.Combine(root, "delegation-state", "alice"),
                    Path.Combine(root, "delegation-state", "bob")
                ],
                loaded.Users.Select(static user =>
                    user.DelegationStateDir)
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingConfigBootstrapCreatesMissingInRootPromptOnce() {
        string root = NewRoot();
        try {
            string promptRelative = Path.Combine(
                "prompts",
                "custom.md"
            );
            string promptPath = Path.Combine(root, promptRelative);
            string configPath = WriteFileConfig(
                root,
                [new GalateaUserFileConfig(
                    "alice",
                    "pw",
                    "Galatea",
                    "刘世超",
                    Path.Combine(root, "session"),
                    Path.Combine(root, "delegation-state"),
                    GalateaSessionProvisioning.ExistingOnly,
                    CharacterContextTemplate: "",
                    CharacterContextTemplateFile: promptRelative
                )]
            );
            Assert.False(File.Exists(promptPath));

            InvalidOperationException generated = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigBootstrapper.EnsureExistsOrBootstrap(
                configPath
            ));

            Assert.Contains(promptPath, generated.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                GalateaBuiltInCharacterContextTemplate.Utf8.ToArray(),
                File.ReadAllBytes(promptPath)
            );
            Assert.DoesNotContain(
                "GM carrier",
                File.ReadAllText(promptPath),
                StringComparison.Ordinal
            );
            GalateaConfigBootstrapper.EnsureExistsOrBootstrap(configPath);
            GalateaUserConfig user = Assert.Single(
                GalateaConfigLoader.Load(configPath).Users
            );
            Assert.Contains("Galatea", user.SystemPrompt,
                StringComparison.Ordinal);
            Assert.Contains("刘世超", user.SystemPrompt,
                StringComparison.Ordinal);
            Assert.Contains("GM carrier", user.SystemPrompt,
                StringComparison.Ordinal);
            Assert.Contains("## 界外邮箱", user.SystemPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain("### 发信给 Codex", user.SystemPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "### 提交 Note 请求（开发中）",
                user.SystemPrompt,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain("${", user.SystemPrompt,
                StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BootstrapNeverOverwritesOrCreatesOutsideConfigRoot() {
        string root = NewRoot();
        string external = NewRoot();
        try {
            string existingPrompt = Path.Combine(root, "existing.md");
            byte[] existing =
                "${characterName} meets ${playerName}"u8.ToArray();
            File.WriteAllBytes(existingPrompt, existing);
            string configPath = WriteFileConfig(
                root,
                [new GalateaUserFileConfig(
                    "alice",
                    "pw",
                    "Galatea",
                    "刘世超",
                    Path.Combine(root, "session"),
                    Path.Combine(root, "delegation-state"),
                    GalateaSessionProvisioning.ExistingOnly,
                    CharacterContextTemplate: "",
                    CharacterContextTemplateFile: existingPrompt
                )]
            );

            GalateaConfigBootstrapper.EnsureExistsOrBootstrap(configPath);
            Assert.Equal(existing, File.ReadAllBytes(existingPrompt));

            string outsidePrompt = Path.Combine(external, "missing.md");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(
                    GalateaConfigTemplateFactory.CreateUsersFile() with {
                        Users = [new GalateaUserFileConfig(
                            "alice",
                            "pw",
                            "Galatea",
                            "刘世超",
                            Path.Combine(root, "session"),
                            Path.Combine(root, "delegation-state"),
                            GalateaSessionProvisioning.ExistingOnly,
                            CharacterContextTemplate: "",
                            CharacterContextTemplateFile: outsidePrompt
                        )]
                    },
                    GalateaJson.Options
                )
            );
            GalateaConfigBootstrapper.EnsureExistsOrBootstrap(configPath);
            Assert.False(File.Exists(outsidePrompt));
            Assert.Throws<FileNotFoundException>(() =>
                GalateaConfigLoader.Load(configPath));
        }
        finally {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task RelativeSessionDirectoryUsesConfigDirectoryNotProcessCurrentDirectory() {
        string root = NewRoot();
        string configDirectory = Path.Combine(root, "configuration");
        Directory.CreateDirectory(configDirectory);
        string expectedSessionDirectory = Path.Combine(
            configDirectory,
            "sessions",
            "alice"
        );
        string expectedDelegationStateDirectory = Path.Combine(
            configDirectory,
            "delegation-state",
            "alice"
        );
        try {
            Assert.NotEqual(
                Path.GetFullPath(Path.Combine("sessions", "alice")),
                expectedSessionDirectory
            );
            using (SessionJournalEngine engine =
                   SessionJournalEngine.Create(
                       expectedSessionDirectory,
                       new SessionCreateOptions(
                           "model-a",
                           "prompt",
                           "openai-chat/strict"
                       ))) {
                Assert.Equal(expectedSessionDirectory, engine.Path);
            }

            string configPath = WriteConfig(
                configDirectory,
                [User(
                    "alice",
                    "sessions/alice",
                    "delegation-state/alice"
                )]
            );
            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);
            GalateaUserConfig loadedUser = Assert.Single(loaded.Users);
            Assert.Equal(expectedSessionDirectory, loadedUser.SessionDir);
            Assert.Equal(
                expectedDelegationStateDirectory,
                loadedUser.DelegationStateDir
            );

            var factory = new TrackingFactory();
            await using var service = new GalateaHostService(
                loaded with { MaintenanceMode = true },
                factory,
                DisabledGalateaUserMessageNormalizer.Instance
            );
            UserSessionHost session = await service.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            Assert.Equal(expectedSessionDirectory, session.Engine.Path);
            Assert.Equal(0, factory.CreateCallCount);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AbsoluteSessionDirectoryTargetIsPreserved() {
        string root = NewRoot();
        string repositoryRoot = NewRoot();
        string absoluteSessionDirectory = Path.Combine(
            repositoryRoot,
            "session"
        );
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", absoluteSessionDirectory)]
            );

            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);

            Assert.Equal(
                absoluteSessionDirectory,
                Assert.Single(loaded.Users).SessionDir
            );
            Assert.False(Directory.Exists(absoluteSessionDirectory));
        }
        finally {
            Directory.Delete(root, recursive: true);
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void RelativeAndAbsoluteSessionAliasesAreRejectedAfterResolution() {
        string root = NewRoot();
        string expectedSessionDirectory = Path.Combine(
            root,
            "sessions",
            "alice"
        );
        try {
            string configPath = WriteConfig(
                root,
                [
                    User("alice", "sessions/alice"),
                    User("bob", expectedSessionDirectory)
                ]
            );

            InvalidOperationException failure = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigLoader.Load(configPath));

            AssertDuplicateDetail(failure, expectedSessionDirectory);
            Assert.False(Directory.Exists(expectedSessionDirectory));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DelegationStatePathsResolveExactlyAndMustRemainDisjoint() {
        string root = NewRoot();
        string external = NewRoot();
        try {
            string absolute = Path.Combine(external, "delegation-state");
            string configPath = WriteConfig(
                root,
                [User("alice", "sessions/alice", absolute)]
            );
            Assert.Equal(
                absolute,
                Assert.Single(GalateaConfigLoader.Load(configPath).Users)
                    .DelegationStateDir
            );

            GalateaUserConfig[] duplicate = [
                User("alice", "sessions/alice", "delegation/shared"),
                User("bob", "sessions/bob", "./delegation/shared")
            ];
            Assert.Throws<InvalidOperationException>(() =>
                GalateaConfigLoader.Load(WriteConfig(root, duplicate)));

            GalateaUserConfig[] nestedDelegation = [
                User("alice", "sessions/alice", "delegation/alice"),
                User("bob", "sessions/bob", "delegation/alice/bob")
            ];
            Assert.Throws<InvalidOperationException>(() =>
                GalateaConfigLoader.Load(WriteConfig(
                    root,
                    nestedDelegation
                )));

            GalateaUserConfig[] crossUserNesting = [
                User(
                    "alice",
                    "sessions/alice",
                    "sessions/bob/delegation"
                ),
                User("bob", "sessions/bob", "delegation/bob")
            ];
            Assert.Throws<InvalidOperationException>(() =>
                GalateaConfigLoader.Load(WriteConfig(
                    root,
                    crossUserNesting
                )));

            foreach (string delegationPath in new[] {
                         "sessions/alice",
                         "sessions/alice/delegation",
                         "sessions"
                     }) {
                Assert.Throws<InvalidOperationException>(() =>
                    GalateaConfigLoader.Load(WriteConfig(
                        root,
                        [User(
                            "alice",
                            "sessions/alice",
                            delegationPath
                        )]
                    )));
            }
        }
        finally {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void DelegationStatePathRejectsExistingAncestorSymlink() {
        if (!OperatingSystem.IsLinux()) { return; }
        string root = NewRoot();
        string external = NewRoot();
        try {
            string link = Path.Combine(root, "delegation-link");
            Directory.CreateSymbolicLink(link, external);
            string configPath = WriteConfig(
                root,
                [User(
                    "alice",
                    "sessions/alice",
                    "delegation-link/alice"
                )]
            );

            InvalidOperationException failure = Assert.Throws<
                InvalidOperationException>(() =>
                    GalateaConfigLoader.Load(configPath));
            Assert.Contains(
                "delegationStateDir",
                failure.Message,
                StringComparison.Ordinal
            );
            Assert.Contains("symlink", failure.Message);
        }
        finally {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void ResolvedRelativeSessionAndCallLogNestingIsRejected() {
        string root = NewRoot();
        string expectedSessionDirectory = Path.Combine(
            root,
            "sessions",
            "alice"
        );
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", "sessions/alice")],
                callLogDirectory: "sessions/alice/call-logs"
            );

            InvalidOperationException failure = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigLoader.Load(configPath));

            Assert.Contains("disjoint", failure.Message);
            Assert.False(Directory.Exists(expectedSessionDirectory));

            configPath = WriteConfig(
                root,
                [User(
                    "alice",
                    "sessions/alice",
                    "delegation-state/alice"
                )],
                callLogDirectory: "delegation-state"
            );
            Assert.Throws<InvalidOperationException>(() =>
                GalateaConfigLoader.Load(configPath));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RootConfigAcceptsExactV5OutsideFirstProperty() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string original = File.ReadAllText(configPath);
            const string LeadingVersion = "{\"v\":5,";
            Assert.StartsWith(LeadingVersion, original);
            string reordered = "{"
                + original[LeadingVersion.Length..^1]
                + ",\"v\":5}";
            File.WriteAllText(configPath, reordered);

            GalateaConfig loaded = GalateaConfigLoader.Load(configPath);
            Assert.Equal("alice", Assert.Single(loaded.Users).UserId);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RootConfigRequiresExactIntegerV5AndRejectsOtherVersions() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string original = File.ReadAllText(configPath);
            const string Version = "\"v\":5";
            Assert.Contains(Version, original, StringComparison.Ordinal);

            string[] invalid = [
                original.Replace(
                    Version + ",",
                    string.Empty,
                    StringComparison.Ordinal
                ),
                original.Replace(Version, "\"v\":null",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":\"5\"",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":0",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":1",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":2",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":3",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":4",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":6",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":5.0",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"v\":5e0",
                    StringComparison.Ordinal),
                original.Replace(Version, "\"V\":5",
                    StringComparison.Ordinal),
                original.Replace(
                    Version + ",",
                    Version + "," + Version + ",",
                    StringComparison.Ordinal
                )
            ];

            foreach (string candidate in invalid) {
                Assert.NotEqual(original, candidate);
                File.WriteAllText(configPath, candidate);
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
    public void InvalidRootVersionPrecedesSiblingReadsAndWritesNothing() {
        string root = NewRoot();
        try {
            string configPath = Path.Combine(root, "config.json");
            File.WriteAllText(
                configPath,
                """
                {"v":4,"users":[{"userId":"alice","password":"pw","sessionDir":"session","delegationStateDir":"delegation-state","sessionProvisioning":"existing-only","systemPrompt":"","systemPromptFile":"missing-prompt.txt"}],"recapGrid":{"routeManifestPath":"missing-routes.json","agentControlProfileFiles":["missing-profile.json"],"currentAgentControlProfileId":"missing"}}
                """
            );

            InvalidDataException bootstrapFailure = Assert.Throws<
                InvalidDataException
            >(() => GalateaConfigBootstrapper.EnsureExistsOrBootstrap(
                configPath
            ));

            Assert.Contains(
                "migrate",
                bootstrapFailure.Message,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Equal(
                [Path.GetFullPath(configPath)],
                Directory.EnumerateFiles(
                        root,
                        "*",
                        SearchOption.AllDirectories
                    )
                    .Select(Path.GetFullPath)
                    .ToArray()
            );
            Assert.Empty(Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.AllDirectories
            ));

            InvalidDataException loadFailure = Assert.Throws<
                InvalidDataException
            >(() => GalateaConfigLoader.Load(configPath));
            Assert.Contains(
                "migrate",
                loadFailure.Message,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BootstrapDoesNotRewriteExistingVersionlessConfig() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string connectionsPath = Path.Combine(
                root,
                GalateaConfigLoader.ConnectionsFileName
            );
            byte[] versionless = File.ReadAllBytes(configPath);
            versionless = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(versionless).Replace(
                    "\"v\":5,",
                    string.Empty,
                    StringComparison.Ordinal
                )
            );
            File.WriteAllBytes(configPath, versionless);
            byte[] connectionsBefore = File.ReadAllBytes(connectionsPath);

            InvalidDataException bootstrapFailure = Assert.Throws<
                InvalidDataException
            >(() => GalateaConfigBootstrapper.EnsureExistsOrBootstrap(
                configPath
            ));

            Assert.Equal(versionless, File.ReadAllBytes(configPath));
            Assert.Equal(
                connectionsBefore,
                File.ReadAllBytes(connectionsPath)
            );
            Assert.Contains(
                "migrate",
                bootstrapFailure.Message,
                StringComparison.OrdinalIgnoreCase
            );
            InvalidDataException failure = Assert.Throws<
                InvalidDataException
            >(() => GalateaConfigLoader.Load(configPath));
            Assert.Contains(
                "migrate",
                failure.Message,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StrictRecapGridConfigDefersRouteReadAndCompletionClientCreation() {
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
    public void DelegateSiblingIsRequiredByTheMergedLoader() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string delegatesPath = Path.Combine(
                root,
                GalateaConfigLoader.DelegatesFileName
            );
            File.Delete(delegatesPath);

            FileNotFoundException failure = Assert.Throws<
                FileNotFoundException>(() =>
                    GalateaConfigLoader.Load(configPath));
            Assert.Equal(delegatesPath, failure.FileName);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublicHostRejectsProgrammaticDelegateValidationBypass() {
        string root = NewRoot();
        try {
            GalateaConfig loaded = LoadConstructionFixture(root);
            var invalid = loaded with {
                Delegates = loaded.Delegates with {
                    Sidecar = loaded.Delegates.Sidecar with {
                        RpcTimeoutMs = 99
                    }
                }
            };
            var factory = new TrackingFactory();

            Assert.Throws<InvalidDataException>(() =>
                new GalateaHostService(
                    invalid,
                    factory,
                    DisabledGalateaUserMessageNormalizer.Instance
                ));
            Assert.Equal(0, factory.CreateCallCount);
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
                    RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
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
                        Version: GalateaStrictConfigReader.CurrentConfigVersion,
                        Users: [FileUser(
                            User("alice", Path.Combine(root, "session"))
                        )],
                        RecapGrid: new GalateaRecapGridFileConfig(
                            "routes.json",
                            ["profile.json"],
                            "test-profile"
                        )
                    ),
                    GalateaJson.Options
                )
            );
            GalateaTestHost.WriteConnectionsFile(
                Path.Combine(root, GalateaConfigLoader.ConnectionsFileName),
                Connections,
                "test"
            );
            GalateaTestHost.WriteDelegatesFile(root);

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
                    "{\"v\":5,\"users\"",
                    "{\"v\":5,\"unknown\":1,\"users\"",
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
                    "\"delegationStateDir\":",
                    "\"DelegationStateDir\":",
                    StringComparison.Ordinal
                ),
                originalConfig.Replace(
                    "\"delegationStateDir\":",
                    "\"delegationStateDir\":\"first\","
                    + "\"DelegationStateDir\":",
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
                    "{\"v\":1,",
                    "{\"unknown\":1,\"v\":1,",
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
    public void ConnectionsRequireCompletionOwnedV1AndBootstrapRoundTrips() {
        byte[] template = GalateaConfigTemplateFactory
            .CreateConnectionsFileUtf8();
        CompletionConnectionsFileConfig decoded =
            CompletionConnectionConfigLoader.Decode(template);
        Assert.Single(decoded.Connections);
        Assert.Equal(
            [GalateaConfigTemplateFactory.DefaultConnectionId],
            decoded.SelectableConnectionIds
        );
        Assert.Null(decoded.Bindings![
            GalateaCompletionOwner.InputNormalizerBindingKey
        ]);
        Assert.Null(decoded.Bindings[
            GalateaCompletionOwner.OutboundMailExtractorBindingKey
        ]);
        Assert.Null(decoded.Bindings[
            GalateaCompletionOwner.CharacterNoteExtractorBindingKey
        ]);
        Assert.Equal(3, decoded.Bindings.Count);
        using (JsonDocument document = JsonDocument.Parse(template)) {
            JsonElement root = document.RootElement;
            Assert.Equal("1", root.GetProperty("v").GetRawText());
            JsonElement item = root.GetProperty("connections")[0];
            Assert.True(item.TryGetProperty("baseAddress", out _));
            Assert.False(item.TryGetProperty("baseAddressEnv", out _));
            Assert.True(item.TryGetProperty("apiKey", out _));
            Assert.False(item.TryGetProperty("apiKeyEnv", out _));
        }

        string rootDirectory = NewRoot();
        try {
            string configPath = WriteConfig(
                rootDirectory,
                [User("alice", Path.Combine(rootDirectory, "session"))]
            );
            string connectionsPath = Path.Combine(
                rootDirectory,
                GalateaConfigLoader.ConnectionsFileName
            );
            string noVersion = File.ReadAllText(connectionsPath).Replace(
                "\"v\":1,",
                string.Empty,
                StringComparison.Ordinal
            );
            File.WriteAllText(connectionsPath, noVersion);

            Assert.Throws<InvalidDataException>(() =>
                GalateaConfigLoader.Load(configPath)
            );
        }
        finally {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void GalateaConnectionsRequireExactSelectionAndFeatureBindings() {
        string root = NewRoot();
        try {
            string configPath = WriteConfig(
                root,
                [User("alice", Path.Combine(root, "session"))]
            );
            string path = Path.Combine(
                root,
                GalateaConfigLoader.ConnectionsFileName
            );
            JsonObject original = JsonNode.Parse(
                File.ReadAllText(path)
            )!.AsObject();

            GalateaConfig outboundDisabled = Load(original);
            string disabledPrompt = Assert.Single(
                outboundDisabled.Users
            ).SystemPrompt;
            Assert.Null(outboundDisabled.OutboundMailExtractorConnectionId);
            Assert.Null(
                outboundDisabled.CharacterNoteExtractorConnectionId
            );
            Assert.Contains("## 界外邮箱", disabledPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain("### 发信给 Codex", disabledPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "### 提交 Note 请求（开发中）",
                disabledPrompt,
                StringComparison.Ordinal
            );

            JsonObject enabled = original.DeepClone().AsObject();
            enabled["bindings"]!.AsObject()[
                GalateaCompletionOwner.OutboundMailExtractorBindingKey
            ] = "test";
            GalateaConfig outboundEnabled = Load(enabled);
            string enabledPrompt = Assert.Single(
                outboundEnabled.Users
            ).SystemPrompt;
            Assert.Equal(
                "test",
                outboundEnabled.OutboundMailExtractorConnectionId
            );
            Assert.Contains("## 界外邮箱", enabledPrompt,
                StringComparison.Ordinal);
            Assert.Contains("### 发信给 Codex", enabledPrompt,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "### 提交 Note 请求（开发中）",
                enabledPrompt,
                StringComparison.Ordinal
            );
            Assert.NotEqual(disabledPrompt, enabledPrompt);

            JsonObject noteEnabledJson = original.DeepClone().AsObject();
            noteEnabledJson["bindings"]!.AsObject()[
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            ] = "test";
            GalateaConfig noteEnabled = Load(noteEnabledJson);
            Assert.Equal(
                "test",
                noteEnabled.CharacterNoteExtractorConnectionId
            );
            Assert.Null(noteEnabled.OutboundMailExtractorConnectionId);
            string noteEnabledPrompt = Assert.Single(
                noteEnabled.Users
            ).SystemPrompt;
            Assert.DoesNotContain("### 发信给 Codex", noteEnabledPrompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "### 提交 Note 请求（开发中）",
                noteEnabledPrompt,
                StringComparison.Ordinal
            );
            Assert.Contains("尚不保存、索引或召回Note", noteEnabledPrompt,
                StringComparison.Ordinal);
            Assert.Contains("明确完成“提交Note请求”的动作", noteEnabledPrompt,
                StringComparison.Ordinal);
            Assert.NotEqual(disabledPrompt, noteEnabledPrompt);

            JsonObject bothEnabledJson = enabled.DeepClone().AsObject();
            bothEnabledJson["bindings"]!.AsObject()[
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            ] = "test";
            string bothEnabledPrompt = Assert.Single(
                Load(bothEnabledJson).Users
            ).SystemPrompt;
            Assert.True(
                bothEnabledPrompt.IndexOf(
                    "### 发信给 Codex",
                    StringComparison.Ordinal
                ) < bothEnabledPrompt.IndexOf(
                    "### 提交 Note 请求（开发中）",
                    StringComparison.Ordinal
                )
            );

            AssertRejected(original, "selectableConnectionIds");
            AssertRejected(original, "bindings");

            JsonObject missingKey = original.DeepClone().AsObject();
            Assert.True(missingKey["bindings"]!.AsObject().Remove(
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            ));
            Assert.Throws<InvalidDataException>(() => Load(
                missingKey
            ));

            JsonObject wrongCase = original.DeepClone().AsObject();
            wrongCase["bindings"] = new JsonObject {
                [GalateaCompletionOwner.InputNormalizerBindingKey] = null,
                [GalateaCompletionOwner.OutboundMailExtractorBindingKey] =
                    null,
                ["Galatea.Character-Note-Extractor"] = null,
            };
            Assert.Throws<InvalidDataException>(() => Load(wrongCase));

            JsonObject unknown = original.DeepClone().AsObject();
            unknown["bindings"]!.AsObject()[
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            ] = "unknown";
            Assert.Throws<InvalidDataException>(() => Load(unknown));

            JsonObject blank = original.DeepClone().AsObject();
            blank["bindings"]!.AsObject()[
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            ] = "";
            Assert.Throws<InvalidDataException>(() => Load(blank));

            JsonObject extra = original.DeepClone().AsObject();
            extra["bindings"]!.AsObject()["galatea.future"] = null;
            Assert.Throws<InvalidDataException>(() => Load(extra));

            void AssertRejected(JsonObject source, string property) {
                JsonObject candidate = source.DeepClone().AsObject();
                Assert.True(candidate.Remove(property));
                Assert.Throws<InvalidDataException>(() => Load(candidate));
            }

            GalateaConfig Load(JsonObject candidate) {
                File.WriteAllText(path, candidate.ToJsonString());
                return GalateaConfigLoader.Load(configPath);
            }
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LateDuplicateUserFailureDisposesEagerNormalizerClientOnce() {
        string root = NewRoot();
        try {
            GalateaConfig config = LoadConstructionFixture(root) with {
                Users = [
                    User("duplicate", Path.Combine(root, "session-a")),
                    User("duplicate", Path.Combine(root, "session-b")),
                ],
                InputNormalizerConnectionId = "test",
            };
            var client = new ConstructionClient();

            Assert.Throws<ArgumentException>(() => new GalateaHostService(
                config,
                new ConstructionClientFactory(client),
                new EagerNormalizerFactory()
            ));

            Assert.Equal(1, client.DisposeCount);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LateConstructionAndCleanupFailuresPreserveBothExceptions() {
        string root = NewRoot();
        try {
            GalateaConfig config = LoadConstructionFixture(root) with {
                Users = [
                    User("duplicate", Path.Combine(root, "session-a")),
                    User("duplicate", Path.Combine(root, "session-b")),
                ],
                InputNormalizerConnectionId = "test",
            };
            var client = new ConstructionClient(
                new IOException("client cleanup failed")
            );

            AggregateException failure = Assert.Throws<AggregateException>(
                () => new GalateaHostService(
                    config,
                    new ConstructionClientFactory(client),
                    new EagerNormalizerFactory()
                )
            );

            Assert.Contains(
                failure.InnerExceptions,
                static value => value is ArgumentException
            );
            Assert.Contains(
                failure.InnerExceptions,
                static value => value is IOException
                    && value.Message == "client cleanup failed"
            );
            Assert.Equal(1, client.DisposeCount);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NormalizerFactoryAndCleanupFailuresPreserveBothExceptions() {
        string root = NewRoot();
        try {
            GalateaConfig config = LoadConstructionFixture(root) with {
                InputNormalizerConnectionId = "test",
            };
            var client = new ConstructionClient(
                new IOException("client cleanup failed")
            );

            AggregateException failure = Assert.Throws<AggregateException>(
                () => new GalateaHostService(
                    config,
                    new ConstructionClientFactory(client),
                    new EagerNormalizerFactory(
                        new InvalidOperationException(
                            "normalizer factory failed"
                        )
                    )
                )
            );

            Assert.Contains(
                failure.InnerExceptions,
                static value => value is InvalidOperationException
                    && value.Message == "normalizer factory failed"
            );
            Assert.Contains(
                failure.InnerExceptions,
                static value => value is IOException
                    && value.Message == "client cleanup failed"
            );
            Assert.Equal(1, client.DisposeCount);
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
            File.WriteAllText(prompt, "${characterName}");
            configPath = WriteFileConfig(
                root,
                [new GalateaUserFileConfig(
                    "alice",
                    "pw",
                    "Galatea",
                    "刘世超",
                    Path.Combine(root, "session"),
                    Path.Combine(root, "delegation-state"),
                    GalateaSessionProvisioning.ExistingOnly,
                    CharacterContextTemplate: "",
                    CharacterContextTemplateFile: "prompt.txt"
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
                         "characterContextTemplateFile"
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
            "test",
            ["test"],
            InputNormalizerConnectionId: null,
            Delegates: GalateaDelegateTestConfiguration.Create(root)
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
        IReadOnlyList<GalateaUserConfig> users,
        string? callLogDirectory = null,
        IReadOnlyList<string>? listenUrls = null,
        IReadOnlyList<CompletionConnectionConfig>? connections = null,
        string defaultConnectionId = "test",
        IReadOnlyList<string>? selectableConnectionIds = null,
        string? inputNormalizerConnectionId = null
    ) {
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new GalateaUsersFileConfig(
                    Version: GalateaStrictConfigReader.CurrentConfigVersion,
                    Users: users.Select(FileUser).ToArray(),
                    ListenUrls: listenUrls,
                    CallLogDir: callLogDirectory,
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
        GalateaTestHost.WriteConnectionsFile(
            Path.Combine(root, GalateaConfigLoader.ConnectionsFileName),
            connections ?? Connections,
            defaultConnectionId,
            selectableConnectionIds,
            inputNormalizerConnectionId
        );
        GalateaTestHost.WriteDelegatesFile(root);
        return configPath;
    }

    private static string WriteFileConfig(
        string root,
        IReadOnlyList<GalateaUserFileConfig> users
    ) {
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new GalateaUsersFileConfig(
                    Version: GalateaStrictConfigReader.CurrentConfigVersion,
                    Users: users,
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
        GalateaTestHost.WriteConnectionsFile(
            Path.Combine(root, GalateaConfigLoader.ConnectionsFileName),
            Connections,
            "test"
        );
        GalateaTestHost.WriteDelegatesFile(root);
        return configPath;
    }

    private static GalateaUserFileConfig FileUser(
        GalateaUserConfig user
    ) => new(
        user.UserId,
        user.Password,
        user.CharacterName.Value,
        user.PlayerName.Value,
        user.SessionDir,
        user.DelegationStateDir,
        user.SessionProvisioning,
        CharacterContextTemplate: "prompt ${characterName}"
    );

    private static RecapGridAgentControlProfile CreateProfile(
        string profileId = "test-profile"
    ) {
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
                out RecapGridControlRegistrationBundle? builtIn
            ));
        return RecapGridAgentControlProfile.Create(
            profileId,
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
        string sessionDirectory,
        string? delegationStateDirectory = null
    ) => new(
        userId,
        "pw",
        new GalateaCharacterName("Galatea"),
        new GalateaPlayerName("刘世超"),
        sessionDirectory,
        delegationStateDirectory
            ?? sessionDirectory + "-delegation-state-" + userId,
        GalateaSessionProvisioning.ExistingOnly,
        SystemPrompt: "prompt Galatea"
    );

    private static GalateaConfig LoadConstructionFixture(string root) {
        string configPath = WriteConfig(
            root,
            [User("alice", Path.Combine(root, "session"))]
        );
        return GalateaConfigLoader.Load(configPath);
    }

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

    private sealed class ConstructionClientFactory(
        ConstructionClient client
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return client;
        }
    }

    private sealed class EagerNormalizerFactory(
        Exception? failure = null
    ) : IGalateaUserMessageNormalizerFactory {
        public IGalateaUserMessageNormalizer Create(
            CompletionConnectionConfig? connection,
            Func<ICompletionClient> getClient
        ) {
            Assert.NotNull(connection);
            _ = getClient();
            if (failure is not null) { throw failure; }
            return DisabledGalateaUserMessageNormalizer.Instance;
        }
    }

    private sealed class ConstructionClient(Exception? disposeFailure = null)
        : ICompletionClient, IDisposable {
        private int _disposeCount;

        public string Name => "galatea-construction-test";

        public string ApiSpecId => "test-v1";

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "Construction tests must not dispatch."
        );

        public void Dispose() {
            Interlocked.Increment(ref _disposeCount);
            if (disposeFailure is not null) { throw disposeFailure; }
        }
    }
}

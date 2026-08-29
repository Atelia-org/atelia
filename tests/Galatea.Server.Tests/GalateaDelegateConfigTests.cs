using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegateConfigTests {
    [Fact]
    public void ValidClosedV2LoadsCanonicalExactCodexRouteAndToolPolicy() {
        using var fixture = new Fixture();

        GalateaDelegateConfig config = fixture.Load();

        Assert.Single(config.Routes);
        Assert.Equal("Codex", config.CodexRoute.Recipient);
        Assert.Equal("codex-app-server", config.CodexRoute.Kind);
        Assert.Equal(fixture.Root, config.CodexRoute.Cwd);
        Assert.Equal(GalateaDelegateMode.Work, config.CodexRoute.Mode);
        Assert.False(config.CodexRoute.LocalCommandNetwork);
        Assert.Equal(
            GalateaDelegateWebSearchMode.Live,
            config.CodexRoute.Tools.WebSearch
        );
        Assert.True(config.CodexRoute.Tools.ImageGeneration);
        Assert.True(config.CodexRoute.Tools.ViewImage);
        Assert.Equal(1_048_576, config.Sidecar.MaximumFrameUtf8Bytes);
    }

    [Fact]
    public void LegacyV1IsRejectedWithoutCompatibilityFallback() {
        using var fixture = new Fixture();
        string legacy = fixture.ValidJson.Replace(
            "\"v\": 2,",
            "\"v\": 1,",
            StringComparison.Ordinal
        );

        Assert.Throws<InvalidDataException>(() => fixture.Load(legacy));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong-case")]
    [InlineData("missing")]
    [InlineData("duplicate-case-variant")]
    public void ClosedObjectsRejectUnknownMissingWrongCaseAndDuplicates(
        string mutation
    ) {
        using var fixture = new Fixture();
        string json = fixture.ValidJson;
        json = mutation switch {
            "unknown" => json.Replace(
                "\"rpcTimeoutMs\": 1000,",
                "\"rpcTimeoutMs\": 1000,\n\"surprise\": 1,",
                StringComparison.Ordinal
            ),
            "wrong-case" => json.Replace(
                "\"allowedRoots\"",
                "\"AllowedRoots\"",
                StringComparison.Ordinal
            ),
            "missing" => json.Replace(
                "\"shutdownGraceMs\": 100,",
                string.Empty,
                StringComparison.Ordinal
            ),
            "duplicate-case-variant" => json.Replace(
                "\"v\": 2,",
                "\"v\": 2,\n\"V\": 2,",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.Throws<InvalidDataException>(() => fixture.Load(json));
    }

    [Theory]
    [InlineData("codex", "codex-app-server")]
    [InlineData("Codex", "Codex-App-Server")]
    public void RouteIdentityIsExactCaseSensitive(
        string recipient,
        string kind
    ) {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        JsonObject route = root["routes"]!.AsArray()[0]!.AsObject();
        route["recipient"] = recipient;
        route["kind"] = kind;

        Assert.Throws<InvalidDataException>(() => fixture.Load(root));
    }

    [Fact]
    public void RoutesMustContainExactlyOneEntry() {
        using var fixture = new Fixture();
        JsonObject empty = fixture.Parse();
        empty["routes"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => fixture.Load(empty));

        JsonObject multiple = fixture.Parse();
        JsonArray routes = multiple["routes"]!.AsArray();
        routes.Add(routes[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => fixture.Load(multiple));
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("unknown")]
    [InlineData("")]
    public void WebSearchModeIsClosedAndCaseSensitive(string mode) {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        root["routes"]!.AsArray()[0]!.AsObject()["tools"]!
            .AsObject()["webSearch"] = mode;

        Assert.Throws<InvalidDataException>(() => fixture.Load(root));
    }

    [Theory]
    [InlineData("rpcTimeoutMs", 99)]
    [InlineData("rpcTimeoutMs", 300001)]
    [InlineData("turnTimeoutMs", 99)]
    [InlineData("shutdownGraceMs", 9)]
    [InlineData("maximumFrameUtf8Bytes", 1023)]
    public void SidecarRangesAreClosed(string property, int value) {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        root["sidecar"]!.AsObject()[property] = value;

        Assert.Throws<InvalidDataException>(() => fixture.Load(root));
    }

    [Fact]
    public void TaskReplyAndInboxBoundsMustBeFrameCompatible() {
        using var fixture = new Fixture();
        JsonObject task = fixture.Parse();
        task["routes"]!.AsArray()[0]!.AsObject()[
            "maximumTaskUtf8Bytes"] = 174_600;
        Assert.Throws<InvalidDataException>(() => fixture.Load(task));

        JsonObject reply = fixture.Parse();
        reply["routes"]!.AsArray()[0]!.AsObject()[
            "maximumReplyUtf8Bytes"] = 174_600;
        Assert.Throws<InvalidDataException>(() => fixture.Load(reply));

        JsonObject inbox = fixture.Parse();
        inbox["routes"]!.AsArray()[0]!.AsObject()[
            "maximumInboxUtf8Bytes"] = 99_999;
        Assert.Throws<InvalidDataException>(() => fixture.Load(inbox));

        JsonObject failure = fixture.Parse();
        JsonObject failureRoute = failure["routes"]!.AsArray()[0]!
            .AsObject();
        failureRoute["maximumReplyUtf8Bytes"] = 1;
        failureRoute["maximumInboxUtf8Bytes"] =
            PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes - 1;
        Assert.Throws<InvalidDataException>(() => fixture.Load(failure));
    }

    [Fact]
    public void ConfiguredSymlinkIsRejectedAndCanonicalTargetIsAccepted() {
        using var fixture = new Fixture();
        string link = Path.Combine(fixture.Root, "node-link");
        File.CreateSymbolicLink(link, fixture.Executable);
        JsonObject root = fixture.Parse();
        root["sidecar"]!.AsObject()["nodeCommand"] = link;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => fixture.Load(root)
        );
        Assert.Contains("canonical resolved path", failure.Message);
        Assert.Equal(
            fixture.Executable,
            fixture.Load(fixture.ValidJson).Sidecar.NodeCommand
        );
    }

    [Fact]
    public void CommandsMustBeExecutableRegularFiles() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        using var fixture = new Fixture();
        string plain = Path.Combine(fixture.Root, "plain-file");
        File.WriteAllText(plain, "not executable");
        File.SetUnixFileMode(
            plain,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
        JsonObject root = fixture.Parse();
        root["sidecar"]!.AsObject()["codexCommand"] = plain;

        Assert.Throws<InvalidDataException>(() => fixture.Load(root));
    }

    [Fact]
    public void CwdMustBeCanonicalAndContainedInAllowedRoot() {
        using var fixture = new Fixture();
        string outside = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(outside);
        try {
            JsonObject root = fixture.Parse();
            root["routes"]!.AsArray()[0]!.AsObject()["cwd"] = outside;
            Assert.Throws<InvalidDataException>(() => fixture.Load(root));
        }
        finally {
            Directory.Delete(outside);
        }
    }

    [Fact]
    public async Task ProgrammaticConfigGetsFullValidationAndImmutableSnapshot() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        using var fixture = new Fixture();
        GalateaDelegateConfig valid = fixture.Load();
        GalateaDelegateConfig invalid = valid with {
            Sidecar = valid.Sidecar with { RpcTimeoutMs = 99 }
        };
        Assert.Throws<InvalidDataException>(() =>
            new GalateaCodexDurableSidecarClient(invalid));

        string link = Path.Combine(fixture.Root, "programmatic-node-link");
        File.CreateSymbolicLink(link, fixture.Executable);
        Assert.Throws<InvalidDataException>(() =>
            new GalateaCodexDurableSidecarClient(valid with {
                Sidecar = valid.Sidecar with { NodeCommand = link }
            }));

        string plain = Path.Combine(fixture.Root, "programmatic-plain");
        File.WriteAllText(plain, "plain");
        File.SetUnixFileMode(
            plain,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
        Assert.Throws<InvalidDataException>(() =>
            new GalateaCodexDurableSidecarClient(valid with {
                Sidecar = valid.Sidecar with { CodexCommand = plain }
            }));

        Assert.Throws<InvalidDataException>(() =>
            new GalateaCodexDurableSidecarClient(valid with {
                Routes = [valid.CodexRoute with {
                    MaximumTaskUtf8Bytes = 174_600
                }]
            }));

        string outside = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(outside);
        try {
            Assert.Throws<InvalidDataException>(() =>
                new GalateaCodexDurableSidecarClient(valid with {
                    Routes = [valid.CodexRoute with { Cwd = outside }]
                }));
        }
        finally {
            Directory.Delete(outside);
        }

        var mutableRoots = valid.AllowedRoots.ToList();
        var mutableRoutes = valid.Routes.ToList();
        await using var client = new GalateaCodexDurableSidecarClient(
            valid with {
                AllowedRoots = mutableRoots,
                Routes = mutableRoutes
            }
        );
        mutableRoots.Clear();
        mutableRoutes.Clear();

        ProcessStartInfo startInfo = client.CreateStartInfoForTest();
        Assert.Equal(
            JsonSerializer.Serialize(valid.AllowedRoots),
            startInfo.Environment["CODEX_BRIDGE_ALLOWED_ROOTS"]
        );
        Assert.Equal(
            valid.CodexRoute.Cwd,
            startInfo.Environment["CODEX_BRIDGE_DEFAULT_CWD"]
        );
    }

    [Fact]
    public void BootstrapCreatesDelegatePlaceholderWithoutOverwritingExistingFiles() {
        using var fixture = new Fixture(writeDelegates: false);
        string configPath = Path.Combine(fixture.Root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                GalateaConfigTemplateFactory.CreateUsersFile(),
                GalateaJson.Options
            )
        );
        string connectionsPath = Path.Combine(
            fixture.Root,
            GalateaConfigLoader.ConnectionsFileName
        );
        File.WriteAllText(
            connectionsPath,
            "existing-connections"
        );

        Assert.Throws<InvalidOperationException>(() =>
            GalateaConfigBootstrapper.EnsureExistsOrBootstrap(configPath)
        );

        string delegatesPath = Path.Combine(
            fixture.Root,
            GalateaConfigLoader.DelegatesFileName
        );
        Assert.Contains(
            "REPLACE_WITH_CANONICAL_NODE_EXECUTABLE",
            File.ReadAllText(delegatesPath),
            StringComparison.Ordinal
        );
        Assert.Equal("existing-connections", File.ReadAllText(connectionsPath));
    }

    private sealed class Fixture : IDisposable {
        internal Fixture(bool writeDelegates = true) {
            Root = Path.Combine(
                Path.GetTempPath(),
                "atelia-galatea-delegate-config-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Root);
            string processPath = Path.GetFullPath(
                Environment.ProcessPath
                    ?? throw new InvalidOperationException(
                        "The test process executable is unavailable."
                    )
            );
            Executable = new FileInfo(processPath)
                .ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? processPath;
            EntryPoint = Path.GetFullPath(
                typeof(Fixture).Assembly.Location
            );
            ValidJson = BuildJson();
            if (writeDelegates) {
                File.WriteAllText(DelegatesPath, ValidJson);
            }
        }

        internal string Root { get; }
        internal string Executable { get; }
        private string EntryPoint { get; }
        internal string ValidJson { get; }
        private string DelegatesPath => System.IO.Path.Combine(
            Root,
            GalateaConfigLoader.DelegatesFileName
        );

        internal GalateaDelegateConfig Load() =>
            GalateaDelegateConfigReader.Read(DelegatesPath);

        internal GalateaDelegateConfig Load(string json) {
            File.WriteAllText(DelegatesPath, json);
            return Load();
        }

        internal GalateaDelegateConfig Load(JsonObject root) =>
            Load(root.ToJsonString());

        internal JsonObject Parse() => JsonNode.Parse(ValidJson)!.AsObject();

        private string BuildJson() => $$"""
        {
          "v": 2,
          "sidecar": {
            "nodeCommand": {{JsonSerializer.Serialize(Executable)}},
            "entryPoint": {{JsonSerializer.Serialize(EntryPoint)}},
            "codexCommand": {{JsonSerializer.Serialize(Executable)}},
            "rpcTimeoutMs": 1000,
            "turnTimeoutMs": 1000,
            "shutdownGraceMs": 100,
            "maximumFrameUtf8Bytes": 1048576
          },
          "allowedRoots": [{{JsonSerializer.Serialize(Root)}}],
          "routes": [
            {
              "recipient": "Codex",
              "kind": "codex-app-server",
              "cwd": {{JsonSerializer.Serialize(Root)}},
              "mode": "work",
              "localCommandNetwork": false,
              "tools": {
                "webSearch": "live",
                "imageGeneration": true,
                "viewImage": true
              },
              "maximumQueuedMails": 16,
              "maximumTaskUtf8Bytes": 100000,
              "maximumReplyUtf8Bytes": 100000,
              "maximumInboxReplies": 16,
              "maximumInboxUtf8Bytes": 1048576
            }
          ]
        }
        """;

        public void Dispose() {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

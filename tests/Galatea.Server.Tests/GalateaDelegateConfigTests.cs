using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegateConfigTests {
    [Fact]
    public async Task PinnedConfigProbeThroughProductionStartEnvironment() {
        string? repository = Environment.GetEnvironmentVariable("ATELIA_CODEX_HOME_CANARY_REPO");
        if (repository is null) { return; }
        string command = Environment.GetEnvironmentVariable("ATELIA_CODEX_HOME_CANARY_COMMAND")
            ?? throw new InvalidOperationException("The pinned canary executable is required.");
        using var fixture = new Fixture();
        string personal = Path.Combine(fixture.Root, "personal");
        string dedicated = Path.Combine(fixture.Root, "dedicated");
        Directory.CreateDirectory(Path.Combine(personal, ".codex"));
        Directory.CreateDirectory(dedicated);
        File.WriteAllText(Path.Combine(personal, ".codex", "config.toml"), "model = \"personal-sentinel\"\n");
        File.WriteAllText(Path.Combine(dedicated, "config.toml"), "model = \"dedicated-sentinel\"\n");
        string probe = Path.Combine(fixture.Root, "probe.mjs");
        string moduleRoot = new Uri(Path.Combine(repository, "local-codex-mcp", "dist", "src") + "/").AbsoluteUri;
        File.WriteAllText(probe, $$"""
            import assert from 'node:assert/strict';
            import { CodexAppServerClient } from '{{moduleRoot}}codex/client.js';
            import { loadGalateaSidecarConfig, createGalateaCodexChildEnvironment } from '{{moduleRoot}}galatea/sidecar-config.js';
            import { NullLogger } from '{{moduleRoot}}logger.js';
            const config = loadGalateaSidecarConfig();
            const client = new CodexAppServerClient({ command: config.bridge.codexCommand,
              args: config.bridge.codexArgs, env: createGalateaCodexChildEnvironment(process.env),
              requestTimeoutMs: 15000, logger: new NullLogger() });
            try {
              await client.start();
              assert.equal((await client.request('config/read', {})).config.model, 'dedicated-sentinel');
              const thread = await client.request('thread/start', { cwd: {{JsonSerializer.Serialize(fixture.Root)}}, ephemeral: true });
              assert.equal(thread.model, 'dedicated-sentinel');
              console.log('dedicated-home-ok');
            } finally { await client.stop(); }
            """);
        GalateaDelegateConfig config = fixture.Load();
        await using var client = new GalateaCodexDurableSidecarClient(config with {
            Sidecar = config.Sidecar with {
                NodeCommand = "/usr/bin/node", EntryPoint = probe, CodexCommand = command, CodexHome = dedicated
            }
        });
        ProcessStartInfo info = client.CreateStartInfoForTest();
        // Keep the production injection; isolate all unrelated inherited state for this probe.
        foreach (string key in info.Environment.Keys.ToArray()) {
            if (key != "CODEX_HOME" && !key.StartsWith("CODEX_BRIDGE_", StringComparison.Ordinal)
                && !key.StartsWith("GALATEA_CODEX_", StringComparison.Ordinal)) { info.Environment.Remove(key); }
        }
        info.Environment["HOME"] = personal;
        info.Environment["PATH"] = "/usr/bin:/bin";
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("dedicated-home-ok", (await stdout).Trim());
            await stderr;
        }
        finally {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public void CodexHomeIsRequiredAndValidatedForJsonAndProgrammaticConfig() {
        using var fixture = new Fixture();
        GalateaDelegateConfig valid = fixture.Load();
        JsonObject missing = fixture.Parse();
        missing["sidecar"]!.AsObject().Remove("codexHome");
        Assert.Throws<InvalidDataException>(() => fixture.Load(missing));
        string link = Path.Combine(fixture.Root, "home-link");
        Directory.CreateSymbolicLink(link, fixture.Root);
        foreach (string invalid in new[] { "", "relative", "~/.codex", fixture.Executable,
                     Path.Combine(fixture.Root, "absent"), link, Path.Combine(link, ".") }) {
            JsonObject root = fixture.Parse();
            root["sidecar"]!["codexHome"] = invalid;
            Assert.Throws<InvalidDataException>(() => fixture.Load(root));
            Assert.Throws<InvalidDataException>(() => GalateaDelegateConfigReader.Validate(
                valid with { Sidecar = valid.Sidecar with { CodexHome = invalid } }));
        }
        using JsonDocument template = JsonDocument.Parse(GalateaDelegateConfigReader.CreatePlaceholderTemplateUtf8());
        Assert.Equal(5, template.RootElement.GetProperty("v").GetInt32());
        Assert.Contains("EXISTING_CANONICAL_CODEX_HOME", template.RootElement.GetProperty("sidecar").GetProperty("codexHome").GetString());
    }

    [Fact]
    public void ValidClosedV5LoadsCanonicalExactCodexRouteWithoutImplicitConfig() {
        using var fixture = new Fixture();

        GalateaDelegateConfig config = fixture.Load();

        Assert.Single(config.Routes);
        Assert.Equal("Codex", config.CodexRoute.Recipient);
        Assert.Equal("codex-app-server", config.CodexRoute.Kind);
        Assert.Equal([fixture.Root], config.AllowedRoots);
        Assert.Null(config.CodexRoute.CodexConfig);
        Assert.Equal(1_048_576, config.Sidecar.MaximumFrameUtf8Bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LegacyVersionsAreRejectedWithoutCompatibilityFallback(int version) {
        using var fixture = new Fixture();
        string legacy = fixture.ValidJson.Replace(
            "\"v\": 5,",
            $"\"v\": {version},",
            StringComparison.Ordinal
        );

        Assert.Throws<InvalidDataException>(() => fixture.Load(legacy));
    }

    [Fact]
    public void RetiredTurnTimeoutIsRejectedAsUnknownWithoutCompatibilityFallback() {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        root["sidecar"]!.AsObject()["turnTimeoutMs"] = 1_200_000;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => fixture.Load(root)
        );
        Assert.Contains(
            "sidecar contains unknown property 'turnTimeoutMs'",
            failure.Message,
            StringComparison.Ordinal
        );
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
                "\"v\": 5,",
                "\"v\": 5,\n\"V\": 4,",
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
    [InlineData("mode")]
    [InlineData("localCommandNetwork")]
    [InlineData("tools")]
    public void RetiredRoutePolicyFieldsAreRejected(string property) {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        root["routes"]!.AsArray()[0]!.AsObject()[property] = "retired";
        Assert.Throws<InvalidDataException>(() => fixture.Load(root));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"sandbox_mode\":null}")]
    [InlineData("{\"features\":{\"x\":true,\"X\":false}}")]
    [InlineData("{\"items\":[null]}")]
    [InlineData("{\"x\":1,\"x\":2}")]
    public void NativeConfigRejectsNonObjectsNullAndDuplicateKeys(string value) {
        using var fixture = new Fixture();
        string json = fixture.ValidJson.Replace(
            "\"kind\": \"codex-app-server\",",
            "\"kind\": \"codex-app-server\", \"codexConfig\": " + value + ",",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => fixture.Load(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyConfigDoesNotOverrideCodexDefaults(bool empty) {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        if (empty) { root["routes"]!.AsArray()[0]!.AsObject()["codexConfig"] = new JsonObject(); }
        await using var client = new GalateaCodexDurableSidecarClient(fixture.Load(root));
        string? parentCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        ProcessStartInfo start = client.CreateStartInfoForTest();
        Assert.False(start.Environment.ContainsKey("GALATEA_CODEX_CONFIG"));
        Assert.Equal(new[] { "app-server", "--listen", "stdio://" },
            JsonSerializer.Deserialize<string[]>(start.Environment["CODEX_BRIDGE_CODEX_ARGS"]!));
        Assert.False(start.Environment.ContainsKey("GALATEA_CODEX_MODE"));
        Assert.False(start.Environment.ContainsKey("GALATEA_CODEX_LOCAL_COMMAND_NETWORK"));
        Assert.False(start.Environment.ContainsKey("GALATEA_CODEX_WEB_SEARCH"));
        Assert.False(start.Environment.ContainsKey("GALATEA_CODEX_IMAGE_GENERATION"));
        Assert.False(start.Environment.ContainsKey("GALATEA_CODEX_VIEW_IMAGE"));
        Assert.Equal(Environment.GetEnvironmentVariable("HOME"), (start.Environment.TryGetValue("HOME", out string? home) ? home : null));
        Assert.Equal(fixture.Root, (start.Environment.TryGetValue("CODEX_HOME", out string? codexHome) ? codexHome : null));
        Assert.Equal(parentCodexHome, Environment.GetEnvironmentVariable("CODEX_HOME"));
        foreach (string key in new[] { "CODEX_SQLITE_HOME", "HTTPS_PROXY", "DEEPSEEK_API_KEY" }) {
            Assert.Equal(Environment.GetEnvironmentVariable(key), (start.Environment.TryGetValue(key, out string? value) ? value : null));
        }
        Assert.Equal("/", start.WorkingDirectory);
        Assert.False(start.Environment.ContainsKey("CODEX_THREAD_ID"));
        Assert.False(start.Environment.ContainsKey("CODEX_PERMISSION_PROFILE"));
    }

    [Fact]
    public async Task ExplicitNativeConfigSurvivesDocumentDisposalAndProgrammaticMutation() {
        using var fixture = new Fixture();
        JsonObject root = fixture.Parse();
        root["routes"]!.AsArray()[0]!.AsObject()["codexConfig"] = JsonNode.Parse("""
            {"sandbox_mode":"danger-full-access","approval_policy":"never",
             "features":{"apps":false},"future_setting":["first",2,true]}
            """);
        GalateaDelegateConfig loaded = fixture.Load(root);
        var mutable = loaded.CodexRoute.CodexConfig!.ToDictionary(static entry => entry.Key, static entry => entry.Value);
        string expected = JsonSerializer.Serialize(mutable);
        await using var client = new GalateaCodexDurableSidecarClient(loaded with {
            Routes = [loaded.CodexRoute with { CodexConfig = mutable }]
        });
        mutable.Clear();
        Assert.Equal(expected, client.CreateStartInfoForTest().Environment["GALATEA_CODEX_CONFIG"]);
    }

    [Fact]
    public void ProgrammaticNativeConfigReceivesTheSameShapeValidation() {
        using var fixture = new Fixture();
        GalateaDelegateConfig loaded = fixture.Load();
        foreach (string json in new[] { "{\"value\":null}", "{\"value\":1,\"VALUE\":2}" }) {
            var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            Assert.Throws<InvalidDataException>(() => new GalateaCodexDurableSidecarClient(loaded with {
                Routes = [loaded.CodexRoute with { CodexConfig = config }]
            }));
        }
    }

    [Theory]
    [InlineData("rpcTimeoutMs", 99)]
    [InlineData("rpcTimeoutMs", 300001)]
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
    public void RemovedGlobalCwdIsRejected() {
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
        Assert.Equal("/", startInfo.WorkingDirectory);
        Assert.False(startInfo.Environment.ContainsKey("CODEX_BRIDGE_DEFAULT_CWD"));
    }

    [Fact]
    public void BootstrapCreatesDelegatePlaceholderWithoutOverwritingExistingFiles() {
        using var fixture = new Fixture(writeDelegates: false);
        string configPath = Path.Combine(fixture.Root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                GalateaConfigTemplateFactory.CreateRootFile(),
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
        Assert.DoesNotContain(
            "turnTimeoutMs",
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
          "v": 5,
          "sidecar": {
            "nodeCommand": {{JsonSerializer.Serialize(Executable)}},
            "entryPoint": {{JsonSerializer.Serialize(EntryPoint)}},
            "codexCommand": {{JsonSerializer.Serialize(Executable)}},
            "codexHome": {{JsonSerializer.Serialize(Root)}},
            "rpcTimeoutMs": 1000,
            "shutdownGraceMs": 100,
            "maximumFrameUtf8Bytes": 1048576
          },
          "allowedRoots": [{{JsonSerializer.Serialize(Root)}}],
          "routes": [
            {
              "recipient": "Codex",
              "kind": "codex-app-server",
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

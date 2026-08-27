namespace Atelia.Galatea.Server.Tests;

internal sealed class GalateaSidecarProcessFixture : IDisposable {
    private readonly string _scriptTemplate;

    internal GalateaSidecarProcessFixture(string script) {
        Root = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-sidecar-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(Root);
        ScriptPath = Path.Combine(Root, "fake-sidecar.sh");
        EnvironmentPath = Path.Combine(Root, "environment.txt");
        InputPath = Path.Combine(Root, "input.jsonl");
        CountPath = Path.Combine(Root, "count.txt");
        _scriptTemplate = "#!/usr/bin/dash\nset -eu\n" + script + "\n";
        RewritePlaceholders();
    }

    internal string Root { get; }
    private string ScriptPath { get; }
    internal string EnvironmentPath { get; }
    internal string InputPath { get; }
    internal string CountPath { get; }

    internal static string ShellQuote(string fixturePath) =>
        $"'__{fixturePath}_PATH__'";

    internal void RewritePlaceholders() {
        string script = _scriptTemplate
            .Replace(
                "__ENV_PATH__",
                EnvironmentPath,
                StringComparison.Ordinal
            )
            .Replace(
                "__INPUT_PATH__",
                InputPath,
                StringComparison.Ordinal
            )
            .Replace(
                "__COUNT_PATH__",
                CountPath,
                StringComparison.Ordinal
            );
        File.WriteAllText(ScriptPath, script);
    }

    internal GalateaCodexSidecarClient CreateV1Client(
        int rpcTimeoutMs = 2_000,
        int maximumFrameUtf8Bytes = 65_536,
        int maximumBodyUtf8Bytes = 8_000,
        GalateaSidecarProcessTestHooks? processHooks = null
    ) => new(CreateConfig(
        rpcTimeoutMs,
        maximumFrameUtf8Bytes,
        maximumBodyUtf8Bytes
    ), processHooks);

    internal GalateaCodexDurableSidecarClient CreateV2Client(
        int rpcTimeoutMs = 2_000,
        int maximumFrameUtf8Bytes = 65_536,
        int maximumBodyUtf8Bytes = 8_000,
        GalateaSidecarProcessTestHooks? processHooks = null
    ) => new(CreateConfig(
        rpcTimeoutMs,
        maximumFrameUtf8Bytes,
        maximumBodyUtf8Bytes
    ), processHooks);

    public void Dispose() {
        if (Directory.Exists(Root)) {
            Directory.Delete(Root, recursive: true);
        }
    }

    private GalateaDelegateConfig CreateConfig(
        int rpcTimeoutMs,
        int maximumFrameUtf8Bytes,
        int maximumBodyUtf8Bytes
    ) => new(
        new GalateaDelegateSidecarConfig(
            "/usr/bin/dash",
            ScriptPath,
            "/usr/bin/true",
            RpcTimeoutMs: rpcTimeoutMs,
            TurnTimeoutMs: 2_000,
            ShutdownGraceMs: 100,
            MaximumFrameUtf8Bytes: maximumFrameUtf8Bytes
        ),
        [Root],
        [new GalateaDelegateRouteConfig(
            "Codex",
            "codex-app-server",
            Root,
            GalateaDelegateMode.Work,
            LocalCommandNetwork: false,
            Tools: new GalateaDelegateToolConfig(
                GalateaDelegateWebSearchMode.Live,
                ImageGeneration: true,
                ViewImage: true
            ),
            MaximumQueuedMails: 16,
            MaximumTaskUtf8Bytes: maximumBodyUtf8Bytes,
            MaximumReplyUtf8Bytes: maximumBodyUtf8Bytes,
            MaximumInboxReplies: 16,
            MaximumInboxUtf8Bytes: Math.Max(
                GalateaPlayerObservationEnvelope.MaximumFailureUtf8Bytes,
                Math.Max(
                    maximumFrameUtf8Bytes,
                    maximumBodyUtf8Bytes
                )
            )
        )]
    );
}

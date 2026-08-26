using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegateSidecarTests {
    [Fact]
    public async Task ProcessIsLazyAndDisposeWithoutDispatchDoesNotStartIt() {
        using var fixture = new Fixture("exit 99");
        var client = fixture.CreateClient();

        Assert.False(client.HasStartedProcessForTest);
        await client.DisposeAsync();
        Assert.False(client.HasStartedProcessForTest);
    }

    [Fact]
    public async Task ReadyAcceptedAndCompletedPreserveUnicodeAndRouteEnvironment() {
        using var fixture = new Fixture(
            $$"""
            printf '%s\n' '{"v":1,"type":"ready"}'
            printf '%s|%s|%s|%s|%s|%s\n' \
              "$CODEX_BRIDGE_ALLOWED_ROOTS" \
              "$CODEX_BRIDGE_DEFAULT_CWD" \
              "$CODEX_BRIDGE_CODEX_COMMAND" \
              "$GALATEA_CODEX_MODE" \
              "$GALATEA_CODEX_NETWORK" \
              "$GALATEA_CODEX_MAX_FINAL_BYTES" > {{ShellQuote(fixturePath: "ENV")}}
            head -c 262144 /dev/zero >&2
            while IFS= read -r line; do
              printf '%s\n' "$line" > {{ShellQuote(fixturePath: "INPUT")}}
              request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
              dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
              printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
              printf '{"v":1,"type":"completed","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1","final":"答复 🌙 ```"}\n' "$dispatch_id"
            done
            """
        );
        // Replace symbolic fixture paths after the temp root exists.
        fixture.RewritePlaceholders();
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateAcceptedHandle accepted = await client.StartAsync(
            new GalateaDelegateDispatchRequest(
                "dispatch-1",
                ThreadId: null,
                "任务正文：你好 🌙"
            ),
            CancellationToken.None
        );
        GalateaDelegateTerminal.Completed completed = Assert.IsType<
            GalateaDelegateTerminal.Completed>(await accepted.Completion);

        Assert.Equal("dispatch-1", accepted.DispatchId);
        Assert.Equal("thread-1", accepted.ThreadId);
        Assert.Equal("turn-1", accepted.TurnId);
        Assert.Equal("答复 🌙 ```", completed.Final);
        using JsonDocument input = JsonDocument.Parse(
            File.ReadAllText(fixture.InputPath)
        );
        Assert.Equal(
            "任务正文：你好 🌙",
            input.RootElement.GetProperty("task").GetString()
        );
        Assert.False(input.RootElement.TryGetProperty("cwd", out _));
        Assert.False(input.RootElement.TryGetProperty("mode", out _));
        Assert.False(input.RootElement.TryGetProperty("network", out _));
        string environment = File.ReadAllText(fixture.EnvironmentPath).Trim();
        Assert.Contains(fixture.Root, environment, StringComparison.Ordinal);
        Assert.Contains("|work|false|8000", environment,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrelatedFailureBeforeAcceptThrowsStableStartFailure() {
        using var fixture = new Fixture(CommonPrefix + "\n" + """
        printf '{"v":1,"type":"failed","requestId":"%s","dispatchId":"%s","stage":"start","code":"START_OUTCOME_UNKNOWN"}\n' "$request_id" "$dispatch_id"
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateStartException failure = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-preaccept"),
                CancellationToken.None
            ));

        Assert.Equal("start", failure.Stage);
        Assert.Equal("START_OUTCOME_UNKNOWN", failure.Code);
    }

    [Fact]
    public async Task CorrelatedFailureAfterAcceptCompletesTerminalFailure() {
        using var fixture = new Fixture(CommonPrefix + "\n" + """
        printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
        printf '{"v":1,"type":"failed","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1","stage":"turn","code":"TURN_FAILED"}\n' "$request_id" "$dispatch_id"
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateAcceptedHandle accepted = await client.StartAsync(
            Request("dispatch-terminal-failure"),
            CancellationToken.None
        );
        GalateaDelegateTerminal.Failed failure = Assert.IsType<
            GalateaDelegateTerminal.Failed>(await accepted.Completion);

        Assert.Equal("turn", failure.Stage);
        Assert.Equal("TURN_FAILED", failure.Code);
    }

    [Fact]
    public async Task ConcurrentDuplicateDispatchCoalescesWithoutSecondFrame() {
        using var fixture = new Fixture(
            $$"""
            printf '%s\n' '{"v":1,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" >> {{ShellQuote(fixturePath: "INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
            sleep 1
            printf '{"v":1,"type":"completed","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1","final":"one"}\n' "$dispatch_id"
            while IFS= read -r extra; do
              printf '%s\n' "$extra" >> {{ShellQuote(fixturePath: "INPUT")}}
            done
            """
        );
        fixture.RewritePlaceholders();
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        Task<GalateaDelegateAcceptedHandle> first = client.StartAsync(
            Request("dispatch-coalesce"),
            CancellationToken.None
        );
        Task<GalateaDelegateAcceptedHandle> second = client.StartAsync(
            Request("dispatch-coalesce"),
            CancellationToken.None
        );
        GalateaDelegateAcceptedHandle[] accepted = await Task.WhenAll(
            first,
            second
        );
        Assert.Same(accepted[0], accepted[1]);
        Assert.IsType<GalateaDelegateTerminal.Completed>(
            await accepted[0].Completion
        );
        Assert.Single(File.ReadAllLines(fixture.InputPath));
    }

    [Fact]
    public async Task RequestLevelProtocolRejectionDoesNotKillGeneration() {
        using var fixture = new Fixture("""
        printf '%s\n' '{"v":1,"type":"ready"}'
        IFS= read -r first
        first_request=$(printf '%s' "$first" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
        printf '{"v":1,"type":"failed","requestId":"%s","stage":"protocol","code":"DUPLICATE_DISPATCH_ID"}\n' "$first_request"
        IFS= read -r second
        second_request=$(printf '%s' "$second" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
        second_dispatch=$(printf '%s' "$second" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
        printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-2"}\n' "$second_request" "$second_dispatch"
        printf '{"v":1,"type":"completed","dispatchId":"%s","threadId":"thread-1","turnId":"turn-2","final":"still alive"}\n' "$second_dispatch"
        while IFS= read -r ignored; do :; done
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateStartException rejected = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-terminal-replay"),
                CancellationToken.None
            ));
        Assert.Equal("protocol", rejected.Stage);
        Assert.Equal("DUPLICATE_DISPATCH_ID", rejected.Code);

        GalateaDelegateAcceptedHandle accepted = await client.StartAsync(
            Request("dispatch-next"),
            CancellationToken.None
        );
        GalateaDelegateTerminal.Completed completed = Assert.IsType<
            GalateaDelegateTerminal.Completed>(await accepted.Completion);
        Assert.Equal("still alive", completed.Final);
        Assert.Equal(1, client.GenerationCountForTest);
    }

    [Fact]
    public async Task ExitAfterAcceptMapsUnknownOutcomeToTerminalFailure() {
        using var fixture = new Fixture(CommonPrefix + "\n" + """
        printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
        exit 7
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateAcceptedHandle accepted = await client.StartAsync(
            Request("dispatch-exit"),
            CancellationToken.None
        );
        GalateaDelegateTerminal.Failed failure = Assert.IsType<
            GalateaDelegateTerminal.Failed>(await accepted.Completion);

        Assert.Equal("protocol", failure.Stage);
        Assert.Equal("SIDECAR_EXITED", failure.Code);
    }

    [Fact]
    public async Task MalformedGenerationFailsAndNextRequestStartsFreshGeneration() {
        using var fixture = new Fixture(
            $$"""
            count=0
            if [ -f {{ShellQuote(fixturePath: "COUNT")}} ]; then
              count=$(cat {{ShellQuote(fixturePath: "COUNT")}})
            fi
            count=$((count + 1))
            printf '%s' "$count" > {{ShellQuote(fixturePath: "COUNT")}}
            printf '%s\n' '{"v":1,"type":"ready"}'
            IFS= read -r line
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            if [ "$count" -eq 1 ]; then
              printf '%s\n' '{"v":1,"type":"accepted","V":1}'
              sleep 5
            else
              printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-2","turnId":"turn-2"}\n' "$request_id" "$dispatch_id"
              printf '{"v":1,"type":"completed","dispatchId":"%s","threadId":"thread-2","turnId":"turn-2","final":"fresh"}\n' "$dispatch_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        fixture.RewritePlaceholders();
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateStartException first = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-bad"),
                CancellationToken.None
            ));
        Assert.Equal("SIDECAR_PROTOCOL_ERROR", first.Code);

        GalateaDelegateAcceptedHandle second = await client.StartAsync(
            Request("dispatch-good"),
            CancellationToken.None
        );
        GalateaDelegateTerminal.Completed completed = Assert.IsType<
            GalateaDelegateTerminal.Completed>(await second.Completion);
        Assert.Equal("fresh", completed.Final);
        Assert.Equal("2", File.ReadAllText(fixture.CountPath));
    }

    [Fact]
    public async Task OversizeStdoutFrameIsProtocolFatal() {
        using var fixture = new Fixture("""
        printf '%s\n' '{"v":1,"type":"ready"}'
        head -c 70000 /dev/zero | tr '\000' x
        printf '\n'
        sleep 5
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient();

        GalateaDelegateStartException failure = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-oversize"),
                CancellationToken.None
            ));

        Assert.Equal("SIDECAR_FRAME_TOO_LARGE", failure.Code);
    }

    [Fact]
    public async Task DisposeClosesThenKillsBoundedProcessTreeAndFailsTerminal() {
        using var fixture = new Fixture(CommonPrefix + "\n" + """
        printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
        sleep 30
        """);
        GalateaCodexSidecarClient client = fixture.CreateClient();
        GalateaDelegateAcceptedHandle accepted = await client.StartAsync(
            Request("dispatch-dispose"),
            CancellationToken.None
        );
        var watch = Stopwatch.StartNew();

        await client.DisposeAsync();

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        GalateaDelegateTerminal.Failed failure = Assert.IsType<
            GalateaDelegateTerminal.Failed>(await accepted.Completion);
        Assert.Equal("SIDECAR_DISPOSED", failure.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.StartAsync(Request("dispatch-after-dispose"),
                CancellationToken.None));
    }

    private const string CommonPrefix = """
    printf '%s\n' '{"v":1,"type":"ready"}'
    IFS= read -r line
    request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
    dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
    """;

    private static GalateaDelegateDispatchRequest Request(string dispatchId) =>
        new(dispatchId, ThreadId: null, Body: "do work");

    private static string ShellQuote(string fixturePath) =>
        $"'__{fixturePath}_PATH__'";

    private sealed class Fixture : IDisposable {
        private readonly string _scriptTemplate;

        internal Fixture(string script) {
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

        internal void RewritePlaceholders() {
            string script = _scriptTemplate
                .Replace("__ENV_PATH__", EnvironmentPath,
                    StringComparison.Ordinal)
                .Replace("__INPUT_PATH__", InputPath,
                    StringComparison.Ordinal)
                .Replace("__COUNT_PATH__", CountPath,
                    StringComparison.Ordinal);
            File.WriteAllText(ScriptPath, script);
        }

        internal GalateaCodexSidecarClient CreateClient() {
            var config = new GalateaDelegateConfig(
                new GalateaDelegateSidecarConfig(
                    "/usr/bin/dash",
                    ScriptPath,
                    "/usr/bin/true",
                    RpcTimeoutMs: 2_000,
                    TurnTimeoutMs: 2_000,
                    ShutdownGraceMs: 100,
                    MaximumFrameUtf8Bytes: 65_536
                ),
                [Root],
                [new GalateaDelegateRouteConfig(
                    "Codex",
                    "codex-app-server",
                    Root,
                    GalateaDelegateMode.Work,
                    Network: false,
                    MaximumQueuedMails: 16,
                    MaximumTaskUtf8Bytes: 8_000,
                    MaximumReplyUtf8Bytes: 8_000,
                    MaximumInboxReplies: 16,
                    MaximumInboxUtf8Bytes: 65_536
                )]
            );
            return new GalateaCodexSidecarClient(config);
        }

        public void Dispose() {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

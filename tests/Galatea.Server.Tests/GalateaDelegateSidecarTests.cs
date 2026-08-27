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
    public async Task EnvironmentIsClearedAndPinnedAgainstAmbientOverrides() {
        using var fixture = new Fixture("exit 99");
        await using GalateaCodexSidecarClient client = fixture.CreateClient();
        var environment = new Dictionary<string, string?> {
            ["PATH"] = "/safe/path",
            ["CODEX_BRIDGE_CODEX_ARGS"] = "[\"malicious\"]",
            ["CODEX_BRIDGE_HTTP_HOST"] = "0.0.0.0",
            ["CODEX_BRIDGE_UNKNOWN_FUTURE_FIELD"] = "ambient",
            ["GALATEA_CODEX_MODE"] = "research",
            ["GALATEA_CODEX_INTERRUPT_GRACE_MS"] = "invalid",
            ["GALATEA_CODEX_UNKNOWN_FUTURE_FIELD"] = "ambient"
        };

        client.ConfigureSidecarEnvironmentForTest(environment);

        Assert.Equal("/safe/path", environment["PATH"]);
        Assert.DoesNotContain("CODEX_BRIDGE_UNKNOWN_FUTURE_FIELD",
            environment.Keys);
        Assert.DoesNotContain("GALATEA_CODEX_UNKNOWN_FUTURE_FIELD",
            environment.Keys);
        Assert.Equal("stdio", environment["CODEX_BRIDGE_TRANSPORT"]);
        Assert.Equal("127.0.0.1",
            environment["CODEX_BRIDGE_HTTP_HOST"]);
        Assert.Equal(
            "[\"app-server\",\"--listen\",\"stdio://\",\"-c\","
            + "\"mcp_servers={}\",\"-c\",\"features.apps=false\"]",
            environment["CODEX_BRIDGE_CODEX_ARGS"]
        );
        Assert.Equal("work", environment["GALATEA_CODEX_MODE"]);
        Assert.Equal("false", environment["GALATEA_CODEX_NETWORK"]);
        Assert.Equal("65536",
            environment["GALATEA_CODEX_MAX_INPUT_FRAME_BYTES"]);
        Assert.Equal("65536",
            environment["GALATEA_CODEX_MAX_OUTPUT_FRAME_BYTES"]);
        Assert.Equal("8000",
            environment["GALATEA_CODEX_MAX_TASK_BYTES"]);
        Assert.Equal("8000",
            environment["GALATEA_CODEX_MAX_FINAL_BYTES"]);
        Assert.Equal("4096",
            environment["GALATEA_CODEX_MAX_DISPATCH_TOMBSTONES"]);
        Assert.Equal("10000",
            environment["GALATEA_CODEX_OUTPUT_WRITE_TIMEOUT_MS"]);
    }

    [Fact]
    public async Task MaximumRpcTimeoutKeepsNodeOutputDeadlineInWireRange() {
        using var fixture = new Fixture("exit 99");
        await using GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 300_000
        );

        ProcessStartInfo startInfo = client.CreateStartInfoForTest();

        Assert.Equal(
            "300000",
            startInfo.Environment["CODEX_BRIDGE_RPC_TIMEOUT_MS"]
        );
        Assert.Equal(
            "10000",
            startInfo.Environment[
                "GALATEA_CODEX_OUTPUT_WRITE_TIMEOUT_MS"
            ]
        );
    }

    [Theory]
    [InlineData(100, 5_200)]
    [InlineData(30_000, 65_000)]
    [InlineData(300_000, 605_000)]
    public void ReadyDeadlineComposesTwoRpcsAndStartupMargin(
        int rpcTimeoutMs,
        int expectedReadyMs
    ) {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedReadyMs),
            GalateaCodexSidecarClient.ComputeReadyDeadline(rpcTimeoutMs)
        );
    }

    [Fact]
    public async Task ReadyAfterOneRpcButBeforeAggregateDeadlineIsAccepted() {
        using var fixture = new Fixture("""
        sleep 0.3
        printf '%s\n' '{"v":1,"type":"ready"}'
        IFS= read -r line
        request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
        dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
        printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
        printf '{"v":1,"type":"completed","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1","final":"cold ready"}\n' "$dispatch_id"
        while IFS= read -r ignored; do :; done
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 200
        );
        var watch = Stopwatch.StartNew();

        GalateaDelegateAcceptedHandle accepted = await client.StartAsync(
            Request("dispatch-cold-ready"),
            CancellationToken.None
        );
        GalateaDelegateTerminal.Completed completed = Assert.IsType<
            GalateaDelegateTerminal.Completed>(await accepted.Completion);

        watch.Stop();
        Assert.Equal("cold ready", completed.Final);
        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(250));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task NeverReadyFailsAtAggregateDeadlineAndReapsChild() {
        using var fixture = new Fixture(
            $$"""
            printf '%s' "$$" > {{ShellQuote(fixturePath: "COUNT")}}
            sleep 30
            """
        );
        fixture.RewritePlaceholders();
        await using GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 100
        );
        var watch = Stopwatch.StartNew();

        GalateaDelegateStartException failure = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-never-ready"),
                CancellationToken.None
            ));

        watch.Stop();
        Assert.Equal("SIDECAR_READY_TIMEOUT", failure.Code);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(4.5));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
        int pid = int.Parse(
            File.ReadAllText(fixture.CountPath),
            System.Globalization.CultureInfo.InvariantCulture
        );
        Assert.False(IsProcessAlive(pid));
    }

    [Fact]
    public async Task ReadyTimeoutSurfacesUnconfirmedReapInsteadOfPlainTimeout() {
        using var fixture = new Fixture("sleep 30");
        var hooks = new GalateaSidecarProcessTestHooks(
            WaitForExitBoundedAsync: (_, _) => Task.FromResult(false)
        );
        GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 100,
            processHooks: hooks
        );

        GalateaDelegateStartException failure = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-ready-reap-unconfirmed"),
                CancellationToken.None
            ));

        Assert.Equal("shutdown", failure.Stage);
        Assert.Equal("SIDECAR_REAP_UNCONFIRMED", failure.Code);
        Assert.Equal(1, client.GenerationCountForTest);
        GalateaDelegateStartException dispose = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(async () =>
                await client.DisposeAsync());
        Assert.Equal("SIDECAR_REAP_UNCONFIRMED", dispose.Code);
    }

    [Fact]
    public async Task SidecarThatNeverReadsStdinFailsWithinWriteDeadline() {
        using var fixture = new Fixture("""
        printf '%s\n' '{"v":1,"type":"ready"}'
        sleep 30
        """);
        await using GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 200,
            maximumFrameUtf8Bytes: 1_048_576,
            maximumBodyUtf8Bytes: 170_000
        );
        var watch = Stopwatch.StartNew();

        GalateaDelegateStartException failure = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                new GalateaDelegateDispatchRequest(
                    "dispatch-blocked-write",
                    ThreadId: null,
                    new string('x', 170_000)
                ),
                CancellationToken.None
            ));

        watch.Stop();
        Assert.Equal("SIDECAR_WRITE_OUTCOME_UNKNOWN", failure.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DisposeUnblocksAnInFlightBlockedWrite() {
        using var fixture = new Fixture("""
        printf '%s\n' '{"v":1,"type":"ready"}'
        sleep 30
        """);
        GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 5_000,
            maximumFrameUtf8Bytes: 1_048_576,
            maximumBodyUtf8Bytes: 170_000
        );
        Task<GalateaDelegateAcceptedHandle> start = client.StartAsync(
            new GalateaDelegateDispatchRequest(
                "dispatch-dispose-blocked-write",
                ThreadId: null,
                new string('x', 170_000)
            ),
            CancellationToken.None
        );
        await Task.Delay(100);
        var watch = Stopwatch.StartNew();

        await client.DisposeAsync();
        await Assert.ThrowsAsync<GalateaDelegateStartException>(async () =>
            await start);

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AcceptanceUnknownTombstonePreventsCrossGenerationReplay() {
        using var fixture = new Fixture(
            $$"""
            printf '%s\n' '{"v":1,"type":"ready"}'
            if IFS= read -r line; then
              printf '%s\n' "$line" >> {{ShellQuote(fixturePath: "INPUT")}}
            fi
            sleep 30
            """
        );
        fixture.RewritePlaceholders();
        await using GalateaCodexSidecarClient client = fixture.CreateClient(
            rpcTimeoutMs: 200
        );

        GalateaDelegateStartException first = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-unknown"),
                CancellationToken.None
            ));
        Assert.Equal("SIDECAR_ACCEPTANCE_OUTCOME_UNKNOWN", first.Code);

        GalateaDelegateStartException replay = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-unknown"),
                CancellationToken.None
            ));
        Assert.Equal("DUPLICATE_DISPATCH_ID", replay.Code);
        Assert.Equal(2, client.GenerationCountForTest);
        Assert.Single(File.ReadAllLines(fixture.InputPath));
    }

    [Fact]
    public async Task AttachedCancellationDoesNotFaultOwnerOrGeneration() {
        using var fixture = new Fixture(
            $$"""
            printf '%s\n' '{"v":1,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{ShellQuote(fixturePath: "INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            sleep 1
            printf '{"v":1,"type":"accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1"}\n' "$request_id" "$dispatch_id"
            printf '{"v":1,"type":"completed","dispatchId":"%s","threadId":"thread-1","turnId":"turn-1","final":"owner survived"}\n' "$dispatch_id"
            while IFS= read -r ignored; do :; done
            """
        );
        fixture.RewritePlaceholders();
        await using GalateaCodexSidecarClient client = fixture.CreateClient();
        Task<GalateaDelegateAcceptedHandle> owner = client.StartAsync(
            Request("dispatch-attached-cancel"),
            CancellationToken.None
        );
        await WaitForFileAsync(fixture.InputPath);
        using var cancellation = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.StartAsync(
                Request("dispatch-attached-cancel"),
                cancellation.Token
            ));

        GalateaDelegateAcceptedHandle accepted = await owner;
        GalateaDelegateTerminal.Completed completed = Assert.IsType<
            GalateaDelegateTerminal.Completed>(await accepted.Completion);
        Assert.Equal("owner survived", completed.Final);
        Assert.Equal(1, client.GenerationCountForTest);
    }

    [Theory]
    [InlineData(2048, 2049, false)]
    [InlineData(65536, 65537, false)]
    [InlineData(2048, 2048, true)]
    public async Task StdoutFrameCapAppliesBeforeParsingAcrossChunkShapes(
        int maximumFrameUtf8Bytes,
        int payloadBytes,
        bool crlf
    ) {
        string terminator = crlf ? "\\r\\n" : "\\n";
        using var fixture = new Fixture($$"""
        printf '%s\n' '{"v":1,"type":"ready"}'
        head -c {{payloadBytes}} /dev/zero | tr '\000' x
        printf '{{terminator}}'
        sleep 30
        """);
        int maximumBody = Math.Max(
            1,
            (maximumFrameUtf8Bytes - 1024) / 6
        );
        await using GalateaCodexSidecarClient client = fixture.CreateClient(
            maximumFrameUtf8Bytes: maximumFrameUtf8Bytes,
            maximumBodyUtf8Bytes: maximumBody
        );

        GalateaDelegateStartException failure = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-oversize-shape"),
                CancellationToken.None
            ));

        Assert.Equal("SIDECAR_FRAME_TOO_LARGE", failure.Code);
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
        Assert.Equal(2, client.GenerationCountForTest);
    }

    [Fact]
    public async Task ReapUnconfirmedFaultsBarrierAndPermanentlyBlocksRestart() {
        using var fixture = new Fixture(
            $$"""
            printf '%s\n' "$$" >> {{ShellQuote(fixturePath: "COUNT")}}
            printf '%s\n' '{"v":1,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' '{"v":1,"type":"accepted","V":1}'
            sleep 30
            """
        );
        fixture.RewritePlaceholders();
        int waitCalls = 0;
        var hooks = new GalateaSidecarProcessTestHooks(
            WaitForExitBoundedAsync: (_, _) => {
                Interlocked.Increment(ref waitCalls);
                return Task.FromResult(false);
            }
        );
        GalateaCodexSidecarClient client = fixture.CreateClient(
            processHooks: hooks
        );

        GalateaDelegateStartException first = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-unconfirmed-reap"),
                CancellationToken.None
            ));
        Assert.Equal("SIDECAR_PROTOCOL_ERROR", first.Code);

        GalateaDelegateStartException restart = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(() => client.StartAsync(
                Request("dispatch-must-not-restart"),
                CancellationToken.None
            ));
        Assert.Equal("shutdown", restart.Stage);
        Assert.Equal("SIDECAR_REAP_UNCONFIRMED", restart.Code);
        Assert.Equal(1, client.GenerationCountForTest);
        Assert.Single(File.ReadAllLines(fixture.CountPath));
        Assert.True(Volatile.Read(ref waitCalls) >= 1);

        GalateaDelegateStartException dispose = await Assert.ThrowsAsync<
            GalateaDelegateStartException>(async () =>
                await client.DisposeAsync());
        Assert.Equal("shutdown", dispose.Stage);
        Assert.Equal("SIDECAR_REAP_UNCONFIRMED", dispose.Code);
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

    private static async Task WaitForFileAsync(string path) {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5)
        );
        while (!File.Exists(path)) {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static bool IsProcessAlive(int pid) {
        try {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) {
            return false;
        }
    }

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

        internal GalateaCodexSidecarClient CreateClient(
            int rpcTimeoutMs = 2_000,
            int maximumFrameUtf8Bytes = 65_536,
            int maximumBodyUtf8Bytes = 8_000,
            GalateaSidecarProcessTestHooks? processHooks = null
        ) {
            var config = new GalateaDelegateConfig(
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
                    Network: false,
                    MaximumQueuedMails: 16,
                    MaximumTaskUtf8Bytes: maximumBodyUtf8Bytes,
                    MaximumReplyUtf8Bytes: maximumBodyUtf8Bytes,
                    MaximumInboxReplies: 16,
                    MaximumInboxUtf8Bytes: Math.Max(
                        GalateaPlayerObservationEnvelope
                            .MaximumFailureUtf8Bytes,
                        Math.Max(
                            maximumFrameUtf8Bytes,
                            maximumBodyUtf8Bytes
                        )
                    )
                )]
            );
            return new GalateaCodexSidecarClient(config, processHooks);
        }

        public void Dispose() {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

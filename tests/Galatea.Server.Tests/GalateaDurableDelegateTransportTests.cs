using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDurableDelegateTransportTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExactV2FramesAndFiveInspectionOutcomesAreTyped() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":2,"type":"ready"}'
            count=0
            while IFS= read -r line; do
              count=$((count + 1))
              printf '%s\n' "$line" >> {{Q("INPUT")}}
              request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
              dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
              thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
              case "$count" in
                1)
                  binding_id=$(printf '%s' "$line" | sed -n 's/.*"bindingOperationId":"\([^"]*\)".*/\1/p')
                  printf '{"v":2,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-fixed"}\n' "$request_id" "$binding_id"
                  ;;
                2)
                  printf '{"v":2,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"%s","turnId":"turn-1"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                3)
                  printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                4)
                  printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"running","turnId":"turn-1"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                5)
                  printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"completed","turnId":"turn-1","final":"完成，包含中文。"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                6)
                  printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"failed","turnId":"turn-1","code":"TURN_FAILED"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                7)
                  printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"ambiguous","code":"DISPATCH_BODY_MISMATCH"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
              esac
            done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client();

        GalateaDelegateBindingEstablished binding =
            await client.EnsureBindingAsync(
                new("binding-1"),
                CancellationToken.None
            );
        Assert.Equal("thread-fixed", binding.ThreadId);

        GalateaDelegateTurnAccepted accepted = await client.StartTurnAsync(
            new("dispatch-1", binding.ThreadId, "exact\n你好"),
            CancellationToken.None
        );
        Assert.Equal("turn-1", accepted.TurnId);

        GalateaDelegateDispatchInspection[] inspections = [
            await client.InspectDispatchAsync(
                new("dispatch-missing", binding.ThreadId, "never sent"),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                new("dispatch-1", binding.ThreadId, "exact\n你好"),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                new("dispatch-1", binding.ThreadId, "exact\n你好"),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                new("dispatch-1", binding.ThreadId, "exact\n你好"),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                new("dispatch-1", binding.ThreadId, "exact\n你好"),
                CancellationToken.None
            )
        ];

        Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(
            inspections[0]
        );
        Assert.IsType<GalateaDelegateDispatchInspection.Running>(
            inspections[1]
        );
        var completed = Assert.IsType<
            GalateaDelegateDispatchInspection.Completed>(inspections[2]);
        Assert.Equal("完成，包含中文。", completed.Final);
        Assert.Equal(
            "TURN_FAILED",
            Assert.IsType<GalateaDelegateDispatchInspection.Failed>(
                inspections[3]
            ).Code
        );
        Assert.Equal(
            "DISPATCH_BODY_MISMATCH",
            Assert.IsType<GalateaDelegateDispatchInspection.Ambiguous>(
                inspections[4]
            ).Code
        );

        string[] lines = File.ReadAllLines(fixture.InputPath);
        Assert.Equal(7, lines.Length);
        Assert.Equal(
            [
                "ensure-binding",
                "start-turn",
                "inspect-dispatch",
                "inspect-dispatch",
                "inspect-dispatch",
                "inspect-dispatch",
                "inspect-dispatch"
            ],
            lines.Select(ReadType)
        );
        using JsonDocument start = JsonDocument.Parse(lines[1]);
        Assert.Equal("exact\n你好", start.RootElement.GetProperty("task")
            .GetString());
        Assert.Equal(
            ["dispatchId", "requestId", "task", "threadId", "type", "v"],
            start.RootElement.EnumerateObject()
                .Select(static value => value.Name)
                .Order(StringComparer.Ordinal)
        );
        Assert.Equal(1, client.GenerationCountForTest);
    }

    [Fact]
    public async Task ConcurrentResponsesAreCorrelatedByRequestId() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":2,"type":"ready"}'
            IFS= read -r first
            IFS= read -r second
            printf '%s\n' "$first" >> {{Q("INPUT")}}
            printf '%s\n' "$second" >> {{Q("INPUT")}}
            first_request=$(printf '%s' "$first" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            first_dispatch=$(printf '%s' "$first" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            second_request=$(printf '%s' "$second" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            second_dispatch=$(printf '%s' "$second" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            printf '{"v":2,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-fixed","turnId":"turn-%s"}\n' "$second_request" "$second_dispatch" "$second_dispatch"
            printf '{"v":2,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-fixed","turnId":"turn-%s"}\n' "$first_request" "$first_dispatch" "$first_dispatch"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client();

        Task<GalateaDelegateTurnAccepted> first = client.StartTurnAsync(
            new("dispatch-first", "thread-fixed", "first"),
            CancellationToken.None
        );
        Task<GalateaDelegateTurnAccepted> second = client.StartTurnAsync(
            new("dispatch-second", "thread-fixed", "second"),
            CancellationToken.None
        );

        Assert.Equal("turn-dispatch-first", (await first).TurnId);
        Assert.Equal("turn-dispatch-second", (await second).TurnId);
    }

    [Theory]
    [InlineData("wrong-identity")]
    [InlineData("extra-property")]
    [InlineData("wrong-case")]
    [InlineData("duplicate-property")]
    public async Task InvalidV2BusinessFrameIsGenerationFatal(string kind) {
        string response = kind switch {
            "wrong-identity" =>
                "printf '{\"v\":2,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"wrong\",\"threadId\":\"thread-1\"}\\n' \"$request_id\"",
            "extra-property" =>
                "printf '{\"v\":2,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\",\"extra\":1}\\n' \"$request_id\"",
            "wrong-case" =>
                "printf '{\"v\":2,\"type\":\"binding-established\",\"RequestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\"}\\n' \"$request_id\"",
            _ =>
                "printf '{\"v\":2,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\",\"threadId\":\"thread-2\"}\\n' \"$request_id\""
        };
        using var fixture = new GalateaSidecarProcessFixture(
            "printf '%s\\n' '{\"v\":2,\"type\":\"ready\"}'\n"
                + "IFS= read -r line\n"
                + "request_id=$(printf '%s' \"$line\" | sed -n 's/.*\"requestId\":\"\\([^\"]*\\)\".*/\\1/p')\n"
                + response
                + "\nsleep 30\n"
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client();

        GalateaDurableDelegateTransportException failure =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(() =>
                    client.EnsureBindingAsync(
                        new("binding-1"),
                        CancellationToken.None
                    ));

        Assert.Equal("BINDING_OUTCOME_UNKNOWN", failure.Code);
    }

    [Theory]
    [InlineData("start", "malformed", "START_OUTCOME_UNKNOWN")]
    [InlineData("start", "exit", "START_OUTCOME_UNKNOWN")]
    [InlineData("inspect", "malformed", "INSPECTION_UNAVAILABLE")]
    [InlineData("inspect", "exit", "INSPECTION_UNAVAILABLE")]
    public async Task GenerationFailureAfterWriteMapsToOperationPolicy(
        string operation,
        string failureKind,
        string expectedCode
    ) {
        string terminal = failureKind == "malformed"
            ? "printf '%s\\n' '{\"v\":2,\"type\":\"broken\",'"
            : "exit 7";
        using var fixture = new GalateaSidecarProcessFixture(
            "printf '%s\\n' '{\"v\":2,\"type\":\"ready\"}'\n"
                + "IFS= read -r line\n"
                + terminal
                + "\n"
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client();

        GalateaDurableDelegateTransportException failure = operation == "start"
            ? await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(() =>
                    client.StartTurnAsync(
                        new("dispatch-1", "thread-fixed", "task"),
                        CancellationToken.None
                    ))
            : await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(() =>
                    client.InspectDispatchAsync(
                        new("dispatch-1", "thread-fixed", "task"),
                        CancellationToken.None
                    ));

        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public async Task ConcurrentBindingDuplicateDoesNotReleaseOwnersClaim() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":2,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{Q("INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            binding_id=$(printf '%s' "$line" | sed -n 's/.*"bindingOperationId":"\([^"]*\)".*/\1/p')
            while [ ! -f {{Q("COUNT")}} ]; do sleep 0.01; done
            printf '{"v":2,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-1"}\n' "$request_id" "$binding_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client();
        Task<GalateaDelegateBindingEstablished> owner =
            client.EnsureBindingAsync(
                new("binding-active"),
                CancellationToken.None
            );
        await WaitForLinesAsync(fixture.InputPath, 1);

        for (int duplicate = 0; duplicate < 2; duplicate++) {
            GalateaDurableDelegateTransportException failure =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(() =>
                        client.EnsureBindingAsync(
                            new("binding-active"),
                            CancellationToken.None
                        ));
            Assert.Equal("DUPLICATE_BINDING_OPERATION_ID", failure.Code);
            Assert.Single(File.ReadAllLines(fixture.InputPath));
        }

        File.WriteAllText(fixture.CountPath, "release");
        Assert.Equal("thread-1", (await owner).ThreadId);
    }

    [Fact]
    public async Task BindingOutcomeUnknownCanRetrySameDurableOperation() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            count=0
            if [ -f {{Q("COUNT")}} ]; then count=$(cat {{Q("COUNT")}}); fi
            count=$((count + 1))
            printf '%s' "$count" > {{Q("COUNT")}}
            printf '%s\n' '{"v":2,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" >> {{Q("INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            binding_id=$(printf '%s' "$line" | sed -n 's/.*"bindingOperationId":"\([^"]*\)".*/\1/p')
            if [ "$count" -eq 1 ]; then
              sleep 30
            else
              printf '{"v":2,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-2"}\n' "$request_id" "$binding_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client(rpcTimeoutMs: 100);
        Task<GalateaDelegateBindingEstablished> first =
            client.EnsureBindingAsync(
                new("binding-retry"),
                CancellationToken.None
            );
        await WaitForLinesAsync(fixture.InputPath, 1);

        GalateaDurableDelegateTransportException unknown =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(async () =>
                    await first);
        Assert.Equal("BINDING_OUTCOME_UNKNOWN", unknown.Code);

        GalateaDelegateBindingEstablished retried =
            await client.EnsureBindingAsync(
                new("binding-retry"),
                CancellationToken.None
            );
        Assert.Equal("thread-2", retried.ThreadId);
        Assert.Equal(2, client.GenerationCountForTest);
        string[] lines = File.ReadAllLines(fixture.InputPath);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("ensure-binding", ReadType(line)));
    }

    [Fact]
    public async Task StartOutcomeUnknownPermanentlyFencesReplayButAllowsInspect() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            count=0
            if [ -f {{Q("COUNT")}} ]; then count=$(cat {{Q("COUNT")}}); fi
            count=$((count + 1))
            printf '%s' "$count" > {{Q("COUNT")}}
            printf '%s\n' '{"v":2,"type":"ready"}'
            if [ "$count" -eq 1 ]; then
              IFS= read -r line
              printf '%s\n' "$line" >> {{Q("INPUT")}}
              sleep 30
            else
              IFS= read -r line
              printf '%s\n' "$line" >> {{Q("INPUT")}}
              request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
              dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
              thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
              printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found"}\n' "$request_id" "$dispatch_id" "$thread_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client(rpcTimeoutMs: 100);
        Task<GalateaDelegateTurnAccepted> first = client.StartTurnAsync(
            new("dispatch-unknown", "thread-fixed", "exact task"),
            CancellationToken.None
        );
        await WaitForLinesAsync(fixture.InputPath, 1);

        GalateaDurableDelegateTransportException unknown =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(async () =>
                    await first);
        Assert.Equal("START_OUTCOME_UNKNOWN", unknown.Code);

        GalateaDurableDelegateTransportException replay =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(() =>
                    client.StartTurnAsync(
                        new(
                            "dispatch-unknown",
                            "thread-fixed",
                            "exact task"
                        ),
                        CancellationToken.None
                    ));
        Assert.Equal("DUPLICATE_DISPATCH_ID", replay.Code);

        GalateaDelegateDispatchInspection inspection =
            await client.InspectDispatchAsync(
                new("dispatch-unknown", "thread-fixed", "exact task"),
                CancellationToken.None
            );
        Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(inspection);
        string[] lines = File.ReadAllLines(fixture.InputPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("start-turn", ReadType(lines[0]));
        Assert.Equal("inspect-dispatch", ReadType(lines[1]));
    }

    [Fact]
    public async Task InspectionTimeoutCanRetryAndNotFoundStaysNonTerminal() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            count=0
            if [ -f {{Q("COUNT")}} ]; then count=$(cat {{Q("COUNT")}}); fi
            count=$((count + 1))
            printf '%s' "$count" > {{Q("COUNT")}}
            printf '%s\n' '{"v":2,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" >> {{Q("INPUT")}}
            if [ "$count" -eq 1 ]; then
              sleep 30
            else
              request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
              dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
              thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
              printf '{"v":2,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found"}\n' "$request_id" "$dispatch_id" "$thread_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client(rpcTimeoutMs: 100);
        Task<GalateaDelegateDispatchInspection> first =
            client.InspectDispatchAsync(
                new("dispatch-1", "thread-fixed", "task"),
                CancellationToken.None
            );
        await WaitForLinesAsync(fixture.InputPath, 1);

        GalateaDurableDelegateTransportException unavailable =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(async () =>
                    await first);
        Assert.Equal("INSPECTION_UNAVAILABLE", unavailable.Code);

        GalateaDelegateDispatchInspection retried =
            await client.InspectDispatchAsync(
                new("dispatch-1", "thread-fixed", "task"),
                CancellationToken.None
            );
        Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(retried);
        Assert.Equal(2, client.GenerationCountForTest);
        Assert.All(
            File.ReadAllLines(fixture.InputPath),
            line => Assert.Equal("inspect-dispatch", ReadType(line))
        );
    }

    [Fact]
    public async Task UnconfirmedReapPermanentlyBlocksV2Restart() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":2,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{Q("INPUT")}}
            sleep 30
            """
        );
        var hooks = new GalateaSidecarProcessTestHooks(
            WaitForExitBoundedAsync: static (_, _) =>
                Task.FromResult(false)
        );
        GalateaCodexDurableSidecarClient client = fixture.CreateV2Client(
            rpcTimeoutMs: 100,
            processHooks: hooks
        );
        using var cancellation = new CancellationTokenSource();
        Task<GalateaDelegateDispatchInspection> first =
            client.InspectDispatchAsync(
                new("dispatch-1", "thread-fixed", "task"),
                cancellation.Token
            );
        await WaitForLinesAsync(fixture.InputPath, 1);
        cancellation.Cancel();
        try {
            GalateaDurableDelegateTransportException unavailable =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(async () =>
                        await first);
            Assert.Equal("INSPECTION_UNAVAILABLE", unavailable.Code);

            GalateaDurableDelegateTransportException blocked =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(() =>
                        client.InspectDispatchAsync(
                            new("dispatch-1", "thread-fixed", "task"),
                            CancellationToken.None
                        ));
            Assert.Equal("SIDECAR_REAP_UNCONFIRMED", blocked.Code);
            Assert.Equal(1, client.GenerationCountForTest);
        }
        finally {
            GalateaDurableDelegateTransportException dispose =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(async () =>
                        await client.DisposeAsync());
            Assert.Equal("SIDECAR_REAP_UNCONFIRMED", dispose.Code);
        }
    }

    [Fact]
    public async Task ColdInnerRestartDelayUsesFiveRpcStartBudget() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":2,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{Q("INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
            # Model the five cold inner app-server RPC phases without spending
            # the independent startup margin: initialize, account/read,
            # thread/read, thread/resume, and turn/start.
            sleep 3
            sleep 3
            sleep 3
            sleep 3
            sleep 3
            printf '{"v":2,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"%s","turnId":"turn-after-cold-restart"}\n' "$request_id" "$dispatch_id" "$thread_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV2Client(rpcTimeoutMs: 3_000);

        GalateaDelegateTurnAccepted accepted = await client.StartTurnAsync(
            new("dispatch-cold", "thread-fixed", "task"),
            CancellationToken.None
        );

        Assert.Equal("turn-after-cold-restart", accepted.TurnId);
        Assert.Single(File.ReadAllLines(fixture.InputPath));
    }

    [Theory]
    [InlineData(100, 5_500, 5_300)]
    [InlineData(300_000, 1_505_000, 905_000)]
    public void NamedDeadlinesMatchNodeRpcComposition(
        int rpcTimeoutMs,
        int fiveRpcMilliseconds,
        int threeRpcMilliseconds
    ) {
        Assert.Equal(
            TimeSpan.FromMilliseconds(fiveRpcMilliseconds),
            GalateaCodexDurableSidecarClient.ComputeBindingDeadline(
                rpcTimeoutMs
            )
        );
        Assert.Equal(
            TimeSpan.FromMilliseconds(fiveRpcMilliseconds),
            GalateaCodexDurableSidecarClient.ComputeStartTurnDeadline(
                rpcTimeoutMs
            )
        );
        Assert.Equal(
            TimeSpan.FromMilliseconds(threeRpcMilliseconds),
            GalateaCodexDurableSidecarClient.ComputeInspectionDeadline(
                rpcTimeoutMs
            )
        );
    }

    [Theory]
    [InlineData(
        "ensure-binding",
        "BINDING_OUTCOME_UNKNOWN",
        (int)GalateaDurableDelegateFailurePolicy.RetryableBinding
    )]
    [InlineData(
        "start-turn",
        "START_OUTCOME_UNKNOWN",
        (int)GalateaDurableDelegateFailurePolicy.StartOutcomeUnknown
    )]
    [InlineData(
        "inspect-dispatch",
        "INSPECTION_UNAVAILABLE",
        (int)GalateaDurableDelegateFailurePolicy.InspectionUnavailable
    )]
    [InlineData(
        "protocol",
        "DUPLICATE_DISPATCH_ID",
        (int)GalateaDurableDelegateFailurePolicy.DeterministicConflict
    )]
    [InlineData(
        "start-turn",
        "CWD_MISMATCH",
        (int)GalateaDurableDelegateFailurePolicy.DeterministicConflict
    )]
    [InlineData(
        "ensure-binding",
        "THREAD_NOT_FOUND",
        (int)GalateaDurableDelegateFailurePolicy.DeterministicConflict
    )]
    [InlineData(
        "protocol",
        "SIDECAR_WRITE_GATE_TIMEOUT",
        (int)GalateaDurableDelegateFailurePolicy.PreWriteRejected
    )]
    [InlineData(
        "ensure-binding",
        "SIDECAR_STOPPING",
        (int)GalateaDurableDelegateFailurePolicy.Stopped
    )]
    [InlineData(
        "protocol",
        "SIDECAR_PROTOCOL_ERROR",
        (int)GalateaDurableDelegateFailurePolicy.FatalTransport
    )]
    [InlineData(
        "start-turn",
        "BINDING_OUTCOME_UNKNOWN",
        (int)GalateaDurableDelegateFailurePolicy.FatalTransport
    )]
    [InlineData(
        "inspect-dispatch",
        "START_OUTCOME_UNKNOWN",
        (int)GalateaDurableDelegateFailurePolicy.FatalTransport
    )]
    public void FailurePolicyIsClosedAndKeepsStageAndCode(
        string stage,
        string code,
        int expectedPolicy
    ) {
        var exception = new GalateaDurableDelegateTransportException(
            stage,
            code
        );

        Assert.Equal(stage, exception.Stage);
        Assert.Equal(code, exception.Code);
        Assert.Equal(
            (GalateaDurableDelegateFailurePolicy)expectedPolicy,
            exception.FailurePolicy
        );
    }

    private static string Q(string key) =>
        GalateaSidecarProcessFixture.ShellQuote(key);

    private static string ReadType(string line) {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("type").GetString()!;
    }

    private static async Task WaitForLinesAsync(string path, int count) {
        using var cancellation = new CancellationTokenSource(Deadline);
        while (true) {
            try {
                if (File.ReadAllLines(path).Length >= count) {
                    return;
                }
            }
            catch (FileNotFoundException) { }
            await Task.Delay(10, cancellation.Token);
        }
    }
}

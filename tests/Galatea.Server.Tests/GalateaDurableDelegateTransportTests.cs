using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDurableDelegateTransportTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExactV3FramesAndSixInspectionOutcomesAreTyped() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
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
                  printf '{"v":3,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-fixed"}\n' "$request_id" "$binding_id"
                  ;;
                2)
                  printf '{"v":3,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"%s","turnId":"turn-1"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                3)
                  printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                4)
                  printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"running","turnId":"turn-1","source":"live"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                5)
                  printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"completed","turnId":"turn-1","final":"完成，包含中文。","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                6)
                  printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"failed","turnId":"turn-1","code":"TURN_FAILED","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                7)
                  printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"ambiguous","code":"DISPATCH_BODY_MISMATCH","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
                8)
                  printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"unavailable","turnId":"turn-1","code":"ACCEPTED_TURN_NOT_VISIBLE","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
                  ;;
              esac
            done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

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
                GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    "dispatch-missing",
                    binding.ThreadId,
                    "never sent"
                ),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1", binding.ThreadId, "exact\n你好", "turn-1"
                ),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1", binding.ThreadId, "exact\n你好", "turn-1"
                ),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1", binding.ThreadId, "exact\n你好", "turn-1"
                ),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1", binding.ThreadId, "exact\n你好", "turn-1"
                ),
                CancellationToken.None
            ),
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1", binding.ThreadId, "exact\n你好", "turn-1"
                ),
                CancellationToken.None
            )
        ];

        Assert.Equal(
            GalateaDelegateInspectionSource.Persistent,
            Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(
                inspections[0]
            ).Source
        );
        Assert.Equal(
            GalateaDelegateInspectionSource.Live,
            Assert.IsType<GalateaDelegateDispatchInspection.Running>(
                inspections[1]
            ).Source
        );
        var completed = Assert.IsType<
            GalateaDelegateDispatchInspection.Completed>(inspections[2]);
        Assert.Equal("完成，包含中文。", completed.Final);
        Assert.Equal(GalateaDelegateInspectionSource.Persistent,
            completed.Source);
        var failed = Assert.IsType<
            GalateaDelegateDispatchInspection.Failed>(inspections[3]);
        Assert.Equal("TURN_FAILED", failed.Code);
        Assert.Equal(GalateaDelegateInspectionSource.Persistent,
            failed.Source);
        var ambiguous = Assert.IsType<
            GalateaDelegateDispatchInspection.Ambiguous>(inspections[4]);
        Assert.Equal("DISPATCH_BODY_MISMATCH", ambiguous.Code);
        Assert.Equal(GalateaDelegateInspectionSource.Persistent,
            ambiguous.Source);
        var unavailable = Assert.IsType<
            GalateaDelegateDispatchInspection.AcceptedTurnNotVisible>(
                inspections[5]
            );
        Assert.Equal("ACCEPTED_TURN_NOT_VISIBLE", unavailable.Code);
        Assert.Equal(GalateaDelegateInspectionSource.Persistent,
            unavailable.Source);

        string[] lines = File.ReadAllLines(fixture.InputPath);
        Assert.Equal(8, lines.Length);
        Assert.Equal(
            [
                "ensure-binding",
                "start-turn",
                "inspect-dispatch",
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
        using JsonDocument unknownInspect = JsonDocument.Parse(lines[2]);
        Assert.Equal(JsonValueKind.Null, unknownInspect.RootElement
            .GetProperty("expectedTurnId").ValueKind);
        Assert.Equal(
            [
                "dispatchId", "expectedTurnId", "requestId", "task",
                "threadId", "type", "v"
            ],
            unknownInspect.RootElement.EnumerateObject()
                .Select(static value => value.Name)
                .Order(StringComparer.Ordinal)
        );
        using JsonDocument acceptedInspect = JsonDocument.Parse(lines[3]);
        Assert.Equal("turn-1", acceptedInspect.RootElement
            .GetProperty("expectedTurnId").GetString());
        Assert.Equal(1, client.GenerationCountForTest);
    }

    [Fact]
    public async Task ConcurrentResponsesAreCorrelatedByRequestId() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r first
            IFS= read -r second
            printf '%s\n' "$first" >> {{Q("INPUT")}}
            printf '%s\n' "$second" >> {{Q("INPUT")}}
            first_request=$(printf '%s' "$first" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            first_dispatch=$(printf '%s' "$first" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            second_request=$(printf '%s' "$second" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            second_dispatch=$(printf '%s' "$second" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            printf '{"v":3,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-fixed","turnId":"turn-%s"}\n' "$second_request" "$second_dispatch" "$second_dispatch"
            printf '{"v":3,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"thread-fixed","turnId":"turn-%s"}\n' "$first_request" "$first_dispatch" "$first_dispatch"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

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
    [InlineData("v2")]
    public async Task InvalidV3BusinessFrameIsGenerationFatal(string kind) {
        string response = kind switch {
            "wrong-identity" =>
                "printf '{\"v\":3,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"wrong\",\"threadId\":\"thread-1\"}\\n' \"$request_id\"",
            "extra-property" =>
                "printf '{\"v\":3,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\",\"extra\":1}\\n' \"$request_id\"",
            "wrong-case" =>
                "printf '{\"v\":3,\"type\":\"binding-established\",\"RequestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\"}\\n' \"$request_id\"",
            "duplicate-property" =>
                "printf '{\"v\":3,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\",\"threadId\":\"thread-2\"}\\n' \"$request_id\"",
            _ =>
                "printf '{\"v\":2,\"type\":\"binding-established\",\"requestId\":\"%s\",\"bindingOperationId\":\"binding-1\",\"threadId\":\"thread-1\"}\\n' \"$request_id\""
        };
        using var fixture = new GalateaSidecarProcessFixture(
            "printf '%s\\n' '{\"v\":3,\"type\":\"ready\"}'\n"
                + "IFS= read -r line\n"
                + "request_id=$(printf '%s' \"$line\" | sed -n 's/.*\"requestId\":\"\\([^\"]*\\)\".*/\\1/p')\n"
                + response
                + "\nsleep 30\n"
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

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
    [InlineData("missing-source", false)]
    [InlineData("wrong-source-case", false)]
    [InlineData("extra-property", false)]
    [InlineData("duplicate-property", false)]
    [InlineData("accepted-not-found", true)]
    [InlineData("outcome-unknown-unavailable", true)]
    [InlineData("not-found-live", false)]
    [InlineData("unavailable-live", false)]
    [InlineData("unavailable-wrong-code", false)]
    [InlineData("wrong-returned-turn", true)]
    public async Task InvalidInspectionFrameOrSelectorCombinationIsFatal(
        string kind,
        bool selectorMismatch
    ) {
        string response = kind switch {
            "missing-source" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"running\",\"turnId\":\"turn-1\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "wrong-source-case" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"running\",\"turnId\":\"turn-1\",\"source\":\"Persistent\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "extra-property" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"running\",\"turnId\":\"turn-1\",\"source\":\"persistent\",\"extra\":1}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "duplicate-property" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"running\",\"turnId\":\"turn-1\",\"source\":\"persistent\",\"source\":\"live\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "accepted-not-found" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"not-found\",\"source\":\"persistent\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "outcome-unknown-unavailable" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"unavailable\",\"turnId\":\"turn-1\",\"code\":\"ACCEPTED_TURN_NOT_VISIBLE\",\"source\":\"persistent\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "not-found-live" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"not-found\",\"source\":\"live\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "unavailable-live" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"unavailable\",\"turnId\":\"turn-1\",\"code\":\"ACCEPTED_TURN_NOT_VISIBLE\",\"source\":\"live\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            "unavailable-wrong-code" =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"unavailable\",\"turnId\":\"turn-1\",\"code\":\"INSPECTION_UNAVAILABLE\",\"source\":\"persistent\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\"",
            _ =>
                "printf '{\"v\":3,\"type\":\"dispatch-inspected\",\"requestId\":\"%s\",\"dispatchId\":\"%s\",\"threadId\":\"%s\",\"outcome\":\"running\",\"turnId\":\"turn-2\",\"source\":\"persistent\"}\\n' \"$request_id\" \"$dispatch_id\" \"$thread_id\""
        };
        using var fixture = new GalateaSidecarProcessFixture(
            "printf '%s\\n' '{\"v\":3,\"type\":\"ready\"}'\n"
                + "IFS= read -r line\n"
                + "request_id=$(printf '%s' \"$line\" | sed -n 's/.*\"requestId\":\"\\([^\"]*\\)\".*/\\1/p')\n"
                + "dispatch_id=$(printf '%s' \"$line\" | sed -n 's/.*\"dispatchId\":\"\\([^\"]*\\)\".*/\\1/p')\n"
                + "thread_id=$(printf '%s' \"$line\" | sed -n 's/.*\"threadId\":\"\\([^\"]*\\)\".*/\\1/p')\n"
                + response
                + "\nsleep 30\n"
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();
        GalateaInspectDelegateDispatchRequest request =
            kind == "outcome-unknown-unavailable"
                ? GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    "dispatch-1", "thread-fixed", "task"
                )
                : GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1", "thread-fixed", "task", "turn-1"
                );

        GalateaDurableDelegateTransportException failure =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(() =>
                    client.InspectDispatchAsync(
                        request,
                        CancellationToken.None
                    ));

        Assert.Equal(
            selectorMismatch
                ? "INSPECTION_SELECTOR_MISMATCH"
                : "INSPECTION_UNAVAILABLE",
            failure.Code
        );
        Assert.Equal(
            selectorMismatch
                ? GalateaDurableDelegateFailurePolicy.DeterministicConflict
                : GalateaDurableDelegateFailurePolicy.InspectionUnavailable,
            failure.FailurePolicy
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("not allowed")]
    [InlineData("\n")]
    public async Task InvalidExpectedTurnIdIsRejectedBeforeProcessStart(
        string expectedTurnId
    ) {
        using var fixture = new GalateaSidecarProcessFixture(
            "printf '%s\\n' '{\"v\":3,\"type\":\"ready\"}'\n"
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    "dispatch-1",
                    "thread-fixed",
                    "task",
                    expectedTurnId
                ),
                CancellationToken.None
            ));

        Assert.False(client.HasStartedProcessForTest);
    }

    [Fact]
    public async Task GenericAcceptedTurnNotVisibleFailureIsNotSemantic() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
            printf '{"v":3,"type":"failed","stage":"inspect-dispatch","requestId":"%s","dispatchId":"%s","threadId":"%s","code":"ACCEPTED_TURN_NOT_VISIBLE"}\n' "$request_id" "$dispatch_id" "$thread_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

        GalateaDurableDelegateTransportException failure =
            await Assert.ThrowsAsync<
                GalateaDurableDelegateTransportException>(() =>
                    client.InspectDispatchAsync(
                        GalateaInspectDelegateDispatchRequest.ForAccepted(
                            "dispatch-1",
                            "thread-fixed",
                            "task",
                            "turn-1"
                        ),
                        CancellationToken.None
                    ));

        Assert.Equal("ACCEPTED_TURN_NOT_VISIBLE", failure.Code);
        Assert.Equal(
            GalateaDurableDelegateFailurePolicy.FatalTransport,
            failure.FailurePolicy
        );
    }

    [Theory]
    [InlineData("DISPATCH_TURN_MISMATCH")]
    [InlineData("LIVE_OBSERVATION_CONFLICT")]
    [InlineData("PAGE_SHAPE_INVALID")]
    [InlineData("PAGINATION_CURSOR_INVALID")]
    [InlineData("PAGINATION_CURSOR_LOOP")]
    public async Task V3PaginationAndLiveAmbiguitiesRemainSemantic(
        string code
    ) {
        string source = code == "LIVE_OBSERVATION_CONFLICT"
            ? "live"
            : "persistent";
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
            printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"ambiguous","code":"{{code}}","source":"{{source}}"}\n' "$request_id" "$dispatch_id" "$thread_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

        GalateaDelegateDispatchInspection inspection =
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    "dispatch-1",
                    "thread-fixed",
                    "task"
                ),
                CancellationToken.None
            );

        var ambiguous = Assert.IsType<
            GalateaDelegateDispatchInspection.Ambiguous>(inspection);
        Assert.Equal(code, ambiguous.Code);
        Assert.Equal(
            source == "live"
                ? GalateaDelegateInspectionSource.Live
                : GalateaDelegateInspectionSource.Persistent,
            ambiguous.Source
        );
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
            ? "printf '%s\\n' '{\"v\":3,\"type\":\"broken\",'"
            : "exit 7";
        using var fixture = new GalateaSidecarProcessFixture(
            "printf '%s\\n' '{\"v\":3,\"type\":\"ready\"}'\n"
                + "IFS= read -r line\n"
                + terminal
                + "\n"
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();

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
                        GalateaInspectDelegateDispatchRequest
                            .ForOutcomeUnknown(
                                "dispatch-1", "thread-fixed", "task"
                            ),
                        CancellationToken.None
                    ));

        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public async Task ConcurrentBindingDuplicateDoesNotReleaseOwnersClaim() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{Q("INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            binding_id=$(printf '%s' "$line" | sed -n 's/.*"bindingOperationId":"\([^"]*\)".*/\1/p')
            while [ ! -f {{Q("COUNT")}} ]; do sleep 0.01; done
            printf '{"v":3,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-1"}\n' "$request_id" "$binding_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client();
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
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" >> {{Q("INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            binding_id=$(printf '%s' "$line" | sed -n 's/.*"bindingOperationId":"\([^"]*\)".*/\1/p')
            if [ "$count" -eq 1 ]; then
              sleep 30
            else
              printf '{"v":3,"type":"binding-established","requestId":"%s","bindingOperationId":"%s","threadId":"thread-2"}\n' "$request_id" "$binding_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client(rpcTimeoutMs: 100);
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
            printf '%s\n' '{"v":3,"type":"ready"}'
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
              printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client(rpcTimeoutMs: 100);
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
                GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    "dispatch-unknown", "thread-fixed", "exact task"
                ),
                CancellationToken.None
            );
        Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(inspection);
        string[] lines = File.ReadAllLines(fixture.InputPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("start-turn", ReadType(lines[0]));
        Assert.Equal("inspect-dispatch", ReadType(lines[1]));
    }

    [Fact]
    public async Task CancelledInFlightStartStillFencesReplayAsOutcomeUnknown() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            count=0
            if [ -f {{Q("COUNT")}} ]; then count=$(cat {{Q("COUNT")}}); fi
            count=$((count + 1))
            printf '%s' "$count" > {{Q("COUNT")}}
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" >> {{Q("INPUT")}}
            if [ "$count" -eq 1 ]; then
              sleep 30
            else
              request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
              dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
              thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
              printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
              while IFS= read -r ignored; do :; done
            fi
            """
        );
        GalateaCodexDurableSidecarClient client = fixture.CreateV3Client();
        try {
            using var cancellation = new CancellationTokenSource();
            Task<GalateaDelegateTurnAccepted> first = client.StartTurnAsync(
                new("dispatch-cancelled", "thread-fixed", "exact task"),
                cancellation.Token
            );
            await WaitForLinesAsync(fixture.InputPath, 1);
            cancellation.Cancel();

            GalateaDurableDelegateTransportException unknown =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(async () =>
                        await first.WaitAsync(Deadline));
            Assert.Equal("START_OUTCOME_UNKNOWN", unknown.Code);

            GalateaDurableDelegateTransportException replay =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(() =>
                        client.StartTurnAsync(
                            new(
                                "dispatch-cancelled",
                                "thread-fixed",
                                "exact task"
                            ),
                            CancellationToken.None
                        ));
            Assert.Equal("DUPLICATE_DISPATCH_ID", replay.Code);

            GalateaDelegateDispatchInspection inspection =
                await client.InspectDispatchAsync(
                    GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                        "dispatch-cancelled",
                        "thread-fixed",
                        "exact task"
                    ),
                    CancellationToken.None
                );
            Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(
                inspection
            );
            Assert.Equal(2, client.GenerationCountForTest);
            string[] lines = File.ReadAllLines(fixture.InputPath);
            Assert.Equal(2, lines.Length);
            Assert.Equal("start-turn", ReadType(lines[0]));
            Assert.Equal("inspect-dispatch", ReadType(lines[1]));
        }
        finally {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task InspectionHasNoClientAggregateDeadline() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" >> {{Q("INPUT")}}
            request_id=$(printf '%s' "$line" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$line" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            thread_id=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
            # The removed C# three-RPC aggregate deadline was 5.3 seconds
            # for rpcTimeoutMs=100. Node owns each bounded RPC now.
            sleep 6
            printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client(rpcTimeoutMs: 100);
        GalateaDelegateDispatchInspection result =
            await client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    "dispatch-1", "thread-fixed", "task"
                ),
                CancellationToken.None
            );

        Assert.IsType<GalateaDelegateDispatchInspection.NotFound>(result);
        Assert.Equal(1, client.GenerationCountForTest);
        Assert.Single(File.ReadAllLines(fixture.InputPath));
    }

    [Fact]
    public async Task DisposeCompletesInspectionWithoutAggregateDeadline() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{Q("INPUT")}}
            while IFS= read -r ignored; do :; done
            printf '%s' 'graceful-eof' > {{Q("ENV")}}
            """
        );
        GalateaCodexDurableSidecarClient client = fixture.CreateV3Client();
        Task? dispose = null;
        try {
            Task<GalateaDelegateDispatchInspection> pending =
                client.InspectDispatchAsync(
                    GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                        "dispatch-1",
                        "thread-fixed",
                        "task"
                    ),
                    CancellationToken.None
                );
            await WaitForLinesAsync(fixture.InputPath, 1);

            dispose = client.DisposeAsync().AsTask();
            GalateaDurableDelegateTransportException failure =
                await Assert.ThrowsAsync<
                    GalateaDurableDelegateTransportException>(async () =>
                        await pending.WaitAsync(Deadline));
            await dispose.WaitAsync(Deadline);

            Assert.Equal("INSPECTION_UNAVAILABLE", failure.Code);
            Assert.Equal(1, client.GenerationCountForTest);
            Assert.Equal("graceful-eof",
                File.ReadAllText(fixture.EnvironmentPath));
        }
        finally {
            if (dispose is null) {
                await client.DisposeAsync();
            }
            else if (!dispose.IsCompleted) {
                await dispose.WaitAsync(Deadline);
            }
        }
    }

    [Fact]
    public async Task CancelledInFlightInspectionKeepsGenerationForLateResponse() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r first
            printf '%s\n' "$first" >> {{Q("INPUT")}}
            while [ ! -f {{Q("COUNT")}} ]; do sleep 0.01; done
            request_id=$(printf '%s' "$first" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$first" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            thread_id=$(printf '%s' "$first" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
            printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"not-found","source":"persistent"}\n' "$request_id" "$dispatch_id" "$thread_id"
            IFS= read -r second
            printf '%s\n' "$second" >> {{Q("INPUT")}}
            request_id=$(printf '%s' "$second" | sed -n 's/.*"requestId":"\([^"]*\)".*/\1/p')
            dispatch_id=$(printf '%s' "$second" | sed -n 's/.*"dispatchId":"\([^"]*\)".*/\1/p')
            thread_id=$(printf '%s' "$second" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
            printf '{"v":3,"type":"dispatch-inspected","requestId":"%s","dispatchId":"%s","threadId":"%s","outcome":"running","turnId":"turn-2","source":"live"}\n' "$request_id" "$dispatch_id" "$thread_id"
            while IFS= read -r ignored; do :; done
            printf '%s' 'graceful-eof' > {{Q("ENV")}}
            """
        );
        GalateaCodexDurableSidecarClient client = fixture.CreateV3Client();
        try {
            using var cancellation = new CancellationTokenSource();
            Task<GalateaDelegateDispatchInspection> cancelled =
                client.InspectDispatchAsync(
                    GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                        "dispatch-1", "thread-fixed", "task"
                    ),
                    cancellation.Token
                );
            await WaitForLinesAsync(fixture.InputPath, 1);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await cancelled.WaitAsync(Deadline));
            Assert.Equal(1, client.GenerationCountForTest);

            File.WriteAllText(fixture.CountPath, "release");
            GalateaDelegateDispatchInspection inspection =
                await client.InspectDispatchAsync(
                    GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                        "dispatch-2", "thread-fixed", "task"
                    ),
                    CancellationToken.None
                );

            var running = Assert.IsType<
                GalateaDelegateDispatchInspection.Running>(inspection);
            Assert.Equal("turn-2", running.TurnId);
            Assert.Equal(1, client.GenerationCountForTest);
            Assert.Equal(2, File.ReadAllLines(fixture.InputPath).Length);
        }
        finally {
            await client.DisposeAsync();
        }
        Assert.Equal("graceful-eof", File.ReadAllText(fixture.EnvironmentPath));
    }

    [Fact]
    public async Task UnconfirmedReapPermanentlyBlocksV3Restart() {
        using var fixture = new GalateaSidecarProcessFixture(
            $$"""
            printf '%s\n' '{"v":3,"type":"ready"}'
            IFS= read -r line
            printf '%s\n' "$line" > {{Q("INPUT")}}
            printf '%s\n' '{"v":3,"type":"broken",'
            sleep 30
            """
        );
        var hooks = new GalateaSidecarProcessTestHooks(
            WaitForExitBoundedAsync: static (_, _) =>
                Task.FromResult(false)
        );
        GalateaCodexDurableSidecarClient client = fixture.CreateV3Client(
            rpcTimeoutMs: 100,
            processHooks: hooks
        );
        Task<GalateaDelegateDispatchInspection> first =
            client.InspectDispatchAsync(
                GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    "dispatch-1", "thread-fixed", "task"
                ),
                CancellationToken.None
            );
        await WaitForLinesAsync(fixture.InputPath, 1);
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
                            GalateaInspectDelegateDispatchRequest
                                .ForOutcomeUnknown(
                                    "dispatch-1", "thread-fixed", "task"
                                ),
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
            printf '%s\n' '{"v":3,"type":"ready"}'
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
            printf '{"v":3,"type":"turn-accepted","requestId":"%s","dispatchId":"%s","threadId":"%s","turnId":"turn-after-cold-restart"}\n' "$request_id" "$dispatch_id" "$thread_id"
            while IFS= read -r ignored; do :; done
            """
        );
        await using GalateaCodexDurableSidecarClient client =
            fixture.CreateV3Client(rpcTimeoutMs: 3_000);

        GalateaDelegateTurnAccepted accepted = await client.StartTurnAsync(
            new("dispatch-cold", "thread-fixed", "task"),
            CancellationToken.None
        );

        Assert.Equal("turn-after-cold-restart", accepted.TurnId);
        Assert.Single(File.ReadAllLines(fixture.InputPath));
    }

    [Theory]
    [InlineData(100, 5_500)]
    [InlineData(300_000, 1_505_000)]
    public void NamedDeadlinesMatchNodeRpcComposition(
        int rpcTimeoutMs,
        int fiveRpcMilliseconds
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
        "inspect-dispatch",
        "ACCEPTED_TURN_NOT_VISIBLE",
        (int)GalateaDurableDelegateFailurePolicy.FatalTransport
    )]
    [InlineData(
        "protocol",
        "DUPLICATE_DISPATCH_ID",
        (int)GalateaDurableDelegateFailurePolicy.DeterministicConflict
    )]
    [InlineData(
        "inspect-dispatch",
        "INSPECTION_SELECTOR_MISMATCH",
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

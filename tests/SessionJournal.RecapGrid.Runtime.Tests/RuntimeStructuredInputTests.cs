using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class RuntimeStructuredInputTests {
    [Theory]
    [InlineData(SessionTurnEndReason.Stopped)]
    [InlineData(SessionTurnEndReason.Rejected)]
    [InlineData(SessionTurnEndReason.Incomplete)]
    public async Task EndedTurnProjectsAsExplicitObservationWithoutAFakeAction(SessionTurnEndReason reason) {
        var ended = new SessionTurnEndedMessage(reason);
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [ended]);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            Assert.DoesNotContain(request.PromptPrefix.SharedContextMessages,
                static message => message is SessionTurnEndedMessage or ActionMessage);
            Assert.Equal(ended.Render(), Assert.IsType<ObservationMessage>(
                request.PromptPrefix.SharedContextMessages[1]).Content);
            return ValueTask.FromResult(RuntimeTestFixture.Updated(request, invoker!));
        });
        using var runtime = Runtime(batch, invoker);

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, default));

        Assert.IsType<RecapCellExecutionOutcome.Updated>(Assert.Single(result.OrderedOutcomes));
        Assert.Equal(1, invoker.CallCount);
    }

    [Fact]
    public async Task StructuredHistoryWithoutProjectorRejectsBeforeAnyCall() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [new SessionInputObservationMessage(Content())]);
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var runtime = Runtime(batch, invoker);

        var rejected = Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("InputProjectorUnavailable", rejected.Code);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task EachActualCallProjectsSemanticHistoryUsingCurrentStyle() {
        SessionInputContent content = Content();
        byte[] original = content.ToUtf8Json();
        string semanticHash = SessionHistorySemanticCommitment.ComputeObservationContributionSha256(content);
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 2, history: [new SessionInputObservationMessage(content)]);
        var projector = new ChangingProjector();
        var observed = new List<string>();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            Assert.Equal(observed.Count + 1, projector.Calls);
            Assert.All(request.PromptPrefix.SharedContextMessages, static message => Assert.IsNotType<SessionInputObservationMessage>(message));
            observed.Add(Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[1]).Content!);
            projector.Style = "second";
            return ValueTask.FromResult(RuntimeTestFixture.Updated(request, invoker!));
        });
        using var runtime = Runtime(batch, invoker, projector);

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, default));

        Assert.All(result.OrderedOutcomes, static outcome => Assert.IsType<RecapCellExecutionOutcome.Updated>(outcome));
        Assert.Equal(["first: semantic body", "second: semantic body"], observed);
        Assert.Equal(original, content.ToUtf8Json());
        Assert.Equal(semanticHash, SessionHistorySemanticCommitment.ComputeObservationContributionSha256(content));
    }

    [Fact]
    public async Task UnknownSchemaDoesNotBecomeJsonPromptOrLeakProjectionExceptionBody() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [new SessionInputObservationMessage(Content())]);
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var runtime = Runtime(batch, invoker, new RejectingProjector());

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, default));
        var failure = Assert.IsType<RecapCellExecutionOutcome.Failed>(Assert.Single(result.OrderedOutcomes));

        Assert.Equal("InputProjectionFailed", failure.Code);
        Assert.DoesNotContain("private input", failure.Detail);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task OversizedTransientProjectionNeverDispatchesOrChangesSemanticFacts() {
        SessionInputContent content = Content();
        byte[] original = content.ToUtf8Json();
        string hash = SessionHistorySemanticCommitment.ComputeObservationContributionSha256(content);
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [new SessionInputObservationMessage(content)]);
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var runtime = Runtime(batch, invoker, new DelegateProjector(_ => new string('界', 1024)), maximumInputUtf8Bytes: 512);

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("InputRequestTooLarge", Assert.IsType<RecapCellExecutionOutcome.Failed>(Assert.Single(result.OrderedOutcomes)).Code);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(original, content.ToUtf8Json());
        Assert.Equal(hash, SessionHistorySemanticCommitment.ComputeObservationContributionSha256(content));
    }

    [Theory]
    [InlineData("system-header")]
    [InlineData("observation-header")]
    [InlineData("action-header")]
    [InlineData("observation")]
    [InlineData("action")]
    [InlineData("tool-name")]
    [InlineData("tool-id")]
    [InlineData("tool-arguments")]
    [InlineData("tool-results-content")]
    [InlineData("tool-result-name")]
    [InlineData("tool-result-id")]
    [InlineData("tool-result-text")]
    public async Task EveryExistingVisibleHistoryCarrierContributesToTheFinalInputLimit(string carrier) {
        string large = new('界', 1024);
        IHistoryMessage history = carrier switch {
            "system-header" => new SessionContextHeader(large, null, null),
            "observation-header" => new SessionContextHeader(null, large, null),
            "action-header" => new SessionContextHeader(null, null, new ActionMessage([new ActionBlock.Text(large)])),
            "observation" => new ObservationMessage(large),
            "action" => new ActionMessage([new ActionBlock.Text(large)]),
            "tool-name" or "tool-id" or "tool-arguments" => new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                carrier == "tool-name" ? large : "tool",
                carrier == "tool-id" ? large : "call",
                carrier == "tool-arguments" ? large : "{}"))]),
            _ => new ToolResultsMessage(carrier == "tool-results-content" ? large : null, [ToolResult.FromText(
                carrier == "tool-result-name" ? large : "tool",
                carrier == "tool-result-id" ? large : "call",
                ToolExecutionStatus.Success,
                carrier == "tool-result-text" ? large : "result")])
        };
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [history]);
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var runtime = Runtime(batch, invoker, maximumInputUtf8Bytes: 512);

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("InputRequestTooLarge", Assert.IsType<RecapCellExecutionOutcome.Failed>(Assert.Single(result.OrderedOutcomes)).Code);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task SmallOrdinaryRequestFitsTheSameLimitUsedByOversizeTests() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [new ObservationMessage("small")]);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(RuntimeTestFixture.Updated(request, invoker!)));
        using var runtime = Runtime(batch, invoker, maximumInputUtf8Bytes: 512);

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, default));

        Assert.IsType<RecapCellExecutionOutcome.Updated>(Assert.Single(result.OrderedOutcomes));
        Assert.Equal(1, invoker.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationDuringProjectionRemainsNotStarted(bool throwCancellation) {
        using var cancellation = new CancellationTokenSource();
        FrozenRowBatch batch = RuntimeTestFixture.Batch(history: [new SessionInputObservationMessage(Content())]);
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var runtime = Runtime(batch, invoker, new DelegateProjector(_ => {
            cancellation.Cancel();
            if (throwCancellation) { throw new OperationCanceledException(cancellation.Token); }
            return "rendered content";
        }));

        var result = Assert.IsType<RecapCellBatchExecutionResult.Completed>(await runtime.ExecuteAsync(batch, cancellation.Token));

        Assert.IsType<RecapCellExecutionOutcome.NotStartedDueToCallerCancellation>(Assert.Single(result.OrderedOutcomes));
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public void InputMeasureIncludesSystemPriorAndWorkTailAndRejectsUnsupportedToolDefinitions() {
        var request = new CompletionRequest("model", new CompletionPromptPrefix(
            "system", CompletionOutputContract.ProviderDefault([]), [new ObservationMessage("prior")]), [new ObservationMessage("tail")]);
        Assert.Equal("modelsystempriortail".Length, RuntimeRequestBudget.MeasureInputUtf8Bytes(request));
        Assert.Throws<NotSupportedException>(() => RuntimeRequestBudget.MeasureInputUtf8Bytes(new CompletionRequest(
            "model", new CompletionPromptPrefix("system", CompletionOutputContract.ProviderDefault([
                new ToolDefinition("tool", "description", new ToolSchema.Object())]), []), [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecapCompletionRuntimeOptions(maximumInputUtf8Bytes: 0));
    }

    private static SessionInputContent Content() {
        using JsonDocument document = JsonDocument.Parse("{\"body\":\"semantic body\",\"sender\":\"character-a\"}");
        return SessionInputContent.Structured("test.observation.v1", document.RootElement);
    }

    private static RecapCompletionRuntime Runtime(FrozenRowBatch batch, IRecapCompletionInvoker invoker, ISessionInputProjector? projector = null,
        long maximumInputUtf8Bytes = RecapCompletionRuntimeOptions.DefaultMaximumInputUtf8Bytes) {
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        return new RecapCompletionRuntime(new ScriptedResolver(key => key == route.Key
            ? new RecapCompletionRouteResolution.Bound(route)
            : new RecapCompletionRouteResolution.Unavailable("RouteMissing", "No exact route.")),
            new RecapCompletionRuntimeOptions(maximumInputUtf8Bytes: maximumInputUtf8Bytes), inputProjector: projector);
    }

    private sealed class ChangingProjector : ISessionInputProjector {
        internal string Style { get; set; } = "first";
        internal int Calls { get; private set; }
        public string Project(SessionInputContent input) {
            Calls++;
            return Style + ": " + input.JsonValue.GetProperty("body").GetString();
        }
    }

    private sealed class RejectingProjector : ISessionInputProjector {
        public string Project(SessionInputContent input) => throw new NotSupportedException("private input echoed by renderer");
    }

    private sealed class DelegateProjector(Func<SessionInputContent, string> project) : ISessionInputProjector {
        public string Project(SessionInputContent input) => project(input);
    }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class TextExtractionLoopTests {
    [Theory]
    [InlineData("a\nb\n", 3, "a\nb")]
    [InlineData("a\r\nb\r\n", 3, "a\r\nb")]
    [InlineData("a\rb\r", 3, "a\rb")]
    [InlineData("a\r\nb\n", 3, "a\r\nb")]
    public void Lines_PreserveOriginalSeparatorsAndTerminalEmptyLine(string text, int count, string firstTwo) {
        TextExtractionInput input = TextExtractionInput.Numbered(text);
        Assert.Equal(text, input.OriginalText);
        Assert.Equal(count, input.Lines!.LineCount);
        Assert.Equal(firstTwo, input.Lines.Slice(1, 2));
        Assert.Equal(string.Empty, input.Lines.Slice(count, count));
        Assert.Equal(text, input.Lines.Slice(1, count));
    }

    [Fact]
    public void Lines_RenderQuotedDataAndNeverNormalizeContent() {
        const string body = "\t  > **x** 😀 &quot;\n```cs\r\nL000099 | \\\"literal\\\"\r```";
        var input = TextExtractionInput.Numbered(body);
        Assert.Equal(body, input.Lines!.Slice(1, 4));
        string[] rows = input.RenderedText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, rows.Length);
        Assert.Equal("\t  > **x** 😀 &quot;", JsonSerializer.Deserialize<string>(rows[0][10..]));
        Assert.StartsWith("L000003 | ", rows[2]);
        Assert.Equal("L000099 | \\\"literal\\\"", JsonSerializer.Deserialize<string>(rows[2][10..]));
        Assert.Equal(1, TextExtractionInput.Numbered("").Lines!.LineCount);
    }

    [Fact]
    public void Lines_RejectInvalidRangesUnicodeAndInputLimits() {
        var lines = TextExtractionInput.Numbered("a\n\nb").Lines!;
        foreach ((int start, int end) in new[] { (0, 1), (2, 1), (1, 4), (-1, 1) }) {
            Assert.Equal(TextExtractionFailureKind.InvalidSourceRange,
                Assert.Throws<TextExtractionException>(() => lines.Slice(start, end)).Kind);
        }
        Assert.Equal("", lines.Slice(2, 2));
        Assert.Throws<TextExtractionException>(() => TextExtractionInput.Numbered("\ud800"));
        Assert.Throws<TextExtractionException>(() => TextExtractionInput.Numbered(
            new string('x', TextExtractorBounds.MaximumTargetTextUtf8Bytes + 1)));
        Assert.Equal(TextExtractionInput.MaximumLineCount,
            TextExtractionInput.Numbered(new string('\n', TextExtractionInput.MaximumLineCount - 1)).Lines!.LineCount);
        Assert.Throws<TextExtractionException>(() => TextExtractionInput.Numbered(
            new string('\n', TextExtractionInput.MaximumLineCount)));
    }

    [Fact]
    public async Task Loop_ContinuesUntilZeroAndPreservesCompleteActionsAndToolResults() {
        var reasoning = new ActionBlock.OpaqueReasoningBlock("test-native", new byte[] { 0, 42, 255 },
            new CompletionDescriptor("line-loop-tests", "test", "model"));
        var first = new ActionMessage([reasoning, new ActionBlock.Text("diagnostic"), Call(2, "a")]);
        var second = new ActionMessage([Call(1, "b"), Call(2, "c")]);
        var client = new SequenceClient((_, ordinal, _) => Task.FromResult(
            ordinal switch { 1 => first, 2 => second, _ => new ActionMessage([]) }));
        TextExtractionResult result = await Create(client).ExtractAsync(
            TextExtractionInput.Numbered("first\nsecond"), "extract");
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(new[] { "first", "second" }, result.Artifacts
            .Cast<TextExtractionArtifact<Body>>().Select(static a => a.Value.Text));
        Assert.Same(first, client.Requests[1].TailMessages[1]);
        Assert.Same(reasoning, Assert.IsType<ActionMessage>(client.Requests[1].TailMessages[1]).Blocks[0]);
        Assert.IsType<ToolResultsMessage>(client.Requests[1].TailMessages[2]);
        Assert.Same(second, client.Requests[2].TailMessages[3]);
        Assert.Same(client.Requests[0].PromptPrefix, client.Requests[2].PromptPrefix);
        Assert.Equal("diagnostic", result.DiagnosticText);
    }

    [Fact]
    public async Task Loop_ZeroInFirstCompletionReturnsEmpty() {
        var client = new SequenceClient((_, _, _) => Task.FromResult(new ActionMessage([])));
        Assert.Empty((await Create(client).ExtractAsync(TextExtractionInput.Numbered("none"), "extract")).Artifacts);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Loop_SixteenIndividualArtifactsThenZero() {
        var client = new SequenceClient((_, ordinal, _) => Task.FromResult(
            ordinal <= 16 ? new ActionMessage([Call(ordinal, $"call-{ordinal}")]) : new ActionMessage([])));
        TextExtractionResult result = await Create(client, maxCount: 16).ExtractAsync(
            TextExtractionInput.Numbered(string.Join('\n', Enumerable.Range(1, 16))), "extract");
        Assert.Equal(16, result.Artifacts.Count);
        Assert.Equal(17, client.Requests.Count);
    }

    [Fact]
    public async Task Loop_CumulativeRawLimitCountsDuplicatesAndAllowsFinalZero() {
        var client = new SequenceClient((_, ordinal, _) => Task.FromResult(
            ordinal <= 64 ? new ActionMessage([Call(1, $"call-{ordinal}")]) : new ActionMessage([])));
        Assert.Single((await Create(client, maxCount: 1, maxBytes: 1).ExtractAsync(
            TextExtractionInput.Numbered("x"), "extract")).Artifacts);
        Assert.Equal(65, client.Requests.Count);
        var endless = new SequenceClient((_, ordinal, _) => Task.FromResult(new ActionMessage([Call(1, $"c-{ordinal}")])));
        Assert.Equal(TextExtractionFailureKind.ToolCallLimitExceeded,
            (await Assert.ThrowsAsync<TextExtractionException>(() => Create(endless).ExtractAsync(
                TextExtractionInput.Numbered("x"), "extract").AsTask())).Kind);
        Assert.Equal(65, endless.Requests.Count);
    }

    [Fact]
    public async Task Loop_FailureAfterAcceptedCandidateDoesNotReturnPartialBatch() {
        var client = new SequenceClient((_, ordinal, _) => ordinal == 1
            ? Task.FromResult(new ActionMessage([Call(1, "first")]))
            : throw new IOException("provider fixture"));
        await Assert.ThrowsAsync<IOException>(() => Create(client).ExtractAsync(
            TextExtractionInput.Numbered("first"), "extract").AsTask());
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Loop_DuplicateToolCallIdAcrossRoundsFailsBeforeAdmission() {
        var client = new SequenceClient((_, _, _) => Task.FromResult(new ActionMessage([Call(1, "same-id")])));
        Assert.Equal(TextExtractionFailureKind.DuplicateToolCallId,
            (await Assert.ThrowsAsync<TextExtractionException>(() => Create(client).ExtractAsync(
                TextExtractionInput.Numbered("x"), "extract").AsTask())).Kind);
    }

    [Fact]
    public async Task Loop_DiagnosticBudgetIsCumulative() {
        var client = new SequenceClient((_, ordinal, _) => Task.FromResult(new ActionMessage([
            new ActionBlock.Text(new string('x', 33 * 1024)), Call(1, $"call-{ordinal}")])));
        Assert.Equal(TextExtractionFailureKind.CompletionOutputInvalid,
            (await Assert.ThrowsAsync<TextExtractionException>(() => Create(client).ExtractAsync(
                TextExtractionInput.Numbered("x"), "extract").AsTask())).Kind);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Loop_RawArgumentBudgetIsCumulativeAcrossResponses() {
        string arguments = new string(' ', 200 * 1024) + "{\"line\":1}";
        var client = new SequenceClient((_, ordinal, _) => Task.FromResult(new ActionMessage(
            Enumerable.Range(0, ordinal == 1 ? 4 : 2).Select(index => (ActionBlock)
                new ActionBlock.ToolCall(new RawToolCall("emit_range", $"c-{ordinal}-{index}", arguments))).ToArray())));
        Assert.Equal(TextExtractionFailureKind.ToolArgumentsLimitExceeded,
            (await Assert.ThrowsAsync<TextExtractionException>(() => Create(client).ExtractAsync(
                TextExtractionInput.Numbered("x"), "extract").AsTask())).Kind);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Admission_ExtraFullTextFieldCannotBypassRangeSchema() {
        var client = new SequenceClient((_, _, _) => Task.FromResult(new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall("emit_range", "bad-wire",
                """{"line":1,"body":"rewritten"}"""))])));
        Assert.Equal(TextExtractionFailureKind.ToolExecutionFailed,
            (await Assert.ThrowsAsync<TextExtractionException>(() => Create(client).ExtractAsync(
                TextExtractionInput.Numbered("original"), "extract").AsTask())).Kind);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Loop_DeadlineHasTypedFailureAndCallerCancellationRemainsCancellation() {
        var client = new SequenceClient(async (_, _, ct) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ActionMessage([]);
        });
        Assert.Equal(TextExtractionFailureKind.DeadlineExceeded,
            (await Assert.ThrowsAsync<TextExtractionException>(() => Create(client,
                deadline: TimeSpan.FromMilliseconds(30)).ExtractAsync(
                    TextExtractionInput.Numbered("x"), "extract").AsTask())).Kind);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).ExtractAsync(
            TextExtractionInput.Numbered("x"), "extract", caller.Token).AsTask());
    }

    [Fact]
    public async Task Loop_ExplicitLayoutFailureIsNeverSuccessfulZero() {
        var client = new SequenceClient((_, _, _) => Task.FromResult(new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall("report_extraction_problem", "bad-layout",
                """{"reason":"unrepresentable_layout"}"""))])));
        TextExtractionException exception = await Assert.ThrowsAsync<TextExtractionException>(() =>
            Create(client).ExtractAsync(TextExtractionInput.Numbered("request and body on same line"), "extract").AsTask());
        Assert.Equal(TextExtractionFailureKind.UnrepresentableLayout, exception.Kind);
        Assert.Equal("unrepresentable_layout", exception.DiagnosticReasonCode);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Admission_RejectsConflictAndReservesBudgetOnlyForNewOccurrences() {
        var trace = TextExtractionTrace.Create("test", null, null, null, "x");
        var session = new TextExtractionSession(TextExtractionInput.Numbered("x"), trace);
        Assert.Equal(TextExtractionAdmissionKind.Accepted,
            session.Admit(new Body("x"), "one", "a", 1, 1, 1, 1).Kind);
        Assert.Equal(TextExtractionAdmissionKind.AlreadyAccepted,
            session.Admit(new Body("x"), "one", "a", 1, 1, 1, 1).Kind);
        Assert.Equal("conflicting-candidate",
            session.Admit(new Body("x"), "one", "b", 1, 1, 1, 1).ReasonCode);
        Assert.Equal("materialized-budget-exceeded",
            session.Admit(new Body("x"), "two", "a", 1, 1, 1, 1).ReasonCode);
        var client = new SequenceClient((_, ordinal, _) => Task.FromResult(ordinal == 1
            ? new ActionMessage([Call(1, "a"), Call(2, "b")]) : new ActionMessage([])));
        Assert.Equal(2, (await Create(client).ExtractAsync(TextExtractionInput.Numbered("same\nsame"), "extract")).Artifacts.Count);
    }

    [Fact]
    public async Task Admission_ConcurrentInvocationsHaveSeparateSourcesDedupAndBudget() {
        var client = new SequenceClient(async (request, _, _) => {
            await Task.Yield();
            return request.TailMessages.Length == 1
                ? new ActionMessage([Call(1, "a")]) : new ActionMessage([]);
        });
        TextExtractor extractor = Create(client, maxCount: 1, maxBytes: 5);
        TextExtractionResult[] results = await Task.WhenAll(
            extractor.ExtractAsync(TextExtractionInput.Numbered("alpha"), "extract").AsTask(),
            extractor.ExtractAsync(TextExtractionInput.Numbered("beta"), "extract").AsTask());
        Assert.Equal(new[] { "alpha", "beta" }, results.Select(result =>
            Assert.IsType<TextExtractionArtifact<Body>>(Assert.Single(result.Artifacts)).Value.Text));
    }

    private static ActionBlock.ToolCall Call(int line, string id) => new(new RawToolCall(
        "emit_range", id, JsonSerializer.Serialize(new { line })));

    private static TextExtractor Create(ICompletionClient client, int maxCount = 64,
        int maxBytes = 1024 * 1024, TimeSpan? deadline = null) => new(
        "Extract complete source ranges.",
        TextExtractorToolSet.CreateWithProblemTool(TextExtractorArtifactTool.Create<RangeWire, Body>(
            "emit_range", (wire, session) => {
                string text = session.Input.Lines!.Slice(wire.Line, wire.Line);
                return session.Admit(new Body(text), wire.Line.ToString(), wire.Line.ToString(),
                    wire.Line, TextExtractorUtf8.GetByteCount(text), maxBytes, maxCount);
            })),
        new CompletionConnectionConfig("test", "test", "model", "test", "https://example.invalid/"),
        () => client, TextExtractionExecutionPolicy.UntilNoToolCalls, deadline);

    [Description("A source line.")]
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RangeWire {
        [Description("The one-based source line.")]
        [JsonPropertyName("line")]
        [Range(1, int.MaxValue)]
        public int Line { get; init; }
    }
    private sealed record Body(string Text);

    private sealed class SequenceClient(
        Func<CompletionRequest, int, CancellationToken, Task<ActionMessage>> next
    ) : ICompletionClient {
        public string Name => "line-loop-tests";
        public string ApiSpecId => "test";
        internal List<CompletionRequest> Requests { get; } = [];
        public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            int ordinal;
            lock (Requests) { Requests.Add(request); ordinal = Requests.Count; }
            ActionMessage message = await next(request, ordinal, cancellationToken);
            return new CompletionResult(message, CompletionDescriptor.From(this, request));
        }
    }
}

using Atelia.Completion.Abstractions;

namespace Atelia.MemoPod.Tests.Recall;

public sealed class MemoPodRecallOutputValidationTests {
    public enum InvalidBlockShape {
        Empty,
        TextOnly,
        EmptyTextOnly,
        ReasoningOnly,
        ToolAndText,
        ToolAndReasoning,
        TextAndTool,
        ReasoningAndTool,
        TwoTools,
        NullBlock,
        NullToolCall,
        WrongTool,
    }

    public enum InvalidEnvelopeShape {
        Incomplete,
        Failed,
        Errors,
        InvocationMismatch,
        NullResult,
        UnknownTermination,
    }

    public enum InvalidToolCallIdShape {
        Null,
        Empty,
        Whitespace,
        InvalidUtf16,
        TooLong,
    }

    public static TheoryData<string?> InvalidArguments { get; } = new() {
        null,
        string.Empty,
        " ",
        "{",
        "[]",
        "{}",
        "{\"MemoIds\":[]}",
        "{\"unknown\":[],\"memoIds\":[]}",
        "{\"memoIds\":[],\"unknown\":1}",
        "{\"memoIds\":[],\"memoIds\":[]}",
        "{\"memoIds\":null}",
        "{\"memoIds\":\"m1:00000001\"}",
        "{\"memoIds\":[1]}",
        "{\"memoIds\":[\"M1:00000001\"]}",
        "{\"memoIds\":[\"m1:00000000\"]}",
        "{\"memoIds\":[\"m1:1\"]}",
        "{\"memoIds\":[\"\ud800\"]}",
        "{\"memoIds\":[\"m1:00000001\",\"m1:00000001\"]}",
        "{\"memoIds\":[],}",
        "{\"memoIds\":[]/*comment*/}",
        "{\"memoIds\":[]}{}",
    };

    [Theory]
    [InlineData(InvalidBlockShape.Empty)]
    [InlineData(InvalidBlockShape.TextOnly)]
    [InlineData(InvalidBlockShape.EmptyTextOnly)]
    [InlineData(InvalidBlockShape.ReasoningOnly)]
    [InlineData(InvalidBlockShape.ToolAndText)]
    [InlineData(InvalidBlockShape.ToolAndReasoning)]
    [InlineData(InvalidBlockShape.TextAndTool)]
    [InlineData(InvalidBlockShape.ReasoningAndTool)]
    [InlineData(InvalidBlockShape.TwoTools)]
    [InlineData(InvalidBlockShape.NullBlock)]
    [InlineData(InvalidBlockShape.NullToolCall)]
    [InlineData(InvalidBlockShape.WrongTool)]
    public async Task MixedOrNonToolBlocksAreInvalidModelOutput(
        InvalidBlockShape shape
    ) {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var client = new FakeMemoRecallCompletionClient();
        client.Handler = (self, request, _) => {
            CompletionDescriptor descriptor = CompletionDescriptor.From(
                self,
                request
            );
            ActionBlock tool = self.ToolCall("{\"memoIds\":[]}");
            ActionBlock reasoning = new ActionBlock.TextReasoningBlock(
                "reasoning",
                descriptor
            );
            IReadOnlyList<ActionBlock> blocks = shape switch {
                InvalidBlockShape.Empty => Array.Empty<ActionBlock>(),
                InvalidBlockShape.TextOnly => [new ActionBlock.Text("text")],
                InvalidBlockShape.EmptyTextOnly => [
                    new ActionBlock.Text(string.Empty)
                ],
                InvalidBlockShape.ReasoningOnly => [reasoning],
                InvalidBlockShape.ToolAndText => [
                    tool,
                    new ActionBlock.Text("text")
                ],
                InvalidBlockShape.ToolAndReasoning => [tool, reasoning],
                InvalidBlockShape.TextAndTool => [
                    new ActionBlock.Text("text"),
                    tool
                ],
                InvalidBlockShape.ReasoningAndTool => [reasoning, tool],
                InvalidBlockShape.TwoTools => [tool, tool],
                InvalidBlockShape.NullBlock => [null!],
                InvalidBlockShape.NullToolCall => [
                    new ActionBlock.ToolCall(null!)
                ],
                InvalidBlockShape.WrongTool => [self.ToolCall(
                    "{\"memoIds\":[]}",
                    toolName: "other_tool"
                )],
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
            return Task.FromResult(self.Result(request, blocks));
        };

        await AssertInvalidOutputAndUnchanged(fixture, client);
    }

    [Theory]
    [InlineData(InvalidEnvelopeShape.Incomplete)]
    [InlineData(InvalidEnvelopeShape.Failed)]
    [InlineData(InvalidEnvelopeShape.Errors)]
    [InlineData(InvalidEnvelopeShape.InvocationMismatch)]
    [InlineData(InvalidEnvelopeShape.NullResult)]
    [InlineData(InvalidEnvelopeShape.UnknownTermination)]
    public async Task InvalidTerminalEnvelopeIsProviderFailure(
        InvalidEnvelopeShape shape
    ) {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var client = new FakeMemoRecallCompletionClient();
        client.Handler = (self, request, _) => {
            if (shape is InvalidEnvelopeShape.NullResult) {
                return Task.FromResult<CompletionResult>(null!);
            }
            CompletionTermination? termination = shape switch {
                InvalidEnvelopeShape.Incomplete =>
                    CompletionTermination.Incomplete(),
                InvalidEnvelopeShape.Failed => CompletionTermination.Failed(),
                InvalidEnvelopeShape.UnknownTermination =>
                    new CompletionTermination(
                        (CompletionTerminationKind)999
                    ),
                _ => null,
            };
            IReadOnlyList<string>? errors =
                shape is InvalidEnvelopeShape.Errors
                    ? ["provider detail must not escape"]
                    : null;
            CompletionDescriptor? invocation =
                shape is InvalidEnvelopeShape.InvocationMismatch
                    ? new CompletionDescriptor(
                        "other",
                        self.ApiSpecId,
                        request.ModelId
                    )
                    : null;
            return Task.FromResult(self.Result(
                request,
                [self.ToolCall("{\"memoIds\":[]}")],
                errors,
                termination,
                invocation: invocation
            ));
        };
        MemoPodFrozenPrompt prompt = fixture.Pod.FrozenPrompt;

        MemoRecallException failure =
            await Assert.ThrowsAsync<MemoRecallException>(() =>
                fixture.Pod.RecallAsync(
                    client,
                    "model",
                    "query",
                    MemoPodRecallFixture.Options()
                ));

        Assert.Equal(
            MemoRecallFailureKind.ProviderFailure,
            failure.FailureKind
        );
        Assert.DoesNotContain(
            "provider detail",
            failure.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(1, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        Assert.Same(prompt, fixture.Pod.FrozenPrompt);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task MalformedArgumentsAreInvalidModelOutput(
        string? rawArgumentsJson
    ) {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(
                    request,
                    [self.ToolCall(rawArgumentsJson)]
                )
            )
        };

        await AssertInvalidOutputAndUnchanged(fixture, client);
    }

    [Theory]
    [InlineData(InvalidToolCallIdShape.Null)]
    [InlineData(InvalidToolCallIdShape.Empty)]
    [InlineData(InvalidToolCallIdShape.Whitespace)]
    [InlineData(InvalidToolCallIdShape.InvalidUtf16)]
    [InlineData(InvalidToolCallIdShape.TooLong)]
    public async Task InvalidToolCallIdsAreInvalidModelOutput(
        InvalidToolCallIdShape shape
    ) {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        string? toolCallId = shape switch {
            InvalidToolCallIdShape.Null => null,
            InvalidToolCallIdShape.Empty => string.Empty,
            InvalidToolCallIdShape.Whitespace => " \t\r\n",
            InvalidToolCallIdShape.InvalidUtf16 => "\ud800",
            InvalidToolCallIdShape.TooLong => new string(
                'x',
                MemoPodRecallValidation.MaximumToolCallIdUtf8Bytes + 1
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(
                    request,
                    [self.ToolCall(
                        "{\"memoIds\":[]}",
                        toolCallId: toolCallId
                    )]
                )
            )
        };

        await AssertInvalidOutputAndUnchanged(fixture, client);
    }

    [Fact]
    public async Task MaximumToolCallIdUtf8LengthIsAccepted() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(
                    request,
                    [self.ToolCall(
                        "{\"memoIds\":[]}",
                        toolCallId: new string(
                            'x',
                            MemoPodRecallValidation
                                .MaximumToolCallIdUtf8Bytes
                        )
                    )]
                )
            )
        };

        MemoRecallResult result = await fixture.Pod.RecallAsync(
            client,
            "model",
            "query",
            MemoPodRecallFixture.Options()
        );

        Assert.Empty(result.Memos);
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task FailureCanRetryAgainstTheSameFrozenPromptEpoch() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        MemoPodFrozenPrompt epoch = fixture.Pod.FrozenPrompt;
        string hash = epoch.Sha256;
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(
                    request,
                    [self.ToolCall(
                        "{\"memoIds\":[]}",
                        toolName: "wrong_tool"
                    )]
                )
            )
        };

        await Assert.ThrowsAsync<MemoRecallException>(() =>
            fixture.Pod.RecallAsync(
                client,
                "model",
                "first query",
                MemoPodRecallFixture.Options()
            ));
        client.Handler = static (self, request, _) => Task.FromResult(
            self.Result(
                request,
                [self.ToolCall("{\"memoIds\":[]}")]
            )
        );

        MemoRecallResult result = await fixture.Pod.RecallAsync(
            client,
            "model",
            "second query",
            MemoPodRecallFixture.Options()
        );

        Assert.Empty(result.Memos);
        Assert.Equal(2, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        Assert.Same(epoch, fixture.Pod.FrozenPrompt);
        Assert.Equal(hash, fixture.Pod.FrozenPrompt.Sha256);
        Assert.Equal(
            ((ObservationMessage)client.Requests[0].PromptPrefix
                .SharedContextMessages[0]).Content,
            ((ObservationMessage)client.Requests[1].PromptPrefix
                .SharedContextMessages[0]).Content
        );
    }

    [Fact]
    public async Task ResultCountAndRawArgumentBoundsAreEnforced() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["first", "second"]
            );
        string[] invalidArguments = [
            "{\"memoIds\":[\"m1:00000001\",\"m1:00000002\"]}",
            "{\"memoIds\":[]}"
                + new string(
                    ' ',
                    MemoPodRecallValidation.MaximumToolArgumentsUtf8Bytes
                ),
        ];

        foreach (string arguments in invalidArguments) {
            var client = new FakeMemoRecallCompletionClient {
                Handler = (self, request, _) => Task.FromResult(
                    self.Result(request, [self.ToolCall(arguments)])
                )
            };

            MemoRecallException failure =
                await Assert.ThrowsAsync<MemoRecallException>(() =>
                    fixture.Pod.RecallAsync(
                        client,
                        "model",
                        "query",
                        MemoPodRecallFixture.Options(maxResults: 1)
                    ));
            Assert.Equal(
                MemoRecallFailureKind.InvalidModelOutput,
                failure.FailureKind
            );
            Assert.Equal(1, client.InvocationCount);
        }
    }

    private static async Task AssertInvalidOutputAndUnchanged(
        MemoPodRecallFixture fixture,
        FakeMemoRecallCompletionClient client
    ) {
        MemoPodFrozenPrompt prompt = fixture.Pod.FrozenPrompt;
        string hash = prompt.Sha256;

        MemoRecallException failure =
            await Assert.ThrowsAsync<MemoRecallException>(() =>
                fixture.Pod.RecallAsync(
                    client,
                    "model",
                    "query",
                    MemoPodRecallFixture.Options()
                ));

        Assert.Equal(
            MemoRecallFailureKind.InvalidModelOutput,
            failure.FailureKind
        );
        Assert.Equal(1, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        Assert.Equal(hash, fixture.Pod.FrozenPrompt.Sha256);
        Assert.Same(prompt, fixture.Pod.FrozenPrompt);
    }
}

using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public sealed class RewriteRecapBlockMaintainerTests {
    [Fact]
    public async Task SameFamilyMembers_UseSharedPrefixInputsAndDistinctTails() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(Updated("world"));
        client.Enqueue(Updated("self"));
        var world = new RewriteRecapBlockMaintainer(
            WorldUnderstandingRecapMaintainers.Default,
            client,
            "model"
        );
        var autobiography = new RewriteRecapBlockMaintainer(
            AutobiographicalRecapMaintainers.Default,
            client,
            "model"
        );
        var prior = new ContextHeaderPack();
        prior.Observation.Add("own", new ContextHeaderBlock("old-own"));
        prior.Action.Add("peer", new ContextHeaderBlock("old-peer"));
        var epoch = new RecapMaintenanceEpochInput(
            prior.Render(),
            [new ObservationMessage("new-history")],
            sourceId: "source"
        );

        await world.MaintainAsync(epoch, CancellationToken.None);
        await autobiography.MaintainAsync(epoch, CancellationToken.None);

        CompletionRequest first = client.Requests[0];
        CompletionRequest second = client.Requests[1];
        Assert.Equal(
            first.PromptPrefix.SystemPrompt,
            second.PromptPrefix.SystemPrompt
        );
        Assert.Same(
            first.PromptPrefix.OutputContract,
            second.PromptPrefix.OutputContract
        );
        Assert.Equal(
            Flatten(first.PromptPrefix.SharedContextMessages),
            Flatten(second.PromptPrefix.SharedContextMessages)
        );
        Assert.Equal(
            1,
            CountOccurrences(
                first.PromptPrefix.SharedContextMessages,
                "old-own"
            )
        );
        Assert.Equal(
            1,
            CountOccurrences(
                first.PromptPrefix.SharedContextMessages,
                "old-peer"
            )
        );
        Assert.Equal(
            1,
            CountOccurrences(
                first.PromptPrefix.SharedContextMessages,
                "new-history"
            )
        );
        Assert.DoesNotContain(
            WorldUnderstandingRecapMaintainers.Default.TaskInstruction,
            Flatten(first.PromptPrefix.SharedContextMessages)
        );
        Assert.NotEqual(
            Flatten(first.TailMessages),
            Flatten(second.TailMessages)
        );
    }

    [Fact]
    public async Task MaintainAsync_ParsesUpdatedAndKeepUnchanged() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(Updated("new content"));
        client.Enqueue(KeepUnchanged());
        var maintainer = new RewriteRecapBlockMaintainer(
            WorldUnderstandingRecapMaintainers.Default,
            client,
            "model"
        );
        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("history")]
        );

        var updated = Assert.IsType<RecapMaintenanceSuccess.Updated>(
            await maintainer.MaintainAsync(
                input,
                CancellationToken.None
            )
        );
        Assert.Equal("new content", updated.Content);
        Assert.Same(
            RecapMaintenanceSuccess.KeepUnchanged.Instance,
            await maintainer.MaintainAsync(
                input,
                CancellationToken.None
            )
        );
        Assert.All(
            client.InvocationOptions,
            static options => Assert.Equal(
                PromptCacheReuseHint.NoReuseExpected,
                options.PromptCacheReuseHint
            )
        );
    }

    [Fact]
    public async Task IncompleteCompletion_ThrowsSessionJournalException() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([]),
            Descriptor(request),
            termination: CompletionTermination.Incomplete("length")
        ));
        var maintainer = new RewriteRecapBlockMaintainer(
            WorldUnderstandingRecapMaintainers.Default,
            client,
            "model"
        );

        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            async () => await maintainer.MaintainAsync(
                new RecapMaintenanceEpochInput(
                    ContextHeaderSnapshot.Empty,
                    []
                ),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task CompletedCompletionWithErrorsIsRejected() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => new CompletionResult(
            Updated("ignored")(request).Message,
            Descriptor(request),
            errors: ["provider stream reported an error"]
        ));
        var maintainer = new RewriteRecapBlockMaintainer(
            WorldUnderstandingRecapMaintainers.Default,
            client,
            "model"
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await maintainer.MaintainAsync(
                new RecapMaintenanceEpochInput(
                    ContextHeaderSnapshot.Empty,
                    []
                ),
                CancellationToken.None
            )
        );
    }

    private static Func<CompletionRequest, CompletionResult> Updated(
        string content
    ) => request => Result(
        request,
        $"{{\"outcome\":\"updated\",\"content\":{System.Text.Json.JsonSerializer.Serialize(content)}}}"
    );

    private static Func<CompletionRequest, CompletionResult>
        KeepUnchanged() => request => Result(
            request,
            "{\"outcome\":\"keep-unchanged\",\"content\":null}"
        );

    private static CompletionResult Result(
        CompletionRequest request,
        string arguments
    ) => new(
        new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall(
                StructuredRecapMaintainerOutputProtocol.SubmitToolName,
                "call",
                arguments
            ))
        ]),
        Descriptor(request)
    );

    private static CompletionDescriptor Descriptor(
        CompletionRequest request
    ) => new("scripted", "test-v1", request.ModelId);

    private static int CountOccurrences(
        IReadOnlyList<IHistoryMessage> messages,
        string value
    ) {
        string text = Flatten(messages);
        int count = 0;
        for (int index = 0;
            (index = text.IndexOf(
                value,
                index,
                StringComparison.Ordinal
            )) >= 0;
            index += value.Length) {
            count++;
        }
        return count;
    }

    private static string Flatten(
        IEnumerable<IHistoryMessage> messages
    ) => string.Join(
        "\n",
        messages.Select(static message => message switch {
            ObservationMessage observation => observation.Content,
            ActionMessage action => action.GetFlattenedText(),
            _ => string.Empty
        })
    );

    private sealed class ScriptedCompletionClient : ICompletionClient {
        private readonly Queue<
            Func<CompletionRequest, CompletionResult>
        > _responses = new();

        internal List<CompletionRequest> Requests { get; } = [];
        internal List<CompletionInvocationOptions> InvocationOptions {
            get;
        } = [];

        public string Name => "scripted";
        public string ApiSpecId => "test-v1";

        internal void Enqueue(
            Func<CompletionRequest, CompletionResult> response
        ) => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "Explicit invocation options are required."
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            InvocationOptions.Add(invocationOptions);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}

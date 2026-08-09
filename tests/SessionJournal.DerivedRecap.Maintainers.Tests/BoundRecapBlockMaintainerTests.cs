using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public sealed class BoundRecapBlockMaintainerTests {
    [Fact]
    public async Task SameGroupMembers_UseSharedInputsAndDistinctTails() {
        var client = new ScriptedCompletionClient();
        client.Enqueue(Updated("world"));
        client.Enqueue(Updated("self"));
        var lane = new RecapExecutionLane(client, "model", 321);
        var groups = new RecapRuntimeGroupInterner();
        RecapRuntimeGroup group = groups.GetOrAdd(
            lane,
            BuiltInRecapMaintainerFamilies.Default
        );
        BoundRecapBlockMaintainer world = group.Bind(
            WorldUnderstandingRecapMaintainers.Default
        );
        BoundRecapBlockMaintainer autobiography = group.Bind(
            AutobiographicalRecapMaintainers.Default
        );
        var prior = new ContextHeaderPack();
        prior.Observation.Add("own", new ContextHeaderBlock("old-own"));
        prior.Action.Add("peer", new ContextHeaderBlock("old-peer"));
        var epoch = new RecapMaintenanceEpochInput(
            prior.Render(),
            [new ObservationMessage("new-history")],
            sourceId: "source"
        );

        IRecapMaintenanceGroupExecution groupExecution =
            world.CreateGroupExecution(epoch);
        await world.MaintainAsync(
            groupExecution,
            new ImmediateCallControl(),
            CancellationToken.None
        );
        await autobiography.MaintainAsync(
            groupExecution,
            new ImmediateCallControl(RecapMaintainerCallRole.Follower),
            CancellationToken.None
        );

        Assert.Same(world.RuntimeGroup, autobiography.RuntimeGroup);
        Assert.Same(group, world.RuntimeGroupAffinity);
        Assert.Same(group, autobiography.RuntimeGroupAffinity);
        CompletionRequest first = client.Requests[0];
        CompletionRequest second = client.Requests[1];
        Assert.Same(first.PromptPrefix, second.PromptPrefix);
        Assert.Equal("model", first.ModelId);
        Assert.Equal(321, first.MaxTokens);
        Assert.Equal(
            first.PromptPrefix.SystemPrompt,
            second.PromptPrefix.SystemPrompt
        );
        Assert.Same(
            first.PromptPrefix.OutputContract,
            group.Family.OutputProtocol.RequestContract
        );
        Assert.Equal(
            Flatten(first.PromptPrefix.SharedContextMessages),
            Flatten(second.PromptPrefix.SharedContextMessages)
        );
        Assert.Equal(1, CountOccurrences(
            first.PromptPrefix.SharedContextMessages,
            "old-own"
        ));
        Assert.Equal(1, CountOccurrences(
            first.PromptPrefix.SharedContextMessages,
            "old-peer"
        ));
        Assert.Equal(1, CountOccurrences(
            first.PromptPrefix.SharedContextMessages,
            "new-history"
        ));
        Assert.DoesNotContain(
            WorldUnderstandingRecapMaintainers.Default.TaskInstruction,
            Flatten(first.PromptPrefix.SharedContextMessages)
        );
        Assert.Contains(
            WorldUnderstandingRecapMaintainers.Default.TaskInstruction,
            Flatten(first.TailMessages)
        );
        Assert.Contains(
            AutobiographicalRecapMaintainers.Default.TaskInstruction,
            Flatten(second.TailMessages)
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
        BoundRecapBlockMaintainer maintainer = Bind(
            WorldUnderstandingRecapMaintainers.Default,
            client
        );
        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("history")]
        );

        var updated = Assert.IsType<RecapMaintenanceSuccess.Updated>(
            await InvokeAsync(maintainer, input)
        );
        Assert.Equal("new content", updated.Content);
        Assert.Same(
            RecapMaintenanceSuccess.KeepUnchanged.Instance,
            await InvokeAsync(maintainer, input)
        );
        Assert.All(
            client.InvocationOptions,
            static options => Assert.Equal(
                PromptCacheReuseHint.ReuseExpectedSoon,
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
        BoundRecapBlockMaintainer maintainer = Bind(
            WorldUnderstandingRecapMaintainers.Default,
            client
        );

        await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
            async () => await InvokeAsync(
                maintainer,
                new RecapMaintenanceEpochInput(
                    ContextHeaderSnapshot.Empty,
                    []
                )
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
        BoundRecapBlockMaintainer maintainer = Bind(
            WorldUnderstandingRecapMaintainers.Default,
            client
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await InvokeAsync(
                maintainer,
                new RecapMaintenanceEpochInput(
                    ContextHeaderSnapshot.Empty,
                    []
                )
            )
        );
    }

    private static ValueTask<RecapMaintenanceSuccess> InvokeAsync(
        BoundRecapBlockMaintainer maintainer,
        RecapMaintenanceEpochInput input
    ) => maintainer.MaintainAsync(
        maintainer.CreateGroupExecution(input),
        new ImmediateCallControl(),
        CancellationToken.None
    );

    private static BoundRecapBlockMaintainer Bind(
        RecapMaintainerDefinition definition,
        ICompletionClient client
    ) {
        var lane = new RecapExecutionLane(client, "model");
        RecapRuntimeGroup group = new RecapRuntimeGroupInterner()
            .GetOrAdd(lane, definition.Family);
        return group.Bind(definition);
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

    private sealed class ImmediateCallControl(
        RecapMaintainerCallRole role = RecapMaintainerCallRole.Leader
    ) : IRecapMaintainerCallControl {
        private bool _permitted;

        public RecapMaintainerCallRole Role { get; } = role;

        public ValueTask WaitForDispatchPermissionAsync(
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            _permitted = true;
            return ValueTask.CompletedTask;
        }

        public void MarkDispatchStarted() {
            Assert.True(_permitted);
        }

        public void MarkLaneAdmissionRequested() {
            Assert.True(_permitted);
        }
    }
}

using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public sealed class RecapRuntimeBindingTests : IDisposable {
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-runtime-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(_tempDirectory)) {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Interners_UseExactRouteLaneAndFamilyReferences() {
        var client = new ScriptedCompletionClient();
        var lanes = new RecapExecutionLaneInterner();
        var routeA = new EqualAffinity("same");
        var routeAEqualButDistinct = new EqualAffinity("same");
        RecapExecutionLane laneA = lanes.GetOrAdd(
            routeA,
            client,
            "model-a"
        );
        RecapExecutionLane laneAAgain = lanes.GetOrAdd(
            routeA,
            client,
            "model-a"
        );
        RecapExecutionLane distinctLane = lanes.GetOrAdd(
            routeAEqualButDistinct,
            client,
            "model-a"
        );

        Assert.Equal(routeA, routeAEqualButDistinct);
        Assert.NotSame(routeA, routeAEqualButDistinct);
        Assert.Same(laneA, laneAAgain);
        Assert.NotSame(laneA, distinctLane);
        Assert.True(laneA.MaxConcurrentCalls > 1);
        Assert.Same(client, laneA.RawClient);
        Assert.Throws<InvalidOperationException>(() => lanes.GetOrAdd(
            routeA,
            new ScriptedCompletionClient(),
            "model-a"
        ));
        Assert.Throws<InvalidOperationException>(() => lanes.GetOrAdd(
            routeA,
            client,
            "model-a",
            maxConcurrentCalls: 2
        ));

        RecapMaintainerFamilyDefinition family =
            BuiltInRecapMaintainerFamilies.Default;
        var otherFamily = new RecapMaintainerFamilyDefinition(
            "other",
            "other system",
            StructuredRecapMaintainerOutputProtocol.Shared
        );
        var groups = new RecapRuntimeGroupInterner();
        RecapRuntimeGroup group = groups.GetOrAdd(laneA, family);

        Assert.Same(group, groups.GetOrAdd(laneA, family));
        Assert.NotSame(
            group,
            groups.GetOrAdd(distinctLane, family)
        );
        Assert.NotSame(
            group,
            groups.GetOrAdd(laneA, otherFamily)
        );
    }

    [Fact]
    public void Binding_RejectsDifferentFamilyReference() {
        var copiedFamily = new RecapMaintainerFamilyDefinition(
            "copied",
            BuiltInRecapMaintainerFamilies.Default.SystemPrompt,
            StructuredRecapMaintainerOutputProtocol.Shared
        );
        RecapRuntimeGroup group = new RecapRuntimeGroupInterner()
            .GetOrAdd(
                new RecapExecutionLane(
                    new ScriptedCompletionClient(),
                    "model"
                ),
                copiedFamily
            );

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            group.Bind(WorldUnderstandingRecapMaintainers.Default)
        );

        Assert.Contains("exact same family instance", error.Message);
    }

    [Fact]
    public async Task SharedLoggedLane_AttributesEachMemberAndSource() {
        var client = new ScriptedCompletionClient();
        client.Enqueue("world");
        client.Enqueue("self");
        var connection = new CompletionConnectionConfig(
            "shared",
            "openai-chat",
            "shared-model",
            "openai-chat/strict",
            "http://localhost:8000/",
            ApiKey: "test-key",
            MaxTokens: 432
        );
        RecapExecutionLane lane = RecapExecutionLane.CreateWithLogging(
            client,
            connection,
            _tempDirectory,
            "test/recap"
        );
        RecapRuntimeGroup group = new RecapRuntimeGroupInterner()
            .GetOrAdd(lane, BuiltInRecapMaintainerFamilies.Default);
        BoundRecapBlockMaintainer world = group.Bind(
            WorldUnderstandingRecapMaintainers.Default
        );
        BoundRecapBlockMaintainer autobiography = group.Bind(
            AutobiographicalRecapMaintainers.Default
        );
        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("history")],
            sourceId: "epoch-source"
        );

        IRecapMaintenanceGroupExecution execution =
            world.CreateGroupExecution(input);
        await world.MaintainAsync(
            execution,
            new ImmediateCallControl(),
            CancellationToken.None
        );
        await autobiography.MaintainAsync(
            execution,
            new ImmediateCallControl(RecapMaintainerCallRole.Follower),
            CancellationToken.None
        );

        Assert.Same(group, world.RuntimeGroup);
        Assert.Same(group, autobiography.RuntimeGroup);
        Assert.Same(group, world.RuntimeGroupAffinity);
        Assert.Same(group, autobiography.RuntimeGroupAffinity);
        Assert.Same(client, lane.RawClient);
        Assert.Equal(2, lane.WrittenCallLogPaths.Count);
        Assert.All(client.Requests, request => {
            Assert.Equal("shared-model", request.ModelId);
            Assert.Equal(432, request.MaxTokens);
        });
        string[] expectedIds = [world.Id, autobiography.Id];
        string[] expectedBlocks = [
            world.Target.BlockKey,
            autobiography.Target.BlockKey
        ];
        for (int index = 0; index < 2; index++) {
            using JsonDocument log = JsonDocument.Parse(
                File.ReadAllText(lane.WrittenCallLogPaths[index])
            );
            JsonElement root = log.RootElement;
            Assert.Equal(
                "atelia.completion.call-log.v7",
                root.GetProperty("schema").GetString()
            );
            JsonElement context = root.GetProperty("context");
            Assert.Equal(
                expectedIds[index],
                context.GetProperty("maintainerId").GetString()
            );
            Assert.Equal(
                expectedBlocks[index],
                context.GetProperty("targetBlockId").GetString()
            );
            Assert.Equal(
                "epoch-source",
                context.GetProperty("sourceId").GetString()
            );
            Assert.Equal(
                "test/recap",
                context.GetProperty("command").GetString()
            );
        }
    }

    [Fact]
    public async Task LaneCapAdmitsQueuedLeaderBeforeFollower() {
        var client = new PriorityCompletionClient();
        var lane = new RecapExecutionLane(
            client,
            "model",
            maxConcurrentCalls: 1
        );
        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("history")]
        );
        CompletionPromptPrefix prefix = BuiltInRecapMaintainerFamilies
            .Default.CreatePromptPrefix(input);

        Task firstLeader = SendAsync(
            lane,
            prefix,
            "leader-a",
            RecapMaintainerCallRole.Leader
        );
        await client.WaitForNextStartAsync();

        Task queuedFollower = SendAsync(
            lane,
            prefix,
            "follower-a",
            RecapMaintainerCallRole.Follower
        );
        Task queuedLeader = SendAsync(
            lane,
            prefix,
            "leader-b",
            RecapMaintainerCallRole.Leader
        );

        client.ReleaseOne();
        await client.WaitForNextStartAsync();
        Assert.Equal(["leader-a", "leader-b"], client.StartOrder);

        client.ReleaseOne();
        await client.WaitForNextStartAsync();
        Assert.Equal(
            ["leader-a", "leader-b", "follower-a"],
            client.StartOrder
        );
        client.ReleaseOne();
        await Task.WhenAll(firstLeader, queuedFollower, queuedLeader);
    }

    [Fact]
    public async Task LeaderAdmissionSignalAndLaneRegistrationAreAtomic() {
        var client = new PriorityCompletionClient();
        var lane = new RecapExecutionLane(
            client,
            "model",
            maxConcurrentCalls: 1
        );
        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("history")]
        );
        CompletionPromptPrefix prefix = BuiltInRecapMaintainerFamilies
            .Default.CreatePromptPrefix(input);
        using var leaderControl = new BlockingAdmissionControl();

        Task leader = Task.Run(() => SendAsync(
            lane,
            prefix,
            "leader",
            leaderControl
        ));
        await leaderControl.AdmissionSignalEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5)
        );
        Task follower = Task.Run(() => SendAsync(
            lane,
            prefix,
            "follower",
            new ImmediateCallControl(RecapMaintainerCallRole.Follower)
        ));

        leaderControl.AllowLaneRegistration();
        await client.WaitForNextStartAsync();
        Assert.Equal(["leader"], client.StartOrder);

        client.ReleaseOne();
        await client.WaitForNextStartAsync();
        Assert.Equal(["leader", "follower"], client.StartOrder);
        client.ReleaseOne();
        await Task.WhenAll(leader, follower);
    }

    private static async Task SendAsync(
        RecapExecutionLane lane,
        CompletionPromptPrefix prefix,
        string label,
        RecapMaintainerCallRole role
    ) => await SendAsync(
        lane,
        prefix,
        label,
        new ImmediateCallControl(role)
    );

    private static async Task SendAsync(
        RecapExecutionLane lane,
        CompletionPromptPrefix prefix,
        string label,
        IRecapMaintainerCallControl callControl
    ) => _ = await lane.SendAsync(
        prefix,
        [new ObservationMessage(label)],
        new RecapCallContext(label, new ContextHeaderBlockPath(
            ContextHeaderCarrier.Observation,
            label
        )),
        callControl,
        CancellationToken.None
    );

    private sealed record EqualAffinity(string Value);

    private sealed class ImmediateCallControl(
        RecapMaintainerCallRole role = RecapMaintainerCallRole.Leader
    ) : IRecapMaintainerCallControl {
        public RecapMaintainerCallRole Role { get; } = role;

        public ValueTask WaitForDispatchPermissionAsync(
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void MarkDispatchStarted() {
        }

        public void MarkLaneAdmissionRequested() {
        }
    }

    private sealed class BlockingAdmissionControl
        : IRecapMaintainerCallControl,
          IDisposable {
        private readonly ManualResetEventSlim _allowRegistration = new();

        public RecapMaintainerCallRole Role =>
            RecapMaintainerCallRole.Leader;

        internal TaskCompletionSource AdmissionSignalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForDispatchPermissionAsync(
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void MarkLaneAdmissionRequested() {
            AdmissionSignalEntered.TrySetResult();
            Assert.True(_allowRegistration.Wait(TimeSpan.FromSeconds(5)));
        }

        public void MarkDispatchStarted() {
        }

        internal void AllowLaneRegistration() =>
            _allowRegistration.Set();

        public void Dispose() => _allowRegistration.Dispose();
    }

    private sealed class ScriptedCompletionClient : ICompletionClient {
        private readonly Queue<string> _contents = new();

        internal List<CompletionRequest> Requests { get; } = [];

        public string Name => "scripted/shared";
        public string ApiSpecId => "test-v1";

        internal void Enqueue(string content) => _contents.Enqueue(content);

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
            Requests.Add(request);
            string content = _contents.Dequeue();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall(
                        StructuredRecapMaintainerOutputProtocol
                            .SubmitToolName,
                        "call",
                        $"{{\"outcome\":\"updated\",\"content\":{JsonSerializer.Serialize(content)}}}"
                    ))
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class PriorityCompletionClient : ICompletionClient {
        private readonly System.Collections.Concurrent
            .ConcurrentQueue<string> _startOrder = new();
        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _release = new(0);

        public string Name => "priority";

        public string ApiSpecId => "test-v1";

        internal IReadOnlyList<string> StartOrder => [.. _startOrder];

        internal async Task WaitForNextStartAsync() => Assert.True(
            await _started.WaitAsync(TimeSpan.FromSeconds(5))
        );

        internal void ReleaseOne() => _release.Release();

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "Explicit invocation options are required."
        );

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            string? content = Assert.IsType<ObservationMessage>(
                Assert.Single(request.TailMessages)
            ).Content;
            Assert.NotNull(content);
            string label = content;
            _startOrder.Enqueue(label);
            _started.Release();
            await _release.WaitAsync(cancellationToken);
            return new CompletionResult(
                new ActionMessage([new ActionBlock.Text(label)]),
                CompletionDescriptor.From(this, request)
            );
        }
    }
}

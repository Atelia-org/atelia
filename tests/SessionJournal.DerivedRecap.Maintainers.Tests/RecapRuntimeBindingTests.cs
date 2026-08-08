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
        Assert.Same(client, laneA.RawClient);
        Assert.Throws<InvalidOperationException>(() => lanes.GetOrAdd(
            routeA,
            new ScriptedCompletionClient(),
            "model-a"
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

        await world.MaintainAsync(input, CancellationToken.None);
        await autobiography.MaintainAsync(input, CancellationToken.None);

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

    private sealed record EqualAffinity(string Value);

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
}

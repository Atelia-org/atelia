using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class RecapCliCompositionTests : IDisposable {
    private readonly string _callLogDirectory = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-cli-composition-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(_callLogDirectory)) {
            Directory.Delete(_callLogDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SingleRoute_BuiltInsShareLaneGroupAndRawClient() {
        var rawClient = new ScriptedCompletionClient();
        var connection = new CompletionConnectionConfig(
            "shared",
            "openai-chat",
            "shared-model",
            "openai-chat/strict",
            "http://localhost:8000/",
            ApiKey: "test-key"
        );

        RecapCliMaintainerComposition composition =
            RecapCliComposition.CreateMaintainers(
                RecapMaintainerProfileCatalog.BuiltIn,
                connection,
                rawClient,
                _callLogDirectory,
                "test/cli-recap"
            );
        BoundRecapBlockMaintainer world = Resolve(
            composition.Registry,
            RecapMaintainerProfileCatalog.WorldUnderstandingRewrite
        );
        BoundRecapBlockMaintainer autobiography = Resolve(
            composition.Registry,
            RecapMaintainerProfileCatalog.AutobiographicalRewrite
        );

        RecapExecutionLane lane = Assert.Single(composition.Lanes);
        Assert.Same(lane, world.RuntimeGroup.Lane);
        Assert.Same(lane, autobiography.RuntimeGroup.Lane);
        Assert.Same(world.RuntimeGroup, autobiography.RuntimeGroup);
        Assert.Same(rawClient, lane.RawClient);

        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            [new ObservationMessage("history")]
        );
        await world.MaintainAsync(input, CancellationToken.None);
        await autobiography.MaintainAsync(input, CancellationToken.None);

        Assert.Equal(2, rawClient.CallCount);
        Assert.Equal(2, lane.WrittenCallLogPaths.Count);
    }

    private static BoundRecapBlockMaintainer Resolve(
        IRecapBlockMaintainerRegistry registry,
        string profileName
    ) {
        RecapMaintainerProfileDescriptor descriptor =
            RecapMaintainerProfileCatalog.BuiltIn.Resolve(profileName);
        Assert.True(registry.TryResolve(
            descriptor.MaintainerId,
            descriptor.Target,
            descriptor.CapabilityFingerprint,
            out IRecapBlockMaintainer maintainer
        ));
        return Assert.IsType<BoundRecapBlockMaintainer>(maintainer);
    }

    private sealed class ScriptedCompletionClient : ICompletionClient {
        internal int CallCount { get; private set; }

        public string Name => "scripted/cli-shared";
        public string ApiSpecId => "test-v1";

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
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall(
                        StructuredRecapMaintainerOutputProtocol
                            .SubmitToolName,
                        $"call-{CallCount}",
                        $"{{\"outcome\":\"updated\",\"content\":\"content-{CallCount}\"}}"
                    ))
                ]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}

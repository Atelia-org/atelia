using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public sealed class RecapMaintainerFamilyContractTests {
    [Fact]
    public void EpochInput_FreezesOrderedHistorySnapshot() {
        var source = new List<IHistoryMessage> {
            new ObservationMessage("history")
        };
        var input = new RecapMaintenanceEpochInput(
            ContextHeaderSnapshot.Empty,
            source,
            sourceId: "source",
            estimatedTokens: 42
        );
        source.Clear();

        Assert.Single(input.HistoryMessages);
        Assert.Equal("source", input.SourceId);
        Assert.Equal((ulong)42, input.EstimatedTokens);
        Assert.Throws<ArgumentException>(() =>
            new RecapMaintenanceEpochInput(
                ContextHeaderSnapshot.Empty,
                new IHistoryMessage[] { null! }
            )
        );
    }

    [Fact]
    public void BuiltIns_ShareFamilyAndOutputProtocolByReference() {
        RecapMaintainerDefinition world =
            WorldUnderstandingRecapMaintainers.Default;
        RecapMaintainerDefinition autobiography =
            AutobiographicalRecapMaintainers.Default;

        Assert.Same(world.Family, autobiography.Family);
        Assert.Same(
            world.Family.OutputProtocol,
            autobiography.Family.OutputProtocol
        );
        Assert.Same(
            world.Family.OutputProtocol.RequestContract,
            autobiography.Family.OutputProtocol.RequestContract
        );
        Assert.StartsWith(
            "你负责维护共享 recap pack",
            world.Family.SystemPrompt
        );
        Assert.StartsWith(
            "你是Galatea的认知制图师",
            world.TaskInstruction
        );
        Assert.StartsWith(
            "你是Galatea的代笔人",
            autobiography.TaskInstruction
        );
    }

    [Fact]
    public void EnglishMembers_ShareASeparateEnglishFamily() {
        Assert.Same(
            WorldUnderstandingRecapMaintainers.English.Family,
            AutobiographicalRecapMaintainers.English.Family
        );
        Assert.NotSame(
            WorldUnderstandingRecapMaintainers.English.Family,
            WorldUnderstandingRecapMaintainers.Default.Family
        );
        Assert.Same(
            WorldUnderstandingRecapMaintainers.English.Family
                .OutputProtocol,
            WorldUnderstandingRecapMaintainers.Default.Family
                .OutputProtocol
        );
    }

    [Fact]
    public void Catalog_RejectsCopiedSemanticFamilyInstance() {
        RecapMaintainerOutputProtocol protocol =
            StructuredRecapMaintainerOutputProtocol.Shared;
        var firstFamily = new RecapMaintainerFamilyDefinition(
            "first",
            "same system",
            protocol
        );
        var copiedFamily = new RecapMaintainerFamilyDefinition(
            "copy",
            "same system",
            protocol
        );
        Assert.Equal(
            firstFamily.SemanticFingerprint,
            copiedFamily.SemanticFingerprint
        );

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RecapMaintainerProfileCatalog([
                Descriptor("first", Definition("one", firstFamily)),
                Descriptor("second", Definition("two", copiedFamily))
            ])
        );

        Assert.Contains(
            "distinct family instances",
            error.Message
        );
    }

    [Fact]
    public void Capability_ComesFromFamilyAndTaskButNotRuntimeRoute() {
        RecapMaintainerDefinition original = Definition(
            "member",
            new RecapMaintainerFamilyDefinition(
                "family",
                "system",
                StructuredRecapMaintainerOutputProtocol.Shared
            ),
            "task"
        );
        RecapMaintainerDefinition changedTask = Definition(
            "member",
            original.Family,
            "different task"
        );
        RecapMaintainerDefinition changedFamily = Definition(
            "member",
            new RecapMaintainerFamilyDefinition(
                "family-next",
                "different system",
                StructuredRecapMaintainerOutputProtocol.Shared
            ),
            "task"
        );
        var changedOutputContract = new CompletionOutputContract(
            [
                new ToolDefinition(
                    "different.submit",
                    "Different protocol.",
                    new ToolSchema.Object()
                )
            ],
            CompletionToolChoice.Auto
        );
        RecapMaintainerDefinition changedOutput = Definition(
            "member",
            new RecapMaintainerFamilyDefinition(
                "different-output",
                "system",
                new TestOutputProtocol(changedOutputContract)
            ),
            "task"
        );
        var client = new NoCallCompletionClient();
        var routeA = new RewriteRecapBlockMaintainer(
            original,
            client,
            "model-a"
        );
        var routeB = new RewriteRecapBlockMaintainer(
            original,
            client,
            "model-b"
        );

        Assert.Equal(
            routeA.CapabilityFingerprint,
            routeB.CapabilityFingerprint
        );
        Assert.NotEqual(
            original.CapabilityFingerprint,
            changedTask.CapabilityFingerprint
        );
        Assert.NotEqual(
            original.CapabilityFingerprint,
            changedFamily.CapabilityFingerprint
        );
        Assert.NotEqual(
            original.CapabilityFingerprint,
            changedOutput.CapabilityFingerprint
        );
    }

    [Fact]
    public void MemberAndRewriteApisHaveNoPrivatePromptProtocolOverrides() {
        Assert.DoesNotContain(
            typeof(RecapMaintainerDefinition).GetProperties(),
            static property => property.Name is
                "SystemPrompt" or "OutputProtocol" or "Tools"
        );
        var constructor = Assert.Single(
            typeof(RewriteRecapBlockMaintainer).GetConstructors()
        );
        Assert.Equal(
            [
                typeof(RecapMaintainerDefinition),
                typeof(ICompletionClient),
                typeof(string)
            ],
            constructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
        );
    }

    [Fact]
    public void OutputContractFingerprint_CoversOrderedSchemaAndPolicy() {
        ToolDefinition tool = new(
            "submit",
            "Submit.",
            new ToolSchema.Object([
                new ToolSchema.Property(
                    "value",
                    new ToolSchema.Value(
                        ToolParamType.String,
                        minLength: 1
                    ),
                    true
                )
            ])
        );
        var auto = new CompletionOutputContract(
            [tool],
            CompletionToolChoice.Auto
        );
        var required = new CompletionOutputContract(
            [tool],
            CompletionToolChoice.RequiredNamed("submit")
        );
        var changedSchema = new CompletionOutputContract(
            [
                new ToolDefinition(
                    "submit",
                    "Submit.",
                    new ToolSchema.Object()
                )
            ],
            CompletionToolChoice.Auto
        );

        Assert.Matches(
            "^sha256:[0-9a-f]{64}$",
            auto.SemanticFingerprint
        );
        Assert.NotEqual(
            auto.SemanticFingerprint,
            required.SemanticFingerprint
        );
        Assert.NotEqual(
            auto.SemanticFingerprint,
            changedSchema.SemanticFingerprint
        );
    }

    private static RecapMaintainerProfileDescriptor Descriptor(
        string profile,
        RecapMaintainerDefinition definition
    ) => new(profile, $"recap.{profile}", definition);

    private static RecapMaintainerDefinition Definition(
        string id,
        RecapMaintainerFamilyDefinition family,
        string task = "task"
    ) => new(
        RewriteRecapBlockMaintainer.ImplementationId,
        id,
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.Observation,
            $"target.{id}"
        ),
        family,
        task
    );

    private sealed class NoCallCompletionClient : ICompletionClient {
        public string Name => "no-call";
        public string ApiSpecId => "test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException();
    }

    private sealed class TestOutputProtocol(
        CompletionOutputContract contract
    ) : RecapMaintainerOutputProtocol(
        "test-output-protocol.v1",
        contract
    ) {
        public override RecapMaintenanceSuccess ParseAndValidate(
            CompletionResult result
        ) => throw new NotSupportedException();
    }
}

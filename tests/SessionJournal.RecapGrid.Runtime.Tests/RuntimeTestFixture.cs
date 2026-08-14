using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

internal static class RuntimeTestFixture {
    internal static FrozenRowBatch Batch(
        int columnCount = 1,
        string? semanticModelId = null,
        string runtimeProtocolId = RecapRewriterProtocolV1.RuntimeProtocolId,
        string inputProtocolId = RecapRewriterProtocolV1.InputProtocolId,
        IReadOnlyList<IHistoryMessage>? history = null,
        int maxContentUtf8Bytes = 16 * 1024,
        bool distinctFamilies = false,
        bool distinctSemanticModels = false,
        int semanticModelGroupSize = 0,
        IReadOnlyList<string>? runtimeProtocolIds = null,
        FamilyToolChoice toolChoice = FamilyToolChoice.Required,
        bool? allowParallel = false,
        bool includeAdditionalTool = false,
        string terminalToolName = RecapRewriterProtocolV1.TerminalToolName,
        bool includeSchemaDescription = false
    ) {
        FamilyDefinition[] families = [.. Enumerable.Range(0, columnCount)
            .Select(index => Family(
                inputProtocolId,
                distinctFamilies ? $"-{index}" : string.Empty,
                toolChoice,
                allowParallel,
                includeAdditionalTool,
                terminalToolName,
                includeSchemaDescription
            ))];
        var definitions = new MaintainerDefinitionRevision[columnCount];
        for (int index = 0; index < columnCount; index++) {
            var logical = new LogicalColumnId($"case.column-{index}");
            definitions[index] = MaintainerDefinitionRevision.Create(
                logical,
                families[index].Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    $"column-{index}"
                ),
                new MaintainerCapabilitySpec(
                    runtimeProtocolIds?[index] ?? runtimeProtocolId,
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1,
                    semanticModelGroupSize > 0
                        ? $"semantic-{index / semanticModelGroupSize}"
                        : distinctSemanticModels
                            ? $"semantic-{index}"
                            : semanticModelId
                ),
                new MaintainerDeclarativeSpec(
                    $"Question {index}",
                    $"Maintain question {index} literally."
                ),
                maxContentUtf8Bytes
            );
        }
        return Batch(families, definitions, history);
    }

    internal static FrozenRowBatch Batch(
        FamilyDefinition family,
        IReadOnlyList<MaintainerDefinitionRevision> definitions,
        IReadOnlyList<IHistoryMessage>? history = null
    ) {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(definitions);
        return Batch(
            Enumerable.Repeat(family, definitions.Count).ToArray(),
            definitions.ToArray(),
            history
        );
    }

    private static FrozenRowBatch Batch(
        FamilyDefinition[] families,
        MaintainerDefinitionRevision[] definitions,
        IReadOnlyList<IHistoryMessage>? history
    ) {
        BuildTargetColumn[] columns = [.. definitions.Select(static definition =>
            new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest
            ))];
        var timelineId = new TimelineId(new string('1', 32));
        var rowId = new HistoryRowId(new string('2', 64));
        BuildTarget target = BuildTarget.Create(columns);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineId,
            rowId,
            target
        );
        HistorySegmentDescriptor descriptor = Descriptor(timelineId, rowId);
        RowBuildAssignment[] assignments = [.. definitions.Select(
            definition => (RowBuildAssignment)new RowBuildAssignment.Evaluate(
                definition.LogicalColumnId,
                EvaluationKey.Create(
                    descriptor.DescriptorDigest,
                    definition.Digest,
                    PriorInputReference.FirstRow.Value
                )
            ))];
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            Coordinate(recipe, descriptor, previousView: null),
            PriorInputReference.FirstRow.Value,
            assignments
        );
        SessionHistoryPlanningWindow window = Window(history ?? [
            new ObservationMessage("visible history")
        ]);
        var segment = new HistorySegmentContent(descriptor, window);
        FrozenRecapCellWork[] work = [.. assignments.Select((assignment, ordinal) => {
            var evaluate = (RowBuildAssignment.Evaluate)assignment;
            return new FrozenRecapCellWork(
                ordinal,
                evaluate.LogicalColumnId,
                evaluate.EvaluationKey,
                definitions[ordinal],
                families[ordinal]
            );
        })];
        RefId refId = descriptor.RefId;
        EventAddress rawHead = descriptor.EndInclusive;
        var timelineHead = new TimelineHeadRef(
            timelineId,
            refId,
            rowId,
            new string('3', 64),
            rawHead,
            1,
            new string('4', 64),
            generation: 1
        );
        var controlHead = new ControlHeadRef(
            new ControlInstanceId(new string('4', 32)),
            refId,
            timelineId,
            generation: 1,
            new ControlStateDigest(new string('5', 64)),
            recipe.Digest
        );
        var storeIdentity = new RecapGridStoreIdentity(
            new RecapGridStoreInstanceId(new string('6', 32)),
            schemaVersion: 1
        );
        return new FrozenRowBatch(
            timelineHead,
            controlHead,
            storeIdentity,
            recipe,
            segment,
            spec,
            previousView: null,
            previousCells: Array.Empty<RecapCellArtifact>(),
            priorProjection: null,
            work
        );
    }

    internal static FamilyDefinition Family(
        string inputProtocolId = RecapRewriterProtocolV1.InputProtocolId,
        string systemPromptSuffix = "",
        FamilyToolChoice toolChoice = FamilyToolChoice.Required,
        bool? allowParallel = false,
        bool includeAdditionalTool = false,
        string terminalToolName = RecapRewriterProtocolV1.TerminalToolName,
        bool includeSchemaDescription = false
    ) {
        FamilyToolDefinition exact = RecapRewriterProtocolV1
            .CreateTerminalTool("Submit the maintained content.");
        FamilyToolDefinition terminal = string.Equals(
            terminalToolName,
            RecapRewriterProtocolV1.TerminalToolName,
            StringComparison.Ordinal
        ) ? exact : new FamilyToolDefinition(
            terminalToolName,
            exact.Description,
            exact.InputSchema
        );
        if (includeSchemaDescription) {
            terminal = new FamilyToolDefinition(
                terminal.Name,
                terminal.Description,
                new FamilyObjectInputSchema(
                    terminal.InputSchema.Properties,
                    description: "Schema drift."
                )
            );
        }
        FamilyToolDefinition[] tools = includeAdditionalTool
            ? [
                terminal,
                new FamilyToolDefinition(
                    "ordinary-tool",
                    "This runtime must not dispatch ordinary tools.",
                    new FamilyObjectInputSchema([])
                )
            ]
            : [terminal];
        return FamilyDefinition.Create(
            "Maintain the inquiry." + systemPromptSuffix,
            tools,
            new FamilyOutputProtocol(
                RecapRewriterProtocolV1.OutputProtocolId,
                terminalToolName,
                toolChoice,
                allowParallel
            ),
            new FamilyInputRenderingProtocol(
                inputProtocolId,
                RecapRewriterProtocolV1.PriorProjectionSchemaId,
                RecapRewriterProtocolV1.HistorySegmentRenderingSchemaId
            )
        );
    }

    internal static CompletionResult Updated(
        CompletionRequest request,
        IRecapCompletionInvoker invoker,
        string content = "updated content"
    ) => Result(
        request,
        invoker,
        $"{{\"outcome\":\"updated\",\"content\":{System.Text.Json.JsonSerializer.Serialize(content)}}}"
    );

    internal static CompletionResult Result(
        CompletionRequest request,
        IRecapCompletionInvoker invoker,
        string arguments,
        IReadOnlyList<ActionBlock>? blocks = null
    ) => new(
        new ActionMessage(blocks ?? [new ActionBlock.ToolCall(
            new RawToolCall("submit", "call-1", arguments)
        )]),
        new CompletionDescriptor(
            invoker.ProviderId,
            invoker.ApiSpecId,
            request.ModelId
        )
    );

    internal static RecapCompletionRoute Route(
        FrozenRowBatch batch,
        IRecapCompletionInvoker invoker,
        int maximumConcurrency = 4,
        TimeSpan? dispatchTimeout = null,
        int workIndex = 0
    ) {
        FrozenRecapCellWork work = batch.OrderedMissingWork[workIndex];
        return RecapCompletionRoute.Create(
            new RecapCompletionRouteKey(
                work.Family.Digest,
                work.Definition.Capability.RuntimeProtocolId,
                work.Definition.Capability.SemanticModelId
            ),
            "test-model",
            invoker,
            RecapCompletionResourceOwnership.Owned,
            maximumConcurrency,
            dispatchTimeout ?? TimeSpan.FromSeconds(5)
        );
    }

    internal static FrozenRowBatch BatchWithPrior(
        RecapCellOutcome priorOutcome = RecapCellOutcome.Updated,
        string priorContent = "prior content"
    ) {
        FamilyDefinition family = Family();
        var logical = new LogicalColumnId("case.column-0");
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                logical,
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "column-0"
                ),
                new MaintainerCapabilitySpec(
                    RecapRewriterProtocolV1.RuntimeProtocolId,
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1
                ),
                new MaintainerDeclarativeSpec(
                    "Question 0",
                    "Maintain question 0 literally."
                ),
                16 * 1024
            );
        var timelineId = new TimelineId(new string('1', 32));
        var priorRowId = new HistoryRowId(new string('9', 64));
        var currentRowId = new HistoryRowId(new string('2', 64));
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(logical, definition.Digest)
        ]);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineId,
            currentRowId,
            target
        );
        HistorySegmentDescriptor priorDescriptor = Descriptor(
            timelineId,
            priorRowId,
            digestCharacter: 'a',
            previousRowId: null
        );
        EvaluationKey priorKey = EvaluationKey.Create(
            priorDescriptor.DescriptorDigest,
            definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        RowBuildSpec priorSpec = RowBuildSpec.CreateFull(
            recipe,
            Coordinate(recipe, priorDescriptor, previousView: null),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(logical, priorKey)]
        );
        RecapCellArtifact priorCell = RecapCellArtifact.Create(
            logical,
            definition.Digest,
            priorKey,
            priorOutcome,
            priorContent,
            definition.MaxContentUtf8Bytes
        );
        RecapRowView previousView = RecapRowView.Create(
            priorSpec,
            [priorCell]
        );
        PriorInputProjection projection = PriorInputProjection.Create([
            new PriorProjectedContent(logical, priorCell.ContentDigest)
        ]);
        var priorReference = new PriorInputReference.Projection(
            projection.Digest
        );
        HistorySegmentDescriptor currentDescriptor = Descriptor(
            timelineId,
            currentRowId,
            digestCharacter: '8',
            previousRowId: priorRowId
        );
        EvaluationKey currentKey = EvaluationKey.Create(
            currentDescriptor.DescriptorDigest,
            definition.Digest,
            priorReference
        );
        RowBuildSpec currentSpec = RowBuildSpec.CreateNormal(
            recipe,
            Coordinate(recipe, currentDescriptor, previousView),
            priorReference,
            [new RowBuildAssignment.Evaluate(logical, currentKey)]
        );
        var segment = new HistorySegmentContent(
            currentDescriptor,
            Window([new ObservationMessage("visible history")])
        );
        var timelineHead = new TimelineHeadRef(
            timelineId,
            currentDescriptor.RefId,
            currentRowId,
            new string('3', 64),
            currentDescriptor.EndInclusive,
            2,
            new string('4', 64),
            generation: 2
        );
        var controlHead = new ControlHeadRef(
            new ControlInstanceId(new string('4', 32)),
            currentDescriptor.RefId,
            timelineId,
            generation: 1,
            new ControlStateDigest(new string('5', 64)),
            recipe.Digest
        );
        return new FrozenRowBatch(
            timelineHead,
            controlHead,
            new RecapGridStoreIdentity(
                new RecapGridStoreInstanceId(new string('6', 32)),
                1
            ),
            recipe,
            segment,
            currentSpec,
            previousView,
            [priorCell],
            projection,
            [new FrozenRecapCellWork(
                0,
                logical,
                currentKey,
                definition,
                family
            )]
        );
    }

    internal static FrozenRowBatch OverlayBatchWithNewColumn() {
        FamilyDefinition family = Family();
        var priorLogical = new LogicalColumnId("case.prior");
        var newLogical = new LogicalColumnId("case.new");
        MaintainerDefinitionRevision priorDefinition = Definition(
            family,
            priorLogical,
            "prior"
        );
        MaintainerDefinitionRevision newDefinition = Definition(
            family,
            newLogical,
            "new"
        );
        var timelineId = new TimelineId(new string('1', 32));
        var priorRowId = new HistoryRowId(new string('9', 64));
        var currentRowId = new HistoryRowId(new string('2', 64));
        GridBuildRecipe baseRecipe = GridBuildRecipe.CreateFull(
            timelineId,
            priorRowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    priorLogical,
                    priorDefinition.Digest
                )
            ])
        );
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            baseRecipe,
            currentRowId,
            BuildTarget.Create([
                new BuildTargetColumn(
                    priorLogical,
                    priorDefinition.Digest
                ),
                new BuildTargetColumn(
                    newLogical,
                    newDefinition.Digest
                )
            ]),
            [newLogical]
        );
        HistorySegmentDescriptor priorDescriptor = Descriptor(
            timelineId,
            priorRowId,
            digestCharacter: 'a'
        );
        EvaluationKey priorKey = EvaluationKey.Create(
            priorDescriptor.DescriptorDigest,
            priorDefinition.Digest,
            PriorInputReference.FirstRow.Value
        );
        RowBuildSpec priorSpec = RowBuildSpec.CreateFull(
            baseRecipe,
            Coordinate(baseRecipe, priorDescriptor, previousView: null),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(priorLogical, priorKey)]
        );
        RecapCellArtifact priorCell = RecapCellArtifact.Create(
            priorLogical,
            priorDefinition.Digest,
            priorKey,
            RecapCellOutcome.Updated,
            "prior content",
            priorDefinition.MaxContentUtf8Bytes
        );
        RecapRowView previousView = RecapRowView.Create(
            priorSpec,
            [priorCell]
        );
        PriorInputProjection projection = PriorInputProjection.Create([
            new PriorProjectedContent(
                priorLogical,
                priorCell.ContentDigest
            )
        ]);
        var priorReference = new PriorInputReference.Projection(
            projection.Digest
        );
        HistorySegmentDescriptor descriptor = Descriptor(
            timelineId,
            currentRowId,
            digestCharacter: '8',
            previousRowId: priorRowId
        );
        EvaluationKey newKey = EvaluationKey.Create(
            descriptor.DescriptorDigest,
            newDefinition.Digest,
            priorReference
        );
        EvaluationKey currentBaseKey = EvaluationKey.Create(
            descriptor.DescriptorDigest,
            priorDefinition.Digest,
            priorReference
        );
        RecapCellArtifact currentBaseCell = RecapCellArtifact.Create(
            priorLogical,
            priorDefinition.Digest,
            currentBaseKey,
            RecapCellOutcome.Updated,
            "current base content",
            priorDefinition.MaxContentUtf8Bytes
        );
        RowBuildSpec spec = RowBuildSpec.CreateOverlayBootstrap(
            overlay,
            Coordinate(overlay, descriptor, previousView),
            priorReference,
            [
                new RowBuildAssignment.Reuse(
                    priorLogical,
                    currentBaseCell
                ),
                new RowBuildAssignment.Evaluate(newLogical, newKey)
            ]
        );
        return new FrozenRowBatch(
            new TimelineHeadRef(
                timelineId,
                descriptor.RefId,
                currentRowId,
                new string('3', 64),
                descriptor.EndInclusive,
                2,
                new string('4', 64),
                2
            ),
            new ControlHeadRef(
                new ControlInstanceId(new string('4', 32)),
                descriptor.RefId,
                timelineId,
                1,
                new ControlStateDigest(new string('5', 64)),
                overlay.Digest
            ),
            new RecapGridStoreIdentity(
                new RecapGridStoreInstanceId(new string('6', 32)),
                1
            ),
            overlay,
            new HistorySegmentContent(
                descriptor,
                Window([new ObservationMessage("visible history")])
            ),
            spec,
            previousView,
            [priorCell],
            projection,
            [new FrozenRecapCellWork(
                1,
                newLogical,
                newKey,
                newDefinition,
                family
            )]
        );
    }

    private static HistorySegmentDescriptor Descriptor(
        TimelineId timelineId,
        HistoryRowId rowId,
        char digestCharacter = '8',
        HistoryRowId? previousRowId = null
    ) {
        SessionContextAnchorSetupReferences setups = Setups();
        return new HistorySegmentDescriptor(
            timelineId,
            new string('3', 64),
            rowId,
            previousRowId,
            new RefId(1),
            Address(10),
            Address(20),
            setups,
            setups,
            "test-estimator-v1",
            new HistoryLoadUnit(1),
            new HistoryLoadUnit(1),
            rawEventCount: 1,
            measuredRenderedUtf8Bytes: 1,
            new string('7', 64),
            new HistorySegmentDescriptorDigest(
                new string(digestCharacter, 64)
            )
        );
    }

    private static RowViewCoordinate Coordinate(
        GridBuildRecipe recipe,
        HistorySegmentDescriptor descriptor,
        RecapRowView? previousView
    ) => new(
        descriptor.RefId,
        descriptor.TimelineId,
        descriptor.RowId,
        descriptor.DescriptorDigest,
        recipe.Digest,
        recipe.Target.Digest,
        descriptor.PreviousRowId,
        previousView?.Digest,
        recipe.Kind == GridBuildRecipeKind.Full
            || recipe.BootstrapThroughRowId == descriptor.RowId
    );

    private static SessionHistoryPlanningWindow Window(
        IReadOnlyList<IHistoryMessage> messages
    ) {
        EventAddress start = Address(10);
        EventAddress end = Address(20);
        SessionContextAnchorSetupReferences setups = Setups();
        return new SessionHistoryPlanningWindow(
            end,
            start,
            setups,
            setups,
            [end],
            [.. messages.Select(message => new SessionHistoryPlanningUnit(
                message,
                end,
                end
            ))],
            [new SessionHistoryPlanningBoundary(end, messages.Count)],
            new Dictionary<EventAddress, SessionContextAnchorSetupReferences> {
                [end] = setups
            },
            new SessionHistoryPlanningDiagnostics(0, 0, 0, 0)
        );
    }

    private static SessionContextAnchorSetupReferences Setups() => new(
        new SessionContextSetupReference(
            Address(1),
            1,
            new string('a', 64)
        ),
        new SessionContextSetupReference(
            Address(2),
            1,
            new string('b', 64)
        )
    );

    private static MaintainerDefinitionRevision Definition(
        FamilyDefinition family,
        LogicalColumnId logical,
        string blockKey
    ) => MaintainerDefinitionRevision.Create(
        logical,
        family.Digest,
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            blockKey
        ),
        new MaintainerCapabilitySpec(
            RecapRewriterProtocolV1.RuntimeProtocolId,
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1
        ),
        new MaintainerDeclarativeSpec(
            "Question " + blockKey,
            "Maintain " + blockKey + " literally."
        ),
        16 * 1024
    );

    private static EventAddress Address(ulong value) => new(
        SizedPtr.FromPacked(value),
        1,
        AddressHint.None
    );
}

internal sealed class ScriptedInvoker : IRecapCompletionInvoker {
    private readonly Func<CompletionRequest, CancellationToken,
        ValueTask<CompletionResult>> _handler;
    private int _callCount;
    private int _active;
    private int _maximumActive;

    internal ScriptedInvoker(
        Func<CompletionRequest, CancellationToken,
            ValueTask<CompletionResult>> handler
    ) => _handler = handler;

    public string ProviderId => "test-provider";
    public string ApiSpecId => "test-api-v1";
    internal int CallCount => Volatile.Read(ref _callCount);
    internal int MaximumActive => Volatile.Read(ref _maximumActive);

    public async ValueTask<CompletionResult> InvokeAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CancellationToken cancellationToken
    ) {
        _ = invocationOptions;
        Interlocked.Increment(ref _callCount);
        int active = Interlocked.Increment(ref _active);
        int observed;
        while (active > (observed = Volatile.Read(ref _maximumActive))) {
            if (Interlocked.CompareExchange(
                    ref _maximumActive,
                    active,
                    observed) == observed) {
                break;
            }
        }
        try {
            return await _handler(request, cancellationToken)
                .ConfigureAwait(false);
        }
        finally {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed class ScriptedResolver : IRecapCompletionRouteResolver {
    private readonly Func<RecapCompletionRouteKey,
        RecapCompletionRouteResolution> _handler;
    private int _callCount;

    internal ScriptedResolver(Func<RecapCompletionRouteKey,
        RecapCompletionRouteResolution> handler) => _handler = handler;

    internal int CallCount => Volatile.Read(ref _callCount);

    public RecapCompletionRouteResolution Resolve(
        RecapCompletionRouteKey key
    ) {
        Interlocked.Increment(ref _callCount);
        return _handler(key);
    }
}

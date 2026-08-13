using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Hosting.Tests;

public sealed class HostingTests {
    private static readonly FamilyDefinitionDigest Family =
        new(new string('a', 64));

    [Fact]
    public void Manifest_IsCanonicalBoundedAndSemanticNullIsExplicit() {
        RecapGridRouteManifest manifest = Manifest(null);
        string text = Encoding.UTF8.GetString(manifest.ToCanonicalBytes());

        Assert.Equal(
            "{\"v\":1,\"routes\":[{\"familyDigest\":\""
            + new string('a', 64)
            + "\",\"runtimeProtocolId\":\"tool-runtime-v1\","
            + "\"semanticModelId\":null,\"connectionId\":\"main\","
            + "\"maximumConcurrency\":2,"
            + "\"dispatchTimeoutMilliseconds\":30000,"
            + "\"maximumOutputTokens\":1024}]}",
            text
        );
        RecapGridRouteManifest decoded =
            RecapGridRouteManifest.DecodeCanonical(
                manifest.ToCanonicalBytes()
            );
        Assert.Null(decoded.Routes[0].Key.SemanticModelId);
        Assert.Equal(text, Encoding.UTF8.GetString(
            decoded.ToCanonicalBytes()));
    }

    [Fact]
    public void Manifest_DefensivelyFreezesRoutesAndCanonicalBytes() {
        RecapGridRouteManifestEntry original = Manifest(null).Routes[0];
        RecapGridRouteManifestEntry[] source = [original];
        RecapGridRouteManifest manifest = RecapGridRouteManifest.Create(source);
        byte[] expected = manifest.ToCanonicalBytes();

        source[0] = null!;
        byte[] exposed = manifest.ToCanonicalBytes();
        exposed[0] ^= 0xff;
        Assert.Equal(expected, manifest.ToCanonicalBytes());
        Assert.Same(original, manifest.Routes[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RecapGridRouteManifestEntry>)manifest.Routes)[0] = original
        );
    }

    [Theory]
    [InlineData("missing-semantic")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("whitespace")]
    public void Manifest_RejectsNoncanonicalOrAmbiguousInput(string kind) {
        string canonical = Encoding.UTF8.GetString(
            Manifest(null).ToCanonicalBytes()
        );
        string invalid = kind switch {
            "missing-semantic" => canonical.Replace(
                "\"semanticModelId\":null,",
                "",
                StringComparison.Ordinal
            ),
            "unknown" => canonical.Replace(
                "\"connectionId\":",
                "\"unknown\":1,\"connectionId\":",
                StringComparison.Ordinal
            ),
            "duplicate" => canonical.Replace(
                "\"connectionId\":",
                "\"semanticModelId\":null,\"connectionId\":",
                StringComparison.Ordinal
            ),
            "whitespace" => canonical.Replace(
                "{\"v\"",
                "{ \"v\"",
                StringComparison.Ordinal
            ),
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<InvalidDataException>(() =>
            RecapGridRouteManifest.DecodeCanonical(
                Encoding.UTF8.GetBytes(invalid)
            ));
    }

    [Fact]
    public void ExactResolver_IsLazyAndNeverFallsBackFromNullSemanticKey() {
        var factory = new RecordingFactory();
        using var registry = new CompletionConnectionRegistry(
            Connections(),
            factory
        );
        var resolver = new RecapGridRuntimeHost.HostingExactRouteResolver(
            Manifest(null),
            registry
        );
        Assert.Equal(0, factory.CreateCount);

        var missing = resolver.Resolve(new RecapCompletionRouteKey(
            Family,
            RecapCompletionProtocolV1.RuntimeProtocolId,
            "semantic-v1"
        ));
        Assert.IsType<RecapCompletionRouteResolution.Unavailable>(missing);
        Assert.Equal(0, factory.CreateCount);

        var bound = Assert.IsType<RecapCompletionRouteResolution.Bound>(
            resolver.Resolve(new RecapCompletionRouteKey(
                Family,
                RecapCompletionProtocolV1.RuntimeProtocolId,
                null
            ))
        );
        Assert.Equal("model-main", bound.Route.ModelId);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Host_IsLazyAndDrainsRuntimeBeforeRegistryClient() {
        var factory = new RecordingFactory();
        await using RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
            Manifest(null),
            Connections(),
            factory
        );

        Assert.Equal(0, factory.CreateCount);
        Assert.False(host.Telemetry.IsMaterialized);
        Assert.Empty(host.Telemetry.ReadSnapshot().Events);
        Assert.False(host.Telemetry.IsMaterialized);
        await host.DisposeAsync();
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CompletionHostSharesOneRegistryAndDefersRouteManifest() {
        var factory = new RecordingFactory();
        FrozenRowBatch batch = Batch();
        int manifestLoads = 0;
        await using RecapGridCompletionHost host =
            RecapGridCompletionHost.Create(
                () => {
                    manifestLoads++;
                    return RecapGridRouteManifest.Create([
                        new RecapGridRouteManifestEntry(
                            new RecapCompletionRouteKey(
                                batch.OrderedMissingWork[0].Family.Digest,
                                RecapCompletionProtocolV1.RuntimeProtocolId,
                                null),
                            "main",
                            2,
                            TimeSpan.FromSeconds(30),
                            1024)
                    ]);
                },
                Connections(),
                factory);

        Assert.Equal(0, manifestLoads);
        Assert.Equal(0, factory.CreateCount);
        RecapGridAgentConnectionResult.Bound agent = Assert.IsType<
            RecapGridAgentConnectionResult.Bound>(
            host.BindAgentExact("main"));
        Assert.Equal(0, manifestLoads);
        Assert.Equal(1, factory.CreateCount);
        Assert.Same(agent.Client, Assert.IsType<
            CompletionDispatchBindingResult.Bound>(
            host.BindPreparedExact(agent.Identity)).Client);

        RecapCellBatchExecutionResult result = await host.Executor.ExecuteAsync(
            batch, CancellationToken.None);

        Assert.IsType<RecapCellBatchExecutionResult.Completed>(result);
        Assert.Equal(1, manifestLoads);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Host_DisposeAsyncDrainsRealInFlightRuntimeBeforeClient() {
        FrozenRowBatch batch = Batch();
        var client = new BlockingClient();
        var factory = new SingleClientFactory(client);
        RecapGridRouteManifest manifest = RecapGridRouteManifest.Create([
            new RecapGridRouteManifestEntry(
                new RecapCompletionRouteKey(
                    batch.OrderedMissingWork[0].Family.Digest,
                    RecapCompletionProtocolV1.RuntimeProtocolId,
                    null
                ),
                "main",
                1,
                TimeSpan.FromSeconds(30),
                1024
            )
        ]);
        RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
            manifest,
            Connections(),
            factory
        );

        Task<RecapCellBatchExecutionResult> operation = host.Executor
            .ExecuteAsync(batch, CancellationToken.None).AsTask();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task disposing = host.DisposeAsync().AsTask();
        await Task.Delay(20);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, client.DisposeCount);

        client.Release.TrySetResult();
        RecapCellBatchExecutionResult.Completed settled = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await operation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(settled.OrderedOutcomes);
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, client.DisposeCount);

        await host.DisposeAsync();
        host.Dispose();
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task CompletionHost_DisposeAsyncDrainsRealInFlightRuntimeBeforeClient() {
        FrozenRowBatch batch = Batch();
        var client = new BlockingClient();
        var factory = new SingleClientFactory(client);
        RecapGridCompletionHost host = RecapGridCompletionHost.Create(
            () => RecapGridRouteManifest.Create([
                new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        batch.OrderedMissingWork[0].Family.Digest,
                        RecapCompletionProtocolV1.RuntimeProtocolId,
                        null),
                    "main",
                    1,
                    TimeSpan.FromSeconds(30),
                    1024)
            ]),
            Connections(),
            factory);

        Task<RecapCellBatchExecutionResult> operation = host.Executor
            .ExecuteAsync(batch, CancellationToken.None).AsTask();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task disposing = host.DisposeAsync().AsTask();
        await Task.Delay(20);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, client.DisposeCount);

        client.Release.TrySetResult();
        Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await operation.WaitAsync(TimeSpan.FromSeconds(10)));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, client.DisposeCount);
        await host.DisposeAsync();
        host.Dispose();
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task CompletionHost_FatalRegistryCleanupPropagatesAndRunsExactlyOnce() {
        var client = new FatalDisposeClient();
        RecapGridCompletionHost host = RecapGridCompletionHost.Create(
            () => Manifest(null),
            Connections(),
            new SingleClientFactory(client));
        Assert.IsType<RecapGridAgentConnectionResult.Bound>(
            host.BindAgentExact("main"));

        OutOfMemoryException first = await Assert.ThrowsAsync<
            OutOfMemoryException>(() => host.DisposeAsync().AsTask());
        OutOfMemoryException second = await Assert.ThrowsAsync<
            OutOfMemoryException>(() => host.DisposeAsync().AsTask());

        Assert.Equal("completion-host-fatal", first.Message);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public void TelemetryCollector_IsBoundedAndCountsDrops() {
        var collector = new BoundedRecapCompletionTelemetry(2);
        Assert.False(collector.IsMaterialized);
        Assert.Empty(collector.ReadSnapshot().Events);
        Assert.False(collector.IsMaterialized);
        RecapCompletionTelemetryEvent first = Telemetry("one");
        RecapCompletionTelemetryEvent second = Telemetry("two");
        RecapCompletionTelemetryEvent third = Telemetry("three");
        collector.Record(first);
        collector.Record(second);
        collector.Record(third);

        RecapCompletionTelemetrySnapshot snapshot = collector.ReadSnapshot();
        Assert.Equal([second, third], snapshot.Events);
        Assert.Equal(1, snapshot.DroppedEventCount);
        Assert.True(snapshot.RetainedUtf8Bytes > 0);
        Assert.True(collector.IsMaterialized);
    }

    [Fact]
    public void TelemetryCollector_DropsInvalidUtf16AndBoundsProviderFields() {
        var collector = new BoundedRecapCompletionTelemetry();
        RecapCompletionTelemetryEvent invalid = Telemetry("one", "\ud800");

        collector.Record(invalid);

        RecapCompletionTelemetrySnapshot snapshot = collector.ReadSnapshot();
        Assert.Empty(snapshot.Events);
        Assert.Equal(1, snapshot.DroppedEventCount);
        Assert.Equal(0, snapshot.RetainedUtf8Bytes);
    }

    [Fact]
    public void ConnectionsManifest_IsStrictBoundedAndRetainsEveryField() {
        CompletionConnectionsFileConfig config =
            RecapGridCompletionConnectionsManifest.Decode(
                Encoding.UTF8.GetBytes("""
                    {"connections":[{"id":"main","kind":"anthropic","modelId":"model-main","completionSurfaceId":"anthropic","baseAddress":"https://example.invalid/","apiKey":"secret","baseAddressEnv":null,"apiKeyEnv":null,"maxTokens":2048,"reasoningEffort":"high","anthropicPromptCacheTtl":"1h"}],"defaultConnectionId":"main"}
                    """)
            );

        CompletionConnectionConfig item = Assert.Single(config.Connections);
        Assert.Equal("main", config.DefaultConnectionId);
        Assert.Equal("secret", item.ApiKey);
        Assert.Equal(2048, item.MaxTokens);
        Assert.Equal(CompletionReasoningEffort.High, item.ReasoningEffort);
        Assert.Equal(
            Atelia.Completion.Anthropic.AnthropicPromptCacheTtl.OneHour,
            item.AnthropicPromptCacheTtl
        );
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CompletionConnectionConfig>)config.Connections)[0] = item
        );
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("invalid-utf16")]
    public void ConnectionsManifest_RejectsAmbiguousOrInvalidText(
        string kind
    ) {
        string json = kind switch {
            "duplicate" => """
                {"connections":[],"connections":[]}
                """,
            "unknown" => """
                {"connections":[],"unknown":1}
                """,
            "invalid-utf16" => """
                {"connections":[{"id":"\ud800","kind":"test","modelId":"model","completionSurfaceId":"test-v1","baseAddress":"https://example.invalid/"}]}
                """,
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<InvalidDataException>(() =>
            RecapGridCompletionConnectionsManifest.Decode(
                Encoding.UTF8.GetBytes(json)
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            RecapGridCompletionConnectionsManifest.Decode([0xff])
        );
        Assert.Throws<InvalidDataException>(() =>
            RecapGridCompletionConnectionsManifest.Decode(
                new byte[
                    RecapGridCompletionConnectionsLimits
                        .MaximumInputUtf8Bytes + 1
                ]
            )
        );
    }

    [Fact]
    public void ConnectionsManifest_RejectsFieldAndCountCapPlusOne() {
        string oversizedId = new('a',
            RecapGridCompletionConnectionsLimits
                .MaximumIdentifierUtf8Bytes + 1);
        Assert.Throws<InvalidDataException>(() =>
            RecapGridCompletionConnectionsManifest.Decode(
                Encoding.UTF8.GetBytes(
                    "{\"connections\":[{\"id\":\"" + oversizedId
                    + "\",\"kind\":\"test\",\"modelId\":\"model\","
                    + "\"completionSurfaceId\":\"test-v1\","
                    + "\"baseAddress\":\"https://example.invalid/\"}]}"
                )
            )
        );

        string item =
            "{\"id\":\"a\",\"kind\":\"test\",\"modelId\":\"model\","
            + "\"completionSurfaceId\":\"test-v1\","
            + "\"baseAddress\":\"https://x/\"}";
        string tooMany = "{\"connections\":["
            + string.Join(',', Enumerable.Repeat(
                item,
                RecapGridCompletionConnectionsLimits.MaximumConnectionCount
                    + 1
            ))
            + "]}";
        Assert.True(Encoding.UTF8.GetByteCount(tooMany)
            < RecapGridCompletionConnectionsLimits.MaximumInputUtf8Bytes);
        Assert.Throws<InvalidDataException>(() =>
            RecapGridCompletionConnectionsManifest.Decode(
                Encoding.UTF8.GetBytes(tooMany)
            )
        );
    }

    private static RecapGridRouteManifest Manifest(string? semantic)
        => RecapGridRouteManifest.Create([
            new RecapGridRouteManifestEntry(
                new RecapCompletionRouteKey(
                    Family,
                    RecapCompletionProtocolV1.RuntimeProtocolId,
                    semantic
                ),
                "main",
                2,
                TimeSpan.FromSeconds(30),
                1024
            )
        ]);

    private static CompletionConnectionsFileConfig Connections() => new(
        [new CompletionConnectionConfig(
            "main",
            "test",
            "model-main",
            "test-v1",
            "https://example.invalid/"
        )],
        "main"
    );

    private static FrozenRowBatch Batch() {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain the inquiry.",
            [new FamilyToolDefinition(
                "submit",
                "Submit the maintained content.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "outcome",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            orderedEnum: [
                                RecapCompletionProtocolV1.UpdatedOutcome,
                                RecapCompletionProtocolV1.KeepUnchangedOutcome
                            ]
                        ),
                        required: true
                    ),
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            nullable: true
                        ),
                        required: true
                    )
                ])
            )],
            new FamilyOutputProtocol(
                RecapCompletionProtocolV1.OutputProtocolId,
                "submit",
                FamilyToolChoice.Required,
                allowParallel: false
            ),
            new FamilyInputRenderingProtocol(
                RecapCompletionProtocolV1.InputProtocolId,
                RecapCompletionProtocolV1.PriorProjectionSchemaId,
                RecapCompletionProtocolV1.HistorySegmentRenderingSchemaId
            )
        );
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
                    RecapCompletionProtocolV1.RuntimeProtocolId,
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
        var rowId = new HistoryRowId(new string('2', 64));
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(logical, definition.Digest)
        ]);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineId,
            rowId,
            target
        );
        HistorySegmentDescriptor descriptor = Descriptor(timelineId, rowId);
        EvaluationKey key = EvaluationKey.Create(
            descriptor.DescriptorDigest,
            definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            new RowViewCoordinate(
                descriptor.RefId,
                timelineId,
                rowId,
                descriptor.DescriptorDigest,
                recipe.Digest,
                target.Digest,
                previousHistoryRowId: null,
                previousViewDigest: null,
                bootstrapCompleted: true
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(logical, key)]
        );
        var segment = new HistorySegmentContent(
            descriptor,
            Window([new ObservationMessage("visible history")])
        );
        var timelineHead = new TimelineHeadRef(
            timelineId,
            descriptor.RefId,
            rowId,
            new string('3', 64),
            descriptor.EndInclusive,
            1,
            new string('4', 64),
            generation: 1
        );
        var controlHead = new ControlHeadRef(
            new ControlInstanceId(new string('4', 32)),
            descriptor.RefId,
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
                schemaVersion: 1
            ),
            recipe,
            segment,
            spec,
            previousView: null,
            previousCells: Array.Empty<RecapCellArtifact>(),
            priorProjection: null,
            [new FrozenRecapCellWork(
                0,
                logical,
                key,
                definition,
                family
            )]
        );
    }

    private static HistorySegmentDescriptor Descriptor(
        TimelineId timelineId,
        HistoryRowId rowId
    ) {
        SessionContextAnchorSetupReferences setups = Setups();
        return new HistorySegmentDescriptor(
            timelineId,
            new string('3', 64),
            rowId,
            previousRowId: null,
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
            new HistorySegmentDescriptorDigest(new string('8', 64))
        );
    }

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

    private static EventAddress Address(ulong value) => new(
        SizedPtr.FromPacked(value),
        1,
        AddressHint.None
    );

    private static RecapCompletionTelemetryEvent Telemetry(
        string outcome,
        string modelId = "model-main"
    )
        => new(
            "settled",
            new RecapCompletionRouteKey(
                Family,
                RecapCompletionProtocolV1.RuntimeProtocolId,
                null
            ),
            modelId,
            "test",
            "test-v1",
            new EvaluationKeyDigest(new string('b', 64)),
            Family,
            new MaintainerDefinitionDigest(new string('c', 64)),
            new string('d', 64),
            true,
            null,
            RecapCompletionWorkRole.Leader,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            PromptCacheReuseHint.ConnectionDefault,
            false,
            null,
            0,
            null,
            outcome,
            null,
            null
        );

    private sealed class RecordingFactory : ICompletionClientFactory {
        public int CreateCount { get; private set; }

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreateCount++;
            return new Client();
        }
    }

    private sealed class SingleClientFactory(ICompletionClient client)
        : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection)
            => client;
    }

    private sealed class FatalDisposeClient : ICompletionClient,
        IDisposable {
        internal int DisposeCount { get; private set; }
        public string Name => "fatal-test";
        public string ApiSpecId => "fatal-test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public void Dispose() {
            DisposeCount++;
            throw new OutOfMemoryException("completion-host-fatal");
        }
    }

    private sealed class BlockingClient : ICompletionClient, IDisposable {
        private int _disposed;
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal int DisposeCount { get; private set; }
        public string Name => "blocking-test";
        public string ApiSpecId => "blocking-test-v1";

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                    "submit",
                    "call-1",
                    "{\"outcome\":\"updated\",\"content\":\"settled\"}"
                ))]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            );
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                DisposeCount++;
            }
        }
    }

    private sealed class Client : ICompletionClient, IDisposable {
        public string Name => "test";
        public string ApiSpecId => "test-v1";
        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

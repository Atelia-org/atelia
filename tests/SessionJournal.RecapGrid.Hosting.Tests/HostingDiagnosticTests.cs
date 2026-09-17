using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Hosting.Tests;

public sealed partial class HostingTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientConstructionFailurePreservesCauseBeforeAnyRuntimeInvocation(bool sharedHost) {
        FrozenRowBatch batch = Batch();
        var factory = new DiagnosticFactory(failConstruction: true);
        RecapCellBatchExecutionResult result;
        RecapCompletionTelemetrySnapshot evidence;
        if (sharedHost) {
            await using RecapGridCompletionHost host = RecapGridCompletionHost.Create(
                () => DiagnosticManifest(batch), Connections(), factory);
            result = await host.Executor.ExecuteAsync(batch, CancellationToken.None);
            evidence = host.ReadTelemetrySnapshot();
        }
        else {
            await using RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
                DiagnosticManifest(batch), Connections(), factory);
            result = await host.Executor.ExecuteAsync(batch, CancellationToken.None);
            evidence = host.ReadTelemetrySnapshot();
        }
        var rejected = Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(result);
        Assert.Equal("RouteClientConstructionFailed", rejected.Code);
        Assert.Contains("InvalidOperationException: Cannot construct diagnostic client", rejected.Detail);
        Assert.Contains("ArgumentException: Required environment variable TEST_RECAP_API_KEY is missing", rejected.Detail);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, factory.Client.InvokeCount);
        Assert.Empty(evidence.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureAfterInvocationRetainsSettledEvidence(bool sharedHost) {
        FrozenRowBatch batch = Batch();
        var factory = new DiagnosticFactory(failConstruction: false);
        RecapCellBatchExecutionResult result;
        RecapCompletionTelemetrySnapshot evidence;
        if (sharedHost) {
            await using RecapGridCompletionHost host = RecapGridCompletionHost.Create(
                () => DiagnosticManifest(batch), Connections(), factory);
            result = await host.Executor.ExecuteAsync(batch, CancellationToken.None);
            evidence = host.ReadTelemetrySnapshot();
        }
        else {
            await using RecapGridRuntimeHost host = RecapGridRuntimeHost.Create(
                DiagnosticManifest(batch), Connections(), factory);
            result = await host.Executor.ExecuteAsync(batch, CancellationToken.None);
            evidence = host.ReadTelemetrySnapshot();
        }
        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(result);
        var failure = Assert.IsType<RecapCellExecutionOutcome.Failed>(Assert.Single(completed.OrderedOutcomes));
        Assert.Equal("CompletionProviderFailure", failure.Code);
        Assert.Equal("Diagnostic provider failed after invocation", failure.Detail);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.Client.InvokeCount);
        RecapCompletionTelemetryEvent settled = Assert.Single(evidence.Events);
        Assert.Equal("completion-settled", settled.Kind);
        Assert.Equal(failure.Code, settled.Code);
        Assert.Equal(failure.Detail, settled.Detail);
        Assert.False(settled.ResultReceived);
        Assert.Equal(0, evidence.DroppedEventCount);
    }

    [Fact]
    public async Task DeferredManifestFailurePreservesInnerCauseWithoutConstructingClient() {
        var factory = new DiagnosticFactory(failConstruction: false);
        FrozenRowBatch batch = Batch();
        await using RecapGridCompletionHost host = RecapGridCompletionHost.Create(
            () => throw new InvalidDataException("Cannot load diagnostic routes",
                new FormatException("routes.json entry is missing familyDigest")), Connections(), factory);
        RecapCompletionRouteKey key = DiagnosticManifest(batch).Routes[0].Key;
        var inspection = Assert.IsType<RecapGridConfiguredRouteInspectionResult.Invalid>(host.InspectRouteExact(key));
        var result = Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(
            await host.Executor.ExecuteAsync(batch, CancellationToken.None));
        Assert.Equal("RouteConfigurationFailed", result.Code);
        Assert.Equal(inspection.Detail, result.Detail);
        Assert.Contains("InvalidDataException: Cannot load diagnostic routes", result.Detail);
        Assert.Contains("FormatException: routes.json entry is missing familyDigest", result.Detail);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, factory.Client.InvokeCount);
    }

    [Fact]
    public async Task ProviderFreeInspectionBoundsLongUnicodeDiagnostic() {
        var factory = new DiagnosticFactory(failConstruction: false);
        FrozenRowBatch batch = Batch();
        await using RecapGridCompletionHost host = RecapGridCompletionHost.Create(
            () => throw new InvalidDataException(string.Concat(Enumerable.Repeat("错误😀", 4_096))),
            Connections(), factory);
        var inspection = Assert.IsType<RecapGridConfiguredRouteInspectionResult.Invalid>(
            host.InspectRouteExact(DiagnosticManifest(batch).Routes[0].Key));
        Assert.StartsWith("InvalidDataException: 错误😀", inspection.Detail);
        var strictUtf8 = new UTF8Encoding(false, true);
        Assert.InRange(strictUtf8.GetByteCount(inspection.Detail), 4_092, 4_096);
        Assert.Equal(0, factory.CreateCount);
    }

    private static RecapGridRouteManifest DiagnosticManifest(FrozenRowBatch batch) => RecapGridRouteManifest.Create([
        new RecapGridRouteManifestEntry(new RecapCompletionRouteKey(batch.OrderedMissingWork[0].Family.Digest,
            RecapRewriterProtocolV3.RuntimeProtocolId, null), "main", 1, TimeSpan.FromSeconds(30))
    ]);

    private sealed class DiagnosticFactory(bool failConstruction) : ICompletionClientFactory {
        internal int CreateCount { get; private set; }
        internal DiagnosticClient Client { get; } = new();
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreateCount++;
            if (failConstruction) throw new InvalidOperationException("Cannot construct diagnostic client",
                new ArgumentException("Required environment variable TEST_RECAP_API_KEY is missing"));
            return Client;
        }
    }

    private sealed class DiagnosticClient : ICompletionClient {
        internal int InvokeCount { get; private set; }
        public string Name => "test";
        public string ApiSpecId => "test-v1";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            InvokeCount++;
            throw new InvalidOperationException("Diagnostic provider failed after invocation");
        }
    }
}

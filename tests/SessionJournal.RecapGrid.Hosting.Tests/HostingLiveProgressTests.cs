using System.Collections.Concurrent;
using Atelia.Completion;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Hosting.Tests;

public sealed partial class HostingTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveProgressDoesNotConsumeSettledEvidenceCapacity(bool throwFromObserver) {
        FrozenRowBatch batch = Batch();
        var client = new BlockingClient();
        await using var registry = new CompletionConnectionRegistry(
            CompletionConnectionConfigLoader.NormalizeAndValidate(Connections()),
            new SingleClientFactory(client));
        var kinds = new ConcurrentQueue<string>();
        RecapGridCompletionHost? observingHost = null;
        var observer = new LiveObserver(value => {
            kinds.Enqueue(value.Kind);
            _ = observingHost!.ReadTelemetrySnapshot();
            if (throwFromObserver) { throw new InvalidOperationException("observer only"); }
        });
        await using RecapGridCompletionHost host = RecapGridCompletionHost.CreateBorrowingRegistry(
            () => RecapGridRouteManifest.Create([
                new RecapGridRouteManifestEntry(new RecapCompletionRouteKey(
                    batch.OrderedMissingWork[0].Family.Digest,
                    RecapRewriterProtocolV3.RuntimeProtocolId, null),
                    "main", 1, TimeSpan.FromSeconds(30))
            ]), registry, maximumTelemetryEvents: 1, liveTelemetry: observer);
        observingHost = host;
        Task<RecapCellBatchExecutionResult> operation = host.Executor
            .ExecuteAsync(batch, CancellationToken.None).AsTask();
        try {
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["completion-started"], kinds.ToArray());
            Assert.Empty(host.ReadTelemetrySnapshot().Events);
        }
        finally { client.Release.TrySetResult(); }
        Assert.IsType<RecapCellBatchExecutionResult.Completed>(await operation);
        Assert.Equal(["completion-started", "completion-settled"], kinds.ToArray());
        RecapCompletionTelemetrySnapshot evidence = host.ReadTelemetrySnapshot();
        Assert.Equal("completion-settled", Assert.Single(evidence.Events).Kind);
        Assert.Equal(0, evidence.DroppedEventCount);
    }

    private sealed class LiveObserver(Action<RecapCompletionTelemetryEvent> record)
        : IRecapCompletionTelemetry {
        public void Record(RecapCompletionTelemetryEvent value) => record(value);
    }
}

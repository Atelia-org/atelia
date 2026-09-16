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
    public async Task MaintenanceFactoryDoesNotDecorateAgentBindingAndMustOwnExactDeadline(bool wrongDeadline) {
        FrozenRowBatch batch = Batch();
        var raw = new Client();
        await using var registry = new CompletionConnectionRegistry(
            CompletionConnectionConfigLoader.NormalizeAndValidate(Connections()), new SingleClientFactory(raw));
        int creations = 0;
        MaintenanceDeadlineInvoker? wrapper = null;
        using var host = RecapGridCompletionHost.CreateBorrowingRegistry(
            () => RecapGridRouteManifest.Create([new(new(batch.OrderedMissingWork[0].Family.Digest,
                RecapRewriterProtocolV3.RuntimeProtocolId, null), "main", 1, TimeSpan.FromSeconds(30))]),
            registry, maintenanceInvokerFactory: (id, client, deadline) => {
                Assert.Equal("main", id);
                Assert.Same(raw, client);
                creations++;
                return wrapper = new(client, wrongDeadline ? deadline + TimeSpan.FromSeconds(1) : deadline);
            });
        var bound = Assert.IsType<RecapGridAgentConnectionResult.Bound>(host.BindAgentExact("main"));
        Assert.Same(raw, bound.Client);
        Assert.Equal(0, creations);
        var result = await host.Executor.ExecuteAsync(batch, default);
        Assert.Equal(1, creations);
        if (wrongDeadline) {
            Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(result);
            Assert.Equal(0, wrapper!.Calls);
        }
        else {
            Assert.IsType<RecapCellBatchExecutionResult.Completed>(result);
            Assert.Equal(1, wrapper!.Calls);
        }
        Assert.Same(raw, Assert.IsType<RecapGridAgentConnectionResult.Bound>(host.BindAgentExact("main")).Client);
    }

    private sealed class MaintenanceDeadlineInvoker(ICompletionClient client, TimeSpan timeout)
        : IRecapCompletionAttemptDeadlineInvoker {
        public TimeSpan AttemptTimeout => timeout;
        public string ProviderId => client.Name;
        public string ApiSpecId => client.ApiSpecId;
        internal int Calls;
        public async ValueTask<CompletionResult> InvokeAsync(CompletionRequest request, CompletionInvocationOptions options, CancellationToken cancellationToken) {
            Calls++;
            using var deadline = new CancellationTokenSource(AttemptTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            return await client.StreamCompletionAsync(request, options, null, linked.Token);
        }
    }
}

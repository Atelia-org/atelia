using System.Collections.Concurrent;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class GalateaAssetRuntimeTests {
    [Fact]
    public async Task SharedFamily_UsesOnePrefixAndIndependentMemberTails() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV5,
            out RecapGridControlRegistrationBundle? bundle
        ));
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            Assert.Single(bundle!.Families),
            bundle.Definitions,
            [new ObservationMessage("shared visible history")]
        );
        var requests = new ConcurrentQueue<CompletionRequest>();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            requests.Enqueue(request);
            return ValueTask.FromResult(RuntimeTestFixture.Updated(
                request,
                invoker!,
                request.TailMessages.Single() is ObservationMessage tail
                    ? tail.Content!
                    : "missing-tail"
            ));
        });
        using var runtime = new RecapCompletionRuntime(
            new ScriptedResolver(key => new RecapCompletionRouteResolution.Bound(
                RuntimeTestFixture.Route(batch, invoker)
            ))
        );

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );

        Assert.Equal(2, completed.OrderedOutcomes.Count);
        Assert.All(completed.OrderedOutcomes, static outcome =>
            Assert.IsType<RecapCellExecutionOutcome.Updated>(outcome));
        CompletionRequest[] captured = requests.ToArray();
        Assert.Equal(2, captured.Length);
        Assert.Same(captured[0].PromptPrefix, captured[1].PromptPrefix);
        Assert.Equal(bundle.Families[0].SystemPrompt,
            captured[0].PromptPrefix.SystemPrompt);
        var firstTail = Assert.IsType<ObservationMessage>(
            Assert.Single(captured[0].TailMessages)
        );
        var secondTail = Assert.IsType<ObservationMessage>(
            Assert.Single(captured[1].TailMessages)
        );
        Assert.Contains("\"logicalColumnId\":\"world-understanding\"",
            firstTail.Content, StringComparison.Ordinal);
        Assert.Contains("\"logicalColumnId\":\"autobiography\"",
            secondTail.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("autobiography", firstTail.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("world-understanding", secondTail.Content,
            StringComparison.Ordinal);
        Assert.Equal(
            captured[0].PromptPrefix.SharedContextMessages,
            captured[1].PromptPrefix.SharedContextMessages
        );
    }
}

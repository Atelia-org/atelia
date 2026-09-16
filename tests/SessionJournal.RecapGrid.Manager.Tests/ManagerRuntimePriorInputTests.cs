using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed partial class ManagerVerticalTests {
    [Fact]
    public async Task RealRuntimeUsesOrderedMultiColumnPriorAndReopensWithoutCalls() {
        Fixture fixture = CreateFullFixture(turns: 2, zeroColumns: true);
        using (fixture.Journal) {
            Assert.True(fixture.Rows.Count >= 2);
            FamilyDefinition family = FamilyDefinition.Create(
                "Maintain two independent lines of inquiry.", [],
                RecapRewriterProtocolV3.CreateOutputProtocol(),
                new FamilyInputRenderingProtocol(
                    RecapRewriterProtocolV3.InputProtocolId,
                    RecapRewriterProtocolV3.PriorProjectionSchemaId,
                    RecapRewriterProtocolV3.HistorySegmentRenderingSchemaId));
            MaintainerDefinitionRevision[] definitions = [
                RuntimePriorDefinition(family, "case.culprit"),
                RuntimePriorDefinition(family, "case.world")
            ];
            GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
                fixture.TimelineHead.TimelineId,
                fixture.Rows[^1].Descriptor.RowId,
                BuildTarget.Create(definitions.Select(static definition =>
                    new BuildTargetColumn(definition.LogicalColumnId, definition.Digest))));
            var admission = new RecapGridControlAdmission(
                RecapGridControlPermission.All, [family.Digest],
                [definitions[0].Capability.CapabilityFingerprint],
                [ContextHeaderCarrier.System], ["case."], 64, 1024);
            using (RecapGridControlHandle control = Assert.IsType<RecapGridControlOpenResult.Opened>(
                       RecapGridControlFactory.Open(fixture.Path,
                           fixture.Journal.BranchRefId, admission)).Handle) {
                ControlHeadRef head = Assert.IsType<RecapGridControlSnapshotResult.Available>(
                    control.Reader.ReadSnapshot()).Snapshot.Head;
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutFamilyDefinition(head, family)).Head;
                foreach (MaintainerDefinitionRevision definition in definitions) {
                    head = Assert.IsType<RecapGridControlPutResult.Stored>(
                        control.Coordinator.PutMaintainerDefinition(head, definition)).Head;
                }
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutBuildRecipe(head, fixture.TimelineHead,
                        recipe, fixture.Rows[^1].Witness)).Head;
                Assert.IsType<RecapGridControlActivateResult.Applied>(
                    control.Coordinator.CompareExchangeActiveRecipe(head,
                        fixture.TimelineHead, recipe.Digest, RecapGridControlActivationPurpose.Direct));
            }

            var provider = new PriorInputProvider();
            RecapCompletionRoute route = RecapCompletionRoute.Create(
                new RecapCompletionRouteKey(family.Digest,
                    RecapRewriterProtocolV3.RuntimeProtocolId, null),
                "test", "test-model", provider, RecapCompletionResourceOwnership.Borrowed,
                maximumConcurrency: 1, TimeSpan.FromSeconds(30));
            RecapGridBuildResult.Fulfilled first;
            using (var runtime = new RecapCompletionRuntime(new PriorInputRouteResolver(route)))
            using (RecapGridManagerHandle manager = OpenManager(fixture)) {
                first = Assert.IsType<RecapGridBuildResult.Fulfilled>(
                    await manager.Manager.BuildAsync(Request(recipe.Target), runtime));
            }
            int expectedCalls = fixture.Rows.Count * 2;
            Assert.Equal(expectedCalls, provider.Requests.Count);
            Assert.Equal(expectedCalls, first.Metrics.CellsCommitted);
            Assert.Equal(fixture.Rows.Count, first.Metrics.RowViewsCommitted);
            string[] expectedColumns = definitions.Select(static value => value.LogicalColumnId.Value).ToArray();
            foreach (CompletionRequest request in provider.Requests.Take(2)) {
                using JsonDocument prior = JsonDocument.Parse(Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                    request.PromptPrefix.SharedContextMessages[0]).Content));
                Assert.Empty(prior.RootElement.GetProperty("columns").EnumerateArray());
            }
            for (int row = 1; row < fixture.Rows.Count; row++) {
                foreach (CompletionRequest request in provider.Requests.Skip(row * 2).Take(2)) {
                    using JsonDocument prior = JsonDocument.Parse(Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                        request.PromptPrefix.SharedContextMessages[0]).Content));
                    Assert.Equal(RecapRewriterProtocolV3.PriorProjectionSchemaId,
                        prior.RootElement.GetProperty("schema").GetString());
                    JsonElement[] columns = prior.RootElement.GetProperty("columns").EnumerateArray().ToArray();
                    Assert.Equal(expectedColumns, columns.Select(static column =>
                        column.GetProperty("logicalColumnId").GetString()));
                    Assert.Equal(expectedColumns.Select(column => column + ":generation=" + row),
                        columns.Select(static column => column.GetProperty("content").GetString()));
                }
            }

            // A new Manager and Runtime reopen the durable store. If any digest
            // changed or the committed winner was lost, new provider calls expose it.
            using var reopenedRuntime = new RecapCompletionRuntime(new PriorInputRouteResolver(route));
            using RecapGridManagerHandle reopenedManager = OpenManager(fixture);
            RecapGridBuildResult.Fulfilled reopened = Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await reopenedManager.Manager.BuildAsync(Request(recipe.Target,
                    maximumNewCalls: 0), reopenedRuntime));
            Assert.Equal(first.Proof.RowResultId, reopened.Proof.RowResultId);
            Assert.Equal(0, reopened.Metrics.NewCalls);
            Assert.Equal(expectedCalls, provider.Requests.Count);
        }
    }

    private static MaintainerDefinitionRevision RuntimePriorDefinition(FamilyDefinition family, string column)
        => MaintainerDefinitionRevision.Create(new LogicalColumnId(column), family.Digest,
            new ContextHeaderBlockTarget(ContextHeaderCarrier.System, column,
                "Derived context from prior history: " + column),
            new MaintainerCapabilitySpec(RecapRewriterProtocolV3.RuntimeProtocolId,
                MaintainerReadableScope.FullPriorBuildTargetAndCurrentHistorySegmentV1),
            new MaintainerDeclarativeSpec(column, "Maintain " + column), 16 * 1024);

    private sealed class PriorInputRouteResolver(RecapCompletionRoute route) : IRecapCompletionRouteResolver {
        public RecapCompletionRouteResolution Resolve(RecapCompletionRouteKey key) => key == route.Key
            ? new RecapCompletionRouteResolution.Bound(route)
            : new RecapCompletionRouteResolution.Unavailable("RouteMissing", "No fallback route.");
    }

    private sealed class PriorInputProvider : IRecapCompletionInvoker {
        internal List<CompletionRequest> Requests { get; } = [];
        public string ProviderId => "prior-test";
        public string ApiSpecId => "prior-test-v1";

        public ValueTask<CompletionResult> InvokeAsync(CompletionRequest request,
            CompletionInvocationOptions invocationOptions, CancellationToken cancellationToken) {
            Requests.Add(request);
            using JsonDocument prior = JsonDocument.Parse(Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                request.PromptPrefix.SharedContextMessages[0]).Content));
            using JsonDocument work = JsonDocument.Parse(Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                Assert.Single(request.TailMessages)).Content));
            JsonElement[] columns = prior.RootElement.GetProperty("columns").EnumerateArray().ToArray();
            int generation = columns.Length == 0 ? 1 : int.Parse(
                Assert.IsType<string>(columns[0].GetProperty("content").GetString()).Split("generation=")[1],
                System.Globalization.CultureInfo.InvariantCulture) + 1;
            string content = work.RootElement.GetProperty("logicalColumnId").GetString()
                + ":generation=" + generation;
            return ValueTask.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(content)]),
                new CompletionDescriptor(ProviderId, ApiSpecId, request.ModelId)));
        }
    }
}

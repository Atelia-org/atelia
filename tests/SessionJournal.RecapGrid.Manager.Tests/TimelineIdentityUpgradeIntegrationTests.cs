using System.IO.Compression;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed partial class ManagerVerticalTests {
    [Fact]
    public async Task OldTimelineAndControlUpgradeThenFreshRuntimeBuildsAndColdReopens() {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RowIdentityV2");
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, "expected.json")));
        string path = NewPath();
        ZipFile.ExtractToDirectory(Path.Combine(fixtureDirectory, "repository.zip"), path);
        if (!OperatingSystem.IsWindows()) {
            foreach (string directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories).Prepend(path)) {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        var refId = new RefId(ulong.Parse(expected.RootElement.GetProperty("refId").GetString()!,
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
        var timelineId = new TimelineId(expected.RootElement.GetProperty("timelineId").GetString()!);
        string database = Path.Combine(path, "derived", "history-timeline", "v2", "refs", refId.ToHexString(),
            "timelines", timelineId.Value + ".sqlite");
        string controlPath = Assert.Single(Directory.GetFiles(Path.Combine(path, "control"), "control.json", SearchOption.AllDirectories));
        byte[] oldDatabase = File.ReadAllBytes(database);
        byte[] oldControl = File.ReadAllBytes(controlPath);
        Dictionary<string, byte[]> preservedFiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(path, file).StartsWith("control" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.GetRelativePath(path, file).StartsWith("derived" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(file) == "cadence.json")
            .ToDictionary(file => file, File.ReadAllBytes);
        Assert.IsType<HistoryTimelineReaderOpenResult.UnsupportedSchema>(HistoryTimelineMaintenance.OpenReader(path, refId));
        Assert.Equal(oldDatabase, File.ReadAllBytes(database));
        var upgraded = Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(
            HistoryTimelineMaintenance.UpgradeSchemaV2(path, refId, timelineId));
        Assert.Equal(Convert.FromBase64String(expected.RootElement.GetProperty("timelineHeadBase64").GetString()!),
            upgraded.Head.ToCanonicalBytes());
        Assert.Equal(oldControl, File.ReadAllBytes(controlPath));
        Assert.IsType<RecapGridStoreOpenResult.UnsupportedSchema>(RecapGridStoreFactory.Open(path));

        // The fixed old rules use test-only runtime-v1. Preserve them, then
        // register the real V3 built-in as a fresh operation after upgrade.
        Assert.True(RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
            RecapGridAgentControlBuiltIns.MysteryInvestigationV4, out RecapGridControlRegistrationBundle? builtIn));
        FamilyDefinition family = Assert.Single(builtIn!.Families);
        string[] columns = builtIn.Definitions.Select(value => value.LogicalColumnId.Value).ToArray();
        Assert.Equal(2, columns.Length);
        var admission = new RecapGridControlAdmission(RecapGridControlPermission.All, [family.Digest],
            builtIn.Definitions.Select(value => value.Capability.CapabilityFingerprint),
            [ContextHeaderCarrier.System], ["case."], 64, 1024);
        GridBuildRecipe recipe;
        RecapGridBuildResult.Fulfilled first;
        var provider = new PriorInputProvider();
        RecapCompletionRoute route = RecapCompletionRoute.Create(
            new RecapCompletionRouteKey(family.Digest, RecapRewriterProtocolV3.RuntimeProtocolId, null),
            "fixture", "synthetic-model", provider, RecapCompletionResourceOwnership.Borrowed,
            maximumConcurrency: 1, TimeSpan.FromSeconds(30));
        int rowCount = checked((int)upgraded.Head.SelectedPathCount);
        Assert.True(rowCount > 1);
        using (SessionJournalEngine journal = SessionJournalEngine.Open(path)) {
            HistoryTimelineSelectedRow through;
            using (HistoryTimelineReaderHandle timeline = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
                       HistoryTimelineMaintenance.OpenReader(path, refId)).Handle) {
                through = Assert.IsType<HistoryTimelineReaderRowResult.Selected>(timeline.Reader.ReadSelectedRow(
                    upgraded.Head, upgraded.Head.HeadRowId!.Value)).Row;
            }
            recipe = GridBuildRecipe.CreateFull(timelineId, through.Descriptor.RowId,
                BuildTarget.Create(builtIn.Definitions.Select(value => new BuildTargetColumn(value.LogicalColumnId, value.Digest))));
            using (RecapGridControlHandle control = Assert.IsType<RecapGridControlOpenResult.Opened>(
                       RecapGridControlFactory.Open(path, refId, admission)).Handle) {
                RecapGridControlSnapshot old = Assert.IsType<RecapGridControlSnapshotResult.Available>(control.Reader.ReadSnapshot()).Snapshot;
                Assert.Equal(oldControl, File.ReadAllBytes(controlPath));
                using JsonDocument oldState = JsonDocument.Parse(oldControl);
                JsonElement[] receipts = oldState.RootElement.GetProperty("operationReceipts").EnumerateArray().ToArray();
                Assert.Equal(3, receipts.Length);
                var operation = RecapGridControlOperation.Create("after-timeline-upgrade-v3-built-in", 50, new string('d', 64));
                var bundle = new RecapGridControlRegistrationBundle(builtIn.Families, builtIn.Definitions,
                    [new RecapGridControlRecipeRegistration(recipe, through.Witness)]);
                ControlHeadRef registered = Assert.IsType<RecapGridControlOperationResult.Applied>(
                    control.Coordinator.ApplyRegistrationBundle(old.Head, upgraded.Head, operation, bundle)).Head;
                Assert.Equal(old.Head.Generation + 1, registered.Generation);
                using JsonDocument newState = JsonDocument.Parse(File.ReadAllBytes(controlPath));
                Assert.Equal(4, newState.RootElement.GetProperty("schemaVersion").GetInt32());
                var newReceipts = newState.RootElement.GetProperty("operationReceipts").EnumerateArray()
                    .ToDictionary(value => value.GetProperty("operationKey").GetString()!);
                Assert.Equal(4, newReceipts.Count);
                foreach (JsonElement receipt in receipts) {
                    Assert.Equal(receipt.GetRawText(), newReceipts[receipt.GetProperty("operationKey").GetString()!].GetRawText());
                }
                RecapGridControlSnapshot current = Assert.IsType<RecapGridControlSnapshotResult.Available>(control.Reader.ReadSnapshot()).Snapshot;
                foreach (FamilyDefinition oldFamily in old.Families) {
                    Assert.Equal(oldFamily.ToCanonicalBytes(), current.Families.Single(value => value.Digest == oldFamily.Digest).ToCanonicalBytes());
                }
                foreach (MaintainerDefinitionRevision oldDefinition in old.Definitions) {
                    Assert.Equal(oldDefinition.ToCanonicalBytes(), current.Definitions.Single(value => value.Digest == oldDefinition.Digest).ToCanonicalBytes());
                }
                foreach (RegisteredGridRecipe oldRecipe in old.Recipes) {
                    Assert.Equal(oldRecipe.Recipe.ToCanonicalBytes(), current.Recipes.Single(value => value.Recipe.Digest == oldRecipe.Recipe.Digest).Recipe.ToCanonicalBytes());
                }
                Assert.IsType<RecapGridControlActivateResult.Applied>(control.Coordinator.CompareExchangeActiveRecipe(
                    registered, upgraded.Head, recipe.Digest, RecapGridControlActivationPurpose.Direct));
            }
            RecapGridStorePhysicalWitness witness = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
                RecapGridStoreMaintenance.PrepareReset(path)).Witness;
            var reset = Assert.IsType<RecapGridStoreResetResult.Reset>(RecapGridStoreMaintenance.Reset(path, witness));
            Assert.Equal(4, reset.Identity.SchemaVersion);
            var empty = Assert.IsType<RecapGridStoreInspectResult.Available>(RecapGridStoreMaintenance.Inspect(path)).Info;
            Assert.Equal(0, empty.CellCount);
            Assert.Equal(0, empty.RowViewCount);
            using (var runtime = new RecapCompletionRuntime(new PriorInputRouteResolver(route)))
            using (RecapGridManagerHandle manager = Assert.IsType<RecapGridManagerOpenResult.Opened>(
                       RecapGridManagerFactory.Open(journal.ReadView, _estimator)).Handle) {
                first = Assert.IsType<RecapGridBuildResult.Fulfilled>(await manager.Manager.BuildAsync(Request(), runtime));
            }
            Assert.Equal(rowCount * 2, provider.Requests.Count);
            Assert.Equal(rowCount, first.Metrics.RowViewsCommitted);
            for (int row = 0; row < rowCount; row++) {
                foreach (CompletionRequest request in provider.Requests.Skip(row * 2).Take(2)) {
                    using JsonDocument prior = JsonDocument.Parse(Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                        request.PromptPrefix.SharedContextMessages[0]).Content));
                    JsonElement[] entries = prior.RootElement.GetProperty("columns").EnumerateArray().ToArray();
                    Assert.Equal(row == 0 ? [] : columns, entries.Select(value => value.GetProperty("logicalColumnId").GetString()).ToArray());
                    Assert.Equal(row == 0 ? [] : columns.Select(column => column + ":generation=" + row).ToArray(),
                        entries.Select(value => value.GetProperty("content").GetString()).ToArray());
                }
            }
            AssertUpgradeCandidate(journal, columns, rowCount);
        }
        // All Journal, Manager, Runtime and Getter handles have closed.
        using (SessionJournalEngine cold = SessionJournalEngine.Open(path))
        using (var runtime = new RecapCompletionRuntime(new PriorInputRouteResolver(route)))
        using (RecapGridManagerHandle manager = Assert.IsType<RecapGridManagerOpenResult.Opened>(
                   RecapGridManagerFactory.Open(cold.ReadView, _estimator)).Handle) {
            var replay = Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await manager.Manager.BuildAsync(Request(maximumNewCalls: 0), runtime));
            Assert.Equal(first.Proof.RowResultId, replay.Proof.RowResultId);
            Assert.Equal(0, replay.Metrics.NewCalls);
            Assert.Equal(rowCount * 2, provider.Requests.Count);
            AssertUpgradeCandidate(cold, columns, rowCount);
        }
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(RecapGridStoreMaintenance.Verify(path));
        foreach ((string file, byte[] bytes) in preservedFiles) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
    }

    private void AssertUpgradeCandidate(SessionJournalEngine journal, string[] columns, int generation) {
        using RecapGridContextHandle getter = Assert.IsType<RecapGridContextOpenResult.Opened>(
            RecapGridContextFactory.Open(journal.ReadView, _estimator)).Handle;
        RecapGridContextSelection selected = Assert.IsType<RecapGridContextResolveResult.Selected>(
            getter.Resolve(journal.ReadCurrentHead()!.Value, 0)).Selection;
        var materialized = Assert.IsType<RecapGridContextMaterializeResult.Available>(getter.Materialize(selected));
        Assert.Equal(columns.Select(column => column + ":generation=" + generation).Order(StringComparer.Ordinal),
            materialized.Candidate.Contributions.Select(value => value.ExactText).Order(StringComparer.Ordinal));
        Assert.Equal(RecapGridProvenanceStatus.Verified, materialized.Provenance.PriorSourceAligned);
    }
}

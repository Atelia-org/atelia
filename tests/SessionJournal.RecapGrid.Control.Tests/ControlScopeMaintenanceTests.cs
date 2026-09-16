using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    [Fact]
    public void ExactMaintenanceInventoryReadsHistoricalScopesWithoutChangingLocatorOrControlBytes() {
        string path = NewPath();
        using SessionJournalEngine main = CreateTimeline(path);
        Values values = ValuesFor(path, main);
        ControlHeadRef historicalControl = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(path, main.BranchRefId, values.Admission)).Head;
        ActiveTimelineLocator historicalLocator;
        using (HistoryTimelineReaderHandle active = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
                   HistoryTimelineMaintenance.OpenReader(path, main.BranchRefId)).Handle) {
            historicalLocator = active.Locator;
        }
        HistoryTimelineAbandonResult.Abandoned abandoned = Assert.IsType<HistoryTimelineAbandonResult.Abandoned>(
            HistoryTimelineMaintenance.Abandon(path, main.BranchRefId, historicalLocator, InitialPolicy(), _estimator));
        ControlHeadRef activeControl = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(path, main.BranchRefId, values.Admission)).Head;

        RefId mainRef = main.BranchRefId;
        RefId featureRef;
        EventAddress head = main.ReadView.ReadCurrentHead()!.Value;
        main.Dispose();
        using (var raw = EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = raw.ForkBranch("scope-feature", mainRef, head).Unwrap();
        }
        using SessionJournalEngine feature = SessionJournalEngine.Open(path, "scope-feature");
        Assert.IsType<HistoryTimelineCreateResult.Created>(HistoryTimelineFactory.Create(feature.ReadView, InitialPolicy(), _estimator));
        Values featureValues = ValuesFor(path, feature);
        ControlHeadRef featureControl = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(path, feature.BranchRefId, featureValues.Admission)).Head;

        string locatorPath = Path.Combine(path, "derived", "history-timeline", "v2", "refs", mainRef.ToHexString(), "locator.json");
        string historicalControlPath = ControlStatePath(path, mainRef, historicalControl);
        string activeControlPath = ControlStatePath(path, mainRef, activeControl);
        byte[] locatorBytes = File.ReadAllBytes(locatorPath);
        byte[] historicalBytes = File.ReadAllBytes(historicalControlPath);
        byte[] activeBytes = File.ReadAllBytes(activeControlPath);

        HistoryTimelineScope[] timelines = Assert.IsType<HistoryTimelineScopeInventoryResult.Available>(
            HistoryTimelineMaintenance.InventoryScopes(path)).Scopes.ToArray();
        Assert.Equal(timelines.OrderBy(x => x.RefId.ToHexString(), StringComparer.Ordinal).ThenBy(x => x.TimelineId.Value, StringComparer.Ordinal), timelines);
        Assert.Contains(timelines, x => x.RefId == mainRef && x.TimelineId == historicalControl.TimelineId);
        Assert.Contains(timelines, x => x.RefId == mainRef && x.TimelineId == activeControl.TimelineId);
        Assert.Contains(timelines, x => x.RefId == featureRef && x.TimelineId == featureControl.TimelineId);
        RecapGridControlScope[] controls = Assert.IsType<RecapGridControlScopeInventoryResult.Available>(
            RecapGridControlMaintenance.InventoryScopes(path)).Scopes.ToArray();
        Assert.Equal(3, controls.Length);

        AssertExactHistory(path, mainRef, historicalControl.TimelineId);
        AssertExactHistory(path, mainRef, activeControl.TimelineId);
        AssertExactControl(path, mainRef, historicalControl);
        AssertExactControl(path, mainRef, activeControl);
        AssertExactControl(path, featureRef, featureControl);
        Assert.Equal(activeControl.TimelineId, abandoned.Locator.ActiveTimelineId);
        Assert.Equal(locatorBytes, File.ReadAllBytes(locatorPath));
        Assert.Equal(historicalBytes, File.ReadAllBytes(historicalControlPath));
        Assert.Equal(activeBytes, File.ReadAllBytes(activeControlPath));
    }

    [Fact]
    public void InventoryRejectsForeignEntriesAndExactReaderRejectsForeignIdentity() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef control = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(path, journal.BranchRefId, values.Admission)).Head;
        string refs = Path.Combine(path, "derived", "history-timeline", "v2", "refs");
        File.WriteAllText(Path.Combine(refs, "foreign"), "x");
        Assert.IsType<HistoryTimelineScopeInventoryResult.Invalid>(HistoryTimelineMaintenance.InventoryScopes(path));
        File.Delete(Path.Combine(refs, "foreign"));
        string timelines = Path.Combine(refs, journal.BranchRefId.ToHexString(), "timelines");
        Directory.CreateDirectory(Path.Combine(timelines, "foreign"));
        Assert.IsType<HistoryTimelineScopeInventoryResult.Invalid>(HistoryTimelineMaintenance.InventoryScopes(path));
        Directory.Delete(Path.Combine(timelines, "foreign"));
        File.CreateSymbolicLink(Path.Combine(timelines, "linked.sqlite"), Path.Combine(timelines, $"{control.TimelineId.Value}.sqlite"));
        Assert.IsType<HistoryTimelineScopeInventoryResult.Invalid>(HistoryTimelineMaintenance.InventoryScopes(path));
        File.Delete(Path.Combine(timelines, "linked.sqlite"));
        string other = "11111111111111111111111111111111.sqlite";
        File.Copy(Path.Combine(timelines, $"{control.TimelineId.Value}.sqlite"), Path.Combine(timelines, other));
        Assert.IsType<HistoryTimelineExactReaderOpenResult.Invalid>(HistoryTimelineMaintenance.OpenExactReader(path, journal.BranchRefId, new TimelineId(other[..^7])));
        Assert.IsType<HistoryTimelineScopeInventoryResult.Invalid>(HistoryTimelineMaintenance.InventoryScopes(path));
        File.Delete(Path.Combine(timelines, other));
        string controlRefs = Path.Combine(path, "control", "recap-grid", "v1", "refs");
        File.WriteAllText(Path.Combine(controlRefs, "foreign"), "x");
        Assert.IsType<RecapGridControlScopeInventoryResult.Invalid>(RecapGridControlMaintenance.InventoryScopes(path));
        File.Delete(Path.Combine(controlRefs, "foreign"));
        string controlTimelines = Path.Combine(controlRefs, journal.BranchRefId.ToHexString(), "timelines");
        File.CreateSymbolicLink(Path.Combine(controlTimelines, "linked"), Path.Combine(controlTimelines, control.TimelineId.Value));
        Assert.IsType<RecapGridControlScopeInventoryResult.Invalid>(RecapGridControlMaintenance.InventoryScopes(path));
    }

    private static HistoryTimelineInitialPolicySpec InitialPolicy() => new(
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        new HistoryLoadUnit(1), 8, 1024 * 1024);

    private static void AssertExactHistory(string path, RefId refId, TimelineId timelineId) {
        using HistoryTimelineExactReaderHandle handle = Assert.IsType<HistoryTimelineExactReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenExactReader(path, refId, timelineId)).Handle;
        Assert.Equal(timelineId, Assert.IsType<HistoryTimelineSnapshotResult.Available>(handle.Reader.ReadSnapshot()).Head.TimelineId);
    }

    private static void AssertExactControl(string path, RefId refId, ControlHeadRef expected) {
        using RecapGridControlReaderHandle handle = Assert.IsType<RecapGridControlReaderOpenResult.Opened>(
            RecapGridControlMaintenance.OpenExactReader(path, refId, expected.TimelineId)).Handle;
        Assert.Equal(expected, Assert.IsType<RecapGridControlSnapshotResult.Available>(handle.Reader.ReadSnapshot()).Snapshot.Head);
    }
}

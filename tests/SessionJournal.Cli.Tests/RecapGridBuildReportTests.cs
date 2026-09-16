using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class RecapGridBuildReportTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalReportRetainsConcreteOutcomeFieldsAndMetrics(bool includeEvidence) {
        var row = new HistoryRowId(new string('a', 64));
        var recipe = new GridBuildRecipeDigest(new string('b', 64));
        var slot = new CellSlot(recipe, row, new LogicalColumnId("case.culprit"));
        var timeline = new TimelineHeadRef(new TimelineId(new string('1', 32)), new RefId(1),
            null, new string('c', 64), null, 0, HistoryTimelineSelectedPath.EmptyDigest, 7);
        var control = new ControlHeadRef(new ControlInstanceId(new string('2', 32)), new RefId(1),
            timeline.TimelineId, 8, new ControlStateDigest(new string('d', 64)), null);
        var failure = new RecapGridCellFailure(0, slot, "ProviderFailure", "Specific provider cause", false);
        var cases = new (RecapGridBuildResult Result, string Status, string? Field)[] {
            (new RecapGridBuildResult.NoRows(timeline, recipe), "no-rows", "TimelineHead"),
            (new RecapGridBuildResult.NoActiveRecipe(), "no-active-recipe", null),
            (new RecapGridBuildResult.RecipeAbsent(recipe), "recipe-absent", "RecipeDigest"),
            (new RecapGridBuildResult.ProducerPolicyRequired(recipe, row), "producer-policy-required", "RootRecipeDigest"),
            (new RecapGridBuildResult.ThroughRowNotSelected(row), "through-row-not-selected", "RowId"),
            (new RecapGridBuildResult.BudgetExceeded(RecapGridBuildBudgetKind.NewCalls, row), "budget-exceeded", "Kind"),
            (new RecapGridBuildResult.Cancelled(), "cancelled", null),
            (new RecapGridBuildResult.Incomplete(row, [failure]), "incomplete", "Failures"),
            (new RecapGridBuildResult.ExecutorRejected("RouteClientConstructionFailed", "Specific constructor cause"), "executor-rejected", "Code"),
            (new RecapGridBuildResult.ExecutorFailed("ExecutorFailedAfterDispatch", "Specific executor cause"), "executor-failed", "Code"),
            (new RecapGridBuildResult.Unavailable(RecapGridBuildDependency.Store, "StoreUnavailable", "Specific store cause"), "unavailable", "Dependency"),
            (new RecapGridBuildResult.StaleTimelineHead(timeline), "stale-timeline-head", "Actual"),
            (new RecapGridBuildResult.StaleControlAuthority(control), "stale-control-authority", "Actual"),
            (new RecapGridBuildResult.SettlementRequired(RecapGridBuildCommitKind.Cell, "intended-slot", "observed-cell"), "settlement-required", "IntendedIdentity"),
            (new RecapGridBuildResult.Disposed(), "disposed", null),
            (new RecapGridBuildResult.Invalid("InputInvalid", "Specific validation cause"), "invalid", "Code")
        };
        var metrics = new RecapGridBuildMetrics(1, 2, 3, 4, 5);
        foreach ((RecapGridBuildResult original, string status, string? field) in cases) {
            RecapGridBuildResult result = original with { Metrics = metrics };
            RecapCompletionTelemetrySnapshot? evidence = includeEvidence ? new([], 7, 0) : null;
            (int exitCode, JsonElement report) = Capture(result, evidence);
            Assert.Equal(status == "no-rows" ? 0 : 2, exitCode);
            Assert.Equal("build", report.GetProperty("command").GetString());
            Assert.Equal(status, report.GetProperty("status").GetString());
            JsonElement detail = report.GetProperty("detail");
            JsonElement actual = includeEvidence ? detail.GetProperty("result") : detail;
            if (field is not null) Assert.True(actual.TryGetProperty(field, out _), $"{status} lost {field}");
            Assert.Equal(3, actual.GetProperty("Metrics").GetProperty("NewCalls").GetInt32());
            // Compare final Console JSON against the concrete contract, catching
            // any other lost payload field as new fields join these outcomes.
            JsonElement expected = JsonSerializer.SerializeToElement(result, result.GetType());
            Assert.Equal(expected.GetRawText(), actual.GetRawText());
            if (actual.TryGetProperty("Code", out _)) {
                Assert.StartsWith("Specific ", actual.GetProperty("Detail").GetString());
            }
            if (result is RecapGridBuildResult.Incomplete) {
                JsonElement failed = Assert.Single(actual.GetProperty("Failures").EnumerateArray());
                Assert.Equal("ProviderFailure", failed.GetProperty("Code").GetString());
                Assert.Equal("Specific provider cause", failed.GetProperty("Detail").GetString());
                Assert.False(failed.GetProperty("NotStarted").GetBoolean());
                Assert.Equal("case.culprit", failed.GetProperty("Slot").GetProperty("LogicalColumnId").GetProperty("Value").GetString());
            }
            if (includeEvidence) Assert.Equal(7, detail.GetProperty("evidence").GetProperty("DroppedEventCount").GetInt64());
        }
    }

    [Theory]
    [InlineData("executor-failed")]
    [InlineData("incomplete")]
    [InlineData("settlement-required")]
    public void EscapedEvidenceOverflowKeepsBusinessOutcomeAndExplicitlyReportsOmission(string status) {
        var slot = new CellSlot(new GridBuildRecipeDigest(new string('b', 64)),
            new HistoryRowId(new string('a', 64)), new LogicalColumnId("case.culprit"));
        var family = new FamilyDefinitionDigest(new string('c', 64));
        var collector = new BoundedRecapCompletionTelemetry();
        var settled = new RecapCompletionTelemetryEvent("completion-settled",
            new RecapCompletionRouteKey(family, RecapRewriterProtocolV3.RuntimeProtocolId, null),
            "connection", "model", "test", "test-v1", slot, new string('1', 32), 4, null,
            family, new MaintainerDefinitionDigest(new string('d', 64)), RecapCompletionWorkRole.Leader,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, PromptCacheReuseHint.ConnectionDefault,
            false, null, 0, null, "failed", "ProviderFailure", new string('\u0001', 4_096));
        for (int index = 0; index < 1_024; index++) collector.Record(settled);
        RecapCompletionTelemetrySnapshot evidence = collector.ReadSnapshot();
        Assert.NotEmpty(evidence.Events);
        Assert.InRange(evidence.RetainedUtf8Bytes, 1, 4 * 1024 * 1024);
        RecapGridBuildResult result = status switch {
            "executor-failed" => new RecapGridBuildResult.ExecutorFailed("ExactFailure", "Exact lower cause"),
            "incomplete" => new RecapGridBuildResult.Incomplete(slot.HistoryRowId,
                [new RecapGridCellFailure(0, slot, "ExactFailure", "Exact lower cause", false)]),
            _ => new RecapGridBuildResult.SettlementRequired(RecapGridBuildCommitKind.Cell,
                "exact-intended", "exact-observed")
        };
        (int exitCode, JsonElement report) = Capture(result, evidence);
        Assert.Equal(2, exitCode);
        Assert.Equal(status, report.GetProperty("status").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(report.GetRawText()) <= RecapGridCommands.MaximumReportUtf8Bytes);
        JsonElement detail = report.GetProperty("detail");
        Assert.True(detail.GetProperty("evidenceOmitted").GetBoolean());
        Assert.Equal(evidence.Events.Count, detail.GetProperty("evidenceOmittedEventCount").GetInt32());
        Assert.Equal(evidence.DroppedEventCount, detail.GetProperty("evidenceDroppedEventCount").GetInt64());
        Assert.False(detail.TryGetProperty("evidence", out _));
        JsonElement payload = detail.GetProperty("result");
        if (status == "settlement-required") {
            Assert.Equal("exact-intended", payload.GetProperty("IntendedIdentity").GetString());
            Assert.Equal("exact-observed", payload.GetProperty("ObservedIdentity").GetString());
        }
        else {
            JsonElement failure = status == "incomplete"
                ? Assert.Single(payload.GetProperty("Failures").EnumerateArray()) : payload;
            Assert.Equal("ExactFailure", failure.GetProperty("Code").GetString());
            Assert.Equal("Exact lower cause", failure.GetProperty("Detail").GetString());
        }
    }

    private static (int ExitCode, JsonElement Report) Capture(RecapGridBuildResult result,
        RecapCompletionTelemetrySnapshot? evidence) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = RecapGridCommands.PrintBuildResult("build", result, evidence);
            using JsonDocument report = JsonDocument.Parse(output.ToString());
            return (exitCode, report.RootElement.Clone());
        }
        finally { Console.SetOut(original); }
    }
}

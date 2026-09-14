using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class RecapGridBuildProgressWriterTests {
    [Fact]
    public async Task WaitingTracksOnlyActualInflightCallsAndStopsBeforeSummary() {
        var capture = new ProgressCapture();
        await using var progress = new RecapGridBuildProgressWriter(capture, TimeSpan.FromMilliseconds(10));
        progress.Record(Event("completion-settled", "not-started"));
        progress.Record(Event("completion-started", "invoking"));
        await capture.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("request-start", capture.Text);
        Assert.Contains("waiting request=1 elapsed-seconds=", capture.Text);
        Assert.DoesNotContain("request-end", capture.Text);
        progress.Record(Event("completion-settled", "failed"));
        await progress.FinishAsync(new RecapGridBuildResult.ExecutorFailed("test", "private-body"));
        string final = capture.Text;
        Assert.Contains("request-end request=1", final);
        Assert.Contains("requests-started=1 requests-ended=1 requests-pending=0", final);
        Assert.EndsWith("row-views-committed=0" + Environment.NewLine, final);
        Assert.DoesNotContain("private-body", final);
        Assert.Contains("connection=test-connection", final);
        await progress.DisposeAsync();
        progress.Record(Event("completion-started", "invoking"));
        await Task.Delay(40);
        Assert.Equal(final, capture.Text);
    }

    [Fact]
    public async Task ZeroCallsProduceOnlySummaryAndNoWaiting() {
        var capture = new ProgressCapture();
        await using var progress = new RecapGridBuildProgressWriter(capture, TimeSpan.FromMilliseconds(10));
        await Task.Delay(40);
        Assert.Equal("", capture.Text);
        await progress.FinishAsync(new RecapGridBuildResult.ExecutorFailed("preflight", "private-body"));
        Assert.StartsWith("[recap-build] summary", capture.Text);
        Assert.Contains("requests-started=0 requests-ended=0", capture.Text);
        Assert.Single(capture.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task ConcurrentCallsKeepSeparateStartsAndEnds() {
        var capture = new ProgressCapture();
        await using var progress = new RecapGridBuildProgressWriter(capture);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            progress.Record(Event("completion-started", "invoking", "case.column-" + index)))));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            progress.Record(Event("completion-settled", "failed", "case.column-" + index)))));
        await progress.FinishAsync(new RecapGridBuildResult.ExecutorFailed("test", "private-body"));
        string[] lines = capture.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16, lines.Count(line => line.StartsWith("[recap-build] request-start")));
        Assert.Equal(16, lines.Count(line => line.StartsWith("[recap-build] request-end")));
        Assert.Contains("requests-started=16 requests-ended=16 requests-pending=0", lines[^1]);
    }

    [Fact]
    public async Task ClosedStderrCannotChangeObservationOrFinish() {
        await using var progress = new RecapGridBuildProgressWriter(new BrokenWriter(), TimeSpan.FromMilliseconds(10));
        progress.Record(Event("completion-started", "invoking"));
        await Task.Delay(40);
        progress.Record(Event("completion-settled", "failed"));
        await progress.FinishAsync(new RecapGridBuildResult.ExecutorFailed("test", "private-body"));
    }

    private static RecapCompletionTelemetryEvent Event(string kind, string outcome, string column = "case.column") {
        var family = new FamilyDefinitionDigest(new string('c', 64));
        return new(kind,
            new RecapCompletionRouteKey(family, RecapRewriterProtocolV3.RuntimeProtocolId, null),
            "test-connection", "model", "test", "test-v1",
            new CellSlot(new GridBuildRecipeDigest(new string('b', 64)),
                new HistoryRowId(new string('a', 64)), new LogicalColumnId(column)),
            new string('1', 32), 4, null, family,
            new MaintainerDefinitionDigest(new string('d', 64)), RecapCompletionWorkRole.Leader,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, PromptCacheReuseHint.ConnectionDefault,
            false, null, 0, null, outcome, null, "private-body");
    }

    private sealed class ProgressCapture : TextWriter {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Text { get { lock (_gate) { return _text.ToString(); } } }
        public override void WriteLine(string? value) {
            lock (_gate) { _text.AppendLine(value); }
            if (value?.Contains("[recap-build] waiting") == true) { Waiting.TrySetResult(); }
        }
    }

    private sealed class BrokenWriter : TextWriter {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("closed");
    }
}

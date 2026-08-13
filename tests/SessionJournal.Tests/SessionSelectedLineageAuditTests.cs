using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionSelectedLineageAuditTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void PagedCapture_IsExactBoundedAndMaterializesForwardRange() {
        string path = NewPath();
        EventAddress observation;
        EventAddress action;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            observation = writer.AppendObservation("fact-A");
            action = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("answer-A")
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                )
            );
        }

        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!capture.IsCaptureComplete) {
            pages.Add(capture.ReadNextPage(maxEventCount: 2));
        }
        _ = capture.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(capture.Capture, pages)
            );
        SessionSelectedLineageAuditAuthority authority =
            cursor.Authority;

        Assert.Equal(action, authority.Capture.CapturedHead);
        Assert.Equal(5, authority.EventCount);
        Assert.Equal(2, authority.MaximumResidentEntryCount);
        Assert.Equal(3, pages.Count);
        AssertPageChain(pages, authority.Capture.CapturedHead);

        SessionSelectedLineageForwardRange requestedRange =
            Assert.IsType<SessionSelectedLineageForwardRange>(
                cursor.ReadNextRange(maxRawEventCount: 10)
            );
        Assert.True(requestedRange.IsFinal);
        SessionHistoryPlanningWindow window = cursor.Materialize(
            requestedRange
        );

        Assert.Equal([observation, action], window.RawAddresses);
        Assert.Equal(action, window.ObservedRawHead);
        Assert.Equal(
            authority.BootstrapSeed.Address,
            window.StartExclusive
        );
    }

    [Fact]
    public void ForwardCursor_OwnsSeedAndRejectsReadAhead() {
        string path = CreateLongFixture(extraEventCount: 4);
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!capture.IsCaptureComplete) {
            pages.Add(capture.ReadNextPage(maxEventCount: 2));
        }
        _ = capture.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(capture.Capture, pages)
            );

        SessionSelectedLineageForwardRange pending =
            Assert.IsType<SessionSelectedLineageForwardRange>(
                cursor.ReadNextRange(maxRawEventCount: 4)
            );
        Assert.Throws<InvalidOperationException>(
            () => cursor.ReadNextRange(maxRawEventCount: 4)
        );
        Assert.DoesNotContain(
            typeof(SessionSelectedLineageForwardCursor)
                .GetMethod(nameof(cursor.Materialize))!
                .GetParameters(),
            static parameter => parameter.ParameterType
                == typeof(SessionHistoryPlanningSeed)
        );

        _ = cursor.Materialize(pending);
    }

    [Fact]
    public void ForwardCursor_PreviewConsumesReplaySafePrefixWithoutLosingSuffix() {
        string path = NewPath();
        EventAddress firstAction;
        EventAddress secondAction;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            _ = writer.AppendObservation("first");
            firstAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("second");
            secondAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("two")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        SessionSelectedLineageForwardRange range = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ReadNextRange(10));

        SessionHistoryPlanningWindow preview = cursor.Preview(range);
        Assert.Equal(secondAction, preview.ObservedRawHead);
        SessionSelectedLineageForwardConsumption first =
            cursor.ConsumePreviewedPrefix(range, firstAction);
        Assert.Equal(firstAction, first.Window.ObservedRawHead);
        Assert.NotNull(first.RemainingRange);
        Assert.Equal(firstAction, first.RemainingRange!.StartExclusive);
        Assert.Equal(secondAction, first.RemainingRange.EndInclusive);

        SessionHistoryPlanningWindow remainingPreview = cursor.Preview(
            first.RemainingRange
        );
        Assert.Equal(firstAction, remainingPreview.StartExclusive);
        SessionSelectedLineageForwardConsumption second =
            cursor.ConsumePreviewedPrefix(
                first.RemainingRange,
                secondAction
            );
        Assert.Null(second.RemainingRange);
        Assert.Null(cursor.ReadNextRange(1));
    }

    [Fact]
    public void ForwardCursor_ExtendsExactConsumedRemainderAcrossLaterEntries() {
        string path = NewPath();
        EventAddress firstAction;
        EventAddress finalAction;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            _ = writer.AppendObservation("first");
            firstAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("second");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("two")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("third");
            finalAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("three")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        SessionSelectedLineageForwardRange initial = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ReadNextRange(4));

        _ = cursor.Preview(initial);
        SessionSelectedLineageForwardRange previewExtended =
            cursor.ExtendPendingRange(initial, 6);
        Assert.Throws<ArgumentException>(() =>
            cursor.Materialize(initial));
        _ = cursor.Preview(previewExtended);
        SessionSelectedLineageForwardRange remainder = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ConsumePreviewedPrefix(previewExtended, firstAction)
            .RemainingRange);
        Assert.Equal(4, remainder.Entries.Count);
        Assert.True(remainder.IsFinal);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cursor.ExtendPendingRange(remainder, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cursor.ExtendPendingRange(
                remainder,
                SessionSelectedLineageAuditLimits
                    .MaximumForwardRangeEventCount + 1
            ));

        SessionSelectedLineageForwardRange extended =
            cursor.ExtendPendingRange(remainder, 4);

        Assert.NotSame(remainder, extended);
        Assert.Equal(firstAction, extended.StartExclusive);
        Assert.Equal(4, extended.Entries.Count);
        Assert.Equal(finalAction, extended.EndInclusive);
        Assert.True(extended.IsFinal);
        Assert.Throws<ArgumentException>(() =>
            cursor.Materialize(remainder));
        SessionHistoryPlanningWindow exact = cursor.Materialize(
            extended
        );
        Assert.Equal(firstAction, exact.StartExclusive);
        Assert.Equal(finalAction, exact.ObservedRawHead);
        Assert.Equal(4, exact.RawAddresses.Count);
        Assert.Null(cursor.ReadNextRange(1));
    }

    [Fact]
    public void ForwardCursorForkAndSourceHaveIndependentSharedSnapshotLeases() {
        string path = NewPath();
        EventAddress firstAction;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A", "system-A", "surface-A"))) {
            _ = writer.AppendObservation("first");
            firstAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("one")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            _ = writer.AppendObservation("second");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("two")]),
                new CompletionDescriptor("import", "v1", "model-A"));
        }
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        var snapshot = new CountingPageSnapshot(audit.Capture, pages);

        SessionSelectedLineageForwardCursor source =
            engine.OpenSelectedLineageForwardCursor(snapshot);
        SessionSelectedLineageForwardCursor fork =
            source.ForkAtBoundary(
                source.Authority.BootstrapSeed.Address,
                source.Authority.BootstrapSeed.Setups);
        source.Dispose();
        Assert.Equal(0, snapshot.DisposeCount);
        Assert.NotNull(fork.ReadNextRange(2));
        fork.Dispose();
        Assert.Equal(1, snapshot.DisposeCount);

        SessionSelectedLineageForwardCursor secondSource =
            engine.OpenSelectedLineageForwardCursor(
                new CountingPageSnapshot(audit.Capture, pages));
        SessionSelectedLineageForwardCursor secondFork =
            secondSource.ForkAtBoundary(
                firstAction,
                secondSource.Preview(Assert.IsType<
                    SessionSelectedLineageForwardRange>(
                    secondSource.ReadNextRange(4)))
                    .ReplaySafeBoundarySetups[firstAction]);
        secondFork.Dispose();
        Assert.NotNull(secondSource.ReadCurrentHead());
        secondSource.Dispose();
    }

    [Fact]
    public void ForwardCursor_ExtendRejectsRangeOwnedByAnotherCursor() {
        string path = CreateLongFixture(extraEventCount: 6);
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor first =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        using SessionSelectedLineageForwardCursor second =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        SessionSelectedLineageForwardRange firstRange = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(first.ReadNextRange(2));
        _ = second.ReadNextRange(2);

        Assert.Throws<ArgumentException>(() =>
            second.ExtendPendingRange(firstRange, 4));
    }

    [Fact]
    public void ForwardCursor_ExtendRejectsDisposedCursor() {
        string path = CreateLongFixture(extraEventCount: 3);
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        SessionSelectedLineageForwardRange pending = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ReadNextRange(2));
        cursor.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            cursor.ExtendPendingRange(pending, 3));
        Assert.Throws<ObjectDisposedException>(() =>
            cursor.Preview(pending));
        Assert.Throws<ObjectDisposedException>(() =>
            cursor.Materialize(pending));
    }

    [Fact]
    public void ForwardCursor_ExtendRejectsRawHeadDrift() {
        string path = CreateLongFixture(extraEventCount: 6);
        EventAddress rewrittenHead = default;
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            new SessionRuntime(
                new UnusedCompletionClient(),
                CompletionTarget: new SessionCompletionTargetIdentity(
                    "audit-extend-test",
                    "test",
                    "audit-extend-v1",
                    "audit-extend-adapter-v1"
                ),
                ContextCandidateSource: source
            ),
            new SessionJournalTestHooks(
                RewritePendingRangeExtendObservedHead:
                    observed => rewrittenHead == default
                        ? observed
                        : rewrittenHead
            )
        );
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        SessionSelectedLineageForwardRange range = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ReadNextRange(4));
        SessionHistoryPlanningWindow preview = cursor.Preview(range);
        EventAddress firstBoundary = preview.ReplaySafeBoundaries[0]
            .Address;
        SessionSelectedLineageForwardRange pending = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ConsumePreviewedPrefix(range, firstBoundary)
            .RemainingRange);
        rewrittenHead = pending.EndInclusive;

        SessionSelectedLineageAuditChangedException error =
            Assert.Throws<SessionSelectedLineageAuditChangedException>(
                () => cursor.ExtendPendingRange(pending, 4)
            );
        Assert.Equal(
            SessionSelectedLineageAuditChangeKind.RawHeadChanged,
            error.Kind
        );
        AssertInvalidated(cursor, pending);
    }

    [Fact]
    public void ForwardCursor_ExtendCancellationAfterReadInvalidatesCursor() {
        string path = CreateLongFixture(extraEventCount: 6);
        using var cancellation = new CancellationTokenSource();
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            new SessionRuntime(
                new UnusedCompletionClient(),
                CompletionTarget: new SessionCompletionTargetIdentity(
                    "audit-extend-cancel-test",
                    "test",
                    "audit-extend-v1",
                    "audit-extend-adapter-v1"),
                ContextCandidateSource: source),
            new SessionJournalTestHooks(
                AfterPendingRangeExtendEntryRead: cancellation.Cancel));
        (SessionSelectedLineageForwardCursor cursor,
            SessionSelectedLineageForwardRange pending) =
            OpenExtendablePendingRange(engine);
        using (cursor) {
            Assert.Throws<OperationCanceledException>(() =>
                cursor.ExtendPendingRange(pending, 4, cancellation.Token));
            AssertInvalidated(cursor, pending);
        }
    }

    [Fact]
    public void ForwardCursor_ExtendValidationFailureAfterReadInvalidatesCursor() {
        string path = CreateLongFixture(extraEventCount: 6);
        var source = new TestContextCandidateSource();
        using var engine = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            new SessionRuntime(
                new UnusedCompletionClient(),
                CompletionTarget: new SessionCompletionTargetIdentity(
                    "audit-extend-invalid-test",
                    "test",
                    "audit-extend-v1",
                    "audit-extend-adapter-v1"),
                ContextCandidateSource: source),
            new SessionJournalTestHooks(
                RewritePendingRangeExtendEntry: entry => entry with {
                    SequenceNumber = 0
                }));
        (SessionSelectedLineageForwardCursor cursor,
            SessionSelectedLineageForwardRange pending) =
            OpenExtendablePendingRange(engine);
        using (cursor) {
            Assert.Throws<InvalidDataException>(() =>
                cursor.ExtendPendingRange(pending, 4));
            AssertInvalidated(cursor, pending);
        }
    }

    [Fact]
    public void ForwardCursor_SeekRequiresExactGoverningSetupsAndContinuesAfterBoundary() {
        string path = NewPath();
        EventAddress firstAction;
        EventAddress laterSetup;
        EventAddress finalAction;
        SessionContextAnchorSetupReferences firstSetups;
        SessionContextAnchorSetupReferences laterSetups;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            _ = writer.AppendObservation("first");
            firstAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            firstSetups = writer.ResolveContextAnchorSetupReferences(
                firstAction
            );
            laterSetup = writer.AppendSystemPromptSetup("system-B");
            laterSetups = writer.ResolveContextAnchorSetupReferences(
                laterSetup
            );
            _ = writer.AppendObservation("second");
            finalAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("two")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        var snapshot = new InMemoryPageSnapshot(audit.Capture, pages);

        using (SessionSelectedLineageForwardCursor forged =
               engine.OpenSelectedLineageForwardCursor(snapshot)) {
            Assert.Throws<InvalidDataException>(() =>
                forged.SeekToBoundary(firstAction, laterSetups));
        }

        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(snapshot);
        cursor.SeekToBoundary(firstAction, firstSetups);
        Assert.Equal(firstAction, cursor.CurrentBoundary);
        SessionSelectedLineageForwardRange remaining = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ReadNextRange(8));
        Assert.Equal(firstAction, remaining.StartExclusive);
        Assert.Equal(finalAction, remaining.EndInclusive);
        Assert.Contains(
            remaining.Entries,
            entry => entry.Address == laterSetup
        );
        Assert.Equal(finalAction, cursor.Materialize(remaining).ObservedRawHead);
        Assert.Null(cursor.ReadNextRange(1));

        using SessionSelectedLineageForwardCursor membership =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        Assert.Equal(
            finalAction,
            membership.FindLatestMatchingBoundary(
                new HashSet<EventAddress> {
                    firstAction,
                    finalAction
                }
            )
        );
        Assert.Null(membership.ReadNextRange(1));
        AssertInspectionOperationsRejected(membership);
    }

    [Fact]
    public void ForwardCursor_BoundaryProbeStreamsLatestMatchAndExhaustsInspection() {
        string path = CreateLongFixture(extraEventCount: 6);
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        EventAddress first = cursor.Authority.BootstrapSeed.Address;
        EventAddress captured = cursor.Authority.Capture.CapturedHead;
        int inspected = 0;

        SessionSelectedLineageBoundaryProbeResult result =
            cursor.ProbeBoundaries(address => {
                inspected++;
                return address == first || address == captured
                    ? SessionSelectedLineageBoundaryProbeDecision.Match
                    : SessionSelectedLineageBoundaryProbeDecision.Continue;
            });

        Assert.Equal(captured, result.LatestMatchingBoundary);
        Assert.False(result.Stopped);
        Assert.True(inspected > 1);
        Assert.Null(cursor.ReadNextRange(1));
        AssertInspectionOperationsRejected(cursor);
    }

    [Fact]
    public void ForwardCursor_BoundaryProbeStopAndFailureAreFailClosed() {
        string path = CreateLongFixture(extraEventCount: 4);
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        var snapshot = new InMemoryPageSnapshot(audit.Capture, pages);
        using (SessionSelectedLineageForwardCursor stopped =
               engine.OpenSelectedLineageForwardCursor(snapshot)) {
            SessionSelectedLineageBoundaryProbeResult result =
                stopped.ProbeBoundaries(_ =>
                    SessionSelectedLineageBoundaryProbeDecision.Stop
                );
            Assert.True(result.Stopped);
            Assert.Null(stopped.ReadNextRange(1));
            AssertInspectionOperationsRejected(stopped);
        }

        using (SessionSelectedLineageForwardCursor stoppedMid =
               engine.OpenSelectedLineageForwardCursor(
                   new InMemoryPageSnapshot(audit.Capture, pages)
               )) {
            int inspected = 0;
            SessionSelectedLineageBoundaryProbeResult result =
                stoppedMid.ProbeBoundaries(_ => ++inspected == 2
                    ? SessionSelectedLineageBoundaryProbeDecision.Stop
                    : SessionSelectedLineageBoundaryProbeDecision.Continue
                );
            Assert.True(result.Stopped);
            Assert.Equal(2, inspected);
            Assert.Null(stoppedMid.ReadNextRange(1));
            AssertInspectionOperationsRejected(stoppedMid);
        }

        using SessionSelectedLineageForwardCursor failed =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        _ = Assert.Throws<InvalidOperationException>(() =>
            failed.ProbeBoundaries(_ =>
                throw new InvalidOperationException("probe failed")
            )
        );
        Assert.Null(failed.ReadNextRange(1));
        AssertInspectionOperationsRejected(failed);

        using SessionSelectedLineageForwardCursor canceled =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            canceled.ProbeBoundaries(
                _ => SessionSelectedLineageBoundaryProbeDecision.Continue,
                cancellation.Token
            )
        );
        Assert.Null(canceled.ReadNextRange(1));
        AssertInspectionOperationsRejected(canceled);
    }

    [Fact]
    public void ForwardCursor_BoundaryProbeFinalHeadDriftFailsTyped() {
        string path = CreateLongFixture(extraEventCount: 4);
        EventAddress rewritten = default;
        using var engine = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            new SessionJournalTestHooks(
                RewriteForwardBoundaryProbeObservedHead: observed =>
                    rewritten == default ? observed : rewritten
            )
        );
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        rewritten = cursor.Authority.BootstrapSeed.Address;

        SessionSelectedLineageAuditChangedException error =
            Assert.Throws<SessionSelectedLineageAuditChangedException>(
                () => cursor.ProbeBoundaries(_ =>
                    SessionSelectedLineageBoundaryProbeDecision.Continue
                )
            );

        Assert.Equal(
            SessionSelectedLineageAuditChangeKind.RawHeadChanged,
            error.Kind
        );
        Assert.Null(cursor.ReadNextRange(1));
        AssertInspectionOperationsRejected(cursor);
    }

    [Fact]
    public async Task ForwardCursor_FinalNonReplaySafeHeadMaterializesWithoutNextSeed() {
        string path = NewPath();
        var source = new TestContextCandidateSource {
            IsEmptyLineage = true
        };
        var runtime = new SessionRuntime(
            new UnusedCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "audit-final-range",
                "test",
                "audit-final-range-v1",
                "audit-final-range-adapter-v1"
            ),
            ContextCandidateSource: source
        );
        using (SessionJournalEngine writer =
               SessionJournalEngine.CreateForTest(
                   path,
                   new SessionCreateOptions(
                       "model-A",
                       "system-A",
                       "surface-A"
                   ),
                   runtime,
                   new SessionJournalTestHooks(
                       SessionJournalFailpoint
                           .AfterRequestPreparedCommitted
                   )
               )) {
            _ = await Assert.ThrowsAsync<
                SessionJournalFailpointException
            >(() => writer.SendAsync("prepare only"));
        }

        using var engine = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            runtime
        );
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages)
            );
        SessionSelectedLineageForwardRange final = Assert.IsType<
            SessionSelectedLineageForwardRange
        >(cursor.ReadNextRange(8));

        SessionHistoryPlanningWindow window = cursor.Materialize(final);

        Assert.True(final.IsFinal);
        Assert.Equal(
            SessionEventKind.CompletionRequestPrepared,
            cursor.Authority.ExecutionStateAtCapturedHead.HeadKind
        );
        Assert.Equal(final.EndInclusive, window.ObservedRawHead);
        Assert.Null(cursor.ReadNextRange(1));
    }

    [Fact]
    public void ForwardCursor_BootstrapOnlyLineageIsAlreadyComplete() {
        string path = NewPath();
        using (SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
        }
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession capture =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!capture.IsCaptureComplete) {
            pages.Add(capture.ReadNextPage(maxEventCount: 2));
        }
        SessionSelectedLineageAuditAuthority authority =
            capture.Complete();
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(capture.Capture, pages)
            );

        Assert.Equal(
            authority.BootstrapSeed.Address,
            authority.Capture.CapturedHead
        );
        Assert.Null(cursor.ReadNextRange(maxRawEventCount: 1));
    }

    [Fact]
    public void Resume_RechecksCommittedPagesAgainstRawAuthority() {
        string path = CreateLongFixture(extraEventCount: 9);
        SessionSelectedLineageAuditCapture capture;
        SessionSelectedLineageAuditPage first;
        using (var initial = SessionJournalEngine.OpenReadOnly(path)) {
            SessionSelectedLineageAuditSession session =
                initial.BeginSelectedLineageAudit();
            first = session.ReadNextPage(maxEventCount: 4);
            capture = session.Capture;
        }

        using var reopened = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession resumed =
            reopened.ResumeSelectedLineageAudit(capture, [first]);

        Assert.Equal(1, resumed.CommittedPageCount);
        Assert.Equal(first.Continuation, resumed.NextAddress);
        while (!resumed.IsCaptureComplete) {
            _ = resumed.ReadNextPage(maxEventCount: 4);
        }
        SessionSelectedLineageAuditAuthority authority =
            resumed.Complete();
        Assert.Equal(12, authority.EventCount);
        Assert.Equal(4, authority.MaximumResidentEntryCount);
    }

    [Fact]
    public void Resume_RejectsTamperedCommittedPage() {
        string path = CreateLongFixture(extraEventCount: 3);
        SessionSelectedLineageAuditCapture capture;
        SessionSelectedLineageAuditPage first;
        using (var initial = SessionJournalEngine.OpenReadOnly(path)) {
            SessionSelectedLineageAuditSession session =
                initial.BeginSelectedLineageAudit();
            first = session.ReadNextPage(maxEventCount: 2);
            capture = session.Capture;
        }
        SessionSelectedLineageAuditEntry original =
            first.HeadToOldest[0];
        SessionSelectedLineageAuditEntry tampered = original with {
            PayloadSha256 = new string('0', 64)
        };
        var corruptPage = first with {
            HeadToOldest = [
                tampered,
                .. first.HeadToOldest.Skip(1)
            ]
        };

        using var reopened = SessionJournalEngine.OpenReadOnly(path);
        Assert.Throws<InvalidDataException>(
            () => reopened.ResumeSelectedLineageAudit(
                capture,
                [corruptPage]
            )
        );
    }

    [Fact]
    public void ForwardCursor_RejectsDivergentSecondPassBeforeIssuingCursor() {
        string path = CreateLongFixture(extraEventCount: 5);
        var pages = new List<SessionSelectedLineageAuditPage>();
        SessionSelectedLineageAuditCapture capture;
        using (var initial = SessionJournalEngine.OpenReadOnly(path)) {
            SessionSelectedLineageAuditSession session =
                initial.BeginSelectedLineageAudit();
            while (!session.IsCaptureComplete) {
                pages.Add(session.ReadNextPage(maxEventCount: 3));
            }
            _ = session.Complete();
            capture = session.Capture;
        }

        using var reopened = SessionJournalEngine.OpenReadOnly(path);
        Assert.Throws<InvalidDataException>(
            () => reopened.OpenSelectedLineageForwardCursor(
                new DivergentForwardPageSnapshot(capture, pages)
            )
        );
    }

    [Fact]
    public void Resume_AfterSelectedHeadChanges_FailsTyped() {
        string path = CreateLongFixture(extraEventCount: 3);
        SessionSelectedLineageAuditCapture capture;
        SessionSelectedLineageAuditPage first;
        using (var initial = SessionJournalEngine.OpenReadOnly(path)) {
            SessionSelectedLineageAuditSession session =
                initial.BeginSelectedLineageAudit();
            first = session.ReadNextPage(maxEventCount: 2);
            capture = session.Capture;
        }
        using (var writer = SessionJournalEngine.Open(path)) {
            _ = writer.AppendSystemPromptSetup("later-system");
        }

        using var reopened = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditChangedException error =
            Assert.Throws<SessionSelectedLineageAuditChangedException>(
                () => reopened.ResumeSelectedLineageAudit(
                    capture,
                    [first]
                )
            );
        Assert.Equal(
            SessionSelectedLineageAuditChangeKind.RawHeadChanged,
            error.Kind
        );
    }

    [Fact]
    public void Capture_OverFiveHundredThirteenEvents_RemainsPaged() {
        string path = CreateLongFixture(extraEventCount: 520);
        using var engine = SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession session =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!session.IsCaptureComplete) {
            pages.Add(session.ReadNextPage(maxEventCount: 64));
        }
        SessionSelectedLineageAuditAuthority authority =
            session.Complete();

        Assert.Equal(523, authority.EventCount);
        Assert.Equal(9, pages.Count);
        Assert.Equal(64, authority.MaximumResidentEntryCount);
        AssertPageChain(pages, authority.Capture.CapturedHead);
    }

    [Fact]
    public void Capture_IsExplicitlyOfflineOnly() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

        Assert.Throws<InvalidOperationException>(
            () => engine.BeginSelectedLineageAudit()
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    private string CreateLongFixture(int extraEventCount) {
        string path = NewPath();
        using var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        for (int index = 0; index < extraEventCount; index++) {
            _ = writer.AppendSystemPromptSetup($"system-{index}");
        }
        return path;
    }

    private static (
        SessionSelectedLineageForwardCursor Cursor,
        SessionSelectedLineageForwardRange Pending
    ) OpenExtendablePendingRange(SessionJournalEngine engine) {
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(2));
        }
        _ = audit.Complete();
        SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(
                new InMemoryPageSnapshot(audit.Capture, pages));
        SessionSelectedLineageForwardRange range = Assert.IsType<
            SessionSelectedLineageForwardRange>(cursor.ReadNextRange(4));
        SessionHistoryPlanningWindow preview = cursor.Preview(range);
        SessionSelectedLineageForwardRange pending = Assert.IsType<
            SessionSelectedLineageForwardRange>(
            cursor.ConsumePreviewedPrefix(
                range,
                preview.ReplaySafeBoundaries[0].Address).RemainingRange);
        return (cursor, pending);
    }

    private static void AssertInvalidated(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange pending
    ) {
        Assert.Throws<InvalidOperationException>(() =>
            cursor.Preview(pending));
        Assert.Throws<InvalidOperationException>(() =>
            cursor.Materialize(pending));
        Assert.Throws<InvalidOperationException>(() =>
            cursor.ExtendPendingRange(pending, pending.Entries.Count));
    }

    private static void AssertPageChain(
        IReadOnlyList<SessionSelectedLineageAuditPage> pages,
        EventAddress expectedHead
    ) {
        EventAddress? expectedPageHead = expectedHead;
        for (int pageIndex = 0;
             pageIndex < pages.Count;
             pageIndex++) {
            SessionSelectedLineageAuditPage page = pages[pageIndex];
            Assert.Equal((long)pageIndex, page.Ordinal);
            Assert.Equal(expectedPageHead, page.PageHead);
            Assert.NotEmpty(page.HeadToOldest);
            Assert.Equal(
                page.PageHead,
                page.HeadToOldest[0].Address
            );
            for (int index = 0;
                 index < page.HeadToOldest.Count - 1;
                 index++) {
                Assert.Equal(
                    page.HeadToOldest[index].Parent,
                    page.HeadToOldest[index + 1].Address
                );
            }
            Assert.Equal(
                page.HeadToOldest[^1].Parent,
                page.Continuation
            );
            expectedPageHead = page.Continuation;
        }
        Assert.Null(expectedPageHead);
    }

    private static void AssertInspectionOperationsRejected(
        SessionSelectedLineageForwardCursor cursor
    ) {
        Assert.Throws<InvalidOperationException>(() =>
            cursor.ProbeBoundaries(_ =>
                SessionSelectedLineageBoundaryProbeDecision.Continue
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            cursor.FindLatestMatchingBoundary(
                new HashSet<EventAddress>()
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            cursor.SeekToBoundary(
                cursor.Authority.BootstrapSeed.Address,
                cursor.Authority.BootstrapSeed.Setups
            )
        );
    }

    private string NewPath() {
        string temporaryRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            temporaryRoot,
            "atelia-session-selected-lineage-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class InMemoryPageSnapshot(
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) : ISessionSelectedLineageAuditPageSnapshot {
        public SessionSelectedLineageAuditCapture Capture { get; } =
            capture;
        public long PageCount => pages.Count;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() => pages;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() => pages.Reverse();

        public void Dispose() {
        }
    }

    private sealed class CountingPageSnapshot(
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) : ISessionSelectedLineageAuditPageSnapshot {
        public SessionSelectedLineageAuditCapture Capture { get; } = capture;
        public long PageCount => pages.Count;
        public int DisposeCount { get; private set; }
        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() => pages;
        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() => pages.Reverse();
        public void Dispose() => DisposeCount++;
    }

    private sealed class DivergentForwardPageSnapshot(
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) : ISessionSelectedLineageAuditPageSnapshot {
        public SessionSelectedLineageAuditCapture Capture { get; } =
            capture;
        public long PageCount => pages.Count;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() => pages;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() {
            SessionSelectedLineageAuditPage oldest = pages[^1];
            SessionSelectedLineageAuditEntry entry =
                oldest.HeadToOldest[^1] with {
                    PayloadSha256 = new string('0', 64)
                };
            SessionSelectedLineageAuditEntry[] rewritten = [
                .. oldest.HeadToOldest.Take(
                    oldest.HeadToOldest.Count - 1
                ),
                entry
            ];
            yield return oldest with {
                HeadToOldest = rewritten
            };
            for (int index = pages.Count - 2;
                 index >= 0;
                 index--) {
                yield return pages[index];
            }
        }

        public void Dispose() {
        }
    }

    private sealed class UnusedCompletionClient : ICompletionClient {
        public string Name => "unused-audit-extend";
        public string ApiSpecId => "unused-audit-extend-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "The read-only audit fixture must not call Completion."
        );
    }
}

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
            requestedRange,
            authority.BootstrapSeed
        );

        Assert.Equal([observation, action], window.RawAddresses);
        Assert.Equal(action, window.ObservedRawHead);
        Assert.Equal(
            authority.BootstrapSeed.Address,
            window.StartExclusive
        );
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
}

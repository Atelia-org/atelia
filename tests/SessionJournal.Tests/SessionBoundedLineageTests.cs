using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionBoundedLineageTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void Prefix_StopsAt512And513ThenCompletesAt514() {
        (string path, EventAddress[] addresses) =
            CreateHeaderOnlyLineage(514);
        using var engine = SessionJournalEngine.Open(path);

        SessionCurrentLineagePrefix first =
            engine.ReadCurrentLineagePrefix(512);
        SessionCurrentLineagePrefix second =
            engine.ReadLineagePrefixAt(addresses[^1], 513);
        SessionCurrentLineagePrefix complete =
            engine.ReadLineagePrefixAt(addresses[^1], 514);

        Assert.Equal(addresses[^1], first.CapturedHead);
        Assert.Equal(512, first.HeadToOldest.Count);
        Assert.Equal(addresses[1], first.Continuation!.NextAddress);
        Assert.False(first.IsComplete);
        Assert.Equal(513, second.HeadToOldest.Count);
        Assert.Equal(addresses[0], second.Continuation!.NextAddress);
        Assert.Equal(514, complete.HeadToOldest.Count);
        Assert.True(complete.IsComplete);
        Assert.Null(complete.Continuation);
        Assert.Null(complete.HeadToOldest[^1].Parent);
        Assert.All(
            new[] { first, second, complete },
            prefix => {
                Assert.Equal(
                    prefix.HeadToOldest.Count,
                    prefix.Diagnostics.HeaderVisits
                );
                Assert.Equal(0, prefix.Diagnostics.PayloadReads);
                Assert.Equal(
                    0,
                    prefix.Diagnostics.DecodedPayloadBytes
                );
            }
        );
    }

    [Fact]
    public void PrefixLookup_DistinguishesFoundBeyondAndOffLineage() {
        (string path, EventAddress[] addresses) =
            CreateHeaderOnlyLineage(3);
        using var engine = SessionJournalEngine.Open(path);

        SessionCurrentLineagePrefix bounded =
            engine.ReadCurrentLineagePrefix(2);
        var found = Assert.IsType<
            SessionCurrentLineageAnchorLookup.Found
        >(bounded.Lookup(addresses[1]));
        var beyond = Assert.IsType<
            SessionCurrentLineageAnchorLookup.BeyondPrefix
        >(bounded.Lookup(addresses[0]));
        Assert.Equal(1, found.Index);
        Assert.Equal(addresses[0], beyond.Evidence.RequiredAnchor);
        Assert.Equal(addresses[^1], beyond.Evidence.CapturedHead);
        Assert.Equal(2, beyond.Evidence.HeaderCount);
        Assert.Equal(addresses[0], beyond.Evidence.NextAddress);

        SessionCurrentLineagePrefix complete =
            engine.ReadCurrentLineagePrefix(3);
        var offLineage = Assert.IsType<
            SessionCurrentLineageAnchorLookup.OffLineage
        >(complete.Lookup(Address(1000)));
        Assert.Equal(Address(1000), offLineage.RequiredAnchor);
        Assert.Equal(addresses[^1], offLineage.CapturedHead);
    }

    [Fact]
    public void Prefix_RejectsInvalidLimitAndMalformedShapes() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.ReadCurrentLineagePrefix(0)
        );

        EventAddress head = Address(1);
        EventAddress parent = Address(2);
        EventAddress other = Address(3);
        SessionCurrentLineageDiagnostics twoHeaders =
            new(HeaderVisits: 2, PayloadReads: 0, DecodedPayloadBytes: 0);
        Assert.Throws<ArgumentException>(() => new SessionCurrentLineagePrefix(
            head,
            2,
            [
                new(head, parent, SessionEventKind.ObservationAccepted),
                new(other, null, SessionEventKind.SessionCreated)
            ],
            continuation: null,
            twoHeaders
        ));
        Assert.Throws<ArgumentException>(() => new SessionCurrentLineagePrefix(
            head,
            2,
            [
                new(head, parent, SessionEventKind.ObservationAccepted),
                new(parent, head, SessionEventKind.ObservationAccepted)
            ],
            new SessionCurrentLineageContinuation(head),
            twoHeaders
        ));
        Assert.Throws<ArgumentException>(() => new SessionCurrentLineagePrefix(
            head,
            1,
            [new(head, null, (SessionEventKind)uint.MaxValue)],
            continuation: null,
            new(1, 0, 0)
        ));
        Assert.Throws<ArgumentException>(() => new SessionCurrentLineagePrefix(
            head,
            1,
            [new(head, parent, SessionEventKind.ObservationAccepted)],
            continuation: null,
            new(1, 0, 0)
        ));
        Assert.Throws<ArgumentException>(() => new SessionCurrentLineagePrefix(
            head,
            1,
            [new(head, null, SessionEventKind.SessionCreated)],
            new SessionCurrentLineageContinuation(parent),
            new(1, 0, 0)
        ));
    }

    [Fact]
    public void BoundedPlanning_Uses513HeaderProofFor512RawEvents() {
        (
            string path,
            EventAddress start,
            EventAddress headAt512,
            EventAddress headAt513
        ) = CreatePlanningLineage();
        using var engine = SessionJournalEngine.Open(path);

        SessionHistoryPlanningWindowReadResult result =
            engine.ReadHistoryPlanningWindowAtBounded(
                headAt512,
                start,
                maxRawEventCount: 512
            );

        var available = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(result);
        Assert.Equal(512, available.Window.RawAddresses.Count);
        Assert.Equal(512, available.Window.Diagnostics.DecodedEventCount);
        Assert.Equal(513, available.PrefixDiagnostics.HeaderVisits);
        Assert.Equal(0, available.PrefixDiagnostics.PayloadReads);
        Assert.Equal(headAt512, available.Window.ObservedRawHead);
        Assert.True(
            available.Window.RawAddresses.Count <= 512
        );

        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        result = engine.ReadHistoryPlanningWindowAtBounded(
            headAt513,
            start,
            maxRawEventCount: 512
        );
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        var beyond = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.BeyondPrefix
        >(result);
        Assert.Equal(start, beyond.Evidence.RequiredAnchor);
        Assert.Equal(headAt513, beyond.Evidence.CapturedHead);
        Assert.Equal(513, beyond.Evidence.HeaderCount);
        Assert.Equal(start, beyond.Evidence.NextAddress);
        Assert.Equal(0, beyond.Diagnostics.PayloadReads);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
        before = engine.CaptureReadDiagnostics();
        Assert.Throws<InvalidDataException>(
            () => engine.ReadHistoryPlanningWindowAtBounded(
                headAt512,
                Address(1000),
                maxRawEventCount: 600
            )
        );
        after = engine.CaptureReadDiagnostics();
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.ReadHistoryPlanningWindowAtBounded(
                headAt512,
                start,
                maxRawEventCount: -1
            )
        );
    }

    private (string Path, EventAddress[] Addresses)
        CreateHeaderOnlyLineage(int count) {
        string path = NewPath();
        var addresses = new EventAddress[count];
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            RefId main = journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            EventAddress? parent = null;
            for (int index = 0; index < count; index++) {
                parent = journal.AppendEventFrame(
                    parent,
                    ReadOnlySpan<byte>.Empty,
                    (uint)SessionEventKind.ObservationAccepted,
                    hint: default
                ).Unwrap();
                addresses[index] = parent.Value;
            }
            Assert.True(journal.MoveRef(main, null, parent).Unwrap());
        }
        return (path, addresses);
    }

    private (
        string Path,
        EventAddress Start,
        EventAddress HeadAt512,
        EventAddress HeadAt513
    ) CreatePlanningLineage() {
        string path = NewPath();
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        RefId main = journal.CreateBranch(
            SessionJournalDefaults.MainBranchName,
            startPoint: null
        ).Unwrap();
        EventAddress runtime = AppendRaw(
            journal,
            parent: null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        EventAddress prompt = AppendRaw(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress start = AppendRaw(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(SessionCreationOrigin.Native)
        );
        EventAddress cursor = start;
        for (int index = 0; index < 256; index++) {
            EventAddress observation = AppendRaw(
                journal,
                cursor,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody($"observation-{index}")
            );
            cursor = AppendRaw(
                journal,
                observation,
                SessionEventKind.ImportedAgentAction,
                new AgentActionProducedBody(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-A"
                    ),
                    $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}",
                    new SessionExecutionCheckpoint(0),
                    ToolRuntimeIdentity: null
                )
            );
        }
        EventAddress headAt512 = cursor;
        EventAddress headAt513 = AppendRaw(
            journal,
            cursor,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("pending-observation")
        );
        Assert.True(journal.MoveRef(main, null, headAt513).Unwrap());
        return (path, start, headAt512, headAt513);
    }

    private static EventAddress AppendRaw(
        EventJournal.EventJournal journal,
        EventAddress? parent,
        SessionEventKind kind,
        object body
    ) => journal.AppendEventFrame(
        parent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap();

    private string NewPath() {
        string temporaryRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            temporaryRoot,
            "atelia-session-bounded-lineage-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static EventAddress Address(ulong ticket)
        => EventAddressTextCodec.Parse(
            $"ej1:{ticket:x16}0000000100000000"
        );

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned temporary directories.
            }
        }
    }
}

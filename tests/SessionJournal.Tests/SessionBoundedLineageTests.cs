using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
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

        Assert.Empty(typeof(SessionCurrentLineageContinuation)
            .GetConstructors());
        Assert.Empty(typeof(SessionCurrentLineageBeyondPrefix)
            .GetConstructors());
        Assert.Empty(typeof(SessionCurrentLineagePrefix)
            .GetConstructors());

        EventAddress head = Address(1);
        EventAddress parent = Address(2);
        EventAddress other = Address(3);
        SessionCurrentLineageDiagnostics twoHeaders =
            new(HeaderVisits: 2, PayloadReads: 0, DecodedPayloadBytes: 0);
        Assert.Throws<ArgumentException>(() => new SessionCurrentLineagePrefix(
            head,
            1,
            [null!],
            continuation: null,
            new(1, 0, 0)
        ));
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
        // Append-only EventJournal addresses cannot point to a future child, so a storage-level
        // Parent cycle is not writer-constructable. Lock the same authority rejection at its
        // internal shape boundary; reachable storage corruption is covered below.
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
            EventAddress headAt513,
            EventAddress headAt514,
            SessionContextAnchorSetupReferences setups
        ) = CreatePlanningLineage();
        using var engine = SessionJournalEngine.Open(path);
        SessionHistoryPlanningSeed seed =
            engine.CreateHistoryPlanningSeed(start, setups);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        SessionHistoryPlanningWindowReadResult result =
            engine.ReadHistoryPlanningWindowAtBounded(
                headAt512,
                seed,
                maxRawEventCount: 512
            );
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        var available = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(result);
        Assert.Equal(512, available.Window.RawAddresses.Count);
        Assert.Equal(512, available.Window.Diagnostics.DecodedEventCount);
        Assert.Equal(513, available.PrefixDiagnostics.HeaderVisits);
        Assert.Equal(0, available.PrefixDiagnostics.PayloadReads);
        Assert.Equal(513, available.Window.Diagnostics.HeaderVisits);
        Assert.Equal(
            513,
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );
        Assert.Equal(headAt512, available.Window.ObservedRawHead);
        Assert.True(
            available.Window.RawAddresses.Count <= 512
        );

        before = engine.CaptureReadDiagnostics();
        result = engine.ReadHistoryPlanningWindowAtBounded(
            headAt513,
            start,
            maxRawEventCount: 512
        );
        after = engine.CaptureReadDiagnostics();

        var beyond = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.BeyondPrefix
        >(result);
        Assert.Equal(start, beyond.Evidence.RequiredAnchor);
        Assert.Equal(headAt513, beyond.Evidence.CapturedHead);
        Assert.Equal(513, beyond.Evidence.HeaderCount);
        Assert.Equal(start, beyond.Evidence.NextAddress);
        Assert.Equal(0, beyond.Diagnostics.PayloadReads);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
        Assert.Equal(
            513,
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );
        result = engine.ReadHistoryPlanningWindowAtBounded(
            headAt514,
            start,
            maxRawEventCount: 512
        );
        beyond = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.BeyondPrefix
        >(result);
        Assert.Equal(513, beyond.Evidence.HeaderCount);
        Assert.NotEqual(
            beyond.Evidence.RequiredAnchor,
            beyond.Evidence.NextAddress
        );
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
        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.ReadHistoryPlanningWindowAtBounded(
                headAt512,
                start,
                maxRawEventCount: int.MaxValue
            )
        );
    }

    [Fact]
    public void BoundedSessionCreatedSeed_Uses513HeaderProofFor512RawEvents() {
        (
            string path,
            EventAddress start,
            EventAddress headAt512,
            EventAddress headAt513,
            EventAddress headAt514,
            SessionContextAnchorSetupReferences setups
        ) = CreatePlanningLineage();
        using var engine = SessionJournalEngine.Open(path);

        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        var available = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.Available
        >(engine.ReadSessionCreatedPlanningSeedAtBounded(
            headAt512,
            maxRawEventCount: 512
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        Assert.Equal(start, available.Seed.Address);
        Assert.Equal(setups, available.Seed.Setups);
        Assert.Equal(512, available.RawEventCountAfterStart);
        Assert.Equal(513, available.Diagnostics.HeaderVisits);
        Assert.Equal(0, available.Diagnostics.PayloadReads);
        Assert.True(
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
                >= 513
        );
        Assert.True(after.PayloadReadCount > before.PayloadReadCount);

        before = engine.CaptureReadDiagnostics();
        var beyond513 = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.BeyondPrefix
        >(engine.ReadSessionCreatedPlanningSeedAtBounded(
            headAt513,
            maxRawEventCount: 512
        ));
        after = engine.CaptureReadDiagnostics();
        Assert.Equal(headAt513, beyond513.CapturedHead);
        Assert.Equal(513, beyond513.HeaderCount);
        Assert.Equal(start, beyond513.NextAddress);
        Assert.Null(beyond513.ContinuationEvidence.RequiredAnchor);
        Assert.Equal(0, beyond513.Diagnostics.PayloadReads);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);

        before = engine.CaptureReadDiagnostics();
        var beyond514 = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.BeyondPrefix
        >(engine.ReadSessionCreatedPlanningSeedAtBounded(
            headAt514,
            maxRawEventCount: 512
        ));
        after = engine.CaptureReadDiagnostics();
        Assert.Equal(513, beyond514.HeaderCount);
        Assert.NotEqual(start, beyond514.NextAddress);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.ReadSessionCreatedPlanningSeedAtBounded(
                headAt512,
                maxRawEventCount: -1
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => engine.ReadSessionCreatedPlanningSeedAtBounded(
                headAt512,
                maxRawEventCount: int.MaxValue
            )
        );
    }

    [Fact]
    public void BoundedSessionCreatedSeed_MaxZeroRequiresCreatedHead() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress created =
            engine.InspectExecutionBoundary().Head!.Value;

        var available = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.Available
        >(engine.ReadSessionCreatedPlanningSeedAtBounded(
            created,
            maxRawEventCount: 0
        ));
        Assert.Equal(created, available.Seed.Address);
        Assert.Equal(0, available.RawEventCountAfterStart);
        Assert.Equal(1, available.Diagnostics.HeaderVisits);

        EventAddress observation = engine.AppendObservation("one");
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        var beyond = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.BeyondPrefix
        >(engine.ReadSessionCreatedPlanningSeedAtBounded(
            observation,
            maxRawEventCount: 0
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Equal(1, beyond.HeaderCount);
        Assert.Equal(created, beyond.NextAddress);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
    }

    [Fact]
    public void BoundedGoverningSetupProof_IsHeaderOnlyUntilMaterialized() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress boundary =
            engine.InspectExecutionBoundary().Head!.Value;
        SessionContextAnchorSetupReferences setups =
            engine.ResolveContextAnchorSetupReferences(boundary);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        var available = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(engine.ProveGoverningSetupAtBounded(
            boundary,
            setups,
            maxHeaderCount: 3
        ));
        SessionJournalReadDiagnostics afterProof =
            engine.CaptureReadDiagnostics();

        Assert.Equal(boundary, available.Proof.Boundary);
        Assert.Equal(setups, available.Proof.ExpectedSetups);
        Assert.Equal(3, available.Proof.Diagnostics.HeaderVisits);
        Assert.Equal(0, available.Proof.Diagnostics.PayloadReads);
        Assert.Equal(
            before.PayloadReadCount,
            afterProof.PayloadReadCount
        );
        Assert.Empty(typeof(SessionGoverningSetupProof)
            .GetConstructors());

        SessionHistoryPlanningSeed seed =
            engine.MaterializeHistoryPlanningSeed(available.Proof);

        Assert.Equal(boundary, seed.Address);
        Assert.Equal(setups, seed.Setups);
        Assert.True(
            engine.CaptureReadDiagnostics().PayloadReadCount
                > afterProof.PayloadReadCount
        );
    }

    [Fact]
    public void PrefixGoverningSetupPayloadValidation_DeduplicatesToExactlyTwoReads() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress boundary = engine.ReadCurrentHead()!.Value;
        SessionContextAnchorSetupReferences setups =
            engine.ResolveContextAnchorSetupReferences(boundary);
        SessionCurrentLineagePrefix prefix =
            engine.ReadLineagePrefixAt(boundary, 513);
        SessionJournalReadDiagnostics beforeProof =
            engine.CaptureReadDiagnostics();
        var available = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(engine.ProveGoverningSetupInPrefix(
            prefix,
            boundary,
            setups
        ));
        SessionJournalReadDiagnostics afterProof =
            engine.CaptureReadDiagnostics();

        Assert.Equal(
            beforeProof.PayloadReadCount,
            afterProof.PayloadReadCount
        );
        engine.ValidateGoverningSetupPayloads([
            available.Proof,
            available.Proof
        ]);
        SessionJournalReadDiagnostics afterValidation =
            engine.CaptureReadDiagnostics();

        Assert.Equal(
            2,
            afterValidation.PayloadReadCount
                - afterProof.PayloadReadCount
        );
    }

    [Fact]
    public void PrefixGoverningSetupProof_UsesExact513ContinuationWithoutPayloads() {
        (
            string path,
            _,
            _,
            _,
            EventAddress headAt514,
            SessionContextAnchorSetupReferences setups
        ) = CreatePlanningLineage();
        using var engine = SessionJournalEngine.Open(path);
        SessionCurrentLineagePrefix prefix =
            engine.ReadLineagePrefixAt(headAt514, 513);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        var beyond = Assert.IsType<
            SessionGoverningSetupProofResult.BeyondPrefix
        >(engine.ProveGoverningSetupInPrefix(
            prefix,
            headAt514,
            setups
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        Assert.Equal(headAt514, beyond.Evidence.Boundary);
        Assert.Equal(
            headAt514,
            beyond.Evidence.ContinuationEvidence.CapturedHead
        );
        Assert.Equal(513, beyond.Evidence.HeaderCount);
        Assert.Equal(
            prefix.Continuation!.NextAddress,
            beyond.Evidence.NextAddress
        );
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
    }

    [Fact]
    public void PrefixGoverningSetupPayloadValidation_RejectsWrongHashAndSchema() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress boundary = engine.ReadCurrentHead()!.Value;
        SessionContextAnchorSetupReferences setups =
            engine.ResolveContextAnchorSetupReferences(boundary);
        SessionCurrentLineagePrefix prefix =
            engine.ReadLineagePrefixAt(boundary, 513);
        var wrongHash = setups with {
            RuntimeConfig = setups.RuntimeConfig with {
                PayloadSha256 = new string('0', 64)
            }
        };
        var wrongSchema = setups with {
            RuntimeConfig = setups.RuntimeConfig with {
                BodySchemaVersion =
                    setups.RuntimeConfig.BodySchemaVersion + 1
            }
        };
        SessionGoverningSetupProof hashProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(engine.ProveGoverningSetupInPrefix(
            prefix,
            boundary,
            wrongHash
        )).Proof;
        SessionGoverningSetupProof schemaProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(engine.ProveGoverningSetupInPrefix(
            prefix,
            boundary,
            wrongSchema
        )).Proof;

        Assert.Contains(
            "hash mismatch",
            Assert.Throws<InvalidDataException>(() =>
                engine.ValidateGoverningSetupPayloads([hashProof])
            ).Message
        );
        Assert.Contains(
            "schema version mismatch",
            Assert.Throws<InvalidDataException>(() =>
                engine.ValidateGoverningSetupPayloads([schemaProof])
            ).Message
        );
    }

    [Fact]
    public void PrefixGoverningSetupPayloadValidation_RejectsForeignAndConflictingProofsBeforeReads() {
        string firstPath = NewPath();
        string secondPath = NewPath();
        using var first = SessionJournalEngine.Create(
            firstPath,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        using var second = SessionJournalEngine.Create(
            secondPath,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress boundary = first.ReadCurrentHead()!.Value;
        SessionContextAnchorSetupReferences setups =
            first.ResolveContextAnchorSetupReferences(boundary);
        SessionCurrentLineagePrefix prefix =
            first.ReadLineagePrefixAt(boundary, 513);
        SessionGoverningSetupProof valid = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(first.ProveGoverningSetupInPrefix(
            prefix,
            boundary,
            setups
        )).Proof;
        var conflictingSetups = setups with {
            RuntimeConfig = setups.RuntimeConfig with {
                PayloadSha256 = new string('0', 64)
            }
        };
        SessionGoverningSetupProof conflicting = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(first.ProveGoverningSetupInPrefix(
            prefix,
            boundary,
            conflictingSetups
        )).Proof;
        EventAddress foreignBoundary = second.ReadCurrentHead()!.Value;
        SessionContextAnchorSetupReferences foreignSetups =
            second.ResolveContextAnchorSetupReferences(foreignBoundary);
        SessionGoverningSetupProof foreign = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(second.ProveGoverningSetupInPrefix(
            second.ReadLineagePrefixAt(foreignBoundary, 513),
            foreignBoundary,
            foreignSetups
        )).Proof;
        SessionJournalReadDiagnostics before =
            first.CaptureReadDiagnostics();

        Assert.Throws<ArgumentException>(() =>
            first.ValidateGoverningSetupPayloads([foreign])
        );
        Assert.Throws<InvalidDataException>(() =>
            first.ValidateGoverningSetupPayloads([
                valid,
                conflicting
            ])
        );
        SessionJournalReadDiagnostics after =
            first.CaptureReadDiagnostics();

        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
    }

    [Fact]
    public void GoverningSetupTransition_RequiresRepositoryBoundStartProof() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        engine.AppendObservation("observation");
        EventAddress endpoint = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("answer")
            ]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindow();
        EventAddress start = window.StartExclusive;
        SessionCurrentLineagePrefix prefix =
            engine.ReadLineagePrefixAt(endpoint, 513);
        SessionGoverningSetupProof startProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(engine.ProveGoverningSetupInPrefix(
            prefix,
            start,
            window.StartSetups
        )).Proof;
        SessionHistoryPlanningWindowProof routeProof = Assert.IsType<
            SessionHistoryPlanningWindowProofResult.Available
        >(engine.ProveHistoryPlanningWindowInPrefix(
            prefix,
            endpoint,
            start,
            maxRawEventCount: 8
        )).Proof;

        SessionGoverningSetupProof endpointProof =
            engine.ProveGoverningSetupTransition(
                routeProof,
                startProof,
                window.EndSetups
            );

        Assert.Equal(endpoint, endpointProof.Boundary);
        Assert.Equal(window.EndSetups, endpointProof.ExpectedSetups);
        Assert.Throws<ArgumentException>(() =>
            engine.ProveGoverningSetupTransition(
                routeProof,
                endpointProof,
                window.EndSetups
            )
        );
    }

    [Fact]
    public void BoundedGoverningSetupProof_RejectsOldButRealReferencesHeaderOnly() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress created =
            engine.InspectExecutionBoundary().Head!.Value;
        SessionContextAnchorSetupReferences oldSetups =
            engine.ResolveContextAnchorSetupReferences(created);
        _ = engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-B",
                "surface-B",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        EventAddress boundary =
            engine.AppendSystemPromptSetup("system-B");
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => engine.ProveGoverningSetupAtBounded(
                boundary,
                oldSetups,
                maxHeaderCount: 8
            )
        );
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        Assert.Contains("first system-prompt-setup", error.Message);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
        Assert.Equal(
            1,
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );
    }

    [Fact]
    public void BoundedGoverningSetupProof_ReturnsBeyondAt513And514Headers() {
        (
            string path,
            _,
            EventAddress boundary,
            _,
            _,
            SessionContextAnchorSetupReferences setups
        ) = CreatePlanningLineage();
        using var engine = SessionJournalEngine.Open(path);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        var beyond513 = Assert.IsType<
            SessionGoverningSetupProofResult.BeyondPrefix
        >(engine.ProveGoverningSetupAtBounded(
            boundary,
            setups,
            maxHeaderCount: 513
        ));
        var beyond514 = Assert.IsType<
            SessionGoverningSetupProofResult.BeyondPrefix
        >(engine.ProveGoverningSetupAtBounded(
            boundary,
            setups,
            maxHeaderCount: 514
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();

        Assert.Equal(boundary, beyond513.Evidence.Boundary);
        Assert.Equal(513, beyond513.Evidence.HeaderCount);
        Assert.Equal(514, beyond514.Evidence.HeaderCount);
        Assert.Equal(
            setups.RuntimeConfig.Address,
            beyond513.Evidence.RequiredAnchor
        );
        Assert.Equal(
            setups.RuntimeConfig.Address,
            beyond514.Evidence.RequiredAnchor
        );
        Assert.NotEqual(
            beyond513.Evidence.NextAddress,
            beyond514.Evidence.NextAddress
        );
        Assert.Equal(0, beyond513.Diagnostics.PayloadReads);
        Assert.Equal(0, beyond514.Diagnostics.PayloadReads);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
    }

    [Fact]
    public void BoundedGoverningSetupProof_StopsAtExactBoundaryAndAfterExpectedSetups() {
        var journalOptions = new EventJournalOptions {
            EventSegmentStoreOptions =
                new RbfSegmentStoreOptions {
                    SegmentSizeThresholdBytes = 4,
                    CacheMode = RbfCacheMode.Off
                }
        };
        string path = NewPath();
        EventAddress missingOlder;
        EventAddress boundary;
        SessionContextAnchorSetupReferences setups;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(
                   path,
                   journalOptions
               )) {
            RefId main = journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            missingOlder = AppendRaw(
                journal,
                parent: null,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("unreachable older payload")
            );
            var runtimeBody = new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            );
            byte[] runtimePayload = SessionEventCodec.Encode(
                SessionEventKind.RuntimeConfigSetup,
                runtimeBody
            );
            EventAddress runtime = journal.AppendEventFrame(
                missingOlder,
                runtimePayload,
                (uint)SessionEventKind.RuntimeConfigSetup,
                hint: default
            ).Unwrap();
            var promptBody = new SystemPromptSetupBody("system-A");
            byte[] promptPayload = SessionEventCodec.Encode(
                SessionEventKind.SystemPromptSetup,
                promptBody
            );
            EventAddress prompt = journal.AppendEventFrame(
                runtime,
                promptPayload,
                (uint)SessionEventKind.SystemPromptSetup,
                hint: default
            ).Unwrap();
            setups = new SessionContextAnchorSetupReferences(
                new SessionContextSetupReference(
                    runtime,
                    SessionEventCodec.GetExpectedBodySchemaVersion(
                        SessionEventKind.RuntimeConfigSetup
                    ),
                    SessionRequestCanonicalizer.Sha256Hex(
                        runtimePayload
                    )
                ),
                new SessionContextSetupReference(
                    prompt,
                    SessionEventCodec.GetExpectedBodySchemaVersion(
                        SessionEventKind.SystemPromptSetup
                    ),
                    SessionRequestCanonicalizer.Sha256Hex(
                        promptPayload
                    )
                )
            );
            boundary = AppendRaw(
                journal,
                prompt,
                SessionEventKind.SessionCreated,
                new SessionCreatedBody(SessionCreationOrigin.Native)
            );
            Assert.True(journal.MoveRef(main, null, boundary).Unwrap());
            _ = AppendRaw(
                journal,
                boundary,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("unreferenced newer suffix")
            );
        }
        TruncateEventSegment(path, missingOlder.SegmentNumber);
        using var engine = SessionJournalEngine.OpenForTest(
            path,
            runtime: null,
            new SessionJournalTestHooks(),
            journalOptions
        );
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        var available = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(engine.ProveGoverningSetupAtBounded(
            boundary,
            setups,
            maxHeaderCount: 8
        ));
        SessionJournalReadDiagnostics afterProof =
            engine.CaptureReadDiagnostics();

        Assert.Equal(3, available.Proof.Diagnostics.HeaderVisits);
        Assert.Equal(
            3,
            afterProof.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );
        Assert.Equal(before.PayloadReadCount, afterProof.PayloadReadCount);
    }

    [Fact]
    public void PlanningWindowProofs_AllRemainHeaderOnlyUntilMaterialized() {
        (
            string path,
            EventAddress start,
            EventAddress headAt512,
            EventAddress headAt513,
            _,
            SessionContextAnchorSetupReferences setups
        ) = CreatePlanningLineage();
        using var engine = SessionJournalEngine.Open(path);
        SessionHistoryPlanningSeed seed =
            engine.CreateHistoryPlanningSeed(start, setups);
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        var first = Assert.IsType<
            SessionHistoryPlanningWindowProofResult.Available
        >(engine.ProveHistoryPlanningWindowAtBounded(
            headAt512,
            start,
            maxRawEventCount: 512
        ));
        SessionJournalReadDiagnostics afterFirstProof =
            engine.CaptureReadDiagnostics();
        var second = Assert.IsType<
            SessionHistoryPlanningWindowProofResult.BeyondPrefix
        >(engine.ProveHistoryPlanningWindowAtBounded(
            headAt513,
            start,
            maxRawEventCount: 512
        ));
        SessionJournalReadDiagnostics afterAllProofs =
            engine.CaptureReadDiagnostics();

        Assert.Equal(512, first.Proof.RawEventCount);
        Assert.Equal(513, first.Proof.Diagnostics.HeaderVisits);
        Assert.Equal(513, second.Evidence.HeaderCount);
        Assert.Equal(
            before.PayloadReadCount,
            afterFirstProof.PayloadReadCount
        );
        Assert.Equal(
            before.PayloadReadCount,
            afterAllProofs.PayloadReadCount
        );

        SessionHistoryPlanningWindow window =
            engine.MaterializeHistoryPlanningWindow(
                first.Proof,
                seed
            );
        Assert.Equal(512, window.RawAddresses.Count);
        Assert.True(
            engine.CaptureReadDiagnostics().PayloadReadCount
                > afterAllProofs.PayloadReadCount
        );
        Assert.Empty(typeof(SessionHistoryPlanningWindowProof)
            .GetConstructors());
    }

    [Fact]
    public void BoundedPlanning_TargetHitStopsAtOneOrTwoHeaders() {
        string path = NewPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        EventAddress start =
            engine.InspectExecutionBoundary().Head!.Value;
        EventAddress head = engine.AppendObservation("one");
        SessionHistoryPlanningSeedBatch batch =
            engine.ReadHistoryPlanningSeeds([start]);
        SessionHistoryPlanningSeed seed =
            engine.CreateHistoryPlanningSeed(
                start,
                Assert.Single(batch.Seeds).Setups
            );

        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        var atHead = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(engine.ReadHistoryPlanningWindowAtBounded(
            start,
            seed,
            maxRawEventCount: 0
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Empty(atHead.Window.RawAddresses);
        Assert.Equal(1, atHead.PrefixDiagnostics.HeaderVisits);
        Assert.Equal(
            1,
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );

        before = engine.CaptureReadDiagnostics();
        var oneEvent = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(engine.ReadHistoryPlanningWindowAtBounded(
            head,
            seed,
            maxRawEventCount: 1
        ));
        after = engine.CaptureReadDiagnostics();
        Assert.Equal([head], oneEvent.Window.RawAddresses);
        Assert.Equal(2, oneEvent.PrefixDiagnostics.HeaderVisits);
        Assert.Equal(2, oneEvent.Window.Diagnostics.HeaderVisits);
        Assert.Equal(
            2,
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );
    }

    [Fact]
    public void FrozenSeed_DoesNotReadMissingParentBeforeExactAnchor() {
        string path = NewPath();
        var journalOptions = new EventJournalOptions {
            EventSegmentStoreOptions =
                new RbfSegmentStoreOptions {
                    SegmentSizeThresholdBytes = 4,
                    CacheMode = RbfCacheMode.Off
                }
        };
        EventAddress missing;
        EventAddress start;
        EventAddress head;
        SessionHistoryPlanningSeed seed;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(
                   path,
                   journalOptions
               )) {
            RefId main = journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            var runtime = new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            );
            byte[] runtimePayload = SessionEventCodec.Encode(
                SessionEventKind.RuntimeConfigSetup,
                runtime
            );
            EventAddress runtimeAddress = journal.AppendEventFrame(
                parent: null,
                runtimePayload,
                (uint)SessionEventKind.RuntimeConfigSetup,
                hint: default
            ).Unwrap();
            byte[] promptPayload = SessionEventCodec.Encode(
                SessionEventKind.SystemPromptSetup,
                new SystemPromptSetupBody("system-A")
            );
            EventAddress promptAddress = journal.AppendEventFrame(
                runtimeAddress,
                promptPayload,
                (uint)SessionEventKind.SystemPromptSetup,
                hint: default
            ).Unwrap();
            missing = promptAddress;
            start = AppendRaw(
                journal,
                promptAddress,
                SessionEventKind.SessionCreated,
                new SessionCreatedBody(SessionCreationOrigin.Native)
            );
            head = AppendRaw(
                journal,
                start,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("reachable suffix")
            );
            Assert.True(journal.MoveRef(main, null, head).Unwrap());
            _ = AppendRaw(
                journal,
                head,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("unreferenced active")
            );
            var setups = new SessionContextAnchorSetupReferences(
                new SessionContextSetupReference(
                    runtimeAddress,
                    SessionEventCodec.GetExpectedBodySchemaVersion(
                        SessionEventKind.RuntimeConfigSetup
                    ),
                    SessionRequestCanonicalizer.Sha256Hex(
                        runtimePayload
                    )
                ),
                new SessionContextSetupReference(
                    promptAddress,
                    SessionEventCodec.GetExpectedBodySchemaVersion(
                        SessionEventKind.SystemPromptSetup
                    ),
                    SessionRequestCanonicalizer.Sha256Hex(
                        promptPayload
                    )
                )
            );
            seed = new SessionHistoryPlanningSeed(
                path,
                start,
                setups,
                new SessionGoverningSetup(
                    start,
                    runtimeAddress,
                    runtime,
                    promptAddress,
                    "system-A"
                ),
                new SessionExecutionRecovery(
                    start,
                    new SessionExecutionState(
                        SessionExecutionPhase.Idle,
                        SessionEventKind.SessionCreated
                    ),
                    new SessionExecutionRecoveryBoundary(
                        SourcePrepared: null,
                        SourceAction: null,
                        SourceObservation: null,
                        LatestExecutionCheckpoint: null
                    ),
                    new SessionExecutionRecoveryDiagnostics(0, 0)
                )
            );
        }
        TruncateEventSegment(path, missing.SegmentNumber);

        using var engine = SessionJournalEngine.OpenForTest(
            path,
            runtime: null,
            new SessionJournalTestHooks(),
            journalOptions
        );
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        var available = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(engine.ReadHistoryPlanningWindowAtBounded(
            head,
            seed,
            maxRawEventCount: 1
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Equal([head], available.Window.RawAddresses);
        Assert.Equal(2, available.PrefixDiagnostics.HeaderVisits);
        Assert.Equal(
            2,
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount
        );
        InvalidOperationException missingError =
            Assert.Throws<InvalidOperationException>(() =>
            engine.ReadHistoryPlanningWindowAtBounded(
                head,
                missing,
                maxRawEventCount: 2
            )
        );
        Assert.Contains(
            "Short read",
            missingError.ToString(),
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public void BoundedPlanning_RejectsProofToPayloadHeaderDrift() {
        (
            string path,
            EventAddress start,
            EventAddress head,
            SessionHistoryPlanningSeed seed
        ) = CreateOneEventPlanningFixture();
        var hooks = new SessionJournalTestHooks(
            RewriteBoundedHistoryProofHeader: header => header with {
                OpaqueEventKind =
                    (uint)SessionEventKind.ImportedAgentAction
            }
        );
        using var engine = SessionJournalEngine.OpenForTest(
            path,
            runtime: null,
            hooks,
            new EventJournalOptions()
        );

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => engine.ReadHistoryPlanningWindowAtBounded(
                head,
                seed,
                maxRawEventCount: 1
            )
        );

        Assert.Contains("proven lineage header", error.Message);
    }

    [Fact]
    public void BoundedPlanning_FullReadRejectsPayloadCrcCorruptionAfterProof() {
        var journalOptions = new EventJournalOptions {
            EventSegmentStoreOptions =
                new RbfSegmentStoreOptions {
                    SegmentSizeThresholdBytes = 4,
                    CacheMode = RbfCacheMode.Off
                }
        };
        (
            string path,
            EventAddress start,
            EventAddress head,
            SessionHistoryPlanningSeed seed
        ) = CreateOneEventPlanningFixture(journalOptions);
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(
                   path,
                   journalOptions
               )) {
            _ = AppendRaw(
                journal,
                head,
                SessionEventKind.ObservationAccepted,
                new ObservationAcceptedBody("unreferenced active")
            );
        }
        CorruptFramePayloadByte(path, head);
        bool proofCompleted = false;
        var hooks = new SessionJournalTestHooks(
            AfterBoundedHistoryProof: () => {
                Assert.False(proofCompleted);
                proofCompleted = true;
            }
        );
        using var engine = SessionJournalEngine.OpenForTest(
            path,
            runtime: null,
            hooks,
            journalOptions
        );

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
            () => engine.ReadHistoryPlanningWindowAtBounded(
                head,
                seed,
                maxRawEventCount: 1
            )
        );

        Assert.True(proofCompleted, error.ToString());
        Assert.Contains(
            "crc",
            error.ToString(),
            StringComparison.OrdinalIgnoreCase
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
        EventAddress Head,
        SessionHistoryPlanningSeed Seed
    ) CreateOneEventPlanningFixture(
        EventJournalOptions? journalOptions = null
    ) {
        string path = NewPath();
        journalOptions ??= new EventJournalOptions();
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            ),
            runtime: null,
            new SessionJournalTestHooks(),
            journalOptions
        );
        EventAddress start =
            engine.InspectExecutionBoundary().Head!.Value;
        SessionHistoryPlanningSeedBatch batch =
            engine.ReadHistoryPlanningSeeds([start]);
        SessionHistoryPlanningSeed seed =
            engine.CreateHistoryPlanningSeed(
                start,
                Assert.Single(batch.Seeds).Setups
            );
        EventAddress head = engine.AppendObservation("one");
        return (path, start, head, seed);
    }

    private static void CorruptFramePayloadByte(
        string path,
        EventAddress address
    ) {
        string segmentPath = Assert.Single(
            Directory.GetFiles(
                Path.Combine(path, "events"),
                $"{address.SegmentNumber:x8}.rbf",
                SearchOption.AllDirectories
            )
        );
        using var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite
        );
        long payloadOffset = checked(address.Ticket.Offset + 4);
        stream.Position = payloadOffset;
        int value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = payloadOffset;
        stream.WriteByte((byte)(value ^ 0x01));
        stream.Flush(flushToDisk: true);
    }

    private static void TruncateEventSegment(
        string path,
        uint segmentNumber
    ) {
        string segmentPath = Assert.Single(
            Directory.GetFiles(
                Path.Combine(path, "events"),
                $"{segmentNumber:x8}.rbf",
                SearchOption.AllDirectories
            )
        );
        using var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None
        );
        stream.SetLength(4);
        stream.Flush(flushToDisk: true);
    }

    private (
        string Path,
        EventAddress Start,
        EventAddress HeadAt512,
        EventAddress HeadAt513,
        EventAddress HeadAt514,
        SessionContextAnchorSetupReferences Setups
    ) CreatePlanningLineage() {
        string path = NewPath();
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        RefId main = journal.CreateBranch(
            SessionJournalDefaults.MainBranchName,
            startPoint: null
        ).Unwrap();
        var runtimeBody = new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            );
        byte[] runtimePayload = SessionEventCodec.Encode(
            SessionEventKind.RuntimeConfigSetup,
            runtimeBody
        );
        EventAddress runtime = journal.AppendEventFrame(
            parent: null,
            runtimePayload,
            (uint)SessionEventKind.RuntimeConfigSetup,
            hint: default
        ).Unwrap();
        var promptBody = new SystemPromptSetupBody("system-A");
        byte[] promptPayload = SessionEventCodec.Encode(
            SessionEventKind.SystemPromptSetup,
            promptBody
        );
        EventAddress prompt = journal.AppendEventFrame(
            runtime,
            promptPayload,
            (uint)SessionEventKind.SystemPromptSetup,
            hint: default
        ).Unwrap();
        var setups = new SessionContextAnchorSetupReferences(
            new SessionContextSetupReference(
                runtime,
                SessionEventCodec.GetExpectedBodySchemaVersion(
                    SessionEventKind.RuntimeConfigSetup
                ),
                SessionRequestCanonicalizer.Sha256Hex(
                    runtimePayload
                )
            ),
            new SessionContextSetupReference(
                prompt,
                SessionEventCodec.GetExpectedBodySchemaVersion(
                    SessionEventKind.SystemPromptSetup
                ),
                SessionRequestCanonicalizer.Sha256Hex(
                    promptPayload
                )
            )
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
        EventAddress headAt514 = AppendRaw(
            journal,
            headAt513,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([
                    new ActionBlock.Text("pending-answer")
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                ),
                $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(headAt513)}",
                new SessionExecutionCheckpoint(0),
                ToolRuntimeIdentity: null
            )
        );
        Assert.True(journal.MoveRef(main, null, headAt514).Unwrap());
        return (
            path,
            start,
            headAt512,
            headAt513,
            headAt514,
            setups
        );
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

using System.Security.Cryptography;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionContextCandidateContractTests : IDisposable {
    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup for test journals.
            }
        }
    }

    [Fact]
    public async Task FakeSource_LegalUnorderedCandidate_IsNormalizedAndValidated() {
        Fixture fixture = CreateFixture();
        SessionContextCandidate candidate = CreateCandidate(
            fixture,
            Contribution(MemoryPackCarrier.Action, "autobiography", "action memory", fixture.Boundary),
            Contribution(MemoryPackCarrier.Observation, "world", "observation memory", fixture.Anchor)
        );
        ICoherentContextCandidateSource source = new FakeCandidateSource(candidate);
        var request = new SessionContextSelectionRequest(
            fixture.Boundary,
            SessionContextSelectionMode.Latest,
            "roleplay.default",
            RawSuffixTokenBudget: 4096
        );
        request.ValidateShape();

        SessionContextCandidate selected = Assert.IsType<SessionContextCandidate>(
            await source.SelectAsync(request, CancellationToken.None)
        );
        ValidatedSessionContextCandidate validated = Validate(fixture, selected);

        Assert.Equal(fixture.Anchor, validated.RawStartExclusive);
        Assert.Equal(fixture.Anchor, validated.AnchorGoverningSetup.Head);
        Assert.Collection(
            validated.CanonicalContributions,
            observation => Assert.Equal(MemoryPackCarrier.Observation, observation.Target.Carrier),
            action => Assert.Equal(MemoryPackCarrier.Action, action.Target.Carrier)
        );
    }

    [Fact]
    public void Validator_RejectsEqualAnchor() {
        Fixture fixture = CreateFixture();
        SessionContextCandidate candidate = CreateCandidate(
            fixture,
            Contribution(MemoryPackCarrier.Observation, "world", "memory", fixture.Boundary)
        ) with { RawStartExclusive = fixture.Boundary };

        Assert.Throws<InvalidDataException>(() => Validate(fixture, candidate));
    }

    [Fact]
    public void Validator_RejectsDivergentAnchor() {
        Fixture fixture = CreateFixture();
        EventAddress divergent;
        using (var journal = EventJournal.EventJournal.OpenExisting(fixture.Path)) {
            journal.CreateBranch("off", fixture.Anchor).Unwrap();
            divergent = journal.CommitToRef(
                "off",
                fixture.Anchor,
                SessionEventCodec.Encode(
                    SessionEventKind.ObservationAccepted,
                    new ObservationAcceptedBody("off-main")
                ),
                opaqueEventKind: (uint)SessionEventKind.ObservationAccepted,
                hint: default
            ).Unwrap().EventAddress;
        }
        SessionContextCandidate candidate = CreateCandidate(
            fixture,
            Contribution(MemoryPackCarrier.Observation, "world", "memory", divergent)
        ) with { RawStartExclusive = divergent };

        Assert.Throws<InvalidDataException>(() => Validate(fixture, candidate));
    }

    [Fact]
    public void Validator_RejectsAnchorSetupReferenceMismatch() {
        Fixture fixture = CreateFixture();
        SessionContextCandidate candidate = CreateCandidate(
            fixture,
            Contribution(MemoryPackCarrier.Observation, "world", "memory", fixture.Anchor)
        ) with {
            AnchorSetups = fixture.AnchorSetups with {
                SystemPrompt = fixture.AnchorSetups.SystemPrompt with {
                    PayloadSha256 = new string('0', 64)
                }
            }
        };

        Assert.Throws<InvalidDataException>(() => Validate(fixture, candidate));
    }

    [Fact]
    public void Validator_RejectsSourceHeadOutsideAuthoritativeInterval() {
        Fixture fixture = CreateFixture();
        SessionContextCandidate candidate = CreateCandidate(
            fixture,
            Contribution(MemoryPackCarrier.Observation, "world", "memory", fixture.BeforeAnchor)
        );

        Assert.Throws<InvalidDataException>(() => Validate(fixture, candidate));
    }

    [Fact]
    public void Validator_RejectsDuplicateTargetAndInvalidCarrier() {
        Fixture fixture = CreateFixture();
        SessionContextCandidate duplicate = CreateCandidate(
            fixture,
            Contribution(MemoryPackCarrier.Observation, "world", "first", fixture.Anchor),
            Contribution(MemoryPackCarrier.Observation, "world", "second", fixture.Boundary)
        );
        Assert.Throws<InvalidDataException>(() => Validate(fixture, duplicate));

        SessionContextCandidate invalidCarrier = CreateCandidate(
            fixture,
            Contribution((MemoryPackCarrier)99, "invalid", "memory", fixture.Anchor)
        );
        Assert.Throws<InvalidDataException>(() => Validate(fixture, invalidCarrier));
    }

    [Fact]
    public void Validator_RejectsBadHashAndOversizedText() {
        Fixture fixture = CreateFixture();
        SessionContextContribution badHash = Contribution(
            MemoryPackCarrier.Observation,
            "world",
            "memory",
            fixture.Anchor
        ) with { ContentSha256 = new string('f', 64) };
        Assert.Throws<InvalidDataException>(() => Validate(fixture, CreateCandidate(fixture, badHash)));

        string oversizedText = new('x', 256 * 1024 + 1);
        SessionContextContribution oversized = Contribution(
            MemoryPackCarrier.Observation,
            "world",
            oversizedText,
            fixture.Anchor
        );
        Assert.Throws<InvalidDataException>(() => Validate(fixture, CreateCandidate(fixture, oversized)));
    }

    [Fact]
    public void Validator_SnapshotsProviderContributionsBeforeLineageAndContentValidation() {
        Fixture fixture = CreateFixture();
        SessionContextContribution accepted = Contribution(
            MemoryPackCarrier.Observation,
            "world",
            "accepted memory",
            fixture.Anchor
        );
        SessionContextContribution injectedAfterValidation = Contribution(
            MemoryPackCarrier.Action,
            "injected",
            "injected memory",
            fixture.BeforeAnchor
        ) with { ContentSha256 = new string('f', 64) };
        var unstable = new ChangesAfterFirstEnumerationList(
            [accepted],
            [injectedAfterValidation]
        );
        SessionContextCandidate candidate = new(
            fixture.Anchor,
            fixture.AnchorSetups,
            unstable
        );

        ValidatedSessionContextCandidate validated = Validate(fixture, candidate);

        SessionContextContribution only = Assert.Single(validated.CanonicalContributions);
        Assert.Equal("accepted memory", only.ExactText);
        Assert.Equal(1, unstable.EnumerationCount);
    }

    [Fact]
    public void Validator_UsesBoundedEnumerationSnapshotInsteadOfUntrustedCount() {
        Fixture fixture = CreateFixture();
        var countMismatch = new CountMismatchContributionList(
            reportedCount: 0,
            [
                Contribution(MemoryPackCarrier.Observation, "world", "world memory", fixture.Anchor),
                Contribution(MemoryPackCarrier.Action, "self", "self memory", fixture.Boundary)
            ]
        );
        SessionContextCandidate candidate = new(
            fixture.Anchor,
            fixture.AnchorSetups,
            countMismatch
        );

        ValidatedSessionContextCandidate validated = Validate(fixture, candidate);

        Assert.Equal(2, validated.CanonicalContributions.Length);
        Assert.Equal(1, countMismatch.EnumerationCount);
    }

    [Fact]
    public void SessionJournalProjectFile_DoesNotReferenceConcreteDerivedOrHostAssemblies() {
        string repoRoot = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal",
            "SessionJournal.csproj"
        ));

        Assert.DoesNotContain("SessionJournal.Maintainers", project, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionJournal.DerivedMemory", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent.Core", project, StringComparison.Ordinal);
    }

    private Fixture CreateFixture() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        EventAddress beforeAnchor = engine.Project().Head
            ?? throw new InvalidDataException("New SessionJournal has no setup head.");
        EventAddress anchor = engine.AppendObservation("anchor observation");
        EventAddress boundary = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("anchor action")]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(anchor);
        return new Fixture(
            path,
            beforeAnchor,
            anchor,
            boundary,
            setup,
            new SessionContextAnchorSetupReferences(
                CreateSetupReference(engine, setup.RuntimeConfigSetupAddress, SessionEventKind.RuntimeConfigSetup),
                CreateSetupReference(engine, setup.SystemPromptSetupAddress, SessionEventKind.SystemPromptSetup)
            )
        );
    }

    private static SessionContextSetupReference CreateSetupReference(
        SessionJournalEngine engine,
        EventAddress address,
        SessionEventKind kind
    ) {
        byte[] payload = engine.ReadPayloadBytes(address);
        _ = SessionEventCodec.Decode(kind, payload, out int schemaVersion);
        return new SessionContextSetupReference(
            address,
            schemaVersion,
            Convert.ToHexStringLower(SHA256.HashData(payload))
        );
    }

    private static SessionContextCandidate CreateCandidate(
        Fixture fixture,
        params SessionContextContribution[] contributions
    ) => new(fixture.Anchor, fixture.AnchorSetups, contributions);

    private static SessionContextContribution Contribution(
        MemoryPackCarrier carrier,
        string blockKey,
        string text,
        EventAddress sourceRawHead
    ) => new(
        new MemoryPackBlockPath(carrier, blockKey),
        text,
        SessionContextContributionHasher.CodecId,
        SessionContextContributionHasher.ComputeSha256(text),
        sourceRawHead
    );

    private static ValidatedSessionContextCandidate Validate(
        Fixture fixture,
        SessionContextCandidate candidate
    ) {
        using var journal = EventJournal.EventJournal.OpenExisting(fixture.Path);
        var reader = new SessionJournalEventReader(journal);
        return SessionContextCandidateValidator.Validate(
            reader,
            fixture.Boundary,
            fixture.AnchorSetup,
            candidate
        );
    }

    private static string FindRepositoryRoot() {
        for (DirectoryInfo? cursor = new DirectoryInfo(AppContext.BaseDirectory);
             cursor is not null;
             cursor = cursor.Parent) {
            if (File.Exists(Path.Combine(cursor.FullName, "prototypes", "SessionJournal", "SessionJournal.csproj"))) {
                return cursor.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Atelia repository root from the test assembly path.");
    }

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-context-candidate-contract-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed record Fixture(
        string Path,
        EventAddress BeforeAnchor,
        EventAddress Anchor,
        EventAddress Boundary,
        SessionGoverningSetup AnchorSetup,
        SessionContextAnchorSetupReferences AnchorSetups
    );

    private sealed class FakeCandidateSource(SessionContextCandidate candidate)
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidate?> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            request.ValidateShape();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SessionContextCandidate?>(candidate);
        }
    }

    private sealed class ChangesAfterFirstEnumerationList(
        IReadOnlyList<SessionContextContribution> first,
        IReadOnlyList<SessionContextContribution> later
    ) : IReadOnlyList<SessionContextContribution> {
        private readonly IReadOnlyList<SessionContextContribution> _first = first;
        private readonly IReadOnlyList<SessionContextContribution> _later = later;

        public int EnumerationCount { get; private set; }

        public int Count => _first.Count;

        public SessionContextContribution this[int index] => _first[index];

        public IEnumerator<SessionContextContribution> GetEnumerator() {
            EnumerationCount++;
            return (EnumerationCount == 1 ? _first : _later).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class CountMismatchContributionList(
        int reportedCount,
        IReadOnlyList<SessionContextContribution> contents
    ) : IReadOnlyList<SessionContextContribution> {
        private readonly IReadOnlyList<SessionContextContribution> _contents = contents;

        public int EnumerationCount { get; private set; }

        public int Count => reportedCount;

        public SessionContextContribution this[int index] => _contents[index];

        public IEnumerator<SessionContextContribution> GetEnumerator() {
            EnumerationCount++;
            return _contents.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}

using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionContextCandidateMaterializationContractTests {
    private static readonly EventAddress RuntimeSetup = Address(1);
    private static readonly EventAddress PromptSetup = Address(2);
    private static readonly EventAddress Anchor = Address(3);
    private static readonly EventAddress Boundary = Address(4);
    private static readonly SessionContextAnchorSetupReferences AnchorSetups =
        new(
            new SessionContextSetupReference(
                RuntimeSetup,
                1,
                new string('a', 64)
            ),
            new SessionContextSetupReference(
                PromptSetup,
                1,
                new string('b', 64)
            )
    );
    private static readonly SessionContextCandidateDescriptor Descriptor =
        new("contract-test", "snapshot-test", Anchor, AnchorSetups);
    private static readonly IReadOnlySet<EventAddress> AllowedSourceHeads =
        new HashSet<EventAddress> { Anchor, Boundary };

    [Fact]
    public void ContextHeaderBlockTarget_RequiresStrictSingleLineSemanticHeading() {
        string[] invalidHeadings = [
            "",
            " ",
            " leading",
            "trailing ",
            "line\nbreak",
            "line\rbreak",
            "control\u0001character",
            "invalid\ud800utf16",
            new('界', 86)
        ];

        foreach (string heading in invalidHeadings) {
            Assert.ThrowsAny<ArgumentException>(() =>
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.Observation,
                    "world",
                    heading
                ));
        }

        var accepted = new ContextHeaderBlockTarget(
            ContextHeaderCarrier.Observation,
            "world",
            "派生上下文：世界理解"
        );
        Assert.Equal("派生上下文：世界理解", accepted.SemanticHeading);
    }

    [Fact]
    public void MaterializedCandidate_RevalidatesProviderSemanticHeading() {
        var malformedTarget = (ContextHeaderBlockTarget)
            System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(ContextHeaderBlockTarget));
        var contribution = new SessionContextContribution(
            malformedTarget,
            "memory",
            SessionContextContributionHasher.CodecId,
            SessionContextContributionHasher.ComputeSha256("memory"),
            Anchor
        );

        Assert.Throws<InvalidDataException>(() =>
            Validate(CreateCandidate(contribution))
        );
    }

    [Fact]
    public void MaterializedCandidate_LegalUnorderedContributions_AreNormalized() {
        SessionContextCandidate candidate = CreateCandidate(
            Contribution(
                ContextHeaderCarrier.Action,
                "autobiography",
                "action memory",
                Boundary
            ),
            Contribution(
                ContextHeaderCarrier.Observation,
                "world",
                "observation memory",
                Anchor
            )
        );

        IReadOnlyList<SessionContextContribution> validated =
            Validate(candidate);

        Assert.Collection(
            validated,
            observation => Assert.Equal(
                ContextHeaderCarrier.Observation,
                observation.Target.Carrier
            ),
            action => Assert.Equal(
                ContextHeaderCarrier.Action,
                action.Target.Carrier
            )
        );
    }

    [Fact]
    public void RenderProviderHeader_ReturnsExactLeadingObservationAndAction() {
        SessionContextContribution[] contributions = [
            Contribution(
                ContextHeaderCarrier.Action,
                "self",
                "action memory",
                Boundary
            ),
            Contribution(
                ContextHeaderCarrier.Observation,
                "world-b",
                "second observation memory",
                Anchor
            ),
            Contribution(
                ContextHeaderCarrier.Observation,
                "world-a",
                "first observation memory",
                Anchor
            )
        ];

        ContextHeaderSnapshot rendered = SessionContextContributionContract
            .RenderProviderHeader(contributions);

        Assert.Equal(string.Empty, rendered.SystemPromptFragment);
        Assert.Equal(
            "## Derived context from prior history: world-a\n\n"
            + "~~~~recap-block\nfirst observation memory\n~~~~\n\n"
            + "## Derived context from prior history: world-b\n\n"
            + "~~~~recap-block\nsecond observation memory\n~~~~",
            rendered.ObservationMessage
        );
        Assert.Equal(
            "## Derived context from prior history: self\n\n"
            + "~~~~recap-block\naction memory\n~~~~",
            rendered.ActionMessage
        );
    }

    [Fact]
    public void MaterializedCandidate_RejectsDescriptorMismatch() {
        SessionContextCandidate candidate = CreateCandidate(
            Contribution(
                ContextHeaderCarrier.Observation,
                "world",
                "memory",
                Anchor
            )
        ) with {
            AnchorSetups = AnchorSetups with {
                SystemPrompt = AnchorSetups.SystemPrompt with {
                    PayloadSha256 = new string('0', 64)
                }
            }
        };

        Assert.Throws<InvalidDataException>(
            () => Validate(candidate)
        );
    }

    [Fact]
    public void MaterializedCandidate_RejectsSourceHeadOutsideAuthoritativeInterval() {
        SessionContextCandidate candidate = CreateCandidate(
            Contribution(
                ContextHeaderCarrier.Observation,
                "world",
                "memory",
                Address(5)
            )
        );

        Assert.Throws<InvalidDataException>(
            () => Validate(candidate)
        );
    }

    [Fact]
    public void MaterializedCandidate_RejectsDuplicateTargetAndInvalidCarrier() {
        SessionContextCandidate duplicate = CreateCandidate(
            Contribution(
                ContextHeaderCarrier.Observation,
                "world",
                "first",
                Anchor
            ),
            Contribution(
                ContextHeaderCarrier.Observation,
                "world",
                "second",
                Boundary,
                "A different heading does not create a distinct target"
            )
        );
        Assert.Throws<InvalidDataException>(
            () => Validate(duplicate)
        );

        SessionContextCandidate invalidCarrier = CreateCandidate(
            Contribution(
                (ContextHeaderCarrier)99,
                "invalid",
                "memory",
                Anchor
            )
        );
        Assert.Throws<InvalidDataException>(
            () => Validate(invalidCarrier)
        );
    }

    [Fact]
    public void MaterializedCandidate_RejectsBadHashAndOversizedText() {
        SessionContextContribution badHash = Contribution(
            ContextHeaderCarrier.Observation,
            "world",
            "memory",
            Anchor
        ) with { ContentSha256 = new string('f', 64) };
        Assert.Throws<InvalidDataException>(
            () => Validate(CreateCandidate(badHash))
        );

        string oversizedText = new('x', 256 * 1024 + 1);
        SessionContextContribution oversized = Contribution(
            ContextHeaderCarrier.Observation,
            "world",
            oversizedText,
            Anchor
        );
        Assert.Throws<InvalidDataException>(
            () => Validate(CreateCandidate(oversized))
        );
    }

    [Fact]
    public void MaterializedCandidate_SnapshotsProviderContributionsBeforeValidation() {
        SessionContextContribution accepted = Contribution(
            ContextHeaderCarrier.Observation,
            "world",
            "accepted memory",
            Anchor
        );
        SessionContextContribution injectedAfterValidation = Contribution(
            ContextHeaderCarrier.Action,
            "injected",
            "injected memory",
            Address(5)
        ) with { ContentSha256 = new string('f', 64) };
        var unstable = new ChangesAfterFirstEnumerationList(
            [accepted],
            [injectedAfterValidation]
        );
        var candidate = new SessionContextCandidate(
            Anchor,
            AnchorSetups,
            unstable
        );

        SessionContextContribution only =
            Assert.Single(Validate(candidate));

        Assert.Equal("accepted memory", only.ExactText);
        Assert.Equal(1, unstable.EnumerationCount);
    }

    [Fact]
    public void MaterializedCandidate_UsesBoundedSnapshotInsteadOfUntrustedCount() {
        var countMismatch = new CountMismatchContributionList(
            reportedCount: 0,
            [
                Contribution(
                    ContextHeaderCarrier.Observation,
                    "world",
                    "world memory",
                    Anchor
                ),
                Contribution(
                    ContextHeaderCarrier.Action,
                    "self",
                    "self memory",
                    Boundary
                )
            ]
        );
        var candidate = new SessionContextCandidate(
            Anchor,
            AnchorSetups,
            countMismatch
        );

        IReadOnlyList<SessionContextContribution> validated =
            Validate(candidate);

        Assert.Equal(2, validated.Count);
        Assert.Equal(1, countMismatch.EnumerationCount);
    }

    [Fact]
    public void MaterializedCandidate_RejectsMoreThanHardCapDespiteReportedCount() {
        var overflowing = new CountMismatchContributionList(
            reportedCount: 1,
            Enumerable.Range(0, 129)
                .Select(index => Contribution(
                    ContextHeaderCarrier.Observation,
                    $"block-{index}",
                    $"memory-{index}",
                    Anchor
                ))
                .ToArray()
        );
        var candidate = new SessionContextCandidate(
            Anchor,
            AnchorSetups,
            overflowing
        );

        Assert.Throws<InvalidDataException>(
            () => Validate(candidate)
        );
        Assert.Equal(1, overflowing.EnumerationCount);
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

        Assert.DoesNotContain(
            "SessionJournal.DerivedRecap.Maintainers",
            project,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "SessionJournal.DerivedMemory",
            project,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "Agent.Core",
            project,
            StringComparison.Ordinal
        );
    }

    private static IReadOnlyList<SessionContextContribution> Validate(
        SessionContextCandidate candidate
    ) => SessionContextCandidateValidator.ValidateMaterializedCandidate(
        Descriptor,
        candidate,
        AllowedSourceHeads,
        allowEmpty: false
    );

    private static SessionContextCandidate CreateCandidate(
        params SessionContextContribution[] contributions
    ) => new(Anchor, AnchorSetups, contributions);

    private static SessionContextContribution Contribution(
        ContextHeaderCarrier carrier,
        string blockKey,
        string text,
        EventAddress sourceRawHead,
        string? semanticHeading = null
    ) => new(
        new ContextHeaderBlockTarget(
            carrier,
            blockKey,
            semanticHeading
                ?? $"Derived context from prior history: {blockKey}"
        ),
        text,
        SessionContextContributionHasher.CodecId,
        SessionContextContributionHasher.ComputeSha256(text),
        sourceRawHead
    );

    private static EventAddress Address(ulong ticket)
        => EventAddressTextCodec.Parse(
            $"ej1:{ticket:x16}0000000100000000"
        );

    private static string FindRepositoryRoot() {
        for (
            DirectoryInfo? cursor =
                new DirectoryInfo(AppContext.BaseDirectory);
            cursor is not null;
            cursor = cursor.Parent
        ) {
            if (File.Exists(Path.Combine(
                    cursor.FullName,
                    "prototypes",
                    "SessionJournal",
                    "SessionJournal.csproj"
                ))) {
                return cursor.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Atelia repository root from the test assembly path."
        );
    }

    private sealed class ChangesAfterFirstEnumerationList(
        IReadOnlyList<SessionContextContribution> first,
        IReadOnlyList<SessionContextContribution> later
    ) : IReadOnlyList<SessionContextContribution> {
        private readonly IReadOnlyList<SessionContextContribution> _first =
            first;
        private readonly IReadOnlyList<SessionContextContribution> _later =
            later;

        public int EnumerationCount { get; private set; }

        public int Count => _first.Count;

        public SessionContextContribution this[int index] => _first[index];

        public IEnumerator<SessionContextContribution> GetEnumerator() {
            EnumerationCount++;
            return (
                EnumerationCount == 1
                    ? _first
                    : _later
            ).GetEnumerator();
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class CountMismatchContributionList(
        int reportedCount,
        IReadOnlyList<SessionContextContribution> contents
    ) : IReadOnlyList<SessionContextContribution> {
        private readonly IReadOnlyList<SessionContextContribution> _contents =
            contents;

        public int EnumerationCount { get; private set; }

        public int Count => reportedCount;

        public SessionContextContribution this[int index] =>
            _contents[index];

        public IEnumerator<SessionContextContribution> GetEnumerator() {
            EnumerationCount++;
            return _contents.GetEnumerator();
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}

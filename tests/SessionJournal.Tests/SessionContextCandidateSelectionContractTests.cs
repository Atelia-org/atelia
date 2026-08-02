using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionContextCandidateSelectionContractTests {
    [Fact]
    public void BeyondPrefixFactory_RequiresEvidenceDetailAndValidatesShape() {
        Assert.Throws<ArgumentException>(
            () => SessionContextCandidateSelection.BeyondPrefix(" ")
        );

        SessionContextCandidateSelection selection =
            SessionContextCandidateSelection.BeyondPrefix(
                "anchor ej1:... is beyond 513 inspected headers"
            );

        selection.ValidateShape();
        Assert.Equal(
            SessionContextCandidateSelectionStatus.BeyondPrefix,
            selection.Status
        );
        Assert.Null(selection.Candidate);
    }

    [Fact]
    public void SelectionShape_RejectsMissingOrUnexpectedDescriptor() {
        Assert.Throws<InvalidDataException>(() =>
            new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.Selected,
                Candidate: null
            ).ValidateShape()
        );

        SessionContextCandidateDescriptor descriptor = new(
            "handle",
            "snapshot",
            Address(3),
            new SessionContextAnchorSetupReferences(
                new SessionContextSetupReference(
                    Address(1),
                    1,
                    new string('a', 64)
                ),
                new SessionContextSetupReference(
                    Address(2),
                    1,
                    new string('b', 64)
                )
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.BeyondPrefix,
                descriptor,
                "bounded evidence"
            ).ValidateShape()
        );
        Assert.Throws<InvalidDataException>(() =>
            new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.BeyondPrefix,
                Candidate: null,
                Detail: null
            ).ValidateShape()
        );
        Assert.Throws<InvalidDataException>(() =>
            new SessionContextCandidateSelection(
                (SessionContextCandidateSelectionStatus)int.MaxValue,
                Candidate: null
            ).ValidateShape()
        );
    }

    private static EventAddress Address(ulong ticket)
        => EventAddressTextCodec.Parse(
            $"ej1:{ticket:x16}0000000100000000"
        );
}

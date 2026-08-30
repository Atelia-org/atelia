using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteOriginBarrierTests {
    [Fact]
    public void BuilderUsesOnlySingleSourceActionsAndSharedFingerprint() {
        EventAddress observation = Address(1);
        EventAddress actionAddress = Address(2);
        var action = new ActionMessage([
            new ActionBlock.Text("visible "),
            new ActionBlock.Text("note \u4f60\u597d"),
        ]);
        var reader = new RecordingOriginReader();

        CharacterNoteOriginBarrier barrier =
            GalateaCharacterNoteOriginBarrierBuilder
                .BuildFromProviderVisibleRawUnits([
                    new SessionHistoryPlanningUnit(
                        new ObservationMessage("player"),
                        observation,
                        observation
                    ),
                    new SessionHistoryPlanningUnit(
                        action,
                        actionAddress,
                        actionAddress
                    ),
                ], reader);

        Assert.Same(reader.Returned, barrier);
        CharacterNoteVisibleActionIdentity visible = Assert.Single(
            reader.VisibleActions
        );
        Assert.Equal(actionAddress, visible.SourceAction);
        var target = new GalateaTerminalActionExtractionTarget(
            actionAddress,
            GalateaVisibleActionTextRenderer.Render(action)
        );
        Assert.Equal(target.VisibleTextSha256,
            visible.Fingerprint.Sha256);
        Assert.Equal(target.VisibleTextUtf8Bytes,
            visible.Fingerprint.Utf8Bytes);
    }

    [Fact]
    public void BuilderRequiresOneExactSourceAddressPerVisibleAction() {
        var reader = new RecordingOriginReader();

        Assert.Throws<InvalidDataException>(() =>
            GalateaCharacterNoteOriginBarrierBuilder
                .BuildFromProviderVisibleRawUnits([
                    new SessionHistoryPlanningUnit(
                        new ActionMessage([
                            new ActionBlock.Text("visible")
                        ]),
                        Address(1),
                        Address(2)
                    ),
                ], reader)
        );
        Assert.Empty(reader.VisibleActions);
    }

    [Fact]
    public void MissingCharacterMemoryBindingProducesEmptyBarrier() {
        CharacterNoteOriginBarrier barrier =
            GalateaCharacterNoteOriginBarrierBuilder
                .BuildFromProviderVisibleRawUnits([
                    new SessionHistoryPlanningUnit(
                        new ActionMessage([
                            new ActionBlock.Text("visible")
                        ]),
                        Address(1),
                        Address(2)
                    ),
                ], originReader: null);

        Assert.Empty(barrier.Entries);
    }

    [Fact]
    public void TypedMemoKeysDeduplicateExactOriginsAndRejectConflicts() {
        MemoPodId podId = MemoPodId.Parse(
            "00000000000000000000000000000001"
        );
        MemoId memoId = MemoId.Parse("m1:00000001");
        CharacterNoteVisibleActionIdentity first = Origin(1, "first");
        var exact = new CharacterNoteOriginBarrierEntry(
            podId,
            memoId,
            first
        );
        var barrier = new CharacterNoteOriginBarrier([exact, exact]);

        Assert.Single(barrier.Entries);
        Assert.True(barrier.Contains(podId, memoId));
        Assert.False(barrier.Contains(
            podId,
            MemoId.Parse("m1:00000002")
        ));
        Assert.Throws<InvalidDataException>(() =>
            new CharacterNoteOriginBarrier([
                exact,
                new CharacterNoteOriginBarrierEntry(
                    podId,
                    memoId,
                    Origin(2, "second")
                ),
            ])
        );
    }

    private static CharacterNoteVisibleActionIdentity Origin(
        int address,
        string visibleText
    ) => new(
        Address(address),
        GalateaVisibleActionFingerprint.Derive(visibleText)
    );

    private static EventAddress Address(int value) => new(
        SizedPtr.Create(value * 4L, 4),
        SegmentNumber: 1,
        AddressHint.None
    );

    private sealed class RecordingOriginReader : ICharacterNoteOriginReader {
        internal CharacterNoteOriginBarrier Returned { get; } = new([]);
        internal IReadOnlyList<CharacterNoteVisibleActionIdentity>
            VisibleActions { get; private set; } = [];

        public CharacterNoteOriginBarrier ReadOriginBarrier(
            IReadOnlyList<CharacterNoteVisibleActionIdentity> visibleActions
        ) {
            VisibleActions = visibleActions;
            return Returned;
        }
    }
}

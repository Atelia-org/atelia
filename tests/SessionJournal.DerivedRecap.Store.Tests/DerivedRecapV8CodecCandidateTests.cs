using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapV8CodecCandidateTests {
    private const string ZeroHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly EventAddress A1 = new(
        SizedPtr.FromPacked(0x0102_0304_0506_0708),
        0x0a0b_0c0d,
        AddressHint.None
    );

    private static readonly EventAddress A2 = new(
        SizedPtr.FromPacked(0x1112_1314_1516_1718),
        0x1a1b_1c1d,
        AddressHint.None
    );

    [Fact]
    public void EpochInputRoundTripsOneClosedFrozenProjection() {
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 4,
            ZeroHash,
            [
                new ObservationMessage("B"),
                new ActionMessage([
                    new ActionBlock.Text("answer"),
                    new ActionBlock.ToolCall(
                        new RawToolCall("lookup", "call-1", "{}")
                    )
                ]),
                new ToolResultsMessage(
                    null,
                    [ToolResult.FromText(
                        "lookup",
                        "call-1",
                        ToolExecutionStatus.Success,
                        "ok"
                    )]
                )
            ],
            RecapEpochPrevious.Empty.Instance
        );

        byte[] encoded = DerivedRecapV8Codec.EncodeEpochInput(input);
        DerivedRecapEpochInput decoded =
            DerivedRecapV8Codec.DecodeEpochInput(encoded);

        Assert.Equal(4, decoded.RawEventCount);
        Assert.Equal(3, decoded.HistoryMessages.Count);
        Assert.IsType<RecapEpochPrevious.Empty>(decoded.Previous);
        Assert.Equal(encoded, DerivedRecapV8Codec.EncodeEpochInput(decoded));
    }

    [Fact]
    public void PriorPackIsStructuredAndFinalCannotCrossEpochs() {
        RecapEpochBlockDefinition definition = Definition("facts", 0);
        DerivedRecapEpochInput firstInput = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            ZeroHash,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest firstManifest =
            DerivedRecapV8Codec.CreateManifest(
                new RefId(0x55),
                A2,
                firstInput.PayloadSha256,
                [definition]
            );
        DerivedRecapFinalBlock firstFinal =
            DerivedRecapV8Codec.CreateFinalBlock(
                firstManifest,
                definition,
                "fact A"
            );
        DerivedRecapV8Codec.ValidateEpochSet(firstManifest, firstInput);
        PublishedRecapEpoch publication =
            DerivedRecapV8Codec.CreatePublication(
                firstManifest,
                [firstFinal]
            );
        PriorRecapPackSnapshot prior = DerivedRecapV8Codec.CreatePriorPack(
            new PublishedRecapEpochDescriptor(
                publication.RefId,
                publication.AdmissionAnchor,
                publication.EnvelopeSha256
            ),
            [DerivedRecapV8Codec.CreatePriorBlock(
                firstFinal.RecapBlockId,
                firstFinal.Target,
                firstFinal.Content,
                firstFinal.EpochBlockExecutionSha256,
                firstFinal.PayloadSha256
            )]
        );
        DerivedRecapEpochInput secondInput =
            DerivedRecapV8Codec.CreateEpochInput(
                Boundary(A2),
                Boundary(A1),
                rawEventCount: 2,
                new string('1', 64),
                [new ObservationMessage("B")],
                new RecapEpochPrevious.Prior(prior)
            );
        DerivedRecapEpochManifest secondManifest =
            DerivedRecapV8Codec.CreateManifest(
                publication.RefId,
                A1,
                secondInput.PayloadSha256,
                [definition]
            );
        DerivedRecapV8Codec.ValidateEpochSet(secondManifest, secondInput);

        DerivedRecapEpochInput reopenedSecond =
            DerivedRecapV8Codec.DecodeEpochInput(
                DerivedRecapV8Codec.EncodeEpochInput(secondInput)
            );
        var reopenedPrior = Assert.IsType<RecapEpochPrevious.Prior>(
            reopenedSecond.Previous
        );
        Assert.Equal(
            firstFinal.EpochBlockExecutionSha256,
            Assert.Single(reopenedPrior.Pack.Blocks)
                .SourceEpochBlockExecutionSha256
        );
        Assert.Equal(
            firstFinal.PayloadSha256,
            Assert.Single(reopenedPrior.Pack.Blocks).SourcePayloadSha256
        );

        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.ValidateFinalForManifest(
                secondManifest,
                firstFinal
            )
        );
        Assert.NotEqual(
            firstFinal.EpochBlockExecutionSha256,
            DerivedRecapV8Codec.ComputeEpochBlockExecutionSha256(
                secondManifest,
                definition
            )
        );
    }

    [Fact]
    public void EpochSetRejectsCrossComponentAndPriorFinalMisbindings() {
        RecapEpochBlockDefinition definition = Definition("facts", 0);
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            ZeroHash,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        DerivedRecapEpochManifest manifest =
            DerivedRecapV8Codec.CreateManifest(
                new RefId(0x55),
                A2,
                input.PayloadSha256,
                [definition]
            );

        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.ValidateEpochSet(
                manifest with { AdmissionAnchor = A1 },
                input
            )
        );

        DerivedRecapFinalBlock final =
            DerivedRecapV8Codec.CreateFinalBlock(
                manifest,
                definition,
                "fact A"
            );
        PriorRecapBlockSnapshot mismatched =
            DerivedRecapV8Codec.CreatePriorBlock(
                final.RecapBlockId,
                final.Target,
                "different content",
                final.EpochBlockExecutionSha256,
                final.PayloadSha256
            );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.CreatePriorPack(
                new PublishedRecapEpochDescriptor(
                    manifest.RefId,
                    manifest.AdmissionAnchor,
                    new string('2', 64)
                ),
                [mismatched]
            )
        );

        RecapEpochBlockDefinition later = new(
            new RecapBlockId("later"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.Observation,
                "later"
            ),
            "later",
            RecapTestIdentity.CapabilityFingerprint,
            1024,
            0
        );
        RecapEpochBlockDefinition earlier = Definition("earlier", 1);
        DerivedRecapEpochManifest reversed =
            DerivedRecapV8Codec.CreateManifest(
                manifest.RefId,
                manifest.AdmissionAnchor,
                input.PayloadSha256,
                [later, earlier]
            );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.ValidateEpochSet(reversed, input)
        );
    }

    [Fact]
    public void StrictCodecRejectsOldSchemaUnknownPropertyAndReasoning() {
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            Boundary(A1),
            Boundary(A2),
            rawEventCount: 1,
            ZeroHash,
            [new ObservationMessage("A")],
            RecapEpochPrevious.Empty.Instance
        );
        string json = Encoding.UTF8.GetString(
            DerivedRecapV8Codec.EncodeEpochInput(input)
        );

        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.DecodeEpochInput(
                Encoding.UTF8.GetBytes(
                    json.Replace(
                        DerivedRecapV8Codec.EpochInputSchema,
                        "atelia.session-journal.derived-recap-epoch-input.v7",
                        StringComparison.Ordinal
                    )
                )
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.DecodeEpochInput(
                Encoding.UTF8.GetBytes(
                    json.Insert(json.Length - 1, ",\"unknown\":true")
                )
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.CreateEpochInput(
                Boundary(A1),
                Boundary(A2),
                rawEventCount: 1,
                ZeroHash,
                [new ActionMessage([
                    new ActionBlock.TextReasoningBlock(
                        "private",
                        new CompletionDescriptor("p", "api", "m")
                    )
                ])],
                RecapEpochPrevious.Empty.Instance
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapV8Codec.CreateEpochInput(
                Boundary(A1),
                Boundary(A2),
                rawEventCount: 1,
                ZeroHash,
                [new ObservationMessage("\ud800")],
                RecapEpochPrevious.Empty.Instance
            )
        );
    }

    private static RecapEpochBoundary Boundary(EventAddress address)
        => new(address, RecapWireTestFacts.SyntheticSetups(address));

    private static RecapEpochBlockDefinition Definition(
        string id,
        int ordinal
    ) => new(
        new RecapBlockId(id),
        new ContextHeaderBlockPath(ContextHeaderCarrier.System, id),
        id,
        RecapTestIdentity.CapabilityFingerprint,
        1024,
        ordinal
    );
}

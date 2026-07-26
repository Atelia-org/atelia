using System.Text;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionArtifactSetCodecTests {
    private static readonly EventAddress A =
        EventAddressTextCodec.Parse("ej1:00000000000000010000000100000000");
    private static readonly EventAddress B =
        EventAddressTextCodec.Parse("ej1:00000000000000020000000100000000");

    [Fact]
    public void ArtifactSetCommitted_RoundtripsCanonicalBytesExactly() {
        ArtifactSetCommittedBody body = CreateBody();

        byte[] encoded = SessionEventCodec.Encode(
            SessionEventKind.ArtifactSetCommitted,
            body
        );
        var decoded = Assert.IsType<ArtifactSetCommittedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.ArtifactSetCommitted,
                encoded,
                out int version
            )
        );

        Assert.Equal(1, version);
        Assert.Equal(body.PolicyId, decoded.PolicyId);
        Assert.Equal(body.PolicyFingerprint, decoded.PolicyFingerprint);
        Assert.Equal(body.CommonAnchor, decoded.CommonAnchor);
        Assert.Equal(body.CoverageSetups, decoded.CoverageSetups);
        Assert.Equal(body.CurrentSetups, decoded.CurrentSetups);
        Assert.Equal(body.Members.Length, decoded.Members.Length);
        for (int i = 0; i < body.Members.Length; i++) {
            Assert.Equal(body.Members[i].RoleId, decoded.Members[i].RoleId);
            Assert.Equal(body.Members[i].ArtifactId, decoded.Members[i].ArtifactId);
            Assert.Equal(body.Members[i].ArtifactKind, decoded.Members[i].ArtifactKind);
            Assert.Equal(
                body.Members[i].Target.Carrier,
                decoded.Members[i].Target.Carrier
            );
            Assert.Equal(
                body.Members[i].Target.BlockKey,
                decoded.Members[i].Target.BlockKey
            );
            Assert.Equal(
                body.Members[i].ContentSha256,
                decoded.Members[i].ContentSha256
            );
        }
        Assert.Equal(
            encoded,
            SessionEventCodec.Encode(
                SessionEventKind.ArtifactSetCommitted,
                decoded
            )
        );
        string json = Encoding.UTF8.GetString(encoded);
        Assert.True(
            json.IndexOf("\"roleId\":\"autobiography\"", StringComparison.Ordinal)
            < json.IndexOf("\"roleId\":\"world\"", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ArtifactSetCommitted_RejectsUnknownOrMissingWireProperties() {
        string json = Encoding.UTF8.GetString(
            SessionEventCodec.Encode(
                SessionEventKind.ArtifactSetCommitted,
                CreateBody()
            )
        );
        string unknown = json.Replace(
            "\"policyId\":",
            "\"unknown\":1,\"policyId\":",
            StringComparison.Ordinal
        );
        string missing = json.Replace(
            "\"policyFingerprint\":\"atelia.session-journal.active-artifact-set.v1\",",
            "",
            StringComparison.Ordinal
        );

        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.ArtifactSetCommitted,
            Encoding.UTF8.GetBytes(unknown),
            out _
        ));
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.ArtifactSetCommitted,
            Encoding.UTF8.GetBytes(missing),
            out _
        ));
    }

    private static ArtifactSetCommittedBody CreateBody() {
        var setup = new SessionGoverningSetupReferences(
            new SessionSetupReference(A, 1, new string('a', 64)),
            new SessionSetupReference(B, 1, new string('b', 64))
        );
        return new ArtifactSetCommittedBody(
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
            A,
            setup,
            setup,
            [
                new SessionArtifactSetMember(
                    "autobiography",
                    "artifact-a",
                    "autobiography",
                    new MemoryPackBlockPath(MemoryPackCarrier.Action, "self"),
                    new string('c', 64)
                ),
                new SessionArtifactSetMember(
                    "world",
                    "artifact-w",
                    "world-understanding",
                    new MemoryPackBlockPath(MemoryPackCarrier.Observation, "world"),
                    new string('d', 64)
                )
            ]
        );
    }
}

using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaHistoricalAgentControlProfileLazyLoadingTests {
    [Fact]
    public void ExactBindLoadsConfiguredProfilesOnceAndCachesTheRegistry() {
        RecapGridAgentControlProfile profile = CreateProfile("one", 1);
        RecapGridAgentControlProfile other = CreateProfile("two", 2);
        int reads = 0;
        var resolver = new GalateaHistoricalAgentControlProfiles(
            ["one.json", "two.json"],
            path => {
                reads++;
                return path == "one.json"
                    ? profile.ToCanonicalBytes()
                    : other.ToCanonicalBytes();
            });

        Assert.Equal(0, reads);
        Assert.True(resolver.TryBindExact(profile.RuntimeIdentity, out var first));
        Assert.Equal(profile.ProfileId, first.ProfileId);
        Assert.Equal(profile.RuntimeIdentity, first.RuntimeIdentity);
        Assert.Equal(2, reads);
        Assert.True(resolver.TryGet("one", out var second));
        Assert.Equal(profile.ProfileId, second.ProfileId);
        Assert.Equal(profile.RuntimeIdentity, second.RuntimeIdentity);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void DecodeFailureIsDeferredAndCached() {
        int reads = 0;
        byte[] bytes = "{"u8.ToArray();
        var resolver = new GalateaHistoricalAgentControlProfiles(
            ["broken.json"],
            _ => {
                reads++;
                return bytes;
            });

        Assert.Equal(0, reads);
        Assert.Throws<InvalidDataException>(() => resolver.TryGet("one", out _));
        Assert.Equal(1, reads);
        bytes = CreateProfile("one", 1).ToCanonicalBytes();
        Assert.Throws<InvalidDataException>(() => resolver.TryGet("one", out _));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void DuplicateIdentitiesAreRejectedOnlyAtFirstExactLookup() {
        RecapGridAgentControlProfile first = CreateProfile("duplicate", 1);
        RecapGridAgentControlProfile second = CreateProfile("duplicate", 2);
        int reads = 0;
        var resolver = new GalateaHistoricalAgentControlProfiles(
            ["first.json", "second.json"],
            path => {
                reads++;
                return path == "first.json"
                    ? first.ToCanonicalBytes()
                    : second.ToCanonicalBytes();
            });

        Assert.Equal(0, reads);
        Assert.Throws<ArgumentException>(() => resolver.TryBindExact(
            first.RuntimeIdentity, out _));
        Assert.Equal(2, reads);
    }

    private static RecapGridAgentControlProfile CreateProfile(
        string profileId,
        int discriminator
    ) {
        Assert.True(RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
            RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
            out RecapGridControlRegistrationBundle? bundle));
        return RecapGridAgentControlProfile.Create(profileId,
            new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                [bundle!.Families[0].Digest],
                bundle.Definitions.Select(static item =>
                    item.Capability.CapabilityFingerprint),
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: discriminator,
                maximumProjectedCalls: 1_024));
    }
}

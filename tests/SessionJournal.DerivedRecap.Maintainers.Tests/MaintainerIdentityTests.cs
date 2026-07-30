using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public class MaintainerIdentityTests {
    [Fact]
    public void PersistedIdentity_IsStable() {
        Assert.Equal(
            "roleplay.first-person-autobiography.rewrite",
            AutobiographicalRewriteProfiles.MaintainerId
        );
        Assert.Equal(
            "roleplay.world-understanding.rewrite",
            WorldUnderstandingRewriteProfiles.MaintainerId
        );

        Assert.Equal(
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.Action,
                "roleplay.first-person-autobiography"
            ),
            RolePlayRecapBlockPaths.FirstPersonAutobiography
        );
        Assert.Equal(
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.Observation,
                "roleplay.world-understanding"
            ),
            RolePlayRecapBlockPaths.WorldUnderstanding
        );

        RecapMaintainerProfileDescriptor autobiography =
            RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                RecapMaintainerProfileCatalog
                    .AutobiographicalRewrite
            );
        RecapMaintainerProfileDescriptor world =
            RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                RecapMaintainerProfileCatalog
                    .WorldUnderstandingRewrite
            );
        Assert.Equal("autobiography", autobiography.RoleId);
        Assert.Equal(
            AutobiographicalRewriteProfiles.MaintainerId,
            autobiography.MaintainerId
        );
        Assert.Equal(
            RolePlayRecapBlockPaths
                .FirstPersonAutobiographyBlockKey,
            autobiography.RecapBlockIdValue
        );
        Assert.Equal("world-understanding", world.RoleId);
        Assert.Equal(
            WorldUnderstandingRewriteProfiles.MaintainerId,
            world.MaintainerId
        );
        Assert.Equal(
            RolePlayRecapBlockPaths.WorldUnderstandingBlockKey,
            world.RecapBlockIdValue
        );
        Assert.Matches(
            "^sha256:[0-9a-f]{64}$",
            autobiography.PromptFingerprint
        );
    }

    [Fact]
    public void PromptFingerprint_IsStructuredAndNulBoundarySafe() {
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.Action,
            "memory.test"
        );
        var left = new RecapMaintainerProfileDescriptor(
            "profile",
            "role",
            "memory.left",
            new RecapRewriteProfile(
                "maintainer",
                target,
                "a\0b",
                "c"
            )
        );
        var right = new RecapMaintainerProfileDescriptor(
            "profile",
            "role",
            "memory.right",
            new RecapRewriteProfile(
                "maintainer",
                target,
                "a",
                "b\0c"
            )
        );

        Assert.NotEqual(
            left.PromptFingerprint,
            right.PromptFingerprint
        );
    }
}

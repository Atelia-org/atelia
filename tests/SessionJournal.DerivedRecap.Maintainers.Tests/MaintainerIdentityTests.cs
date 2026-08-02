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
        Assert.Equal(
            RewriteRecapBlockMaintainer.ImplementationId,
            autobiography.ImplementationId
        );
        Assert.Equal(
            "sha256:a74f4fa428ff5283a078470dd26650e0d694e87308305f3376cc029218737aef",
            autobiography.PromptFingerprint
        );
        Assert.Equal(
            "sha256:ac851405d18654fb3428afc7c8050bac46c3072184da8177388446b57d79552c",
            autobiography.CapabilityFingerprint
        );
        Assert.Equal(
            "sha256:4e41ab30e63e48a34e3416c1062be0328d086995bbafd7167c5c8785e2edea1d",
            world.PromptFingerprint
        );
        Assert.Equal(
            "sha256:3439d6e858a55784c1f2946de366c9799caaddd58df31eaf18636347f8a1b5bd",
            world.CapabilityFingerprint
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
        Assert.NotEqual(
            left.CapabilityFingerprint,
            right.CapabilityFingerprint
        );
    }

    [Fact]
    public void ImplementationIdentity_IsCapabilityHashInput() {
        RecapMaintainerProfileDescriptor descriptor =
            RecapMaintainerProfileCatalog.BuiltIn.All[0];

        string changed =
            RecapMaintainerCapabilityFingerprint.Compute(
                descriptor.ImplementationId + ".next",
                descriptor.MaintainerId,
                descriptor.Target,
                descriptor.PromptFingerprint
            );

        Assert.NotEqual(descriptor.CapabilityFingerprint, changed);
    }
}

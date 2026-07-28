using Atelia.SessionJournal;
using Atelia.SessionJournal.Maintainers;
using Xunit;

namespace Atelia.SessionJournal.Maintainers.Tests;

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
            new MemoryPackBlockPath(
                MemoryPackCarrier.Action,
                "roleplay.first-person-autobiography"
            ),
            RolePlayMemoryBlockPaths.FirstPersonAutobiography
        );
        Assert.Equal(
            new MemoryPackBlockPath(
                MemoryPackCarrier.Observation,
                "roleplay.world-understanding"
            ),
            RolePlayMemoryBlockPaths.WorldUnderstanding
        );

        MemoryMaintainerProfileDescriptor autobiography =
            MemoryMaintainerProfileCatalog.Resolve(
                MemoryMaintainerProfileCatalog
                    .AutobiographicalRewrite
            );
        MemoryMaintainerProfileDescriptor world =
            MemoryMaintainerProfileCatalog.Resolve(
                MemoryMaintainerProfileCatalog
                    .WorldUnderstandingRewrite
            );
        Assert.Equal("autobiography", autobiography.RoleId);
        Assert.Equal(
            AutobiographicalRewriteProfiles.MaintainerId,
            autobiography.RewriteProfile.Id
        );
        Assert.Equal("world-understanding", world.RoleId);
        Assert.Equal(
            WorldUnderstandingRewriteProfiles.MaintainerId,
            world.RewriteProfile.Id
        );
        Assert.Matches(
            "^sha256:[0-9a-f]{64}$",
            autobiography.PromptFingerprint
        );
    }

    [Fact]
    public void PromptFingerprint_IsStructuredAndNulBoundarySafe() {
        var target = new MemoryPackBlockPath(
            MemoryPackCarrier.Action,
            "memory.test"
        );
        var left = new MemoryMaintainerProfileDescriptor(
            "profile",
            "role",
            new MemoryRewriteProfile(
                "maintainer",
                target,
                "a\0b",
                "c"
            )
        );
        var right = new MemoryMaintainerProfileDescriptor(
            "profile",
            "role",
            new MemoryRewriteProfile(
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

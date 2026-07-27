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
    }
}

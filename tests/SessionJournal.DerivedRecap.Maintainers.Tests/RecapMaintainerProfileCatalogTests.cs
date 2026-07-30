using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public class RecapMaintainerProfileCatalogTests {
    [Fact]
    public void BuiltIn_IsOneOrderedImmutableSnapshotWithDualIndexes() {
        RecapMaintainerProfileCatalog catalog =
            RecapMaintainerProfileCatalog.BuiltIn;

        Assert.Same(catalog, RecapMaintainerProfileCatalog.BuiltIn);
        Assert.Collection(
            catalog.All,
            world => Assert.Equal(
                RecapMaintainerProfileCatalog
                    .WorldUnderstandingRewrite,
                world.ProfileName
            ),
            autobiography => Assert.Equal(
                RecapMaintainerProfileCatalog
                    .AutobiographicalRewrite,
                autobiography.ProfileName
            )
        );
        Assert.Throws<NotSupportedException>(
            () => ((IList<RecapMaintainerProfileDescriptor>)
                catalog.All).Clear()
        );

        RecapMaintainerProfileDescriptor expected = catalog.All[0];
        Assert.True(catalog.TryResolveProfileName(
            expected.ProfileName,
            out RecapMaintainerProfileDescriptor byProfileName
        ));
        Assert.Same(expected, byProfileName);
        Assert.True(catalog.TryResolveFrozen(
            expected.MaintainerId,
            expected.Target,
            out RecapMaintainerProfileDescriptor byFrozenIdentity
        ));
        Assert.Same(expected, byFrozenIdentity);
        Assert.Same(expected, catalog.Resolve(expected.ProfileName));
    }

    [Fact]
    public void Snapshot_DoesNotObserveCallerListMutation() {
        var descriptors = new List<
            RecapMaintainerProfileDescriptor
        > {
            Descriptor(
                "first-profile",
                "first-maintainer",
                Target(ContextHeaderCarrier.Action, "target.first"),
                "recap.first"
            )
        };

        var catalog = new RecapMaintainerProfileCatalog(descriptors);
        descriptors.Clear();

        Assert.Single(catalog.All);
        Assert.Equal("first-profile", catalog.All[0].ProfileName);
    }

    [Fact]
    public void Descriptor_BlockIdentityIsExplicitAndIndependentOfTarget() {
        ContextHeaderBlockPath target = Target(
            ContextHeaderCarrier.Observation,
            "context.target"
        );
        RecapMaintainerProfileDescriptor descriptor = Descriptor(
            "profile",
            "maintainer",
            target,
            "stable.recap-block"
        );
        var catalog = new RecapMaintainerProfileCatalog([descriptor]);

        Assert.NotEqual(
            descriptor.Target.BlockKey,
            descriptor.RecapBlockIdValue
        );
        Assert.True(catalog.TryResolveFrozen(
            "maintainer",
            target,
            out RecapMaintainerProfileDescriptor resolved
        ));
        Assert.Same(descriptor, resolved);
    }

    [Fact]
    public void Constructor_RejectsDuplicateProfileName() {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RecapMaintainerProfileCatalog([
                Descriptor(
                    "duplicate",
                    "maintainer-a",
                    Target(
                        ContextHeaderCarrier.Action,
                        "target.a"
                    ),
                    "recap.a"
                ),
                Descriptor(
                    "duplicate",
                    "maintainer-b",
                    Target(
                        ContextHeaderCarrier.Observation,
                        "target.b"
                    ),
                    "recap.b"
                )
            ])
        );

        Assert.Contains("duplicate profile name", error.Message);
    }

    [Fact]
    public void Constructor_RejectsDuplicateExactFrozenIdentity() {
        ContextHeaderBlockPath target = Target(
            ContextHeaderCarrier.Action,
            "shared.target"
        );

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new RecapMaintainerProfileCatalog([
                Descriptor(
                    "profile-a",
                    "shared-maintainer",
                    target,
                    "recap.a"
                ),
                Descriptor(
                    "profile-b",
                    "shared-maintainer",
                    target,
                    "recap.b"
                )
            ])
        );

        Assert.Contains("duplicate frozen identity", error.Message);
    }

    [Fact]
    public void Constructor_AllowsSameTargetForDifferentMaintainers() {
        ContextHeaderBlockPath target = Target(
            ContextHeaderCarrier.Action,
            "shared.target"
        );
        RecapMaintainerProfileDescriptor first = Descriptor(
            "profile-a",
            "maintainer-a",
            target,
            "shared.recap"
        );
        RecapMaintainerProfileDescriptor second = Descriptor(
            "profile-b",
            "maintainer-b",
            target,
            "shared.recap"
        );

        var catalog = new RecapMaintainerProfileCatalog([
            first,
            second
        ]);

        Assert.True(catalog.TryResolveFrozen(
            first.MaintainerId,
            target,
            out RecapMaintainerProfileDescriptor resolvedFirst
        ));
        Assert.True(catalog.TryResolveFrozen(
            second.MaintainerId,
            target,
            out RecapMaintainerProfileDescriptor resolvedSecond
        ));
        Assert.Same(first, resolvedFirst);
        Assert.Same(second, resolvedSecond);
    }

    [Fact]
    public void TryResolveMethods_ReturnFalseForUnknownOrNullKeys() {
        RecapMaintainerProfileCatalog catalog =
            RecapMaintainerProfileCatalog.BuiltIn;

        Assert.False(catalog.TryResolveProfileName(
            "unknown",
            out _
        ));
        Assert.False(catalog.TryResolveProfileName(null, out _));
        Assert.False(catalog.TryResolveFrozen(
            "unknown",
            catalog.All[0].Target,
            out _
        ));
        Assert.False(catalog.TryResolveFrozen(
            null,
            catalog.All[0].Target,
            out _
        ));
        Assert.False(catalog.TryResolveFrozen(
            catalog.All[0].MaintainerId,
            null,
            out _
        ));
    }

    private static RecapMaintainerProfileDescriptor Descriptor(
        string profileName,
        string maintainerId,
        ContextHeaderBlockPath target,
        string recapBlockIdValue
    ) => new(
        profileName,
        "test-role",
        recapBlockIdValue,
        new RecapRewriteProfile(
            maintainerId,
            target,
            "system",
            "user"
        )
    );

    private static ContextHeaderBlockPath Target(
        ContextHeaderCarrier carrier,
        string blockKey
    ) => new(carrier, blockKey);
}

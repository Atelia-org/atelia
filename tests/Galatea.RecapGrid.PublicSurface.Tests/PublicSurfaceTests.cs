using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.RecapGrid.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public void ExternalOperatorCanResolveOnlyTheNarrowAssetCatalog() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV4,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);
        Assert.Equal(
            [typeof(GalateaRecapGridAssets)],
            typeof(GalateaRecapGridAssets).Assembly.GetExportedTypes()
        );
        Assert.Single(bundle.Families);
        Assert.Equal(2, bundle.Definitions.Count);
        Assert.Empty(bundle.Recipes);
    }
}

using System.Reflection;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.PublicSurface.Tests;

public sealed class StoreRestorePublicSurfaceTests {
    [Fact]
    public void RestoreIsTwoStepAndDoesNotExposeTestHooks() {
        MethodInfo[] methods = typeof(RecapGridStoreMaintenance).GetMethods(
            BindingFlags.Public | BindingFlags.Static);
        Assert.Contains(methods, static method =>
            method.Name == nameof(RecapGridStoreMaintenance.PrepareRestoreV4)
            && method.GetParameters().Length == 2);
        Assert.Contains(methods, static method =>
            method.Name == nameof(RecapGridStoreMaintenance.RestoreV4)
            && method.GetParameters().Length == 4);
        Assert.All(methods.Where(static method => method.Name.Contains(
                "RestoreV4", StringComparison.Ordinal)),
            static method => Assert.DoesNotContain(method.GetParameters(),
                parameter => parameter.ParameterType.Name.Contains(
                    "Hook", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            typeof(RecapGridStoreMaintenance).Assembly.GetExportedTypes(),
            static type => type.Name.Contains(
                "RestoreTestHook", StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreResultsCarryBothPhysicalConfirmationBoundaries() {
        Type prepared = typeof(RecapGridStorePrepareRestoreResult.Prepared);
        Assert.Equal(typeof(RecapGridStoreUpgradeEvidence),
            prepared.GetProperty("Active")!.PropertyType);
        Assert.Equal(typeof(RecapGridStoreUpgradeEvidence),
            prepared.GetProperty("Backup")!.PropertyType);
        Assert.Equal(typeof(RecapGridStorePhysicalWitness),
            typeof(RecapGridStoreRestoreResult.ActiveChanged)
                .GetProperty("Actual")!.PropertyType);
        Assert.Equal(typeof(RecapGridStorePhysicalWitness),
            typeof(RecapGridStoreRestoreResult.BackupChanged)
                .GetProperty("Actual")!.PropertyType);
    }
}

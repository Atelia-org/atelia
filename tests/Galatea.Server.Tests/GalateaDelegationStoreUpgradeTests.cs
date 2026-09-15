using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationStoreUpgradeTests {
    [Fact]
    public void RemovedUserFlagIsRejectedBeforeLoadingConfigOrOpeningAnyStore() {
        string absentConfig = Path.Combine(Path.GetTempPath(), "galatea-upgrade-unused-" + Guid.NewGuid().ToString("N"), "config.json");
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = GalateaDelegationStoreUpgrade.Run(
            ["operator", GalateaDelegationStoreUpgrade.CommandName, "--config", absentConfig, "--user", "resident", "--apply"],
            output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--character <characterId>", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--user", error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(absentConfig)));
    }
}

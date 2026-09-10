using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

[Trait("Category", "GalateaLab")]
public sealed class GalateaStartupIsolationTests {
    [Fact]
    public async Task LoginKeyRingUsesExplicitInstanceDirectory() {
        await using var lab = GalateaScenarioLab.Create("key-ring-isolation", new NoCalls());
        using HttpClient http = lab.Host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
        KeyManagementOptions keys = lab.Host.Factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = Assert.IsType<FileSystemXmlRepository>(keys.XmlRepository);
        Assert.StartsWith(lab.RootDirectory + Path.DirectorySeparatorChar, repository.Directory.FullName);
        Assert.NotEmpty(repository.Directory.GetFiles("*.xml"));
        await lab.CompleteAsync();
    }

    [Theory]
    [InlineData("relative/keys")]
    [InlineData(" ")]
    public async Task InvalidKeyRingDirectoryFailsStartup(string path) {
        await using var host = GalateaTestHost.Create(new NoCalls(), DisabledGalateaUserMessageNormalizer.Instance);
        await using var overridden = host.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Galatea:DataProtectionKeysDirectory", path));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => overridden.CreateClient());
        Assert.Contains("Galatea:DataProtectionKeysDirectory", error.Message);
    }

    private sealed class NoCalls : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) =>
            throw new InvalidOperationException("Startup test must not dispatch completion.");
    }
}

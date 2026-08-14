using System.Xml.Linq;
using Xunit;

namespace Atelia.Galatea.RecapGrid.Tests;

public sealed class GalateaRecapGridArchitectureTests {
    [Fact]
    public void ProductHasOnlyLockedDirectInputsAndNoAgentControlBinding() {
        string root = FindRepositoryRoot();
        string project = Path.Combine(
            root,
            "prototypes",
            "Galatea.RecapGrid",
            "Galatea.RecapGrid.csproj"
        );
        XDocument xml = XDocument.Load(project);
        Assert.Equal(
            [
                "../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj",
                "../SessionJournal.RecapGrid.Control/SessionJournal.RecapGrid.Control.csproj"
            ],
            xml.Descendants("ProjectReference")
                .Select(static value => value.Attribute("Include")!.Value)
        );
        Assert.Empty(xml.Descendants("PackageReference"));
        Assert.Equal(
            [
                "../../docs/Galatea/prompt/recap-maintainer-family/system-zh-cn.md",
                "../../docs/Galatea/prompt/world-understanding-maintainer/rewrite-zh-cn/user.md",
                "../../docs/Galatea/prompt/autobiographical-maintainer/rewrite-zh-cn/user.md"
            ],
            xml.Descendants("EmbeddedResource")
                .Select(static value => value.Attribute("Include")!.Value)
        );
        Assert.Equal(
            ["Atelia.Galatea.RecapGrid.Tests"],
            xml.Descendants("InternalsVisibleTo")
                .Select(static value => value.Attribute("Include")!.Value)
        );

        string[] references = typeof(GalateaRecapGridAssets).Assembly
            .GetReferencedAssemblies()
            .Select(static value => value.Name!)
            .ToArray();
        Assert.DoesNotContain(references, static value =>
            value.Contains("RecapGrid.Runtime", StringComparison.Ordinal)
            || value.Contains("RecapGrid.Manager", StringComparison.Ordinal)
            || value.Contains("RecapGrid.Store", StringComparison.Ordinal)
            || value.Contains("RecapGrid.AgentControl", StringComparison.Ordinal)
            || value.Contains("Completion", StringComparison.Ordinal)
            || value.Contains("SessionJournal.Cli", StringComparison.Ordinal)
            || value.Contains("Galatea.Server", StringComparison.Ordinal));

        string agentControl = File.ReadAllText(Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.AgentControl",
            "AgentControlContracts.cs"
        ));
        Assert.DoesNotContain(
            GalateaRecapGridAssets.RollingRewriteZhCnV1,
            agentControl,
            StringComparison.Ordinal
        );
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "Atelia.sln"))) {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

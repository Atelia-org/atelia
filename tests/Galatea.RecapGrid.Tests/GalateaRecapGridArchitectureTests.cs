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
                "../Galatea.Prompts/Galatea.Prompts.csproj",
                "../SessionJournal.RecapGrid/SessionJournal.RecapGrid.csproj"
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
        Assert.Contains(
            "Atelia.Galatea.Prompts",
            references,
            StringComparer.Ordinal
        );
        Assert.Contains(
            "Atelia.SessionJournal.RecapGrid",
            references,
            StringComparer.Ordinal
        );
        Assert.DoesNotContain(references, static value =>
            value.Contains("Completion", StringComparison.Ordinal)
            || value.Contains("SessionJournal.Cli", StringComparison.Ordinal)
            || value.Contains("Galatea.Server", StringComparison.Ordinal));

        string agentControl = File.ReadAllText(Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid",
            "AgentControl",
            "AgentControlContracts.cs"
        ));
        Assert.DoesNotContain(
            GalateaRecapGridAssets.RollingRewriteZhCnV6,
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

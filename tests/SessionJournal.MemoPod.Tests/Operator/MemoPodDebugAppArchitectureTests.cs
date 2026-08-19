using System.Xml.Linq;
using Atelia.SessionJournal.MemoPod.DebugApp;

namespace Atelia.SessionJournal.MemoPod.Tests.Operator;

public sealed class MemoPodDebugAppArchitectureTests {
    [Fact]
    public void DebugAppExportsNoTypes() {
        Assert.Empty(typeof(Program).Assembly.GetExportedTypes());
    }

    [Fact]
    public void DebugAppHasOnlyLockedDependenciesAndTestFriend() {
        string projectDirectory = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "SessionJournal.MemoPod.DebugApp"
        );
        XDocument project = XDocument.Load(Path.Combine(
            projectDirectory,
            "SessionJournal.MemoPod.DebugApp.csproj"
        ));

        Assert.Equal(
            new[] { "Completion.Abstractions", "SessionJournal.MemoPod" },
            project.Descendants("ProjectReference")
                .Select(static element => Path.GetFileNameWithoutExtension(
                    (string)element.Attribute("Include")!
                ))
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal(
            ["Atelia.SessionJournal.MemoPod.Tests"],
            project.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string)element.Attribute("Include")!)
                .ToArray()
        );
        Assert.Equal(
            "Exe",
            project.Descendants("OutputType").Single().Value
        );
        Assert.Equal(
            "net10.0",
            project.Descendants("TargetFramework").Single().Value
        );
        Assert.Equal(
            "enable",
            project.Descendants("Nullable").Single().Value
        );
        Assert.Equal(
            "enable",
            project.Descendants("ImplicitUsings").Single().Value
        );
        Assert.Equal(
            "true",
            project.Descendants("TreatWarningsAsErrors").Single().Value
        );

        Assert.Equal(
            new[] {
                "Atelia.Completion.Abstractions",
                "Atelia.SessionJournal.MemoPod"
            },
            typeof(Program).Assembly.GetReferencedAssemblies()
                .Where(static reference => reference.Name?.StartsWith(
                    "Atelia.",
                    StringComparison.Ordinal
                ) is true)
                .Select(static reference => reference.Name!)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
    }

    [Fact]
    public void DebugAppSourceHasNoLiveConnectionOrEnvironmentAccess() {
        string sourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "SessionJournal.MemoPod.DebugApp"
        );
        string source = string.Join(
            "\n",
            Directory.EnumerateFiles(sourceRoot, "*.cs")
                .Select(File.ReadAllText)
        );

        Assert.DoesNotContain("Environment.", source);
        Assert.DoesNotContain("GetEnvironmentVariable", source);
        Assert.DoesNotContain("DebugUtil", source);
        Assert.DoesNotContain("Connection", source);
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "Atelia.sln"))) {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate Atelia.sln from test output."
        );
    }
}

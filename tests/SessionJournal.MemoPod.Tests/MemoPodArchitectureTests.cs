using System.Reflection;
using System.Xml.Linq;
using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests;

public sealed class MemoPodArchitectureTests {
    [Fact]
    public void ProductExportsOnlyLockedLeafTypes() {
        string[] exported = typeof(Memo).Assembly.GetExportedTypes()
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] {
                typeof(Memo).FullName!,
                typeof(MemoId).FullName!,
                typeof(MemoPodId).FullName!,
                typeof(MemoPodLimits).FullName!
            }.Order(StringComparer.Ordinal),
            exported
        );
        Assert.DoesNotContain(
            exported,
            static name => name.EndsWith(".MemoPod", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void MemoHasNoPublicConstructionOrMutationSurface() {
        Assert.Empty(typeof(Memo).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Equal(
            ["ExactText", "Id"],
            typeof(Memo).GetProperties(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.All(
            typeof(Memo).GetProperties(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly),
            static property => Assert.False(property.CanWrite)
        );
        Assert.DoesNotContain(
            typeof(Memo).GetMethods(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly),
            static method => method.Name == "Deconstruct"
        );
    }

    [Fact]
    public void IdsCanOnlyBeCreatedFromStrictParsers() {
        Assert.Empty(typeof(MemoPodId).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
        Assert.Empty(typeof(MemoId).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance
        ));
    }

    [Fact]
    public void ProductProjectHasNoProductionDependenciesAndOneTestFriend() {
        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "SessionJournal.MemoPod",
            "SessionJournal.MemoPod.csproj"
        );
        XDocument project = XDocument.Load(projectPath);

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal(
            ["Atelia.SessionJournal.MemoPod.Tests"],
            project.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray()
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

        Assert.DoesNotContain(
            typeof(Memo).Assembly.GetReferencedAssemblies(),
            static reference => reference.Name?.StartsWith(
                "Atelia.",
                StringComparison.Ordinal
            ) is true
        );
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

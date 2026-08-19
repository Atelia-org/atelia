using System.Reflection;
using System.Xml.Linq;
using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests;

public sealed class MemoPodArchitectureTests {
    [Fact]
    public void ProductExportsOnlyLockedLifecycleTypes() {
        string[] exported = typeof(Memo).Assembly.GetExportedTypes()
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] {
                typeof(Memo).FullName!,
                typeof(MemoId).FullName!,
                typeof(MemoPod).FullName!,
                typeof(MemoPodCommitIndeterminateException).FullName!,
                typeof(MemoPodId).FullName!,
                typeof(MemoPodInvalidatedException).FullName!,
                typeof(MemoPodLimits).FullName!,
                typeof(MemoPodPersistenceException).FullName!,
                typeof(MemoPodPersistenceFailureKind).FullName!,
                typeof(MemoPodPhase).FullName!,
                typeof(MemoRecallException).FullName!,
                typeof(MemoRecallFailureKind).FullName!,
                typeof(MemoRecallOptions).FullName!,
                typeof(MemoRecallResult).FullName!
            }.Order(StringComparer.Ordinal),
            exported
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
    public void ProductDependenciesRemainWithinAllowlistAndFriendsAreExact() {
        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "SessionJournal.MemoPod",
            "SessionJournal.MemoPod.csproj"
        );
        XDocument project = XDocument.Load(projectPath);

        string[] projectReferences = project.Descendants("ProjectReference")
            .Select(static element => Path.GetFileNameWithoutExtension(
                (string?)element.Attribute("Include")
                    ?? throw new InvalidDataException(
                        "ProjectReference must have Include."
                    )
            ))
            .ToArray();
        Assert.Contains("Completion.Abstractions", projectReferences);
        Assert.All(
            projectReferences,
            static reference => Assert.Contains(
                reference,
                new[] { "Completion.Abstractions", "Diagnostics" }
            )
        );
        Assert.Equal(
            projectReferences.Length,
            projectReferences.Distinct(StringComparer.Ordinal).Count()
        );
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal(
            new[] {
                "Atelia.SessionJournal.MemoPod.CrashHarness",
                "Atelia.SessionJournal.MemoPod.Tests"
            },
            project.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .Order(StringComparer.Ordinal)
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

        string[] productAssemblyReferences = typeof(Memo).Assembly
            .GetReferencedAssemblies()
            .Where(static reference => reference.Name?.StartsWith(
                "Atelia.",
                StringComparison.Ordinal
            ) is true)
            .Select(static reference => reference.Name!)
            .ToArray();
        Assert.Contains(
            "Atelia.Completion.Abstractions",
            productAssemblyReferences
        );
        Assert.All(
            productAssemblyReferences,
            static reference => Assert.Contains(
                reference,
                new[] {
                    "Atelia.Completion.Abstractions",
                    "Atelia.Diagnostics"
                }
            )
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

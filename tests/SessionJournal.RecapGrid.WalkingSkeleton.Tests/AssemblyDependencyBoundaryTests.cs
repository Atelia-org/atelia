using System.Xml.Linq;
using System.Reflection;

namespace Atelia.SessionJournal.RecapGrid.WalkingSkeleton.Tests;

public sealed class AssemblyDependencyBoundaryTests {
    [Fact]
    public void ProductProjectsFollowTheLockedDirectDependencyGraph() {
        string root = FindRepositoryRoot();
        string timelineProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline",
            "SessionJournal.HistoryTimeline.csproj"
        );
        string abstractionsProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Abstractions",
            "SessionJournal.RecapGrid.Abstractions.csproj"
        );

        Assert.Equal(
            ["../SessionJournal/SessionJournal.csproj"],
            DirectProjectReferences(timelineProject)
        );
        Assert.Equal(
            ["../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"],
            DirectProjectReferences(abstractionsProject)
        );
        Assert.Empty(DirectPackageReferences(timelineProject));
        Assert.Empty(DirectPackageReferences(abstractionsProject));

        string combined = File.ReadAllText(timelineProject)
            + File.ReadAllText(abstractionsProject);
        Assert.DoesNotContain("../Completion/", combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Completion.Tools", combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DerivedRecap", combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Galatea", combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RecapGrid.Store", combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RecapGrid.Control", combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RecapGrid.Manager", combined,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WalkingSkeletonShapesStayOutsideProductAssemblies() {
        string root = FindRepositoryRoot();
        foreach (string relativeDirectory in new[] {
            "prototypes/SessionJournal.HistoryTimeline",
            "prototypes/SessionJournal.RecapGrid.Abstractions"
        }) {
            string directory = Path.Combine(
                root,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar)
            );
            Assert.DoesNotContain(Directory.EnumerateFiles(
                directory,
                "*.cs",
                SearchOption.AllDirectories
            ), static path => !IsBuildOutput(path));
        }
    }

    [Fact]
    public void ProjectReferenceClosureContainsNoConcreteRuntimeOrLegacyOwner() {
        string root = FindRepositoryRoot();
        string timelineProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline",
            "SessionJournal.HistoryTimeline.csproj"
        );
        string abstractionsProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Abstractions",
            "SessionJournal.RecapGrid.Abstractions.csproj"
        );
        HashSet<string> closure = ProjectClosure(
            timelineProject,
            abstractionsProject
        );

        Assert.DoesNotContain(closure, path => path.EndsWith(
            "/prototypes/Completion/Completion.csproj",
            StringComparison.OrdinalIgnoreCase
        ));
        Assert.DoesNotContain(closure, path => path.Contains(
            "/prototypes/Galatea/",
            StringComparison.OrdinalIgnoreCase
        ));
        Assert.DoesNotContain(closure, path => path.Contains(
            "/SessionJournal.DerivedRecap.",
            StringComparison.OrdinalIgnoreCase
        ));
    }

    [Fact]
    public void ProductAssembliesDoNotActuallyReferenceForbiddenOwners() {
        foreach (string assemblyFileName in new[] {
            "Atelia.SessionJournal.HistoryTimeline.dll",
            "Atelia.SessionJournal.RecapGrid.Abstractions.dll"
        }) {
            Assembly assembly = Assembly.LoadFrom(Path.Combine(
                AppContext.BaseDirectory,
                assemblyFileName
            ));
            string[] references = [.. assembly.GetReferencedAssemblies()
                .Select(static item => item.Name ?? string.Empty)];
            Assert.DoesNotContain(references, static name =>
                string.Equals(name, "Atelia.Completion", StringComparison.Ordinal)
                || string.Equals(
                    name,
                    "Atelia.Completion.Tools",
                    StringComparison.Ordinal
                )
                || name.Contains("Galatea", StringComparison.Ordinal)
                || name.Contains(
                    "SessionJournal.DerivedRecap",
                    StringComparison.Ordinal
                ));
        }
    }

    [Fact]
    public void RawSessionJournalDoesNotReferenceNewDerivedOwners() {
        string root = FindRepositoryRoot();
        string sessionJournalProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal",
            "SessionJournal.csproj"
        );
        Assert.DoesNotContain(
            DirectProjectReferences(sessionJournalProject),
            static reference => reference.Contains(
                "SessionJournal.HistoryTimeline",
                StringComparison.OrdinalIgnoreCase
            ) || reference.Contains(
                "SessionJournal.RecapGrid",
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    private static string[] DirectProjectReferences(string projectPath) {
        XDocument document = XDocument.Load(projectPath);
        return [.. document
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .Select(static value => value!.Replace('\\', '/'))];
    }

    private static string[] DirectPackageReferences(string projectPath) {
        XDocument document = XDocument.Load(projectPath);
        return [.. document
            .Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static value => value is not null)
            .Select(static value => value!)];
    }

    private static bool IsBuildOutput(string path) {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ProjectClosure(params string[] roots) {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots.Select(Path.GetFullPath));
        while (pending.TryPop(out string? projectPath)) {
            if (!visited.Add(projectPath.Replace('\\', '/'))) {
                continue;
            }
            string directory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException(
                    "A project path has no parent directory."
                );
            foreach (string reference in DirectProjectReferences(projectPath)) {
                pending.Push(Path.GetFullPath(reference, directory));
            }
        }
        return visited;
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
            "Could not locate the repository root from the test output."
        );
    }
}

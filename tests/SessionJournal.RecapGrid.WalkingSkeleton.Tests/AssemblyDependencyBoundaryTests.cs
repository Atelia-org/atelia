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
        string controlProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Control",
            "SessionJournal.RecapGrid.Control.csproj"
        );

        Assert.Equal(
            ["../SessionJournal/SessionJournal.csproj"],
            DirectProjectReferences(timelineProject)
        );
        Assert.Equal(
            ["../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"],
            DirectProjectReferences(abstractionsProject)
        );
        Assert.Equal(
            [
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj"
            ],
            DirectProjectReferences(controlProject)
        );
        Assert.Equal(
            [
                "Microsoft.Data.Sqlite@10.0.10",
                "SQLitePCLRaw.bundle_e_sqlite3@2.1.12",
                "Microsoft.Bcl.Memory@9.0.17",
                "Microsoft.ML.Tokenizers@2.0.0",
                "Microsoft.ML.Tokenizers.Data.O200kBase@2.0.0"
            ],
            DirectPackageReferences(timelineProject)
        );
        Assert.Empty(DirectPackageReferences(abstractionsProject));
        Assert.Empty(DirectPackageReferences(controlProject));

        string upstream = File.ReadAllText(timelineProject)
            + File.ReadAllText(abstractionsProject);
        string combined = upstream + File.ReadAllText(controlProject);
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
        Assert.DoesNotContain("RecapGrid.Control", upstream,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RecapGrid.Manager", combined,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqliteIsTheOnlyProductionTimelineBackendAndHasOneDirectPin() {
        string root = FindRepositoryRoot();
        string timelineProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline",
            "SessionJournal.HistoryTimeline.csproj"
        );
        string[] newProjects = [.. new[] {
            Path.Combine(root, "prototypes"),
            Path.Combine(root, "tests")
        }.SelectMany(static parent => Directory.EnumerateDirectories(
            parent,
            "SessionJournal.*",
            SearchOption.TopDirectoryOnly
        )).Where(static directory => directory.Contains(
            "SessionJournal.HistoryTimeline",
            StringComparison.Ordinal
        ) || directory.Contains(
            "SessionJournal.RecapGrid",
            StringComparison.Ordinal
        )).SelectMany(static directory => Directory.EnumerateFiles(
            directory,
            "*.csproj",
            SearchOption.TopDirectoryOnly
        ))];
        string sqliteOwner = Assert.Single(newProjects, project =>
            DirectPackageReferences(project).Contains(
                "Microsoft.Data.Sqlite@10.0.10",
                StringComparer.Ordinal
            )
        );
        Assert.Equal(
            Path.GetFullPath(timelineProject),
            Path.GetFullPath(sqliteOwner)
        );
        string bundleOwner = Assert.Single(newProjects, project =>
            DirectPackageReferences(project).Contains(
                "SQLitePCLRaw.bundle_e_sqlite3@2.1.12",
                StringComparer.Ordinal
            )
        );
        Assert.Equal(
            Path.GetFullPath(timelineProject),
            Path.GetFullPath(bundleOwner)
        );

        Assembly product = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.HistoryTimeline.dll"
        ));
        Assert.Null(product.GetType(
            "Atelia.SessionJournal.HistoryTimeline.InMemoryHistoryTimelineLedger",
            throwOnError: false
        ));
        Assert.DoesNotContain(
            product.GetExportedTypes(),
            static type => type.Name.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase
            ) || string.Equals(
                type.Name,
                "IHistoryTimelineLedgerPort",
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    [Fact]
    public void WalkingSkeletonHasNoPrivateTimelineIdentityOwner() {
        string root = FindRepositoryRoot();
        string skeleton = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "SessionJournal.RecapGrid.WalkingSkeleton.Tests",
            "GridWalkingSkeletonTests.cs"
        ));
        foreach (string forbidden in new[] {
            "HistorySegmentDescriptorShape",
            "record TimelineId",
            "record HistoryRowId",
            "history-segment-v1",
            "history-timeline.row-id",
            "history-timeline.descriptor"
        }) {
            Assert.DoesNotContain(
                forbidden,
                skeleton,
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public void WalkingSkeletonHasNoPrivateGridShapeOrHasherOwner() {
        string root = FindRepositoryRoot();
        string skeleton = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "SessionJournal.RecapGrid.WalkingSkeleton.Tests",
            "GridWalkingSkeletonTests.cs"
        ));
        foreach (string forbidden in new[] {
            "record DefinitionShape",
            "record RecipeShape",
            "record RowBuildSpecShape",
            "IncrementalHash",
            "SHA256.HashData"
        }) {
            Assert.DoesNotContain(
                forbidden,
                skeleton,
                StringComparison.Ordinal
            );
        }
        string solution = File.ReadAllText(Path.Combine(root, "Atelia.sln"));
        Assert.Contains(
            "SessionJournal.RecapGrid.Abstractions.Tests.csproj",
            solution,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void HistoryLoadDeclarationsHaveOneProductionOwner() {
        string root = FindRepositoryRoot();
        string[] sourceFiles = [.. Directory.EnumerateFiles(
            Path.Combine(root, "prototypes"),
            "*.cs",
            SearchOption.AllDirectories
        ).Select(static path => path.Replace('\\', '/'))
            .Where(static path => !IsBuildOutput(path))];
        foreach (string declaration in new[] {
            "public readonly record struct HistoryLoadUnit",
            "public interface IHistoryUnitLoadEstimator",
            "public sealed class O200kBaseHistoryUnitLoadEstimator",
            "public static class HistoryLoadProjector",
            "public const string EstimatorId"
        }) {
            string owner = Assert.Single(sourceFiles, path =>
                File.ReadAllText(path).Contains(
                    declaration,
                    StringComparison.Ordinal
                )
            );
            Assert.Contains(
                "/prototypes/SessionJournal.HistoryTimeline/",
                owner,
                StringComparison.Ordinal
            );
        }
        foreach (string legacyDeclaration in new[] {
            "record RecapHistoryLoadMeasurement",
            "record RecapHistoryLoadBoundary",
            "class RecapHistoryLoadProjector",
            "record RecapHistoryLoadBaseline",
            "class RecapHistoryLoadBaselineResolver"
        }) {
            Assert.DoesNotContain(sourceFiles, path =>
                File.ReadAllText(path).Contains(
                    legacyDeclaration,
                    StringComparison.Ordinal
                )
            );
        }
        const string o200kIdentity =
            "atelia.history-load.o200k-base.history-unit-v1";
        string o200kOwner = Assert.Single(sourceFiles, path =>
            File.ReadAllText(path).Contains(
                o200kIdentity,
                StringComparison.Ordinal
            )
        );
        Assert.EndsWith(
            "/prototypes/SessionJournal.HistoryTimeline/O200kBaseHistoryUnitLoadEstimator.cs",
            o200kOwner,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(sourceFiles, path =>
            path.EndsWith(
                "/SessionJournal.DerivedRecap.Planner/HistoryLoadContracts.cs",
                StringComparison.Ordinal
            )
            || path.EndsWith(
                "/SessionJournal.DerivedRecap.Planner/O200kBaseHistoryUnitLoadEstimator.cs",
                StringComparison.Ordinal
            )
            || path.EndsWith(
                "/SessionJournal.DerivedRecap.Planner/RecapHistoryLoadProjector.cs",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void LegacyConsumersReferenceTimelineDirectlyWithoutTokenizerPins() {
        string root = FindRepositoryRoot();
        string plannerProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.DerivedRecap.Planner",
            "SessionJournal.DerivedRecap.Planner.csproj"
        );
        Assert.Empty(DirectPackageReferences(plannerProject));

        foreach ((string project, string expectedReference) in new[] {
            (
                plannerProject,
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"
            ),
            (
                Path.Combine(
                    root,
                    "prototypes",
                    "SessionJournal.Cli",
                    "SessionJournal.Cli.csproj"
                ),
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"
            ),
            (
                Path.Combine(
                    root,
                    "prototypes",
                    "Galatea",
                    "Galatea.Server.csproj"
                ),
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"
            )
        }) {
            Assert.Contains(
                expectedReference,
                DirectProjectReferences(project),
                StringComparer.Ordinal
            );
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
        string controlProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Control",
            "SessionJournal.RecapGrid.Control.csproj"
        );
        HashSet<string> closure = ProjectClosure(
            timelineProject,
            abstractionsProject,
            controlProject
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
            "Atelia.SessionJournal.RecapGrid.Abstractions.dll",
            "Atelia.SessionJournal.RecapGrid.Control.dll"
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
            .Select(element => new {
                Include = (string?)element.Attribute("Include"),
                Version = (string?)element.Attribute("Version")
            })
            .Where(static value => value.Include is not null)
            .Select(static value =>
                $"{value.Include}@{value.Version}")];
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

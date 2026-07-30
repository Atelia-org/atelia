using System.Xml.Linq;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class RecapCutoverArchitectureBoundaryTests {
    private static readonly string[] RetiredCommandNames = [
        "run-memory-maintainer",
        "run-derived-memory-orchestration",
        "publish-derived-artifact-set",
        "list-derived-artifact-sets",
        "validate-derived-memory",
        "rebuild-derived-artifact-set-latest",
        "configure-derived-artifact-planner",
        "plan-derived-artifact-epoch",
        "list-derived-artifact-epochs"
    ];

    [Fact]
    public void RetiredDerivedMemorySurface_IsAbsent() {
        string repoRoot = FindRepositoryRoot();
        string retiredProductDirectory = Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.DerivedMemory"
        );
        string retiredTestDirectory = Path.Combine(
            repoRoot,
            "tests",
            "SessionJournal.DerivedMemory.Tests"
        );

        Assert.False(Directory.Exists(retiredProductDirectory));
        Assert.False(Directory.Exists(retiredTestDirectory));
        Assert.False(File.Exists(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "DerivedMemoryCommands.cs"
        )));
        Assert.False(File.Exists(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "MemoryMaintainerRun.cs"
        )));
        Assert.False(File.Exists(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "MemoryMaintainerRunUtils.cs"
        )));

        string program = File.ReadAllText(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "Program.cs"
        ));
        foreach (string command in RetiredCommandNames) {
            Assert.DoesNotContain(
                command,
                program,
                StringComparison.Ordinal
            );
        }

        string solution = File.ReadAllText(Path.Combine(
            repoRoot,
            "Atelia.sln"
        ));
        Assert.DoesNotContain(
            "SessionJournal.DerivedMemory",
            solution,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "D3822044-41C9-47B0-8245-D4110714D7E4",
            solution,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "0B73B345-A8F2-4515-BC29-3D6BDE905C19",
            solution,
            StringComparison.OrdinalIgnoreCase
        );

        foreach (
            string projectFile in Directory.EnumerateFiles(
                repoRoot,
                "*.csproj",
                SearchOption.AllDirectories
            )
        ) {
            Assert.DoesNotContain(
                "SessionJournal.DerivedMemory",
                File.ReadAllText(projectFile),
                StringComparison.Ordinal
            );
        }
        string assemblyInfo = File.ReadAllText(Path.Combine(
            repoRoot,
            "prototypes",
            "SessionJournal",
            "Properties",
            "AssemblyInfo.cs"
        ));
        Assert.DoesNotContain(
            "Atelia.SessionJournal.DerivedMemory.Tests",
            assemblyInfo,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ProjectReferences_PreserveOneWayCompositionBoundaries() {
        string repoRoot = FindRepositoryRoot();

        Assert.Equal(
            [
                "../Completion.Abstractions/Completion.Abstractions.csproj",
                "../SessionJournal/SessionJournal.csproj"
            ],
            ReadProjectReferences(
                repoRoot,
                "prototypes",
                "SessionJournal.DerivedRecap.Maintainers",
                "SessionJournal.DerivedRecap.Maintainers.csproj"
            )
        );
        Assert.Equal(
            ["../ChatSession/ChatSession.csproj"],
            ReadProjectReferences(
                repoRoot,
                "prototypes",
                "ChatSession.LegacyExportCli",
                "ChatSession.LegacyExportCli.csproj"
            )
        );

        string[] cliReferences = ReadProjectReferences(
            repoRoot,
            "prototypes",
            "SessionJournal.Cli",
            "SessionJournal.Cli.csproj"
        );
        Assert.Contains(
            "../SessionJournal.DerivedRecap.Maintainers/SessionJournal.DerivedRecap.Maintainers.csproj",
            cliReferences
        );
        Assert.Contains(
            "../SessionJournal.DerivedRecap.Planner/SessionJournal.DerivedRecap.Planner.csproj",
            cliReferences
        );
        Assert.Contains(
            "../SessionJournal.DerivedRecap.Store/SessionJournal.DerivedRecap.Store.csproj",
            cliReferences
        );

        string[] rawJournalReferences = ReadProjectReferences(
            repoRoot,
            "prototypes",
            "SessionJournal",
            "SessionJournal.csproj"
        );
        Assert.DoesNotContain(
            rawJournalReferences,
            reference => reference.Contains(
                "SessionJournal.DerivedRecap",
                StringComparison.Ordinal
            )
        );
    }

    private static string[] ReadProjectReferences(
        string repoRoot,
        params string[] relativePath
    ) {
        XDocument project = XDocument.Load(
            Path.Combine([repoRoot, .. relativePath])
        );
        return [
            .. project.Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(static value => value is not null)
                .Select(static value => value!.Replace('\\', '/'))
        ];
    }

    private static string FindRepositoryRoot() {
        for (
            DirectoryInfo? cursor =
                new DirectoryInfo(AppContext.BaseDirectory);
            cursor is not null;
            cursor = cursor.Parent
        ) {
            if (File.Exists(Path.Combine(
                    cursor.FullName,
                    "Atelia.sln"
                ))) {
                return cursor.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Atelia repository root from the test assembly path."
        );
    }
}

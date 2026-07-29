using System.Xml.Linq;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class ArchitectureBoundaryTests {
    [Fact]
    public void ProjectReferences_PreserveOneWaySubsystemBoundaries() {
        string root = FindRepositoryRoot();

        Assert.Equal(
            ["../SessionJournal/SessionJournal.csproj"],
            ReadProjectReferences(
                root,
                "prototypes/SessionJournal.DerivedMemory/"
                    + "SessionJournal.DerivedMemory.csproj"
            )
        );
        Assert.Equal(
            [
                "../Completion.Abstractions/Completion.Abstractions.csproj",
                "../SessionJournal/SessionJournal.csproj"
            ],
            ReadProjectReferences(
                root,
                "prototypes/SessionJournal.DerivedRecap.Maintainers/"
                    + "SessionJournal.DerivedRecap.Maintainers.csproj"
            )
        );
        Assert.Equal(
            ["../ChatSession/ChatSession.csproj"],
            ReadProjectReferences(
                root,
                "prototypes/ChatSession.LegacyExportCli/"
                    + "ChatSession.LegacyExportCli.csproj"
            )
        );

        string coreProject = File.ReadAllText(Path.Combine(
            root,
            "prototypes/SessionJournal/SessionJournal.csproj"
        ));
        Assert.DoesNotContain(
            "SessionJournal.DerivedMemory",
            coreProject,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "SessionJournal.DerivedRecap.Maintainers",
            coreProject,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "Agent.Core",
            coreProject,
            StringComparison.Ordinal
        );

        string coreTestsProject = File.ReadAllText(Path.Combine(
            root,
            "tests/SessionJournal.Tests/SessionJournal.Tests.csproj"
        ));
        Assert.DoesNotContain(
            "SessionJournal.DerivedMemory",
            coreTestsProject,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ConcreteProvider_DoesNotOpenRawSessionJournal() {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "prototypes/SessionJournal.DerivedMemory/"
                + "DerivedArtifactSetContextCandidateSource.cs"
        ));

        Assert.DoesNotContain(
            "SessionJournalEngine",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "EventJournal.",
            source,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "OpenExisting",
            source,
            StringComparison.Ordinal
        );
    }

    private static string[] ReadProjectReferences(
        string repositoryRoot,
        string relativeProjectPath
    ) => [
        .. XDocument.Load(
                Path.Combine(repositoryRoot, relativeProjectPath)
            )
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(static value => value is not null)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
    ];

    private static string FindRepositoryRoot() {
        for (DirectoryInfo? cursor =
                 new DirectoryInfo(AppContext.BaseDirectory);
             cursor is not null;
             cursor = cursor.Parent) {
            if (File.Exists(Path.Combine(
                    cursor.FullName,
                    "Atelia.sln"
                ))) {
                return cursor.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Atelia repository root."
        );
    }
}

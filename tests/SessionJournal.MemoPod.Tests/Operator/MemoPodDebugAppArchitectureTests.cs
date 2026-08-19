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
            new[] {
                "Completion",
                "Completion.Abstractions",
                "SessionJournal.MemoPod"
            },
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
                "Atelia.Completion",
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
    public void LiveOwnedSourceContainsConcreteCompositionAndNoLoggingSink() {
        string sourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "SessionJournal.MemoPod.DebugApp"
        );
        Dictionary<string, string> sources = Directory
            .EnumerateFiles(sourceRoot, "*.cs")
            .ToDictionary(
                static path => Path.GetFileName(path)
                    ?? throw new InvalidDataException(
                        "DebugApp source path has no file name."
                    ),
                File.ReadAllText,
                StringComparer.Ordinal
            );
        const string liveOwnedFile = "LiveMemoRecall.cs";
        Assert.True(sources.ContainsKey(liveOwnedFile));

        foreach ((string fileName, string source) in sources) {
            Assert.DoesNotContain("LoggingCompletionClient", source);
            Assert.DoesNotContain("ICompletionHttpExchangeSink", source);
            Assert.DoesNotContain("DebugHttpExchangeSink", source);
            Assert.DoesNotContain("DebugUtil", source);
            if (string.Equals(
                    fileName,
                    liveOwnedFile,
                    StringComparison.Ordinal
                )) {
                continue;
            }

            Assert.DoesNotContain("Environment.", source);
            Assert.DoesNotContain("GetEnvironmentVariable", source);
            Assert.DoesNotContain("CompletionConnectionRegistry", source);
            Assert.DoesNotContain("CompletionConnectionConfigLoader", source);
            Assert.DoesNotContain("DefaultCompletionClientFactory", source);
        }

        string liveSource = sources[liveOwnedFile];
        Assert.Contains("Environment.GetEnvironmentVariable", liveSource);
        Assert.Contains("CompletionConnectionRegistry", liveSource);
        Assert.Contains("CompletionConnectionConfigLoader", liveSource);
        Assert.Contains("DefaultCompletionClientFactory", liveSource);
        Assert.Contains("#if DEBUG", liveSource);
        Assert.Contains("ATELIA_DEBUG_FILE_LEVEL", liveSource);
        Assert.Contains("ATELIA_DEBUG_CONSOLE_LEVEL", liveSource);
        Assert.Contains("\"ERROR\"", liveSource);
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

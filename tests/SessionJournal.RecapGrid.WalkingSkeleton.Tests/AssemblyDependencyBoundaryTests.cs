using System.Xml.Linq;
using System.Reflection;
using Atelia.SessionJournal.HistoryTimeline;

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
        string storeProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Store",
            "SessionJournal.RecapGrid.Store.csproj"
        );
        string managerProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Manager",
            "SessionJournal.RecapGrid.Manager.csproj"
        );
        string getterProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Getter",
            "SessionJournal.RecapGrid.Getter.csproj"
        );
        string runtimeProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Runtime",
            "SessionJournal.RecapGrid.Runtime.csproj"
        );
        string hostingProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Hosting",
            "SessionJournal.RecapGrid.Hosting.csproj"
        );
        string agentControlProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.AgentControl",
            "SessionJournal.RecapGrid.AgentControl.csproj"
        );
        string onlineProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Online",
            "SessionJournal.RecapGrid.Online.csproj"
        );
        string cadenceProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Cadence",
            "SessionJournal.RecapGrid.Cadence.csproj"
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
            ["../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj"],
            DirectProjectReferences(storeProject)
        );
        Assert.Equal(
            [
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj",
                "../SessionJournal.RecapGrid.Control/SessionJournal.RecapGrid.Control.csproj",
                "../SessionJournal.RecapGrid.Store/SessionJournal.RecapGrid.Store.csproj"
            ],
            DirectProjectReferences(managerProject)
        );
        Assert.Equal(
            [
                "../SessionJournal/SessionJournal.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj",
                "../SessionJournal.RecapGrid.Cadence/SessionJournal.RecapGrid.Cadence.csproj",
                "../SessionJournal.RecapGrid.Control/SessionJournal.RecapGrid.Control.csproj",
                "../SessionJournal.RecapGrid.Store/SessionJournal.RecapGrid.Store.csproj"
            ],
            DirectProjectReferences(getterProject)
        );
        Assert.Equal(
            [
                "../SessionJournal.RecapGrid.Manager/SessionJournal.RecapGrid.Manager.csproj",
                "../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj",
                "../Completion.Abstractions/Completion.Abstractions.csproj"
            ],
            DirectProjectReferences(runtimeProject)
        );
        Assert.Equal(
            [
                "../SessionJournal.RecapGrid.Runtime/SessionJournal.RecapGrid.Runtime.csproj",
                "../SessionJournal.RecapGrid.AgentControl/SessionJournal.RecapGrid.AgentControl.csproj",
                "../Completion/Completion.csproj"
            ],
            DirectProjectReferences(hostingProject)
        );
        Assert.Equal(
            [
                "../Completion.Tools/Completion.Tools.csproj",
                "../SessionJournal/SessionJournal.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../SessionJournal.RecapGrid.Abstractions/SessionJournal.RecapGrid.Abstractions.csproj",
                "../SessionJournal.RecapGrid.Control/SessionJournal.RecapGrid.Control.csproj",
                "../SessionJournal.RecapGrid.Manager/SessionJournal.RecapGrid.Manager.csproj"
            ],
            DirectProjectReferences(agentControlProject)
        );
        Assert.Equal(
            [
                "../SessionJournal/SessionJournal.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../SessionJournal.RecapGrid.Cadence/SessionJournal.RecapGrid.Cadence.csproj",
                "../SessionJournal.RecapGrid.Manager/SessionJournal.RecapGrid.Manager.csproj",
                "../SessionJournal.RecapGrid.Getter/SessionJournal.RecapGrid.Getter.csproj"
            ],
            DirectProjectReferences(onlineProject)
        );
        Assert.Equal(
            [
                "../../src/EventJournal/EventJournal.csproj",
                "../SessionJournal/SessionJournal.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"
            ],
            DirectProjectReferences(cadenceProject)
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
        Assert.Equal(
            [
                "Microsoft.Data.Sqlite@10.0.10",
                "SQLitePCLRaw.bundle_e_sqlite3@2.1.12"
            ],
            DirectPackageReferences(storeProject)
        );
        Assert.Empty(DirectPackageReferences(managerProject));
        Assert.Empty(DirectPackageReferences(getterProject));
        Assert.Empty(DirectPackageReferences(runtimeProject));
        Assert.Empty(DirectPackageReferences(hostingProject));
        Assert.Empty(DirectPackageReferences(agentControlProject));
        Assert.Empty(DirectPackageReferences(onlineProject));
        Assert.Empty(DirectPackageReferences(cadenceProject));

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
    public void CadenceOwnsOnlyBoundedProviderNeutralPolicyDurability() {
        string root = FindRepositoryRoot();
        string cadenceRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Cadence"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                cadenceRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "Microsoft.Data.Sqlite",
                     "SQLitePCLRaw",
                     "RecapGrid.Control",
                     "RecapGrid.Store",
                     "RecapGrid.Manager",
                     "CompletionConnectionRegistry",
                     "Galatea",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        XDocument project = XDocument.Load(Path.Combine(
            cadenceRoot,
            "SessionJournal.RecapGrid.Cadence.csproj"
        ));
        Assert.Equal(
            ["Atelia.SessionJournal.RecapGrid.Cadence.Tests"],
            project.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray()
        );

        Assert.Equal(
            2,
            product.Split(
                "ExecuteDerivedSidecarMutation",
                StringSplitOptions.None).Length - 1);
        foreach (string forbiddenMutationSurface in new[] {
                     ".SendAsync(",
                     ".ResumeAsync(",
                     ".Append(",
                     "AppendEvent",
                     "ReadPayloadBytes"
                 }) {
            Assert.DoesNotContain(
                forbiddenMutationSurface,
                product,
                StringComparison.Ordinal);
        }

        string[] sessionJournalProductionFriends = [.. File.ReadLines(
                Path.Combine(
                    root,
                    "prototypes",
                    "SessionJournal",
                    "Properties",
                    "AssemblyInfo.cs"))
            .Where(static line => line.Contains(
                "InternalsVisibleTo",
                StringComparison.Ordinal))
            .Where(static line => !line.Contains(
                ".Tests\")",
                StringComparison.Ordinal))];
        Assert.Equal(
            ["[assembly: InternalsVisibleTo(\"Atelia.SessionJournal.RecapGrid.Cadence\")]"],
            sessionJournalProductionFriends);

        string timelineRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline");
        string timelineCoordinator = File.ReadAllText(Path.Combine(
            timelineRoot,
            "HistoryTimelineCoordinator.cs"));
        foreach (string mutation in new[] {
                     "PlanNextRow",
                     "CommitRow",
                     "OpenOfflineBuilder"
                 }) {
            Assert.DoesNotContain(
                typeof(HistoryTimelineCoordinator).GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly),
                method => string.Equals(
                    method.Name,
                    mutation,
                    StringComparison.Ordinal));
        }
        Assert.DoesNotContain(
            typeof(Atelia.SessionJournal.RecapGrid.Cadence
                .RecapGridCadenceTimelineSealOperation).GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly),
            static method => method.Name == "OpenOfflineBuilder"
                && method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(
                        Atelia.SessionJournal
                            .SessionSelectedLineageForwardCursor)));
        Assert.Contains("CreateNoReservePolicyForTests", timelineCoordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CreateNoReservePolicyForTests",
            product,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CreateForTest",
            product,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PlanNextRowForTests",
            product,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenOfflineBuilderForTests",
            product,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HistoryRecentReserveProof",
            product,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"new\s+HistoryRowCommitCandidate\s*\(",
            product);
        Assert.Contains(".PlanNextRow(", product, StringComparison.Ordinal);
        Assert.Contains(".CommitRow(", product, StringComparison.Ordinal);
        Assert.Contains(".OpenOfflineBuilder(", product,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentControlOwnsOnlyProviderNeutralBoundedToolMutation() {
        string root = FindRepositoryRoot();
        string agentControlRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.AgentControl"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                agentControlRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "Microsoft.Data.Sqlite",
                     "SQLitePCLRaw",
                     "Atelia.Completion.OpenAI",
                     "Atelia.Completion.Anthropic",
                     "Atelia.Completion.Gemini",
                     "CompletionConnectionRegistry",
                     "Galatea",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        Assembly productAssembly = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.AgentControl.dll"
        ));
        Assert.DoesNotContain(
            productAssembly.GetExportedTypes(),
            static type => type.Name.Contains(
                "Backend",
                StringComparison.OrdinalIgnoreCase
            )
        );
        XDocument project = XDocument.Load(Path.Combine(
            agentControlRoot,
            "SessionJournal.RecapGrid.AgentControl.csproj"
        ));
        Assert.Equal(
            ["Atelia.SessionJournal.RecapGrid.AgentControl.Tests"],
            project.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray()
        );
    }

    [Fact]
    public void RecapGridOnlineOwnsOnlyBoundedProviderNeutralComposition() {
        string root = FindRepositoryRoot();
        string onlineRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Online"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                onlineRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "IHistoryTimelineLedgerPort",
                     "SqliteHistoryTimelineLedger",
                     "Microsoft.Data.Sqlite",
                     "CompletionConnectionRegistry",
                     "Atelia.Completion",
                     "Galatea",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        Assembly online = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.Online.dll"
        ));
        Assert.DoesNotContain(online.GetExportedTypes(), static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Ledger", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Coordinator", StringComparison.OrdinalIgnoreCase)
        );
        XDocument onlineProject = XDocument.Load(Path.Combine(
            onlineRoot,
            "SessionJournal.RecapGrid.Online.csproj"));
        Assert.Equal(
            ["Atelia.SessionJournal.RecapGrid.Online.Tests"],
            onlineProject.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray());
    }

    [Fact]
    public void GalateaFormalCompositionIsInternalAndOwnedByHostService() {
        string root = FindRepositoryRoot();
        string compositionPath = Path.Combine(
            root,
            "prototypes",
            "Galatea",
            "GalateaRecapGridComposition.cs"
        );
        string host = File.ReadAllText(Path.Combine(
            root, "prototypes", "Galatea", "GalateaServices.cs"));
        string composition = File.ReadAllText(compositionPath);

        Assert.Contains(
            "internal sealed class GalateaRecapGridComposition",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "new GalateaRecapGridComposition",
            host,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Environment.GetEnvironmentVariable",
            composition,
            StringComparison.Ordinal);

        XDocument galateaProject = XDocument.Load(Path.Combine(
            root, "prototypes", "Galatea", "Galatea.Server.csproj"));
        Assert.Equal(
            ["Atelia.Galatea.Server.Tests"],
            galateaProject.Descendants("AssemblyAttribute")
                .Where(static element => string.Equals(
                    (string?)element.Attribute("Include"),
                    "System.Runtime.CompilerServices.InternalsVisibleTo",
                    StringComparison.Ordinal))
                .SelectMany(static element =>
                    element.Elements("_Parameter1"))
                .Select(static element => element.Value)
                .ToArray());

        string[] cliFriends = [.. File.ReadLines(Path.Combine(
                root,
                "prototypes",
                "SessionJournal.Cli",
                "Properties",
                "AssemblyInfo.cs"))
            .Where(static line => line.Contains(
                "InternalsVisibleTo",
                StringComparison.Ordinal))];
        Assert.Equal(
            [
                "[assembly: InternalsVisibleTo(\"Atelia.SessionJournal.Cli.Tests\")]",
                "[assembly: InternalsVisibleTo(\"Atelia.SessionJournal.Cli.LegacyRoot.CrashHarness\")]",
                "[assembly: InternalsVisibleTo(\"Atelia.Galatea.Server.Tests\")]"
            ],
            cliFriends);
    }

    [Fact]
    public void RecapGridHostingOwnsOnlyExactCompositionAndNoScheduler() {
        string root = FindRepositoryRoot();
        string hostingRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Hosting"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                hostingRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "Microsoft.Data.Sqlite",
                     "HistoryTimelineCoordinator",
                     "RecapGridControlCoordinator",
                     "RecapGridStoreWriter",
                     "Registry.Resolve",
                     "CompletionConnectionConfigLoader.LoadFile",
                     "Galatea",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        Assembly hosting = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.Hosting.dll"
        ));
        Assert.DoesNotContain(hosting.GetExportedTypes(), static type =>
            type.Name.Contains("Scheduler", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void FormalCliDelegatesToGridOwnersWithoutLegacyOrProviderAlgorithms() {
        string root = FindRepositoryRoot();
        string recapGridSource = string.Join(
            "\n",
            Directory.EnumerateFiles(
                Path.Combine(root, "prototypes", "SessionJournal.Cli"),
                "RecapGrid*.cs",
                SearchOption.TopDirectoryOnly
            ).Order(StringComparer.Ordinal)
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "SessionJournal.DerivedRecap",
                     "Galatea",
                     "Microsoft.Data.Sqlite",
                     "SQLitePCLRaw",
                     "CompletionConnectionRegistry.Resolve",
                     "DefaultConnectionId",
                     "DerivedRecapRebuildSpool",
                     "DerivedRecapEpoch",
                     "RowBuildSpec.Create",
                     "RecapGridStoreWriter"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                recapGridSource,
                StringComparison.Ordinal
            );
        }
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(root, "prototypes", "SessionJournal.Cli"),
            "RecapGridCandidate*.cs",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void RecapGridRuntimeUsesOnlyProviderNeutralBoundedContracts() {
        string root = FindRepositoryRoot();
        string runtimeRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Runtime"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                runtimeRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Select(static path => path.Replace('\\', '/'))
                .Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "Microsoft.Data.Sqlite",
                     "SQLitePCLRaw",
                     "Atelia.Completion.OpenAI",
                     "Atelia.Completion.Anthropic",
                     "Atelia.Completion.Gemini",
                     "Completion.Tools",
                     "HistoryTimelineCoordinator",
                     "RecapGridControlCoordinator",
                     "RecapGridStoreWriter",
                     "Galatea",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        Assembly runtime = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.Runtime.dll"
        ));
        Assert.DoesNotContain(runtime.GetExportedTypes(), static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Coordinator", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void RuntimeWhiteBoxFriendAccessIsTestOnlyAndExact() {
        string root = FindRepositoryRoot();
        foreach (string project in new[] {
                     Path.Combine(
                         root,
                         "prototypes",
                         "SessionJournal.HistoryTimeline",
                         "SessionJournal.HistoryTimeline.csproj"
                     ),
                     Path.Combine(
                         root,
                         "prototypes",
                         "SessionJournal.RecapGrid.Manager",
                         "SessionJournal.RecapGrid.Manager.csproj"
                     )
                 }) {
            XDocument document = XDocument.Load(project);
            string[] friends = [.. document
                .Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)];
            Assert.Contains(
                "Atelia.SessionJournal.RecapGrid.Runtime.Tests",
                friends,
                StringComparer.Ordinal
            );
            Assert.Contains(
                "Atelia.SessionJournal.RecapGrid.Hosting.Tests",
                friends,
                StringComparer.Ordinal
            );
            Assert.DoesNotContain(
                "Atelia.SessionJournal.RecapGrid.Runtime",
                friends,
                StringComparer.Ordinal
            );
        }
        string[] completionFriends = [.. File.ReadLines(Path.Combine(
                root,
                "prototypes",
                "Completion",
                "Properties",
                "AssemblyInfo.cs"
            ))
            .Where(static line => line.Contains(
                "InternalsVisibleTo",
                StringComparison.Ordinal
            ))];
        Assert.Equal(
            [
                "[assembly: InternalsVisibleTo(\"Atelia.Completion.Tests\")]",
                "[assembly: InternalsVisibleTo(\"Atelia.SessionJournal.RecapGrid.Runtime.Tests\")]"
            ],
            completionFriends
        );
        string runtimeTestsProject = Path.Combine(
            root,
            "tests",
            "SessionJournal.RecapGrid.Runtime.Tests",
            "SessionJournal.RecapGrid.Runtime.Tests.csproj"
        );
        Assert.Contains(
            "../../prototypes/Completion/Completion.csproj",
            DirectProjectReferences(runtimeTestsProject),
            StringComparer.Ordinal
        );
    }

    [Fact]
    public void RecapGridGetterIsPureReadAndUsesOnlyBoundedPublicAuthorities() {
        string root = FindRepositoryRoot();
        string getterRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Getter"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                getterRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Select(static path => path.Replace('\\', '/'))
                .Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "HistoryTimelineCoordinator",
                     "IHistoryTimelineLedgerPort",
                     "SqliteHistoryTimelineLedger",
                     "RecapGridControlCoordinator",
                     "RecapGridStoreWriter",
                     "Microsoft.Data.Sqlite",
                     "Atelia.Completion",
                     "Galatea",
                     "DerivedRecap",
                     "RecapGridManager"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        Assembly getter = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.Getter.dll"
        ));
        Assert.DoesNotContain(getter.GetExportedTypes(), static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Coordinator", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void RecapGridManagerUsesOnlyPublicBoundedAuthorities() {
        string root = FindRepositoryRoot();
        string managerRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Manager"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                managerRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Select(static path => path.Replace('\\', '/'))
                .Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "HistoryTimelineCoordinator",
                     "IHistoryTimelineLedgerPort",
                     "SqliteHistoryTimelineLedger",
                     "RecapGridControlCoordinator",
                     "Microsoft.Data.Sqlite",
                     "Atelia.Completion",
                     "Galatea",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        Assembly manager = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.Manager.dll"
        ));
        Assert.DoesNotContain(manager.GetExportedTypes(), static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains(
                "Coordinator",
                StringComparison.OrdinalIgnoreCase
            ));
    }

    [Fact]
    public void SqliteOwnersAndDirectPinsAreExact() {
        string root = FindRepositoryRoot();
        string timelineProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline",
            "SessionJournal.HistoryTimeline.csproj"
        );
        string storeProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Store",
            "SessionJournal.RecapGrid.Store.csproj"
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
        string[] sqliteOwners = [.. newProjects.Where(project =>
            DirectPackageReferences(project).Contains(
                "Microsoft.Data.Sqlite@10.0.10",
                StringComparer.Ordinal
            )
        ).Select(Path.GetFullPath).Order(StringComparer.Ordinal)];
        Assert.Equal(
            new[] { timelineProject, storeProject }
                .Select(Path.GetFullPath)
                .Order(StringComparer.Ordinal),
            sqliteOwners
        );
        string[] bundleOwners = [.. newProjects.Where(project =>
            DirectPackageReferences(project).Contains(
                "SQLitePCLRaw.bundle_e_sqlite3@2.1.12",
                StringComparer.Ordinal
            )
        ).Select(Path.GetFullPath).Order(StringComparer.Ordinal)];
        Assert.Equal(
            new[] { timelineProject, storeProject }
                .Select(Path.GetFullPath)
                .Order(StringComparer.Ordinal),
            bundleOwners
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
        Assembly store = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.Store.dll"
        ));
        Assert.DoesNotContain(
            store.GetExportedTypes(),
            static type => type.Name.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase
            ) || type.Name.Contains(
                "BackendSelector",
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    [Fact]
    public void RecapGridStoreHasNoRuntimeAuthorityBackEdge() {
        string root = FindRepositoryRoot();
        string storeRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Store"
        );
        string product = string.Join(
            "\n",
            Directory.EnumerateFiles(
                storeRoot,
                "*.cs",
                SearchOption.AllDirectories
            ).Select(static path => path.Replace('\\', '/'))
                .Where(static path => !IsBuildOutput(path))
                .Select(File.ReadAllText)
        );
        foreach (string forbidden in new[] {
                     "Atelia.SessionJournal.HistoryTimeline",
                     "HistoryTimelineFactory",
                     "HistoryTimelineCoordinator",
                     "SessionJournalReadView",
                     "SessionJournalEngine",
                     "RecapGrid.Control",
                     "Completion",
                     "DerivedRecap"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
        string project = File.ReadAllText(Path.Combine(
            storeRoot,
            "SessionJournal.RecapGrid.Store.csproj"
        ));
        Assert.DoesNotContain(
            "SessionJournal.HistoryTimeline.csproj",
            project,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "SessionJournal.RecapGrid.Control.csproj",
            project,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "SessionJournal.csproj",
            project,
            StringComparison.OrdinalIgnoreCase
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
    public void FormalConsumersReferenceTimelineDirectlyWithoutTokenizerPins() {
        string root = FindRepositoryRoot();
        foreach ((string project, string expectedReference) in new[] {
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
    public void LegacyDerivedRecapProjectsAndReferencesAreAbsent() {
        string root = FindRepositoryRoot();
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(root, "prototypes"),
            "SessionJournal.DerivedRecap.*.csproj",
            SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(root, "tests"),
            "SessionJournal.DerivedRecap.*.csproj",
            SearchOption.AllDirectories));
        foreach (string sourceRoot in new[] {
                     Path.Combine(root, "prototypes"),
                     Path.Combine(root, "tests")
                 }) {
            foreach (string project in Directory.EnumerateFiles(
                         sourceRoot, "*.csproj", SearchOption.AllDirectories)) {
                if (project.Contains("/bin/", StringComparison.Ordinal)
                    || project.Contains("/obj/", StringComparison.Ordinal)) {
                    continue;
                }
                Assert.DoesNotContain(
                    "SessionJournal.DerivedRecap",
                    File.ReadAllText(project),
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ProductionConfigCurrentDocsAndSolutionHaveZeroLegacyLedger() {
        string root = FindRepositoryRoot();
        var files = new List<string> {
            Path.Combine(root, "Atelia.sln"),
            Path.Combine(root, "prototypes", "SessionJournal.Cli", "README.md"),
            Path.Combine(root, "prototypes", "Galatea", "README.md")
        };
        files.AddRange(Directory.EnumerateFiles(
            Path.Combine(root, "prototypes"),
            "*",
            SearchOption.AllDirectories
        ).Where(static path =>
            !path.Contains("/bin/", StringComparison.Ordinal)
            && !path.Contains("/obj/", StringComparison.Ordinal)
            && !path.Contains("/.atelia/", StringComparison.Ordinal)
            && Path.GetExtension(path) is ".cs" or ".csproj" or ".json"));
        files.AddRange(Directory.EnumerateFiles(
            Path.Combine(root, "docs", "SessionJournal", "current"),
            "*.md",
            SearchOption.AllDirectories
        ));

        string combined = string.Join('\n', files.Select(File.ReadAllText));
        foreach (string forbidden in new[] {
                     "SessionJournal.DerivedRecap",
                     "DerivedRecapEpoch",
                     "DerivedRecapRebuildSpool",
                     "recapMaintainerConnections",
                     "GalateaRecapComposition",
                     "RecapGridCandidateComposition",
                     "RecapGridCandidateCommands"
                 }) {
            Assert.DoesNotContain(forbidden, combined, StringComparison.Ordinal);
        }

        string legacyOwner = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.Cli",
            "RecapGridLegacyRootCommands.cs"
        );
        string[] oldRootOwners = [.. files
        .Append(legacyOwner)
        .Distinct(StringComparer.Ordinal)
        .Where(path => new[] {
            "derived/recap/v4",
            "derived/recap/v5",
            "derived/recap/v6",
            "derived/recap/v7",
            "derived/recap/v8",
            "derived/recap/v9",
            "derived/recap/rebuild/v1",
            "config/recap-planner-config.json"
        }.Any(token => File.ReadAllText(path).Contains(
            token,
            StringComparison.Ordinal)))];
        Assert.Equal([legacyOwner], oldRootOwners);
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
        string storeProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Store",
            "SessionJournal.RecapGrid.Store.csproj"
        );
        string managerProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Manager",
            "SessionJournal.RecapGrid.Manager.csproj"
        );
        HashSet<string> closure = ProjectClosure(
            timelineProject,
            abstractionsProject,
            controlProject,
            storeProject,
            managerProject
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
            "Atelia.SessionJournal.RecapGrid.Control.dll",
            "Atelia.SessionJournal.RecapGrid.Store.dll",
            "Atelia.SessionJournal.RecapGrid.Manager.dll",
            "Atelia.SessionJournal.RecapGrid.Getter.dll",
            "Atelia.SessionJournal.RecapGrid.Runtime.dll",
            "Atelia.SessionJournal.RecapGrid.Online.dll"
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

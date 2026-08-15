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
        string o200kProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline.O200k",
            "SessionJournal.HistoryTimeline.O200k.csproj"
        );
        string recapGridProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid",
            "SessionJournal.RecapGrid.csproj"
        );
        string hostingProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Hosting",
            "SessionJournal.RecapGrid.Hosting.csproj"
        );
        string cadenceProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid.Cadence",
            "SessionJournal.RecapGrid.Cadence.csproj"
        );
        string galateaAssetProject = Path.Combine(
            root,
            "prototypes",
            "Galatea.RecapGrid",
            "Galatea.RecapGrid.csproj"
        );

        Assert.Equal(
            ["../SessionJournal/SessionJournal.csproj"],
            DirectProjectReferences(timelineProject)
        );
        Assert.Equal(
            [
                "../Completion.Abstractions/Completion.Abstractions.csproj",
                "../SessionJournal/SessionJournal.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj"
            ],
            DirectProjectReferences(o200kProject)
        );
        Assert.Equal(
            [
                "../../src/EventJournal/EventJournal.csproj",
                "../Completion.Abstractions/Completion.Abstractions.csproj",
                "../Completion.Tools/Completion.Tools.csproj",
                "../SessionJournal/SessionJournal.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../SessionJournal.RecapGrid.Cadence/SessionJournal.RecapGrid.Cadence.csproj"
            ],
            DirectProjectReferences(recapGridProject)
        );
        Assert.Equal(
            [
                "../SessionJournal.RecapGrid/SessionJournal.RecapGrid.csproj",
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                "../Completion/Completion.csproj",
                "../Completion.Abstractions/Completion.Abstractions.csproj"
            ],
            DirectProjectReferences(hostingProject)
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
            ["../SessionJournal.RecapGrid/SessionJournal.RecapGrid.csproj"],
            DirectProjectReferences(galateaAssetProject)
        );
        Assert.Equal(
            [
                "Microsoft.Data.Sqlite@10.0.10",
                "SQLitePCLRaw.bundle_e_sqlite3@2.1.12",
                "Microsoft.Bcl.Memory@9.0.17"
            ],
            DirectPackageReferences(timelineProject)
        );
        Assert.Equal(
            [
                "Microsoft.ML.Tokenizers@2.0.0",
                "Microsoft.ML.Tokenizers.Data.O200kBase@2.0.0"
            ],
            DirectPackageReferences(o200kProject)
        );
        Assert.Equal(
            ["Atelia.SessionJournal.HistoryTimeline.Tests"],
            XDocument.Load(o200kProject)
                .Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray()
        );
        Assert.Equal(
            [
                "Microsoft.Data.Sqlite@10.0.10",
                "SQLitePCLRaw.bundle_e_sqlite3@2.1.12"
            ],
            DirectPackageReferences(recapGridProject)
        );
        Assert.Empty(DirectPackageReferences(hostingProject));
        Assert.Empty(DirectPackageReferences(cadenceProject));
        Assert.Empty(DirectPackageReferences(galateaAssetProject));
        XDocument recapGridDocument = XDocument.Load(recapGridProject);
        Assert.Equal(
            [
                "Atelia.SessionJournal.RecapGrid.Control.Tests",
                "Atelia.SessionJournal.RecapGrid.Control.CrashHarness",
                "Atelia.SessionJournal.RecapGrid.Store.Tests",
                "Atelia.SessionJournal.RecapGrid.Store.CrashHarness",
                "Atelia.SessionJournal.RecapGrid.Manager.Tests",
                "Atelia.SessionJournal.RecapGrid.Runtime.Tests",
                "Atelia.SessionJournal.RecapGrid.Getter.Tests",
                "Atelia.SessionJournal.RecapGrid.Hosting.Tests",
                "Atelia.SessionJournal.RecapGrid.Online.Tests",
                "Atelia.SessionJournal.RecapGrid.AgentControl.Tests"
            ],
            recapGridDocument.Descendants("InternalsVisibleTo")
                .Select(static element =>
                    (string?)element.Attribute("Include"))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray()
        );
        Assert.DoesNotContain(
            recapGridDocument.Descendants("InternalsVisibleTo"),
            static element => ((string?)element.Attribute("Include"))
                ?.Contains("PublicSurface", StringComparison.Ordinal) is true
        );
        Assert.Equal(
            "Atelia.SessionJournal.RecapGrid.Store.SchemaV2.sql",
            (string?)recapGridDocument.Descendants("EmbeddedResource")
                .Single().Attribute("LogicalName")
        );
        Assert.Equal(
            "true",
            recapGridDocument.Descendants("TreatWarningsAsErrors")
                .Single().Value
        );
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
            "SessionJournal.RecapGrid",
            "AgentControl"
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
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        Assert.DoesNotContain(
            ProductModuleTypes(productAssembly, "AgentControl"),
            static type => type.Name.Contains(
                "Backend",
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    [Fact]
    public void RecapGridOnlineOwnsOnlyBoundedProviderNeutralComposition() {
        string root = FindRepositoryRoot();
        string onlineRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid",
            "Online"
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
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        Assert.DoesNotContain(ProductModuleTypes(online, "Online"), static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Ledger", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Coordinator", StringComparison.OrdinalIgnoreCase)
        );
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
        string cliProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.Cli",
            "SessionJournal.Cli.csproj"
        );
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
                     "Galatea.Server",
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
        Assert.Contains(
            "../Galatea.RecapGrid/Galatea.RecapGrid.csproj",
            DirectProjectReferences(cliProject),
            StringComparer.Ordinal
        );
        Assert.DoesNotContain(
            "../Galatea/Galatea.Server.csproj",
            DirectProjectReferences(cliProject),
            StringComparer.Ordinal
        );
        string galateaServerProject = Path.Combine(
            root,
            "prototypes",
            "Galatea",
            "Galatea.Server.csproj"
        );
        Assert.DoesNotContain(
            "../Galatea.RecapGrid/Galatea.RecapGrid.csproj",
            DirectProjectReferences(galateaServerProject),
            StringComparer.Ordinal
        );
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
            "SessionJournal.RecapGrid",
            "Runtime"
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
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        Assert.DoesNotContain(ProductModuleTypes(runtime, "Runtime"), static type =>
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
                         "SessionJournal.RecapGrid",
                         "SessionJournal.RecapGrid.csproj"
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
            "SessionJournal.RecapGrid",
            "Getter"
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
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        Assert.DoesNotContain(ProductModuleTypes(getter, "Getter"), static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Coordinator", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void RecapGridManagerUsesOnlyBoundedOwnerApis() {
        string root = FindRepositoryRoot();
        string managerRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid",
            "Manager"
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
                     "MaximumSelectedRows",
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
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        Assert.DoesNotContain(ProductModuleTypes(manager, "Manager"), static type =>
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
            "SessionJournal.RecapGrid",
            "SessionJournal.RecapGrid.csproj"
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
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        Assert.DoesNotContain(
            ProductModuleTypes(store, "Store"),
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
            "SessionJournal.RecapGrid",
            "Store"
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
                     "DerivedRecap",
                     "MaximumDatabaseBytes",
                     "MaximumCellCount",
                     "MaximumRowViewCount",
                     "MaximumRowViewMemberCount",
                     "MaximumFulfilledViewCount",
                     "SchemaV1.sql"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                product,
                StringComparison.Ordinal
            );
        }
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
            "public static class HistoryLoadProjector"
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
        foreach (string declaration in new[] {
                     "public sealed class O200kBaseHistoryUnitLoadEstimator",
                     "public const string EstimatorId"
                 }) {
            string owner = Assert.Single(sourceFiles, path =>
                File.ReadAllText(path).Contains(
                    declaration,
                    StringComparison.Ordinal
                )
            );
            Assert.Contains(
                "/prototypes/SessionJournal.HistoryTimeline.O200k/",
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
            "/prototypes/SessionJournal.HistoryTimeline.O200k/O200kBaseHistoryUnitLoadEstimator.cs",
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
    public void HistoryTimelineV2HasNoImmutableTrieOrLifetimeCapSource() {
        string root = FindRepositoryRoot();
        string timelineRoot = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.HistoryTimeline");
        string[] sourceFiles = [.. Directory.EnumerateFiles(
            timelineRoot,
            "*.cs",
            SearchOption.AllDirectories
        ).Where(static path => !IsBuildOutput(path))];
        string combined = string.Join('\n',
            sourceFiles.Select(File.ReadAllText));

        Assert.Contains(
            "internal const int SchemaVersion = 2;",
            combined,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE current_selected_path",
            combined,
            StringComparison.Ordinal);
        string storage = File.ReadAllText(Path.Combine(
            timelineRoot,
            "HistoryTimelineStorage.cs"));
        Assert.Contains("\"v2\"", storage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"v1\"", storage, StringComparison.Ordinal);
        foreach (string forbidden in new[] {
                     "SqliteSelectedPathTrie",
                     "selected_path_nodes",
                     "selected_path_snapshots",
                     "MaximumRowCount",
                     "MaximumTrieNodeCount",
                     "MaximumDatabaseBytes",
                     "MaximumRestoreCopyBytes"
                 }) {
            Assert.DoesNotContain(
                forbidden,
                combined,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FormalConsumersReferenceTimelineAndO200kDirectlyWithoutPackagePins() {
        string root = FindRepositoryRoot();
        foreach (string project in new[] {
            Path.Combine(
                root,
                "prototypes",
                "SessionJournal.Cli",
                "SessionJournal.Cli.csproj"
            ),
            Path.Combine(
                root,
                "prototypes",
                "Galatea",
                "Galatea.Server.csproj"
            )
        }) {
            string[] references = DirectProjectReferences(project);
            Assert.Contains(
                "../SessionJournal.HistoryTimeline/SessionJournal.HistoryTimeline.csproj",
                references,
                StringComparer.Ordinal);
            Assert.Contains(
                "../SessionJournal.HistoryTimeline.O200k/SessionJournal.HistoryTimeline.O200k.csproj",
                references,
                StringComparer.Ordinal);
            Assert.DoesNotContain(
                DirectPackageReferences(project),
                static package => package.StartsWith(
                    "Microsoft.ML.Tokenizers",
                    StringComparison.Ordinal));
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
        string recapGridProject = Path.Combine(
            root,
            "prototypes",
            "SessionJournal.RecapGrid",
            "SessionJournal.RecapGrid.csproj"
        );
        HashSet<string> closure = ProjectClosure(
            timelineProject,
            recapGridProject
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
            "Atelia.SessionJournal.HistoryTimeline.O200k.dll"
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
        Assembly recapGrid = Assembly.LoadFrom(Path.Combine(
            AppContext.BaseDirectory,
            "Atelia.SessionJournal.RecapGrid.dll"
        ));
        string[] recapGridReferences = [.. recapGrid.GetReferencedAssemblies()
            .Select(static item => item.Name ?? string.Empty)];
        Assert.DoesNotContain(recapGridReferences, static name =>
            string.Equals(name, "Atelia.Completion", StringComparison.Ordinal)
            || name.Contains("Galatea", StringComparison.Ordinal)
            || name.Contains(
                "SessionJournal.DerivedRecap",
                StringComparison.Ordinal
            ));
        foreach (string legacyAssemblyFileName in new[] {
                     "Atelia.SessionJournal.RecapGrid.Abstractions.dll",
                     "Atelia.SessionJournal.RecapGrid.Control.dll",
                     "Atelia.SessionJournal.RecapGrid.Store.dll",
                     "Atelia.SessionJournal.RecapGrid.Manager.dll",
                     "Atelia.SessionJournal.RecapGrid.Runtime.dll",
                     "Atelia.SessionJournal.RecapGrid.Getter.dll",
                     "Atelia.SessionJournal.RecapGrid.Online.dll",
                     "Atelia.SessionJournal.RecapGrid.AgentControl.dll"
                 }) {
            Assert.False(File.Exists(Path.Combine(
                AppContext.BaseDirectory,
                legacyAssemblyFileName
            )), $"Legacy product assembly remained: {legacyAssemblyFileName}");
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

    private static IEnumerable<Type> ProductModuleTypes(
        Assembly assembly,
        string module
    ) {
        string moduleNamespace = $"Atelia.SessionJournal.RecapGrid.{module}";
        return assembly.GetExportedTypes().Where(type =>
            string.Equals(type.Namespace, moduleNamespace, StringComparison.Ordinal)
            || type.Namespace?.StartsWith(
                moduleNamespace + ".",
                StringComparison.Ordinal
            ) is true
        );
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

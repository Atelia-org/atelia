using System.Diagnostics;
using System.Security;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapOperationPreparerTests : IDisposable {
    private static readonly EventAddress Head = Address(100);
    private static readonly EventAddress ChangedHead = Address(101);
    private static readonly RecapBlockId BlockId = new("self");
    private static readonly ContextHeaderBlockPath Target = new(
        ContextHeaderCarrier.System,
        "self"
    );
    private const string ProfileName = "self-profile";
    private const string MaintainerId = "self-maintainer";

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-preparer-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best effort for test-owned files.
        }
    }

    [Fact]
    public async Task FrozenBuildingSkipsThrowingConfigurationSource() {
        Scenario scenario = Scenario.Create();
        BuildingPlanSnapshot building = CreateBuildingPlan(
            MaintainerId,
            Target
        );
        scenario.Building =
            new CurrentLineageBuildingSelection.Available(building);
        scenario.Load = () => throw new InvalidOperationException(
            "must not load active config"
        );

        var ready = Assert.IsType<
            DerivedRecapOperationPreparationResult.Ready
        >(await scenario.PrepareAsync(Capabilities()));
        var frozen = Assert.IsType<
            PreparedRecapOperationAuthority.FrozenBuilding
        >(ready.Authority);

        Assert.Equal(0, scenario.LoadCallCount);
        Assert.Same(scenario.Lineage, frozen.Lineage);
        Assert.Equal(building.Descriptor, frozen.Descriptor);
    }

    [Fact]
    public async Task FrozenUnavailableBindingSkipsConfiguration() {
        Scenario scenario = Scenario.Create();
        scenario.Building =
            new CurrentLineageBuildingSelection.Available(
                CreateBuildingPlan("retired-maintainer", Target)
            );

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(0, scenario.LoadCallCount);
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.MaintainerUnavailable,
            Assert.Single(unavailable.Defects).Code
        );
        Assert.Null(unavailable.Configuration);
        Assert.Null(unavailable.ConfigSnapshot);
    }

    [Fact]
    public async Task FrozenFingerprintDriftSkipsConfiguration() {
        Scenario scenario = Scenario.Create();
        scenario.Building =
            new CurrentLineageBuildingSelection.Available(
                CreateBuildingPlan(
                    MaintainerId,
                    Target,
                    "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
                )
            );

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(0, scenario.LoadCallCount);
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.MaintainerUnavailable,
            Assert.Single(unavailable.Defects).Code
        );
    }

    [Fact]
    public async Task NoBuildingLoadsOnceAndPinsOneConfiguration() {
        Scenario scenario = Scenario.Create();
        ResolvedRecapPlanningConfiguration configuration =
            Configuration();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
            );

        var ready = Assert.IsType<
            DerivedRecapOperationPreparationResult.Ready
        >(await scenario.PrepareAsync(Capabilities()));
        var planning = Assert.IsType<
            PreparedRecapOperationAuthority.NewPlanning
        >(ready.Authority);

        Assert.Equal(1, scenario.LoadCallCount);
        Assert.Same(configuration, planning.Configuration);
        Assert.Same(scenario.Lineage, planning.Lineage);
        Assert.Equal(Head, planning.Baseline.CapturedRawHead);
        Assert.Null(planning.Baseline.ExpectedLatestAnchor);
    }

    [Fact]
    public async Task SelectedLatestPinsExactPublishedIdentity() {
        Scenario scenario = Scenario.Create();
        ResolvedRecapPlanningConfiguration configuration =
            Configuration();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
            );
        PublishedRecapDescriptor descriptor =
            PublishedDescriptor("selected");
        scenario.Latest = new DerivedRecapSelection.Selected(descriptor);
        scenario.ReadPlan = _ =>
            new PublishedPlanReadResult.Available(
                new PublishedPlanSnapshot(
                    descriptor,
                    CreateManifest(BlockId, Target, MaintainerId)
                )
            );

        var ready = Assert.IsType<
            DerivedRecapOperationPreparationResult.Ready
        >(await scenario.PrepareAsync(Capabilities()));
        var planning = Assert.IsType<
            PreparedRecapOperationAuthority.NewPlanning
        >(ready.Authority);

        Assert.Same(configuration, planning.Configuration);
        Assert.Equal(
            descriptor.SetAdmissionAnchor,
            planning.Baseline.ExpectedLatestAnchor
        );
        Assert.Equal(
            descriptor,
            planning.Baseline.ExpectedLatestPublished
        );
    }

    [Fact]
    public async Task SourceCapabilitiesMustMatchPreparerCapabilities() {
        Scenario scenario = Scenario.Create();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                Configuration()
            );
        var otherCapabilities =
            new RecapMaintainerCapabilitySnapshot([
                new RecapProfilePlanningDescriptor(
                    "other-profile",
                    new RecapBlockId("other"),
                    new ContextHeaderBlockPath(
                        ContextHeaderCarrier.System,
                        "other"
                    ),
                    "other-maintainer",
                    RecapPlannerTestIdentity.CapabilityFingerprint
                )
            ]);

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(otherCapabilities));

        Assert.Equal(
            DerivedRecapOperationPreparationDefectCodes
                .PlannerConfigSourceMismatch,
            Assert.Single(unavailable.Defects).Code
        );
    }

    [Fact]
    public async Task FileBackedSourceMustBelongToOperationRepository() {
        string sourceRepository = Path.Combine(
            _tempRoot,
            "source-repository"
        );
        string configPath = RecapPlannerConfigLoader.GetCanonicalPath(
            sourceRepository
        );
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllBytes(
            configPath,
            RecapPlannerConfigCodec.EncodeCanonical(ConfigDocument())
        );
        var source =
            new RepositoryRecapActivePlanningConfigurationSource(
                sourceRepository,
                Capabilities()
            );
        Scenario scenario = Scenario.Create();
        scenario.Load = source.Load;

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapOperationPreparationDefectCodes
                .PlannerConfigSourceMismatch,
            Assert.Single(unavailable.Defects).Code
        );
        Assert.Equal(configPath, unavailable.ConfigSnapshot?.CanonicalPath);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("stale")]
    [InlineData("multiple")]
    [InlineData("store")]
    public async Task NonAvailableBuildingSkipsConfiguration(
        string kind
    ) {
        Scenario scenario = Scenario.Create();
        scenario.Building = kind switch {
            "invalid" => new CurrentLineageBuildingSelection.Invalid(
                Address(80),
                [new RecapStructuralDefect("Broken", "broken")]
            ),
            "stale" => new CurrentLineageBuildingSelection.Stale(
                Address(80),
                Address(90)
            ),
            "multiple" => new CurrentLineageBuildingSelection.Multiple(
                [Address(80), Address(81)]
            ),
            "store" =>
                new CurrentLineageBuildingSelection.StoreUnavailable(
                    "store unavailable"
                ),
            _ => throw new InvalidOperationException()
        };
        scenario.Load = () => throw new InvalidOperationException(
            "must not load active config"
        );

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(0, scenario.LoadCallCount);
        Assert.Null(unavailable.Configuration);
        Assert.Null(unavailable.ConfigSnapshot);
    }

    [Fact]
    public async Task BuildingBeyondIsStagedBeforeConfigurationOrSourceContent() {
        Scenario scenario = Scenario.Create();
        SessionCurrentLineageBeyondPrefix evidence = BeyondEvidence(
            Address(80)
        );
        scenario.Building =
            new CurrentLineageBuildingSelection.BeyondPrefix(evidence);

        var beyond = Assert.IsType<
            DerivedRecapOperationPreparationResult.BeyondPrefix
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapBeyondPrefixStage
                .PreparationBuildingAdmission,
            beyond.Stage
        );
        Assert.Same(evidence, beyond.Evidence);
        Assert.Equal(0, scenario.LoadCallCount);
        Assert.Equal(0, scenario.SelectLatestCallCount);
        Assert.Equal(0, scenario.ReadPlanCallCount);
    }

    [Fact]
    public async Task LatestBeyondIsStagedBeforePublishedPlanContent() {
        Scenario scenario = Scenario.Create();
        SessionCurrentLineageBeyondPrefix evidence = BeyondEvidence(
            Address(80)
        );
        scenario.Latest = new DerivedRecapSelection.BeyondPrefix(
            evidence
        );

        var beyond = Assert.IsType<
            DerivedRecapOperationPreparationResult.BeyondPrefix
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapBeyondPrefixStage.NewPlanningSourceAnchor,
            beyond.Stage
        );
        Assert.Same(evidence, beyond.Evidence);
        Assert.Equal(1, scenario.LoadCallCount);
        Assert.Equal(1, scenario.SelectLatestCallCount);
        Assert.Equal(0, scenario.ReadPlanCallCount);
    }

    [Theory]
    [InlineData("missing",
        DerivedRecapOperationPreparationDefectCodes
            .PlannerConfigMissing)]
    [InlineData("invalid",
        RecapPlannerConfigResolveDefectCodes.UnknownProfile)]
    [InlineData("unavailable",
        DerivedRecapOperationPreparationDefectCodes
            .PlannerConfigUnavailable)]
    public async Task ConfigurationFailuresAreTyped(
        string kind,
        string expectedCode
    ) {
        Scenario scenario = Scenario.Create();
        RecapPlannerConfigSnapshot snapshot = Snapshot();
        scenario.Load = kind switch {
            "missing" => () =>
                new RecapActivePlanningConfigurationLoadResult.Missing(
                    "/repo/config/recap-planner-config.json"
                ),
            "invalid" => () =>
                new RecapActivePlanningConfigurationLoadResult.Invalid(
                    "/repo/config/recap-planner-config.json",
                    [new(expectedCode, "invalid profile")],
                    snapshot
                ),
            "unavailable" => () =>
                new RecapActivePlanningConfigurationLoadResult
                    .Unavailable(
                        "/repo/config/recap-planner-config.json",
                        "read failed",
                        snapshot
                    ),
            _ => throw new InvalidOperationException()
        };

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(1, scenario.LoadCallCount);
        Assert.Equal(expectedCode, Assert.Single(unavailable.Defects).Code);
        Assert.Equal(
            kind == "missing" ? null : snapshot,
            unavailable.ConfigSnapshot
        );
    }

    [Fact]
    public async Task CatalogMismatchIsUnavailableWithConfiguration() {
        Scenario scenario = Scenario.Create();
        ResolvedRecapPlanningConfiguration configuration =
            Configuration();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
            );
        PublishedRecapDescriptor descriptor = PublishedDescriptor("before");
        scenario.Latest = new DerivedRecapSelection.Selected(descriptor);
        ContextHeaderBlockPath otherTarget = new(
            ContextHeaderCarrier.System,
            "other"
        );
        scenario.ReadPlan = _ =>
            new PublishedPlanReadResult.Available(
                new PublishedPlanSnapshot(
                    descriptor,
                    CreateManifest(
                        new RecapBlockId("other"),
                        otherTarget,
                        "other-maintainer"
                    )
                )
            );

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapExecutionDefectCodes.CatalogMigrationRequired,
            Assert.Single(unavailable.Defects).Code
        );
        Assert.Same(configuration, unavailable.Configuration);
        Assert.Same(
            configuration.Snapshot,
            unavailable.ConfigSnapshot
        );
    }

    [Fact]
    public async Task PublishedRaceIsTypedSourceChanged() {
        Scenario scenario = Scenario.Create();
        ResolvedRecapPlanningConfiguration configuration =
            Configuration();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
            );
        PublishedRecapDescriptor before = PublishedDescriptor("before");
        scenario.Latest = new DerivedRecapSelection.Selected(before);
        scenario.ReadPlan = _ => new PublishedPlanReadResult.Changed(
            before,
            PublishedDescriptor("after")
        );

        var retryable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Retryable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapOperationPreparationRetryKind.SourceChanged,
            retryable.Kind
        );
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.SourceChanged,
            retryable.Code
        );
        Assert.Same(configuration, retryable.Configuration);
    }

    [Fact]
    public async Task FinalHeadDriftIsTypedRawHeadChanged() {
        Scenario scenario = Scenario.Create();
        ResolvedRecapPlanningConfiguration configuration =
            Configuration();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
            );
        scenario.CurrentHead = ChangedHead;

        var retryable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Retryable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapOperationPreparationRetryKind.RawHeadChanged,
            retryable.Kind
        );
        Assert.Same(configuration, retryable.Configuration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeadFenceReadFailureIsTypedUnavailable(
        bool frozenBuilding
    ) {
        Scenario scenario = Scenario.Create();
        ResolvedRecapPlanningConfiguration configuration =
            Configuration();
        scenario.Load = () =>
            new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
            );
        if (frozenBuilding) {
            scenario.Building =
                new CurrentLineageBuildingSelection.Available(
                    CreateBuildingPlan(MaintainerId, Target)
                );
        }
        scenario.ReadCurrentHead = () =>
            throw new IOException("raw head unavailable");

        var unavailable = Assert.IsType<
            DerivedRecapOperationPreparationResult.Unavailable
        >(await scenario.PrepareAsync(Capabilities()));

        Assert.Equal(
            DerivedRecapOperationPreparationDefectCodes
                .RawLineageUnavailable,
            Assert.Single(unavailable.Defects).Code
        );
        Assert.Equal(frozenBuilding ? 0 : 1, scenario.LoadCallCount);
        Assert.Equal(
            frozenBuilding ? null : configuration,
            unavailable.Configuration
        );
    }

    [Fact]
    public async Task CancellationIsNotMappedToUnavailable() {
        Scenario scenario = Scenario.Create();
        scenario.ReadLineage = _ =>
            throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await scenario.PrepareAsync(Capabilities())
        );
    }

    [Fact]
    public void RepositorySourceIsZeroTouchAndUsesPublicResolver() {
        Directory.CreateDirectory(_tempRoot);
        string configDirectory = Path.Combine(_tempRoot, "config");
        RecapMaintainerCapabilitySnapshot capabilities = Capabilities();
        var source =
            new RepositoryRecapActivePlanningConfigurationSource(
                _tempRoot,
                capabilities
            );

        Assert.False(Directory.Exists(configDirectory));
        Assert.IsType<
            RecapActivePlanningConfigurationLoadResult.Missing
        >(source.Load());
        Assert.False(Directory.Exists(configDirectory));

        RecapPlannerConfigDocument document = ConfigDocument();
        string path = RecapPlannerConfigLoader.GetCanonicalPath(
            _tempRoot
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            RecapPlannerConfigCodec.EncodeCanonical(document)
        );

        var available = Assert.IsType<
            RecapActivePlanningConfigurationLoadResult.Available
        >(source.Load());
        Assert.Equal(path, available.Configuration.Snapshot.CanonicalPath);
        Assert.Equal(
            RecapPlannerConfigCodec.ComputeSha256(
                available.Configuration.Snapshot
                    .CanonicalBytes.AsSpan()
            ),
            available.Configuration.Snapshot.ConfigSha256
        );

        File.WriteAllBytes(
            path,
            RecapPlannerConfigCodec.EncodeCanonical(
                document with {
                    Catalog = [new("unknown-profile", 32_768)]
                }
            )
        );
        var invalid = Assert.IsType<
            RecapActivePlanningConfigurationLoadResult.Invalid
        >(source.Load());
        Assert.Equal(
            RecapPlannerConfigResolveDefectCodes.UnknownProfile,
            Assert.Single(invalid.Defects).Code
        );
        Assert.NotNull(invalid.Snapshot);
    }

    [Fact]
    public void LifecyclePublicSurfaceIsAuthorityOnly() {
        Assert.Empty(
            typeof(DerivedRecapOnlineLifecycleCoordinator)
                .GetConstructors()
        );
        System.Reflection.MethodInfo factory = Assert.Single(
            typeof(DerivedRecapOnlineLifecycleCoordinator)
                .GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly
                ),
            method => method.Name == nameof(
                DerivedRecapOnlineLifecycleCoordinator.Create
            )
        );
        Assert.Equal(
            [
                typeof(SessionJournalReadView),
                typeof(DerivedRecapStore),
                typeof(PreparedRecapOperationAuthority),
                typeof(IRecapBlockMaintainerRegistry)
            ],
            factory.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
    }

    [Fact]
    public void ExecutionPublicSurfaceIsAuthorityOnly() {
        Type[] exported = typeof(DerivedRecapPreparedExecutor)
            .Assembly.GetExportedTypes();
        foreach (Type type in exported) {
            IEnumerable<System.Reflection.MethodBase> publicCallables =
                type.GetConstructors(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly
                ).Cast<System.Reflection.MethodBase>()
                .Concat(type.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly
                ));
            Assert.DoesNotContain(
                publicCallables.SelectMany(static callable =>
                    callable.GetParameters()
                ),
                static parameter => parameter.ParameterType
                    == typeof(SessionJournalEngine)
            );
        }
        Assert.DoesNotContain(
            exported,
            type => type.Name == "DerivedRecapPlannerExecutor"
        );
        Assert.DoesNotContain(
            exported,
            type => type.Name == "DerivedRecapBuildingExecutor"
        );

        System.Reflection.ConstructorInfo authorityConstructor =
            Assert.Single(
                typeof(PreparedRecapOperationAuthority)
                    .GetConstructors(
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.DeclaredOnly
                    )
            );
        Assert.True(authorityConstructor.IsPrivate);
        Assert.Equal(
            typeof(SessionCurrentLineagePrefix),
            typeof(PreparedRecapOperationAuthority)
                .GetProperty(nameof(
                    PreparedRecapOperationAuthority.Lineage
                ))!
                .PropertyType
        );
        foreach (Type authorityCase in new[] {
            typeof(PreparedRecapOperationAuthority.FrozenBuilding),
            typeof(PreparedRecapOperationAuthority.NewPlanning)
        }) {
            System.Reflection.ConstructorInfo caseConstructor =
                Assert.Single(authorityCase.GetConstructors(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly
                ));
            Assert.True(caseConstructor.IsAssembly);
        }

        System.Reflection.ConstructorInfo constructor = Assert.Single(
            typeof(DerivedRecapPreparedExecutor).GetConstructors()
        );
        Assert.Equal(
            [
                typeof(SessionJournalReadView),
                typeof(DerivedRecapStore),
                typeof(PreparedRecapOperationAuthority),
                typeof(IRecapBlockMaintainerRegistry)
            ],
            constructor.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        System.Reflection.MethodInfo execute = Assert.Single(
            typeof(DerivedRecapPreparedExecutor).GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly
            ),
            method => method.Name == nameof(
                DerivedRecapPreparedExecutor.ExecuteAsync
            )
        );
        Assert.Equal(
            [typeof(CancellationToken)],
            execute.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
    }

    [Fact]
    public async Task ExternalConsumerCannotReachLowLevelExecutors() {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-planner-surface-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempRoot);
        try {
            string assemblyPath = SecurityElement.Escape(
                typeof(DerivedRecapPreparedExecutor).Assembly.Location
            )!;
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "ExternalConsumer.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Atelia.SessionJournal.DerivedRecap.Planner">
                      <HintPath>{{assemblyPath}}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "LowLevelExecutorProbe.cs"),
                """
                using Atelia.SessionJournal.DerivedRecap.Planner;

                public sealed class LowLevelExecutorProbe {
                    private DerivedRecapPlannerExecutor? _planner;
                    private DerivedRecapBuildingExecutor? _building;
                }
                """
            );
            (int lowLevelExitCode, string output) =
                await CompileExternalConsumerAsync(tempRoot);

            Assert.NotEqual(0, lowLevelExitCode);
            Assert.Contains("CS0122", output);
            Assert.Contains("DerivedRecapPlannerExecutor", output);
            Assert.Contains("DerivedRecapBuildingExecutor", output);

            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "LowLevelExecutorProbe.cs"),
                """
                using Atelia.SessionJournal.DerivedRecap.Planner;

                public sealed class ForgedAuthority
                    : PreparedRecapOperationAuthority {
                    public ForgedAuthority(
                        PreparedRecapOperationAuthority original
                    ) : base(original) { }
                }
                """
            );
            (int authorityExitCode, string authorityOutput) =
                await CompileExternalConsumerAsync(tempRoot);
            Assert.NotEqual(0, authorityExitCode);
            Assert.Contains(
                "PreparedRecapOperationAuthority",
                authorityOutput
            );
        }
        finally {
            try {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch {
                // Best-effort cleanup for test-owned compiler inputs.
            }
        }
    }

    private static async Task<(int ExitCode, string Output)>
        CompileExternalConsumerAsync(string workingDirectory) {
        var start = new ProcessStartInfo("dotnet") {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("ExternalConsumer.csproj");
        start.ArgumentList.Add("-m:1");
        start.ArgumentList.Add("-nr:false");
        start.ArgumentList.Add("--nologo");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Failed to start external consumer compilation."
            );
        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (
            process.ExitCode,
            await outputTask + await errorTask
        );
    }

    [Fact]
    public async Task LifecycleRejectsAuthorityFromAnotherRepository() {
        Scenario scenario = Scenario.Create();
        var ready = Assert.IsType<
            DerivedRecapOperationPreparationResult.Ready
        >(await scenario.PrepareAsync(Capabilities()));
        string path = Path.Combine(_tempRoot, "other-repository");
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            path,
            engine.BranchRefId
        );

        Assert.Throws<ArgumentException>(() =>
            DerivedRecapOnlineLifecycleCoordinator.Create(
                engine.ReadView,
                store,
                ready.Authority,
                new RecapBlockMaintainerRegistry([])
            )
        );
    }

    [Fact]
    public void PublicPreparerRejectsDifferentRepositoryStore() {
        string enginePath = Path.Combine(_tempRoot, "engine");
        string storePath = Path.Combine(_tempRoot, "store");
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            enginePath,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        using SessionJournalEngine other = SessionJournalEngine.Create(
            storePath,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            storePath,
            other.BranchRefId
        );
        var source =
            new RepositoryRecapActivePlanningConfigurationSource(
                enginePath,
                Capabilities()
            );

        Assert.Throws<ArgumentException>(() => {
            _ = DerivedRecapOperationPreparer.PrepareAsync(
                engine.ReadView,
                store,
                Capabilities(),
                source
            );
        });
    }

    private static RecapMaintainerCapabilitySnapshot Capabilities()
        => new([
            new RecapProfilePlanningDescriptor(
                ProfileName,
                BlockId,
                Target,
                MaintainerId,
                RecapPlannerTestIdentity.CapabilityFingerprint
            )
        ]);

    private static ResolvedRecapPlanningConfiguration Configuration()
        => Assert.IsType<RecapPlannerConfigResolveResult.Resolved>(
            RecapPlannerConfigResolver.Resolve(
                Snapshot(),
                Capabilities()
            )
        ).Configuration;

    private static RecapPlannerConfigSnapshot Snapshot()
        => RecapPlannerConfigSnapshot.FromDocument(ConfigDocument());

    private static RecapPlannerConfigDocument ConfigDocument() => new(
        RecapPlannerConfigCodec.SchemaV2,
        RecapPlanningPolicyIds.BoundedMaintainAllV1,
        new RecapCadenceConfigDocument(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            MinimumRecentHistoryLoad: 18_000,
            RecapBuildIntervalHistoryLoad: 21_000
        ),
        [new(ProfileName, 32_768)],
        new RecapPlannerLimitsDocument(512, 4, 8, 64, 512)
    );

    private static BuildingPlanSnapshot CreateBuildingPlan(
        string maintainerId,
        ContextHeaderBlockPath target,
        string maintainerCapabilityFingerprint =
            RecapPlannerTestIdentity.CapabilityFingerprint
    ) {
        DerivedRecapSetManifest manifest = CreateManifest(
            BlockId,
            target,
            maintainerId,
            maintainerCapabilityFingerprint
        );
        var descriptor = new BuildingDescriptor(
            manifest.RefId,
            manifest.SetAdmissionAnchor,
            manifest.ManifestPayloadSha256
        );
        return new BuildingPlanSnapshot(
            descriptor,
            manifest,
            new BuildingPlanHandle(
                Path.GetTempPath(),
                descriptor
            )
        );
    }

    private static DerivedRecapSetManifest CreateManifest(
        RecapBlockId blockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        string maintainerCapabilityFingerprint =
            RecapPlannerTestIdentity.CapabilityFingerprint
    ) => DerivedRecapCodec.CreateManifest(
        new RefId(1),
        Head,
        RecapPlannerWireTestFacts.SyntheticSetups(Head),
        [
            new MaintainRecapBlockPlan(
                blockId,
                target,
                maintainerId,
                maintainerCapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    Address(1),
                    RecapPlannerWireTestFacts.SyntheticSetups(
                        Address(1)
                    )
                ),
                [
                    RecapPlannerWireTestFacts.SyntheticBoundary(Head)
                ],
                EmptyRecapPriorContext.Instance,
                32_768
            )
        ]
    );

    private static PublishedRecapDescriptor PublishedDescriptor(
        string token
    ) => new(new RefId(1), Address(90), token.PadRight(64, '0'));

    private static SessionCurrentLineagePrefix Lineage() => new(
        Head,
        1,
        [
            new SessionCurrentLineageHeader(
                Head,
                Parent: null,
                SessionEventKind.SystemPromptSetup
            )
        ],
        continuation: null,
        new SessionCurrentLineageDiagnostics(1, 0, 0)
    );

    private static SessionCurrentLineageBeyondPrefix BeyondEvidence(
        EventAddress requiredAnchor
    ) => new(
        requiredAnchor,
        Head,
        headerCount: 1,
        nextAddress: Address(99)
    );

    private static EventAddress Address(ulong value) => new(
        SizedPtr.FromPacked(value),
        1,
        AddressHint.None
    );

    private sealed class Scenario {
        internal SessionCurrentLineagePrefix Lineage { get; } =
            DerivedRecapOperationPreparerTests.Lineage();
        internal CurrentLineageBuildingSelection Building { get; set; }
            = new CurrentLineageBuildingSelection.None();
        internal DerivedRecapSelection Latest { get; set; } =
            new DerivedRecapSelection.EmptyLineage();
        internal EventAddress? CurrentHead { get; set; } = Head;
        internal Func<EventAddress?> ReadCurrentHead { get; set; }
            = null!;
        internal Func<CancellationToken, SessionCurrentLineagePrefix>
            ReadLineage { get; set; } = null!;
        internal Func<RecapActivePlanningConfigurationLoadResult>
            Load { get; set; } = null!;
        internal Func<
            PublishedRecapDescriptor,
            PublishedPlanReadResult
        > ReadPlan { get; set; } = _ =>
            throw new InvalidOperationException(
                "Published plan read was not expected."
            );
        internal int LoadCallCount { get; private set; }
        internal int SelectLatestCallCount { get; private set; }
        internal int ReadPlanCallCount { get; private set; }

        internal static Scenario Create() {
            var scenario = new Scenario();
            scenario.ReadLineage = _ => scenario.Lineage;
            scenario.ReadCurrentHead = () => scenario.CurrentHead;
            scenario.Load = () =>
                new RecapActivePlanningConfigurationLoadResult.Available(
                    Configuration()
                );
            return scenario;
        }

        internal ValueTask<DerivedRecapOperationPreparationResult>
            PrepareAsync(
            RecapMaintainerCapabilitySnapshot capabilities
        ) => DerivedRecapOperationPreparer.PrepareCoreAsync(
            new DerivedRecapOperationPreparationServices(
                DerivedRecapOperationBinding.Create(
                    "/repo",
                    new RefId(1)
                ),
                ReadLineage,
                (lineage, cancellationToken) =>
                    ValueTask.FromResult(Building),
                () => {
                    LoadCallCount++;
                    return Load();
                },
                (lineage, ordinal, cancellationToken) => {
                    SelectLatestCallCount++;
                    return ValueTask.FromResult(Latest);
                },
                (descriptor, cancellationToken) => {
                    ReadPlanCallCount++;
                    return ValueTask.FromResult(ReadPlan(descriptor));
                },
                (anchor, cancellationToken) =>
                    throw new InvalidOperationException(
                        "Published plan-at-anchor read was not expected."
                    ),
                ReadCurrentHead
            ),
            capabilities,
            CancellationToken.None
        );
    }
}

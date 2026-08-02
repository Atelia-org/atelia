using System.Diagnostics;
using System.Reflection;
using System.Security;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapAuthorityBoundaryTests {
    [Fact]
    public void PublicStoreSurfaceCannotInjectLineageOrCreateBuilding() {
        MethodInfo[] publicStoreMethods = typeof(DerivedRecapStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(
            publicStoreMethods,
            static method => method.Name == "CreateBuildingAsync"
        );
        Assert.DoesNotContain(
            publicStoreMethods,
            static method => method.GetParameters().Any(
                parameter => parameter.ParameterType
                    == typeof(SessionCurrentLineageSnapshot)
                    || parameter.ParameterType
                    == typeof(SessionCurrentLineagePrefix)
            )
        );
        Assert.Empty(typeof(DerivedRecapLineageView).GetConstructors());
        Assert.DoesNotContain(
            typeof(DerivedRecapLineageView).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ),
            static method => method.GetParameters().Any(
                parameter => parameter.ParameterType
                    == typeof(SessionCurrentLineageSnapshot)
            )
        );
        Assert.Null(
            typeof(DerivedRecapLineageView).GetProperty(
                "CurrentPrefix",
                BindingFlags.Instance | BindingFlags.Public
            )
        );
    }

    [Fact]
    public async Task InstallerHistoricalAnchorBeyondPrefixIsTypedBeforeStaging() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 257);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress root =
            fixture.RawLineage().HeadToRoot[^1].Address;
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                lineage.CapturedHead,
                [Maintain(
                    lineage.CapturedHead,
                    root,
                    [lineage.CapturedHead]
                )]
            );
        string buildingRoot = Path.GetDirectoryName(
            fixture.Store.GetBuildingPathForTest(lineage.CapturedHead)
        )!;
        string[] beforeEntries = Directory
            .EnumerateFileSystemEntries(buildingRoot)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        SessionJournalReadDiagnostics beforeReads =
            fixture.Engine.CaptureReadDiagnostics();

        var beyond = Assert.IsType<CreateBuildingResult.BeyondPrefix>(
            await new DerivedRecapBuildingInstaller(
                    fixture.Store,
                    fixture.Engine
                )
                .InstallAsync(manifest, lineage.CapturedHead)
        );
        SessionJournalReadDiagnostics reads =
            fixture.Engine.CaptureReadDiagnostics() - beforeReads;

        Assert.Equal(root, beyond.Evidence.RequiredAnchor);
        Assert.Equal(513, beyond.Evidence.HeaderCount);
        Assert.Equal(1026, reads.HeaderPreviewReadCount);
        Assert.Equal(0, reads.PayloadReadCount);
        Assert.Equal(
            beforeEntries,
            Directory.EnumerateFileSystemEntries(buildingRoot)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(lineage.CapturedHead)
        );
    }

    [Fact]
    public void ClosedUnionBaseConstructorsHaveNoExternalPath() {
        AssertNoExternallyCallableConstructor(typeof(RecapBlockPlan));
        AssertNoExternallyCallableConstructor(
            typeof(RecapMaintainSource)
        );
        AssertNoExternallyCallableConstructor(typeof(RecapPriorContext));
    }

    [Fact]
    public async Task ExternalConsumerCannotDeriveUnknownUnionCases() {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-closed-union-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempRoot);
        try {
            string assemblyPath = SecurityElement.Escape(
                typeof(RecapBlockPlan).Assembly.Location
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
                    <Reference Include="Atelia.SessionJournal.DerivedRecap.Store">
                      <HintPath>{{assemblyPath}}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "ForgedRecapBlockPlan.cs"),
                """
                using Atelia.SessionJournal.DerivedRecap.Store;

                public sealed class ForgedPlanDeclared : RecapBlockPlan {
                    public ForgedPlanDeclared()
                        : base(null!, null!, 1) { }
                }

                public sealed class ForgedPlanCopy : RecapBlockPlan {
                    public ForgedPlanCopy(RecapBlockPlan source)
                        : base(source) { }
                }
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "ForgedRecapMaintainSource.cs"),
                """
                using Atelia.SessionJournal.DerivedRecap.Store;

                public sealed class ForgedSourceDeclared
                    : RecapMaintainSource {
                    public ForgedSourceDeclared() : base() { }
                }

                public sealed class ForgedSourceCopy
                    : RecapMaintainSource {
                    public ForgedSourceCopy(RecapMaintainSource source)
                        : base(source) { }
                }
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "ForgedRecapPriorContext.cs"),
                """
                using Atelia.SessionJournal.DerivedRecap.Store;

                public sealed class ForgedPriorDeclared
                    : RecapPriorContext {
                    public ForgedPriorDeclared() : base() { }
                }

                public sealed class ForgedPriorCopy
                    : RecapPriorContext {
                    public ForgedPriorCopy(RecapPriorContext source)
                        : base(source) { }
                }
                """
            );

            var start = new ProcessStartInfo("dotnet") {
                WorkingDirectory = tempRoot,
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
            string output = await outputTask + await errorTask;

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("CS0122", output);
            Assert.Contains("CS1729", output);
            Assert.Contains("ForgedRecapBlockPlan.cs", output);
            Assert.Contains("ForgedRecapMaintainSource.cs", output);
            Assert.Contains("ForgedRecapPriorContext.cs", output);
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

    [Fact]
    public async Task LineageViewRejectsPathAndRefBindingMismatch() {
        using RecapStoreFixture first =
            await RecapStoreFixture.CreateAsync();
        using RecapStoreFixture second =
            await RecapStoreFixture.CreateAsync();

        Assert.Throws<ArgumentException>(() =>
            DerivedRecapLineageView.Capture(
                first.Store,
                second.Engine
            )
        );

        RefId mismatchedRef = first.Engine.BranchRefId.Packed == 1
            ? new RefId(2)
            : new RefId(1);
        DerivedRecapStore mismatchedStore = DerivedRecapStore.Open(
            first.Path,
            mismatchedRef
        );
        Assert.Throws<ArgumentException>(() =>
            DerivedRecapLineageView.Capture(
                mismatchedStore,
                first.Engine
            )
        );
    }

    [Theory]
    [InlineData("admission", "AdmissionAnchorOffLineage")]
    [InlineData("source", "SourceAnchorInvalid")]
    [InlineData("catch-up", "CatchUpRouteIncomplete")]
    [InlineData("prior", "PriorContextAnchorInvalid")]
    [InlineData("retroactive", "RetroactivePublication")]
    public async Task InstallerRejectsInvalidPlanBeforeStaging(
        string defectKind,
        string expectedCode
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[4].Address;
        EventAddress offLineage = new(
            SizedPtr.FromPacked(ulong.MaxValue),
            uint.MaxValue,
            AddressHint.None
        );
        if (defectKind == "retroactive") {
            await fixture.PublishAsync(
                target,
                lineage.CurrentPrefix.HeadToOldest[2].Address
            );
            target = lineage.CurrentPrefix.HeadToOldest[2].Address;
            replayStart = lineage.CurrentPrefix.HeadToOldest[4].Address;
        }

        RecapBlockPlan plan = defectKind switch {
            "source" => new InheritRecapBlockPlan(
                new RecapBlockId("roleplay.self"),
                Target("roleplay.self"),
                offLineage,
                new string('a', 64),
                new string('b', 64)
            ),
            "catch-up" => Maintain(
                target,
                replayStart,
                [lineage.CurrentPrefix.HeadToOldest[2].Address]
            ),
            "prior" => Maintain(
                target,
                replayStart,
                [target],
                new InlineRecapPriorContext(
                    target,
                    ContextHeaderSnapshot.Empty
                )
            ),
            _ => Maintain(target, replayStart, [target])
        };
        EventAddress admission = defectKind == "admission"
            ? offLineage
            : target;
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                admission,
                [plan]
            );
        string[] before = Directory.EnumerateFileSystemEntries(
                Path.Combine(
                    fixture.Store.StoreRootPathForTest,
                    "building"
                )
            )
            .ToArray();

        CreateBuildingResult.InvalidPlan invalid =
            Assert.IsType<CreateBuildingResult.InvalidPlan>(
                await new DerivedRecapBuildingInstaller(
                        fixture.Store,
                        fixture.Engine
                    )
                    .InstallAsync(manifest, lineage.CapturedHead)
            );

        Assert.Contains(
            invalid.Defects,
            defect => defect.Code == expectedCode
        );
        Assert.Equal(
            before,
            Directory.EnumerateFileSystemEntries(
                    Path.Combine(
                        fixture.Store.StoreRootPathForTest,
                        "building"
                    )
                )
                .ToArray()
        );
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(admission)
        );
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("existing")]
    [InlineData("inherit")]
    public async Task InstallerAcceptsAllSupportedSourceKinds(
        string sourceKind
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress source = lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[4].Address;
        var id = new RecapBlockId("roleplay.self");
        ContextHeaderBlockPath targetPath = Target(id.Value);
        RecapBlockPlan plan;
        if (sourceKind == "empty") {
            plan = Maintain(target, replayStart, [target]);
        }
        else {
            PublishedRecapDescriptor published =
                await fixture.PublishAsync(
                    source,
                    replayStart,
                    content: "source recap"
                );
            DerivedRecapFrozenInput input =
                DerivedRecapCodec.CreateFrozenInput(
                    id,
                    targetPath,
                    source,
                    "source recap"
                );
            plan = sourceKind == "inherit"
                ? new InheritRecapBlockPlan(
                    id,
                    targetPath,
                    source,
                    published.EnvelopeSha256,
                    input.PayloadSha256
                )
                : new MaintainRecapBlockPlan(
                    id,
                    targetPath,
                    "roleplay.autobiographical",
                    RecapTestIdentity.CapabilityFingerprint,
                    new ExistingRecapMaintainSource(
                        source,
                        published.EnvelopeSha256,
                        input.PayloadSha256
                    ),
                    [target],
                    EmptyRecapPriorContext.Instance
                );
        }
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                [plan]
            );

        Assert.IsType<CreateBuildingResult.Created>(
            await new DerivedRecapBuildingInstaller(
                    fixture.Store,
                    fixture.Engine
                )
                .InstallAsync(manifest, lineage.CapturedHead)
        );
    }

    private static MaintainRecapBlockPlan Maintain(
        EventAddress target,
        EventAddress replayStart,
        IReadOnlyList<EventAddress> catchUpThrough,
        RecapPriorContext? priorContext = null
    ) => new(
        new RecapBlockId("roleplay.self"),
        Target("roleplay.self"),
        "roleplay.autobiographical",
        RecapTestIdentity.CapabilityFingerprint,
        new EmptyRecapMaintainSource(replayStart),
        catchUpThrough,
        priorContext ?? EmptyRecapPriorContext.Instance
    );

    private static ContextHeaderBlockPath Target(string blockId)
        => new(ContextHeaderCarrier.System, blockId);

    private static void AssertNoExternallyCallableConstructor(
        Type type
    ) {
        ConstructorInfo[] constructors = type.GetConstructors(
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
        );
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => {
            Assert.False(constructor.IsPublic);
            Assert.False(constructor.IsFamily);
            Assert.False(constructor.IsFamilyOrAssembly);
            Assert.True(
                constructor.IsPrivate
                || constructor.IsAssembly
                || constructor.IsFamilyAndAssembly
            );
        });
    }
}

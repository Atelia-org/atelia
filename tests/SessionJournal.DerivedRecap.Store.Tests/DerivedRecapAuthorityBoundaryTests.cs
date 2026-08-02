using System.Diagnostics;
using System.Reflection;
using System.Security;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapAuthorityBoundaryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustedWritesDoNotRecreateMissingCoordinationLock(
        bool publish
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            );
        if (publish) {
            _ = Assert.IsType<CreateBuildingResult.Created>(
                await fixture.Store.CreateBuildingAsync(manifest)
            );
            await RecapStoreTestDriver.InstallFinalAsync(
                fixture.Store,
                anchor,
                DerivedRecapCodec.CreateBlock(
                    plan,
                    anchor,
                    "ready"
                )
            );
        }
        string lockPath = Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4",
            "locks",
            $"{fixture.Engine.BranchRefId.ToHexString()}.lock"
        );
        File.Delete(lockPath);

        if (publish) {
            Assert.IsType<PublishRecapResult.StoreUnavailable>(
                await fixture.Publisher.PublishAsync(anchor)
            );
        }
        else {
            Assert.IsType<CreateBuildingResult.StoreUnavailable>(
                await new DerivedRecapBuildingInstaller(
                        fixture.Store,
                        fixture.Engine
                    )
                    .InstallAsync(manifest, anchor)
            );
        }
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task EveryMutationSeamRequiresExistingReadyLock() {
        int mutationHooks = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeAtomicFileReplace: _ => mutationHooks++,
                    BeforeBuildingQuarantineRename: () => mutationHooks++
                )
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            );
        var created = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(manifest)
        );
        BuildingBlockInspection building =
            await fixture.Store.InspectBuildingBlockAsync(
                created.Descriptor,
                plan.RecapBlockId
            );
        DerivedRecapBlock candidate =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "ready"
            );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            candidate
        );
        _ = Assert.IsType<PublishRecapResult.Published>(
            await fixture.Publisher.PublishAsync(anchor)
        );
        var published = Assert.IsType<
            PublishedRestoreInspectionResult.Available
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                anchor,
                lineage
            )
        ).Inspection;
        PublishedBlockRestoreInspection publishedBlock =
            published.Blocks[plan.RecapBlockId];
        string v4Root = Path.Combine(
            fixture.Path,
            "derived",
            "recap",
            "v4"
        );
        string lockPath = Path.Combine(
            v4Root,
            "locks",
            $"{fixture.Engine.BranchRefId.ToHexString()}.lock"
        );
        File.Delete(lockPath);
        string[] before = SnapshotTree(v4Root);
        mutationHooks = 0;

        Assert.IsType<QuarantineBuildingResult.Unavailable>(
            await fixture.Store.QuarantineBuildingAsync(anchor)
        );
        AssertStoreUnavailable(
            Assert.IsType<CheckpointWriteResult.Unavailable>(
                await fixture.Store.AdvanceRollingCheckpointAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    building.Checkpoint.StateToken,
                    candidate
                )
            ).Defects
        );
        AssertStoreUnavailable(
            Assert.IsType<FinalBlockWriteResult.Unavailable>(
                await fixture.Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    building.Final.StateToken,
                    candidate
                )
            ).Defects
        );
        AssertStoreUnavailable(
            Assert.IsType<PublishedCheckpointWriteResult.Unavailable>(
                await fixture.Store.AdvancePublishedCheckpointAsync(
                    published.Handle,
                    plan.RecapBlockId,
                    publishedBlock.Checkpoint.StateToken,
                    candidate
                )
            ).Defects
        );
        AssertStoreUnavailable(
            Assert.IsType<PublishedFinalWriteResult.Unavailable>(
                await fixture.Store.InstallPublishedReplacementAsync(
                    published.Handle,
                    plan.RecapBlockId,
                    publishedBlock.Final.StateToken,
                    candidate
                )
            ).Defects
        );
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );
        AssertStoreUnavailable(
            Assert.IsType<PublishedEnvelopeCommitResult.Unavailable>(
                await restorer.CommitEnvelopeAsync(
                    fixture.Store
                        .IssuePublishedEnvelopeCommitAuthority(
                            published.Handle,
                            [publishedBlock.WriteAuthority]
                        ),
                    lineage.CapturedHead
                )
            ).Defects
        );

        Assert.Equal(0, mutationHooks);
        Assert.False(File.Exists(lockPath));
        Assert.Equal(before, SnapshotTree(v4Root));
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("damaged-header")]
    [InlineData("wrong-kind-header")]
    public async Task InstallerStoreAvailabilityFailuresAreTypedBeforeStaging(
        string damageMode
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [Maintain(
                    fixture,
                    anchor,
                    lineage.CurrentPrefix.HeadToOldest[^2].Address,
                    [anchor]
                )]
            );
        string storeRoot = fixture.Store.StoreRootPathForTest;
        string buildingPath =
            fixture.Store.GetBuildingPathForTest(anchor);
        string storeHeader = Path.Combine(storeRoot, "store.json");
        switch (damageMode) {
            case "missing-root":
                Directory.Move(storeRoot, $"{storeRoot}.missing");
                break;
            case "damaged-header":
                await File.WriteAllTextAsync(storeHeader, "damaged");
                break;
            case "wrong-kind-header":
                File.Delete(storeHeader);
                Directory.CreateDirectory(storeHeader);
                break;
            default:
                throw new InvalidOperationException(damageMode);
        }

        Assert.IsType<CreateBuildingResult.StoreUnavailable>(
            await new DerivedRecapBuildingInstaller(
                    fixture.Store,
                    fixture.Engine
                )
                .InstallAsync(manifest, anchor)
        );
        Assert.False(Directory.Exists(buildingPath));
    }

    private static void AssertStoreUnavailable(
        IReadOnlyList<RecapStructuralDefect> defects
    ) => Assert.Contains(
        defects,
        static defect => defect.Code == "StoreUnavailable"
    );

    private static string[] SnapshotTree(string root) =>
        Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories
            )
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task InstallerCancellationPropagates() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [Maintain(
                    fixture,
                    anchor,
                    lineage.CurrentPrefix.HeadToOldest[^2].Address,
                    [anchor]
                )]
            );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new DerivedRecapBuildingInstaller(
                    fixture.Store,
                    fixture.Engine
                )
                .InstallAsync(
                    manifest,
                    anchor,
                    cancellation.Token
                )
        );
    }

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

        MethodInfo buildingRead = Assert.Single(
            publicStoreMethods,
            static method => method.Name
                == nameof(DerivedRecapStore.ReadBuildingAsync)
        );
        Assert.Equal(
            typeof(BuildingPlanHandle),
            buildingRead.GetParameters()[0].ParameterType
        );
        MethodInfo buildingInspection = Assert.Single(
            publicStoreMethods,
            static method => method.Name
                == nameof(DerivedRecapStore.InspectBuildingBlockAsync)
        );
        Assert.Equal(
            typeof(BuildingPlanHandle),
            buildingInspection.GetParameters()[0].ParameterType
        );

        MethodInfo checkpointWrite = Assert.Single(
            publicStoreMethods,
            static method => method.Name
                == nameof(DerivedRecapStore
                    .AdvancePublishedCheckpointAsync)
        );
        Assert.Equal(
            typeof(PublishedBlockWriteAuthority),
            checkpointWrite.GetParameters()[0].ParameterType
        );
        MethodInfo finalWrite = Assert.Single(
            publicStoreMethods,
            static method => method.Name
                == nameof(DerivedRecapStore
                    .InstallPublishedReplacementAsync)
        );
        Assert.Equal(
            typeof(PublishedBlockWriteAuthority),
            finalWrite.GetParameters()[0].ParameterType
        );
        MethodInfo commit = Assert.Single(
            typeof(DerivedRecapRestorer).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ),
            static method => method.Name == "CommitEnvelopeAsync"
        );
        Assert.Equal(
            typeof(PublishedEnvelopeCommitAuthority),
            commit.GetParameters()[0].ParameterType
        );
        Assert.DoesNotContain(
            commit.GetParameters(),
            static parameter => parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition()
                    == typeof(IReadOnlyDictionary<,>)
        );
        AssertNoExternallyCallableConstructor(
            typeof(PublishedBlockWriteAuthority)
        );
        AssertNoExternallyCallableConstructor(
            typeof(PublishedEnvelopeCommitAuthority)
        );
        Assert.DoesNotContain(
            typeof(PublishedEnvelopeCommitResult).GetNestedTypes(
                BindingFlags.Public
            ),
            static type => type.Name == "BeyondPrefix"
        );
    }

    [Fact]
    public async Task BuildingPlanHandleIsPortableAcrossSamePathAndRef() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                RecapWireTestFacts.CreateManifest(
                    fixture.Engine,
                    anchor,
                    [plan]
                )
            )
        );
        BuildingPlanSnapshot snapshot = Assert.IsType<
            BuildingPlanReadResult.Available
        >(
            await fixture.Store.ReadBuildingPlanAsync(anchor)
        ).Snapshot;
        DerivedRecapStore reopened = DerivedRecapStore.Open(
            Path.Combine(fixture.Path, "."),
            fixture.Engine.BranchRefId
        );

        var content = Assert.IsType<BuildingReadResult.Available>(
            await reopened.ReadBuildingAsync(snapshot.Handle)
        );
        BuildingBlockInspection block =
            await reopened.InspectBuildingBlockAsync(
                snapshot.Handle,
                plan.RecapBlockId
        );

        Assert.Equal(snapshot.Descriptor, content.Snapshot.Descriptor);
        Assert.Equal(
            DerivedRecapCodec.ComputeBlockPlanSha256(plan),
            DerivedRecapCodec.ComputeBlockPlanSha256(block.Plan)
        );
    }

    [Fact]
    public async Task BuildingPlanHandleRejectsCrossPathAndCrossRef() {
        using RecapStoreFixture first =
            await RecapStoreFixture.CreateAsync();
        using RecapStoreFixture second =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = first.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = first.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await first.Store.CreateBuildingAsync(
                RecapWireTestFacts.CreateManifest(
                    first.Engine,
                    anchor,
                    [plan]
                )
            )
        );
        BuildingPlanHandle handle = Assert.IsType<
            BuildingPlanReadResult.Available
        >(
            await first.Store.ReadBuildingPlanAsync(anchor)
        ).Snapshot.Handle;
        RefId mismatchedRef = first.Engine.BranchRefId.Packed == 1
            ? new RefId(2)
            : new RefId(1);
        DerivedRecapStore wrongRef = DerivedRecapStore.Open(
            first.Path,
            mismatchedRef
        );

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await second.Store.ReadBuildingAsync(handle)
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await second.Store.InspectBuildingBlockAsync(
                handle,
                plan.RecapBlockId
            )
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await wrongRef.ReadBuildingAsync(handle)
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await wrongRef.InspectBuildingBlockAsync(
                handle,
                plan.RecapBlockId
            )
        );
    }

    [Fact]
    public async Task InstallerHistoricalAnchorBeyondPrefixIsTypedBeforeStaging() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 259);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress beyondAnchor = fixture.RawLineage().HeadToRoot[
            lineage.CurrentPrefix.MaxHeaderCount
        ].Address;
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                lineage.CapturedHead,
                [Maintain(
                    fixture,
                    lineage.CapturedHead,
                    beyondAnchor,
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

        var beyondResult = Assert.IsType<CreateBuildingResult.BeyondPrefix>(
            await new DerivedRecapBuildingInstaller(
                    fixture.Store,
                    fixture.Engine
                )
                .InstallAsync(manifest, lineage.CapturedHead)
        );
        SessionJournalReadDiagnostics reads =
            fixture.Engine.CaptureReadDiagnostics() - beforeReads;

        Assert.Equal(
            beyondAnchor,
            beyondResult.Evidence.RequiredAnchor
        );
        Assert.Equal(513, beyondResult.Evidence.HeaderCount);
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
    [InlineData("catch-up", "CatchUpRouteInvalid")]
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
        SessionContextAnchorSetupReferences targetSetups =
            fixture.Setups(target);

        RecapBlockPlan plan = defectKind switch {
            "admission" => new MaintainRecapBlockPlan(
                new RecapBlockId("roleplay.self"),
                Target("roleplay.self"),
                "roleplay.autobiographical",
                RecapTestIdentity.CapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    replayStart,
                    fixture.Setups(replayStart)
                ),
                [new RecapReplayBoundary(offLineage, targetSetups)],
                EmptyRecapPriorContext.Instance
            ),
            "source" => new InheritRecapBlockPlan(
                new RecapBlockId("roleplay.self"),
                Target("roleplay.self"),
                offLineage,
                fixture.Setups(replayStart),
                new string('a', 64),
                new string('b', 64)
            ),
            "catch-up" => Maintain(
                fixture,
                target,
                replayStart,
                [target, target]
            ),
            "prior" => Maintain(
                fixture,
                target,
                replayStart,
                [target],
                new InlineRecapPriorContext(
                    target,
                    ContextHeaderSnapshot.Empty
                )
            ),
            _ => Maintain(fixture, target, replayStart, [target])
        };
        EventAddress admission = defectKind == "admission"
            ? offLineage
            : target;
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                admission,
                defectKind == "admission"
                    ? targetSetups
                    : fixture.Setups(admission),
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
            plan = Maintain(fixture, target, replayStart, [target]);
        }
        else {
            PublishedRecapDescriptor published =
                await fixture.PublishAsync(
                    source,
                    replayStart,
                    content: "source recap"
                );
            DerivedRecapFrozenInput input =
                RecapWireTestFacts.CreateFrozenInput(fixture.Engine,
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
                    input.AbsorbedThroughSetups,
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
                        input.AbsorbedThroughSetups,
                        published.EnvelopeSha256,
                        input.PayloadSha256
                    ),
                    [fixture.Boundary(target)],
                    EmptyRecapPriorContext.Instance
                );
        }
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
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
        RecapStoreFixture fixture,
        EventAddress target,
        EventAddress replayStart,
        IReadOnlyList<EventAddress> catchUpThrough,
        RecapPriorContext? priorContext = null
    ) => new(
        new RecapBlockId("roleplay.self"),
        Target("roleplay.self"),
        "roleplay.autobiographical",
        RecapTestIdentity.CapabilityFingerprint,
        new EmptyRecapMaintainSource(
            replayStart,
            fixture.Setups(replayStart)
        ),
        RecapWireTestFacts.ResolveBoundaries(
            fixture.Engine,
            catchUpThrough
        ),
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

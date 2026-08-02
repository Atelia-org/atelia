using System.Reflection;
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
    }

    [Fact]
    public void PlanUnionBaseConstructorsArePrivateProtected() {
        AssertPrivateProtectedConstructor(
            typeof(RecapBlockPlan),
            typeof(RecapBlockId),
            typeof(ContextHeaderBlockPath),
            typeof(int)
        );
        AssertPrivateProtectedConstructor(typeof(RecapMaintainSource));
        AssertPrivateProtectedConstructor(typeof(RecapPriorContext));
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
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress replayStart = lineage.HeadToRoot[4].Address;
        EventAddress offLineage = new(
            SizedPtr.FromPacked(ulong.MaxValue),
            uint.MaxValue,
            AddressHint.None
        );
        if (defectKind == "retroactive") {
            await fixture.PublishAsync(
                target,
                lineage.HeadToRoot[2].Address
            );
            target = lineage.HeadToRoot[2].Address;
            replayStart = lineage.HeadToRoot[4].Address;
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
                [lineage.HeadToRoot[2].Address]
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
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress source = lineage.HeadToRoot[2].Address;
        EventAddress replayStart = lineage.HeadToRoot[4].Address;
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

    private static void AssertPrivateProtectedConstructor(
        Type type,
        params Type[] parameterTypes
    ) {
        ConstructorInfo? constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null
        );
        Assert.NotNull(constructor);
        Assert.True(constructor.IsFamilyAndAssembly);
    }
}

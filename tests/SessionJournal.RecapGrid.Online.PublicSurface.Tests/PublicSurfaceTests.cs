using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Online.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public async Task ExternalCompositionCanOpenUseAndDisposeOnlineHandle() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-online-public-tests",
            Guid.NewGuid().ToString("N"));
        try {
            var estimator = new O200kBaseHistoryUnitLoadEstimator();
            using SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions("model", "system", "surface"));
            Assert.IsType<HistoryTimelineCreateResult.Created>(
                HistoryTimelineFactory.Create(
                    engine.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(1),
                        maxRawEvents: 64,
                        maxRenderedBytes: 1024 * 1024),
                    estimator));
            Assert.IsType<RecapGridCadenceCreateResult.Created>(
                RecapGridCadenceFactory.Create(
                    engine,
                    new RecapGridCadencePolicySpec(
                        minimumRecentHistoryLoad: 1,
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        targetHistoryLoad: 1,
                        maxRawEvents: 64,
                        maxRenderedBytes: 1024 * 1024)));
            Assert.IsType<RecapGridControlCreateResult.Created>(
                RecapGridControlFactory.Create(
                    path,
                    engine.BranchRefId,
                    new RecapGridControlAdmission(
                        RecapGridControlPermission.Create,
                        Array.Empty<FamilyDefinitionDigest>(),
                        Array.Empty<string>(),
                        Array.Empty<ContextHeaderCarrier>(),
                        ["surface."],
                        maximumBootstrapRows: 64,
                        maximumProjectedCalls: 64)));

            RecapGridOnlineOpenResult.Opened opened = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(
                    engine,
                    new RejectingExecutor(),
                    RecapGridOnlineLimits.Production,
                    estimator));
            await using RecapGridOnlineContextHandle handle = opened.Handle;
            EventAddress head = engine.ReadCurrentHead()!.Value;
            RecapGridOnlinePassResult result = await handle.PreparePassAsync(
                engine.ReadView,
                new SessionContextLifecycleRequest(
                    new SessionContextSelectionRequest(head, 0),
                    SessionExecutionPhase.Idle,
                    SessionContextLifecycleTrigger.PreObservation,
                    "pending"));

            Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(
                result);
            Assert.NotNull(handle.CandidateSource);
            Assert.Same(handle, handle.Lifecycle);
        }
        finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void CatchUpLimitsAreNotExported() {
        Assert.DoesNotContain(
            typeof(RecapGridOnlineFactory).Assembly.GetExportedTypes()
                .Where(static type => type.Namespace
                    is "Atelia.SessionJournal.RecapGrid.Online"),
            static type => type.Name is "RecapGridOnlineCatchUpLimits"
        );
    }

    [Fact]
    public void MaintenanceEvidenceHasConstructionFreePublicSurface() {
        Type type = typeof(RecapGridOnlineMaintenanceEvidence);
        const System.Reflection.BindingFlags PublicDeclaredInstance =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;
        const System.Reflection.BindingFlags NonPublicInstance =
            System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance;

        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(PublicDeclaredInstance));
        Assert.DoesNotContain(
            type.GetMethods(PublicDeclaredInstance),
            static method => method.Name == "Deconstruct"
        );

        (string Name, Type Type)[] expectedProperties = [
            ("Passes", typeof(int)),
            ("EntryDebt", typeof(bool)),
            ("TimelineRowsCommitted", typeof(int)),
            ("LastAttemptedRecipeRow",
                typeof(RecapGridRecipeRowCoordinate)),
            ("LastAttemptedAuthority",
                typeof(RecapGridBuildProgressAuthority)),
            ("RecipeRowSteps", typeof(int)),
            ("RowViewsCommitted", typeof(int)),
            ("CellsCommitted", typeof(int)),
            ("NewCalls", typeof(int)),
            ("NextRecipeRow", typeof(RecapGridRecipeRowCoordinate)),
            ("NextAuthority", typeof(RecapGridBuildProgressAuthority)),
            ("ContinuationKind", typeof(RecapGridOnlineContinuationKind))
        ];
        System.Reflection.PropertyInfo[] properties = type.GetProperties(
            PublicDeclaredInstance
        );
        Assert.Equal(
            expectedProperties.Select(static property => property.Name),
            properties.Select(static property => property.Name)
        );
        Assert.Equal(
            expectedProperties.Select(static property => property.Type),
            properties.Select(static property => property.PropertyType)
        );
        Assert.All(properties, static property => {
            Assert.NotNull(property.GetMethod);
            Assert.True(property.GetMethod!.IsPublic);
            Assert.Null(property.GetSetMethod(nonPublic: false));
        });

        Type[] argumentTypes = expectedProperties
            .Select(static property => property.Type)
            .ToArray();
        System.Reflection.ConstructorInfo? argumentConstructor = type
            .GetConstructor(
                NonPublicInstance,
                binder: null,
                argumentTypes,
                modifiers: null
            );
        Assert.NotNull(argumentConstructor);
        Assert.True(argumentConstructor!.IsAssembly);

        string[] expectedInternalInitProperties = [
            "NextRecipeRow",
            "NextAuthority",
            "ContinuationKind"
        ];
        System.Reflection.PropertyInfo[] internalInitProperties = properties
            .Where(static property => property.GetSetMethod(nonPublic: true)
                is not null)
            .ToArray();
        Assert.Equal(
            expectedInternalInitProperties,
            internalInitProperties.Select(static property => property.Name)
        );
        Assert.All(internalInitProperties, static property => {
            System.Reflection.MethodInfo setter =
                property.GetSetMethod(nonPublic: true)!;
            Assert.True(setter.IsAssembly);
            Assert.Contains(
                typeof(System.Runtime.CompilerServices.IsExternalInit),
                setter.ReturnParameter.GetRequiredCustomModifiers()
            );
        });
    }

    private sealed class RejectingExecutor : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "A raw-only public-surface fixture must not execute work.");
    }
}

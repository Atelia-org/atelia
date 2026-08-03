using System.Collections.Immutable;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapPlannerConfigResolverTests {
    private static readonly ContextHeaderBlockPath FirstTarget = new(
        ContextHeaderCarrier.System,
        "first"
    );
    private static readonly ContextHeaderBlockPath SecondTarget = new(
        ContextHeaderCarrier.System,
        "second"
    );

    [Fact]
    public void StandaloneConsumerResolvesInjectedCapabilitiesInConfigOrder()
    {
        var policy = new StubPolicy("custom-policy");
        var estimator = new StubEstimator("custom-estimator");
        var resolutionCatalog = new RecapPlannerConfigResolutionCatalog(
            [policy],
            [estimator]
        );
        RecapMaintainerCapabilitySnapshot capabilities = Capabilities();
        RecapPlannerConfigSnapshot snapshot =
            RecapPlannerConfigSnapshot.FromDocument(
                Document(
                    policyId: "custom-policy",
                    estimatorId: estimator.Id,
                    profiles: ["second-profile", "first-profile"]
                )
            );

        var resolved = Assert.IsType<
            RecapPlannerConfigResolveResult.Resolved
        >(RecapPlannerConfigResolver.Resolve(
            snapshot,
            resolutionCatalog,
            capabilities
        )).Configuration;

        Assert.Same(snapshot, resolved.Snapshot);
        Assert.Same(policy, resolved.PlanningInputs.Policy);
        Assert.Same(
            estimator,
            resolved.PlanningInputs.HistoryUnitLoadEstimator
        );
        Assert.Collection(
            resolved.ActiveProfiles,
            second => {
                Assert.Equal("second-profile", second.ProfileName);
                Assert.Equal("second", second.CatalogEntry.RecapBlockId.Value);
            },
            first => {
                Assert.Equal("first-profile", first.ProfileName);
                Assert.Equal("first", first.CatalogEntry.RecapBlockId.Value);
            }
        );
        Assert.Equal(
            resolved.ActiveProfiles.Select(
                static profile => profile.CatalogEntry
            ),
            resolved.PlanningInputs.OrderedCatalog
        );
        Assert.Equal(512, resolved.PlanningLimits.MaxRawEventsPerBuild);
    }

    [Theory]
    [InlineData("policy", "missing-policy", "estimator",
        RecapPlannerConfigResolveDefectCodes.UnknownPolicy)]
    [InlineData("estimator", "policy", "missing-estimator",
        RecapPlannerConfigResolveDefectCodes.UnknownEstimator)]
    [InlineData("profile", "policy", "estimator",
        RecapPlannerConfigResolveDefectCodes.UnknownProfile)]
    public void UnknownConfiguredIdentityIsTyped(
        string scenario,
        string policyId,
        string estimatorId,
        string expectedCode
    ) {
        var resolutionCatalog = new RecapPlannerConfigResolutionCatalog(
            [new StubPolicy()],
            [new StubEstimator("estimator")]
        );
        string[] profiles = scenario == "profile"
            ? ["missing-profile"]
            : ["first-profile"];

        AssertResolveCode(
            Document(policyId, estimatorId, profiles),
            resolutionCatalog,
            Capabilities(),
            expectedCode
        );
    }

    [Fact]
    public void ImplementationIdentityDriftFailsClosed() {
        var policy = new StubPolicy("policy");
        var estimator = new StubEstimator("estimator");
        var catalog = new RecapPlannerConfigResolutionCatalog(
            [policy],
            [estimator]
        );
        policy.Id = "drifted-policy";

        AssertResolveCode(
            Document("policy", "estimator", ["first-profile"]),
            catalog,
            Capabilities(),
            RecapPlannerConfigResolveDefectCodes.PolicyIdentityMismatch
        );

        policy.Id = "policy";
        estimator.Id = "drifted-estimator";
        AssertResolveCode(
            Document("policy", "estimator", ["first-profile"]),
            catalog,
            Capabilities(),
            RecapPlannerConfigResolveDefectCodes.EstimatorIdentityMismatch
        );
    }

    [Fact]
    public void ActiveRosterRejectsDuplicateResolvedBlockAndTarget() {
        RecapProfilePlanningDescriptor first = FirstCapability();
        var duplicateBlock = new RecapProfilePlanningDescriptor(
            "duplicate-block",
            first.RecapBlockId,
            SecondTarget,
            "maintainer-duplicate-block",
            RecapPlannerTestIdentity.CapabilityFingerprint
        );
        AssertResolveCode(
            Document(
                RecapPlanningPolicyIds.BoundedMaintainAllV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                [first.ProfileName, duplicateBlock.ProfileName]
            ),
            RecapPlannerConfigResolutionCatalog.BuiltIn,
            new([first, duplicateBlock]),
            RecapPlannerConfigResolveDefectCodes.DuplicateResolvedBlock
        );

        var duplicateTarget = new RecapProfilePlanningDescriptor(
            "duplicate-target",
            new RecapBlockId("duplicate-target"),
            first.Target,
            "maintainer-duplicate-target",
            RecapPlannerTestIdentity.CapabilityFingerprint
        );
        AssertResolveCode(
            Document(
                RecapPlanningPolicyIds.BoundedMaintainAllV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                [first.ProfileName, duplicateTarget.ProfileName]
            ),
            RecapPlannerConfigResolutionCatalog.BuiltIn,
            new([first, duplicateTarget]),
            RecapPlannerConfigResolveDefectCodes.DuplicateResolvedTarget
        );
    }

    [Fact]
    public void ProtocolHardCapOverflowIsInvalidPlanningAuthority() {
        RecapPlannerConfigDocument source = Document(
            RecapPlanningPolicyIds.BoundedMaintainAllV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            ["first-profile"]
        );

        AssertResolveCode(
            source with {
                Limits = source.Limits with {
                    MaxRawGrowthEventCount =
                        RecapProtocolHardCaps.V4
                            .MaxRawGrowthEventCount + 1
                }
            },
            RecapPlannerConfigResolutionCatalog.BuiltIn,
            Capabilities(),
            RecapPlannerConfigResolveDefectCodes
                .InvalidPlanningAuthority
        );
    }

    [Fact]
    public void CapabilitySnapshotSeparatesCatalogAndActiveRules() {
        RecapProfilePlanningDescriptor first = FirstCapability();
        var sameShape = new RecapProfilePlanningDescriptor(
            "next-first-profile",
            first.RecapBlockId,
            first.Target,
            "next-maintainer",
            RecapPlannerTestIdentity.CapabilityFingerprint
        );

        var snapshot = new RecapMaintainerCapabilitySnapshot([
            first,
            sameShape
        ]);

        Assert.Equal(2, snapshot.All.Count);
        Assert.True(snapshot.TryResolveProfileName(
            sameShape.ProfileName,
            out RecapProfilePlanningDescriptor resolved
        ));
        Assert.Same(sameShape, resolved);
        Assert.True(snapshot.SupportsFrozen(
            first.MaintainerId,
            first.Target,
            first.MaintainerCapabilityFingerprint
        ));

        Assert.Throws<ArgumentException>(() =>
            new RecapMaintainerCapabilitySnapshot([
                first,
                new RecapProfilePlanningDescriptor(
                    first.ProfileName,
                    first.RecapBlockId,
                    SecondTarget,
                    "another",
                    RecapPlannerTestIdentity.CapabilityFingerprint
                )
            ])
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapMaintainerCapabilitySnapshot([
                first,
                new RecapProfilePlanningDescriptor(
                    "another-profile",
                    new RecapBlockId("another"),
                    first.Target,
                    first.MaintainerId,
                    RecapPlannerTestIdentity.CapabilityFingerprint
                )
            ])
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapProfilePlanningDescriptor(
                "invalid-target",
                new RecapBlockId("invalid-target"),
                new ContextHeaderBlockPath(
                    (ContextHeaderCarrier)int.MaxValue,
                    "invalid-target"
                ),
                "invalid-target",
                RecapPlannerTestIdentity.CapabilityFingerprint
            )
        );
    }

    [Fact]
    public void SnapshotOwnsCanonicalDocumentBytesAndProvenance() {
        RecapPlannerConfigDocument source = Document(
            RecapPlanningPolicyIds.BoundedMaintainAllV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            ["first-profile"]
        );
        RecapPlannerConfigSnapshot anonymous =
            RecapPlannerConfigSnapshot.FromDocument(source);
        var available = new RecapPlannerConfigLoadResult.Available(
            "/repo/config/recap-planner-config.json",
            source,
            anonymous.CanonicalBytes,
            anonymous.ConfigSha256
        );

        RecapPlannerConfigSnapshot loaded =
            RecapPlannerConfigSnapshot.FromAvailable(available);

        Assert.Null(anonymous.CanonicalPath);
        Assert.Equal(available.Path, loaded.CanonicalPath);
        Assert.Same(source, loaded.Document);
        Assert.Equal(
            RecapPlannerConfigCodec.EncodeCanonical(source),
            loaded.CanonicalBytes.ToArray()
        );
        Assert.Equal(
            RecapPlannerConfigCodec.ComputeSha256(
                loaded.CanonicalBytes.AsSpan()
            ),
            loaded.ConfigSha256
        );

        Assert.Throws<ArgumentException>(() =>
            RecapPlannerConfigSnapshot.FromAvailable(
                available with {
                    CanonicalBytes = ImmutableArray.Create<byte>(1, 2, 3)
                }
            )
        );
    }

    [Fact]
    public void ResolutionCatalogFreezesInputsAndRejectsInvalidIds() {
        var policy = new StubPolicy();
        var estimator = new StubEstimator("estimator");
        var policies = new List<IRecapPlanningPolicy> {
            policy
        };
        var estimators = new List<IHistoryUnitLoadEstimator> {
            estimator
        };
        var snapshot = new RecapPlannerConfigResolutionCatalog(
            policies,
            estimators
        );
        policies.Clear();
        estimators.Clear();

        Assert.Single(snapshot.Policies);
        Assert.Single(snapshot.Estimators);
        Assert.Same(policy, snapshot.Policies[0]);
        Assert.Same(estimator, snapshot.Estimators[0]);
        Assert.Throws<ArgumentException>(() =>
            new RecapPlannerConfigResolutionCatalog(
                [
                    new StubPolicy("duplicate"),
                    new StubPolicy("duplicate")
                ],
                []
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapPlannerConfigResolutionCatalog(
                [],
                [
                    new StubEstimator("duplicate"),
                    new StubEstimator("duplicate")
                ]
            )
        );
        Assert.Throws<ArgumentNullException>(() =>
            new RecapPlannerConfigResolutionCatalog([null!], [])
        );
        Assert.Throws<ArgumentNullException>(() =>
            new RecapPlannerConfigResolutionCatalog([], [null!])
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapPlannerConfigResolutionCatalog(
                [new StubPolicy(" ")],
                []
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapPlannerConfigResolutionCatalog(
                [],
                [new StubEstimator("")]
            )
        );
    }

    private static RecapMaintainerCapabilitySnapshot Capabilities()
        => new([FirstCapability(), SecondCapability()]);

    private static RecapProfilePlanningDescriptor FirstCapability()
        => new(
            "first-profile",
            new RecapBlockId("first"),
            FirstTarget,
            "maintainer-first",
            RecapPlannerTestIdentity.CapabilityFingerprint
        );

    private static RecapProfilePlanningDescriptor SecondCapability()
        => new(
            "second-profile",
            new RecapBlockId("second"),
            SecondTarget,
            "maintainer-second",
            RecapPlannerTestIdentity.CapabilityFingerprint
        );

    private static RecapPlannerConfigDocument Document(
        string policyId,
        string estimatorId,
        IReadOnlyList<string> profiles
    ) => new(
        RecapPlannerConfigCodec.SchemaV2,
        policyId,
        new RecapCadenceConfigDocument(
            estimatorId,
            MinimumRecentHistoryLoad: 18_000,
            RecapBuildIntervalHistoryLoad: 21_000
        ),
        Array.AsReadOnly([
            .. profiles.Select(profile =>
                new RecapPlannerCatalogEntryDocument(
                    profile,
                    MaxContentUtf8Bytes: 32_768
                )
            )
        ]),
        new RecapPlannerLimitsDocument(
            MaxRawGrowthEventCount: 512,
            MaxRouteEndpointsPerBlock: 4,
            MaxMaintainerCallsPerBuild: 8,
            MaxRawEventsPerStep: 64,
            MaxRawEventsPerBuild: 512
        )
    );

    private static void AssertResolveCode(
        RecapPlannerConfigDocument document,
        RecapPlannerConfigResolutionCatalog resolutionCatalog,
        RecapMaintainerCapabilitySnapshot capabilities,
        string expectedCode
    ) {
        RecapPlannerConfigResolveResult result =
            RecapPlannerConfigResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(document),
                resolutionCatalog,
                capabilities
            );
        Assert.Equal(
            expectedCode,
            Assert.Single(
                Assert.IsType<
                    RecapPlannerConfigResolveResult.Invalid
                >(result).Defects
            ).Code
        );
    }

    private sealed class StubEstimator(string id)
        : IHistoryUnitLoadEstimator {
        public string Id { get; set; } = id;

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => throw new NotSupportedException(
            "Config resolution must not measure history."
        );
    }

    private sealed class StubPolicy(string id = "policy")
        : IRecapPlanningPolicy {
        public string Id { get; set; } = id;

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) => throw new NotSupportedException(
            "Config resolution must not execute policy."
        );
    }
}

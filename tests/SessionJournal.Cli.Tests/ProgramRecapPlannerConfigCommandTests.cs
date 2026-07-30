using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramRecapPlannerConfigCommandTests
    : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-planner-config-cli-tests",
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
    public void InitAndInspectUseOneCanonicalSnapshotWithoutClients() {
        string repository = NewRepository("healthy");
        string initReport = Path.Combine(_tempRoot, "init.json");
        var factory = new CountingCompletionClientFactory();

        Assert.Equal(0, Run([
            "recap", "planner-config", "init",
            "--input", repository,
            "--report-json", initReport
        ], factory));
        Assert.Equal(0, factory.CreateCallCount);

        string configPath =
            RecapPlannerConfigLoader.GetCanonicalPath(repository);
        byte[] expected = [
            .. RecapCliComposition.DefaultComposition
                .Snapshot.CanonicalBytes
        ];
        Assert.Equal(expected, File.ReadAllBytes(configPath));
        byte[] original = File.ReadAllBytes(configPath);

        string duplicateReport =
            Path.Combine(_tempRoot, "duplicate.json");
        Assert.Equal(2, Run([
            "recap", "planner-config", "init",
            "--input", repository,
            "--report-json", duplicateReport
        ], factory));
        Assert.Equal(original, File.ReadAllBytes(configPath));

        string inspectReport =
            Path.Combine(_tempRoot, "inspect.json");
        Assert.Equal(0, Run([
            "recap", "planner-config", "inspect",
            "--input", repository,
            "--report-json", inspectReport
        ], factory));
        Assert.Equal(0, factory.CreateCallCount);
        using JsonDocument report =
            JsonDocument.Parse(File.ReadAllBytes(inspectReport));
        Assert.Equal(
            "atelia.session-journal.recap-planner-config-operation.v1",
            String(report.RootElement, "schema")
        );
        Assert.Equal(
            "Resolved",
            String(report.RootElement, "status")
        );
        Assert.Equal(
            RecapPlannerConfigCodec.SchemaV1,
            String(report.RootElement, "configSchema")
        );
        Assert.Equal(
            RecapPlannerConfigCodec.ComputeSha256(expected),
            String(report.RootElement, "configSha256")
        );
        Assert.Equal(
            RecapPlanningPolicyIds.BoundedMaintainAllV1,
            String(report.RootElement, "planningPolicy")
        );
        JsonElement[] catalog = [
            .. report.RootElement
                .GetProperty("catalog")
                .EnumerateArray()
        ];
        Assert.Equal(2, catalog.Length);
        Assert.Equal(
            RecapMaintainerProfileCatalog
                .WorldUnderstandingRewrite,
            String(catalog[0], "maintainerProfile")
        );
        Assert.Equal(
            RecapMaintainerProfileCatalog
                .AutobiographicalRewrite,
            String(catalog[1], "maintainerProfile")
        );
        Assert.All(catalog, item => Assert.StartsWith(
            "sha256:",
            String(item, "promptFingerprint"),
            StringComparison.Ordinal
        ));
        Assert.False(Directory.Exists(Path.Combine(
            repository,
            "derived"
        )));
    }

    [Fact]
    public void InspectReturnsTypedMissingAndUnknownProfile() {
        var factory = new CountingCompletionClientFactory();
        string missingRepository = NewRepository("missing");
        Assert.Equal(2, Run([
            "recap", "planner-config", "inspect",
            "--input", missingRepository
        ], factory));

        string invalidRepository = NewRepository("invalid");
        WriteDocument(
            invalidRepository,
            BuiltInRecapPlannerConfig.Document with {
                Catalog = Array.AsReadOnly([
                    new RecapPlannerCatalogEntryDocument(
                        "not-installed",
                        32_768
                    )
                ])
            }
        );
        string reportPath = Path.Combine(
            _tempRoot,
            "unknown-profile.json"
        );
        Assert.Equal(2, Run([
            "recap", "planner-config", "inspect",
            "--input", invalidRepository,
            "--report-json", reportPath
        ], factory));
        using JsonDocument report =
            JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.Equal(
            "Invalid",
            String(report.RootElement, "status")
        );
        Assert.Equal(
            RecapPlannerConfigCodec.SchemaV1,
            String(report.RootElement, "configSchema")
        );
        Assert.False(string.IsNullOrWhiteSpace(
            String(report.RootElement, "configSha256")
        ));
        JsonElement defect = Assert.Single(
            report.RootElement
                .GetProperty("defects")
                .EnumerateArray()
        );
        Assert.Equal(
            RecapPlannerConfigResolveDefectCodes.UnknownProfile,
            String(defect, "code")
        );
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public void ResolverRejectsUnknownPolicyAndProtocolCapOverflow() {
        RecapPlannerConfigResolveResult unknownPolicy =
            RecapPlannerCompositionResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(
                    BuiltInRecapPlannerConfig.Document with {
                        PlanningPolicy = "unknown-policy"
                    }
                )
            );
        Assert.Equal(
            RecapPlannerConfigResolveDefectCodes.UnknownPolicy,
            Assert.Single(
                Assert.IsType<
                    RecapPlannerConfigResolveResult.Invalid
                >(unknownPolicy).Defects
            ).Code
        );

        RecapPlannerLimitsDocument source =
            BuiltInRecapPlannerConfig.Document.Limits;
        RecapPlannerConfigResolveResult overflow =
            RecapPlannerCompositionResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(
                    BuiltInRecapPlannerConfig.Document with {
                        Limits = source with {
                            MaxRawGrowthEventCount =
                                RecapProtocolHardCaps.V4
                                    .MaxRawGrowthEventCount + 1
                        }
                    }
                )
            );
        Assert.Equal(
            RecapPlannerConfigResolveDefectCodes
                .InvalidPlanningAuthority,
            Assert.Single(
                Assert.IsType<
                    RecapPlannerConfigResolveResult.Invalid
                >(overflow).Defects
            ).Code
        );
    }

    [Fact]
    public void ResolverRejectsDuplicateResolvedBlockAndTarget() {
        RecapMaintainerProfileDescriptor world =
            RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                RecapMaintainerProfileCatalog
                    .WorldUnderstandingRewrite
            );
        RecapMaintainerProfileDescriptor autobiography =
            RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                RecapMaintainerProfileCatalog
                    .AutobiographicalRewrite
            );
        var duplicateBlock = autobiography with {
            ProfileName = "duplicate-block",
            RecapBlockIdValue = world.RecapBlockIdValue
        };
        AssertResolveCode(
            CreateTwoProfileDocument(
                world.ProfileName,
                duplicateBlock.ProfileName
            ),
            new RecapMaintainerProfileCatalog([
                world,
                duplicateBlock
            ]),
            RecapPlannerConfigResolveDefectCodes
                .DuplicateResolvedBlock
        );

        var duplicateTarget = autobiography with {
            ProfileName = "duplicate-target",
            RewriteProfile =
                autobiography.RewriteProfile with {
                    Target = world.Target
                }
        };
        AssertResolveCode(
            CreateTwoProfileDocument(
                world.ProfileName,
                duplicateTarget.ProfileName
            ),
            new RecapMaintainerProfileCatalog([
                world,
                duplicateTarget
            ]),
            RecapPlannerConfigResolveDefectCodes
                .DuplicateResolvedTarget
        );
    }

    [Fact]
    public void DefaultCompositionIsStableInitializerSnapshot() {
        ResolvedRecapPlannerComposition first =
            RecapCliComposition.DefaultComposition;
        ResolvedRecapPlannerComposition second =
            RecapCliComposition.DefaultComposition;

        Assert.Same(first, second);
        Assert.Null(first.Snapshot.CanonicalPath);
        Assert.Equal(
            BuiltInRecapPlannerConfig.Document,
            first.Snapshot.Document
        );
    }

    private string NewRepository(string name) {
        string path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static RecapPlannerConfigDocument
        CreateTwoProfileDocument(
        string first,
        string second
    ) => BuiltInRecapPlannerConfig.Document with {
        Catalog = Array.AsReadOnly([
            new RecapPlannerCatalogEntryDocument(first, 32_768),
            new RecapPlannerCatalogEntryDocument(second, 32_768)
        ])
    };

    private static void AssertResolveCode(
        RecapPlannerConfigDocument document,
        RecapMaintainerProfileCatalog capabilities,
        string expectedCode
    ) {
        RecapPlannerConfigResolveResult result =
            RecapPlannerCompositionResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(document),
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

    private static void WriteDocument(
        string repository,
        RecapPlannerConfigDocument document
    ) {
        string path =
            RecapPlannerConfigLoader.GetCanonicalPath(repository);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        File.WriteAllBytes(
            path,
            RecapPlannerConfigCodec.EncodeCanonical(document)
        );
    }

    private static int Run(
        string[] args,
        ICompletionClientFactory factory
    ) => Program.MainCore(args, factory);

    private static string String(
        JsonElement element,
        string property
    ) => element.GetProperty(property).GetString()!;

    private sealed class CountingCompletionClientFactory
        : ICompletionClientFactory {
        internal int CreateCallCount { get; private set; }

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreateCallCount++;
            throw new InvalidOperationException(
                $"planner-config command must not create client "
                + $"'{connection.Id}'."
            );
        }
    }
}

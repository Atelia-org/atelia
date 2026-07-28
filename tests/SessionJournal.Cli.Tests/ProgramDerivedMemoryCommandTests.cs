using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedMemory;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramDerivedMemoryCommandTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-derived-memory-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for test-owned repositories.
        }
    }

    [Fact]
    public async Task PublishListValidate_ProviderSelectsAndRawRemainsExact() {
        Fixture fixture = await CreateFixtureAsync();
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string publishReport = Path.Combine(_tempRoot, "publish.json");
        string listReport = Path.Combine(_tempRoot, "list.json");
        string validateReport = Path.Combine(_tempRoot, "validate.json");

        Assert.Equal(
            0,
            Run(PublishArgs(fixture, "none", publishReport))
        );
        Assert.Equal(
            0,
            Run([
                "list-derived-artifact-sets",
                "--input", fixture.Path,
                "--report-json", listReport
            ])
        );
        Assert.Equal(
            0,
            Run([
                "validate-derived-memory",
                "--input", fixture.Path,
                "--report-json", validateReport
            ])
        );

        using JsonDocument publish = JsonDocument.Parse(
            File.ReadAllText(publishReport)
        );
        Assert.Equal(
            "atelia.session-journal.cli.derived-artifact-set-operation.v1",
            publish.RootElement.GetProperty("schema").GetString()
        );
        Assert.Equal(
            "publish",
            publish.RootElement.GetProperty("operation").GetString()
        );
        string setId = publish.RootElement
            .GetProperty("set")
            .GetProperty("setId")
            .GetString()!;
        Assert.StartsWith("das_", setId, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "derived alpha text",
            File.ReadAllText(publishReport),
            StringComparison.Ordinal
        );
        using JsonDocument list = JsonDocument.Parse(
            File.ReadAllText(listReport)
        );
        Assert.Equal(
            "atelia.session-journal.cli.derived-artifact-set-inventory.v1",
            list.RootElement.GetProperty("schema").GetString()
        );
        Assert.Equal(1, list.RootElement.GetProperty("sets").GetArrayLength());
        Assert.Equal(
            1,
            list.RootElement.GetProperty("latestPointers").GetArrayLength()
        );
        using JsonDocument validation = JsonDocument.Parse(
            File.ReadAllText(validateReport)
        );
        Assert.Equal(
            "atelia.session-journal.cli.derived-memory-validation.v2",
            validation.RootElement.GetProperty("schema").GetString()
        );
        Assert.Equal(
            2,
            validation.RootElement.GetProperty("artifactCount").GetInt32()
        );

        var provider = new DerivedArtifactSetContextCandidateSource(
            fixture.Repository,
            fixture.Policy,
            fixture.LineageKey
        );
        SJ.SessionContextCandidateDiscovery discovery =
            await provider.DiscoverAsync(
                new SJ.SessionContextSelectionRequest(
                    fixture.Anchor,
                    SJ.SessionContextSelectionMode.Latest,
                    fixture.Policy.CoherenceGroup
                ),
                CancellationToken.None
            );
        SJ.SessionContextCandidateDescriptor descriptor = Assert.Single(
            discovery.Candidates
        );
        SJ.SessionContextCandidate candidate =
            await provider.MaterializeAsync(
                descriptor,
                CancellationToken.None
            );
        Assert.Equal(2, candidate.Contributions.Count);
        Assert.Equal(before, ReadRawSnapshot(fixture.Path));
        Assert.Equal(0, before.UnknownEventKindCount);
        Assert.Empty(Directory.EnumerateFiles(
            _tempRoot,
            ".*.tmp",
            SearchOption.AllDirectories
        ));
    }

    [Fact]
    public async Task ConfigurePlanListEpochs_AreContentFreeAndRawExact() {
        Fixture fixture = await CreateFixtureAsync();
        Directory.Delete(fixture.Repository.DerivedRoot, recursive: true);
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string configReport = Path.Combine(_tempRoot, "config.json");
        string planReport = Path.Combine(_tempRoot, "epoch.json");
        string listReport = Path.Combine(_tempRoot, "epochs.json");
        string validateReport = Path.Combine(
            _tempRoot,
            "epoch-validation.json"
        );

        Assert.Equal(0, Run([
            "configure-derived-artifact-planner",
            "--input", fixture.Path,
            "--lineage", "main",
            "--coherence-group", "memory-pack",
            "--topology-version", "topology-v1",
            "--minimum-recent-tokens", "1",
            "--epoch-trigger-tokens", "1",
            "--scheduling-headroom-tokens", "1",
            "--hard-limit-tokens", "100",
            "--expected-current", "none",
            "--report-json", configReport
        ]));
        Assert.Equal(0, Run([
            "plan-derived-artifact-epoch",
            "--input", fixture.Path,
            "--lineage", "main",
            "--coherence-group", "memory-pack",
            "--expected-previous", "none",
            "--input-set", "none",
            "--report-json", planReport
        ]));
        Assert.Equal(0, Run([
            "list-derived-artifact-epochs",
            "--input", fixture.Path,
            "--report-json", listReport
        ]));
        Assert.Equal(0, Run([
            "validate-derived-memory",
            "--input", fixture.Path,
            "--report-json", validateReport
        ]));

        using JsonDocument plan = JsonDocument.Parse(
            File.ReadAllText(planReport)
        );
        Assert.Equal(
            "atelia.session-journal.cli.derived-artifact-epoch-operation.v1",
            plan.RootElement.GetProperty("schema").GetString()
        );
        Assert.StartsWith(
            "dae_",
            plan.RootElement.GetProperty("epoch")
                .GetProperty("epochId")
                .GetString(),
            StringComparison.Ordinal
        );
        string reports = File.ReadAllText(configReport)
            + File.ReadAllText(planReport)
            + File.ReadAllText(listReport)
            + File.ReadAllText(validateReport);
        Assert.DoesNotContain(
            "old observation",
            reports,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "old action",
            reports,
            StringComparison.Ordinal
        );
        using JsonDocument validation = JsonDocument.Parse(
            File.ReadAllText(validateReport)
        );
        Assert.Equal(
            "atelia.session-journal.cli.derived-memory-validation.v2",
            validation.RootElement.GetProperty("schema").GetString()
        );
        Assert.Equal(
            1,
            validation.RootElement
                .GetProperty("plannerConfigCount")
                .GetInt32()
        );
        Assert.Equal(
            1,
            validation.RootElement
                .GetProperty("artifactEpochCount")
                .GetInt32()
        );
        Assert.Equal(before, ReadRawSnapshot(fixture.Path));
        Assert.Empty(Directory.EnumerateFiles(
            _tempRoot,
            ".*.tmp",
            SearchOption.AllDirectories
        ));
    }

    [Fact]
    public async Task EpochCommands_RejectBadNumbersAndMixedGenesis() {
        Fixture fixture = await CreateFixtureAsync();
        Directory.Delete(fixture.Repository.DerivedRoot, recursive: true);
        string[] configure = [
            "configure-derived-artifact-planner",
            "--input", fixture.Path,
            "--lineage", "main",
            "--coherence-group", "memory-pack",
            "--topology-version", "topology-v1",
            "--minimum-recent-tokens", "not-a-number",
            "--epoch-trigger-tokens", "1",
            "--scheduling-headroom-tokens", "1",
            "--hard-limit-tokens", "100",
            "--expected-current", "none"
        ];
        Assert.Equal(1, Run(configure));
        Assert.Equal(1, Run([
            "plan-derived-artifact-epoch",
            "--input", fixture.Path,
            "--lineage", "main",
            "--coherence-group", "memory-pack",
            "--expected-previous", "none",
            "--input-set", "das_" + new string('a', 64)
        ]));
        Assert.False(Directory.Exists(
            fixture.Repository.EpochPlanner.EpochsDirectory
        ));
    }

    [Fact]
    public async Task Publish_StaleCasFailsWithoutChangingRawOrReport() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        DerivedMemoryArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "replacement",
            fixture.Policy.Roles[0].Target,
            "replacement text",
            fixture.Epoch,
            fixture.Anchor,
            fixture.Setups,
            fixture.Policy.Roles[0].RoleId
        );
        string reportPath = Path.Combine(_tempRoot, "must-not-exist.json");
        string[] args = PublishArgs(
            fixture with {
                FirstArtifactId = replacement.ArtifactId
            },
            "none",
            reportPath
        );

        Assert.Equal(1, Run(args));
        Assert.False(File.Exists(reportPath));
        Assert.Equal(before, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task MissingPointer_ListDiagnosesValidateFailsAndRebuildRestores() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));

        Assert.Equal(
            0,
            Run([
                "list-derived-artifact-sets",
                "--input", fixture.Path
            ])
        );
        Assert.Equal(
            1,
            Run([
                "validate-derived-memory",
                "--input", fixture.Path
            ])
        );
        Assert.Equal(0, Run(RebuildArgs(fixture)));
        Assert.Equal(
            0,
            Run([
                "validate-derived-memory",
                "--input", fixture.Path
            ])
        );
    }

    [Fact]
    public async Task ImmutableSettlementPreventsForkAndRebuildRestores() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        string pointer = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        File.Delete(pointer);
        DerivedMemoryArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "fork",
            fixture.Policy.Roles[0].Target,
            "fork text",
            fixture.Epoch,
            fixture.Anchor,
            fixture.Setups,
            fixture.Policy.Roles[0].RoleId
        );
        Assert.Equal(
            1,
            Run(PublishArgs(
                fixture with {
                    FirstArtifactId = replacement.ArtifactId
                },
                "none"
            ))
        );

        Assert.Equal(
            0,
            Run([
                "list-derived-artifact-sets",
                "--input", fixture.Path
            ])
        );
        Assert.Equal(
            1,
            Run([
                "validate-derived-memory",
                "--input", fixture.Path
            ])
        );
        Assert.Equal(0, Run(RebuildArgs(fixture)));
        Assert.Equal(
            0,
            Run([
                "validate-derived-memory",
                "--input", fixture.Path
            ])
        );
    }

    [Theory]
    [InlineData("malformed-artifact")]
    [InlineData("malformed-set")]
    [InlineData("oversize-pointer")]
    public async Task List_CorruptOrOversizePersistedStateExitsOne(
        string corruption
    ) {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        if (corruption == "malformed-artifact") {
            await File.WriteAllTextAsync(
                Path.Combine(
                    fixture.Repository.Artifacts.ArtifactsDirectory,
                    $"{fixture.FirstArtifactId}.json"
                ),
                "{ malformed"
            );
        }
        else if (corruption == "malformed-set") {
            await File.WriteAllTextAsync(
                Assert.Single(Directory.EnumerateFiles(
                    fixture.Repository.ArtifactSets.SetsDirectory
                )),
                "{ malformed"
            );
        }
        else {
            await File.WriteAllTextAsync(
                Assert.Single(Directory.EnumerateFiles(
                    fixture.Repository.ArtifactSets
                        .LatestPointersDirectory
                )),
                new string(
                    'x',
                    checked((int)
                        DerivedArtifactSetStore
                            .MaxLatestPointerFileBytes + 1)
                )
            );
        }

        Assert.Equal(
            1,
            Run([
                "list-derived-artifact-sets",
                "--input", fixture.Path
            ])
        );
    }

    [Fact]
    public async Task StrictGrammarRejectsUnknownDuplicateMissingAndBadRole() {
        Fixture fixture = await CreateFixtureAsync();
        string[] valid = PublishArgs(fixture, "none");

        Assert.Equal(1, Run([.. valid, "--unknown", "value"]));
        Assert.Equal(
            1,
            Run([.. valid, "--lineage", "duplicate"])
        );
        Assert.Equal(
            1,
            Run([
                .. valid.Where(
                    static value => value != "--member"
                        && !value.StartsWith("alpha-role=", StringComparison.Ordinal)
                        && !value.StartsWith("zeta-role=", StringComparison.Ordinal)
                )
            ])
        );
        int roleIndex = Array.IndexOf(valid, "--required-role") + 1;
        string[] badRole = [.. valid];
        badRole[roleIndex] = "alpha-role=unknown/block";
        Assert.Equal(1, Run(badRole));
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
    }

    [Fact]
    public async Task ReportMustBeOutsideRepositoryAndRebuildNeedsMatchingSets() {
        Fixture fixture = await CreateFixtureAsync();
        string inside = Path.Combine(fixture.Path, "report.json");

        Assert.Equal(
            1,
            Run(PublishArgs(fixture, "none", inside))
        );
        Assert.False(File.Exists(inside));
        Assert.Equal(1, Run(RebuildArgs(fixture)));
    }

    [Fact]
    public async Task InputAndReportPathChainsRejectSymbolicLinks() {
        Fixture fixture = await CreateFixtureAsync();
        string inputLink = Path.Combine(_tempRoot, "repo-link");
        string reportDirectory = Path.Combine(_tempRoot, "real-reports");
        string reportLink = Path.Combine(_tempRoot, "report-link");
        Directory.CreateDirectory(reportDirectory);
        try {
            Directory.CreateSymbolicLink(inputLink, fixture.Path);
            Directory.CreateSymbolicLink(reportLink, reportDirectory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException
        ) {
            return;
        }

        Assert.Equal(
            1,
            Run([
                "list-derived-artifact-sets",
                "--input", inputLink
            ])
        );
        string reportPath = Path.Combine(reportLink, "inventory.json");
        Assert.Equal(
            1,
            Run([
                "list-derived-artifact-sets",
                "--input", fixture.Path,
                "--report-json", reportPath
            ])
        );
        Assert.False(File.Exists(Path.Combine(
            reportDirectory,
            "inventory.json"
        )));
    }

    [Theory]
    [InlineData("sets", "list")]
    [InlineData("pointers", "list")]
    [InlineData("artifacts", "validate")]
    public async Task EmptyInternalStoreSymlinkExitsOne(
        string targetKind,
        string command
    ) {
        Fixture fixture = await CreateFixtureAsync();
        string targetPath = targetKind switch {
            "sets" => fixture.Repository.ArtifactSets.SetsDirectory,
            "pointers" =>
                fixture.Repository.ArtifactSets.LatestPointersDirectory,
            "artifacts" => fixture.Repository.Artifacts.ArtifactsDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
        };
        if (Directory.Exists(targetPath)) {
            Directory.Delete(targetPath, recursive: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string external = Path.Combine(
            _tempRoot,
            $"external-{targetKind}"
        );
        Directory.CreateDirectory(external);
        try {
            Directory.CreateSymbolicLink(targetPath, external);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException
        ) {
            return;
        }

        Assert.Equal(
            1,
            Run([
                command == "list"
                    ? "list-derived-artifact-sets"
                    : "validate-derived-memory",
                "--input", fixture.Path
            ])
        );
    }

    [Theory]
    [InlineData("address")]
    [InlineData("token")]
    [InlineData("target")]
    public async Task PersistedShapeFailureHasStableExitOne(
        string corruption
    ) {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        string setPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        JsonNode root = JsonNode.Parse(
            await File.ReadAllTextAsync(setPath)
        )!;
        switch (corruption) {
            case "address":
                root["commonAnchor"] = "bad-address";
                break;
            case "token":
                root["policyId"] = "";
                break;
            case "target":
                root["roleRequirements"]![0]!["target"]!["blockKey"] = "";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        await File.WriteAllTextAsync(setPath, root.ToJsonString());

        Assert.Equal(
            1,
            Run([
                "list-derived-artifact-sets",
                "--input", fixture.Path
            ])
        );
        Assert.Equal(
            1,
            Run([
                "validate-derived-memory",
                "--input", fixture.Path
            ])
        );
    }

    [Fact]
    public async Task InlineSyntaxCarriesLeadingDashIdentitiesAndRoles() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedMemoryArtifact alpha = await WriteArtifactAsync(
            fixture.Repository,
            "inline-alpha",
            fixture.Policy.Roles[0].Target,
            "inline alpha",
            fixture.Epoch,
            fixture.Anchor,
            fixture.Setups,
            "--alpha"
        );
        DerivedMemoryArtifact zeta = await WriteArtifactAsync(
            fixture.Repository,
            "inline-zeta",
            fixture.Policy.Roles[1].Target,
            "inline zeta",
            fixture.Epoch,
            fixture.Anchor,
            fixture.Setups,
            "--zeta"
        );
        var policy = new DerivedArtifactSetPolicy(
            "--policy",
            "--fingerprint",
            fixture.Epoch.CoherenceGroup,
            [
                new DerivedArtifactSetRoleRequirement(
                    "--alpha",
                    fixture.Policy.Roles[0].Target
                ),
                new DerivedArtifactSetRoleRequirement(
                    "--zeta",
                    fixture.Policy.Roles[1].Target
                )
            ]
        );
        DerivedMemoryOrchestrationTransaction transaction =
            await DerivedArtifactSetTestFactoryForCli
                .CreateSettledTransactionAsync(
                    fixture.Repository,
                    fixture.Epoch,
                    policy,
                    [alpha, zeta]
                );

        int exitCode = Run([
            "publish-derived-artifact-set",
            $"--input={fixture.Path}",
            $"--transaction={transaction.TransactionId}",
            $"--member=--alpha={alpha.ArtifactId}",
            $"--member=--zeta={zeta.ArtifactId}"
        ]);

        Assert.Equal(0, exitCode);
        DerivedArtifactSet set = Assert.Single(
            (await fixture.Repository.ArtifactSets.ReadInventoryAsync())
                .Sets
        );
        Assert.Equal(fixture.LineageKey, set.LineageKey);
        Assert.Equal(fixture.Epoch.CoherenceGroup, set.CoherenceGroup);
        Assert.Equal("--policy", set.PolicyId);
        Assert.Equal(
            ["--alpha", "--zeta"],
            set.RoleRequirements.Select(static role => role.RoleId)
        );
    }

    [Fact]
    public void InlineSyntaxPreservesLeadingDashPathAndBlockValues() {
        CliOptions options = CliOptions.Parse([
            "--input=--repo",
            "--lineage=--lineage",
            "--required-role=--role=observation/--block"
        ]);

        options.EnsureOnly("input", "lineage", "required-role");
        Assert.Equal("--repo", options.RequireSingle("input"));
        Assert.Equal("--lineage", options.RequireSingle("lineage"));
        Assert.Equal(
            "--role=observation/--block",
            Assert.Single(options.RequireRepeated("required-role"))
        );
    }

    [Fact]
    public async Task OrchestrationCommandRunsTwoProfilesAndPublishesSet() {
        Fixture fixture = await CreateFixtureAsync();
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string connectionsPath = Path.Combine(
            _tempRoot,
            "orchestration-connections.json"
        );
        string outputPath = Path.Combine(
            _tempRoot,
            "orchestration-result.json"
        );
        string callLogDir = Path.Combine(
            _tempRoot,
            "orchestration-calls"
        );
        WriteConnections(connectionsPath);
        var factory = new ConcurrentScriptedCompletionClientFactory(
            "rewritten memory"
        );

        int exitCode = Program.MainCore([
            "run-derived-memory-orchestration",
            "--input", fixture.Path,
            "--epoch", fixture.Epoch.EpochId,
            "--role",
            "required:autobiographical-rewrite:produce",
            "--role",
            "required:world-understanding-rewrite:produce",
            "--policy-id", "daily-memory",
            "--policy-fingerprint", "daily-memory-v1",
            "--output", outputPath,
            "--connections", connectionsPath,
            "--call-log-dir", callLogDir
        ], factory);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, factory.CompletionCallCount);
        Assert.True(File.Exists(outputPath));
        DerivedArtifactSet set = Assert.Single(
            (await fixture.Repository.ArtifactSets.ReadInventoryAsync())
                .Sets
        );
        Assert.Equal(
            ["autobiography", "world-understanding"],
            set.Members.Select(static member => member.RoleId)
        );
        Assert.Equal(before, ReadRawSnapshot(fixture.Path));
    }

    [Fact]
    public async Task IdentityOnlyOrchestrationNeedsNoConnection() {
        Fixture fixture = await CreateFixtureAsync();
        string outputPath = Path.Combine(
            _tempRoot,
            "identity-orchestration-result.json"
        );

        int exitCode = Program.MainCore([
            "run-derived-memory-orchestration",
            "--input", fixture.Path,
            "--epoch", fixture.Epoch.EpochId,
            "--role",
            "required:autobiographical-rewrite:identity",
            "--role",
            "required:world-understanding-rewrite:identity",
            "--policy-id", "identity-memory",
            "--policy-fingerprint", "identity-memory-v1",
            "--output", outputPath
        ], ThrowingCompletionClientFactory.Instance);

        Assert.Equal(0, exitCode);
        DerivedArtifactSet set = Assert.Single(
            (await fixture.Repository.ArtifactSets.ReadInventoryAsync())
                .Sets
        );
        Assert.All(set.Members, member => Assert.Equal(
            DerivedMemoryArtifactOutcomes.Identity,
            member.Outcome
        ));
    }

    [Fact]
    public void OrchestrationOutputCannotOverwriteConnections() {
        Directory.CreateDirectory(_tempRoot);
        string connectionsPath = Path.Combine(
            _tempRoot,
            "orchestration-protected-connections.json"
        );
        string callsPath = Path.Combine(_tempRoot, "protected-calls");
        WriteConnections(connectionsPath);
        byte[] original = File.ReadAllBytes(connectionsPath);
        var factory = new ConcurrentScriptedCompletionClientFactory(
            "must-not-run"
        );

        int exitCode = Program.MainCore([
            "run-derived-memory-orchestration",
            "--input", Path.Combine(_tempRoot, "missing-journal"),
            "--epoch", "dae_" + new string('1', 64),
            "--role",
            "required:autobiographical-rewrite:produce",
            "--policy-id", "daily-memory",
            "--policy-fingerprint", "daily-memory-v1",
            "--output", connectionsPath,
            "--connections", connectionsPath,
            "--call-log-dir", callsPath
        ], factory);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.Equal(original, File.ReadAllBytes(connectionsPath));
        Assert.False(Directory.Exists(callsPath));
    }

    [Fact]
    public async Task DuplicateOrchestrationRoleFailsBeforeDirectoriesOrClient() {
        Fixture fixture = await CreateFixtureAsync();
        string connectionsPath = Path.Combine(
            _tempRoot,
            "duplicate-role-connections.json"
        );
        string outputPath = Path.Combine(
            _tempRoot,
            "duplicate-role-output.json"
        );
        string callsPath = Path.Combine(
            _tempRoot,
            "duplicate-role-calls"
        );
        WriteConnections(connectionsPath);
        var factory = new ConcurrentScriptedCompletionClientFactory(
            "must-not-run"
        );

        int exitCode = Program.MainCore([
            "run-derived-memory-orchestration",
            "--input", fixture.Path,
            "--epoch", fixture.Epoch.EpochId,
            "--role",
            "required:autobiographical-rewrite:produce",
            "--role",
            "required:autobiographical-rewrite:produce",
            "--policy-id", "daily-memory",
            "--policy-fingerprint", "daily-memory-v1",
            "--output", outputPath,
            "--connections", connectionsPath,
            "--call-log-dir", callsPath
        ], factory);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(callsPath));
    }

    [Fact]
    public async Task OnlineTurnRunsPendingMaintenanceAndAgentCompletion() {
        Fixture fixture = await CreateFixtureAsync();
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string connectionsPath = Path.Combine(
            _tempRoot,
            "online-turn-connections.json"
        );
        string outputPath = Path.Combine(
            _tempRoot,
            "online-turn-result.json"
        );
        string callLogDir = Path.Combine(
            _tempRoot,
            "online-turn-calls"
        );
        WriteConnections(connectionsPath);
        var factory = new ConcurrentScriptedCompletionClientFactory(
            "rewritten memory"
        );

        int exitCode = Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--message", "new observation",
            "--role",
            "required:autobiographical-rewrite:produce",
            "--role",
            "required:world-understanding-rewrite:produce",
            "--policy-id", "daily-memory",
            "--policy-fingerprint", "daily-memory-v1",
            "--connections", connectionsPath,
            "--call-log-dir", callLogDir,
            "--output", outputPath,
            "--coherence-group", "test-group",
            "--selection", "latest",
            "--bootstrap-budget", "1000"
        ], factory);

        Assert.Equal(0, exitCode);
        // The fixture starts with one pending epoch. Preflight settles it,
        // then the intentionally tiny planner threshold schedules the newly
        // appended observation as a second epoch before the agent completion.
        Assert.Equal(5, factory.CompletionCallCount);
        Assert.True(File.Exists(outputPath));
        using JsonDocument output = JsonDocument.Parse(
            File.ReadAllText(outputPath)
        );
        Assert.Equal(
            "atelia.session-journal.online-turn-run.v1",
            output.RootElement.GetProperty("schema").GetString()
        );
        Assert.NotEqual(
            before.Head,
            ReadRawSnapshot(fixture.Path).Head
        );
        Assert.Equal(
            2,
            (await fixture.Repository.ArtifactSets
                .ReadInventoryAsync()).Sets.Count
        );
    }

    [Fact]
    public async Task DuplicateOnlineRoleFailsBeforeDirectoriesOrClient() {
        Fixture fixture = await CreateFixtureAsync();
        string connectionsPath = Path.Combine(
            _tempRoot,
            "duplicate-online-role-connections.json"
        );
        string outputPath = Path.Combine(
            _tempRoot,
            "duplicate-online-role-output.json"
        );
        string callsPath = Path.Combine(
            _tempRoot,
            "duplicate-online-role-calls"
        );
        WriteConnections(connectionsPath);
        var factory = new ConcurrentScriptedCompletionClientFactory(
            "must-not-run"
        );

        int exitCode = Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--message", "new observation",
            "--role",
            "required:autobiographical-rewrite:produce",
            "--role",
            "required:autobiographical-rewrite:produce",
            "--policy-id", "daily-memory",
            "--policy-fingerprint", "daily-memory-v1",
            "--connections", connectionsPath,
            "--call-log-dir", callsPath,
            "--output", outputPath,
            "--coherence-group", "test-group"
        ], factory);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(callsPath));
    }

    [Fact]
    public async Task OnlineTurnDefaultsToRefuseThenExplicitlyReopensPrepared() {
        Fixture fixture = await CreateFixtureAsync();
        string connectionsPath = Path.Combine(
            _tempRoot,
            "online-reopen-connections.json"
        );
        string outputPath = Path.Combine(
            _tempRoot,
            "online-reopen-result.json"
        );
        string callLogDir = Path.Combine(
            _tempRoot,
            "online-reopen-calls"
        );
        WriteConnections(connectionsPath);
        var factory = new ConcurrentScriptedCompletionClientFactory(
            "rewritten memory",
            failAtCall: 5
        );
        string[] args = [
            "run-online-turn",
            "--input", fixture.Path,
            "--message", "new observation",
            "--role",
            "required:autobiographical-rewrite:produce",
            "--role",
            "required:world-understanding-rewrite:produce",
            "--policy-id", "daily-memory",
            "--policy-fingerprint", "daily-memory-v1",
            "--connections", connectionsPath,
            "--call-log-dir", callLogDir,
            "--output", outputPath,
            "--coherence-group", "test-group",
            "--uncertain-recovery", "restart-new-attempt"
        ];

        Assert.Equal(1, Program.MainCore(args, factory));
        Assert.Equal(5, factory.CompletionCallCount);
        Assert.False(File.Exists(outputPath));

        Assert.Equal(
            1,
            Program.MainCore(args[..^2], factory)
        );
        Assert.Equal(5, factory.CompletionCallCount);
        Assert.False(File.Exists(outputPath));

        Assert.Equal(0, Program.MainCore(args, factory));
        Assert.Equal(6, factory.CompletionCallCount);
        Assert.True(File.Exists(outputPath));
        using SJ.SessionJournalEngine reopened =
            SJ.SessionJournalEngine.Open(fixture.Path);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            reopened.InspectExecutionBoundary().Phase
        );
    }

    private async ValueTask<Fixture> CreateFixtureAsync() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(
            _tempRoot,
            Guid.NewGuid().ToString("N")
        );
        EventAddress anchor;
        SJ.SessionContextAnchorSetupReferences setups;
        DerivedArtifactEpochPlan epoch;
        DerivedMemoryRepository repository;
        using (var engine = SJ.SessionJournalEngine.Create(
            path,
            new SJ.SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            for (int index = 0; index < 5; index++) {
                engine.AppendObservation($"old observation {index}");
                _ = engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"old action {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-A"
                    )
                );
            }
            repository = DerivedMemoryRepository.Open(path);
            _ = await repository.EpochPlanner.ConfigureAsync(
                new DerivedArtifactPlannerConfigDefinition(
                    "main",
                    "test-group",
                    "topology-v1",
                    1,
                    1,
                    1,
                    1_000
                ),
                null
            );
            epoch = (await repository.EpochPlanner.PlanAsync(
                engine,
                new("main", "test-group", null, null)
            )).Epoch!;
            anchor = epoch.SourceEndInclusive;
            setups = engine.ResolveContextAnchorSetupReferences(anchor);
        }
        var firstTarget = new SJ.MemoryPackBlockPath(
            SJ.MemoryPackCarrier.Observation,
            "memory.alpha"
        );
        var secondTarget = new SJ.MemoryPackBlockPath(
            SJ.MemoryPackCarrier.System,
            "memory.zeta"
        );
        DerivedMemoryArtifact first = await WriteArtifactAsync(
            repository,
            "alpha",
            firstTarget,
            "derived alpha text",
            epoch,
            anchor,
            setups
        );
        DerivedMemoryArtifact second = await WriteArtifactAsync(
            repository,
            "zeta",
            secondTarget,
            "derived zeta text",
            epoch,
            anchor,
            setups
        );
        var policy = new DerivedArtifactSetPolicy(
            "test-policy",
            "test-policy-v1",
            "test-group",
            [
                new DerivedArtifactSetRoleRequirement(
                    "alpha-role",
                    firstTarget
                ),
                new DerivedArtifactSetRoleRequirement(
                    "zeta-role",
                    secondTarget
                )
            ]
        );
        DerivedMemoryOrchestrationTransaction transaction =
            await DerivedArtifactSetTestFactoryForCli
                .CreateSettledTransactionAsync(
                    repository,
                    epoch,
                    policy,
                    [first, second]
                );
        return new Fixture(
            path,
            repository,
            policy,
            epoch,
            transaction,
            "main",
            anchor,
            setups,
            first.ArtifactId,
            second.ArtifactId
        );
    }

    private static async ValueTask<DerivedMemoryArtifact> WriteArtifactAsync(
        DerivedMemoryRepository repository,
        string profile,
        SJ.MemoryPackBlockPath target,
        string text,
        DerivedArtifactEpochPlan epoch,
        EventAddress anchor,
        SJ.SessionContextAnchorSetupReferences setups,
        string? roleId = null
    ) {
        var draft = new SJ.MemoryPackDraft(new SJ.MemoryPack());
        draft.UpsertBlock(target, text);
        const string fingerprint =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        return await repository.Artifacts.WriteCandidateAsync(
            new DerivedMemoryArtifactWriteRequest(
                epoch.EpochId,
                DerivedMemoryMaintainerRunner
                    .GetEpochPlanFingerprint(epoch),
                roleId ?? $"{profile}-role",
                profile,
                "tests",
                fingerprint,
                fingerprint,
                fingerprint,
                "candidate-1",
                "attempt-1",
                epoch.PlannedAtRawHead,
                epoch.SourceStartExclusive,
                epoch.SourceEndInclusive,
                anchor,
                epoch.RawStartSetups,
                setups,
                null,
                null,
                [],
                target,
                draft.Build()
            )
        );
    }

    private static string[] PublishArgs(
        Fixture fixture,
        string expectedPrevious,
        string? reportPath = null
    ) {
        var args = new List<string> {
            "publish-derived-artifact-set",
            "--input", fixture.Path,
            "--transaction", fixture.Transaction.TransactionId,
            "--member", $"alpha-role={fixture.FirstArtifactId}",
            "--member", $"zeta-role={fixture.SecondArtifactId}"
        };
        if (reportPath is not null) {
            args.Add("--report-json");
            args.Add(reportPath);
        }
        return [.. args];
    }

    private static class DerivedArtifactSetTestFactoryForCli {
        public static async ValueTask<
            DerivedMemoryOrchestrationTransaction
        > CreateSettledTransactionAsync(
            DerivedMemoryRepository repository,
            DerivedArtifactEpochPlan epoch,
            DerivedArtifactSetPolicy policy,
            IReadOnlyList<DerivedMemoryArtifact> artifacts
        ) {
            IReadOnlyDictionary<string, DerivedMemoryArtifact> byRole =
                artifacts.ToDictionary(
                    static artifact => artifact.RoleId,
                    StringComparer.Ordinal
                );
            DerivedMemoryRoleProvisioning[] roles = [
                .. policy.Roles.Select(requirement => {
                    DerivedMemoryArtifact artifact =
                        byRole[requirement.RoleId];
                    return new DerivedMemoryRoleProvisioning(
                        artifact.RoleId,
                        artifact.ProfileId,
                        artifact.Target,
                        requirement.Required,
                        artifact.Producer,
                        artifact.ProducerFingerprint,
                        artifact.PromptFingerprint,
                        artifact.ModelFingerprint,
                        DerivedMemoryRoleExecutionModes.Produce,
                        artifact.CandidateId,
                        artifact.AttemptId
                    );
                })
            ];
            DerivedMemoryOrchestrationTransaction transaction =
                await repository.Orchestrations.GetOrCreateAsync(
                    epoch,
                    policy,
                    roles
                );
            foreach (DerivedMemoryArtifact artifact in artifacts) {
                _ = await repository.Orchestrations.SettleAsync(
                    transaction,
                    new DerivedMemoryRoleSettlement(
                        transaction.TransactionId,
                        artifact.RoleId,
                        artifact.ArtifactId,
                        artifact.Outcome
                    )
                );
            }
            return transaction;
        }
    }

    private static string[] RebuildArgs(Fixture fixture) => [
        "rebuild-derived-artifact-set-latest",
        "--input", fixture.Path,
        "--lineage", fixture.LineageKey,
        "--coherence-group", fixture.Policy.CoherenceGroup,
        "--policy-id", fixture.Policy.PolicyId,
        "--policy-fingerprint", fixture.Policy.PolicyFingerprint,
        "--required-role", "alpha-role=observation/memory.alpha",
        "--required-role", "zeta-role=system/memory.zeta"
    ];

    private static int Run(string[] args) => Program.MainCore(
        args,
        ThrowingCompletionClientFactory.Instance
    );

    private static RawSnapshot ReadRawSnapshot(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path);
        RefId main = journal.OpenBranch(
            SJ.SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        IReadOnlyList<EventAddress> chain =
            journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        return new RawSnapshot(
            head,
            chain.Count,
            chain.Sum(address =>
                (long)journal.ReadEventHeaderPreview(address)
                    .Unwrap()
                    .PayloadLength
            ),
            chain.Count(address => !Enum.IsDefined(
                typeof(SJ.SessionEventKind),
                journal.ReadEventHeaderPreview(address)
                    .Unwrap()
                    .OpaqueEventKind
            )),
            HashRawFiles(path)
        );
    }

    private static void WriteConnections(string path) {
        var config = new CompletionConnectionsFileConfig(
            [
                new CompletionConnectionConfig(
                    "scripted",
                    "scripted",
                    "model-a",
                    "surface-a",
                    "http://localhost/"
                )
            ],
            "scripted"
        );
        File.WriteAllText(path, JsonSerializer.Serialize(config));
    }

    private static string HashRawFiles(string path) {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        foreach (string file in Directory
                     .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                     .Where(static file =>
                         !file.Contains(
                             $"{Path.DirectorySeparatorChar}derived{Path.DirectorySeparatorChar}",
                             StringComparison.Ordinal
                         )
                     )
                     .OrderBy(static file => file, StringComparer.Ordinal)) {
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record Fixture(
        string Path,
        DerivedMemoryRepository Repository,
        DerivedArtifactSetPolicy Policy,
        DerivedArtifactEpochPlan Epoch,
        DerivedMemoryOrchestrationTransaction Transaction,
        string LineageKey,
        EventAddress Anchor,
        SJ.SessionContextAnchorSetupReferences Setups,
        string FirstArtifactId,
        string SecondArtifactId
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        int EventCount,
        long LogicalPayloadBytes,
        int UnknownEventKindCount,
        string RawFilesSha256
    );

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"Derived-memory command must not create completion client '{connection.Id}'."
        );
    }

    private sealed class ConcurrentScriptedCompletionClientFactory(
        string responseText,
        int? failAtCall = null
    ) : ICompletionClientFactory {
        private readonly ConcurrentScriptedCompletionClient _client =
            new(responseText, failAtCall);

        public int CreateCallCount { get; private set; }
        public int CompletionCallCount => _client.CallCount;

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreateCallCount++;
            return _client;
        }
    }

    private sealed class ConcurrentScriptedCompletionClient(
        string responseText,
        int? failAtCall = null
    ) : ICompletionClient {
        private int _callCount;

        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            if (call == failAtCall) {
                throw new HttpRequestException(
                    "scripted uncertain provider failure"
                );
            }
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(responseText)
                ]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}

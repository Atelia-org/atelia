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
            "atelia.session-journal.cli.derived-memory-validation.v1",
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
        SJ.SessionContextCandidate? candidate = await provider.SelectAsync(
            new SJ.SessionContextSelectionRequest(
                fixture.Anchor,
                SJ.SessionContextSelectionMode.Latest,
                fixture.Policy.CoherenceGroup
            ),
            CancellationToken.None
        );
        Assert.NotNull(candidate);
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
    public async Task Publish_StaleCasFailsWithoutChangingRawOrReport() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        DerivedRecapArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "replacement",
            fixture.Policy.Roles[0].Target,
            "replacement text",
            fixture.Anchor,
            fixture.Setups
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
    public async Task Fork_ListDiagnosesButValidateAndRebuildFail() {
        Fixture fixture = await CreateFixtureAsync();
        Assert.Equal(0, Run(PublishArgs(fixture, "none")));
        string pointer = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        File.Delete(pointer);
        DerivedRecapArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "fork",
            fixture.Policy.Roles[0].Target,
            "fork text",
            fixture.Anchor,
            fixture.Setups
        );
        Assert.Equal(
            0,
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
        Assert.Equal(1, Run(RebuildArgs(fixture)));
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
                    fixture.Repository.Recaps.ArtifactsDirectory,
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
            "artifacts" => fixture.Repository.Recaps.ArtifactsDirectory,
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

        int exitCode = Run([
            "publish-derived-artifact-set",
            $"--input={fixture.Path}",
            "--lineage=--main",
            "--coherence-group=--group",
            "--policy-id=--policy",
            "--policy-fingerprint=--fingerprint",
            "--required-role=--alpha=observation/memory.alpha",
            "--required-role=--zeta=system/memory.zeta",
            $"--member=--alpha={fixture.FirstArtifactId}",
            $"--member=--zeta={fixture.SecondArtifactId}",
            "--expected-previous=none"
        ]);

        Assert.Equal(0, exitCode);
        DerivedArtifactSet set = Assert.Single(
            (await fixture.Repository.ArtifactSets.ReadInventoryAsync())
                .Sets
        );
        Assert.Equal("--main", set.LineageKey);
        Assert.Equal("--group", set.CoherenceGroup);
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

    private async ValueTask<Fixture> CreateFixtureAsync() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(
            _tempRoot,
            Guid.NewGuid().ToString("N")
        );
        EventAddress anchor;
        SJ.SessionContextAnchorSetupReferences setups;
        using (var engine = SJ.SessionJournalEngine.Create(
            path,
            new SJ.SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            engine.AppendObservation("old observation");
            anchor = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("old action")
                ]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            setups = engine.ResolveContextAnchorSetupReferences(anchor);
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        var firstTarget = new SJ.MemoryPackBlockPath(
            SJ.MemoryPackCarrier.Observation,
            "memory.alpha"
        );
        var secondTarget = new SJ.MemoryPackBlockPath(
            SJ.MemoryPackCarrier.System,
            "memory.zeta"
        );
        DerivedRecapArtifact first = await WriteArtifactAsync(
            repository,
            "alpha",
            firstTarget,
            "derived alpha text",
            anchor,
            setups
        );
        DerivedRecapArtifact second = await WriteArtifactAsync(
            repository,
            "zeta",
            secondTarget,
            "derived zeta text",
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
        return new Fixture(
            path,
            repository,
            policy,
            "main",
            anchor,
            setups,
            first.ArtifactId,
            second.ArtifactId
        );
    }

    private static async ValueTask<DerivedRecapArtifact> WriteArtifactAsync(
        DerivedMemoryRepository repository,
        string profile,
        SJ.MemoryPackBlockPath target,
        string text,
        EventAddress anchor,
        SJ.SessionContextAnchorSetupReferences setups
    ) {
        var memoryPack = new SJ.MemoryPack();
        switch (target.Carrier) {
            case SJ.MemoryPackCarrier.System:
                memoryPack.System.Add(
                    target.BlockKey,
                    new SJ.MemoryPackBlock(text)
                );
                break;
            case SJ.MemoryPackCarrier.Observation:
                memoryPack.Observation.Add(
                    target.BlockKey,
                    new SJ.MemoryPackBlock(text)
                );
                break;
            case SJ.MemoryPackCarrier.Action:
                memoryPack.Action.Add(
                    target.BlockKey,
                    new SJ.MemoryPackBlock(text)
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
        return await repository.Recaps.WriteProducedAsync(
            new DerivedRecapWriteRequest(
                DerivedRecapArtifactKinds.RollingSummary,
                profile,
                "tests",
                "tests-v1",
                anchor,
                SourceStartExclusive: null,
                anchor,
                anchor,
                setups.RuntimeConfig.Address,
                setups.SystemPrompt.Address,
                PreviousArtifact: null,
                target,
                memoryPack
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
            "--lineage", fixture.LineageKey,
            "--coherence-group", fixture.Policy.CoherenceGroup,
            "--policy-id", fixture.Policy.PolicyId,
            "--policy-fingerprint", fixture.Policy.PolicyFingerprint,
            "--required-role", "alpha-role=observation/memory.alpha",
            "--required-role", "zeta-role=system/memory.zeta",
            "--member", $"alpha-role={fixture.FirstArtifactId}",
            "--member", $"zeta-role={fixture.SecondArtifactId}",
            "--expected-previous", expectedPrevious
        };
        if (reportPath is not null) {
            args.Add("--report-json");
            args.Add(reportPath);
        }
        return [.. args];
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
}

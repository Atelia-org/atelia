using System.Text.Json.Nodes;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedMemoryArtifactStoreTests : IDisposable {
    private readonly List<string> _tempDirectories = [];

    [Fact]
    public async Task WriteCandidate_PersistsV2UnderMemoryRootWithoutLatestIndex() {
        Fixture fixture = CreateFixture();
        DerivedMemoryArtifact artifact =
            await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                fixture.Repository,
                "autobiography",
                "profile-a",
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.Action,
                    "memory.autobiography"
                ),
                "candidate text",
                fixture.Anchor,
                fixture.Setups
            );

        Assert.Equal(
            DerivedMemoryArtifactStore.ArtifactSchema,
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
                fixture.Repository.Artifacts.ArtifactsDirectory,
                $"{artifact.ArtifactId}.json"
            )))!["schema"]!.GetValue<string>()
        );
        JsonNode persisted = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(
                fixture.Repository.Artifacts.ArtifactsDirectory,
                $"{artifact.ArtifactId}.json"
            ))
        )!;
        Assert.NotNull(persisted["rawStartSetups"]);
        Assert.NotNull(persisted["anchorSetups"]);
        Assert.Null(persisted["governingRuntimeConfigSetup"]);
        Assert.Null(persisted["governingSystemPromptSetup"]);
        Assert.StartsWith(
            Path.Combine(
                fixture.Repository.MemoryRoot,
                "artifacts"
            ),
            fixture.Repository.Artifacts.ArtifactsDirectory,
            StringComparison.Ordinal
        );
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Repository.MemoryRoot,
            "indexes",
            "latest-by-profile"
        )));
    }

    [Fact]
    public async Task ExactRetryIsIdempotent_AlternativeCandidateIsAppendOnly() {
        Fixture fixture = CreateFixture();
        ContextHeaderBlockPath target = new(
            ContextHeaderCarrier.Observation,
            "memory.world"
        );
        DerivedMemoryArtifact first =
            await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                fixture.Repository,
                "world-understanding",
                "profile-a",
                target,
                "world text",
                fixture.Anchor,
                fixture.Setups,
                candidateId: "candidate-a"
            );
        DerivedMemoryArtifact retry =
            await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                fixture.Repository,
                "world-understanding",
                "profile-a",
                target,
                "world text",
                fixture.Anchor,
                fixture.Setups,
                candidateId: "candidate-a"
            );
        DerivedMemoryArtifact alternative =
            await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                fixture.Repository,
                "world-understanding",
                "profile-a",
                target,
                "world text",
                fixture.Anchor,
                fixture.Setups,
                candidateId: "candidate-b"
            );

        Assert.Equal(first.ArtifactId, retry.ArtifactId);
        Assert.NotEqual(first.ArtifactId, alternative.ArtifactId);
        Assert.Equal(
            2,
            Directory.EnumerateFiles(
                fixture.Repository.Artifacts.ArtifactsDirectory,
                "*.json"
            ).Count()
        );
    }

    [Fact]
    public async Task StrictInventoryRejectsRetiredV1Schema() {
        Fixture fixture = CreateFixture();
        DerivedMemoryArtifact artifact =
            await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                fixture.Repository,
                "role-a",
                "profile-a",
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "memory.a"
                ),
                "text",
                fixture.Anchor,
                fixture.Setups
            );
        string path = Path.Combine(
            fixture.Repository.Artifacts.ArtifactsDirectory,
            $"{artifact.ArtifactId}.json"
        );
        JsonNode json = JsonNode.Parse(
            await File.ReadAllTextAsync(path)
        )!;
        json["schema"] = "atelia.session-journal.derived-recap.v1";
        await File.WriteAllTextAsync(path, json.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAllActiveBranchesAsync()
        );
    }

    [Theory]
    [InlineData("rawStartSetups")]
    [InlineData("anchorSetups")]
    public async Task StrictInventoryRequiresBothTypedSetupGroups(
        string propertyName
    ) {
        Fixture fixture = CreateFixture();
        DerivedMemoryArtifact artifact =
            await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                fixture.Repository,
                "role-a",
                "profile-a",
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "memory.a"
                ),
                "text",
                fixture.Anchor,
                fixture.Setups
            );
        string path = Path.Combine(
            fixture.Repository.Artifacts.ArtifactsDirectory,
            $"{artifact.ArtifactId}.json"
        );
        JsonNode json = JsonNode.Parse(
            await File.ReadAllTextAsync(path)
        )!;
        json[propertyName] = null;
        await File.WriteAllTextAsync(path, json.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAllActiveBranchesAsync()
        );
    }

    [Fact]
    public async Task CorruptedDeterministicIdCollisionFailsWithoutSuffix() {
        Fixture fixture = CreateFixture();
        DerivedMemoryArtifactWriteRequest request =
            DerivedMemoryArtifactTestFactory.CreateGenesisRequest(
                "role-a",
                "profile-a",
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "memory.a"
                ),
                "text",
                fixture.Anchor,
                fixture.Setups
            );
        DerivedMemoryArtifact artifact =
            await fixture.Repository.Artifacts.WriteCandidateAsync(
                request
            );
        string path = Path.Combine(
            fixture.Repository.Artifacts.ArtifactsDirectory,
            $"{artifact.ArtifactId}.json"
        );
        await File.WriteAllTextAsync(path, "{}");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.Artifacts
                .WriteCandidateAsync(request)
        );
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.Artifacts.ArtifactsDirectory,
            "*.json"
        ));
    }

    [Theory]
    [InlineData("duplicate-role")]
    [InlineData("bad-hash")]
    [InlineData("wrong-previous")]
    [InlineData("invalid-target")]
    public async Task InvalidInputMembersFailBeforeDerivedSideEffects(
        string shape
    ) {
        Fixture fixture = CreateFixture();
        ContextHeaderBlockPath target = new(
            ContextHeaderCarrier.Action,
            "memory.current"
        );
        DerivedMemoryArtifactWriteRequest request =
            DerivedMemoryArtifactTestFactory.CreateGenesisRequest(
                "role-current",
                "profile",
                target,
                "text",
                fixture.Anchor,
                fixture.Setups
            );
        string firstId = "dma_" + new string('1', 64);
        string secondId = "dma_" + new string('2', 64);
        var first = new DerivedMemoryArtifactInputMember(
            "role-old",
            firstId,
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.Observation,
                "memory.old"
            ),
            new string('a', 64)
        );
        DerivedMemoryArtifactInputMember second = shape switch {
            "duplicate-role" => first with {
                ArtifactId = secondId,
                Target = new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "memory.other"
                )
            },
            "bad-hash" => first with {
                RoleId = "role-other",
                ArtifactId = secondId,
                ContentSha256 = "not-a-hash"
            },
            "invalid-target" => first with {
                RoleId = "role-other",
                ArtifactId = secondId,
                Target = new ContextHeaderBlockPath(
                    (ContextHeaderCarrier)99,
                    "memory.other"
                )
            },
            _ => first with {
                RoleId = "role-current",
                ArtifactId = secondId,
                Target = target
            }
        };
        request = request with {
            InputSetId = "das_" + new string('3', 64),
            PreviousRoleArtifact = shape == "wrong-previous"
                ? firstId
                : null,
            InputMembers = [first, second]
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await fixture.Repository.Artifacts
                .WriteCandidateAsync(request)
        );
        Assert.False(Directory.Exists(
            fixture.Repository.DerivedRoot
        ));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nonempty")]
    [InlineData("dangling")]
    public async Task ExactPointReadsRejectSymlinksBeforeExistenceProbe(
        string targetShape
    ) {
        if (OperatingSystem.IsWindows()) {
            return;
        }
        Fixture fixture = CreateFixture();
        string external = Path.Combine(
            Path.GetTempPath(),
            $"atelia-derived-point-{Guid.NewGuid():N}"
        );
        if (targetShape != "dangling") {
            await File.WriteAllTextAsync(
                external,
                targetShape == "empty" ? string.Empty : "{}"
            );
        }
        Directory.CreateDirectory(
            fixture.Repository.Artifacts.ArtifactsDirectory
        );
        Directory.CreateDirectory(
            fixture.Repository.ArtifactSets.SetsDirectory
        );
        string artifactId = "dma_" + new string('4', 64);
        string setId = "das_" + new string('5', 64);
        string artifactPoint = Path.Combine(
            fixture.Repository.Artifacts.ArtifactsDirectory,
            $"{artifactId}.json"
        );
        string setPoint = Path.Combine(
            fixture.Repository.ArtifactSets.SetsDirectory,
            $"{setId}.json"
        );
        File.CreateSymbolicLink(artifactPoint, external);
        File.CreateSymbolicLink(setPoint, external);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.Artifacts
                .TryReadArtifactAsync(artifactId)
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .TryReadExactAsync(setId)
        );

        File.Delete(artifactPoint);
        File.Delete(setPoint);
        if (File.Exists(external)) {
            File.Delete(external);
        }
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("unknown")]
    [InlineData("filename")]
    public async Task StrictV2RegressionsFailFast(string shape) {
        Fixture fixture = CreateFixture();
        string path;
        if (shape == "oversize") {
            Directory.CreateDirectory(
                fixture.Repository.Artifacts.ArtifactsDirectory
            );
            path = Path.Combine(
                fixture.Repository.Artifacts.ArtifactsDirectory,
                $"dma_{new string('6', 64)}.json"
            );
            await File.WriteAllBytesAsync(
                path,
                new byte[
                    DerivedMemoryArtifactStore.MaxArtifactFileBytes + 1
                ]
            );
        }
        else {
            DerivedMemoryArtifact artifact =
                await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
                    fixture.Repository,
                    "role-a",
                    "profile-a",
                    new ContextHeaderBlockPath(
                        ContextHeaderCarrier.System,
                        "memory.a"
                    ),
                    "text",
                    fixture.Anchor,
                    fixture.Setups
                );
            path = Path.Combine(
                fixture.Repository.Artifacts.ArtifactsDirectory,
                $"{artifact.ArtifactId}.json"
            );
            if (shape == "unknown") {
                JsonNode json = JsonNode.Parse(
                    await File.ReadAllTextAsync(path)
                )!;
                json["unknown"] = true;
                await File.WriteAllTextAsync(
                    path,
                    json.ToJsonString()
                );
            }
            else {
                string renamed = Path.Combine(
                    fixture.Repository.Artifacts.ArtifactsDirectory,
                    $"dma_{new string('7', 64)}.json"
                );
                File.Move(path, renamed);
            }
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAllActiveBranchesAsync()
        );
    }

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort fixture cleanup.
            }
        }
    }

    private Fixture CreateFixture() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-memory-artifact-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        using var journal = EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch("main", startPoint: null).Unwrap();
        EventAddress runtime = journal.CommitToRef(
            "main",
            null,
            [1],
            opaqueEventKind: 1
        ).Unwrap().EventAddress;
        EventAddress prompt = journal.CommitToRef(
            "main",
            runtime,
            [2],
            opaqueEventKind: 2
        ).Unwrap().EventAddress;
        EventAddress anchor = journal.CommitToRef(
            "main",
            prompt,
            [3],
            opaqueEventKind: 4
        ).Unwrap().EventAddress;
        return new Fixture(
            DerivedMemoryRepository.Open(path),
            anchor,
            new SessionContextAnchorSetupReferences(
                new SessionContextSetupReference(
                    runtime,
                    1,
                    new string('1', 64)
                ),
                new SessionContextSetupReference(
                    prompt,
                    1,
                    new string('2', 64)
                )
            )
        );
    }

    private sealed record Fixture(
        DerivedMemoryRepository Repository,
        EventAddress Anchor,
        SessionContextAnchorSetupReferences Setups
    );
}

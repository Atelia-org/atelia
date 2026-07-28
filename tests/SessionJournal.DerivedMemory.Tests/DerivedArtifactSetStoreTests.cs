using Atelia.EventJournal;
using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedArtifactSetStoreTests : IDisposable {
    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned directories.
            }
        }
    }

    [Fact]
    public async Task PublishAndProvider_UseCanonicalArbitraryRolesWithoutRawMutation() {
        Fixture fixture = await CreateFixtureAsync();
        EventAddress rawHeadBefore = ReadRawHead(fixture.Path);

        DerivedArtifactSet published =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.SecondSelection, fixture.FirstSelection],
                    expectedPrevious: null
                )
            );
        var provider = new DerivedArtifactSetContextCandidateSource(
            fixture.Repository,
            fixture.Policy,
            fixture.LineageKey
        );
        SessionContextCandidate? candidate = await provider.SelectAsync(
            new SessionContextSelectionRequest(
                fixture.Anchor,
                SessionContextSelectionMode.Latest,
                fixture.Policy.CoherenceGroup
            ),
            CancellationToken.None
        );

        Assert.NotNull(candidate);
        Assert.Equal(fixture.Anchor, candidate.RawStartExclusive);
        Assert.Equal(fixture.AnchorSetups, candidate.AnchorSetups);
        Assert.Equal(
            new[] { "alpha-role", "zeta-role" },
            published.Members.Select(static member => member.RoleId)
        );
        Assert.Equal(
            published.Members.Select(static member => member.Target),
            candidate.Contributions.Select(
                static contribution => contribution.Target
            )
        );
        Assert.All(
            candidate.Contributions,
            static contribution => Assert.Equal(
                SessionContextContributionHasher.ComputeSha256(
                    contribution.ExactText
                ),
                contribution.ContentSha256
            )
        );
        Assert.Equal(rawHeadBefore, ReadRawHead(fixture.Path));
        Assert.DoesNotContain(
            fixture.Policy.CoherenceGroup,
            Assert.Single(
                Directory.EnumerateFiles(
                    fixture.Repository.ArtifactSets.LatestPointersDirectory
                )
            ),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Publish_IsDeterministicAndIdempotent_ButStaleCasFails() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSetPublicationRequest genesis = fixture.Publication(
            [fixture.FirstSelection, fixture.SecondSelection],
            expectedPrevious: null
        );

        DerivedArtifactSet first =
            await fixture.Repository.ArtifactSets.PublishAsync(genesis);
        DerivedArtifactSet retry =
            await fixture.Repository.ArtifactSets.PublishAsync(genesis);
        DerivedArtifactSet second =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.SecondSelection, fixture.FirstSelection],
                    first.SetId
                )
            );
        DerivedMemoryArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "alpha-profile-replacement",
            fixture.Policy.Roles[0].Target,
            "replacement alpha text",
            fixture.Anchor,
            fixture.AnchorSetups,
            fixture.Policy.Roles[0].RoleId
        );

        Assert.Equal(first.SetId, retry.SetId);
        Assert.NotEqual(first.SetId, second.SetId);
        Assert.Equal(first.SetId, second.PreviousSetId);
        await Assert.ThrowsAsync<DerivedArtifactSetConcurrencyException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [
                        new DerivedArtifactSetMemberSelection(
                            fixture.FirstSelection.RoleId,
                            replacement.ArtifactId
                        ),
                        fixture.SecondSelection
                    ],
                    first.SetId
                )
            )
        );
    }

    [Fact]
    public async Task PublishRequiresExactAnchorSetupHashes() {
        Fixture fixture = await CreateFixtureAsync();
        SessionContextAnchorSetupReferences mismatched =
            fixture.AnchorSetups with {
                RuntimeConfig = fixture.AnchorSetups.RuntimeConfig with {
                    PayloadSha256 = new string('c', 64)
                }
            };

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                ) with {
                    AnchorSetups = mismatched
                }
            )
        );
    }

    [Fact]
    public async Task Publish_RejectsMissingRequiredRoleAndTargetMismatch() {
        Fixture fixture = await CreateFixtureAsync();

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection],
                    expectedPrevious: null
                )
            )
        );

        var wrongPolicy = fixture.Policy with {
            Roles = [
                fixture.Policy.Roles[0] with {
                    Target = new MemoryPackBlockPath(
                        MemoryPackCarrier.Action,
                        "wrong-target"
                    )
                },
                fixture.Policy.Roles[1]
            ]
        };
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                ) with {
                    Policy = wrongPolicy
                }
            )
        );
    }

    [Fact]
    public async Task RebuildLatestPointer_UsesUniqueDagTipWithoutTimestamps() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet first =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                )
            );
        DerivedArtifactSet second =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    first.SetId
                )
            );
        string pointerPath = Assert.Single(
            Directory.EnumerateFiles(
                fixture.Repository.ArtifactSets.LatestPointersDirectory
            )
        );
        File.Delete(pointerPath);

        Assert.Null(
            await fixture.Repository.ArtifactSets.TryReadLatestAsync(
                fixture.Policy,
                fixture.LineageKey
            )
        );
        DerivedArtifactSet? rebuilt =
            await fixture.Repository.ArtifactSets.RebuildLatestPointerAsync(
                fixture.Policy,
                fixture.LineageKey
            );

        Assert.NotNull(rebuilt);
        Assert.Equal(second.SetId, rebuilt.SetId);
        Assert.True(File.Exists(pointerPath));
    }

    [Fact]
    public async Task RebuildLatestPointer_TwoTipsFailsFast() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        string pointerPath = Assert.Single(
            Directory.EnumerateFiles(
                fixture.Repository.ArtifactSets.LatestPointersDirectory
            )
        );
        File.Delete(pointerPath);
        DerivedMemoryArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "alpha-profile-fork",
            fixture.Policy.Roles[0].Target,
            "fork alpha text",
            fixture.Anchor,
            fixture.AnchorSetups,
            fixture.Policy.Roles[0].RoleId
        );
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [
                    new DerivedArtifactSetMemberSelection(
                        fixture.FirstSelection.RoleId,
                        replacement.ArtifactId
                    ),
                    fixture.SecondSelection
                ],
                expectedPrevious: null
            )
        );
        File.Delete(pointerPath);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .RebuildLatestPointerAsync(
                    fixture.Policy,
                    fixture.LineageKey
                )
        );

        Assert.Contains("forked or cyclic", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(pointerPath));
    }

    [Fact]
    public async Task Provider_MissingPointerIsUnavailable_MalformedPointerFailsFast() {
        Fixture fixture = await CreateFixtureAsync();
        var provider = new DerivedArtifactSetContextCandidateSource(
            fixture.Repository,
            fixture.Policy,
            fixture.LineageKey
        );
        var request = new SessionContextSelectionRequest(
            fixture.Anchor,
            SessionContextSelectionMode.Latest,
            fixture.Policy.CoherenceGroup
        );

        Assert.Null(
            await provider.SelectAsync(request, CancellationToken.None)
        );
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        string pointerPath = Assert.Single(
            Directory.EnumerateFiles(
                fixture.Repository.ArtifactSets.LatestPointersDirectory
            )
        );
        await File.WriteAllTextAsync(pointerPath, "{ malformed");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await provider.SelectAsync(
                request,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task Provider_RevalidatesExactArtifactsAfterPublication() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        string artifactPath = Path.Combine(
            fixture.Repository.Artifacts.ArtifactsDirectory,
            $"{fixture.FirstSelection.ArtifactId}.json"
        );
        await File.WriteAllTextAsync(artifactPath, "{}");
        var provider = new DerivedArtifactSetContextCandidateSource(
            fixture.Repository,
            fixture.Policy,
            fixture.LineageKey
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await provider.SelectAsync(
                new SessionContextSelectionRequest(
                    fixture.Anchor,
                    SessionContextSelectionMode.Latest,
                    fixture.Policy.CoherenceGroup
                ),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task DifferentCoherenceGroupDoesNotSilentlySelectLatest() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        var provider = new DerivedArtifactSetContextCandidateSource(
            fixture.Repository,
            fixture.Policy,
            fixture.LineageKey
        );

        SessionContextCandidate? candidate = await provider.SelectAsync(
            new SessionContextSelectionRequest(
                fixture.Anchor,
                SessionContextSelectionMode.Latest,
                "another-group"
            ),
            CancellationToken.None
        );

        Assert.Null(candidate);
    }

    [Fact]
    public async Task PersistedRoleSnapshot_IsCanonicalHashedAndPolicyExact() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet published =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.SecondSelection, fixture.FirstSelection],
                    expectedPrevious: null
                )
            );
        string setPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        JsonObject root = JsonNode.Parse(
            await File.ReadAllTextAsync(setPath)
        )!.AsObject();
        JsonArray persistedRoles =
            root["roleRequirements"]!.AsArray();
        Assert.Equal(
            ["alpha-role", "zeta-role"],
            persistedRoles.Select(
                static node => node!["roleId"]!.GetValue<string>()
            )
        );
        Assert.Equal(
            [true, true],
            persistedRoles.Select(
                static node => node!["required"]!.GetValue<bool>()
            )
        );
        Assert.Equal(
            published.RoleRequirements,
            published.RoleRequirements
                .OrderBy(static role => role.RoleId, StringComparer.Ordinal)
        );

        var changedRequired = fixture.Policy with {
            Roles = [
                fixture.Policy.Roles[0] with { Required = false },
                fixture.Policy.Roles[1]
            ]
        };
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .TryReadLatestAsync(
                    changedRequired,
                    fixture.LineageKey
                )
        );
        var changedTarget = fixture.Policy with {
            Roles = [
                fixture.Policy.Roles[0] with {
                    Target = new MemoryPackBlockPath(
                        MemoryPackCarrier.Action,
                        "changed-target"
                    )
                },
                fixture.Policy.Roles[1]
            ]
        };
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .TryReadLatestAsync(
                    changedTarget,
                    fixture.LineageKey
                )
        );
        persistedRoles[0]!["required"] = false;
        await File.WriteAllTextAsync(setPath, root.ToJsonString());
        InvalidDataException hashError =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ArtifactSets
                    .TryReadLatestAsync(
                        changedRequired,
                        fixture.LineageKey
                    )
            );
        Assert.Contains(
            "identity hash is invalid",
            hashError.Message,
            StringComparison.Ordinal
        );

        Fixture other = await CreateFixtureAsync();
        var optionalPolicy = other.Policy with {
            Roles = [
                other.Policy.Roles[0] with { Required = false },
                other.Policy.Roles[1]
            ]
        };
        DerivedArtifactSet optional =
            await other.Repository.ArtifactSets.PublishAsync(
                other.Publication(
                    [other.FirstSelection, other.SecondSelection],
                    expectedPrevious: null
                ) with {
                    Policy = optionalPolicy
                }
            );
        Assert.NotEqual(published.SetId, optional.SetId);
    }

    [Fact]
    public async Task RebuildLatestPointer_RenamedValidSetFailsBeforePointerWrite() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        string pointerPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        File.Delete(pointerPath);
        string originalSetPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        string copiedPath = Path.Combine(
            fixture.Repository.ArtifactSets.SetsDirectory,
            $"das_{new string('0', 64)}.json"
        );
        File.Copy(originalSetPath, copiedPath);

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ArtifactSets
                    .RebuildLatestPointerAsync(
                        fixture.Policy,
                        fixture.LineageKey
                    )
            );

        Assert.Contains(
            "filename does not exactly match",
            error.Message,
            StringComparison.Ordinal
        );
        Assert.False(File.Exists(pointerPath));
    }

    [Fact]
    public async Task ReadCapsAreCheckedBeforeJsonDeserialization() {
        Assert.Equal(
            1024 * 1024,
            DerivedArtifactSetStore.MaxSetFileBytes
        );
        Assert.Equal(
            64 * 1024,
            DerivedArtifactSetStore.MaxLatestPointerFileBytes
        );
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet set =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                )
            );
        string pointerPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        await File.WriteAllTextAsync(
            pointerPath,
            new string(
                'x',
                checked((int)
                    DerivedArtifactSetStore.MaxLatestPointerFileBytes + 1)
            )
        );
        InvalidDataException pointerError =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ArtifactSets
                    .TryReadLatestAsync(
                        fixture.Policy,
                        fixture.LineageKey
                    )
            );
        Assert.Contains(
            "65536-byte limit",
            pointerError.Message,
            StringComparison.Ordinal
        );

        string setPath = Path.Combine(
            fixture.Repository.ArtifactSets.SetsDirectory,
            $"{set.SetId}.json"
        );
        await File.WriteAllTextAsync(
            setPath,
            new string(
                'x',
                checked((int)
                    DerivedArtifactSetStore.MaxSetFileBytes + 1)
            )
        );
        InvalidDataException setError =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ArtifactSets.TryReadAsync(
                    set.SetId,
                    fixture.Policy,
                    fixture.LineageKey
                )
            );
        Assert.Contains(
            "1048576-byte limit",
            setError.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StrictSetSchemaAndMemberLimitRemainEnforced() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet set =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                )
            );
        string setPath = Path.Combine(
            fixture.Repository.ArtifactSets.SetsDirectory,
            $"{set.SetId}.json"
        );
        JsonObject root = JsonNode.Parse(
            await File.ReadAllTextAsync(setPath)
        )!.AsObject();
        root["unknown"] = true;
        await File.WriteAllTextAsync(setPath, root.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.TryReadAsync(
                set.SetId,
                fixture.Policy,
                fixture.LineageKey
            )
        );
        root.Remove("unknown");
        root["roleRequirements"]![0]!.AsObject().Remove("required");
        await File.WriteAllTextAsync(setPath, root.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.TryReadAsync(
                set.SetId,
                fixture.Policy,
                fixture.LineageKey
            )
        );

        DerivedArtifactSetMemberSelection[] tooMany = [
            .. Enumerable.Range(0, 129).Select(
                _ => fixture.FirstSelection
            )
        ];
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(tooMany, set.SetId)
            )
        );
    }

    [Fact]
    public async Task LatestProvider_TreatsRawSuffixBudgetAsNonBindingHint() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        var provider = new DerivedArtifactSetContextCandidateSource(
            fixture.Repository,
            fixture.Policy,
            fixture.LineageKey
        );

        SessionContextCandidate? candidate = await provider.SelectAsync(
            new SessionContextSelectionRequest(
                fixture.Anchor,
                SessionContextSelectionMode.Latest,
                fixture.Policy.CoherenceGroup,
                RawSuffixTokenBudget: 1
            ),
            CancellationToken.None
        );

        Assert.NotNull(candidate);
        Assert.Equal(fixture.Anchor, candidate.RawStartExclusive);
    }

    [Fact]
    public async Task InventoryAndValidation_EmptyDerivedStateIsValidAndReadOnly() {
        string path = NewPath();
        Directory.CreateDirectory(path);
        DerivedMemoryRepository repository = DerivedMemoryRepository.Open(path);

        DerivedArtifactSetInventory inventory =
            await repository.ArtifactSets.ReadInventoryAsync();
        DerivedMemoryValidationReport report = await repository.ValidateAsync();

        Assert.Empty(inventory.Sets);
        Assert.Empty(inventory.LatestPointers);
        Assert.Equal(
            new DerivedMemoryValidationReport(0, 0, 0, 0),
            report
        );
        Assert.False(Directory.Exists(repository.DerivedRoot));
    }

    [Fact]
    public async Task Inventory_IsStableButGlobalValidationRejectsArtifactsWithoutEpoch() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet first =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                )
            );
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                first.SetId
            )
        );
        var earlierPolicy = fixture.Policy with {
            PolicyId = "aaa-policy"
        };
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            ) with {
                Policy = earlierPolicy
            }
        );
        _ = await WriteArtifactAsync(
            fixture.Repository,
            "orphan-profile",
            new MemoryPackBlockPath(
                MemoryPackCarrier.Action,
                "orphan.block"
            ),
            "orphan text",
            fixture.Anchor,
            fixture.AnchorSetups
        );
        Dictionary<string, byte[]> before = SnapshotDerivedFiles(
            fixture.Repository.DerivedRoot
        );

        DerivedArtifactSetInventory firstRead =
            await fixture.Repository.ArtifactSets.ReadInventoryAsync();
        DerivedArtifactSetInventory secondRead =
            await fixture.Repository.ArtifactSets.ReadInventoryAsync();
        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ValidateAsync()
            );

        Assert.Equal(
            firstRead.Sets.Select(static set => set.SetId),
            secondRead.Sets.Select(static set => set.SetId)
        );
        Assert.Equal(
            firstRead.LatestPointers.Select(static pointer => pointer.SetId),
            secondRead.LatestPointers.Select(static pointer => pointer.SetId)
        );
        Assert.Equal(
            firstRead.Sets.OrderBy(
                static set => (
                    set.LineageKey,
                    set.CoherenceGroup,
                    set.PolicyId,
                    set.PolicyFingerprint,
                    set.SetId
                )
            ),
            firstRead.Sets
        );
        Assert.Equal(
            ["aaa-policy", "test-policy", "test-policy"],
            firstRead.Sets.Select(static set => set.PolicyId)
        );
        Assert.Contains("missing epoch", error.Message);
        AssertDerivedFilesEqual(
            before,
            SnapshotDerivedFiles(fixture.Repository.DerivedRoot)
        );
    }

    [Fact]
    public async Task Inventory_AllowsMissingPointerButValidationRejectsIt() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));

        DerivedArtifactSetInventory inventory =
            await fixture.Repository.ArtifactSets.ReadInventoryAsync();

        Assert.Single(inventory.Sets);
        Assert.Empty(inventory.LatestPointers);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAsync()
        );
    }

    [Fact]
    public async Task Validation_RejectsStalePointer() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet first =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                )
            );
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                first.SetId
            )
        );
        string pointerPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        JsonObject pointer = JsonNode.Parse(
            await File.ReadAllTextAsync(pointerPath)
        )!.AsObject();
        pointer["setId"] = first.SetId;
        await File.WriteAllTextAsync(pointerPath, pointer.ToJsonString());

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ValidateAsync()
            );

        Assert.Contains("stale", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_PreservesForkForDiagnosisButValidationRejectsIt() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        string pointerPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        File.Delete(pointerPath);
        DerivedMemoryArtifact replacement = await WriteArtifactAsync(
            fixture.Repository,
            "fork-profile",
            fixture.Policy.Roles[0].Target,
            "fork text",
            fixture.Anchor,
            fixture.AnchorSetups,
            fixture.Policy.Roles[0].RoleId
        );
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [
                    new DerivedArtifactSetMemberSelection(
                        fixture.FirstSelection.RoleId,
                        replacement.ArtifactId
                    ),
                    fixture.SecondSelection
                ],
                expectedPrevious: null
            )
        );

        DerivedArtifactSetInventory inventory =
            await fixture.Repository.ArtifactSets.ReadInventoryAsync();

        Assert.Equal(2, inventory.Sets.Count);
        Assert.Single(inventory.LatestPointers);
        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await fixture.Repository.ValidateAsync()
            );
        Assert.Contains("forked", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsMissingPredecessorAndSameKeyRoleDrift() {
        Fixture missing = await CreateFixtureAsync();
        DerivedArtifactSet predecessor =
            await missing.Repository.ArtifactSets.PublishAsync(
                missing.Publication(
                    [missing.FirstSelection, missing.SecondSelection],
                    expectedPrevious: null
                )
            );
        _ = await missing.Repository.ArtifactSets.PublishAsync(
            missing.Publication(
                [missing.FirstSelection, missing.SecondSelection],
                predecessor.SetId
            )
        );
        File.Delete(Path.Combine(
            missing.Repository.ArtifactSets.SetsDirectory,
            $"{predecessor.SetId}.json"
        ));
        InvalidDataException missingError =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await missing.Repository.ValidateAsync()
            );
        Assert.Contains(
            "missing previous",
            missingError.Message,
            StringComparison.Ordinal
        );

        Fixture drift = await CreateFixtureAsync();
        _ = await drift.Repository.ArtifactSets.PublishAsync(
            drift.Publication(
                [drift.FirstSelection, drift.SecondSelection],
                expectedPrevious: null
            )
        );
        string driftPointer = Assert.Single(Directory.EnumerateFiles(
            drift.Repository.ArtifactSets.LatestPointersDirectory
        ));
        File.Delete(driftPointer);
        var changedRolesPolicy = drift.Policy with {
            Roles = [
                drift.Policy.Roles[0] with { Required = false },
                drift.Policy.Roles[1]
            ]
        };
        _ = await drift.Repository.ArtifactSets.PublishAsync(
            drift.Publication(
                [drift.FirstSelection, drift.SecondSelection],
                expectedPrevious: null
            ) with {
                Policy = changedRolesPolicy
            }
        );
        InvalidDataException driftError =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await drift.Repository.ValidateAsync()
            );
        Assert.Contains(
            "role-snapshot drift",
            driftError.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ExactKeyLineageValidator_RejectsCycle() {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => DerivedMemoryRepository.ValidateExactKeyLineage(
                [
                    new DerivedArtifactSetLineageNode("a", "b"),
                    new DerivedArtifactSetLineageNode("b", "a")
                ]
            )
        );

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("set")]
    [InlineData("pointer")]
    public async Task InventoryAndValidation_CorruptPersistedObjectFails(
        string target
    ) {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet set =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Publication(
                    [fixture.FirstSelection, fixture.SecondSelection],
                    expectedPrevious: null
                )
            );
        string path = target switch {
            "artifact" => Path.Combine(
                fixture.Repository.Artifacts.ArtifactsDirectory,
                $"{fixture.FirstSelection.ArtifactId}.json"
            ),
            "set" => Path.Combine(
                fixture.Repository.ArtifactSets.SetsDirectory,
                $"{set.SetId}.json"
            ),
            "pointer" => Assert.Single(Directory.EnumerateFiles(
                fixture.Repository.ArtifactSets.LatestPointersDirectory
            )),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        await File.WriteAllTextAsync(path, "{ malformed");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .ReadInventoryAsync()
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAsync()
        );
    }

    [Theory]
    [InlineData("address")]
    [InlineData("token")]
    [InlineData("target")]
    public async Task PersistedShapeFailuresAreAlwaysInvalidData(
        string corruption
    ) {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Publication(
                [fixture.FirstSelection, fixture.SecondSelection],
                expectedPrevious: null
            )
        );
        string setPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        JsonObject root = JsonNode.Parse(
            await File.ReadAllTextAsync(setPath)
        )!.AsObject();
        switch (corruption) {
            case "address":
                root["commonAnchor"] = "syntactically-invalid-address";
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

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .ReadInventoryAsync()
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAsync()
        );
    }

    [Theory]
    [InlineData("sets")]
    [InlineData("pointers")]
    [InlineData("artifacts")]
    public async Task EmptyInternalStoreSymlinkFailsBeforeEnumeration(
        string targetKind
    ) {
        string repoPath = NewPath();
        Directory.CreateDirectory(repoPath);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(repoPath);
        string targetPath = targetKind switch {
            "sets" => repository.ArtifactSets.SetsDirectory,
            "pointers" =>
                repository.ArtifactSets.LatestPointersDirectory,
            "artifacts" => repository.Artifacts.ArtifactsDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind))
        };
        string external = NewPath();
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(
            Path.GetDirectoryName(targetPath)!
        );
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

        if (targetKind is "sets" or "pointers") {
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await repository.ArtifactSets
                    .ReadInventoryAsync()
            );
        }
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateAsync()
        );
    }

    private async ValueTask<Fixture> CreateFixtureAsync() {
        string path = NewPath();
        EventAddress runtime;
        EventAddress prompt;
        EventAddress anchor;
        using (var journal = EventJournal.EventJournal.CreateNew(path)) {
            journal.CreateBranch("main", null).Unwrap();
            runtime = journal.CommitToRef(
                "main",
                null,
                [1],
                opaqueEventKind: 1
            ).Unwrap().EventAddress;
            prompt = journal.CommitToRef(
                "main",
                runtime,
                [2],
                opaqueEventKind: 2
            ).Unwrap().EventAddress;
            anchor = journal.CommitToRef(
                "main",
                prompt,
                [3],
                opaqueEventKind: 4
            ).Unwrap().EventAddress;
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        var firstTarget = new MemoryPackBlockPath(
            MemoryPackCarrier.Observation,
            "memory.alpha"
        );
        var secondTarget = new MemoryPackBlockPath(
            MemoryPackCarrier.System,
            "memory.zeta"
        );
        var anchorSetups = new SessionContextAnchorSetupReferences(
            new SessionContextSetupReference(
                runtime,
                1,
                new string('a', 64)
            ),
            new SessionContextSetupReference(
                prompt,
                1,
                new string('b', 64)
            )
        );
        DerivedMemoryArtifact first = await WriteArtifactAsync(
            repository,
            "alpha-profile",
            firstTarget,
            "alpha text",
            anchor,
            anchorSetups
        );
        DerivedMemoryArtifact second = await WriteArtifactAsync(
            repository,
            "zeta-profile",
            secondTarget,
            "zeta text",
            anchor,
            anchorSetups
        );
        var policy = new DerivedArtifactSetPolicy(
            "test-policy",
            "test-policy-v1",
            "group/with:unsafe\\path",
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
            LineageKey: "main/../../unsafe",
            anchor,
            anchorSetups,
            new DerivedArtifactSetMemberSelection(
                "alpha-role",
                first.ArtifactId
            ),
            new DerivedArtifactSetMemberSelection(
                "zeta-role",
                second.ArtifactId
            )
        );
    }

    private static async ValueTask<DerivedMemoryArtifact> WriteArtifactAsync(
        DerivedMemoryRepository repository,
        string profile,
        MemoryPackBlockPath target,
        string text,
        EventAddress anchor,
        SessionContextAnchorSetupReferences setups,
        string? roleId = null
    ) {
        return await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
            repository,
            roleId
                ?? $"{(profile.EndsWith(
                        "-profile",
                        StringComparison.Ordinal
                    )
                    ? profile[..^"-profile".Length]
                    : profile)}-role",
            profile,
            target,
            text,
            anchor,
            setups
        );
    }

    private static EventAddress ReadRawHead(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path);
        RefId main = journal.OpenBranch("main").Unwrap();
        return journal.GetHead(main)!.Value;
    }

    private static Dictionary<string, byte[]> SnapshotDerivedFiles(
        string derivedRoot
    ) => !Directory.Exists(derivedRoot)
        ? []
        : Directory
            .EnumerateFiles(derivedRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(derivedRoot, path),
                File.ReadAllBytes,
                StringComparer.Ordinal
            );

    private static void AssertDerivedFilesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual
    ) {
        Assert.Equal(
            expected.Keys.OrderBy(static key => key, StringComparer.Ordinal),
            actual.Keys.OrderBy(static key => key, StringComparer.Ordinal)
        );
        foreach ((string path, byte[] bytes) in expected) {
            Assert.Equal(bytes, actual[path]);
        }
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-artifact-set-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed record Fixture(
        string Path,
        DerivedMemoryRepository Repository,
        DerivedArtifactSetPolicy Policy,
        string LineageKey,
        EventAddress Anchor,
        SessionContextAnchorSetupReferences AnchorSetups,
        DerivedArtifactSetMemberSelection FirstSelection,
        DerivedArtifactSetMemberSelection SecondSelection
    ) {
        public DerivedArtifactSetPublicationRequest Publication(
            IReadOnlyList<DerivedArtifactSetMemberSelection> members,
            string? expectedPrevious
        ) => new(
            Policy,
            LineageKey,
            AnchorSetups,
            members,
            expectedPrevious
        );
    }
}

using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedMemoryOrchestratorTests : IDisposable {
    private const string Fingerprint =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private readonly List<string> _paths = [];
    private readonly List<SessionJournalEngine> _engines = [];

    [Fact]
    public async Task RequiredRolesRunInParallelAgainstOnePreparedSnapshot() {
        Fixture fixture = await CreateFixtureAsync();
        var gate = new ParallelGate(2);
        var alpha = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            "alpha memory",
            gate
        );
        var zeta = new FakeMaintainer(
            "zeta-profile",
            fixture.Policy.Roles[1].Target,
            "zeta memory",
            gate
        );
        var orchestrator = new DerivedMemoryOrchestrator(
            fixture.Repository
        );

        Task<DerivedMemoryOrchestrationResult> run =
            orchestrator.RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    Execution(fixture, 0, alpha),
                    Execution(fixture, 1, zeta)
                )
            ).AsTask();
        await gate.AllEntered;
        Assert.False(run.IsCompleted);
        gate.Release();
        DerivedMemoryOrchestrationResult result = await run;

        Assert.Equal(
            DerivedMemoryOrchestrationStatus.Published,
            result.Status
        );
        Assert.Same(alpha.History, zeta.History);
        Assert.Equal(2, result.Settlements.Count);
        Assert.NotNull(result.PublishedSet);
        Assert.Equal(
            fixture.RawHead,
            fixture.Engine.ReadCurrentLineageHeaders().CapturedHead
        );
    }

    [Fact]
    public async Task RequiredFailureKeepsPartialAndRestartRunsOnlyMissingRole() {
        Fixture fixture = await CreateFixtureAsync();
        var alpha = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            "alpha memory"
        );
        var failing = new FakeMaintainer(
            "zeta-profile",
            fixture.Policy.Roles[1].Target,
            exception: new InvalidOperationException("planned failure")
        );
        var orchestrator = new DerivedMemoryOrchestrator(
            fixture.Repository
        );

        DerivedMemoryOrchestrationResult first =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    Execution(fixture, 0, alpha),
                    Execution(fixture, 1, failing)
                )
            );

        Assert.Equal(
            DerivedMemoryOrchestrationStatus.Incomplete,
            first.Status
        );
        Assert.Single(first.Settlements);
        Assert.Single(first.Failures);
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        DerivedMemoryValidationReport partialValidation =
            await fixture.Repository.ValidateAsync(fixture.Engine);
        Assert.Equal(
            1,
            partialValidation.OrchestrationTransactionCount
        );
        Assert.Equal(1, partialValidation.RoleSettlementCount);
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException(
                "settled role reopened"
            )
        );
        var recovered = new FakeMaintainer(
            "zeta-profile",
            fixture.Policy.Roles[1].Target,
            "zeta recovered"
        );

        DerivedMemoryOrchestrationResult second =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    Execution(fixture, 0, mustNotRun),
                    Execution(fixture, 1, recovered)
                )
            );

        Assert.Equal(
            DerivedMemoryOrchestrationStatus.Published,
            second.Status
        );
        Assert.Equal(0, mustNotRun.CallCount);
        Assert.Equal(1, recovered.CallCount);
        Assert.Equal(
            first.Transaction.TransactionId,
            second.Transaction.TransactionId
        );
        Assert.Equal(2, second.Settlements.Count);
    }

    [Fact]
    public async Task IdentityCreatesCurrentAnchorArtifactForMissingOldRole() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution execution = Execution(
            fixture,
            0,
            maintainer: null,
            DerivedMemoryRoleExecutionModes.Identity
        );

        DerivedMemoryOrchestrationResult result =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(fixture, execution)
                );

        DerivedMemoryRoleSettlement settlement =
            Assert.Single(result.Settlements);
        DerivedMemoryArtifact artifact =
            await fixture.Repository.Artifacts.TryReadArtifactAsync(
                settlement.ArtifactId
            ) ?? throw new Xunit.Sdk.XunitException(
                "Expected identity artifact."
            );
        Assert.Equal(
            DerivedMemoryArtifactOutcomes.Identity,
            artifact.Outcome
        );
        Assert.Equal(string.Empty, artifact.Content);
        Assert.Equal(fixture.Epoch.SourceEndInclusive, artifact.AnchorRawEvent);
        Assert.Equal(
            fixture.Epoch.SourceEndInclusive,
            result.PublishedSet!.CommonAnchor
        );
    }

    [Fact]
    public async Task ExactAlternativeSelectionChecksEntireProvisioningJob() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution produced = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "tuned candidate"
            )
        );
        DerivedMemoryOrchestrationResult producedResult =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(fixture, produced)
                );
        string artifactId =
            Assert.Single(producedResult.Settlements).ArtifactId;
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        )));
        Directory.Delete(
            fixture.Repository.Orchestrations.SettlementsDirectory,
            recursive: true
        );
        Directory.Delete(
            fixture.Repository.Orchestrations.TransactionsDirectory,
            recursive: true
        );
        Directory.Delete(
            fixture.Repository.Orchestrations.FinalizationsDirectory,
            recursive: true
        );
        DerivedMemoryRoleExecution selected = Execution(
            fixture,
            0,
            maintainer: null,
            DerivedMemoryRoleExecutionModes.SelectExisting,
            artifactId
        );

        DerivedMemoryOrchestrationResult selectedResult =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(fixture, selected)
                );

        Assert.Equal(
            artifactId,
            Assert.Single(selectedResult.Settlements).ArtifactId
        );
        DerivedMemoryRoleProvisioning wrong =
            selected.Provisioning with {
                PromptFingerprint =
                    "sha256:" + new string('f', 64)
            };
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.Orchestrations
                .SettleAsync(
                    selectedResult.Transaction with {
                        Roles = [wrong]
                    },
                    Assert.Single(selectedResult.Settlements)
                )
        );
    }

    [Fact]
    public async Task JobFingerprintChangeCreatesNewTransaction() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution first = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "first"
            )
        );
        DerivedMemoryOrchestrationTransaction firstTransaction =
            (await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(fixture, first)
                )).Transaction;
        DerivedMemoryRoleExecution changed = first with {
            Provisioning = first.Provisioning with {
                PromptFingerprint =
                    "sha256:" + new string('f', 64)
            }
        };

        DerivedMemoryOrchestrationTransaction changedTransaction =
            await fixture.Repository.Orchestrations.GetOrCreateAsync(
                fixture.Epoch,
                fixture.Policy,
                [changed.Provisioning]
            );

        Assert.NotEqual(
            firstTransaction.TransactionId,
            changedTransaction.TransactionId
        );
        Assert.NotEqual(
            firstTransaction.JobFingerprint,
            changedTransaction.JobFingerprint
        );
    }

    [Fact]
    public async Task CorruptSettlementFailsFastAndIsNotOverwritten() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution execution = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "alpha"
            )
        );
        DerivedMemoryOrchestrationResult result =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(fixture, execution)
                );
        string settlementPath = Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(
                    fixture.Repository.Orchestrations
                        .SettlementsDirectory,
                    result.Transaction.TransactionId
                )
            )
        );
        await File.WriteAllTextAsync(settlementPath, "{broken");
        byte[] corrupted = await File.ReadAllBytesAsync(settlementPath);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await new DerivedMemoryOrchestrator(
                fixture.Repository
            ).RunAsync(
                fixture.Engine,
                Request(fixture, execution)
            )
        );
        Assert.Equal(corrupted, await File.ReadAllBytesAsync(settlementPath));
    }

    [Fact]
    public async Task ConcurrentOrchestratorsOnSameEngineConvergeAtomically() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        var maintainer = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            "same candidate"
        );
        DerivedMemoryOrchestrationRequest request = Request(
            fixture,
            Execution(fixture, 0, maintainer)
        );
        var orchestrator = new DerivedMemoryOrchestrator(
            fixture.Repository
        );

        DerivedMemoryOrchestrationResult[] results =
            await Task.WhenAll(
                orchestrator.RunAsync(
                    fixture.Engine,
                    request
                ).AsTask(),
                orchestrator.RunAsync(
                    fixture.Engine,
                    request
                ).AsTask()
            );

        Assert.All(results, result => Assert.Equal(
            DerivedMemoryOrchestrationStatus.Published,
            result.Status
        ));
        Assert.Single(results
            .Select(result => result.Transaction.TransactionId)
            .Distinct(StringComparer.Ordinal));
        Assert.Single(results
            .Select(result => result.PublishedSet!.SetId)
            .Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.Orchestrations.TransactionsDirectory
        ));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(
                fixture.Repository.Orchestrations.SettlementsDirectory,
                results[0].Transaction.TransactionId
            )
        ));
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task CancellationKeepsSuccessfulSettlementWithoutPublishing() {
        Fixture fixture = await CreateFixtureAsync();
        var alpha = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            "alpha durable"
        );
        var blocked = new CancellationMaintainer(
            "zeta-profile",
            fixture.Policy.Roles[1].Target
        );
        using var cancellation = new CancellationTokenSource();
        Task<DerivedMemoryOrchestrationResult> run =
            new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(
                        fixture,
                        Execution(fixture, 0, alpha),
                        Execution(fixture, 1, blocked)
                    ),
                    cancellation.Token
                ).AsTask();
        await blocked.Entered;
        string settlementRoot =
            fixture.Repository.Orchestrations.SettlementsDirectory;
        for (int attempt = 0;
             attempt < 100 && (!Directory.Exists(settlementRoot)
                 || !Directory.EnumerateFiles(
                        settlementRoot,
                        "*.json",
                        SearchOption.AllDirectories
                    ).Any());
             attempt++) {
            await Task.Delay(10);
        }
        cancellation.Cancel();

        DerivedMemoryOrchestrationResult result = await run;

        Assert.Equal(
            DerivedMemoryOrchestrationStatus.Incomplete,
            result.Status
        );
        Assert.Single(result.Settlements);
        Assert.Equal("alpha", result.Settlements[0].RoleId);
        Assert.Contains(
            result.Failures,
            failure => failure.RoleId == "zeta"
                && failure.ExceptionType.Contains(
                    "CanceledException",
                    StringComparison.Ordinal
                )
        );
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task OptionalFailureIsDurablyOmittedAndNeverRetried() {
        Fixture fixture = await CreateFixtureAsync(
            secondRequired: false
        );
        var alpha = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            "alpha"
        );
        var optionalFailure = new FakeMaintainer(
            "zeta-profile",
            fixture.Policy.Roles[1].Target,
            exception: new InvalidOperationException("optional failure")
        );
        var orchestrator = new DerivedMemoryOrchestrator(
            fixture.Repository
        );
        DerivedMemoryOrchestrationRequest firstRequest = Request(
            fixture,
            Execution(fixture, 0, alpha),
            Execution(fixture, 1, optionalFailure)
        );

        DerivedMemoryOrchestrationResult first =
            await orchestrator.RunAsync(
                fixture.Engine,
                firstRequest
            );

        Assert.Equal(
            DerivedMemoryOrchestrationStatus.Published,
            first.Status
        );
        Assert.Single(first.PublishedSet!.Members);
        DerivedMemoryOrchestrationFinalization finalization =
            await fixture.Repository.Orchestrations
                .TryReadFinalizationAsync(first.Transaction)
            ?? throw new Xunit.Sdk.XunitException(
                "Expected durable finalization."
            );
        Assert.Equal(["zeta"], finalization.OmittedOptionalRoleIds);
        var mustNotRunAlpha = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException("reopened alpha")
        );
        var mustNotRunOptional = new FakeMaintainer(
            "zeta-profile",
            fixture.Policy.Roles[1].Target,
            exception: new InvalidOperationException("reopened optional")
        );

        DerivedMemoryOrchestrationResult reopened =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    Execution(fixture, 0, mustNotRunAlpha),
                    Execution(fixture, 1, mustNotRunOptional)
                )
            );

        Assert.Equal(first.PublishedSet.SetId, reopened.PublishedSet!.SetId);
        Assert.Equal(0, mustNotRunAlpha.CallCount);
        Assert.Equal(0, mustNotRunOptional.CallCount);
    }

    [Fact]
    public async Task FinalizationWithoutSetResumesPublicationWithoutProducer() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution execution = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "precommitted"
            )
        );
        DerivedMemoryOrchestrationTransaction transaction =
            await fixture.Repository.Orchestrations.GetOrCreateAsync(
                fixture.Epoch,
                fixture.Policy,
                [execution.Provisioning]
            );
        var runner = new DerivedMemoryMaintainerRunner(
            fixture.Repository
        );
        DerivedMemoryMaintainerSnapshot snapshot =
            await runner.PrepareAsync(
                fixture.Engine,
                fixture.Epoch.EpochId
            );
        DerivedMemoryRoleProvisioning provision =
            execution.Provisioning;
        DerivedMemoryArtifact artifact =
            (await runner.RunPreparedAsync(
                snapshot,
                new DerivedMemoryMaintainerRunRequest(
                    fixture.Epoch.EpochId,
                    provision.RoleId,
                    provision.ProfileId,
                    provision.Producer,
                    provision.ProducerFingerprint,
                    provision.PromptFingerprint,
                    provision.ModelFingerprint,
                    provision.CandidateId,
                    provision.AttemptId
                ),
                execution.Maintainer!
            )).Artifact;
        var settlement = new DerivedMemoryRoleSettlement(
            transaction.TransactionId,
            provision.RoleId,
            artifact.ArtifactId,
            artifact.Outcome
        );
        _ = await fixture.Repository.Orchestrations.SettleAsync(
            transaction,
            settlement
        );
        var publication = new DerivedArtifactSetPublicationRequest(
            fixture.Policy,
            transaction,
            snapshot.AnchorSetups,
            [new(provision.RoleId, artifact.ArtifactId)],
            transaction.InputSetId
        );
        DerivedArtifactSet prepared =
            await fixture.Repository.ArtifactSets
                .PreparePublicationAsync(
                    fixture.Engine,
                    publication
                );
        _ = await fixture.Repository.Orchestrations
            .GetOrCreateFinalizationAsync(
                transaction,
                snapshot.AnchorSetups,
                [settlement],
                prepared.SetId
            );
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        DerivedMemoryValidationReport pending =
            await fixture.Repository.ValidateAsync(fixture.Engine);
        Assert.Equal(0, pending.ArtifactSetCount);
        Assert.Equal(1, pending.OrchestrationFinalizationCount);
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException("producer reopened")
        );

        DerivedMemoryOrchestrationResult resumed =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(
                        fixture,
                        execution with {
                            Maintainer = mustNotRun
                        }
                    )
                );

        Assert.Equal(
            DerivedMemoryOrchestrationStatus.Published,
            resumed.Status
        );
        Assert.Equal(prepared.SetId, resumed.PublishedSet!.SetId);
        Assert.Equal(0, mustNotRun.CallCount);
    }

    [Fact]
    public async Task ExistingSetWithoutPointerRebuildsPointerOnResume() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution execution = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "alpha"
            )
        );
        var orchestrator = new DerivedMemoryOrchestrator(
            fixture.Repository
        );
        DerivedMemoryOrchestrationResult first =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(fixture, execution)
            );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException("producer reopened")
        );

        DerivedMemoryOrchestrationResult resumed =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    execution with {
                        Maintainer = mustNotRun
                    }
                )
            );

        Assert.Equal(first.PublishedSet!.SetId, resumed.PublishedSet!.SetId);
        Assert.Equal(0, mustNotRun.CallCount);
        Assert.Equal(
            first.PublishedSet.SetId,
            (await fixture.Repository.ArtifactSets.TryReadLatestAsync(
                fixture.Policy,
                fixture.Epoch.LineageKey
            ))!.SetId
        );
    }

    [Fact]
    public async Task ValidationRejectsFinalizationPointingPastExistingSet() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryOrchestrationResult result =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(
                        fixture,
                        Execution(
                            fixture,
                            0,
                            new FakeMaintainer(
                                "alpha-profile",
                                fixture.Policy.Roles[0].Target,
                                "alpha"
                            )
                        )
                    )
                );
        string finalizationPath = Path.Combine(
            fixture.Repository.Orchestrations.FinalizationsDirectory,
            $"{result.Transaction.TransactionId}.json"
        );
        string json = await File.ReadAllTextAsync(finalizationPath);
        await File.WriteAllTextAsync(
            finalizationPath,
            json.Replace(
                result.PublishedSet!.SetId,
                "das_" + new string('f', 64),
                StringComparison.Ordinal
            )
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ValidateAsync(
                fixture.Engine
            )
        );
    }

    [Fact]
    public async Task ReopenFailsIfFinalizedSettlementWasDeleted() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        DerivedMemoryRoleExecution execution = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "alpha"
            )
        );
        DerivedMemoryOrchestrationResult result =
            await new DerivedMemoryOrchestrator(fixture.Repository)
                .RunAsync(
                    fixture.Engine,
                    Request(fixture, execution)
                );
        string settlementDirectory = Path.Combine(
            fixture.Repository.Orchestrations.SettlementsDirectory,
            result.Transaction.TransactionId
        );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            settlementDirectory
        )));
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException("producer reopened")
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await new DerivedMemoryOrchestrator(
                fixture.Repository
            ).RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    execution with {
                        Maintainer = mustNotRun
                    }
                )
            )
        );
        Assert.Equal(0, mustNotRun.CallCount);
    }

    [Fact]
    public async Task CompletedTransactionReopensAfterLatestAdvances() {
        Fixture firstFixture = await CreateFixtureAsync(roleCount: 1);
        var firstMaintainer = new FakeMaintainer(
            "alpha-profile",
            firstFixture.Policy.Roles[0].Target,
            "first epoch"
        );
        var orchestrator = new DerivedMemoryOrchestrator(
            firstFixture.Repository
        );
        DerivedMemoryOrchestrationRequest firstRequest = Request(
            firstFixture,
            Execution(firstFixture, 0, firstMaintainer)
        );
        DerivedMemoryOrchestrationResult first =
            await orchestrator.RunAsync(
                firstFixture.Engine,
                firstRequest
            );
        firstFixture.Engine.AppendObservation("new raw history");
        _ = firstFixture.Engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("new answer")
            ]),
            new CompletionDescriptor("import", "v1", "model-a")
        );
        DerivedArtifactEpochPlan secondEpoch =
            (await firstFixture.Repository.EpochPlanner.PlanAsync(
                firstFixture.Engine,
                new(
                    firstFixture.Epoch.LineageKey,
                    firstFixture.Epoch.CoherenceGroup,
                    firstFixture.Epoch.EpochId,
                    first.PublishedSet!.SetId
                )
            )).Epoch!;
        Fixture secondFixture = firstFixture with {
            Epoch = secondEpoch,
            RawHead = firstFixture.Engine.ReadCurrentLineageHeaders()
                .CapturedHead
        };
        DerivedMemoryOrchestrationResult second =
            await orchestrator.RunAsync(
                secondFixture.Engine,
                Request(
                    secondFixture,
                    Execution(
                        secondFixture,
                        0,
                        new FakeMaintainer(
                            "alpha-profile",
                            secondFixture.Policy.Roles[0].Target,
                            "second epoch"
                        )
                    )
                )
            );
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            firstFixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException(
                "completed transaction reopened producer"
            )
        );

        DerivedMemoryOrchestrationResult reopened =
            await orchestrator.RunAsync(
                firstFixture.Engine,
                Request(
                    firstFixture,
                    Execution(firstFixture, 0, mustNotRun)
                )
            );

        Assert.Equal(first.PublishedSet.SetId, reopened.PublishedSet!.SetId);
        Assert.NotEqual(
            reopened.PublishedSet.SetId,
            second.PublishedSet!.SetId
        );
        Assert.Equal(0, mustNotRun.CallCount);
        Assert.Equal(
            second.PublishedSet.SetId,
            (await firstFixture.Repository.ArtifactSets
                .TryReadLatestAsync(
                    firstFixture.Policy,
                    firstFixture.Epoch.LineageKey
                ))!.SetId
        );
    }

    [Fact]
    public async Task MissingPointerWithDescendantRebuildsTipWithoutRollback() {
        Fixture firstFixture = await CreateFixtureAsync(roleCount: 1);
        var orchestrator = new DerivedMemoryOrchestrator(
            firstFixture.Repository
        );
        DerivedMemoryRoleExecution firstExecution = Execution(
            firstFixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                firstFixture.Policy.Roles[0].Target,
                "first"
            )
        );
        DerivedMemoryOrchestrationResult first =
            await orchestrator.RunAsync(
                firstFixture.Engine,
                Request(firstFixture, firstExecution)
            );
        firstFixture.Engine.AppendObservation("next epoch");
        _ = firstFixture.Engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("next answer")]),
            new CompletionDescriptor("import", "v1", "model-a")
        );
        DerivedArtifactEpochPlan secondEpoch =
            (await firstFixture.Repository.EpochPlanner.PlanAsync(
                firstFixture.Engine,
                new(
                    firstFixture.Epoch.LineageKey,
                    firstFixture.Epoch.CoherenceGroup,
                    firstFixture.Epoch.EpochId,
                    first.PublishedSet!.SetId
                )
            )).Epoch!;
        Fixture secondFixture = firstFixture with {
            Epoch = secondEpoch
        };
        DerivedMemoryOrchestrationResult second =
            await orchestrator.RunAsync(
                secondFixture.Engine,
                Request(
                    secondFixture,
                    Execution(
                        secondFixture,
                        0,
                        new FakeMaintainer(
                            "alpha-profile",
                            secondFixture.Policy.Roles[0].Target,
                            "second"
                        )
                    )
                )
            );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            firstFixture.Repository.ArtifactSets.LatestPointersDirectory
        )));
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            firstFixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException("producer reopened")
        );

        DerivedMemoryOrchestrationResult resumed =
            await orchestrator.RunAsync(
                firstFixture.Engine,
                Request(
                    firstFixture,
                    firstExecution with {
                        Maintainer = mustNotRun
                    }
                )
            );

        Assert.Equal(first.PublishedSet.SetId, resumed.PublishedSet!.SetId);
        Assert.Equal(0, mustNotRun.CallCount);
        Assert.Equal(
            second.PublishedSet!.SetId,
            (await firstFixture.Repository.ArtifactSets
                .TryReadLatestAsync(
                    firstFixture.Policy,
                    firstFixture.Epoch.LineageKey
                ))!.SetId
        );
    }

    [Fact]
    public async Task DivergentLatestFailsWithoutPointerRollback() {
        Fixture fixture = await CreateFixtureAsync(roleCount: 1);
        var orchestrator = new DerivedMemoryOrchestrator(
            fixture.Repository
        );
        DerivedMemoryRoleExecution original = Execution(
            fixture,
            0,
            new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "original"
            )
        );
        DerivedMemoryOrchestrationResult first =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(fixture, original)
            );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));
        DerivedMemoryRoleExecution divergent = original with {
            Provisioning = original.Provisioning with {
                PromptFingerprint =
                    "sha256:" + new string('f', 64)
            },
            Maintainer = new FakeMaintainer(
                "alpha-profile",
                fixture.Policy.Roles[0].Target,
                "divergent"
            )
        };
        DerivedMemoryOrchestrationResult fork =
            await orchestrator.RunAsync(
                fixture.Engine,
                Request(fixture, divergent)
            );
        var mustNotRun = new FakeMaintainer(
            "alpha-profile",
            fixture.Policy.Roles[0].Target,
            exception: new InvalidOperationException("producer reopened")
        );

        await Assert.ThrowsAsync<DerivedArtifactSetConcurrencyException>(
            async () => await orchestrator.RunAsync(
                fixture.Engine,
                Request(
                    fixture,
                    original with {
                        Maintainer = mustNotRun
                    }
                )
            )
        );
        Assert.Equal(0, mustNotRun.CallCount);
        Assert.Equal(
            fork.PublishedSet!.SetId,
            (await fixture.Repository.ArtifactSets.TryReadLatestAsync(
                fixture.Policy,
                fixture.Epoch.LineageKey
            ))!.SetId
        );
        Assert.NotEqual(
            first.PublishedSet!.SetId,
            fork.PublishedSet.SetId
        );
    }

    public void Dispose() {
        foreach (SessionJournalEngine engine in _engines) {
            engine.Dispose();
        }
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    private async ValueTask<Fixture> CreateFixtureAsync(
        int roleCount = 2,
        bool secondRequired = true
    ) {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-orchestrator-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        );
        _engines.Add(engine);
        for (int index = 0; index < 5; index++) {
            engine.AppendObservation($"observation {index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer {index}")
                ]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            new(
                "main",
                "memory-pack",
                "topology-v1",
                1,
                1,
                1,
                1_000
            ),
            null
        );
        DerivedArtifactEpochPlan epoch =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("main", "memory-pack", null, null)
            )).Epoch!;
        var roles = new[] {
            new DerivedArtifactSetRoleRequirement(
                "alpha",
                new(
                    MemoryPackCarrier.Observation,
                    "memory.alpha"
                )
            ),
            new DerivedArtifactSetRoleRequirement(
                "zeta",
                new(
                    MemoryPackCarrier.System,
                    "memory.zeta"
                ),
                secondRequired
            )
        };
        var policy = new DerivedArtifactSetPolicy(
            "orchestration-policy",
            "orchestration-policy-v1",
            epoch.CoherenceGroup,
            roles.Take(roleCount).ToArray()
        );
        return new Fixture(
            path,
            engine,
            repository,
            epoch,
            policy,
            engine.ReadCurrentLineageHeaders().CapturedHead
        );
    }

    private static DerivedMemoryOrchestrationRequest Request(
        Fixture fixture,
        params DerivedMemoryRoleExecution[] roles
    ) => new(fixture.Epoch.EpochId, fixture.Policy, roles);

    private static DerivedMemoryRoleExecution Execution(
        Fixture fixture,
        int roleIndex,
        IMemoryBlockMaintainer? maintainer,
        string mode = DerivedMemoryRoleExecutionModes.Produce,
        string? selectedArtifactId = null
    ) {
        DerivedArtifactSetRoleRequirement role =
            fixture.Policy.Roles[roleIndex];
        string profileId = role.RoleId + "-profile";
        return new(
            new DerivedMemoryRoleProvisioning(
                role.RoleId,
                profileId,
                role.Target,
                role.Required,
                "tests",
                Fingerprint,
                Fingerprint,
                Fingerprint,
                mode,
                "candidate",
                "attempt",
                selectedArtifactId
            ),
            maintainer
        );
    }

    private static EventAddress ReadRawHead(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        return journal.GetHead(main)!.Value;
    }

    private sealed record Fixture(
        string Path,
        SessionJournalEngine Engine,
        DerivedMemoryRepository Repository,
        DerivedArtifactEpochPlan Epoch,
        DerivedArtifactSetPolicy Policy,
        EventAddress RawHead
    );

    private sealed class ParallelGate(int count) {
        private readonly TaskCompletionSource _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = count;

        public Task AllEntered => _allEntered.Task;

        public async Task EnterAsync(CancellationToken cancellationToken) {
            if (Interlocked.Decrement(ref _remaining) == 0) {
                _allEntered.TrySetResult();
            }
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class FakeMaintainer(
        string id,
        MemoryPackBlockPath target,
        string text = "",
        ParallelGate? gate = null,
        Exception? exception = null
    ) : IMemoryBlockMaintainer {
        public string Id { get; } = id;
        public MemoryPackBlockPath Target { get; } = target;
        public int CallCount { get; private set; }
        public RecentHistorySlice? History { get; private set; }

        public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            CallCount++;
            History = request.RecentHistory;
            if (gate is not null) {
                await gate.EnterAsync(ct);
            }
            if (exception is not null) {
                throw exception;
            }
            return new(
                Id,
                Target,
                new MemoryPackBlock(text)
            );
        }
    }

    private sealed class CancellationMaintainer(
        string id,
        MemoryPackBlockPath target
    ) : IMemoryBlockMaintainer {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;
        public MemoryPackBlockPath Target { get; } = target;
        public Task Entered => _entered.Task;

        public async ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            _ = request;
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}

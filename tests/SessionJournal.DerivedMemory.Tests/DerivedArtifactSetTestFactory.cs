namespace Atelia.SessionJournal.DerivedMemory.Tests;

internal static class DerivedArtifactSetTestFactory {
    public static async ValueTask<DerivedMemoryOrchestrationTransaction>
        CreateSettledTransactionAsync(
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

    public static async ValueTask<DerivedArtifactSet>
        FinalizeAndPublishAsync(
        DerivedMemoryRepository repository,
        SessionJournalEngine engine,
        DerivedArtifactSetPolicy policy,
        DerivedMemoryOrchestrationTransaction transaction,
        SessionContextAnchorSetupReferences anchorSetups,
        IReadOnlyList<DerivedArtifactSetMemberSelection> members
    ) {
        var publication = new DerivedArtifactSetPublicationRequest(
            policy,
            transaction,
            anchorSetups,
            members,
            transaction.InputSetId
        );
        DerivedArtifactSet prepared =
            await repository.ArtifactSets.PreparePublicationAsync(
                engine,
                publication
            );
        IReadOnlyDictionary<string, DerivedMemoryRoleSettlement> durable =
            (await repository.Orchestrations.ReadSettlementsAsync(
                transaction
            )).ToDictionary(
                static settlement => settlement.RoleId,
                StringComparer.Ordinal
            );
        _ = await repository.Orchestrations.GetOrCreateFinalizationAsync(
            transaction,
            anchorSetups,
            members.Select(member => durable[member.RoleId]).ToArray(),
            prepared.SetId
        );
        return await repository.ArtifactSets.PublishAsync(
            engine,
            publication
        );
    }
}

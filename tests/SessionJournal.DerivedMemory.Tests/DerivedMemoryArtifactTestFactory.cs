using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

internal static class DerivedMemoryArtifactTestFactory {
    private static readonly string EpochId = "dae_" + new string('1', 64);
    private static readonly string Fingerprint =
        "sha256:" + new string('2', 64);

    public static async ValueTask<DerivedMemoryArtifact> WriteGenesisAsync(
        DerivedMemoryRepository repository,
        string roleId,
        string profileId,
        ContextHeaderBlockPath target,
        string text,
        EventAddress anchor,
        SessionContextAnchorSetupReferences setups,
        string candidateId = "candidate-1",
        EventAddress? sourceStartExclusive = null,
        SessionContextAnchorSetupReferences? anchorSetups = null
    ) {
        DerivedMemoryArtifactWriteRequest request =
            CreateGenesisRequest(
                roleId,
                profileId,
                target,
                text,
                anchor,
                setups,
                candidateId,
                sourceStartExclusive,
                anchorSetups
            );
        return await repository.Artifacts.WriteCandidateAsync(request);
    }

    public static DerivedMemoryArtifactWriteRequest
        CreateGenesisRequest(
        string roleId,
        string profileId,
        ContextHeaderBlockPath target,
        string text,
        EventAddress anchor,
        SessionContextAnchorSetupReferences setups,
        string candidateId = "candidate-1",
        EventAddress? sourceStartExclusive = null,
        SessionContextAnchorSetupReferences? anchorSetups = null
    ) {
        var pack = new ContextHeaderPack();
        var draft = new ContextHeaderPackDraft(pack);
        draft.UpsertBlock(target, text);
        return new DerivedMemoryArtifactWriteRequest(
            EpochId,
            Fingerprint,
            roleId,
            profileId,
            "tests",
            Fingerprint,
            Fingerprint,
            Fingerprint,
            candidateId,
            "attempt-1",
            anchor,
            sourceStartExclusive ?? setups.RuntimeConfig.Address,
            anchor,
            anchor,
            setups,
            anchorSetups ?? setups,
            null,
            null,
            [],
            target,
            draft.Build()
        );
    }
}

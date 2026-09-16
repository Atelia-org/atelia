using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaHistoricalAgentControlProfileLazyLoadingTests {
    [Fact]
    public async Task LoadedConfigOwnerDefersProfileBytesUntilFrozenExactBind() {
        RecapGridAgentControlProfile profile = CreateProfile("one", 1);
        var factory = new OwnerFactory();
        await using GalateaTestHost host = GalateaTestHost.Create(factory,
            normalizer: null, agentControlProfile: profile);
        _ = await CreatePreparedAsync(host.SessionDirectory, profile);
        string profilePath = Path.Combine(Path.GetDirectoryName(host.ConfigPath)!,
            "recap-grid-profile.json");
        File.WriteAllBytes(profilePath, "{"u8.ToArray());

        GalateaConfig malformed = GalateaConfigLoader.Load(host.ConfigPath);
        await using (var owner = new GalateaCompletionOwner(malformed, factory)) {
            using SessionJournalEngine engine = SessionJournalEngine.Open(
                host.SessionDirectory);
            var frozen = Assert.IsType<SessionRuntimeRecoveryRequirements
                .FrozenCompletionRequired>(engine.InspectRuntimeRecoveryRequirements());
            Assert.Throws<InvalidDataException>(() => owner.RecapGrid.BindPrepared(
                engine, frozen));
        }
        Assert.Equal(0, factory.Dispatches);

        File.WriteAllBytes(profilePath, profile.ToCanonicalBytes());
        GalateaConfig valid = GalateaConfigLoader.Load(host.ConfigPath);
        await using var validOwner = new GalateaCompletionOwner(valid, factory);
        using SessionJournalEngine validEngine = SessionJournalEngine.Open(
            host.SessionDirectory);
        var validFrozen = Assert.IsType<SessionRuntimeRecoveryRequirements
            .FrozenCompletionRequired>(validEngine.InspectRuntimeRecoveryRequirements());
        await using GalateaRecapGridTurn bound = validOwner.RecapGrid.BindPrepared(
            validEngine, validFrozen);
        Assert.Equal(profile.RuntimeIdentity, bound.AgentControl!.RuntimeIdentity);
        Assert.Equal(0, factory.Dispatches);
    }
    [Fact]
    public void ExactBindLoadsConfiguredProfilesOnceAndCachesTheRegistry() {
        RecapGridAgentControlProfile profile = CreateProfile("one", 1);
        RecapGridAgentControlProfile other = CreateProfile("two", 2);
        int reads = 0;
        var resolver = new GalateaHistoricalAgentControlProfiles(
            ["one.json", "two.json"],
            path => {
                reads++;
                return path == "one.json"
                    ? profile.ToCanonicalBytes()
                    : other.ToCanonicalBytes();
            });

        Assert.Equal(0, reads);
        Assert.True(resolver.TryBindExact(profile.RuntimeIdentity, out var first));
        Assert.Equal(profile.ProfileId, first.ProfileId);
        Assert.Equal(profile.RuntimeIdentity, first.RuntimeIdentity);
        Assert.Equal(2, reads);
        Assert.True(resolver.TryGet("one", out var second));
        Assert.Equal(profile.ProfileId, second.ProfileId);
        Assert.Equal(profile.RuntimeIdentity, second.RuntimeIdentity);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void DecodeFailureIsDeferredAndCached() {
        int reads = 0;
        byte[] bytes = "{"u8.ToArray();
        var resolver = new GalateaHistoricalAgentControlProfiles(
            ["broken.json"],
            _ => {
                reads++;
                return bytes;
            });

        Assert.Equal(0, reads);
        Assert.Throws<InvalidDataException>(() => resolver.TryGet("one", out _));
        Assert.Equal(1, reads);
        bytes = CreateProfile("one", 1).ToCanonicalBytes();
        Assert.Throws<InvalidDataException>(() => resolver.TryGet("one", out _));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void DuplicateIdentitiesAreRejectedOnlyAtFirstExactLookup() {
        RecapGridAgentControlProfile first = CreateProfile("duplicate", 1);
        RecapGridAgentControlProfile second = CreateProfile("duplicate", 2);
        int reads = 0;
        var resolver = new GalateaHistoricalAgentControlProfiles(
            ["first.json", "second.json"],
            path => {
                reads++;
                return path == "first.json"
                    ? first.ToCanonicalBytes()
                    : second.ToCanonicalBytes();
            });

        Assert.Equal(0, reads);
        Assert.Throws<ArgumentException>(() => resolver.TryBindExact(
            first.RuntimeIdentity, out _));
        Assert.Equal(2, reads);
    }

    private static RecapGridAgentControlProfile CreateProfile(
        string profileId,
        int discriminator
    ) {
        Assert.True(RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
            RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
            out RecapGridControlRegistrationBundle? bundle));
        return RecapGridAgentControlProfile.Create(profileId,
            new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                [bundle!.Families[0].Digest],
                bundle.Definitions.Select(static item =>
                    item.Capability.CapabilityFingerprint),
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: discriminator,
                maximumProjectedCalls: 1_024));
    }

    private static async Task<EventAddress> CreatePreparedAsync(string path,
        RecapGridAgentControlProfile profile) {
        var client = new OwnerClient();
        CompletionConnectionConfig connection = new("test", "openai-chat",
            "model-a", "openai-chat/strict", "http://localhost:8000/",
            ApiKey: "test-key");
        CompletionDispatchIdentity identity = CompletionDispatchIdentityFactory
            .Create(connection, client);
        using SessionJournalEngine engine = SessionJournalEngine.OpenForTest(path,
            new SessionRuntime(client, CompletionTarget:
                new SessionCompletionTargetIdentity(identity.ConnectionId,
                    identity.Kind, identity.ConnectionFingerprint),
                ContextCandidateSource: new EmptySource(),
                InputProjector: GalateaInputProjector.Instance),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted));
        using RecapGridAgentControlHandle agent = Assert.IsType<
            RecapGridAgentControlOpenResult.Opened>(RecapGridAgentControlFactory.Bind(
                engine.ReadView, profile,
                new O200kBaseHistoryUnitLoadEstimator())).Handle;
        engine.UseRuntime(new SessionRuntime(client, agent.ToolSession,
            new SessionCompletionTargetIdentity(identity.ConnectionId, identity.Kind,
                identity.ConnectionFingerprint), ToolRuntimeIdentity: agent.RuntimeIdentity,
            ContextCandidateSource: new EmptySource(),
            InputProjector: GalateaInputProjector.Instance));
        await Assert.ThrowsAsync<SessionJournalFailpointException>(() =>
            engine.SendAsync("freeze", CancellationToken.None));
        return engine.ReadCurrentHead()!.Value;
    }

    private sealed class EmptySource : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request, CancellationToken ct) =>
            ValueTask.FromResult(new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.EmptyLineage, null));
        public ValueTask<SessionContextCandidateMaterializationResult> MaterializeAsync(
            SessionContextCandidateDescriptor descriptor, CancellationToken ct) =>
            throw new InvalidOperationException();
    }

    private sealed class OwnerFactory : ICompletionClientFactory {
        private readonly OwnerClient _client = new();
        internal int Dispatches => _client.Dispatches;
        public ICompletionClient Create(CompletionConnectionConfig connection) => _client;
    }

    private sealed class OwnerClient : ICompletionClient {
        private int _dispatches;
        internal int Dispatches => Volatile.Read(ref _dispatches);
        public string Name => "owner-client";
        public string ApiSpecId => "openai-chat-v1";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            Interlocked.Increment(ref _dispatches);
            throw new InvalidOperationException("frozen bind must not dispatch");
        }
    }
}

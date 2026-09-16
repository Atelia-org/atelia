using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Builds synthetic old-format events in a stopped, test-owned journal.
/// It appends a replacement lineage; existing event bytes are never rewritten.</summary>
internal static class LegacyPreparedV7Fixture {
    /// <summary>Appends historical dispatch evidence explicitly; new execution never emits it.</summary>
    internal static EventAddress AppendStarted(string repository, EventAddress prepared) {
        using Journal journal = Journal.OpenExisting(repository);
        var request = SessionPreparedRequestReconstructor.Reconstruct(
            new SessionJournalEventReader(journal), prepared, projector: GalateaInputProjector.Instance);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        return journal.CommitToRef(main, prepared,
            SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted,
                new CompletionAttemptStartedBody(SessionRequestManifestDefaults.CanonicalRequestCodecId,
                    SessionRequestCanonicalizer.CreateCommitment(request.Request))),
            opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted, hint: default).Unwrap().EventAddress;
    }

    internal static EventAddress AppendFailed(string repository, EventAddress prepared) {
        EventAddress started = AppendStarted(repository, prepared);
        using Journal journal = Journal.OpenExisting(repository);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        return journal.CommitToRef(main, started,
            SessionEventCodec.Encode(SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(CompletionTerminationKind.Failed, "legacy-fixture", null, [])),
            opaqueEventKind: (uint)SessionEventKind.CompletionAttemptFailed, hint: default).Unwrap().EventAddress;
    }

    /// <summary>Seeds an empty test repository with actual v1 raw inputs and a nonempty v7 exact request.</summary>
    internal static EventAddress CreatePending(string repository, CompletionConnectionConfig connection,
        ICompletionClient client, bool started, string adapterLabel) {
        const string systemPrompt = "Historical Galatea character instructions.";
        string observationText = GalateaUserMessageEnvelope.Wrap("fixture observation");
        EventAddress runtimeAddress;
        EventAddress promptAddress;
        EventAddress created;
        byte[] runtimePayload = SessionEventCodec.Encode(SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(connection.ModelId, connection.CompletionSurfaceId,
                SessionJournalDefaults.Schema, new SessionDerivedContextConfiguration(0)));
        byte[] promptPayload = Encoding.UTF8.GetBytes("{\"v\":1,\"body\":{\"content\":" + JsonSerializer.Serialize(systemPrompt) + "}}");
        using (Journal journal = Journal.CreateNew(repository)) {
            RefId branch = journal.CreateBranch(SessionJournalDefaults.MainBranchName, null).Unwrap();
            runtimeAddress = journal.CommitToRef(branch, null, runtimePayload,
                opaqueEventKind: (uint)SessionEventKind.RuntimeConfigSetup, hint: default).Unwrap().EventAddress;
            promptAddress = journal.CommitToRef(branch, runtimeAddress, promptPayload,
                opaqueEventKind: (uint)SessionEventKind.SystemPromptSetup, hint: default).Unwrap().EventAddress;
            created = journal.CommitToRef(branch, promptAddress,
                SessionEventCodec.Encode(SessionEventKind.SessionCreated, new SessionCreatedBody(SessionCreationOrigin.Native)),
                opaqueEventKind: (uint)SessionEventKind.SessionCreated, hint: default).Unwrap().EventAddress;
        }
        using (var engine = SessionJournalEngine.Open(repository)) { GalateaTestHost.ProvisionRawOnlyRecapGrid(engine); }
        using Journal output = Journal.OpenExisting(repository);
        RefId main = output.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        Assert.Equal(created, output.GetHead(main));
        byte[] observationPayload = Encoding.UTF8.GetBytes("{\"v\":1,\"body\":{\"content\":" + JsonSerializer.Serialize(observationText) + "}}");
        EventAddress observation = output.CommitToRef(main, created, observationPayload,
            opaqueEventKind: (uint)SessionEventKind.ObservationAccepted, hint: default).Unwrap().EventAddress;
        var setups = new SessionGoverningSetupReferences(
            new(runtimeAddress, 2, SessionRequestCanonicalizer.Sha256Hex(runtimePayload)),
            new(promptAddress, 1, SessionRequestCanonicalizer.Sha256Hex(promptPayload)));
        SessionRequestArtifactContextSnapshot[] snapshots = [
            new("", "Historical world recap: the northern path is open.", ""),
            new("", "", "Historical autobiography: I remember choosing to explore.")
        ];
        var expanded = SessionCoherentRequestRecipe.Expand(systemPrompt, SessionCoherentRequestRecipe.Aggregate(snapshots));
        var request = new CompletionRequest(connection.ModelId,
            new CompletionPromptPrefix(expanded.SystemPrompt, CompletionOutputContract.ProviderDefault([]),
                [.. expanded.Context, new ObservationMessage(observationText)]), []);
        CompletionDispatchIdentity identity = CompletionDispatchIdentityFactory.Create(connection, client);
        var manifest = new CompletionRequestPreparedBody(
            new(SessionOperationalSemantics.BuildObservationCorrelationId(observation), "observation"), new(0),
            new(created, SessionRawRangeHasher.Compute(created, observation, [
                new(observation, created, (uint)SessionEventKind.ObservationAccepted, 1,
                    SessionRequestCanonicalizer.Sha256Hex(observationPayload))]), setups,
                [.. snapshots.Select(snapshot => new SessionRequestContextInput(SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot), snapshot))]),
            setups, new(connection.ModelId),
            new(SessionRequestManifestDefaults.ToolCodecId, SessionRequestCanonicalizer.ComputeToolSetSha256([]), [], null),
            new(SessionRequestManifestDefaults.RecipeId, SessionRequestManifestDefaults.CanonicalRequestCodecId),
            new(new(identity.ConnectionId, identity.Kind, identity.ConnectionFingerprint), client.Name, client.ApiSpecId),
            SessionRequestCanonicalizer.CreateCommitment(request));
        byte[] payload = Encode(manifest, adapterLabel);
        EventAddress prepared = output.CommitToRef(main, observation, payload,
            opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared, hint: default).Unwrap().EventAddress;
        Assert.Equal(SessionRequestCanonicalizer.Canonicalize(request), SessionPreparedRequestReconstructor.Reconstruct(output, prepared).CanonicalBytes);
        return started ? output.CommitToRef(main, prepared,
            SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted, new CompletionAttemptStartedBody()),
            opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted, hint: default).Unwrap().EventAddress : prepared;
    }

    private static byte[] Encode(CompletionRequestPreparedBody manifest, string adapterLabel) {
        JsonObject envelope = JsonNode.Parse(SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, manifest))!.AsObject();
        envelope["v"] = 7;
        envelope["body"]!["target"]!["connection"]!["requestAdapterFingerprint"] = adapterLabel;
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    internal static EventAddress ReplacePending(string repository,
        EventAddress expectedHead, string adapterLabel) {
        bool started;
        EventAddress prepared;
        using (var engine = SessionJournalEngine.OpenReadOnly(repository)) {
            Assert.Equal(expectedHead, engine.ReadCurrentHead());
            var events = new List<SessionJournalAuditEvent>();
            engine.ScanCheckedAuditEvents(events.Add);
            prepared = events.Last(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared).Address;
            started = events[^1].Kind == SessionEventKind.CompletionAttemptStarted;
            Assert.True(started || events[^1].Kind == SessionEventKind.CompletionRequestPrepared);
            var manifest = Assert.IsType<CompletionRequestPreparedBody>(SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared, engine.ReadPayloadBytes(prepared), out _));
            var rawEnd = events.Single(entry => entry.Address == prepared).Parent!.Value;
            if (engine.ResolveGoverningSetup(rawEnd).SystemPrompt.IsStructured
                || engine.ReadHistoryPlanningWindowAt(rawEnd, manifest.Plan.RawStartExclusive).Units
                    .Any(unit => unit.Message is SessionInputObservationMessage)) {
                throw new InvalidOperationException("A structured source cannot be retroactively relabeled as legacy v7. Seed an actual old-format fixture instead.");
            }
        }
        using Journal journal = Journal.OpenExisting(repository);
        SessionPreparedRequestReconstruction original =
            SessionPreparedRequestReconstructor.Reconstruct(journal, prepared);
        CompletionRequestPreparedBody exact = original.Manifest;
        if (exact.Commitment is null) {
            var snapshots = exact.Plan.SemanticContributions.Select(c => SessionContextContributionRenderer.RenderOneHot(c.Target, c.ExactText));
            exact = exact with {
                Plan = exact.Plan with { SemanticContributions = [], ExactContextInputs = [.. snapshots.Select(snapshot =>
                    new SessionRequestContextInput(SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot), snapshot))] },
                Recipe = exact.Recipe with { RecipeId = SessionRequestManifestDefaults.RecipeId },
                Commitment = SessionRequestCanonicalizer.CreateCommitment(original.Request)
            };
        }
        byte[] legacyBytes = Encode(exact, adapterLabel);
        _ = Assert.IsType<CompletionRequestPreparedBody>(SessionEventCodec.Decode(
            SessionEventKind.CompletionRequestPrepared, legacyBytes, out int version));
        Assert.Equal(7, version);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        Assert.True(journal.MoveRef(main, expectedHead, original.RawEndInclusive).Unwrap());
        EventAddress legacyPrepared = journal.CommitToRef(main, original.RawEndInclusive,
            legacyBytes, opaqueEventKind: (uint)SessionEventKind.CompletionRequestPrepared,
            hint: default).Unwrap().EventAddress;
        SessionPreparedRequestReconstruction rebuilt =
            SessionPreparedRequestReconstructor.Reconstruct(journal, legacyPrepared);
        Assert.Equal(original.CanonicalBytes, rebuilt.CanonicalBytes);
        Assert.Equal(exact.Commitment, rebuilt.Manifest.Commitment);
        return started
            ? journal.CommitToRef(main, legacyPrepared,
                SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody()),
                opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted,
                hint: default).Unwrap().EventAddress
            : legacyPrepared;
    }
}

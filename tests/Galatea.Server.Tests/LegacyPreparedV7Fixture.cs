using System.Text;
using System.Text.Json.Nodes;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Builds synthetic old-format events in a stopped, test-owned journal.
/// It appends a replacement lineage; existing event bytes are never rewritten.</summary>
internal static class LegacyPreparedV7Fixture {
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
        }
        using Journal journal = Journal.OpenExisting(repository);
        SessionPreparedRequestReconstruction original =
            SessionPreparedRequestReconstructor.Reconstruct(journal, prepared);
        JsonObject envelope = JsonNode.Parse(SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared, original.Manifest))!.AsObject();
        envelope["v"] = 7;
        envelope["body"]!["target"]!["connection"]!["requestAdapterFingerprint"] = adapterLabel;
        byte[] legacyBytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
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
        Assert.Equal(original.Manifest.Commitment, rebuilt.Manifest.Commitment);
        return started
            ? journal.CommitToRef(main, legacyPrepared,
                SessionEventCodec.Encode(SessionEventKind.CompletionAttemptStarted,
                    new CompletionAttemptStartedBody()),
                opaqueEventKind: (uint)SessionEventKind.CompletionAttemptStarted,
                hint: default).Unwrap().EventAddress
            : legacyPrepared;
    }
}

using System.Text.Json;

namespace Atelia.Galatea.Server;

/// <summary>Offline reset of future Codex routing; never contacts Codex or replays mail.</summary>
internal static class GalateaCodexBindingReset {
    internal const string CommandName = "reset-codex-binding";

    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == CommandName;

    internal static int Run(string[] args, TextWriter output, TextWriter error) {
        try {
            var invocation = Parse(args);
            GalateaConfig config = GalateaConfigLoader.Load(invocation.ConfigPath);
            GalateaCharacterConfig character = config.Characters.SingleOrDefault(
                value => value.CharacterId == invocation.CharacterId)
                ?? throw new InvalidDataException("The requested character is not configured.");
            GalateaCodexBindingResetResult result = Execute(character, config.Delegates.CodexRoute,
                invocation.AbandonActive, invocation.Apply);
            // JSON escapes identifiers without printing any mail, config, or provider content.
            output.WriteLine(JsonSerializer.Serialize(result));
            return result.Refused ? 2 : 0;
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            error.WriteLine("Codex binding reset failed: " + exception.Message);
            return 2;
        }
    }

    internal static GalateaCodexBindingResetResult Execute(
        GalateaCharacterConfig character, GalateaDelegateRouteConfig route, string? abandonActive, bool apply
    ) {
        var owner = new GalateaDelegationStoreOwner(character.CharacterId,
            GalateaDelegationSupervisor.CreateSessionRepositoryId(character.SessionDir));
        GalateaDelegationStoreLimits limits = GalateaDelegationSupervisor.CreateLimits(route);
        using (var readOnly = GalateaDelegationSqliteStore.OpenExistingReadOnly(
                   character.DelegationStateDir, owner, limits)) {
            GalateaCodexBindingResetResult preview = ExecuteOnStore(readOnly, abandonActive, apply: false);
            if (!apply || preview.Refused || preview.Outcome is "AlreadyUnbound" or "AlreadySatisfied") { return preview; }
        }
        using var writable = GalateaDelegationSqliteStore.OpenExisting(character.DelegationStateDir, owner, limits);
        // Reclassify under the writable lifetime lock; an earlier preview grants no authority.
        return ExecuteOnStore(writable, abandonActive, apply: true);
    }

    internal static GalateaCodexBindingResetResult ExecuteOnStore(
        GalateaDelegationSqliteStore store, string? abandonActive, bool apply
    ) {
        GalateaDelegationStateSnapshot before = store.ReadSnapshot();
        string outcome = Classify(before, abandonActive);
        bool refused = outcome is "Quarantined" or "ActiveMailRequiresDecision" or "ActiveDispatchMismatch";
        GalateaDelegationStateSnapshot after = before;
        if (apply && outcome is "WouldReset" or "WouldAbandonAndReset") {
            if (outcome == "WouldReset") {
                store.ResetIdleCodexBinding(before.Route.Revision);
            }
            else {
                GalateaOutboundMailSnapshot mail = before.Mails.Single(value => value.DispatchId == abandonActive);
                _ = store.FinishMailLocally(mail.DispatchId, mail.Revision, before.Route.Revision,
                    GalateaDelegationDurableContract.ResultUnconfirmedCode, resetBinding: true);
            }
            after = store.ReadSnapshot();
            outcome = outcome == "WouldReset" ? "Reset" : "AbandonedAndReset";
        }
        GalateaOutboundMailSnapshot? active = before.Mails.SingleOrDefault(
            value => value.DispatchId == before.Route.ActiveDispatchId);
        return new(outcome, refused, before.Owner.CharacterId, before.Route.State.ToString(),
            before.Route.Revision, before.Route.ThreadId, before.Route.ActiveDispatchId, active?.State.ToString(),
            before.Mails.Count(value => value.State == GalateaDurableMailState.Queued),
            before.Notices.Count(value => value.State != GalateaReplyNoticeState.Consumed),
            before.StoreRevision, after.StoreRevision, after.Route.State.ToString());
    }

    private static string Classify(GalateaDelegationStateSnapshot snapshot, string? abandonActive) {
        if (snapshot.Route.State == GalateaDelegationRouteState.Quarantined
            || snapshot.Mails.Any(value => value.State == GalateaDurableMailState.Quarantined)) { return "Quarantined"; }
        if (snapshot.Route.ActiveDispatchId is string activeId) {
            if (abandonActive is null) { return "ActiveMailRequiresDecision"; }
            return activeId == abandonActive ? "WouldAbandonAndReset" : "ActiveDispatchMismatch";
        }
        if (abandonActive is not null) {
            if (snapshot.Route.State == GalateaDelegationRouteState.Unbound) {
                GalateaOutboundMailSnapshot? mail = snapshot.Mails.SingleOrDefault(value => value.DispatchId == abandonActive);
                GalateaReplyNoticeSnapshot? notice = snapshot.Notices.SingleOrDefault(value => value.DispatchId == abandonActive);
                if (mail?.State == GalateaDurableMailState.TerminalFailed
                    && mail.TerminalCode == GalateaDelegationDurableContract.ResultUnconfirmedCode
                    && notice?.Kind == GalateaReplyNoticeKind.DeliveryFailure
                    && notice.Code == mail.TerminalCode && notice.Stage == mail.TerminalStage) { return "AlreadySatisfied"; }
            }
            return "ActiveDispatchMismatch";
        }
        return snapshot.Route.State == GalateaDelegationRouteState.Unbound ? "AlreadyUnbound" : "WouldReset";
    }

    private static (string ConfigPath, string CharacterId, string? AbandonActive, bool Apply) Parse(string[] args) {
        if (!IsInvocation(args)) { throw Usage(); }
        string? config = null, character = null, abandon = null;
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            switch (args[index]) {
                case "--config" when config is null && index + 1 < args.Length:
                    config = args[++index]; break;
                case "--character" when character is null && index + 1 < args.Length:
                    character = args[++index]; break;
                case "--abandon-active" when abandon is null && index + 1 < args.Length:
                    abandon = args[++index];
                    if (string.IsNullOrWhiteSpace(abandon)) { throw Usage(); }
                    break;
                case "--apply" when !apply:
                    apply = true; break;
                default: throw Usage();
            }
        }
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config)
            || string.IsNullOrWhiteSpace(character)) { throw Usage(); }
        GalateaStrictConfigReader.RequireExistingRegularFileNoFollow(Path.GetFullPath(config), "Galatea config");
        return (config, character, abandon, apply);
    }

    private static InvalidDataException Usage() => new(
        "Usage: Galatea.Server operator reset-codex-binding --config <absolute-path> "
        + "--character <characterId> [--abandon-active <dispatch-id>] [--apply]. "
        + "Default is dry-run. Stop Galatea and its sidecar first. Abandoning stops local waiting; it does not cancel remote work.");
}

internal sealed record GalateaCodexBindingResetResult(
    string Outcome, bool Refused, string CharacterId, string RouteState, long RouteRevision,
    string? ThreadId, string? ActiveDispatchId, string? ActiveMailState, int QueuedCount, int PendingNoticeCount,
    long BeforeStoreRevision, long StoreRevision, string AfterRouteState
);

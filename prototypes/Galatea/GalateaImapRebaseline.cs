using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

/// <summary>Offline, explicit UIDVALIDITY recovery. Never constructs the host or a model client.</summary>
internal static class GalateaImapRebaseline {
    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == "rebaseline-imap";

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) {
        try {
            Options options = Parse(args);
            GalateaConfig config = GalateaConfigLoader.Load(options.ConfigPath);
            GalateaCharacterConfig character = config.Characters.SingleOrDefault(
                value => value.CharacterId == options.CharacterId)
                ?? throw new InvalidDataException("IMAP character is not configured.");
            if (!config.Imap.Accounts.TryGetValue(options.CharacterId, out var account)) {
                throw new InvalidDataException("IMAP account is not configured.");
            }
            var owner = new GalateaDelegationStoreOwner(character.CharacterId,
                GalateaDelegationSupervisor.CreateSessionRepositoryId(character.SessionDir));
            var limits = GalateaDelegationSupervisor.CreateLimits(config.Delegates.CodexRoute);
            using GalateaDelegationSqliteStore store = options.Apply
                ? GalateaDelegationSqliteStore.OpenExistingForImapMaintenance(character.DelegationStateDir, owner, limits)
                : GalateaDelegationSqliteStore.OpenExistingReadOnly(character.DelegationStateDir, owner, limits);
            string reference = GalateaImapConfig.AccountReference(character.CharacterId, account);
            GalateaImapCheckpointSnapshot checkpoint = store.ReadImapCheckpoint(reference)
                ?? throw new InvalidDataException("IMAP baseline has not been established.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(config.Imap.TimeoutSeconds));
            var transport = new GalateaNetworkImapTransport(config.Imap);
            await using IGalateaImapConnection connection = await transport.OpenAsync(account, deadline.Token)
                .ConfigureAwait(false);
            if (connection.UidValidity == 0 || connection.UidNext is not { } next || next == 0) {
                throw new InvalidDataException("IMAP baseline metadata is unavailable.");
            }
            if (options.Apply) {
                if (checkpoint.UidValidity != options.ExpectedValidity
                    || checkpoint.ScannedThroughUid != options.ExpectedCursor
                    || checkpoint.Revision != options.ExpectedRevision
                    || connection.UidValidity != options.NewValidity
                    || next != options.NewUidNext
                    || checkpoint.BlockedCode != "IMAP_UIDVALIDITY_CHANGED") {
                    throw new InvalidDataException("IMAP rebaseline preview is stale or checkpoint is not blocked.");
                }
                checkpoint = store.RebaselineImapCheckpoint(checkpoint,
                    connection.UidValidity, next - 1, DateTimeOffset.UtcNow);
                output.WriteLine($"IMAP rebaseline applied: uidValidity={checkpoint.UidValidity}, cursor={checkpoint.ScannedThroughUid}, revision={checkpoint.Revision}. Existing messages are skipped; old inbox facts are retained.");
            }
            else {
                output.WriteLine($"IMAP rebaseline preview: expectedValidity={checkpoint.UidValidity}, expectedCursor={checkpoint.ScannedThroughUid}, expectedRevision={checkpoint.Revision}, newValidity={connection.UidValidity}, newUidNext={next}, blockedCode={checkpoint.BlockedCode ?? "NONE"}.");
                output.WriteLine("No state changed. Stop the host and back up the store before applying. Apply requires all five preview values and skips existing messages in the new namespace.");
            }
            return 0;
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            error.WriteLine("IMAP rebaseline failed: exceptionType=" + exception.GetType().FullName + ".");
            return 2;
        }
    }

    private sealed record Options(string ConfigPath, string CharacterId, bool Apply,
        uint? ExpectedValidity, uint? ExpectedCursor, long? ExpectedRevision,
        uint? NewValidity, uint? NewUidNext);

    private static Options Parse(string[] args) {
        if (!IsInvocation(args)) { throw Usage(); }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            if (args[index] == "--apply" && !apply) { apply = true; continue; }
            if (args[index] is not ("--config" or "--character" or "--expected-validity"
                or "--expected-cursor" or "--expected-revision" or "--new-validity" or "--new-uidnext")
                || index + 1 >= args.Length || !values.TryAdd(args[index], args[++index])) {
                throw Usage();
            }
        }
        if (!values.TryGetValue("--config", out string? path) || !Path.IsPathFullyQualified(path)
            || !values.TryGetValue("--character", out string? character) || string.IsNullOrWhiteSpace(character)) {
            throw Usage();
        }
        uint? Number(string key, bool nonzero) {
            if (!values.TryGetValue(key, out string? value)) { return null; }
            if (!uint.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out uint number)
                || nonzero && number == 0) { throw Usage(); }
            return number;
        }
        uint? validity = Number("--expected-validity", true);
        uint? cursor = Number("--expected-cursor", false);
        uint? newValidity = Number("--new-validity", true);
        uint? next = Number("--new-uidnext", true);
        long? revision = null;
        if (values.TryGetValue("--expected-revision", out var revisionText)) {
            if (!long.TryParse(revisionText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long number) || number < 0) { throw Usage(); }
            revision = number;
        }
        bool complete = validity.HasValue && cursor.HasValue && revision.HasValue
            && newValidity.HasValue && next.HasValue;
        if (apply ? !complete : values.Count != 2) { throw Usage(); }
        return new(path, character, apply, validity, cursor, revision, newValidity, next);
    }

    private static InvalidDataException Usage() => new(
        "Usage: operator rebaseline-imap --config <absolute-path> --character <id> "
        + "[--apply --expected-validity <uint> --expected-cursor <uint> --expected-revision <long> "
        + "--new-validity <uint> --new-uidnext <uint>]. Default is read-only preview.");
}

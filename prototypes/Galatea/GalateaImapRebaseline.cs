using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

/// <summary>Offline, explicit UIDVALIDITY recovery. Never constructs the host or a model client.</summary>
internal static class GalateaImapRebaseline {
    internal static bool IsInvocation(string[] args) => args.Length >= 2
        && args[0] == "operator" && args[1] == "rebaseline-imap";

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        IGalateaImapTransport? transportForTest = null) {
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
            IGalateaImapTransport transport = transportForTest ?? new GalateaNetworkImapTransport(config.Imap);
            await using IGalateaImapConnection connection = await transport.OpenAsync(account, deadline.Token)
                .ConfigureAwait(false);
            uint newValidity = connection.UidValidity;
            uint? nativeNextBefore = connection.UidNext;
            if (newValidity == 0) {
                throw new InvalidDataException("IMAP baseline metadata is unavailable.");
            }
            uint newCursor = await connection.ReadScanUpperUidAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            uint? nativeNextAfter = connection.UidNext;
            if (connection.UidValidity != newValidity || nativeNextAfter == 0
                || nativeNextAfter is { } nativeAfter && newCursor >= nativeAfter
                || nativeNextBefore is { } nativeBefore
                    && (nativeNextAfter is null || nativeNextAfter.Value < nativeBefore)) {
                throw new InvalidDataException("IMAP namespace or UIDNEXT changed while reading its scan horizon.");
            }
            if (options.Apply) {
                if (checkpoint.UidValidity != options.ExpectedValidity
                    || checkpoint.ScannedThroughUid != options.ExpectedCursor
                    || checkpoint.Revision != options.ExpectedRevision
                    || newValidity != options.NewValidity
                    || newCursor != options.NewCursor
                    || checkpoint.BlockedCode != "IMAP_UIDVALIDITY_CHANGED") {
                    throw new InvalidDataException("IMAP rebaseline preview is stale or checkpoint is not blocked.");
                }
                checkpoint = store.RebaselineImapCheckpoint(checkpoint,
                    newValidity, newCursor, DateTimeOffset.UtcNow);
                output.WriteLine($"IMAP rebaseline applied: uidValidity={checkpoint.UidValidity}, cursor={checkpoint.ScannedThroughUid}, revision={checkpoint.Revision}. Existing messages are skipped; old inbox facts are retained.");
            }
            else {
                output.WriteLine($"IMAP rebaseline preview: expectedValidity={checkpoint.UidValidity}, expectedCursor={checkpoint.ScannedThroughUid}, expectedRevision={checkpoint.Revision}, newValidity={newValidity}, newCursor={newCursor}, blockedCode={checkpoint.BlockedCode ?? "NONE"}.");
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
        uint? NewValidity, uint? NewCursor);

    private static Options Parse(string[] args) {
        if (!IsInvocation(args)) { throw Usage(); }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool apply = false;
        for (int index = 2; index < args.Length; index++) {
            if (args[index] == "--apply" && !apply) { apply = true; continue; }
            if (args[index] is not ("--config" or "--character" or "--expected-validity"
                or "--expected-cursor" or "--expected-revision" or "--new-validity" or "--new-cursor")
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
        uint? newCursor = Number("--new-cursor", false);
        long? revision = null;
        if (values.TryGetValue("--expected-revision", out var revisionText)) {
            if (!long.TryParse(revisionText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long number) || number < 0) { throw Usage(); }
            revision = number;
        }
        bool complete = validity.HasValue && cursor.HasValue && revision.HasValue
            && newValidity.HasValue && newCursor.HasValue;
        if (apply ? !complete : values.Count != 2) { throw Usage(); }
        return new(path, character, apply, validity, cursor, revision, newValidity, newCursor);
    }

    private static InvalidDataException Usage() => new(
        "Usage: operator rebaseline-imap --config <absolute-path> --character <id> "
        + "[--apply --expected-validity <uint> --expected-cursor <uint> --expected-revision <long> "
        + "--new-validity <uint> --new-cursor <uint>]. Default is read-only preview.");
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace Atelia.Galatea.Server.Mailbox;

// Class printing never expands the configured sender list or shared credentials.
internal sealed class GalateaImapAccount {
    public GalateaImapAccount(string host, int port, string tlsMode,
        IReadOnlyList<string>? autoDisplaySenders = null) {
        Host = host;
        Port = port;
        TlsMode = tlsMode;
        AutoDisplaySenders = Array.AsReadOnly((autoDisplaySenders ?? []).ToArray());
    }

    public string Host { get; }
    public int Port { get; }
    public string TlsMode { get; }
    public IReadOnlyList<string> AutoDisplaySenders { get; }
}

internal sealed record GalateaImapPolicy(bool Enabled = false,
    int PollIntervalSeconds = 60, int TimeoutSeconds = 60);

internal sealed class GalateaImapConfig {
    internal const string MailboxName = "INBOX";
    internal const int MaximumAutoDisplaySenders = 128;

    internal GalateaImapConfig(GalateaImapPolicy? policy,
        IReadOnlyDictionary<string, GalateaEmailAccount> accounts) {
        policy ??= new GalateaImapPolicy();
        if (policy.PollIntervalSeconds is < 1 or > 3600) {
            throw new InvalidDataException("IMAP_INVALID_POLICY: pollIntervalSeconds.");
        }
        if (policy.TimeoutSeconds is < 1 or > 300) {
            throw new InvalidDataException("IMAP_INVALID_POLICY: timeoutSeconds.");
        }
        ArgumentNullException.ThrowIfNull(accounts);
        var snapshot = new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal);
        foreach (var pair in accounts) {
            GalateaSmtpConfig.RequireValidAccount(pair.Value);
            RequireValidAccount(pair.Value.Imap);
            snapshot.Add(pair.Key, pair.Value);
        }
        Enabled = policy.Enabled;
        PollIntervalSeconds = policy.PollIntervalSeconds;
        TimeoutSeconds = policy.TimeoutSeconds;
        Accounts = new ReadOnlyDictionary<string, GalateaEmailAccount>(snapshot);
    }

    internal bool Enabled { get; }
    internal int PollIntervalSeconds { get; }
    internal int TimeoutSeconds { get; }
    internal IReadOnlyDictionary<string, GalateaEmailAccount> Accounts { get; }
    internal static GalateaImapConfig Disabled { get; } = new(null,
        new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal));

    internal static GalateaImapConfig Resolve(GalateaImapPolicy? policy,
        IEnumerable<GalateaCharacterFileConfig> characters) {
        ArgumentNullException.ThrowIfNull(characters);
        var accounts = new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal);
        foreach (var character in characters) {
            if (character is null) { throw new InvalidDataException("IMAP_INVALID_CHARACTER."); }
            if (character.Email?.Imap is not null
                && !accounts.TryAdd(character.CharacterId, character.Email)) {
                throw new InvalidDataException("IMAP_DUPLICATE_CHARACTER.");
            }
        }
        return new GalateaImapConfig(policy, accounts);
    }

    // Only the receiving namespace belongs in this descriptor. Sender-list
    // edits and credential rotation must preserve the previously committed UID cursor.
    internal static string AccountReference(string characterId, GalateaEmailAccount account) {
        GalateaImapAccount imap = account.Imap
            ?? throw new InvalidDataException("IMAP_ACCOUNT_NOT_CONFIGURED.");
        byte[] descriptor = JsonSerializer.SerializeToUtf8Bytes(new object[] {
            account.Address, imap.Host, imap.Port, imap.TlsMode, MailboxName
        });
        return "imap:" + characterId + ":" + Convert.ToHexStringLower(SHA256.HashData(descriptor));
    }

    internal string? ReferenceFor(string characterId) =>
        Accounts.TryGetValue(characterId, out var account)
            ? AccountReference(characterId, account) : null;

    internal static bool IsReferenceFor(string? reference, string characterId) {
        string prefix = "imap:" + characterId + ":";
        return reference is not null
            && reference.StartsWith(prefix, StringComparison.Ordinal)
            && reference.Length == prefix.Length + 64
            && reference.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    internal static void RequireValidAccount(GalateaImapAccount? account) {
        if (account is null) { throw InvalidAccount("imap"); }
        if (!GalateaSmtpConfig.IsMailServerHost(account.Host)) { throw InvalidAccount("host"); }
        if (account.Port is < 1 or > 65535) { throw InvalidAccount("port"); }
        if (account.TlsMode is not ("implicit" or "starttls")) { throw InvalidAccount("tlsMode"); }
        if (account.AutoDisplaySenders.Count > MaximumAutoDisplaySenders) {
            throw InvalidAccount("autoDisplaySenders");
        }
        for (int index = 0; index < account.AutoDisplaySenders.Count; index++) {
            string sender = account.AutoDisplaySenders[index];
            if (!GalateaExternalMailAddress.TryParse(sender, out var parsed)
                || parsed!.Value != sender) { throw InvalidAccount("autoDisplaySenders"); }
            for (int earlier = 0; earlier < index; earlier++) {
                if (GalateaExternalMailAddress.SameMailbox(sender, account.AutoDisplaySenders[earlier])) {
                    throw InvalidAccount("autoDisplaySenders");
                }
            }
        }
    }

    private static InvalidDataException InvalidAccount(string field) =>
        new("IMAP_INVALID_ACCOUNT: " + field + ".");
}

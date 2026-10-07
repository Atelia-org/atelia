using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Atelia.Galatea.Server.Mailbox;

// A class deliberately avoids synthesized record printing of authorizationCode.
// The validated file object is also the immutable, host-owned sending snapshot.
internal sealed class GalateaEmailAccount {
    public GalateaEmailAccount(string address, string authorizationCode,
        string smtpHost, int smtpPort, string tlsMode) {
        Address = address;
        AuthorizationCode = authorizationCode;
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        TlsMode = tlsMode;
    }
    public string Address { get; }
    public string AuthorizationCode { get; }
    public string SmtpHost { get; }
    public int SmtpPort { get; }
    public string TlsMode { get; }
}

internal sealed record GalateaSmtpPolicy(bool Enabled = false, int TimeoutSeconds = 60);

internal sealed class GalateaSmtpConfig {
    internal GalateaSmtpConfig(
        GalateaSmtpPolicy? policy,
        IReadOnlyDictionary<string, GalateaEmailAccount> accounts
    ) {
        policy ??= new GalateaSmtpPolicy();
        if (policy.TimeoutSeconds is < 1 or > 300) {
            throw new InvalidDataException("SMTP_INVALID_POLICY: timeoutSeconds.");
        }
        ArgumentNullException.ThrowIfNull(accounts);
        var snapshot = new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal);
        foreach (var pair in accounts) {
            RequireValidAccount(pair.Value);
            snapshot.Add(pair.Key, pair.Value);
        }
        Enabled = policy.Enabled;
        TimeoutSeconds = policy.TimeoutSeconds;
        Accounts = new ReadOnlyDictionary<string, GalateaEmailAccount>(snapshot);
    }

    internal bool Enabled { get; }
    internal int TimeoutSeconds { get; }
    internal IReadOnlyDictionary<string, GalateaEmailAccount> Accounts { get; }
    internal static GalateaSmtpConfig Disabled { get; } = new(null,
        new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal));

    internal static GalateaSmtpConfig Resolve(
        GalateaSmtpPolicy? policy,
        IEnumerable<GalateaCharacterFileConfig> characters
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        var accounts = new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal);
        foreach (var character in characters) {
            if (character is null) {
                throw new InvalidDataException("SMTP_INVALID_CHARACTER.");
            }
            if (character.Email is not null
                && !accounts.TryAdd(character.CharacterId, character.Email)) {
                throw new InvalidDataException("SMTP_DUPLICATE_CHARACTER.");
            }
        }
        return new GalateaSmtpConfig(policy, accounts);
    }

    // Descriptor changes reject old queued work. Authorization-code rotation
    // preserves its identity; secret material never enters this reference.
    internal static string AccountReference(string characterId, GalateaEmailAccount account) {
        byte[] descriptor = JsonSerializer.SerializeToUtf8Bytes(new object[] {
            account.Address, account.SmtpHost, account.SmtpPort, account.TlsMode
        });
        string bindingId = Convert.ToHexStringLower(SHA256.HashData(descriptor));
        return "smtp:" + characterId + ":" + bindingId;
    }

    internal string ReferenceFor(string characterId) {
        if (!Enabled) { return BlockedReference(characterId, "SMTP_DISABLED"); }
        return Accounts.TryGetValue(characterId, out var account)
            ? AccountReference(characterId, account)
            : BlockedReference(characterId, "NO_SENDER_BINDING");
    }

    internal static void RequireValidAccount(GalateaEmailAccount? account) {
        if (account is null) { throw InvalidEmail("email"); }
        if (!GalateaExternalMailAddress.TryParse(account.Address, out var parsed)
            || parsed!.Value != account.Address) { throw InvalidEmail("address"); }
        if (string.IsNullOrWhiteSpace(account.AuthorizationCode)
            || account.AuthorizationCode.Any(char.IsControl)) {
            throw InvalidEmail("authorizationCode");
        }
        if (!IsSmtpHost(account.SmtpHost)) { throw InvalidEmail("smtpHost"); }
        if (account.SmtpPort is < 1 or > 65535) { throw InvalidEmail("smtpPort"); }
        if (account.TlsMode is not ("implicit" or "starttls")) { throw InvalidEmail("tlsMode"); }
    }

    private static bool IsSmtpHost(string? host) {
        if (host is not { Length: > 0 and <= 253 }
            || host.Any(c => c > 127 || char.IsControl(c) || char.IsWhiteSpace(c))) {
            return false;
        }
        if (IPAddress.TryParse(host, out _)) { return true; }
        string dns = host.EndsWith('.') ? host[..^1] : host;
        return dns.Length > 0 && dns.Split('.').All(label =>
            label is { Length: > 0 and <= 63 }
            && IsAsciiLetterOrDigit(label[0])
            && IsAsciiLetterOrDigit(label[^1])
            && label.All(c => IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static bool IsAsciiLetterOrDigit(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static InvalidDataException InvalidEmail(string field) =>
        new("SMTP_INVALID_EMAIL: " + field + ".");

    internal static string BlockedReference(string characterId, string code) =>
        "blocked:" + characterId + ":" + code;

    // These codes and the offline grammar remain durable historical facts.
    internal static string? BlockedReason(string reference, string characterId) {
        foreach (string code in new[] { "SMTP_DISABLED", "NO_SENDER_BINDING", "SENDER_BINDING_DISABLED" }) {
            if (reference == BlockedReference(characterId, code)) { return code; }
        }
        return null;
    }

    internal static bool IsBindingId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static bool IsReferenceFor(string reference, string characterId) =>
        reference == "offline:" + characterId || BlockedReason(reference, characterId) is not null
        || (reference.StartsWith("smtp:" + characterId + ":", StringComparison.Ordinal)
            && IsBindingId(reference[("smtp:" + characterId + ":").Length..]));
}

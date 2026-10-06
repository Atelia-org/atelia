using System.Text.Json.Serialization;

namespace Atelia.Galatea.Server.Mailbox;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GalateaSmtpConfig(bool Enabled = false,
    IReadOnlyList<GalateaSmtpAccountBinding>? SenderAccounts = null, int TimeoutSeconds = 60,
    bool OfflineMode = false) {
    internal static GalateaSmtpConfig Disabled => new(false, []);
    // Only the host-confirmed character ID selects a binding. Never use names or body text.
    internal string ReferenceFor(string characterId) {
        if (OfflineMode && !Enabled) { return "offline:" + characterId; }
        if (!Enabled) { return BlockedReference(characterId, "SMTP_DISABLED"); }
        var binding = SenderAccounts?.SingleOrDefault(a => a.CharacterId == characterId);
        return binding is null ? BlockedReference(characterId, "NO_SENDER_BINDING")
            : !binding.Enabled ? BlockedReference(characterId, "SENDER_BINDING_DISABLED") : binding.Reference;
    }

    internal static string BlockedReference(string characterId, string code) => "blocked:" + characterId + ":" + code;
    internal static string? BlockedReason(string reference, string characterId) {
        foreach (string code in new[] { "SMTP_DISABLED", "NO_SENDER_BINDING", "SENDER_BINDING_DISABLED" }) {
            if (reference == BlockedReference(characterId, code)) { return code; }
        }
        return null;
    }

    internal static GalateaSmtpConfig Resolve(GalateaSmtpConfig? config, IEnumerable<string> characterIds) {
        config ??= Disabled;
        if (config.TimeoutSeconds is < 1 or > 300 || config.SenderAccounts is null
            || (config.Enabled && config.OfflineMode)) {
            throw new InvalidDataException("Invalid SMTP policy.");
        }
        var ids = characterIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in config.SenderAccounts) {
            if (binding is null || !ids.Contains(binding.CharacterId) || !seen.Add(binding.CharacterId)
                || !IsBindingId(binding.BindingId)
                || !GalateaExternalMailAddress.TryParse(binding.FromAddress, out var address)
                || address!.Value != binding.FromAddress
                || string.IsNullOrWhiteSpace(binding.CredentialPath)
                || !Path.IsPathFullyQualified(binding.CredentialPath)
                || binding.DisplayName is { Length: > 256 }
                || binding.DisplayName?.Any(char.IsControl) == true) {
                throw new InvalidDataException("Invalid SMTP host binding.");
            }
        }
        return config with { SenderAccounts = Array.AsReadOnly(config.SenderAccounts.ToArray()) };
    }
    internal static bool IsBindingId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
    internal static bool IsReferenceFor(string reference, string characterId) =>
        reference == "offline:" + characterId || BlockedReason(reference, characterId) is not null
        || (reference.StartsWith("smtp:" + characterId + ":", StringComparison.Ordinal)
            && IsBindingId(reference[("smtp:" + characterId + ":").Length..]));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GalateaSmtpAccountBinding(string CharacterId, string BindingId,
    string FromAddress, string CredentialPath, bool Enabled = false, string? DisplayName = null) {
    internal string Reference => "smtp:" + CharacterId + ":" + BindingId;
}

/// <summary>Configuration never promotes an offline capture to real sending.</summary>
internal sealed class GalateaConfiguredSmtpSender : IGalateaSmtpSender {
    private readonly GalateaSmtpConfig _config;
    private readonly IGalateaSmtpSender _offline;
    private readonly IGalateaSmtpSender _network;
    internal GalateaConfiguredSmtpSender(GalateaSmtpConfig? config,
        IGalateaSmtpSender? offline = null, IGalateaSmtpSender? network = null) {
        _config = config ?? GalateaSmtpConfig.Disabled;
        _offline = offline ?? new GalateaOfflineSmtpSender();
        _network = network ?? new GalateaNetworkSmtpSender(_config);
    }
    public Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken ct) {
        if (request.SenderAccountReference.StartsWith("offline:", StringComparison.Ordinal)) {
            return request.SenderAccountReference == "offline:" + request.FromCharacterId && _config.OfflineMode && !_config.Enabled
                ? _offline.SendAsync(request, ct)
                : Task.FromResult(new GalateaSmtpSendResult(GalateaSmtpMailState.DefiniteFailure, "SMTP_OFFLINE_ISOLATED"));
        }
        string? blocked = GalateaSmtpConfig.BlockedReason(request.SenderAccountReference, request.FromCharacterId);
        if (blocked is not null) {
            return Task.FromResult(new GalateaSmtpSendResult(GalateaSmtpMailState.DefiniteFailure, blocked));
        }
        if (!_config.Enabled || !(_config.SenderAccounts?.Any(a => a.Enabled
            && a.CharacterId == request.FromCharacterId && a.Reference == request.SenderAccountReference) ?? false)) {
            return Task.FromResult(new GalateaSmtpSendResult(GalateaSmtpMailState.DefiniteFailure, "SMTP_BINDING_UNAVAILABLE"));
        }
        return _network.SendAsync(request, ct);
    }
}

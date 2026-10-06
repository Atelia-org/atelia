using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Atelia.Galatea.Server.Mailbox;

/// <summary>
/// One SMTP transaction per captured mail, no pooling or retry. Only host-bound
/// accounts enter this boundary. Protocol replies and credential exceptions never escape.
/// </summary>
internal sealed class GalateaNetworkSmtpSender(GalateaSmtpConfig config,
    X509Certificate2? trustAnchorForLoopbackTests = null) : IGalateaSmtpSender {
    public async Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken cancellationToken) {
        // Hard rule also enforced here, independent of the configured router.
        if (request.SenderAccountReference.StartsWith("offline:", StringComparison.Ordinal)) {
            return Failure("SMTP_OFFLINE_ISOLATED");
        }
        var binding = config.Enabled ? config.SenderAccounts?.SingleOrDefault(a => a.Enabled
            && a.CharacterId == request.FromCharacterId && a.Reference == request.SenderAccountReference) : null;
        if (binding is null) { return Failure("SMTP_BINDING_UNAVAILABLE"); }
        if (!GalateaExternalMailAddress.TryParse(request.Recipient, out var recipient)
            || recipient!.Value != request.Recipient) { return Failure("SMTP_INVALID_RECIPIENT"); }

        bool dataMayHaveBeenSent = false;
        bool providerAccepted = false;
        string stage = "CREDENTIALS";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        CancellationToken ct = deadline.Token;
        try {
            ct.ThrowIfCancellationRequested();
            Credentials credentials = ReadCredentials(binding);
            // Plain transport is permitted solely for a literal loopback test endpoint.
            if (credentials.Security == "none" && !(IPAddress.TryParse(credentials.Host, out var ip) && IPAddress.IsLoopback(ip))) {
                return Failure("SMTP_TLS_REQUIRED");
            }
            string message = CreateMessage(binding, request);
            using var client = new TcpClient();
            stage = "CONNECT";
            await client.ConnectAsync(credentials.Host, credentials.Port, ct).ConfigureAwait(false);
            using Stream network = client.GetStream();
            Stream transport = network;
            SslStream? tls = null;
            try {
                if (credentials.Security == "tls") {
                    stage = "TLS";
                    tls = await SecureAsync(transport, credentials.Host, ct).ConfigureAwait(false);
                    transport = tls;
                }
                var wire = new Wire(transport);
                stage = "GREETING";
                Expect(await wire.ReadReplyAsync(ct).ConfigureAwait(false), 220);
                stage = "EHLO";
                var hello = await wire.CommandAsync("EHLO galatea.invalid", ct).ConfigureAwait(false);
                Expect(hello, 250);
                if (credentials.Security == "starttls") {
                    stage = "TLS";
                    if (!hello.Lines.Any(line => line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))) {
                        return Failure("SMTP_TLS_UNAVAILABLE");
                    }
                    Expect(await wire.CommandAsync("STARTTLS", ct).ConfigureAwait(false), 220);
                    tls = await SecureAsync(transport, credentials.Host, ct).ConfigureAwait(false);
                    transport = tls;
                    wire = new Wire(transport);
                    stage = "EHLO";
                    hello = await wire.CommandAsync("EHLO galatea.invalid", ct).ConfigureAwait(false);
                    Expect(hello, 250);
                }
                stage = "AUTH";
                if (!hello.Lines.Any(line => line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase)
                    && line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("LOGIN", StringComparer.OrdinalIgnoreCase))) {
                    return Failure("SMTP_AUTH_UNSUPPORTED");
                }
                Expect(await wire.CommandAsync("AUTH LOGIN", ct).ConfigureAwait(false), 334);
                Expect(await wire.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.Username)), ct).ConfigureAwait(false), 334);
                Expect(await wire.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.AuthorizationCode)), ct).ConfigureAwait(false), 235);
                stage = "MAIL";
                Expect(await wire.CommandAsync("MAIL FROM:<" + binding.FromAddress + ">", ct).ConfigureAwait(false), 250);
                stage = "RCPT";
                var rcpt = await wire.CommandAsync("RCPT TO:<" + request.Recipient + ">", ct).ConfigureAwait(false);
                if (rcpt.Code is not (250 or 251)) { throw new ReplyException(rcpt.Code); }
                stage = "DATA";
                Expect(await wire.CommandAsync("DATA", ct).ConfigureAwait(false), 354);
                // Set before the first body write: a partial write also crosses the unknown boundary.
                ct.ThrowIfCancellationRequested();
                dataMayHaveBeenSent = true;
                await wire.WriteAsync(message + ".\r\n", ct).ConfigureAwait(false);
                var final = await wire.ReadReplyAsync(ct).ConfigureAwait(false);
                Expect(final, 250);
                providerAccepted = true;
                // No QUIT result can override the already observed DATA acceptance.
                return new(GalateaSmtpMailState.ProviderAccepted, "SMTP_DATA_ACCEPTED");
            }
            finally { tls?.Dispose(); }
        }
        catch (ReplyException exception) {
            // An explicit 4xx/5xx transaction rejection is known, including after DATA.
            return exception.Code is >= 400 and <= 599
                ? Failure("SMTP_" + stage + "_REJECTED")
                : UncertainOrFailure(dataMayHaveBeenSent, "SMTP_" + stage + "_PROTOCOL");
        }
        catch (OperationCanceledException) {
            string reason = cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT";
            return UncertainOrFailure(dataMayHaveBeenSent, "SMTP_" + reason + (dataMayHaveBeenSent ? "_AFTER_DATA" : "_BEFORE_DATA"));
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            // Never expose messages, stack, paths, server replies, or nested exceptions.
            return providerAccepted ? new(GalateaSmtpMailState.ProviderAccepted, "SMTP_DATA_ACCEPTED")
                : UncertainOrFailure(dataMayHaveBeenSent, "SMTP_" + stage + "_FAILED");
        }
    }

    private static GalateaSmtpSendResult Failure(string code) => new(GalateaSmtpMailState.DefiniteFailure, code);
    private static GalateaSmtpSendResult UncertainOrFailure(bool data, string code) =>
        new(data ? GalateaSmtpMailState.OutcomeUnknown : GalateaSmtpMailState.DefiniteFailure, code);

    private sealed record Credentials(string Host, int Port, string Security, string Username, string AuthorizationCode);
    private static Credentials ReadCredentials(GalateaSmtpAccountBinding binding) {
        // File format is deliberately centralized here for the owner to confirm/map later.
        byte[] bytes = GalateaStrictConfigReader.ReadBoundedRegularFile(binding.CredentialPath, 64 * 1024, "SMTP credentials");
        try {
            using JsonDocument document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
            string[] fields = ["host", "port", "security", "username", "authorizationCode", "fromAddress"];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) {
                if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) { throw new InvalidDataException(); }
            }
            if (seen.Count != fields.Length) { throw new InvalidDataException(); }
            string host = root.GetProperty("host").GetString()!;
            int port = root.GetProperty("port").GetInt32();
            string security = root.GetProperty("security").GetString()!;
            string user = root.GetProperty("username").GetString()!;
            string secret = root.GetProperty("authorizationCode").GetString()!;
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl)
                || port is < 1 or > 65535 || security is not ("tls" or "starttls" or "none")
                || string.IsNullOrWhiteSpace(user) || user.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl)
                || root.GetProperty("fromAddress").GetString() != binding.FromAddress) {
                throw new InvalidDataException();
            }
            return new(host, port, security, user, secret);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<SslStream> SecureAsync(Stream stream, string host, CancellationToken ct) {
        var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        try {
            var options = new SslClientAuthenticationOptions {
                TargetHost = host,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            };
            if (trustAnchorForLoopbackTests is not null) {
                // Not configurable or wired in DI; in-memory trust only for literal loopback tests.
                if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip)) { throw new InvalidOperationException(); }
                options.CertificateChainPolicy = new X509ChainPolicy {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true
                };
                options.CertificateChainPolicy.CustomTrustStore.Add(trustAnchorForLoopbackTests);
            }
            await tls.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
            return tls;
        }
        catch { tls.Dispose(); throw; }
    }

    private static string CreateMessage(GalateaSmtpAccountBinding binding, GalateaSmtpSendRequest request) {
        string from = binding.DisplayName is { Length: > 0 } ? EncodeHeader(binding.DisplayName) + " <" + binding.FromAddress + ">" : binding.FromAddress;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.DispatchId))).ToLowerInvariant();
        return "From: " + from + "\r\nTo: " + request.Recipient + "\r\nSubject: " + EncodeHeader(request.Subject ?? "")
            + "\r\nDate: " + DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)
            + "\r\nMessage-ID: <" + id + "@galatea.invalid>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8"
            + "\r\nContent-Transfer-Encoding: base64\r\n\r\n"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body), Base64FormattingOptions.InsertLineBreaks) + "\r\n";
    }

    // Each encoded word is bounded and ends at a Unicode scalar boundary.
    private static string EncodeHeader(string text) {
        var words = new List<string>();
        var chunk = new StringBuilder();
        int bytes = 0;
        foreach (var rune in text.EnumerateRunes()) {
            if (bytes + rune.Utf8SequenceLength > 42) { Add(); }
            chunk.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        if (bytes > 0) { Add(); }
        return string.Join("\r\n ", words);
        void Add() {
            words.Add("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) + "?=");
            chunk.Clear(); bytes = 0;
        }
    }

    private sealed class ReplyException(int code) : Exception { internal int Code { get; } = code; }
    private sealed record Reply(int Code, IReadOnlyList<string> Lines);
    private static void Expect(Reply reply, int code) { if (reply.Code != code) { throw new ReplyException(reply.Code); } }

    // No buffering across STARTTLS; bounded lines and reply count; no transcript sink.
    private sealed class Wire(Stream stream) {
        private readonly byte[] _one = new byte[1];
        internal async Task WriteAsync(string text, CancellationToken ct) {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        internal async Task<Reply> CommandAsync(string command, CancellationToken ct) {
            await WriteAsync(command + "\r\n", ct).ConfigureAwait(false);
            return await ReadReplyAsync(ct).ConfigureAwait(false);
        }
        internal async Task<Reply> ReadReplyAsync(CancellationToken ct) {
            var lines = new List<string>(); int? code = null;
            for (int count = 0; count < 64; count++) {
                var line = new StringBuilder();
                while (line.Length <= 512) {
                    if (await stream.ReadAsync(_one, ct).ConfigureAwait(false) == 0) { throw new IOException(); }
                    if (_one[0] == '\n') { break; }
                    line.Append((char)_one[0]);
                }
                string value = line.ToString();
                if (value.Length > 512 || !value.EndsWith('\r') || value.Length < 5
                    || !int.TryParse(value.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int current)
                    || current is < 200 or > 599 || (code is not null && current != code)
                    || value[3] is not (' ' or '-')) { throw new InvalidDataException(); }
                code = current; lines.Add(value[4..^1]);
                if (value[3] == ' ') { return new(current, lines); }
            }
            throw new InvalidDataException();
        }
    }
}

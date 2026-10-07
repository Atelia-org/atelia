using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaNetworkImapTests {
    [Theory]
    [InlineData("implicit")]
    [InlineData("starttls")]
    public async Task EncryptedLoopback_UsesExamineUidAndBoundedBodyPeek(string tlsMode) {
        await using var server = new FakeImapServer(tlsMode);
        GalateaEmailAccount account = Account(server, tlsMode);
        var transport = new GalateaNetworkImapTransport(Config(account), server.Certificate);
        await using var connection = await transport.OpenAsync(account, default);
        Assert.Equal(41u, connection.UidValidity);
        Assert.Equal(2u, connection.UidNext);
        Assert.Equal(new uint[] { 1 }, await connection.SearchUidsAsync(1, 256, default));
        var raw = await connection.ReadRawAsync(1, default);
        Assert.Null(raw.RejectedCode);
        Assert.Equal(server.Raw, raw.Bytes);
        Assert.True(server.Authenticated);
        Assert.Contains(server.Commands, command => command.Contains(" EXAMINE ", StringComparison.Ordinal));
        Assert.Contains(server.Commands, command => command.Contains(" UID SEARCH UID 1:256", StringComparison.Ordinal));
        Assert.Contains(server.Commands, command => command.Contains(" UID FETCH 1 ", StringComparison.Ordinal)
            && command.Contains("BODY.PEEK[]<0.2097153>", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, command => command.Contains(" SELECT ", StringComparison.Ordinal)
            || command.Contains(" STORE ", StringComparison.Ordinal) || command.Contains(" EXPUNGE", StringComparison.Ordinal));
        Assert.Contains(server.Commands, command => command.Contains(" ID ", StringComparison.Ordinal)
            && command.Contains("\"Galatea\"", StringComparison.Ordinal));
        int statusIndex = server.Commands.FindIndex(command => command.Contains(" STATUS ", StringComparison.Ordinal));
        int examineIndex = server.Commands.FindIndex(command => command.Contains(" EXAMINE ", StringComparison.Ordinal));
        Assert.True(statusIndex >= 0 && examineIndex > statusIndex);
    }

    [Theory]
    [InlineData("implicit")]
    [InlineData("starttls")]
    public async Task ExamineOmitsUidNext_UsesExactPreOpenStatusValue(string tlsMode) {
        await using var server = new FakeImapServer(tlsMode, "examine-missing-uidnext");
        var account = Account(server, tlsMode);
        await using var connection = await new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default);
        Assert.Equal(41u, connection.UidValidity);
        // EXISTS is one and the only actual message has UID one. The exact
        // STATUS value must survive; neither count nor maximum UID proves it.
        Assert.Equal(101u, connection.UidNext);
        Assert.Contains(server.Commands, command => command.Contains(" STATUS ", StringComparison.Ordinal)
            && command.Contains("UIDVALIDITY", StringComparison.Ordinal) && command.Contains("UIDNEXT", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, command => command.Contains(" UID SEARCH ", StringComparison.Ordinal)
            || command.Contains(" UID FETCH ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("status-missing-uidnext", "IMAP_INVALID_UID_METADATA")]
    [InlineData("status-missing-validity", "IMAP_INVALID_UID_METADATA")]
    [InlineData("status-rejected", "IMAP_STATUS_FAILED")]
    [InlineData("examine-missing-validity", "IMAP_INVALID_UID_METADATA")]
    [InlineData("examine-validity-changed", "IMAP_UIDVALIDITY_CHANGED")]
    [InlineData("examine-next-regressed", "IMAP_UIDNEXT_REGRESSED")]
    public async Task StatusMetadataMustBeCompleteAndConsistentWithExamine(string mode, string code) {
        await using var server = new FakeImapServer("implicit", mode);
        var account = Account(server, "implicit");
        var exception = await Assert.ThrowsAsync<GalateaImapReadException>(() =>
            new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default));
        Assert.Equal(code, exception.Code);
        Assert.DoesNotContain("SECRET_MARKER", exception.ToString());
        Assert.DoesNotContain("/synthetic/private/path", exception.ToString());
        Assert.DoesNotContain(server.Commands, command => command.Contains(" UID SEARCH ", StringComparison.Ordinal)
            || command.Contains(" UID FETCH ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewUidNextFromExamine_MayAdvanceBeyondEarlierStatusSnapshot() {
        await using var server = new FakeImapServer("implicit", "examine-next-increased");
        var account = Account(server, "implicit");
        await using var connection = await new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default);
        Assert.Equal(3u, connection.UidNext);
    }

    [Theory]
    [InlineData("implicit")]
    [InlineData("starttls")]
    public async Task UntrustedTls_NeverAuthenticates(string tlsMode) {
        await using var server = new FakeImapServer(tlsMode);
        GalateaEmailAccount account = Account(server, tlsMode);
        var exception = await Assert.ThrowsAsync<GalateaImapReadException>(() =>
            new GalateaNetworkImapTransport(Config(account)).OpenAsync(account, default));
        Assert.Equal("IMAP_CONNECT_FAILED", exception.Code);
        Assert.False(server.Authenticated);
        Assert.DoesNotContain("SECRET_MARKER", exception.ToString());
    }

    [Fact]
    public async Task AuthenticationResponseSecrets_AreNotInErrorChain() {
        await using var server = new FakeImapServer("implicit", "auth-rejected");
        GalateaEmailAccount account = Account(server, "implicit");
        var exception = await Assert.ThrowsAsync<GalateaImapReadException>(() =>
            new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default));
        Assert.Equal("IMAP_AUTH_FAILED", exception.Code);
        Assert.DoesNotContain("SECRET_MARKER", exception.ToString());
        Assert.DoesNotContain("/synthetic/private/path", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task OversizedAdvertisedMessage_IsRejectedWithoutFetchingBody() {
        await using var server = new FakeImapServer("implicit", "advertised-oversize");
        var account = Account(server, "implicit");
        await using var connection = await new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default);
        Assert.Equal("IMAP_RAW_TOO_LARGE", (await connection.ReadRawAsync(1, default)).RejectedCode);
        Assert.DoesNotContain(server.Commands, command => command.Contains("BODY.PEEK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SizeSummaryDoesNotReplaceActualPartialByteLimit() {
        await using var server = new FakeImapServer("implicit", "raw-oversize");
        var account = Account(server, "implicit");
        await using var connection = await new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default);
        Assert.Equal("IMAP_RAW_TOO_LARGE", (await connection.ReadRawAsync(1, default)).RejectedCode);
        Assert.Contains(server.Commands, command => command.Contains("BODY.PEEK[]<0.2097153>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerIgnoresPartialLimit_IsRejectedBeforeLiteralAllocation() {
        await using var server = new FakeImapServer("implicit", "literal-limit-violation");
        var account = Account(server, "implicit");
        await using var connection = await new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default);
        var exception = await Assert.ThrowsAsync<GalateaImapReadException>(() => connection.ReadRawAsync(1, default));
        Assert.Equal("IMAP_PARTIAL_LIMIT_VIOLATION", exception.Code);
    }

    [Fact]
    public async Task SearchResultsOutsideRequestedWindow_AreNotAccepted() {
        await using var server = new FakeImapServer("implicit", "search-outside-range");
        var account = Account(server, "implicit");
        await using var connection = await new GalateaNetworkImapTransport(Config(account), server.Certificate).OpenAsync(account, default);
        var exception = await Assert.ThrowsAsync<GalateaImapReadException>(() => connection.SearchUidsAsync(1, 256, default));
        Assert.Equal("IMAP_INVALID_SEARCH_RESULT", exception.Code);
    }

    private static GalateaEmailAccount Account(FakeImapServer server, string tlsMode) =>
        new("host@example.test", "SECRET_MARKER", "smtp.example.test", 465, "implicit",
            new GalateaImapAccount("127.0.0.1", server.Port, tlsMode));
    private static GalateaImapConfig Config(GalateaEmailAccount account) =>
        new(new(true, 60, 5), new Dictionary<string, GalateaEmailAccount> { ["alice"] = account });

    internal sealed class FakeImapServer : IAsyncDisposable {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        internal X509Certificate2 Certificate { get; }
        internal int Port { get; }
        internal bool Authenticated { get; private set; }
        internal List<string> Commands { get; } = [];
        internal byte[] Raw { get; }

        internal FakeImapServer(string tlsMode, string mode = "normal", byte[]? raw = null) {
            Raw = raw ?? GalateaImapMimeTests.Mail("friend@example.test", "Content-Type: text/plain", "synthetic body");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-imap-loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync(tlsMode, mode);
        }

        private async Task RunAsync(string tlsMode, string mode) {
            CancellationToken ct = _stop.Token;
            try {
                using TcpClient socket = await _listener.AcceptTcpClientAsync(ct);
                using NetworkStream network = socket.GetStream();
                using var tls = new SslStream(network, leaveInnerStreamOpen: true);
                if (tlsMode == "starttls") {
                    using var preReader = new StreamReader(network, Encoding.ASCII, leaveOpen: true);
                    using var preWriter = new StreamWriter(network, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                    await preWriter.WriteLineAsync("* OK [CAPABILITY IMAP4rev1 STARTTLS LOGINDISABLED] synthetic".AsMemory(), ct);
                    while (true) {
                        string command = await preReader.ReadLineAsync(ct) ?? throw new IOException();
                        Commands.Add(command);
                        string tag = command.Split(' ', 2)[0];
                        if (command.EndsWith(" STARTTLS", StringComparison.Ordinal)) {
                            await preWriter.WriteLineAsync((tag + " OK upgrade").AsMemory(), ct);
                            break;
                        }
                        Assert.EndsWith(" CAPABILITY", command);
                        await preWriter.WriteLineAsync("* CAPABILITY IMAP4rev1 STARTTLS LOGINDISABLED".AsMemory(), ct);
                        await preWriter.WriteLineAsync((tag + " OK capabilities").AsMemory(), ct);
                    }
                }
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                async Task Reply(string response) => await writer.WriteLineAsync(response.AsMemory(), ct);
                if (tlsMode == "implicit") { await Reply("* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR ID] synthetic"); }
                while (!ct.IsCancellationRequested) {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line is null) { return; }
                    Commands.Add(line);
                    var split = line.Split(' ', 2);
                    string tag = split[0], command = split[1];
                    if (command == "CAPABILITY") { await Reply("* CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR ID"); }
                    else if (command.StartsWith("AUTHENTICATE PLAIN", StringComparison.Ordinal)) {
                        string[] auth = command.Split(' ');
                        string encoded;
                        if (auth.Length == 3) { encoded = auth[2]; }
                        else { await Reply("+ "); encoded = await reader.ReadLineAsync(ct) ?? throw new IOException(); }
                        string[] fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                        Assert.Equal("host@example.test", fields[^2]);
                        Assert.Equal("SECRET_MARKER", fields[^1]);
                        if (mode == "auth-rejected") { await Reply(tag + " NO SECRET_MARKER /synthetic/private/path"); return; }
                        Authenticated = true;
                    }
                    else if (command.StartsWith("LIST ", StringComparison.Ordinal)) { await Reply("* LIST (\\HasNoChildren) \"/\" \"INBOX\""); }
                    else if (command.StartsWith("ID ", StringComparison.Ordinal)) {
                        Assert.DoesNotContain("SECRET_MARKER", command);
                        await Reply("* ID (\"name\" \"synthetic\")");
                    }
                    else if (command.StartsWith("STATUS ", StringComparison.Ordinal)) {
                        // STATUS is legal before selection and contains no message content.
                        Assert.DoesNotContain(Commands.Take(Commands.Count - 1), previous => previous.Contains(" EXAMINE ", StringComparison.Ordinal));
                        Assert.Contains("UIDVALIDITY", command);
                        Assert.Contains("UIDNEXT", command);
                        Assert.DoesNotContain("MESSAGES", command);
                        if (mode == "status-rejected") {
                            await Reply(tag + " NO SECRET_MARKER /synthetic/private/path");
                            continue;
                        }
                        string fields = mode == "status-missing-uidnext" ? "UIDVALIDITY 41"
                            : mode == "status-missing-validity" ? "UIDNEXT 2"
                            : "UIDVALIDITY 41 UIDNEXT " + (mode == "examine-next-regressed" ? "3"
                                : mode == "examine-missing-uidnext" ? "101" : "2");
                        await Reply("* STATUS INBOX (" + fields + ")");
                    }
                    else if (command.StartsWith("EXAMINE ", StringComparison.Ordinal)) {
                        Assert.Contains("INBOX", command);
                        await Reply("* FLAGS (\\Seen)");
                        await Reply("* 1 EXISTS");
                        await Reply("* 0 RECENT");
                        if (mode != "examine-missing-validity") {
                            await Reply("* OK [UIDVALIDITY " + (mode == "examine-validity-changed" ? "42" : "41") + "] stable");
                        }
                        if (mode != "examine-missing-uidnext") {
                            await Reply("* OK [UIDNEXT " + (mode == "examine-next-increased" ? "3" : "2") + "] next");
                        }
                        await Reply(tag + " OK [READ-ONLY] examined");
                        continue;
                    }
                    else if (command.StartsWith("UID SEARCH ", StringComparison.Ordinal)) {
                        Assert.StartsWith("UID SEARCH UID ", command);
                        Assert.DoesNotContain("*", command);
                        await Reply("* SEARCH " + (mode == "search-outside-range" ? "500" : "1"));
                    }
                    else if (command.StartsWith("UID FETCH 1 ", StringComparison.Ordinal)) {
                        if (command.Contains("BODY.PEEK", StringComparison.Ordinal)) {
                            Assert.Contains("BODY.PEEK[]<0.2097153>", command);
                            if (mode == "literal-limit-violation") {
                                await Reply("* 1 FETCH (UID 1 BODY[]<0> {2147483647}");
                                // A bounded client must reject the length, not wait for/allocate 2 GiB.
                                await Task.Delay(Timeout.Infinite, ct);
                                return;
                            }
                            byte[] bytes = mode == "raw-oversize" ? new byte[GalateaImapBounds.MaximumRawBytes + 1] : Raw;
                            await Reply("* 1 FETCH (UID 1 BODY[]<0> {" + bytes.Length + "}");
                            await tls.WriteAsync(bytes, ct);
                            await Reply(")");
                        }
                        else {
                            Assert.Contains("RFC822.SIZE", command);
                            int size = mode == "advertised-oversize" ? GalateaImapBounds.MaximumRawBytes + 100 : Raw.Length;
                            await Reply("* 1 FETCH (UID 1 RFC822.SIZE " + size + ")");
                        }
                    }
                    else { Assert.Fail("Unexpected synthetic IMAP command: " + command); }
                    await Reply(tag + " OK done");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
            catch (IOException) { }
            catch (System.Security.Authentication.AuthenticationException) { }
        }

        public async ValueTask DisposeAsync() {
            _stop.Cancel();
            _listener.Stop();
            await _run;
            Certificate.Dispose();
            _stop.Dispose();
        }
    }
}

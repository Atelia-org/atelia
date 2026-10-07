using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

// Only literal loopback listeners and synthetic accounts; never production configuration.
public sealed class GalateaNetworkSmtpTests {
    [Theory]
    [InlineData("implicit")]
    [InlineData("starttls")]
    public async Task EncryptedLoopback_AuthenticatesAndAcceptsData(string security) {
        await using var fixture = new TransportFixture(security);
        var result = await new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate).SendAsync(fixture.Request, default);
        Assert.Equal("SMTP_DATA_ACCEPTED", result.Code);
        Assert.Equal(fixture.Account.Address, fixture.Server.LoginUsername);
        Assert.Equal(fixture.Account.AuthorizationCode, fixture.Server.LoginAuthorizationCode);
        Assert.NotNull(fixture.Server.Message);
    }

    [Theory]
    [InlineData("implicit")]
    [InlineData("starttls")]
    public async Task UntrustedLoopbackCertificate_IsDefiniteAndNeverAuthenticates(string security) {
        await using var fixture = new TransportFixture(security);
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal("SMTP_TLS_FAILED", result.Code);
        Assert.Null(fixture.Server.LoginUsername);
        Assert.Null(fixture.Server.MailFrom);
    }

    [Theory]
    [InlineData("accepted", (int)GalateaSmtpMailState.ProviderAccepted, "SMTP_DATA_ACCEPTED")]
    [InlineData("auth-rejected", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_AUTH_REJECTED")]
    [InlineData("rcpt-rejected", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_RCPT_REJECTED")]
    [InlineData("temporary-rcpt", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_RCPT_REJECTED")]
    [InlineData("before-data-close", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_DATA_FAILED")]
    [InlineData("after-data-close", (int)GalateaSmtpMailState.OutcomeUnknown, "SMTP_DATA_FAILED")]
    [InlineData("data-rejected", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_DATA_REJECTED")]
    [InlineData("temporary-data", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_DATA_REJECTED")]
    [InlineData("before-data-timeout", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_TIMEOUT_BEFORE_DATA")]
    [InlineData("after-data-timeout", (int)GalateaSmtpMailState.OutcomeUnknown, "SMTP_TIMEOUT_AFTER_DATA")]
    [InlineData("malformed-after-data", (int)GalateaSmtpMailState.OutcomeUnknown, "SMTP_DATA_FAILED")]
    [InlineData("starttls-missing", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_TLS_UNAVAILABLE")]
    [InlineData("auth-unsupported", (int)GalateaSmtpMailState.DefiniteFailure, "SMTP_AUTH_UNSUPPORTED")]
    public async Task LoopbackProtocol_ClassifiesAtDataBoundary(string mode, int state, string code) {
        await using var fixture = new TransportFixture(mode);
        GalateaSmtpSendResult result = await new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate).SendAsync(fixture.Request, default);
        Assert.Equal((GalateaSmtpMailState)state, result.State);
        Assert.Equal(code, result.Code);
        Assert.DoesNotContain("SECRET_MARKER", result.ToString());
        Assert.DoesNotContain("/synthetic/private/path", result.ToString());
        if (mode == "accepted") {
            Assert.Equal("MAIL FROM:<host@Example.test>", fixture.Server.MailFrom);
            Assert.Equal("RCPT TO:<RobirdLiu@Gmail.com>", fixture.Server.RcptTo);
            string mime = fixture.Server.Message!;
            Assert.StartsWith("From: host@Example.test\r\n", mime);
            Assert.Contains("Subject: =?UTF-8?B?", mime);
            Assert.DoesNotContain("character-supplied-sender", mime);
            string encodedBody = mime.Split("\r\n\r\n", 2)[1];
            Assert.Equal(fixture.Request.Body, Encoding.UTF8.GetString(Convert.FromBase64String(encodedBody)));
            Assert.DoesNotContain("SECRET_MARKER", mime);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_ClassifiesBeforeAndAfterData(bool afterData) {
        await using var fixture = new TransportFixture(afterData ? "after-data-timeout" : "before-data-timeout", timeoutSeconds: 30);
        using var stop = new CancellationTokenSource();
        Task<GalateaSmtpSendResult> send = new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate).SendAsync(fixture.Request, stop.Token);
        await fixture.Server.ReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(afterData ? GalateaSmtpMailState.OutcomeUnknown : GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal(afterData ? "SMTP_CANCELLED_AFTER_DATA" : "SMTP_CANCELLED_BEFORE_DATA", result.Code);
    }

    [Fact]
    public async Task ConnectFailure_IsDefiniteAndDoesNotSendData() {
        await using var fixture = new TransportFixture();
        fixture.Server.StopListening();
        var result = await new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate).SendAsync(fixture.Request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal("SMTP_CONNECT_FAILED", result.Code);
        Assert.Null(fixture.Server.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineReferences_NeverConnectEvenWithAConfiguredAccount(bool enabled) {
        await using var fixture = new TransportFixture();
        var config = ConfigFor(fixture.Account, enabled);
        var request = fixture.Request with { SenderAccountReference = "offline:alice" };
        var result = await new GalateaNetworkSmtpSender(config, fixture.Server.Certificate).SendAsync(request, default);
        Assert.Equal("SMTP_OFFLINE_ISOLATED", result.Code);
        Assert.Equal(0, fixture.Server.Connections);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("other-role")]
    [InlineData("case-mismatch")]
    [InlineData("legacy-binding")]
    public async Task UnavailableRoleOrReference_NeverFallsBackToAnotherAccount(string mode) {
        await using var fixture = new TransportFixture();
        var config = mode switch {
            "disabled" => ConfigFor(fixture.Account, enabled: false),
            "missing" => new GalateaSmtpConfig(new(true), new Dictionary<string, GalateaEmailAccount>()),
            _ => fixture.Config
        };
        var request = mode switch {
            "other-role" => fixture.Request with { FromCharacterId = "bob" },
            "case-mismatch" => fixture.Request with { FromCharacterId = "Alice" },
            "legacy-binding" => fixture.Request with { SenderAccountReference = "smtp:alice:old-handwritten-binding" },
            _ => fixture.Request
        };
        var result = await new GalateaNetworkSmtpSender(config, fixture.Server.Certificate).SendAsync(request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal("SMTP_BINDING_UNAVAILABLE", result.Code);
        Assert.Equal(0, fixture.Server.Connections);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("tls")]
    [InlineData("case")]
    public async Task ChangedAccountDescriptor_RejectsCapturedReferenceBeforeConnect(string field) {
        await using var fixture = new TransportFixture();
        GalateaEmailAccount original = fixture.Account;
        var changed = new GalateaEmailAccount(
            field == "address" ? "other@Example.test" : field == "case" ? "host@example.test" : original.Address,
            original.AuthorizationCode,
            field == "host" ? "not-contacted.example.invalid" : original.SmtpHost,
            field == "port" ? (original.SmtpPort == 65535 ? 65534 : original.SmtpPort + 1) : original.SmtpPort,
            field == "tls" ? "starttls" : original.TlsMode);
        var result = await new GalateaNetworkSmtpSender(ConfigFor(changed), fixture.Server.Certificate).SendAsync(fixture.Request, default);
        Assert.Equal("SMTP_BINDING_UNAVAILABLE", result.Code);
        Assert.Equal(0, fixture.Server.Connections);
    }

    [Fact]
    public async Task AuthorizationCodeRotation_KeepsCapturedIdentityAndUsesNewSecret() {
        await using var fixture = new TransportFixture(authorizationCode: "ROTATED_SECRET_MARKER");
        GalateaEmailAccount updated = fixture.Account;
        var previous = new GalateaEmailAccount(updated.Address, "OLD_SECRET_MARKER", updated.SmtpHost, updated.SmtpPort, updated.TlsMode);
        string oldReference = GalateaSmtpConfig.AccountReference("alice", previous);
        Assert.Equal(fixture.Reference, oldReference);
        var result = await new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate)
            .SendAsync(fixture.Request with { SenderAccountReference = oldReference }, default);
        Assert.Equal(GalateaSmtpMailState.ProviderAccepted, result.State);
        Assert.Equal(updated.AuthorizationCode, fixture.Server.LoginAuthorizationCode);
        Assert.DoesNotContain(updated.AuthorizationCode, result.ToString());
    }

    [Theory]
    [InlineData(" a@Example.test ")]
    [InlineData("a@Example.test\r\nRCPT TO:<other@Example.test>")]
    [InlineData("name <a@Example.test>")]
    public async Task InvalidRecipient_IsRejectedBeforeConnecting(string recipient) {
        await using var fixture = new TransportFixture();
        var result = await new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate)
            .SendAsync(fixture.Request with { Recipient = recipient }, default);
        Assert.Equal("SMTP_INVALID_RECIPIENT", result.Code);
        Assert.Equal(0, fixture.Server.Connections);
    }

    [Fact]
    public async Task CleartextAccount_CannotEnterSendingSnapshot() {
        await using var fixture = new TransportFixture();
        GalateaEmailAccount account = fixture.Account;
        var cleartext = new GalateaEmailAccount(account.Address, account.AuthorizationCode, account.SmtpHost, account.SmtpPort, "none");
        var exception = Assert.Throws<InvalidDataException>(() => ConfigFor(cleartext));
        Assert.Equal("SMTP_INVALID_EMAIL: tlsMode.", exception.Message);
        Assert.Equal(0, fixture.Server.Connections);
    }

    internal static GalateaSmtpConfig ConfigFor(GalateaEmailAccount account, bool enabled = true, int timeoutSeconds = 5) =>
        new(new(enabled, timeoutSeconds), new Dictionary<string, GalateaEmailAccount>(StringComparer.Ordinal) { ["alice"] = account });

    internal sealed class TransportFixture : IAsyncDisposable {
        internal FakeServer Server { get; }
        internal GalateaEmailAccount Account { get; }
        internal string Reference => GalateaSmtpConfig.AccountReference("alice", Account);
        internal GalateaSmtpConfig Config { get; }
        internal GalateaSmtpSendRequest Request => new("synthetic-dispatch", "alice", Reference,
            "RobirdLiu@Gmail.com", "测试：正文边界", "first\r\n\r\n```cs\ncode\n```\n---\nlast without newline");
        internal TransportFixture(string mode = "accepted", int timeoutSeconds = 5,
            string address = "host@Example.test", string authorizationCode = "SECRET_MARKER") {
            Server = new FakeServer(mode, address, authorizationCode);
            Account = new(address, authorizationCode, "127.0.0.1", Server.Port,
                mode is "starttls" or "starttls-missing" ? "starttls" : "implicit");
            Config = ConfigFor(Account, timeoutSeconds: mode.EndsWith("timeout", StringComparison.Ordinal) && timeoutSeconds == 5 ? 1 : timeoutSeconds);
        }
        public async ValueTask DisposeAsync() => await Server.DisposeAsync();
    }

    internal sealed class FakeServer : IAsyncDisposable {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private readonly bool _ownsCertificate;
        internal X509Certificate2 Certificate { get; }
        internal int Port { get; }
        internal int Connections { get; private set; }
        internal string? LoginUsername { get; private set; }
        internal string? LoginAuthorizationCode { get; private set; }
        internal string? MailFrom { get; private set; }
        internal string? RcptTo { get; private set; }
        internal string? Message { get; private set; }
        internal TaskCompletionSource ReachedGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FakeServer(string mode = "accepted", string expectedAddress = "host@Example.test",
            string expectedAuthorizationCode = "SECRET_MARKER", X509Certificate2? sharedCertificate = null) {
            _ownsCertificate = sharedCertificate is null;
            if (sharedCertificate is not null) { Certificate = sharedCertificate; }
            else {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=synthetic-loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            }
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync(mode, expectedAddress, expectedAuthorizationCode);
        }
        internal void StopListening() { _stop.Cancel(); _listener.Stop(); }
        private async Task RunAsync(string mode, string expectedAddress, string expectedAuthorizationCode) {
            CancellationToken ct = _stop.Token;
            try {
                using TcpClient client = await _listener.AcceptTcpClientAsync(ct); Connections++;
                using NetworkStream stream = client.GetStream();
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                if (mode is "starttls" or "starttls-missing") {
                    using var preReader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    using var preWriter = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                    await preWriter.WriteLineAsync("220 loopback".AsMemory(), ct);
                    Assert.StartsWith("EHLO ", await preReader.ReadLineAsync(ct));
                    await preWriter.WriteLineAsync((mode == "starttls-missing" ? "250 AUTH LOGIN" : "250-loopback\r\n250 STARTTLS").AsMemory(), ct);
                    if (mode == "starttls-missing") { return; }
                    Assert.Equal("STARTTLS", await preReader.ReadLineAsync(ct));
                    await preWriter.WriteLineAsync("220 upgrade".AsMemory(), ct);
                }
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                async Task<string> Read() => await reader.ReadLineAsync(ct) ?? throw new IOException();
                async Task Reply(string text) => await writer.WriteLineAsync(text.AsMemory(), ct);
                if (mode != "starttls") { await Reply("220 loopback fake only"); }
                Assert.StartsWith("EHLO ", await Read());
                await Reply("250-loopback"); await Reply(mode == "auth-unsupported" ? "250 SIZE 100000" : "250 AUTH LOGIN");
                if (mode == "auth-unsupported") { return; }
                Assert.Equal("AUTH LOGIN", await Read()); await Reply("334 VXNlcg==");
                LoginUsername = Encoding.UTF8.GetString(Convert.FromBase64String(await Read()));
                Assert.Equal(expectedAddress, LoginUsername); await Reply("334 UGFzcw==");
                LoginAuthorizationCode = Encoding.UTF8.GetString(Convert.FromBase64String(await Read()));
                Assert.Equal(expectedAuthorizationCode, LoginAuthorizationCode);
                if (mode == "auth-rejected") { await Reply("535 SECRET_MARKER /synthetic/private/path"); return; }
                await Reply("235 authenticated"); MailFrom = await Read(); await Reply("250 sender"); RcptTo = await Read();
                if (mode is "rcpt-rejected" or "temporary-rcpt") { await Reply(mode == "temporary-rcpt" ? "450 refused" : "550 refused"); return; }
                await Reply("250 recipient"); Assert.Equal("DATA", await Read());
                if (mode == "before-data-close") { return; }
                if (mode == "before-data-timeout") { ReachedGate.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return; }
                await Reply("354 send data");
                var lines = new List<string>(); string line;
                while ((line = await Read()) != ".") { lines.Add(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line); }
                Message = string.Join("\r\n", lines) + "\r\n"; ReachedGate.TrySetResult();
                if (mode == "after-data-close") { return; }
                if (mode == "after-data-timeout") { await Task.Delay(Timeout.Infinite, ct); return; }
                await Reply(mode switch { "data-rejected" => "554 refused", "temporary-data" => "451 refused", "malformed-after-data" => "not a reply", _ => "250 accepted" });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
            catch (IOException) { } // Client termination is expected in cancellation/failure tests.
            catch (System.Security.Authentication.AuthenticationException) { } // Untrusted TLS test ends during handshake.
        }
        public async ValueTask DisposeAsync() {
            StopListening(); await _run;
            if (_ownsCertificate) { Certificate.Dispose(); }
            _stop.Dispose();
        }
    }
}

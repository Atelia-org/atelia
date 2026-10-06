using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Galatea.Server.Mailbox;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

// Only literal loopback listeners and synthetic credentials; never production configuration.
public sealed class GalateaNetworkSmtpTests {
    [Theory]
    [InlineData("tls")]
    [InlineData("starttls")]
    public async Task EncryptedLoopback_AuthenticatesAndAcceptsData(string security) {
        await using var fixture = new TransportFixture(security);
        var result = await new GalateaNetworkSmtpSender(fixture.Config, fixture.Server.Certificate).SendAsync(fixture.Request, default);
        Assert.Equal("SMTP_DATA_ACCEPTED", result.Code);
        Assert.NotNull(fixture.Server.Message);
    }

    [Fact]
    public async Task UntrustedLoopbackCertificate_IsDefiniteAndNeverAuthenticates() {
        await using var fixture = new TransportFixture("tls");
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal("SMTP_TLS_FAILED", result.Code); Assert.Null(fixture.Server.MailFrom);
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
        GalateaSmtpSendResult result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal((GalateaSmtpMailState)state, result.State); Assert.Equal(code, result.Code);
        Assert.DoesNotContain("SECRET_MARKER", result.ToString());
        Assert.DoesNotContain(fixture.CredentialPath, result.ToString());
        if (mode == "accepted") {
            Assert.Equal("MAIL FROM:<host@Example.test>", fixture.Server.MailFrom);
            Assert.Equal("RCPT TO:<RobirdLiu@Gmail.com>", fixture.Server.RcptTo);
            string mime = fixture.Server.Message!;
            Assert.Contains("From: =?UTF-8?B?", mime);
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
        Task<GalateaSmtpSendResult> send = new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, stop.Token);
        await fixture.Server.ReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(afterData ? GalateaSmtpMailState.OutcomeUnknown : GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal(afterData ? "SMTP_CANCELLED_AFTER_DATA" : "SMTP_CANCELLED_BEFORE_DATA", result.Code);
    }

    [Fact]
    public async Task ConnectFailure_IsDefiniteAndDoesNotSendData() {
        await using var fixture = new TransportFixture();
        fixture.Server.StopListening();
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal("SMTP_CONNECT_FAILED", result.Code);
        Assert.Null(fixture.Server.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("from-mismatch")]
    [InlineData("symlink")]
    public async Task BadCredentials_AreLazySanitizedFailuresWithoutConnecting(string mode) {
        await using var fixture = new TransportFixture();
        if (mode == "missing") { File.Delete(fixture.CredentialPath); }
        if (mode == "malformed") { File.WriteAllText(fixture.CredentialPath, "SECRET_MARKER invalid json"); }
        if (mode == "duplicate") {
            string json = File.ReadAllText(fixture.CredentialPath);
            File.WriteAllText(fixture.CredentialPath, json.Replace("{", "{\"smtpHost\":\"SECRET_MARKER\",", StringComparison.Ordinal));
        }
        if (mode == "from-mismatch") { fixture.WriteCredentials(from: "other@Example.test"); }
        if (mode == "symlink") {
            string original = fixture.CredentialPath + ".original"; File.Move(fixture.CredentialPath, original);
            File.CreateSymbolicLink(fixture.CredentialPath, original);
        }
        var sender = new GalateaNetworkSmtpSender(fixture.Config); // Construction never reads the missing/invalid file.
        var result = await sender.SendAsync(fixture.Request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal(mode == "from-mismatch" ? "SENDER_ADDRESS_MISMATCH" : "SMTP_CREDENTIALS_FAILED", result.Code);
        Assert.Equal(0, fixture.Server.Connections);
        Assert.DoesNotContain("SECRET_MARKER", result.ToString());
        Assert.DoesNotContain(fixture.CredentialPath, result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialsV1_AcceptsProviderMetadataAndCaseInsensitiveLogin(bool changeCase) {
        await using var fixture = new TransportFixture();
        string login = changeCase ? "HOST@example.TEST" : "host@Example.test";
        fixture.WriteCredentials(from: login);
        var json = JsonNode.Parse(File.ReadAllText(fixture.CredentialPath))!.AsObject();
        json["provider"] = "qq"; // Metadata must not change the synthetic loopback endpoint or protocol.
        File.WriteAllText(fixture.CredentialPath, json.ToJsonString());
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal(GalateaSmtpMailState.ProviderAccepted, result.State);
        Assert.Equal("SMTP_DATA_ACCEPTED", result.Code);
        Assert.Equal(login, fixture.Server.LoginUsername);
        Assert.Equal("MAIL FROM:<host@Example.test>", fixture.Server.MailFrom);
        Assert.Contains("<host@Example.test>", fixture.Server.Message!);
        Assert.DoesNotContain("SECRET_MARKER", fixture.Server.Message!);
        if (changeCase) { Assert.DoesNotContain(login, fixture.Server.Message!); }
    }

    [Theory]
    [InlineData("v")]
    [InlineData("provider")]
    [InlineData("smtpHost")]
    [InlineData("smtpPort")]
    [InlineData("tlsMode")]
    [InlineData("username")]
    [InlineData("authorizationCode")]
    public async Task CredentialsV1_MissingRequiredFieldFailsBeforeConnect(string field) {
        await using var fixture = new TransportFixture();
        var json = JsonNode.Parse(File.ReadAllText(fixture.CredentialPath))!.AsObject();
        Assert.True(json.Remove(field)); File.WriteAllText(fixture.CredentialPath, json.ToJsonString());
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal("SMTP_CREDENTIALS_FAILED", result.Code); Assert.Equal(0, fixture.Server.Connections);
    }

    [Theory]
    [InlineData("version-zero", "SMTP_CREDENTIAL_VERSION_UNSUPPORTED")]
    [InlineData("version-two", "SMTP_CREDENTIAL_VERSION_UNSUPPORTED")]
    [InlineData("version-string", "SMTP_CREDENTIAL_VERSION_UNSUPPORTED")]
    [InlineData("version-fraction", "SMTP_CREDENTIAL_VERSION_UNSUPPORTED")]
    [InlineData("unknown-tls", "SMTP_CREDENTIAL_TLS_MODE_UNSUPPORTED")]
    [InlineData("old-tls-name", "SMTP_CREDENTIAL_TLS_MODE_UNSUPPORTED")]
    [InlineData("unknown-field", "SMTP_CREDENTIALS_FAILED")]
    [InlineData("old-format", "SMTP_CREDENTIALS_FAILED")]
    [InlineData("port-zero", "SMTP_CREDENTIALS_FAILED")]
    [InlineData("port-too-large", "SMTP_CREDENTIALS_FAILED")]
    [InlineData("port-string", "SMTP_CREDENTIALS_FAILED")]
    [InlineData("provider-type", "SMTP_CREDENTIALS_FAILED")]
    [InlineData("username-whitespace", "SENDER_ADDRESS_MISMATCH")]
    public async Task CredentialsV1_InvalidSchemaFailsWithoutConnectingOrLeaking(string mode, string code) {
        await using var fixture = new TransportFixture();
        var json = JsonNode.Parse(File.ReadAllText(fixture.CredentialPath))!.AsObject();
        switch (mode) {
            case "version-zero": json["v"] = 0; break;
            case "version-two": json["v"] = 2; break;
            case "version-string": json["v"] = "1"; break;
            case "version-fraction": json["v"] = 1.5; break;
            case "unknown-tls": json["tlsMode"] = "SECRET_MARKER"; break;
            case "old-tls-name": json["tlsMode"] = "tls"; break;
            case "unknown-field": json["fromAddress"] = "host@Example.test"; break;
            case "old-format": json = JsonSerializer.SerializeToNode(new {
                host = "127.0.0.1", port = fixture.Server.Port, security = "none", username = "host@Example.test",
                authorizationCode = "SECRET_MARKER", fromAddress = "host@Example.test"
            })!.AsObject(); break;
            case "port-zero": json["smtpPort"] = 0; break;
            case "port-too-large": json["smtpPort"] = 65536; break;
            case "port-string": json["smtpPort"] = "465"; break;
            case "provider-type": json["provider"] = 1; break;
            case "username-whitespace": json["username"] = " host@Example.test "; break;
        }
        File.WriteAllText(fixture.CredentialPath, json.ToJsonString());
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State); Assert.Equal(code, result.Code);
        Assert.Equal(0, fixture.Server.Connections);
        Assert.DoesNotContain("SECRET_MARKER", result.ToString()); Assert.DoesNotContain(fixture.CredentialPath, result.ToString());
    }

    [Fact]
    public async Task CleartextExternalHost_IsRejectedBeforeConnect() {
        await using var fixture = new TransportFixture();
        fixture.WriteCredentials(host: "not-contacted.example.invalid");
        var result = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(fixture.Request, default);
        Assert.Equal("SMTP_TLS_REQUIRED", result.Code); Assert.Equal(0, fixture.Server.Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineReferences_NeverEnterInjectedNetworkOrReadCredentials(bool enabled) {
        await using var fixture = new TransportFixture();
        File.Delete(fixture.CredentialPath);
        var network = new NeverNetworkSender();
        var request = fixture.Request with { SenderAccountReference = "offline:alice" };
        var router = new GalateaConfiguredSmtpSender(fixture.Config with { Enabled = enabled, OfflineMode = !enabled },
            new GalateaOfflineSmtpSender(GalateaOfflineSmtpBehavior.Accepted), network);
        var result = await router.SendAsync(request, default);
        Assert.Equal(enabled ? "SMTP_OFFLINE_ISOLATED" : "OFFLINE_ACCEPTED", result.Code); Assert.Equal(0, network.Calls);
        var direct = await new GalateaNetworkSmtpSender(fixture.Config).SendAsync(request, default);
        Assert.Equal("SMTP_OFFLINE_ISOLATED", direct.Code); Assert.Equal(0, fixture.Server.Connections);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("binding-disabled")]
    [InlineData("other-role")]
    [InlineData("case-mismatch")]
    public async Task UnavailableRole_NeverFallsBackToAnySender(string mode) {
        await using var fixture = new TransportFixture();
        File.Delete(fixture.CredentialPath);
        var config = mode switch {
            "missing" => fixture.Config with { SenderAccounts = [] },
            "binding-disabled" => fixture.Config with { SenderAccounts = [fixture.Binding with { Enabled = false }] },
            _ => fixture.Config
        };
        var request = mode switch {
            "other-role" => fixture.Request with { FromCharacterId = "bob" },
            "case-mismatch" => fixture.Request with { FromCharacterId = "Alice" },
            _ => fixture.Request
        };
        var offline = new NeverNetworkSender();
        var network = new NeverNetworkSender();
        var result = await new GalateaConfiguredSmtpSender(config, offline, network).SendAsync(request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal("SMTP_BINDING_UNAVAILABLE", result.Code);
        Assert.Equal(0, offline.Calls); Assert.Equal(0, network.Calls);
        Assert.Equal(0, fixture.Server.Connections);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("changed-id")]
    [InlineData("wrong-character")]
    public async Task RealReference_RequiresExactEnabledHostBinding(string mode) {
        await using var fixture = new TransportFixture();
        var config = mode switch {
            "disabled" => fixture.Config with { Enabled = false },
            "missing" => GalateaSmtpConfig.Disabled,
            "changed-id" => fixture.Config with { SenderAccounts = [fixture.Binding with { BindingId = "new-account" }] },
            _ => fixture.Config
        };
        var request = mode == "wrong-character" ? fixture.Request with { FromCharacterId = "bob" } : fixture.Request;
        var network = new NeverNetworkSender();
        var result = await new GalateaConfiguredSmtpSender(config, network: network).SendAsync(request, default);
        Assert.Equal(GalateaSmtpMailState.DefiniteFailure, result.State);
        Assert.Equal("SMTP_BINDING_UNAVAILABLE", result.Code); Assert.Equal(0, network.Calls);
        Assert.Equal(0, fixture.Server.Connections);
    }

    internal sealed class TransportFixture : IAsyncDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "galatea-loopback-smtp-" + Guid.NewGuid().ToString("N"));
        internal FakeServer Server { get; }
        internal string CredentialPath => Path.Combine(_root, "synthetic-account.json");
        internal GalateaSmtpAccountBinding Binding { get; }
        internal GalateaSmtpConfig Config { get; }
        internal GalateaSmtpSendRequest Request => new("synthetic-dispatch", "alice", Binding.Reference,
            "RobirdLiu@Gmail.com", "测试：正文边界", "first\r\n\r\n```cs\ncode\n```\n---\nlast without newline");
        internal TransportFixture(string mode = "accepted", int timeoutSeconds = 5) {
            Directory.CreateDirectory(_root); Server = new FakeServer(mode);
            Binding = new("alice", "test-v1", "host@Example.test", CredentialPath, true, "宿主发件人");
            Config = GalateaSmtpConfig.Resolve(new(true, [Binding], mode.EndsWith("timeout", StringComparison.Ordinal) && timeoutSeconds == 5 ? 1 : timeoutSeconds), ["alice"]);
            WriteCredentials(security: mode == "starttls-missing" ? "starttls" : mode is "tls" or "starttls" ? mode : "none");
        }
        internal void WriteCredentials(string host = "127.0.0.1", string security = "none", string from = "host@Example.test") =>
            File.WriteAllText(CredentialPath, JsonSerializer.Serialize(new {
                v = 1, provider = "synthetic-provider", smtpHost = host, smtpPort = Server.Port,
                tlsMode = security == "tls" ? "implicit" : security, username = from, authorizationCode = "SECRET_MARKER"
            }));
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); Directory.Delete(_root, recursive: true); }
    }

    internal sealed class FakeServer : IAsyncDisposable {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        internal X509Certificate2? Certificate { get; }
        internal int Port { get; }
        internal int Connections { get; private set; }
        internal string? LoginUsername { get; private set; }
        internal string? MailFrom { get; private set; }
        internal string? RcptTo { get; private set; }
        internal string? Message { get; private set; }
        internal TaskCompletionSource ReachedGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FakeServer(string mode) {
            if (mode is "tls" or "starttls") {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=synthetic-loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            }
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync(mode);
        }
        internal void StopListening() { _stop.Cancel(); _listener.Stop(); }
        private async Task RunAsync(string mode) {
            CancellationToken ct = _stop.Token;
            try {
                using TcpClient client = await _listener.AcceptTcpClientAsync(ct); Connections++;
                using NetworkStream stream = client.GetStream();
                using var tls = Certificate is null ? null : new SslStream(stream, leaveInnerStreamOpen: true);
                if (mode == "starttls") {
                    using var preReader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    using var preWriter = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                    await preWriter.WriteLineAsync("220 loopback".AsMemory(), ct);
                    Assert.StartsWith("EHLO ", await preReader.ReadLineAsync(ct));
                    await preWriter.WriteLineAsync("250-loopback\r\n250 STARTTLS".AsMemory(), ct);
                    Assert.Equal("STARTTLS", await preReader.ReadLineAsync(ct));
                    await preWriter.WriteLineAsync("220 upgrade".AsMemory(), ct);
                }
                if (tls is not null) { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct); }
                Stream transport = tls is null ? stream : tls;
                using var reader = new StreamReader(transport, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(transport, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                async Task<string> Read() => await reader.ReadLineAsync(ct) ?? throw new IOException();
                async Task Reply(string text) => await writer.WriteLineAsync(text.AsMemory(), ct);
                if (mode != "starttls") { await Reply("220 loopback fake only"); }
                Assert.StartsWith("EHLO ", await Read());
                await Reply("250-loopback"); await Reply(mode == "auth-unsupported" ? "250 SIZE 100000" : "250 AUTH LOGIN");
                if (mode is "starttls-missing" or "auth-unsupported") { return; }
                Assert.Equal("AUTH LOGIN", await Read()); await Reply("334 VXNlcg==");
                LoginUsername = Encoding.UTF8.GetString(Convert.FromBase64String(await Read()));
                Assert.Equal("host@Example.test", LoginUsername, ignoreCase: true); await Reply("334 UGFzcw==");
                Assert.Equal("SECRET_MARKER", Encoding.UTF8.GetString(Convert.FromBase64String(await Read())));
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
        public async ValueTask DisposeAsync() { StopListening(); await _run; Certificate?.Dispose(); _stop.Dispose(); }
    }

    private sealed class NeverNetworkSender : IGalateaSmtpSender {
        internal int Calls { get; private set; }
        public Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken ct) {
            Calls++; throw new InvalidOperationException("Network dispatch must be unreachable.");
        }
    }
}

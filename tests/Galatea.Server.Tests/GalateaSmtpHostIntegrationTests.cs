using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSmtpHostIntegrationTests {
    [Fact]
    public async Task LoadedTwoRoleAccounts_CaptureAndHostedTlsSendWithoutSecretProjectionOrReplay() {
        const string AliceSecret = "SYNTHETIC_ALICE_AUTH_SENTINEL";
        const string BobSecret = "SYNTHETIC_BOB_AUTH_SENTINEL";
        await using var aliceServer = new GalateaNetworkSmtpTests.FakeServer("accepted",
            expectedAddress: "alice@Example.test", expectedAuthorizationCode: AliceSecret);
        await using var bobServer = new GalateaNetworkSmtpTests.FakeServer("accepted",
            expectedAddress: "bob@Example.test", expectedAuthorizationCode: BobSecret,
            sharedCertificate: aliceServer.Certificate);
        var main = new CompletionConnectionConfig("test", "openai-chat", "fixture", "openai-chat/strict", "http://offline.invalid/");
        var helper = main with { Id = "helper" };
        var clientFactory = new ExtractionFactory();
        await using var files = GalateaTestHost.Create(clientFactory, normalizer: null,
            connections: [main, helper], connectionOptionIds: [main.Id], outboundMailExtractorConnectionId: helper.Id);
        string configDirectory = Path.GetDirectoryName(files.ConfigPath)!;
        JsonNode root = JsonNode.Parse(File.ReadAllText(files.ConfigPath))!;
        JsonNode alice = root["characters"]![0]!;
        alice["email"] = Email("alice@Example.test", AliceSecret, aliceServer.Port);
        var bob = alice.DeepClone();
        bob["id"] = "bob";
        bob["name"] = "Bob";
        bob["sessionDir"] = "sessions/bob";
        bob["delegationStateDir"] = "delegation-state/bob";
        bob["characterMemoryStateDir"] = "character-memory/bob";
        bob["homeDir"] = Directory.CreateDirectory(Path.Combine(configDirectory, "homes", "bob")).FullName;
        bob["sessionProvisioning"] = "create-if-missing";
        bob["email"] = Email("bob@Example.test", BobSecret, bobServer.Port);
        root["characters"]!.AsArray().Add(bob);
        root["runtime"]!["smtp"] = new JsonObject { ["enabled"] = true, ["timeoutSeconds"] = 10 };
        File.WriteAllText(files.ConfigPath, root.ToJsonString());

        var config = GalateaConfigLoader.Load(files.ConfigPath);
        foreach (string secret in new[] { AliceSecret, BobSecret }) {
            Assert.DoesNotContain(secret, config.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(config, GalateaJson.Options), StringComparison.Ordinal);
        }
        var terminal = new Dictionary<string, GalateaSmtpMailOutboxSnapshot>(StringComparer.Ordinal);
        await using (var web = CreateWeb()) {
            using var http = web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.IsType<GalateaNetworkSmtpSender>(web.Services.GetRequiredService<IGalateaSmtpSender>());
            using var login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
            var host = web.Services.GetRequiredService<GalateaHostService>();
            foreach (string id in new[] { "alice", "bob" }) {
                var session = await host.GetSessionAsync(id, CancellationToken.None);
                string body = "  " + id + " 的原文。  \n\n第二段 literal=&gt;";
                string action = "收件人：reader@Example.test\n" + body + "\n[" + (id == "alice" ? "Galatea" : "Bob") + "] 我已寄出。";
                session.Engine.AppendObservation("synthetic host fixture");
                session.Engine.AppendImportedAgentAction(new([new ActionBlock.Text(action)]),
                    new CompletionDescriptor("fixture", "fixture", "fixture"));
                await session.OutboundMailExtractionReconciler!.ReconcileAsync(session.Engine);
                var store = session.DelegationHandle!.Store;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                GalateaSmtpMailOutboxSnapshot row;
                do {
                    row = Assert.Single(store.ReadSnapshot().SmtpMailOutboxes);
                    if (row.State is not (GalateaSmtpMailState.Pending or GalateaSmtpMailState.Attempting)) { break; }
                    await Task.Delay(20, deadline.Token);
                } while (true);
                Assert.Equal(GalateaSmtpMailState.ProviderAccepted, row.State);
                Assert.Equal("SMTP_DATA_ACCEPTED", row.ResultCode);
                Assert.Equal(body, row.Body);
                Assert.Equal(config.Smtp.ReferenceFor(id), row.SenderAccountReference);
                terminal.Add(id, row);
                using var status = await http.GetAsync($"/api/v1/characters/{id}/mailbox/status");
                Assert.Equal(System.Net.HttpStatusCode.OK, status.StatusCode);
                string publicStatus = await status.Content.ReadAsStringAsync();
                foreach (string secret in new[] { AliceSecret, BobSecret }) {
                    Assert.DoesNotContain(secret, publicStatus, StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, config.Characters.Single(character => character.CharacterId == id).SystemPrompt.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(
                        Path.Combine(configDirectory, "delegation-state", id, GalateaDelegationSqliteStore.DatabaseFileName))), StringComparison.Ordinal);
                }
            }
        }
        foreach (var entry in new[] { (Server: aliceServer, Id: "alice", Secret: AliceSecret), (Server: bobServer, Id: "bob", Secret: BobSecret) }) {
            Assert.Equal(entry.Id + "@Example.test", entry.Server.LoginUsername);
            Assert.Equal(entry.Secret, entry.Server.LoginAuthorizationCode);
            Assert.Equal("MAIL FROM:<" + entry.Id + "@Example.test>", entry.Server.MailFrom);
            Assert.StartsWith("From: " + entry.Id + "@Example.test\r\n", entry.Server.Message);
            string encodedBody = entry.Server.Message!.Split("\r\n\r\n", 2)[1];
            Assert.Equal(terminal[entry.Id].Body, Encoding.UTF8.GetString(Convert.FromBase64String(encodedBody)));
        }
        int helperCalls = clientFactory.Calls;
        await using (var restarted = CreateWeb()) {
            using var http = restarted.CreateClient();
            var host = restarted.Services.GetRequiredService<GalateaHostService>();
            foreach (string id in terminal.Keys) {
                var session = await host.GetSessionAsync(id, CancellationToken.None);
                Assert.Equal(terminal[id], Assert.Single(session.DelegationHandle!.Store.ReadSnapshot().SmtpMailOutboxes));
                Assert.IsType<GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured>(
                    await session.OutboundMailExtractionReconciler!.ReconcileAsync(session.Engine));
                Assert.False(await new GalateaSmtpOutboxConsumer(restarted.Services.GetRequiredService<IGalateaSmtpSender>())
                    .ConsumeOneAsync(session.DelegationHandle.Store, CancellationToken.None));
            }
        }
        Assert.Equal(helperCalls, clientFactory.Calls);
        Assert.Equal(1, aliceServer.Connections);
        Assert.Equal(1, bobServer.Connections);
        // After shutdown inspect the actual isolated host artifacts, including
        // Journal and any SQLite WAL. Only the intended private root config contains codes.
        foreach (string artifact in Directory.EnumerateFiles(files.RootDirectory, "*", SearchOption.AllDirectories)) {
            if (artifact == files.ConfigPath) { continue; }
            string contents = Encoding.UTF8.GetString(File.ReadAllBytes(artifact));
            Assert.DoesNotContain(AliceSecret, contents, StringComparison.Ordinal);
            Assert.DoesNotContain(BobSecret, contents, StringComparison.Ordinal);
        }

        WebApplicationFactory<Program> CreateWeb() => new GalateaWebApplicationFactory(files.ConfigPath,
            clientFactory, null, null, null).WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                // The only substituted network policy is an in-memory literal-loopback trust anchor.
                services.RemoveAll<IGalateaSmtpSender>();
                services.AddSingleton<IGalateaSmtpSender>(provider => new GalateaNetworkSmtpSender(
                    provider.GetRequiredService<GalateaConfig>().Smtp, aliceServer.Certificate));
            }));
    }

    private static JsonObject Email(string address, string code, int port) => new() {
        ["address"] = address, ["authorizationCode"] = code, ["smtpHost"] = "127.0.0.1",
        ["smtpPort"] = port, ["tlsMode"] = "implicit"
    };

    private sealed class ExtractionFactory : ICompletionClientFactory {
        internal int Calls { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Assert.Equal("helper", connection.Id);
            return new Client(this);
        }
        private sealed class Client(ExtractionFactory owner) : ICompletionClient {
            private int _round;
            public string Name => "synthetic-extractor";
            public string ApiSpecId => "fixture";
            public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                owner.Calls++;
                ActionMessage message = _round++ % 2 == 0 ? new([new ActionBlock.ToolCall(new RawToolCall(
                    OutboundMailExtractor.ToolName, "synthetic-range", JsonSerializer.Serialize(new {
                        recipient = "reader@Example.test", recipientLine = 1, bodyStartLine = 2, bodyEndLine = 4,
                        evidenceStartLine = 5, evidenceEndLine = 5
                    })))]) : new([]);
                return Task.FromResult(new CompletionResult(message, CompletionDescriptor.From(this, request)));
            }
        }
    }
}

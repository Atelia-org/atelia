using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaPlayerCharacterApiTests {
    private const string PlayerId = "alice"; // Intentionally equals one Character ID.
    private const string First = "/api/v1/characters/alice";
    private const string Second = "/api/v1/characters/beta";
    private static readonly (string Method, string Suffix)[] CharacterRoutes = [
        ("GET", "/recent-turns"), ("GET", "/recap-cadence-progress"),
        ("GET", "/mailbox/status"), ("POST", "/chat/turns"),
        ("POST", "/chat/turns/resume"), ("POST", "/mailbox/ready-turn"),
        ("GET", "/agent/status"), ("POST", "/agent/retry-admission"),
        ("POST", "/mailbox/inbound"), ("POST", "/chat/turns/pop-latest"),
        ("GET", "/chat/turns/current"), ("POST", "/chat/turns/{turnId}/stop"),
        ("GET", "/chat/turns/{turnId}/events"),
        ("POST", "/chat/turns/pending/stop"),
        ("GET", "/agent/admission"),
        ("POST", "/agent/admission/{operationId}/stop")
    ];

    [Fact]
    public async Task DirectoryAndPage_SeparateVisitorFromTarget_AndDoNotExposePrivateConfig() {
        await using var host = CreateTwoCharacterHost();
        using HttpClient client = host.CreateClient();
        using HttpResponseMessage anonymous = await client.GetAsync("/api/v1/characters");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using HttpResponseMessage login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("galatea_player_auth="));

        using JsonDocument me = await ReadJsonAsync(client, "/api/v1/me");
        Assert.Equal(["playerId", "name", "maintenanceMode"], me.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(PlayerId, me.RootElement.GetProperty("playerId").GetString());
        using JsonDocument directory = await ReadJsonAsync(client, "/api/v1/characters");
        Assert.Equal(2, directory.RootElement.GetArrayLength());
        Assert.All(directory.RootElement.EnumerateArray(), value =>
            Assert.Equal(["characterId", "name"], value.EnumerateObject().Select(x => x.Name)));
        string selection = await client.GetStringAsync("/");
        Assert.Contains("href=\"/characters/alice\"", selection);
        Assert.Contains("href=\"/characters/beta\"", selection);
        Assert.DoesNotContain("galateaBootstrap", selection);
        string page = await client.GetStringAsync("/characters/beta");
        Assert.DoesNotContain("playerId:", page);
        Assert.Contains("characterId: \"beta\"", page);
        Assert.Contains("apiBase: \"/api/v1/characters/beta\"", page);
        Assert.Contains("href=\"/\"", page);
        Assert.DoesNotContain("userId:", page);
    }

    [Fact]
    public async Task AllCharacterRoutes_RequireExplicitTarget_AndUnknownTargetsFailBeforeSessionAttach() {
        await using var host = CreateTwoCharacterHost();
        using HttpClient client = host.CreateClient();
        _ = await LoginAsync(client);
        var source = host.Factory.Services.GetRequiredService<EndpointDataSource>();
        RouteEndpoint[] characterEndpoints = source.Endpoints.OfType<RouteEndpoint>()
            .Where(value => value.RoutePattern.RawText?.StartsWith("/api/v1/characters/{characterId}/", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(CharacterRoutes.Length, characterEndpoints.Length);
        foreach ((string method, string suffix) in CharacterRoutes) {
            Assert.Contains(characterEndpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/characters/{characterId}" + suffix
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            string concrete = suffix.Replace("{turnId}", new string('a', 32))
                .Replace("{operationId}", new string('b', 32));
            using var retired = new HttpRequestMessage(new HttpMethod(method), "/api/v1" + concrete);
            if (method == "POST") retired.Content = JsonContent.Create(new { });
            using HttpResponseMessage response = await client.SendAsync(retired);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using HttpResponseMessage unknown = await client.GetAsync("/api/v1/characters/missing/chat/turns/current");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("character-not-found", await unknown.Content.ReadAsStringAsync());
        using HttpResponseMessage unknownPage = await client.GetAsync("/characters/missing");
        Assert.Equal(HttpStatusCode.NotFound, unknownPage.StatusCode);
        Assert.False(Directory.Exists(host.SessionDirectory));
    }

    [Fact]
    public async Task OnePlayer_CanActOnEitherCharacter_ButCannotStopOrReadAnotherCharactersTurn() {
        await using var host = CreateTwoCharacterHost();
        using HttpClient client = host.CreateClient();
        _ = await LoginAsync(client);
        var service = host.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost alpha = await service.GetSessionAsync("alice", CancellationToken.None);
        GalateaLiveTurn pending = service.StartTurn(alpha, "尚未执行的甲动作", new GalateaTurnOptions("test"),
            new GalateaSenderSnapshot("player", PlayerId, "访客"));
        try {
            using JsonDocument alphaCurrent = await ReadJsonAsync(client, First + "/chat/turns/current");
            Assert.Equal(pending.TurnId, alphaCurrent.RootElement.GetProperty("turnId").GetString());
            using JsonDocument betaCurrent = await ReadJsonAsync(client, Second + "/chat/turns/current");
            Assert.Equal("idle", betaCurrent.RootElement.GetProperty("status").GetString());
            using HttpResponseMessage wrongStop = await client.PostAsync(Second + "/chat/turns/" + pending.TurnId + "/stop", null);
            Assert.Equal(HttpStatusCode.NotFound, wrongStop.StatusCode);
            using HttpResponseMessage wrongEvents = await client.GetAsync(Second + "/chat/turns/" + pending.TurnId + "/events");
            Assert.Equal(HttpStatusCode.NotFound, wrongEvents.StatusCode);
            using HttpResponseMessage accepted = await client.PostAsJsonAsync(Second + "/chat/turns", new {
                message = "给乙的动作", diagnosticConnectionId = "test"
            });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.Equal("beta", Assert.Single(accepted.Headers.GetValues("Galatea-Character-Id")));
            await WaitForIdleAsync(client, Second);
            CharacterSessionHost beta = await service.GetSessionAsync("beta", CancellationToken.None);
            SessionInputContent content = Assert.Single(beta.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns).ObservationContent;
            Assert.True(content.IsStructured);
            Assert.Equal(PlayerId, content.JsonValue.GetProperty("sender").GetProperty("id").GetString());
            Assert.Equal("player", content.JsonValue.GetProperty("sender").GetProperty("kind").GetString());
            Assert.Equal("给乙的动作", content.JsonValue.GetProperty("action").GetProperty("text").GetString());
            Assert.Empty(alpha.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns);
            using HttpResponseMessage ownStop = await client.PostAsync(First + "/chat/turns/" + pending.TurnId + "/stop", null);
            Assert.Equal(HttpStatusCode.NoContent, ownStop.StatusCode);
        }
        finally {
            pending.PublishError(GalateaSseErrorCode.InternalFailure);
            service.FinishTurn(alpha, pending);
            pending.Complete();
        }
    }

    [Theory]
    [InlineData("/api/v1/characters/a%2Fb/chat/turns/current", "a/b")]
    [InlineData("/api/v1/characters/a%252Fb/chat/turns/current", "a%2Fb")]
    [InlineData("/characters/%E8%A7%92%E8%89%B2?x=%2F", "角色")]
    public void CharacterRoute_DecodesOnlyTheRawSegmentOnce(string rawTarget, string expected) {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = rawTarget;
        Assert.True(GalateaHttpV1.TryReadCharacterRouteId(context, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/characters/a%2")]
    [InlineData("/characters/%FF")]
    [InlineData("/characters/%GG")]
    [InlineData("/characters/")]
    public void CharacterRoute_RejectsMalformedEncodedIdentity(string rawTarget) {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = rawTarget;
        Assert.False(GalateaHttpV1.TryReadCharacterRouteId(context, out _));
    }

    [Fact]
    public async Task EncodedSlashAndLiteralPercentSequence_SelectDistinctConfiguredCharacters() {
        await using var host = CreateTwoCharacterHost();
        // TestServer drops the original request target; use the actual HTTP
        // server to prove percent decoding against two distinct configured IDs.
        host.Factory.UseKestrel(0);
        JsonNode config = JsonNode.Parse(File.ReadAllText(host.ConfigPath))!;
        config["characters"]![0]!["id"] = "a/b";
        config["characters"]![1]!["id"] = "a%2Fb";
        File.WriteAllText(host.ConfigPath, config.ToJsonString());
        using HttpClient client = host.CreateClient();
        var server = host.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        client.BaseAddress = new Uri(Assert.Single(server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses));
        _ = await LoginAsync(client);
        foreach (string id in new[] { "a/b", "a%2Fb" }) {
            string segment = Uri.EscapeDataString(id);
            using HttpResponseMessage response = await client.GetAsync("/api/v1/characters/" + segment + "/mailbox/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(segment, Assert.Single(response.Headers.GetValues("Galatea-Character-Id")));
            string page = await client.GetStringAsync("/characters/" + segment);
            Assert.Contains("characterId: " + JsonSerializer.Serialize(id, GalateaJson.Options), page);
        }
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("角色", true)]
    [InlineData("a%2Fb", false)]
    [InlineData("a/b", false)]
    public void MissingRawTarget_UsesOnlyUnambiguousAlreadyDecodedRoute(string route, bool succeeds) {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.RouteValues["characterId"] = route;
        Assert.Equal(succeeds, GalateaHttpV1.TryReadCharacterRouteId(context, out string actual));
        if (succeeds) Assert.Equal(route, actual);
    }

    [Theory]
    [InlineData("family_chat_auth", "galatea_user_id", "alice")]
    [InlineData("galatea_player_auth", "galatea_user_id", "alice")]
    [InlineData("galatea_player_auth", "galatea_player_id", "removed-player")]
    public async Task OldOrUnconfiguredPlayerTickets_AreRejected(string cookieName, string claimType, string id) {
        await using var host = CreateTwoCharacterHost();
        using HttpClient client = host.CreateClient();
        var options = host.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("GalateaCookie");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, id)], "GalateaCookie"));
        string ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, "GalateaCookie"));
        using var request = new HttpRequestMessage(HttpMethod.Get, Second + "/mailbox/status");
        request.Headers.Add("Cookie", cookieName + "=" + ticket);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PreviouslyValidCookie_IsRejectedAfterRemovingPlayerAndRestarting() {
        var factory = new CompletingFactory();
        GalateaTestHost host = CreateTwoCharacterHost(factory, deleteFilesOnDispose: false);
        try {
            string cookie;
            using (HttpClient client = host.CreateClient()) {
                using HttpResponseMessage login = await LoginAsync(client);
                cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("galatea_player_auth=")).Split(';')[0];
                using HttpResponseMessage before = await client.GetAsync(Second + "/mailbox/status");
                Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            }
            await host.DisposeAsync();
            JsonNode config = JsonNode.Parse(File.ReadAllText(host.ConfigPath))!;
            config["players"] = new JsonArray();
            File.WriteAllText(host.ConfigPath, config.ToJsonString());
            await using var restarted = new GalateaWebApplicationFactory(host.ConfigPath, factory,
                DisabledGalateaUserMessageNormalizer.Instance, null, null);
            using HttpClient next = restarted.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false, HandleCookies = false
            });
            using var request = new HttpRequestMessage(HttpMethod.Get, Second + "/mailbox/status");
            request.Headers.Add("Cookie", cookie);
            using HttpResponseMessage after = await next.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
        finally {
            await host.DisposeAsync();
            if (Directory.Exists(host.RootDirectory)) Directory.Delete(host.RootDirectory, recursive: true);
        }
    }

    private static GalateaTestHost CreateTwoCharacterHost(CompletingFactory? factory = null, bool deleteFilesOnDispose = true) {
        GalateaTestHost host = GalateaTestHost.CreateMissingSession(factory ?? new CompletingFactory(),
            DisabledGalateaUserMessageNormalizer.Instance, deleteFilesOnDispose: deleteFilesOnDispose);
        JsonNode config = JsonNode.Parse(File.ReadAllText(host.ConfigPath))!;
        config["players"]![0]!["id"] = PlayerId;
        JsonObject beta = (JsonObject)config["characters"]![0]!.DeepClone();
        beta["id"] = "beta";
        beta["name"] = "角色乙";
        beta["sessionDir"] = Path.Combine(host.RootDirectory, "beta-session");
        beta["delegationStateDir"] = Path.Combine(host.RootDirectory, "beta-delegation");
        beta["characterMemoryStateDir"] = Path.Combine(host.RootDirectory, "beta-memory");
        beta["homeDir"] = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(host.ConfigPath)!, "homes", "beta")).FullName;
        config["characters"]!.AsArray().Add(beta);
        File.WriteAllText(host.ConfigPath, config.ToJsonString());
        return host;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client) => client.PostAsync("/login",
        new FormUrlEncodedContent(new Dictionary<string, string> { ["playerId"] = PlayerId, ["password"] = "pw1" }));

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient client, string path) {
        using HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task WaitForIdleAsync(HttpClient client, string apiBase) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true) {
            using JsonDocument current = await ReadJsonAsync(client, apiBase + "/chat/turns/current");
            if (current.RootElement.GetProperty("status").GetString() == "idle") return;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class CompletingFactory : ICompletionClientFactory, ICompletionClient {
        public string Name => "player-character-test";
        public string ApiSpecId => "openai-chat-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
            CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("角色作出回应。")]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
        }
    }
}

using System.Net;
using System.Text;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaLabAdmissionTests {
    [Fact]
    public async Task OnlyExplicitUnacceptedBusyCanBeRetried() {
        var handler = new ScriptHandler(call => Task.FromResult(call == 1
            ? Conflict("turn-busy", "null") : new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        using var response = await GalateaLabAdmission.PostFreshAsync(http, new("input"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("recovery-required", "null")]
    [InlineData("turn-busy", "\"already-running\"")]
    public async Task OtherConflictsAreNotReplayed(string code, string turnId) {
        var handler = new ScriptHandler(_ => Task.FromResult(Conflict(code, turnId)));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GalateaLabAdmission.PostFreshAsync(http, new("input")));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LostResponseIsNotReplayed() {
        var handler = new ScriptHandler(_ => throw new HttpRequestException("response lost"));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => GalateaLabAdmission.PostFreshAsync(http, new("input")));
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Conflict(string code, string turnId) => new(HttpStatusCode.Conflict) {
        Content = new StringContent("{\"code\":\"" + code + "\",\"error\":\"busy\",\"turnId\":" + turnId + "}", Encoding.UTF8, "application/json")
    };
    private sealed class ScriptHandler(Func<int, Task<HttpResponseMessage>> next) : HttpMessageHandler {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => next(++Calls);
    }
}

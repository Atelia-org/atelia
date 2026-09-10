using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// A two-request, fail-closed external boundary. The first accepted request is
/// held without returning headers; only explicitly armed restart gets an SSE
/// answer. It never forwards requests or consults credentials.
/// </summary>
internal sealed class GalateaLabCrashResponsesServer : IAsyncDisposable {
    internal const string Model = "galatea-lab-model";
    internal const string UserMessage = "Synthetic process-crash rehearsal.";
    internal const string Answer = "Synthetic authorized restart completed.";
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _firstReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstDisconnected = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private string? _firstBody;
    private int _calls;
    private int _restartAuthorized;

    private GalateaLabCrashResponsesServer(WebApplication app) => _app = app;

    internal Uri BaseAddress { get; private set; } = null!;
    internal int Calls => Volatile.Read(ref _calls);
    internal Task FirstReceived => _firstReceived.Task;
    internal Task FirstDisconnected => _firstDisconnected.Task;

    internal static async Task<GalateaLabCrashResponsesServer> StartAsync() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.Limits.MaxRequestBodySize = 128 * 1024;
            options.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        var server = new GalateaLabCrashResponsesServer(app);
        app.Run(server.HandleAsync);
        try {
            await app.StartAsync();
            server.BaseAddress = new Uri(app.Urls.Single() + "/");
            return server;
        }
        catch {
            await server.DisposeAsync();
            throw;
        }
    }

    internal void AuthorizeRestart() {
        Require(Calls == 1, "Restart authorization requires exactly one prior request.");
        Require(Interlocked.Exchange(ref _restartAuthorized, 1) == 0,
            "Restart must only be authorized once.");
        ThrowIfFailed();
    }

    internal void AssertComplete() {
        ThrowIfFailed();
        Require(Calls == 2, "The crash script must receive exactly two requests.");
    }

    public async ValueTask DisposeAsync() {
        await _stop.CancelAsync();
        await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await _app.DisposeAsync();
        _stop.Dispose();
    }

    private async Task HandleAsync(HttpContext context) {
        int call = Interlocked.Increment(ref _calls);
        try {
            Require(context.Request.Method == "POST"
                    && context.Request.Path == "/v1/responses",
                "Unexpected provider route or method.");
            Require(context.Request.Headers.Authorization == "Bearer synthetic-lab-key",
                "The provider must use only the synthetic lab credential.");
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(context.RequestAborted);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement request = document.RootElement;
            Require(request.GetProperty("model").GetString() == Model,
                "Unexpected provider model.");
            Require(request.GetProperty("stream").GetBoolean(),
                "The production request must be streaming.");
            Require(request.GetProperty("input").EnumerateArray().Any(item =>
                    item.TryGetProperty("role", out JsonElement role)
                    && role.GetString() == "user"
                    && item.GetProperty("content").EnumerateArray().Any(content =>
                        content.TryGetProperty("text", out JsonElement text)
                        && text.GetString()!.Contains(UserMessage, StringComparison.Ordinal))),
                "The expected synthetic observation is missing.");

            if (call == 1) {
                _firstBody = body;
                _firstReceived.TrySetResult();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted, _stop.Token);
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                    if (context.RequestAborted.IsCancellationRequested) {
                        _firstDisconnected.TrySetResult();
                    }
                }
                return;
            }

            Require(call == 2 && Volatile.Read(ref _restartAuthorized) == 1,
                "An unplanned or unauthorized provider request was attempted.");
            // No request surgery: exact equality covers the frozen context and
            // every production converter field, not just the model/message.
            Require(string.Equals(_firstBody, body, StringComparison.Ordinal),
                "Explicit restart must replay the same production request bytes.");
            context.Response.ContentType = "text/event-stream";
            string stream = Event(new {
                type = "response.output_text.delta", delta = Answer
            }) + Event(new {
                type = "response.completed",
                response = new { id = "lab_restart", status = "completed" }
            });
            await context.Response.WriteAsync(stream, context.RequestAborted);
        }
        catch (Exception exception) {
            Interlocked.CompareExchange(ref _failure, exception, null);
            _firstReceived.TrySetException(exception);
            context.Abort();
        }
    }

    private void ThrowIfFailed() {
        if (_failure is { } failure) {
            throw new InvalidOperationException("The local provider script failed.", failure);
        }
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private static string Event(object payload) =>
        "data: " + JsonSerializer.Serialize(payload) + "\n\n";
}

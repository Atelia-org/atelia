using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Galatea.Server.CharacterMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Exact three-call script: held main request, authorized identical replay,
/// then one no-artifact Note extraction. No DerivedInfo work is permitted:
/// the seed must settle that work before starting the production process.
/// </summary>
internal sealed class GalateaLabNoteReceiptResponsesServer : IAsyncDisposable {
    internal const string MainModel = "galatea-note-crash-main";
    internal const string HelperModel = "galatea-note-crash-helper";
    internal const string UserMessage = "Synthetic receipt crash checkpoint.";
    internal const string Answer = "The synthetic receipt has been acknowledged.";
    internal const string ApiKey = "synthetic-note-crash-key";
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _firstReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstDisconnected = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _noticeBody;
    private string? _firstBody;
    private Exception? _failure;
    private int _mainCalls;
    private int _helperCalls;
    private int _totalCalls;
    private int _authorized;
    private Task? _disposeTask;

    private GalateaLabNoteReceiptResponsesServer(WebApplication app) => _app = app;

    internal Uri BaseAddress { get; private set; } = null!;
    internal int MainCalls => Volatile.Read(ref _mainCalls);
    internal int HelperCalls => Volatile.Read(ref _helperCalls);
    internal Task FirstReceived => _firstReceived.Task;
    internal Task FirstDisconnected => _firstDisconnected.Task;

    internal static async Task<GalateaLabNoteReceiptResponsesServer> StartAsync() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.Limits.MaxRequestBodySize = 128 * 1024;
            options.Listen(IPAddress.Loopback, 0);
        });
        WebApplication app = builder.Build();
        var server = new GalateaLabNoteReceiptResponsesServer(app);
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

    internal void ExpectReceipt(string noticeBody) {
        Require(_noticeBody is null && Volatile.Read(ref _totalCalls) == 0,
            "The receipt script must be armed exactly once before dispatch.");
        ArgumentException.ThrowIfNullOrEmpty(noticeBody);
        _noticeBody = noticeBody;
    }

    internal void AuthorizeRestart() {
        Require(MainCalls == 1 && HelperCalls == 0,
            "Authorization requires exactly one held main request and no helper call.");
        Require(Interlocked.Exchange(ref _authorized, 1) == 0,
            "Restart must only be authorized once.");
        ThrowIfFailed();
    }

    internal void AssertBeforeRestart() {
        ThrowIfFailed();
        Require(MainCalls == 1 && HelperCalls == 0 && Volatile.Read(ref _totalCalls) == 1,
            "Recovery admission must not silently dispatch any provider request.");
    }

    internal void AssertComplete() {
        ThrowIfFailed();
        Require(MainCalls == 2 && HelperCalls == 1 && Volatile.Read(ref _totalCalls) == 3,
            "The receipt scenario requires exactly two main and one helper request.");
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync() {
        try {
            await _stop.CancelAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(deadline.Token);
        }
        finally {
            try { await _app.DisposeAsync(); }
            finally { _stop.Dispose(); }
        }
    }

    private async Task HandleAsync(HttpContext context) {
        Interlocked.Increment(ref _totalCalls);
        try {
            Require(context.Request.Method == "POST" && context.Request.Path == "/v1/responses",
                "Unexpected provider route or method.");
            Require(context.Request.Headers.Authorization == "Bearer " + ApiKey,
                "Only the synthetic Note crash credential is permitted.");
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(context.RequestAborted);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement request = document.RootElement;
            Require(request.GetProperty("stream").GetBoolean(), "A streaming request is required.");
            string? model = request.GetProperty("model").GetString();
            if (model == MainModel) {
                int call = Interlocked.Increment(ref _mainCalls);
                Require(_noticeBody is not null && UserInputContains(request, UserMessage)
                        && UserInputContains(request, _noticeBody),
                    "The main request must contain the synthetic observation and exact frozen receipt.");
                if (call == 1) {
                    Require(HelperCalls == 0, "The settled seed must not dispatch a helper.");
                    _firstBody = body;
                    _firstReceived.TrySetResult();
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        context.RequestAborted, _stop.Token);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                        if (context.RequestAborted.IsCancellationRequested) {
                            _firstDisconnected.TrySetResult();
                        }
                    }
                    return;
                }
                Require(call == 2 && Volatile.Read(ref _authorized) == 1 && HelperCalls == 0,
                    "An unauthorized or extra main request was attempted.");
                Require(string.Equals(_firstBody, body, StringComparison.Ordinal),
                    "Recovery must replay the exact frozen request bytes, including the receipt.");
                await WriteCompletedAsync(context, Answer);
                return;
            }
            Require(model == HelperModel, "Unexpected model at the local provider.");
            Require(Interlocked.Increment(ref _helperCalls) == 1
                    && MainCalls == 2 && Volatile.Read(ref _authorized) == 1,
                "An unplanned helper call was attempted.");
            JsonElement[] tools = request.GetProperty("tools").EnumerateArray().ToArray();
            Require(tools.Length == 1
                    && tools[0].GetProperty("name").GetString() == CharacterNoteExtractor.ToolName
                    && UserInputContains(request, Answer),
                "Only extraction from the completed synthetic acknowledgment is permitted.");
            await WriteCompletedAsync(context, "No qualifying Note request.");
        }
        catch (Exception exception) {
            Interlocked.CompareExchange(ref _failure, exception, null);
            _firstReceived.TrySetException(exception);
            context.Abort();
        }
    }

    private static bool UserInputContains(JsonElement request, string value) =>
        request.GetProperty("input").EnumerateArray().Any(item =>
            item.TryGetProperty("role", out JsonElement role) && role.GetString() == "user"
            && item.GetProperty("content").EnumerateArray().Any(content =>
                content.TryGetProperty("text", out JsonElement text)
                && text.GetString()!.Contains(value, StringComparison.Ordinal)));

    private static Task WriteCompletedAsync(HttpContext context, string text) {
        context.Response.ContentType = "text/event-stream";
        string stream = Event(new { type = "response.output_text.delta", delta = text })
            + Event(new { type = "response.completed",
                response = new { id = "lab_note_receipt", status = "completed" } });
        return context.Response.WriteAsync(stream, context.RequestAborted);
    }

    private void ThrowIfFailed() {
        if (_failure is { } failure) {
            throw new InvalidOperationException("The Note receipt provider script failed.", failure);
        }
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }

    private static string Event(object payload) =>
        "data: " + JsonSerializer.Serialize(payload) + "\n\n";
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Main3 + recap8, in explicit phases. Each recap generation requires exactly
/// one world and one autobiography rewrite against its exact preceding pair.
/// The boundary builds generations 2/3; fresh builds 4/5. Recovery admits no
/// maintenance and must preserve the full main wire body.
/// </summary>
internal sealed class GalateaLabRecapResponsesServer : IAsyncDisposable {
    internal const string MainModel = "galatea-recap-crash-main";
    internal const string RecapModel = "galatea-recap-crash-maintainer";
    internal const string ApiKey = "synthetic-recap-crash-key";
    internal const string CrashMessage = "Synthetic exploration before process loss.";
    internal const string FreshMessage = "Synthetic new exploration after cold recovery.";
    internal const string RecoveryAnswer = "The exploration resumed successfully.";
    internal const string FreshAnswer = "The next exploration completed successfully.";
    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _firstReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, HashSet<string>> _columns = new() {
        [2] = new(StringComparer.Ordinal), [3] = new(StringComparer.Ordinal),
        [4] = new(StringComparer.Ordinal), [5] = new(StringComparer.Ordinal)
    };
    private Task? _disposeTask;
    private Exception? _failure;
    private string? _firstBody;
    private Stage _stage;
    private int _mainCalls;
    private int _recapCalls;
    private int _totalCalls;

    private enum Stage { BeforeCrash, Held, RecoveryAuthorized, Recovered, FreshAuthorized, Completed }
    private GalateaLabRecapResponsesServer(WebApplication app) => _app = app;
    internal Uri BaseAddress { get; private set; } = null!;
    internal Task FirstReceived => _firstReceived.Task;
    internal Task FirstDisconnected => _firstDisconnected.Task;

    internal static async Task<GalateaLabRecapResponsesServer> StartAsync() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => {
            options.Limits.MaxRequestBodySize = 256 * 1024;
            options.Listen(IPAddress.Loopback, 0);
        });
        WebApplication app = builder.Build();
        var server = new GalateaLabRecapResponsesServer(app);
        app.Run(server.HandleAsync);
        try {
            await app.StartAsync();
            server.BaseAddress = new Uri(app.Urls.Single() + "/");
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    internal void AssertHeld() {
        lock (_gate) {
            ThrowIfFailed();
            Require(_stage == Stage.Held && _mainCalls == 1 && _recapCalls == 4 && _totalCalls == 5,
                "The frozen boundary requires one held main and exactly four recap calls.");
        }
    }

    internal void AuthorizeRecovery() {
        lock (_gate) { AssertHeld(); _stage = Stage.RecoveryAuthorized; }
    }

    internal void AssertRecovered() {
        lock (_gate) {
            ThrowIfFailed();
            Require(_stage == Stage.Recovered && _mainCalls == 2 && _recapCalls == 4 && _totalCalls == 6,
                "Recovery must add exactly one main replay and no recap work.");
        }
    }

    internal void AuthorizeFresh() {
        lock (_gate) { AssertRecovered(); _stage = Stage.FreshAuthorized; }
    }

    internal void AssertComplete() {
        lock (_gate) {
            ThrowIfFailed();
            Require(_stage == Stage.Completed && _mainCalls == 3 && _recapCalls == 8 && _totalCalls == 11,
                "The entire local script requires exactly three main and eight recap calls.");
            Require(_columns.Values.All(columns => columns.Count == 2),
                "All four recap generations must contain exactly the two required columns.");
        }
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
        lock (_gate) { _totalCalls++; }
        try {
            Require(context.Request.Method == "POST" && context.Request.Path == "/v1/responses",
                "Unexpected local provider route or method.");
            Require(context.Request.Headers.Authorization == "Bearer " + ApiKey,
                "Only the synthetic recap credential is allowed.");
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(context.RequestAborted);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement request = document.RootElement;
            Require(request.GetProperty("stream").GetBoolean(), "Expected a streaming request.");
            string? model = request.GetProperty("model").GetString();
            if (model == RecapModel) {
                string result;
                lock (_gate) { result = AcceptRecap(request); }
                await WriteCompletedAsync(context, result);
                return;
            }
            Require(model == MainModel, "Unexpected model at the local recap provider.");
            string? answer;
            lock (_gate) { answer = AcceptMain(request, body); }
            if (answer is not null) {
                await WriteCompletedAsync(context, answer);
                return;
            }
            _firstReceived.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) {
                if (context.RequestAborted.IsCancellationRequested) { _firstDisconnected.TrySetResult(); }
            }
        }
        catch (Exception exception) {
            lock (_gate) { _failure ??= exception; }
            _firstReceived.TrySetException(exception);
            context.Abort();
        }
    }

    private string AcceptRecap(JsonElement request) {
        Require(_stage is Stage.BeforeCrash or Stage.FreshAuthorized,
            "Recap work is forbidden while held, recovering, or after completion.");
        Require(!request.TryGetProperty("tools", out JsonElement tools)
                || tools.ValueKind == JsonValueKind.Null || tools.GetArrayLength() == 0,
            "The recap rewriter must not receive tools.");
        string[] userTexts = Texts(request, "user").ToArray();
        using JsonDocument prior = JsonDocument.Parse(userTexts[0]);
        using JsonDocument task = JsonDocument.Parse(userTexts[^1]);
        Require(prior.RootElement.GetProperty("schema").GetString() == "atelia.recap.prior.v1"
                && task.RootElement.GetProperty("schema").GetString() == "atelia.recap.input.v1",
            "Unexpected recap protocol contract.");
        JsonElement[] columns = prior.RootElement.GetProperty("columns").EnumerateArray().ToArray();
        Require(columns.Length == 2, "Recap prior must contain exactly the preceding two cells.");
        var priorValues = columns.ToDictionary(column => column.GetProperty("logicalColumnId").GetString()!,
            column => column.GetProperty("content").GetString(), StringComparer.Ordinal);
        int firstAllowedGeneration = _stage == Stage.BeforeCrash ? 2 : 4;
        int generation = Enumerable.Range(firstAllowedGeneration, 2).SingleOrDefault(candidate =>
            priorValues.GetValueOrDefault("world-understanding") == GalateaRecapFixture.World(candidate - 1)
            && priorValues.GetValueOrDefault("autobiography") == GalateaRecapFixture.Autobiography(candidate - 1));
        Require(generation != 0, "Recap prior cells are not an exact allowed preceding generation.");
        Require(generation == 2 || _columns[generation - 1].Count == 2,
            "A recap row cannot advance before both preceding cells completed.");
        string columnId = task.RootElement.GetProperty("logicalColumnId").GetString()!;
        Require(columnId is "world-understanding" or "autobiography", "Unknown recap column.");
        HashSet<string> seen = _columns[generation];
        Require(seen.Add(columnId), "Duplicate recap dispatch for one generation/column.");
        _recapCalls++;
        return GalateaRecapFixture.RecapReply(columnId, generation);
    }

    private string? AcceptMain(JsonElement request, string body) {
        _mainCalls++;
        int generation = _stage == Stage.FreshAuthorized ? 5 : 3;
        Require(_columns[generation - 1].Count == 2 && _columns[generation].Count == 2,
            "Main admission preceded its two complete recap generations.");
        string world = "## galatea.world-understanding Galatea积累的世界理解：\n\n~~~~recap-block\n"
            + GalateaRecapFixture.World(generation) + "\n~~~~";
        string autobiography = "## galatea.first-person-autobiography Galatea积累的第一人称自传：\n\n~~~~recap-block\n"
            + GalateaRecapFixture.Autobiography(generation) + "\n~~~~";
        Require(Texts(request, "user").Any(text => text.Contains(world, StringComparison.Ordinal))
                && Texts(request, "assistant").Any(text => text.Contains(autobiography, StringComparison.Ordinal)),
            "Main request did not adopt both recap cells in their formal carriers.");
        string latest = Texts(request, "user").Last();
        if (_stage == Stage.BeforeCrash && _mainCalls == 1) {
            Require(latest.Contains(CrashMessage, StringComparison.Ordinal), "Missing current crash Observation.");
            _firstBody = body;
            _stage = Stage.Held;
            return null;
        }
        if (_stage == Stage.RecoveryAuthorized && _mainCalls == 2) {
            Require(string.Equals(_firstBody, body, StringComparison.Ordinal),
                "Explicit recovery changed the frozen production request bytes.");
            _stage = Stage.Recovered;
            return RecoveryAnswer;
        }
        Require(_stage == Stage.FreshAuthorized && _mainCalls == 3,
            "Unexpected or unauthorized main dispatch.");
        Require(latest.Contains(FreshMessage, StringComparison.Ordinal), "Missing current fresh Observation.");
        _stage = Stage.Completed;
        return FreshAnswer;
    }

    private static IEnumerable<string> Texts(JsonElement request, string role) =>
        request.GetProperty("input").EnumerateArray()
            .Where(item => item.TryGetProperty("role", out JsonElement value) && value.GetString() == role)
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(content => content.TryGetProperty("text", out _))
            .Select(content => content.GetProperty("text").GetString()!);

    private static Task WriteCompletedAsync(HttpContext context, string text) {
        context.Response.ContentType = "text/event-stream";
        return context.Response.WriteAsync(Event(new { type = "response.output_text.delta", delta = text })
            + Event(new { type = "response.completed", response = new { id = "lab_recap", status = "completed" } }),
            context.RequestAborted);
    }
    private void ThrowIfFailed() {
        if (_failure is { } failure) { throw new InvalidOperationException("The recap provider script failed.", failure); }
    }
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
    private static string Event(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";
}

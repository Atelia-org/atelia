using System.Text.Json;
using Atelia.TextAdv2.GameServer;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Runtime;

const string HostRunningMode = "host-running";
const string HostAliveReadiness = "alive";

var builder = WebApplication.CreateBuilder(args);

var configuredRepoDir = builder.Configuration["TextAdv2:RepoDir"] ?? ".atelia/textadv2-dev-world";
string resolvedRepoDir = Path.GetFullPath(configuredRepoDir, builder.Environment.ContentRootPath);
string bootstrapMode = builder.Configuration["TextAdv2:BootstrapMode"] ?? GameServerHostPolicy.SampleWorldDevBootstrapMode;
var hostPolicy = GameServerHostPolicy.Create(configuredRepoDir, resolvedRepoDir, bootstrapMode);

builder.Services.AddSingleton(hostPolicy);
builder.Services.AddSingleton<RuntimeService>(_ => new RuntimeService(hostPolicy.OpenRuntime));

var app = builder.Build();

app.MapGet("/", (RuntimeService runtime) => Results.Ok(BuildRuntimeStatus(hostPolicy, bootstrapMode, runtime)));
app.MapGet("/healthz", (RuntimeService runtime) => BuildHealthStatus(runtime));
app.MapGet("/admin/runtime-status", (RuntimeService runtime) => Results.Ok(BuildRuntimeStatus(hostPolicy, bootstrapMode, runtime)));
app.MapGet("/admin/world", (RuntimeService runtime) => ExecuteText(runtime, DevTextRenderer.RenderWorld));
app.MapGet("/admin/time", (RuntimeService runtime) => ExecuteJson(runtime, static x => x.ObserveTime()));
app.MapPost("/admin/locations",
    (CreateLocationRequest request, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.CreateLocation(request.Id, request.Name, request.Description))
);
app.MapPost("/admin/actors",
    (CreateActorRequest request, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.CreateActor(request.Id, request.Name, request.CurrentLocationId))
);
app.MapPost("/admin/passages",
    (CreatePassageRequest request, RuntimeService runtime)
    => ExecuteJson(
        runtime,
        x => x.CreatePassage(
            request.Id,
            request.LocationAId,
            request.ExitNameFromA,
            request.LocationBId,
            request.ExitNameFromB,
            request.ResolveTravelMode(),
            request.BaseTravelCost ?? 1
        )
    )
);
app.MapPost("/admin/advance-time/{ticks}",
    (string ticks, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.AdvanceTime(ParseNonNegativeTickDelta(ticks)))
);
app.MapGet("/admin/route-acceleration",
    (RuntimeService runtime)
    => ExecuteJson(runtime, static x => x.ObserveRouteAcceleration())
);
app.MapPost("/admin/route-acceleration/rebuild",
    (string? landmarks, RuntimeService runtime)
    => ExecuteJson(runtime, x => SampleWorldBootstrap.RebuildRouteAcceleration(x, landmarks))
);

if (hostPolicy.SampleWorldResetEnabled) {
    app.MapPost("/admin/reset-sample-world",
        (RuntimeService runtime) => {
            runtime.ReplaceRuntime(hostPolicy.ResetRuntime);
            return Results.Ok(BuildRuntimeStatus(hostPolicy, bootstrapMode, runtime));
        }
    );
}

app.MapGet("/admin/locations/{locationId}",
    (string locationId, RuntimeService runtime)
    => ExecuteText(runtime, x => DevTextRenderer.RenderLocation(x, locationId))
);
app.MapGet("/admin/locations/{locationId}/observation",
    (string locationId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveLocation(locationId))
);
app.MapGet("/admin/locations/{locationId}/navigation",
    (string locationId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveNavigation(locationId))
);

app.MapGet("/actors/{actorId}/observation",
    (string actorId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveActor(actorId))
);
app.MapGet("/actors/{actorId}/context",
    (string actorId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveActorContext(actorId))
);
app.MapGet("/actors/{actorId}/navigation",
    (string actorId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveActorNavigation(actorId))
);
app.MapPost("/actors/{actorId}/moves/{passageId}",
    (string actorId, string passageId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.MoveActor(actorId, passageId))
);
app.MapGet("/actors/{actorId}/route-trace",
    (string actorId, RuntimeService runtime)
    => ExecuteText(runtime, x => DevTextRenderer.RenderRouteTrace(x.TraceActorRoute(actorId)))
);
app.MapGet("/actors/{actorId}/route-trace/json",
    (string actorId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.TraceActorRoute(actorId))
);
app.MapGet("/actors/{actorId}/plan-route/{toLocationId}",
    (string actorId, string toLocationId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.PlanActorRoute(actorId, toLocationId))
);

app.MapGet("/admin/routes/{fromLocationId}/{toLocationId}",
    (string fromLocationId, string toLocationId, RuntimeService runtime)
    => ExecuteJson(runtime, x => x.PlanRoute(fromLocationId, toLocationId))
);

app.Run();

static IResult ExecuteText(RuntimeService runtime, Func<SerialWorldRuntime, string> operation) {
    try {
        var result = runtime.Invoke(operation);
        return Results.Content(result, HostContentTypes.PlainText);
    }
    catch (RuntimeUnavailableException ex) {
        return BuildRuntimeUnavailableResult(ex.Status);
    }
    catch (ArgumentException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static IResult ExecuteJson<T>(RuntimeService runtime, Func<SerialWorldRuntime, T> operation) {
    try {
        var result = runtime.Invoke(operation);
        return Results.Content(JsonSerializer.Serialize(result, TextAdv2HostJson.Options), HostContentTypes.Json);
    }
    catch (RuntimeUnavailableException ex) {
        return BuildRuntimeUnavailableResult(ex.Status);
    }
    catch (ArgumentException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static long ParseNonNegativeTickDelta(string value) {
    if (!long.TryParse(value, out long ticks)) { throw new InvalidOperationException($"AdvanceTime requires an integer tick delta, but received '{value}'."); }

    ArgumentOutOfRangeException.ThrowIfNegative(ticks);
    return ticks;
}

static IResult BuildHealthStatus(RuntimeService runtimeService) {
    var runtime = runtimeService.DescribeStatus();
    var payload = BuildAvailabilityStatus(runtime);

    return runtime.Readiness == RuntimeReadiness.Ready
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
}

static IResult BuildRuntimeUnavailableResult(RuntimeStatusSnapshot runtime) {
    var payload = BuildAvailabilityStatus(runtime);
    return Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
}

static object BuildAvailabilityStatus(RuntimeStatusSnapshot runtime) {
    return new {
        status = runtime.Readiness == RuntimeReadiness.Ready ? "ok" : "degraded",
        service = "TextAdv2.GameServer",
        mode = HostRunningMode,
        host = new {
            readiness = HostAliveReadiness,
        },
        runtime,
    };
}

static object BuildRuntimeStatus(GameServerHostPolicy hostPolicy, string bootstrapMode, RuntimeService runtimeService) {
    var runtime = runtimeService.DescribeStatus();
    return new {
        service = "TextAdv2.GameServer",
        mode = HostRunningMode,
        host = new {
            readiness = HostAliveReadiness,
        },
        configuration = new {
            configuredRepoDir = hostPolicy.ConfiguredRepoDir,
            resolvedRepoDir = hostPolicy.ResolvedRepoDir,
            bootstrapMode,
        },
        hostPolicy = new {
            bootstrapMode = hostPolicy.BootstrapMode,
            runtimeOpenMode = hostPolicy.RuntimeOpenMode,
            sampleWorldResetEnabled = hostPolicy.SampleWorldResetEnabled,
            repositoryLockRetry = new {
                enabled = hostPolicy.RepositoryLockRetryEnabled,
                maxRetryCount = hostPolicy.RepositoryLockRetryCount,
            },
            notes = hostPolicy.Notes,
        },
        runtime,
        plannedEndpoints = hostPolicy.PlannedEndpoints,
    };
}

public partial class Program {
}

file static class TextAdv2HostJson {
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions() {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        TextAdv2Json.AddHostConverters(options);
        return options;
    }
}

using System.Text.Json;
using Atelia.TextAdv2.GameServer;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Session;

const string HostRunningMode = "host-running";

var builder = WebApplication.CreateBuilder(args);

var configuredRepoDir = builder.Configuration["TextAdv2:RepoDir"] ?? ".atelia/textadv2-dev-world";
string resolvedRepoDir = Path.GetFullPath(configuredRepoDir, builder.Environment.ContentRootPath);
string bootstrapMode = builder.Configuration["TextAdv2:BootstrapMode"] ?? GameServerHostPolicy.SampleWorldDevBootstrapMode;
var hostPolicy = GameServerHostPolicy.Create(configuredRepoDir, resolvedRepoDir, bootstrapMode);

builder.Services.AddSingleton(hostPolicy);
builder.Services.AddSingleton(_ => new SessionService(hostPolicy.OpenSession()));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(BuildSessionStatus(hostPolicy, bootstrapMode)));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", host = "TextAdv2.GameServer", mode = HostRunningMode }));
app.MapGet("/admin/session-status", () => Results.Ok(BuildSessionStatus(hostPolicy, bootstrapMode)));
app.MapGet("/admin/world", (SessionService session) => ExecuteText(session, DevTextRenderer.RenderWorld));
app.MapGet("/admin/time", (SessionService session) => ExecuteJson(session, static x => x.ObserveTime()));
app.MapPost("/admin/locations",
    (CreateLocationRequest request, SessionService session)
    => ExecuteJson(session, x => x.CreateLocation(request.Id, request.Name, request.Description))
);
app.MapPost("/admin/actors",
    (CreateActorRequest request, SessionService session)
    => ExecuteJson(session, x => x.CreateActor(request.Id, request.Name, request.CurrentLocationId))
);
app.MapPost("/admin/passages",
    (CreatePassageRequest request, SessionService session)
    => ExecuteJson(
        session,
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
    (string ticks, SessionService session)
    => ExecuteJson(session, x => x.AdvanceTime(ParseNonNegativeTickDelta(ticks)))
);
app.MapGet("/admin/route-acceleration",
    (SessionService session)
    => ExecuteJson(session, static x => x.ObserveRouteAcceleration())
);
app.MapPost("/admin/route-acceleration/rebuild",
    (string? landmarks, SessionService session)
    => ExecuteJson(session, x => SampleWorldBootstrap.RebuildRouteAcceleration(x, landmarks))
);

if (hostPolicy.SampleWorldResetEnabled) {
    app.MapPost("/admin/reset-sample-world",
        (SessionService session) => {
            session.ReplaceSession(hostPolicy.ResetSession);
            return Results.Ok(BuildSessionStatus(hostPolicy, bootstrapMode));
        }
    );
}

app.MapGet("/admin/locations/{locationId}",
    (string locationId, SessionService session)
    => ExecuteText(session, x => DevTextRenderer.RenderLocation(x, locationId))
);
app.MapGet("/admin/locations/{locationId}/observation",
    (string locationId, SessionService session)
    => ExecuteJson(session, x => x.ObserveLocation(locationId))
);
app.MapGet("/admin/locations/{locationId}/navigation",
    (string locationId, SessionService session)
    => ExecuteJson(session, x => x.ObserveNavigation(locationId))
);

app.MapGet("/actors/{actorId}/observation",
    (string actorId, SessionService session)
    => ExecuteJson(session, x => x.ObserveActor(actorId))
);
app.MapGet("/actors/{actorId}/context",
    (string actorId, SessionService session)
    => ExecuteJson(session, x => x.ObserveActorContext(actorId))
);
app.MapGet("/actors/{actorId}/navigation",
    (string actorId, SessionService session)
    => ExecuteJson(session, x => x.ObserveActorNavigation(actorId))
);
app.MapPost("/actors/{actorId}/moves/{passageId}",
    (string actorId, string passageId, SessionService session)
    => ExecuteJson(session, x => x.MoveActor(actorId, passageId))
);
app.MapGet("/actors/{actorId}/route-trace",
    (string actorId, SessionService session)
    => ExecuteText(session, x => DevTextRenderer.RenderRouteTrace(x.TraceActorRoute(actorId)))
);
app.MapGet("/actors/{actorId}/plan-route/{toLocationId}",
    (string actorId, string toLocationId, SessionService session)
    => ExecuteJson(session, x => x.PlanActorRoute(actorId, toLocationId))
);

app.MapGet("/admin/routes/{fromLocationId}/{toLocationId}",
    (string fromLocationId, string toLocationId, SessionService session)
    => ExecuteJson(session, x => x.PlanRoute(fromLocationId, toLocationId))
);

app.Run();

static IResult ExecuteText(SessionService session, Func<WorldSession, string> operation) {
    try {
        var result = session.Invoke(operation);
        return Results.Content(result, HostContentTypes.PlainText);
    }
    catch (ArgumentException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static IResult ExecuteJson<T>(SessionService session, Func<WorldSession, T> operation) {
    try {
        var result = session.Invoke(operation);
        return Results.Content(JsonSerializer.Serialize(result, TextAdv2HostJson.Options), HostContentTypes.Json);
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

static object BuildSessionStatus(GameServerHostPolicy hostPolicy, string bootstrapMode) {
    var scaffold = HostingScaffold.DescribeCurrentState();
    return new {
        service = "TextAdv2.GameServer",
        mode = HostRunningMode,
        configuration = new {
            configuredRepoDir = hostPolicy.ConfiguredRepoDir,
            resolvedRepoDir = hostPolicy.ResolvedRepoDir,
            bootstrapMode,
        },
        hostPolicy = new {
            bootstrapMode = hostPolicy.BootstrapMode,
            sessionOpenMode = hostPolicy.SessionOpenMode,
            sampleWorldResetEnabled = hostPolicy.SampleWorldResetEnabled,
            repositoryLockRetry = new {
                enabled = hostPolicy.RepositoryLockRetryEnabled,
                maxRetryCount = hostPolicy.RepositoryLockRetryCount,
            },
            notes = hostPolicy.Notes,
        },
        session = scaffold,
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

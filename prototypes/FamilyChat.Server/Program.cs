using System.Security.Claims;
using System.Text;
using Atelia.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Atelia.FamilyChat.Server;
using Microsoft.AspNetCore.Authentication;

const string CookieScheme = "FamilyChatCookie";
const string DefaultConfigPath = ".atelia/family-chat/config.json";

var builder = WebApplication.CreateBuilder(args);

string configuredConfigPath = builder.Configuration["FamilyChat:ConfigPath"] ?? DefaultConfigPath;
string resolvedConfigPath = Path.GetFullPath(configuredConfigPath, builder.Environment.ContentRootPath);
FamilyChatConfigBootstrapper.EnsureExistsOrBootstrap(resolvedConfigPath);
var config = FamilyChatConfigLoader.Load(resolvedConfigPath);

if (config.ListenUrls is { Count: > 0 }) {
    builder.WebHost.UseUrls(config.ListenUrls.ToArray());
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<IFamilyChatCompletionClientFactory, DefaultFamilyChatCompletionClientFactory>();
builder.Services.AddSingleton<IFamilyChatUserMessageNormalizer>(_ => FamilyChatUserMessageNormalizerFactory.CreateFromEnvironment());
builder.Services.AddSingleton<FamilyChatHostService>();
builder.Services.AddAuthentication(CookieScheme)
    .AddCookie(
    CookieScheme,
    options => {
        options.Cookie.Name = "family_chat_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = context => {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)) {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect("/login");
            return Task.CompletedTask;
        };
    }
);
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

app.MapGet(
    "/login",
    (HttpRequest request) => {
        bool invalidCredentials = string.Equals(request.Query["error"], "invalid", StringComparison.Ordinal);
        return Results.Content(FamilyChatHtml.RenderLoginPage(invalidCredentials), "text/html; charset=utf-8");
    }
);

app.MapPost(
    "/login",
    async (HttpContext httpContext, FamilyChatHostService hostService) => {
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        string userId = form["userId"].ToString();
        string password = form["password"].ToString();

        if (!hostService.TryGetUser(userId, out var user) || !hostService.ValidatePassword(user, password)) {
            return Results.Content(
                FamilyChatHtml.RenderLoginPage(invalidCredentials: true),
                "text/html; charset=utf-8",
                Encoding.UTF8,
                StatusCodes.Status401Unauthorized
            );
        }

        var claims = new[] {
            new Claim(FamilyChatClaimTypes.UserId, user.UserId),
            new Claim(ClaimTypes.Name, user.DisplayName),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme));

        await httpContext.SignInAsync(
            CookieScheme,
            principal,
            new AuthenticationProperties {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            }
        );
        return Results.Redirect("/");
    }
);

app.MapPost(
    "/logout",
    async (HttpContext httpContext) => {
        await httpContext.SignOutAsync(CookieScheme);
        return Results.Redirect("/login");
    }
).RequireAuthorization();

app.MapGet(
    "/",
    (ClaimsPrincipal user, FamilyChatHostService hostService) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) { return Results.Unauthorized(); }

        return Results.Content(FamilyChatHtml.RenderAppPage(configUser.DisplayName), "text/html; charset=utf-8");
    }
).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet(
    "/me",
    (ClaimsPrincipal user, FamilyChatHostService hostService) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) { return Results.Unauthorized(); }

        return Results.Ok(new FamilyChatMeDto(configUser.UserId, configUser.DisplayName));
    }
);

api.MapGet(
    "/recent-turns",
    async (ClaimsPrincipal user, FamilyChatHostService hostService, CancellationToken ct) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, ct);
        var response = hostService.BuildRecentTurnsResponse(session.Engine);
        DebugUtil.Info(
            "FamilyChat.Api",
            $"GET /api/recent-turns user={userId}, items={response.Turns.Count}, recapVisible={response.Turns.Any(static x => x.IsRecap)}, head={session.Engine.PersistedHeadAddress}"
        );
        return Results.Ok(response);
    }
);

api.MapPost(
    "/chat/turns",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        FamilyChatHostService hostService,
        IHostApplicationLifetime applicationLifetime,
        ChatStreamRequest request
    ) => {
        if (string.IsNullOrWhiteSpace(request.Message)) { return Results.BadRequest(new { error = "message must not be blank." }); }

        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        if (!session.TurnLock.Wait(0)) { return BuildTurnBusyConflict(hostService, session); }

        var liveTurn = hostService.StartTurn(session, request.Message);
        DebugUtil.Info("FamilyChat.Api", $"POST /api/chat/turns user={userId}, turnId={liveTurn.TurnId}, head={session.Engine.PersistedHeadAddress}");
        return StartAcceptedTurn(session, liveTurn, hostService, applicationLifetime);
    }
);

api.MapPost(
    "/chat/turns/pop-latest",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        FamilyChatHostService hostService
    ) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        if (!session.TurnLock.Wait(0)) { return BuildTurnBusyConflict(hostService, session); }

        var poppedTurn = hostService.PopLatestTurn(session);
        session.TurnLock.Release();
        if (poppedTurn is null) {
            DebugUtil.Warning("FamilyChat.Api", $"POST /api/chat/turns/pop-latest user={userId} returned null, head={session.Engine.PersistedHeadAddress}");
            return Results.Json(
                new StartTurnResponseDto(
                    TurnId: string.Empty,
                    Status: "idle",
                    Error: "当前没有可取出的最近一轮。"
                ),
                statusCode: StatusCodes.Status409Conflict
            );
        }

        DebugUtil.Info("FamilyChat.Api", $"POST /api/chat/turns/pop-latest user={userId} succeeded, head={session.Engine.PersistedHeadAddress}");
        return Results.Ok(new PopLatestTurnResponseDto(poppedTurn));
    }
);

api.MapGet(
    "/chat/turns/current",
    async (ClaimsPrincipal user, FamilyChatHostService hostService, CancellationToken ct) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, ct);
        var currentTurn = hostService.BuildCurrentTurn(session);
        DebugUtil.Info("FamilyChat.Api", $"GET /api/chat/turns/current user={userId}, status={currentTurn.Status}, turnId={currentTurn.TurnId ?? "<none>"}, head={session.Engine.PersistedHeadAddress}");
        return Results.Ok(currentTurn);
    }
);

api.MapGet(
    "/chat/turns/{turnId}/events",
    async (HttpContext httpContext, ClaimsPrincipal user, FamilyChatHostService hostService, string turnId) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);
        var liveTurn = hostService.FindTurn(session, turnId);
        if (liveTurn is null) { return Results.NotFound(new { error = "turn not found." }); }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-store";

        using var subscription = liveTurn.Subscribe();

        try {
            foreach (var replayEvent in subscription.ReplayEvents) {
                await FamilyChatSseWriter.WriteEventAsync(
                    httpContext.Response,
                    replayEvent.Type,
                    replayEvent.Payload,
                    httpContext.RequestAborted
                );
            }

            await foreach (var streamEvent in subscription.Reader.ReadAllAsync(httpContext.RequestAborted)) {
                await FamilyChatSseWriter.WriteEventAsync(
                    httpContext.Response,
                    streamEvent.Type,
                    streamEvent.Payload,
                    httpContext.RequestAborted
                );
            }
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested) {
            return Results.Empty;
        }

        return Results.Empty;
    }
);

app.Run();

static IResult BuildTurnBusyConflict(FamilyChatHostService hostService, UserSessionHost session) {
    var runningTurn = hostService.BuildCurrentTurn(session);
    DebugUtil.Warning(
        "FamilyChat.Api",
        $"Turn busy conflict: user={session.User.UserId}, runningTurn={runningTurn.TurnId ?? "<none>"}, head={session.Engine.PersistedHeadAddress}"
    );
    return Results.Json(
        new StartTurnResponseDto(
            TurnId: runningTurn.TurnId ?? string.Empty,
            Status: "running",
            Error: "该账号当前正在生成，请稍后。"
        ),
        statusCode: StatusCodes.Status409Conflict
    );
}

static IResult StartAcceptedTurn(
    UserSessionHost session,
    FamilyChatLiveTurn liveTurn,
    FamilyChatHostService hostService,
    IHostApplicationLifetime applicationLifetime
) {
    var runTask = Task.Run(
        async () => {
            DebugUtil.Info(
                "FamilyChat.Api",
                $"StartAcceptedTurn background start: user={session.User.UserId}, turnId={liveTurn.TurnId}, head={session.Engine.PersistedHeadAddress}"
            );
            try {
                await hostService.RunTurnAsync(session, liveTurn, applicationLifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested) {
                DebugUtil.Warning("FamilyChat.Api", $"Turn cancelled by shutdown: user={session.User.UserId}, turnId={liveTurn.TurnId}");
                liveTurn.Publish(
                    new StreamEventDto("error", new { message = "服务器正在关闭，当前生成已终止。" }),
                    status: "failed"
                );
            }
            catch (FamilyChatTurnException ex) {
                DebugUtil.Warning("FamilyChat.Api", $"Turn failed with FamilyChatTurnException: user={session.User.UserId}, turnId={liveTurn.TurnId}, reason={ex.FailureReason}");
                liveTurn.Publish(
                    new StreamEventDto("error", new { message = ex.Message, failureReason = ex.FailureReason }),
                    status: "failed"
                );
            }
            catch (Exception ex) {
                DebugUtil.Error("FamilyChat.Api", $"Turn failed with exception: user={session.User.UserId}, turnId={liveTurn.TurnId}", ex);
                liveTurn.Publish(
                    new StreamEventDto("error", new { message = ex.Message }),
                    status: "failed"
                );
            }
            finally {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.Complete();
                session.TurnLock.Release();
                DebugUtil.Info(
                    "FamilyChat.Api",
                    $"StartAcceptedTurn background finish: user={session.User.UserId}, turnId={liveTurn.TurnId}, head={session.Engine.PersistedHeadAddress}, status={liveTurn.Status}"
                );
            }
        },
        CancellationToken.None
    );
    liveTurn.RunTask = runTask;

    return Results.Json(
        new StartTurnResponseDto(liveTurn.TurnId, "running"),
        statusCode: StatusCodes.Status202Accepted
    );
}

public partial class Program;

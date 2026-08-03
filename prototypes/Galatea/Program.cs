using System.Security.Claims;
using System.Text;
using Atelia.Completion;
using Atelia.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Atelia.Galatea.Server;
using Microsoft.AspNetCore.Authentication;
using Atelia.SessionJournal;

const string CookieScheme = "GalateaCookie";
const string DefaultConfigPath = ".atelia/galatea/config.json";

var builder = WebApplication.CreateBuilder(args);

string configuredConfigPath = builder.Configuration["Galatea:ConfigPath"] ?? DefaultConfigPath;
string resolvedConfigPath = Path.GetFullPath(configuredConfigPath, builder.Environment.ContentRootPath);
GalateaConfigBootstrapper.EnsureExistsOrBootstrap(resolvedConfigPath);
var config = GalateaConfigLoader.Load(resolvedConfigPath);
string assetVersion = GalateaStaticAssetVersion.BuildToken(builder.Environment.ContentRootPath);

if (config.ListenUrls is { Count: > 0 }) {
    builder.WebHost.UseUrls(config.ListenUrls.ToArray());
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ICompletionClientFactory, DefaultCompletionClientFactory>();
builder.Services.AddSingleton(_ => new CompletionConnectionsFileConfig(config.Connections, config.DefaultConnectionId));
builder.Services.AddSingleton<CompletionConnectionRegistry>();
builder.Services.AddSingleton<IGalateaUserMessageNormalizer>(_ => GalateaUserMessageNormalizerFactory.CreateFromEnvironment());
builder.Services.AddSingleton<GalateaHostService>();
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

app.Use(async (context, next) => {
    try {
        await next(context);
    }
    catch (GalateaSessionUnavailableException exception) when (
        context.Request.Path.StartsWithSegments(
            "/api",
            StringComparison.Ordinal
        )
    ) {
        context.Response.StatusCode =
            StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new {
            code = exception.Code,
            error = exception.Message
        });
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) => {
    if (config.MaintenanceMode
        && HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.StartsWithSegments(
            "/api/chat/turns",
            StringComparison.Ordinal
        )) {
        context.Response.StatusCode =
            StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new {
            code = "maintenance-mode",
            error = "Galatea当前处于维护模式；会话写操作已禁用。"
        });
        return;
    }
    await next(context);
});
app.UseStaticFiles();

app.MapGet(
    "/login",
    (HttpRequest request) => {
        bool invalidCredentials = string.Equals(request.Query["error"], "invalid", StringComparison.Ordinal);
        return Results.Content(GalateaHtml.RenderLoginPage(invalidCredentials, assetVersion), "text/html; charset=utf-8");
    }
);

app.MapPost(
    "/login",
    async (HttpContext httpContext, GalateaHostService hostService) => {
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        string userId = form["userId"].ToString();
        string password = form["password"].ToString();

        if (!hostService.TryGetUser(userId, out var user) || !hostService.ValidatePassword(user, password)) {
            return Results.Content(
                GalateaHtml.RenderLoginPage(invalidCredentials: true, assetVersion),
                "text/html; charset=utf-8",
                Encoding.UTF8,
                StatusCodes.Status401Unauthorized
            );
        }

        var claims = new[] {
            new Claim(GalateaClaimTypes.UserId, user.UserId),
            new Claim(ClaimTypes.Name, user.UserId),
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
    (ClaimsPrincipal user, GalateaHostService hostService, CompletionConnectionRegistry connections) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) { return Results.Unauthorized(); }

        return Results.Content(
            GalateaHtml.RenderAppPage(
                configUser,
                connections,
                config.MaintenanceMode,
                assetVersion
            ),
            "text/html; charset=utf-8"
        );
    }
).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet(
    "/me",
    (ClaimsPrincipal user, GalateaHostService hostService) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) { return Results.Unauthorized(); }

        return Results.Ok(new GalateaMeDto(
            configUser.UserId,
            config.MaintenanceMode
        ));
    }
);

api.MapGet(
    "/recent-turns",
    async (ClaimsPrincipal user, GalateaHostService hostService, CancellationToken ct) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, ct);
        var response = await hostService.GetRecentTurnsAsync(session, ct);
        DebugUtil.Info(
            "Galatea.Api",
            $"GET /api/recent-turns user={userId}, items={response.Turns.Count}, rewindEligible={response.RewindLatestToken is not null}"
        );
        return Results.Ok(response);
    }
);

api.MapPost(
    "/chat/turns",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService,
        CompletionConnectionRegistry connections,
        IHostApplicationLifetime applicationLifetime,
        ChatStreamRequest request
    ) => {
        if (string.IsNullOrWhiteSpace(request.Message)) { return Results.BadRequest(new { error = "message must not be blank." }); }

        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        if (!session.TurnLock.Wait(0)) { return BuildTurnBusyConflict(hostService, session); }
        GalateaLiveTurn? liveTurn = null;
        bool writerOwnershipTransferred = false;
        try {
            SessionRuntimeRecoveryRequirements recovery =
                session.Engine.InspectRuntimeRecoveryRequirements(
                    httpContext.RequestAborted
                );
            if (recovery.Phase == SessionExecutionPhase.Empty) {
                return RecoveryConflict(
                    recovery,
                    "session-unprovisioned",
                    "会话仓库尚未完成初始化。"
                );
            }
            bool acceptsFreshMessage = recovery switch {
                SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
                    Phase: SessionExecutionPhase.Idle
                } => true,
                SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned => true,
                SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
                    Phase: SessionExecutionPhase.Empty
                } => false,
                SessionRuntimeRecoveryRequirements.NewRequestRequired =>
                    false,
                SessionRuntimeRecoveryRequirements
                    .FrozenCompletionRequired => false,
                SessionRuntimeRecoveryRequirements
                    .ToolContinuationRequired => false,
                _ => throw new InvalidDataException(
                    "Unknown runtime recovery requirement."
                )
            };
            if (!acceptsFreshMessage) {
                return RecoveryConflict(
                    recovery,
                    "recovery-required",
                    "当前会话存在待恢复的持久化轮次；新消息未被接收。"
                );
            }
            if (!TryResolveRequestedConnection(
                    connections,
                    request.ConnectionId,
                    out CompletionConnectionConfig connection
                )) {
                return Results.BadRequest(new {
                    code = "unknown-connection",
                    error = $"Unknown completion connection '{request.ConnectionId}'."
                });
            }
            liveTurn = hostService.StartTurn(
                session,
                request.Message,
                new GalateaTurnOptions(connection.Id)
            );
            DebugUtil.Info("Galatea.Api", $"POST /api/chat/turns user={userId}, turnId={liveTurn.TurnId}, connectionId={connection.Id}, head={session.Engine.ReadCurrentHead()}");
            IResult result = StartAcceptedTurn(
                session,
                liveTurn,
                hostService,
                applicationLifetime
            );
            writerOwnershipTransferred = true;
            return result;
        }
        finally {
            if (!writerOwnershipTransferred) {
                try {
                    if (liveTurn is not null) {
                        hostService.FinishTurn(session, liveTurn);
                        liveTurn.Complete();
                    }
                    hostService.RefreshRecentTurnsBestEffort(session);
                }
                finally {
                    session.TurnLock.Release();
                }
            }
        }
    }
);

api.MapPost(
    "/chat/turns/resume",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService,
        CompletionConnectionRegistry connections,
        IHostApplicationLifetime applicationLifetime,
        ResumeTurnRequest request
    ) => {
        string userId = user.FindFirstValue(
            GalateaClaimTypes.UserId
        ) ?? throw new InvalidOperationException(
            "Authenticated principal is missing user id."
        );
        var session = await hostService.GetSessionAsync(
            userId,
            httpContext.RequestAborted
        );
        if (!session.TurnLock.Wait(0)) {
            return BuildTurnBusyConflict(hostService, session);
        }

        GalateaLiveTurn? liveTurn = null;
        bool writerOwnershipTransferred = false;
        try {
            SessionRuntimeRecoveryRequirements recovery =
                session.Engine.InspectRuntimeRecoveryRequirements(
                    httpContext.RequestAborted
                );
            if (!EventAddressTextCodec.TryParse(
                    request.ExpectedHead,
                    out var expectedHead
                )) {
                return Results.BadRequest(new {
                    code = "invalid-expected-head",
                    error = "expectedHead格式无效。"
                });
            }
            if (recovery.CapturedHead != expectedHead) {
                return RecoveryConflict(
                    recovery,
                    "stale-session-head",
                    "会话边界已变化，请刷新后重新确认恢复。"
                );
            }
            if (recovery is SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned) {
                return RecoveryConflict(
                    recovery,
                    "failed-turn-must-be-abandoned",
                    "失败轮次必须通过新消息入口在精确边界安全放弃。"
                );
            }
            if (recovery is SessionRuntimeRecoveryRequirements
                    .NoRuntimeRequired) {
                return RecoveryConflict(
                    recovery,
                    "no-recovery-required",
                    "当前会话没有待恢复轮次。"
                );
            }
            if (recovery is SessionRuntimeRecoveryRequirements
                    .ToolContinuationRequired
                || recovery is SessionRuntimeRecoveryRequirements
                    .NewRequestRequired {
                        HeadKind: SessionEventKind.ToolResultObserved
                    }) {
                return RecoveryConflict(
                    recovery,
                    "tool-recovery-unsupported",
                    "Galatea G1尚不支持工具阶段恢复。"
                );
            }
            if (recovery is SessionRuntimeRecoveryRequirements
                    .FrozenCompletionRequired {
                        DispatchState:
                            SessionDurableDispatchState
                                .StartedOutcomeUncertain
                    }
                && !request.RestartUncertainCompletion) {
                return RecoveryConflict(
                    recovery,
                    "uncertain-completion-restart-required",
                    "上次模型调用结果不确定；必须明确授权重新调用。"
                );
            }

            string connectionId;
            if (recovery is SessionRuntimeRecoveryRequirements
                    .NewRequestRequired) {
                if (!TryResolveRequestedConnection(
                        connections,
                        request.ConnectionId,
                        out CompletionConnectionConfig connection
                    )) {
                    return Results.BadRequest(new {
                        code = "unknown-connection",
                        error = $"Unknown completion connection '{request.ConnectionId}'."
                    });
                }
                connectionId = connection.Id;
            }
            else if (recovery is SessionRuntimeRecoveryRequirements
                         .FrozenCompletionRequired frozen) {
                connectionId = frozen.CompletionTarget.ConnectionId;
            }
            else {
                throw new InvalidDataException(
                    "Unknown supported recovery requirement."
                );
            }
            liveTurn = hostService.StartRecovery(
                session,
                new GalateaTurnOptions(
                    connectionId,
                    GalateaTurnMode.Resume,
                    request.RestartUncertainCompletion,
                    expectedHead
                )
            );
            IResult result = StartAcceptedTurn(
                session,
                liveTurn,
                hostService,
                applicationLifetime
            );
            writerOwnershipTransferred = true;
            return result;
        }
        finally {
            if (!writerOwnershipTransferred) {
                try {
                    if (liveTurn is not null) {
                        hostService.FinishTurn(session, liveTurn);
                        liveTurn.Complete();
                    }
                    hostService.RefreshRecentTurnsBestEffort(session);
                }
                finally {
                    session.TurnLock.Release();
                }
            }
        }
    }
);

api.MapPost(
    "/chat/turns/pop-latest",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService,
        PopLatestTurnRequestDto request
    ) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        if (!session.TurnLock.Wait(0)) { return BuildTurnBusyConflict(hostService, session); }
        try {
            if (!EventAddressTextCodec.TryParse(
                    request.RewindLatestToken,
                    out var expectedHead
                )) {
                return Results.BadRequest(new {
                    code = "invalid-rewind-token",
                    error = "rewindLatestToken格式无效。"
                });
            }
            var poppedTurn = hostService.PopLatestTurn(
                session,
                expectedHead
            );
            if (poppedTurn is null) {
                DebugUtil.Warning("Galatea.Api", $"POST /api/chat/turns/pop-latest user={userId} returned null, head={session.Engine.ReadCurrentHead()}");
                return Results.Json(
                    new StartTurnResponseDto(
                        TurnId: string.Empty,
                        Status: "idle",
                        Error: "当前没有可取出的最近一轮。"
                    ),
                    statusCode: StatusCodes.Status409Conflict
                );
            }

            DebugUtil.Info("Galatea.Api", $"POST /api/chat/turns/pop-latest user={userId} succeeded, head={session.Engine.ReadCurrentHead()}");
            return Results.Ok(poppedTurn);
        }
        finally {
            session.TurnLock.Release();
        }
    }
);

api.MapGet(
    "/chat/turns/current",
    async (ClaimsPrincipal user, GalateaHostService hostService, CancellationToken ct) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, ct);
        var currentTurn = await hostService.GetCurrentTurnAsync(
            session,
            ct
        );
        DebugUtil.Info("Galatea.Api", $"GET /api/chat/turns/current user={userId}, status={currentTurn.Status}, turnId={currentTurn.TurnId ?? "<none>"}");
        return Results.Ok(currentTurn);
    }
);

api.MapPost(
    "/chat/turns/{turnId}/stop",
    async (HttpContext httpContext, ClaimsPrincipal user, GalateaHostService hostService, string turnId) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);
        if (!hostService.RequestStop(session, turnId)) { return Results.NotFound(new { error = "turn not found or already finished." }); }

        DebugUtil.Warning("Galatea.Api", $"POST /api/chat/turns/{turnId}/stop user={userId}");
        return Results.Ok(new { status = "stopping", turnId });
    }
);

api.MapGet(
    "/chat/turns/{turnId}/events",
    async (HttpContext httpContext, ClaimsPrincipal user, GalateaHostService hostService, string turnId) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
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
                await GalateaSseWriter.WriteEventAsync(
                    httpContext.Response,
                    replayEvent.Type,
                    replayEvent.Payload,
                    httpContext.RequestAborted
                );
            }

            await foreach (var streamEvent in subscription.Reader.ReadAllAsync(httpContext.RequestAborted)) {
                await GalateaSseWriter.WriteEventAsync(
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

static IResult BuildTurnBusyConflict(GalateaHostService hostService, UserSessionHost session) {
    var runningTurn = hostService.BuildLiveCurrentTurn(session);
    DebugUtil.Warning(
        "Galatea.Api",
        $"Turn busy conflict: user={session.User.UserId}, runningTurn={runningTurn.TurnId ?? "<none>"}"
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
    GalateaLiveTurn liveTurn,
    GalateaHostService hostService,
    IHostApplicationLifetime applicationLifetime
) {
    var runTask = Task.Run(
        async () => {
            DebugUtil.Info(
                "Galatea.Api",
                $"StartAcceptedTurn background start: user={session.User.UserId}, turnId={liveTurn.TurnId}, head={session.Engine.ReadCurrentHead()}"
            );
            try {
                await hostService.RunTurnAsync(session, liveTurn, applicationLifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested) {
                DebugUtil.Warning("Galatea.Api", $"Turn cancelled by shutdown: user={session.User.UserId}, turnId={liveTurn.TurnId}");
                liveTurn.Publish(
                    new StreamEventDto("error", new { message = "服务器正在关闭，当前生成已终止。" }),
                    status: "failed"
                );
            }
            catch (GalateaTurnException ex) {
                DebugUtil.Warning("Galatea.Api", $"Turn failed with GalateaTurnException: user={session.User.UserId}, turnId={liveTurn.TurnId}, reason={ex.FailureReason}");
                liveTurn.Publish(
                    new StreamEventDto("error", new { message = ex.Message, failureReason = ex.FailureReason }),
                    status: "failed"
                );
            }
            catch (Exception ex) when (
                GalateaExceptionClassifier.IsNonFatal(ex)
            ) {
                DebugUtil.Error("Galatea.Api", $"Turn failed with exception: user={session.User.UserId}, turnId={liveTurn.TurnId}", ex);
                liveTurn.Publish(
                    new StreamEventDto("error", new { message = ex.Message }),
                    status: "failed"
                );
            }
            finally {
                try {
                    hostService.RefreshRecentTurnsBestEffort(session);
                    hostService.FinishTurn(session, liveTurn);
                    liveTurn.Complete();
                    DebugUtil.Info(
                        "Galatea.Api",
                        $"StartAcceptedTurn background finish: user={session.User.UserId}, turnId={liveTurn.TurnId}, status={liveTurn.Status}"
                    );
                }
                finally {
                    session.TurnLock.Release();
                }
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

static bool TryResolveRequestedConnection(
    CompletionConnectionRegistry connections,
    string? requestedConnectionId,
    out CompletionConnectionConfig connection
) {
    if (string.IsNullOrWhiteSpace(requestedConnectionId)) {
        connection = connections.Resolve(null);
        return true;
    }
    return connections.TryGet(
        requestedConnectionId,
        out connection!
    );
}

static IResult RecoveryConflict(
    SessionRuntimeRecoveryRequirements recovery,
    string code,
    string error
) => Results.Json(
    new {
        code,
        error,
        phase = recovery.Phase.ToString(),
        head = EventAddressTextCodec.FormatNullable(
            recovery.CapturedHead
        )
    },
    statusCode: StatusCodes.Status409Conflict
);

public partial class Program;

using System.Security.Claims;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Diagnostics;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.Mailbox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Atelia.SessionJournal;

const string CookieScheme = "GalateaCookie";
const string DefaultConfigPath = ".atelia/galatea/config.json";

if (GalateaDelegationStoreUpgrade.IsInvocation(args)) {
    Environment.ExitCode = GalateaDelegationStoreUpgrade.Run(args, Console.Out, Console.Error);
    return;
}

if (GalateaDelegationOperatorRecovery.IsOperatorInvocation(args)) {
    Environment.ExitCode = GalateaDelegationOperatorRecovery.Run(
        args,
        Console.Out,
        Console.Error
    );
    return;
}

var builder = WebApplication.CreateBuilder(args);

string configuredConfigPath = builder.Configuration["Galatea:ConfigPath"] ?? DefaultConfigPath;
string resolvedConfigPath = Path.GetFullPath(configuredConfigPath, builder.Environment.ContentRootPath);
GalateaConfigBootstrapper.EnsureExistsOrBootstrap(resolvedConfigPath);
var config = GalateaConfigLoader.Load(resolvedConfigPath);
string assetVersion = GalateaStaticAssetVersion.BuildToken(builder.Environment.ContentRootPath);

GalateaCodexSubscriptionComposition.ConfigureWebHost(
    builder.WebHost,
    config
);

builder.Services.AddSingleton(config);
// Resolve through DI so isolated hosts can replace the external boundary
// without constructing the production credential composition first.
builder.Services.AddSingleton<ICompletionClientFactory>(_ =>
    GalateaCodexSubscriptionComposition.CreateFactory(config));
string? dataProtectionKeysDirectory = builder.Configuration["Galatea:DataProtectionKeysDirectory"];
if (dataProtectionKeysDirectory is not null) {
    if (string.IsNullOrWhiteSpace(dataProtectionKeysDirectory)
        || !Path.IsPathFullyQualified(dataProtectionKeysDirectory)) {
        throw new InvalidOperationException(
            "Galatea:DataProtectionKeysDirectory must be an absolute directory path.");
    }
    builder.Services.AddDataProtection().PersistKeysToFileSystem(
        new DirectoryInfo(dataProtectionKeysDirectory));
}
builder.Services.AddSingleton<IGalateaUserMessageNormalizerFactory,
    GalateaUserMessageNormalizerFactory>();
builder.Services.AddSingleton(static services => new GalateaHostService(
    services.GetRequiredService<GalateaConfig>(),
    services.GetRequiredService<ICompletionClientFactory>(),
    services.GetRequiredService<IGalateaUserMessageNormalizerFactory>()
));
builder.Services.AddSingleton<GalateaAcceptedTurnRunner>();
builder.Services.AddSingleton<GalateaAutomaticTurnCoordinator>();
builder.Services.AddHostedService<GalateaServerAgentHostedService>();
builder.Services.ConfigureHttpJsonOptions(
    options => GalateaHttpV1.ConfigureJson(options.SerializerOptions)
);
builder.Services.Configure<RouteHandlerOptions>(
    options => options.ThrowOnBadRequest = true
);
builder.Services.AddAuthentication(CookieScheme)
    .AddCookie(
    CookieScheme,
    options => {
        options.Cookie.Name = "family_chat_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = async context => {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)) {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new ApiErrorDto(
                        "authentication-required",
                        "Authentication is required."
                    )
                );
                return;
            }

            context.Response.Redirect("/login");
        };
    }
);
builder.Services.AddAuthorization();

var app = builder.Build();
GalateaHostService eagerHost = app.Services
    .GetRequiredService<GalateaHostService>();
app.Lifetime.ApplicationStopping.Register(eagerHost.BeginShutdown);

app.UseRouting();
app.Use(async (context, next) => {
    try {
        await next(context);
    }
    // Cancellation is never rewritten as a protocol 500. This includes
    // RequestAborted and application-owned cancellation tokens; if headers
    // already started, the same rethrow also guarantees no JSON is appended.
    catch (OperationCanceledException) {
        throw;
    }
    catch (Exception exception) when (
        context.Request.Path.StartsWithSegments("/api/v1")
        && GalateaExceptionClassifier.IsNonFatal(exception)
    ) {
        if (context.Response.HasStarted) {
            throw;
        }
        (int statusCode, ApiErrorDto error) = MapApiException(exception);
        if (statusCode >= StatusCodes.Status500InternalServerError) {
            DebugUtil.Error(
                "Galatea.Api",
                $"API request failed: method={context.Request.Method}, path={context.Request.Path}, status={statusCode}, code={error.Code}",
                exception
            );
        }
        context.Response.Clear();
        await Results.Json(error, statusCode: statusCode)
            .ExecuteAsync(context);
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) => {
    if (config.MaintenanceMode
        && GalateaHttpV1.IsMaintenanceWrite(context)) {
        await Results.Json(
                new ApiErrorDto(
                    "maintenance-mode",
                    "Galatea当前处于维护模式；会话写操作已禁用。"
                ),
                statusCode: StatusCodes.Status503ServiceUnavailable
            )
            .ExecuteAsync(context);
        return;
    }
    await next(context);
});
app.Use(async (context, next) => {
    if (!GalateaHttpV1.HasJsonBody(context)) {
        await next(context);
        return;
    }
    if (context.Request.ContentLength == 0
        || context.Features
            .Get<Microsoft.AspNetCore.Http.Features
                .IHttpRequestBodyDetectionFeature>()
            ?.CanHaveBody == false) {
        await Results.Json(
                new ApiErrorDto(
                    "invalid-request",
                    "Request body must contain one JSON object."
                ),
                statusCode: StatusCodes.Status400BadRequest
            )
            .ExecuteAsync(context);
        return;
    }
    if (!GalateaHttpV1.IsExactJsonContentType(
            context.Request.ContentType
        )
        || context.Request.Headers.ContentEncoding.Count != 0) {
        await Results.Json(
                new ApiErrorDto(
                    "unsupported-media-type",
                    "Content-Type must be application/json with optional UTF-8 charset."
                ),
                statusCode: StatusCodes.Status415UnsupportedMediaType
            )
            .ExecuteAsync(context);
        return;
    }
    if (context.Request.ContentLength
        > GalateaHttpV1.MaximumRequestBodyBytes) {
        await Results.Json(
                new ApiErrorDto(
                    "request-too-large",
                    "Request body exceeds the 1 MiB limit."
                ),
                statusCode: StatusCodes.Status413PayloadTooLarge
            )
            .ExecuteAsync(context);
        return;
    }

    Stream originalBody = context.Request.Body;
    context.Request.Body = GalateaHttpV1.CreateBoundedBodyStream(
        originalBody
    );
    try {
        await next(context);
    }
    finally {
        context.Request.Body = originalBody;
    }
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
    (ClaimsPrincipal user, GalateaHostService hostService) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) { return Results.Unauthorized(); }

        return Results.Content(
            GalateaHtml.RenderAppPage(
                configUser,
                hostService.Connections,
                config.MaintenanceMode,
                assetVersion
            ),
            "text/html; charset=utf-8"
        );
    }
).RequireAuthorization();

var api = app.MapGroup("/api/v1").RequireAuthorization();

api.MapGet(
    "/me",
    (ClaimsPrincipal user, GalateaHostService hostService) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) {
            return Results.Json(
                new ApiErrorDto(
                    "authentication-user-unknown",
                    "The authenticated user is no longer configured."
                ),
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

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
            $"GET /api/v1/recent-turns user={userId}, items={response.Turns.Count}, rewindEligible={response.RewindLatestToken is not null}"
        );
        return Results.Ok(response);
    }
);

api.MapGet(
    "/recap-cadence-progress",
    async (
        ClaimsPrincipal user,
        GalateaHostService hostService,
        CancellationToken ct
    ) => {
        string userId = user.FindFirstValue(
            GalateaClaimTypes.UserId
        ) ?? throw new InvalidOperationException(
            "Authenticated principal is missing user id."
        );
        UserSessionHost session = await hostService.GetSessionAsync(
            userId,
            ct
        );
        RecapCadenceProgressSnapshotDto response = await hostService
            .GetRecapCadenceProgressAsync(session, ct);
        DebugUtil.Info(
            "Galatea.Api",
            "GET /api/v1/recap-cadence-progress "
                + $"user={userId}, freshness={response.Freshness}, "
                + $"state={response.State}, "
                + $"head={response.ObservedRawHead ?? "<none>"}"
        );
        return Results.Ok(response);
    }
);

api.MapGet(
    "/mailbox/status",
    (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService
    ) => {
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException(
                "Authenticated principal is missing user id."
            );
        httpContext.Response.Headers.CacheControl = "no-store";
        GalateaMailboxStatusDto response = hostService.ReadMailboxStatus(
            userId
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
        IHostApplicationLifetime applicationLifetime,
        GalateaAcceptedTurnRunner turnRunner
    ) => {
        ChatStreamRequest request = await GalateaHttpV1
            .ReadJsonBodyAsync<ChatStreamRequest>(httpContext);
        string? messageError = GalateaHttpV1.ValidateMessage(
            request.Message
        );
        if (messageError is not null) {
            return Results.BadRequest(
                new ApiErrorDto("invalid-message", messageError)
            );
        }
        string? connectionError = GalateaHttpV1.ValidateConnectionId(
            request.ConnectionId
        );
        if (connectionError is not null) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-connection-id",
                connectionError
            ));
        }

        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        if (!session.TurnLock.Wait(0)) { return BuildTurnBusyConflict(hostService, session); }
        GalateaLiveTurn? liveTurn = null;
        bool writerOwnershipTransferred = false;
        try {
            hostService.RequireRunning();
            await hostService.ReconcileDurableAdmissionAsync(
                session,
                httpContext.RequestAborted
            );
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
            if (!hostService.TryGetConnection(
                    session.User,
                    request.ConnectionId,
                    out CompletionConnectionConfig connection
                )) {
                return Results.BadRequest(new ApiErrorDto(
                    "unknown-connection",
                    $"Unknown completion connection '{request.ConnectionId}'."
                ));
            }
            hostService.RequireFreshTurnTargetAligned(session);
            string effectiveMessage = await hostService
                .NormalizeUserMessageAtAdmissionAsync(
                    request.Message,
                    httpContext.RequestAborted
                );
            await hostService.PrepareFreshTurnAdmissionAsync(
                session,
                recovery,
                httpContext.RequestAborted
            );
            liveTurn = hostService.StartTurn(
                session,
                effectiveMessage,
                new GalateaTurnOptions(connection.Id)
            );
            DebugUtil.Info("Galatea.Api", $"POST /api/v1/chat/turns user={userId}, turnId={liveTurn.TurnId}, connectionId={connection.Id}, head={session.Engine.ReadCurrentHead()}");
            IResult result = BuildAcceptedTurnResult(liveTurn);
            _ = turnRunner.Start(session, liveTurn);
            writerOwnershipTransferred = true;
            return result;
        }
        catch (Exception original) when (
            liveTurn is not null && !writerOwnershipTransferred) {
            try {
                await hostService.ReconcileDurableAdmissionAsync(
                    session,
                    CancellationToken.None
                );
            }
            catch (Exception cleanup) when (
                GalateaExceptionClassifier.IsNonFatal(cleanup)) {
                if (!GalateaExceptionClassifier.IsNonFatal(original)) {
                    ExceptionDispatchInfo.Capture(original).Throw();
                }
                throw new AggregateException(
                    "Fresh-turn acceptance and durable cutoff cleanup both failed.",
                    original,
                    cleanup
                );
            }
            throw;
        }
        finally {
            if (!writerOwnershipTransferred) {
                try {
                    if (liveTurn is not null) {
                        hostService.FinishTurn(session, liveTurn);
                        if (string.Equals(
                                liveTurn.Status,
                                "running",
                                StringComparison.Ordinal)) {
                            liveTurn.PublishError(
                                GalateaSseErrorCode.InternalFailure
                            );
                        }
                        liveTurn.Complete();
                    }
                    await hostService.RefreshRecentTurnsBestEffortAsync(
                        session,
                        applicationLifetime.ApplicationStopping
                    );
                }
                finally {
                    session.TurnLock.Release();
                }
            }
        }
    }
).WithMetadata(
    GalateaHttpV1.JsonBody,
    GalateaHttpV1.MaintenanceWrite
);

api.MapPost(
    "/chat/turns/resume",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService,
        IHostApplicationLifetime applicationLifetime,
        GalateaAcceptedTurnRunner turnRunner
    ) => {
        ResumeTurnRequest request = await GalateaHttpV1
            .ReadJsonBodyAsync<ResumeTurnRequest>(httpContext);
        if (!GalateaHttpV1.TryParseCanonicalEventAddress(
                request.ExpectedHead,
                out var expectedHead
            )) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-expected-head",
                "expectedHead格式无效。"
            ));
        }
        string? connectionError = GalateaHttpV1.ValidateConnectionId(
            request.ConnectionId
        );
        if (connectionError is not null) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-connection-id",
                connectionError
            ));
        }
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
            hostService.RequireRunning();
            await hostService.PrepareRecoveryAdmissionAsync(
                session,
                httpContext.RequestAborted
            );
            SessionRuntimeRecoveryRequirements recovery =
                session.Engine.InspectRuntimeRecoveryRequirements(
                    httpContext.RequestAborted
                );
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
                if (!hostService.TryGetConnection(
                        session.User,
                        request.ConnectionId,
                        out CompletionConnectionConfig connection
                    )) {
                    return Results.BadRequest(new ApiErrorDto(
                        "unknown-connection",
                        $"Unknown completion connection '{request.ConnectionId}'."
                    ));
                }
                connectionId = connection.Id;
            }
            else if (recovery is SessionRuntimeRecoveryRequirements
                         .ToolContinuationRequired) {
                // Do not inspect current selection here. The formal
                // composition must validate the frozen tool identity first,
                // then apply Galatea's current-selection allowlist without
                // constructing a client, and only later open Online/client.
                connectionId = request.ConnectionId
                    ?? session.User.DefaultConnectionId;
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
            IResult result = BuildAcceptedTurnResult(liveTurn);
            _ = turnRunner.Start(session, liveTurn);
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
                    await hostService.RefreshRecentTurnsBestEffortAsync(
                        session,
                        applicationLifetime.ApplicationStopping
                    );
                }
                finally {
                    session.TurnLock.Release();
                }
            }
        }
    }
).WithMetadata(
    GalateaHttpV1.JsonBody,
    GalateaHttpV1.MaintenanceWrite
);

api.MapPost(
    "/mailbox/ready-turn",
    async (HttpContext httpContext, ClaimsPrincipal user, GalateaAutomaticTurnCoordinator coordinator) => {
        _ = await GalateaHttpV1.ReadJsonBodyAsync<ReadyReplyTurnRequest>(httpContext);
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        GalateaAutomaticTurnResult result = await coordinator.TryPulseAsync(userId, httpContext.RequestAborted);
        return result switch {
            GalateaAutomaticTurnResult.Started started => BuildAcceptedTurnResult(started.Turn, started.Origin),
            GalateaAutomaticTurnResult.Status status => Results.Ok(new LoopPulseStatusDto(
                status.Value.State,
                status.Value.NextActivationAtUnixTimeMilliseconds,
                status.Value.LastActivationAtUnixTimeMilliseconds,
                status.Value.Code
            )),
            GalateaAutomaticTurnResult.Busy busy => Results.Json(
                new TurnBusyErrorDto("turn-busy", "该账号当前正在生成，请稍后。", busy.TurnId),
                statusCode: StatusCodes.Status409Conflict
            ),
            GalateaAutomaticTurnResult.Blocked blocked => Results.Json(
                new ApiErrorDto(blocked.Code, blocked.Message),
                statusCode: StatusCodes.Status409Conflict
            ),
            _ => throw new InvalidOperationException("Unknown automatic admission result.")
        };
    }
).WithMetadata(GalateaHttpV1.JsonBody, GalateaHttpV1.MaintenanceWrite);

api.MapGet(
    "/agent/status",
    (HttpContext httpContext, ClaimsPrincipal user, GalateaAutomaticTurnCoordinator coordinator) => {
        httpContext.Response.Headers.CacheControl = "no-store";
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        return Results.Ok(coordinator.ReadStatus(userId));
    }
);

api.MapPost(
    "/agent/retry-admission",
    async (HttpContext httpContext, ClaimsPrincipal user, GalateaAutomaticTurnCoordinator coordinator) => {
        _ = await GalateaHttpV1.ReadJsonBodyAsync<RetryAdmissionRequest>(httpContext);
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        GalateaAutomaticTurnResult result = await coordinator.RetryAdmissionAsync(userId, httpContext.RequestAborted);
        return result switch {
            GalateaAutomaticTurnResult.Status status => Results.Ok(status.Value),
            GalateaAutomaticTurnResult.Busy busy => Results.Json(
                new TurnBusyErrorDto("turn-busy", "该账号正在处理其他请求，请稍后重试。", busy.TurnId),
                statusCode: StatusCodes.Status409Conflict
            ),
            GalateaAutomaticTurnResult.Blocked blocked => Results.Json(
                new ApiErrorDto(blocked.Code, blocked.Message),
                statusCode: StatusCodes.Status409Conflict
            ),
            _ => throw new InvalidOperationException("Admission retry must not create a turn.")
        };
    }
).WithMetadata(GalateaHttpV1.JsonBody, GalateaHttpV1.MaintenanceWrite);

api.MapPost(
    "/mailbox/inbound",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService,
        IHostApplicationLifetime applicationLifetime,
        GalateaAcceptedTurnRunner turnRunner
    ) => {
        InboundMailboxRequest request = await GalateaHttpV1
            .ReadJsonBodyAsync<InboundMailboxRequest>(httpContext);
        string? invalid = GalateaHttpV1.ValidateMailboxText(
                request.From,
                "from",
                GalateaMailboxBounds.MaximumSenderUtf8Bytes,
                allowLineBreaks: false
            )
            ?? GalateaHttpV1.ValidateMailboxText(
                request.Subject,
                "subject",
                GalateaMailboxBounds.MaximumSubjectUtf8Bytes,
                allowNull: true,
                allowLineBreaks: false
            )
            ?? GalateaHttpV1.ValidateMailboxText(
                request.Body,
                "body",
                GalateaMailboxBounds.MaximumBodyUtf8Bytes
            )
            ?? GalateaHttpV1.ValidateConnectionId(request.ConnectionId);
        if (invalid is not null) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-mailbox-message",
                invalid
            ));
        }

        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException(
                "Authenticated principal is missing user id."
            );
        UserSessionHost session = await hostService.GetSessionAsync(
            userId,
            httpContext.RequestAborted
        );
        if (!session.TurnLock.Wait(0)) {
            return BuildTurnBusyConflict(hostService, session);
        }

        GalateaLiveTurn? liveTurn = null;
        bool writerOwnershipTransferred = false;
        try {
            hostService.RequireRunning();
            await hostService.ReconcileDurableAdmissionAsync(
                session,
                httpContext.RequestAborted
            );
            SessionRuntimeRecoveryRequirements recovery =
                session.Engine.InspectRuntimeRecoveryRequirements(
                    httpContext.RequestAborted
                );
            bool acceptsFreshMail = recovery switch {
                SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
                    Phase: SessionExecutionPhase.Idle
                } => true,
                SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned => true,
                _ => false
            };
            if (!acceptsFreshMail) {
                return RecoveryConflict(
                    recovery,
                    recovery.Phase == SessionExecutionPhase.Empty
                        ? "session-unprovisioned"
                        : "recovery-required",
                    recovery.Phase == SessionExecutionPhase.Empty
                        ? "会话仓库尚未完成初始化。"
                        : "当前会话存在待恢复的持久化轮次；新邮件未被接收。"
                );
            }
            if (!hostService.TryGetConnection(
                    session.User,
                    request.ConnectionId,
                    out CompletionConnectionConfig connection)) {
                return Results.BadRequest(new ApiErrorDto(
                    "unknown-connection",
                    $"Unknown completion connection '{request.ConnectionId}'."
                ));
            }
            MailboxMessage message = MailboxMessage.CreateInbound(
                session.User.CharacterName,
                request.From,
                request.Subject,
                request.Body
            );
            await hostService.PrepareFreshTurnAdmissionAsync(
                session,
                recovery,
                httpContext.RequestAborted
            );
            liveTurn = hostService.StartInboundMailTurn(
                session,
                message,
                new GalateaTurnOptions(connection.Id)
            );
            IResult result = Results.Json(
                new InboundMailboxAcceptedDto(
                    liveTurn.TurnId,
                    message.MessageId
                ),
                statusCode: StatusCodes.Status202Accepted
            );
            _ = turnRunner.Start(session, liveTurn);
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
                    await hostService.RefreshRecentTurnsBestEffortAsync(
                        session,
                        applicationLifetime.ApplicationStopping
                    );
                }
                finally {
                    session.TurnLock.Release();
                }
            }
        }
    }
).WithMetadata(
    GalateaHttpV1.JsonBody,
    GalateaHttpV1.MaintenanceWrite
);

api.MapPost(
    "/chat/turns/pop-latest",
    async (
        HttpContext httpContext,
        ClaimsPrincipal user,
        GalateaHostService hostService
    ) => {
        PopLatestTurnRequestDto request = await GalateaHttpV1
            .ReadJsonBodyAsync<PopLatestTurnRequestDto>(httpContext);
        if (!GalateaHttpV1.TryParseCanonicalEventAddress(
                request.RewindLatestToken,
                out var expectedHead
            )) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-rewind-token",
                "rewindLatestToken格式无效。"
            ));
        }
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        // Recent/cadence reads and automatic admission checks also own TurnLock.
        // Let those short operations finish instead of rejecting an idle Undo.
        // Never wait behind a published model turn; the exact head CAS below
        // still rejects a competing mutation that wins during this bounded wait.
        if (session.GetCurrentTurn() is not null
            || !await session.TurnLock.WaitAsync(
                TimeSpan.FromSeconds(1),
                httpContext.RequestAborted
            )) {
            return BuildTurnBusyConflict(hostService, session);
        }
        try {
            hostService.RequireRunning();
            await hostService.ReconcileDurableAdmissionAsync(
                session,
                httpContext.RequestAborted
            );
            GalateaPreparedPopLatestTurn? prepared = hostService
                .PrepareAndCommitPopLatestTurn(
                    session,
                    expectedHead,
                    httpContext.RequestAborted
                );
            if (prepared is null) {
                DebugUtil.Warning("Galatea.Api", $"POST /api/v1/chat/turns/pop-latest user={userId} returned null, head={session.Engine.ReadCurrentHead()}");
                return Results.Json(new ApiErrorDto(
                    "rewind-not-available",
                    "当前没有可取出的最近一轮，或会话边界已变化。"
                ), statusCode: StatusCodes.Status409Conflict);
            }

            DebugUtil.Info("Galatea.Api", $"POST /api/v1/chat/turns/pop-latest user={userId} succeeded, head={session.Engine.ReadCurrentHead()}");
            return Results.Bytes(
                prepared.ReceiptUtf8Bytes,
                "application/json"
            );
        }
        finally {
            session.TurnLock.Release();
        }
    }
).WithMetadata(
    GalateaHttpV1.JsonBody,
    GalateaHttpV1.MaintenanceWrite
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
        DebugUtil.Info("Galatea.Api", $"GET /api/v1/chat/turns/current user={userId}, status={currentTurn.Status}, turnId={currentTurn.TurnId ?? "<none>"}");
        return Results.Ok(currentTurn);
    }
);

api.MapPost(
    "/chat/turns/{turnId}/stop",
    async (HttpContext httpContext, ClaimsPrincipal user, GalateaHostService hostService, string turnId) => {
        if (!GalateaHttpV1.IsCanonicalTurnId(turnId)) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-turn-id",
                "turnId格式无效。"
            ));
        }
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);
        if (!hostService.RequestStop(session, turnId)) {
            return Results.NotFound(new ApiErrorDto(
                "turn-not-found",
                "turn not found or already finished."
            ));
        }

        DebugUtil.Warning("Galatea.Api", $"POST /api/v1/chat/turns/{turnId}/stop user={userId}");
        return Results.NoContent();
    }
).WithMetadata(GalateaHttpV1.MaintenanceWrite);

api.MapGet(
    "/chat/turns/{turnId}/events",
    async (HttpContext httpContext, ClaimsPrincipal user, GalateaHostService hostService, string turnId) => {
        if (!GalateaHttpV1.IsCanonicalTurnId(turnId)) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-turn-id",
                "turnId格式无效。"
            ));
        }
        string userId = user.FindFirstValue(GalateaClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);
        var liveTurn = hostService.FindTurn(session, turnId);
        if (liveTurn is null) {
            return Results.NotFound(new ApiErrorDto(
                "turn-not-found",
                "turn not found."
            ));
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-store";

        using var subscription = liveTurn.Subscribe();

        try {
            foreach (GalateaSseFrame replayFrame
                     in subscription.ReplayFrames) {
                await GalateaSseWriter.WriteFrameAsync(
                    httpContext.Response,
                    replayFrame,
                    httpContext.RequestAborted
                );
            }

            await foreach (GalateaSseFrame streamFrame
                           in subscription.Reader.ReadAllAsync(
                               httpContext.RequestAborted)) {
                await GalateaSseWriter.WriteFrameAsync(
                    httpContext.Response,
                    streamFrame,
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
        new TurnBusyErrorDto(
            "turn-busy",
            "该账号当前正在生成，请稍后。",
            runningTurn.TurnId
        ),
        statusCode: StatusCodes.Status409Conflict
    );
}

static IResult BuildAcceptedTurnResult(
    GalateaLiveTurn liveTurn,
    string? loopPulseOrigin = null
) {
    return loopPulseOrigin switch {
        null => Results.Json(
            new StartTurnResponseDto(liveTurn.TurnId),
            statusCode: StatusCodes.Status202Accepted
        ),
        "delegate-reply" or "heartbeat-activation" => Results.Json(
            new LoopPulseAcceptedTurnDto(
                liveTurn.TurnId,
                loopPulseOrigin
            ),
            statusCode: StatusCodes.Status202Accepted
        ),
        _ => throw new ArgumentOutOfRangeException(
            nameof(loopPulseOrigin),
            loopPulseOrigin,
            "Unknown loop-pulse origin."
        )
    };
}

static IResult RecoveryConflict(
    SessionRuntimeRecoveryRequirements _,
    string code,
    string error
) => Results.Json(
    new ApiErrorDto(code, error),
    statusCode: StatusCodes.Status409Conflict
);

static (int StatusCode, ApiErrorDto Error) MapApiException(
    Exception exception
) {
    if (ContainsRequestBodyLimitException(exception)) {
        return (
            StatusCodes.Status413PayloadTooLarge,
            new ApiErrorDto(
                "request-too-large",
                "Request body exceeds the 1 MiB limit."
            )
        );
    }
    return exception switch {
    BadHttpRequestException badRequest when
        badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge => (
        StatusCodes.Status413PayloadTooLarge,
        new ApiErrorDto(
            "request-too-large",
            "Request body exceeds the 1 MiB limit."
        )
    ),
    BadHttpRequestException or JsonException => (
        StatusCodes.Status400BadRequest,
        new ApiErrorDto(
            "invalid-request",
            "Request JSON does not match the endpoint contract."
        )
    ),
    GalateaSessionUnavailableException unavailable => (
        StatusCodes.Status503ServiceUnavailable,
        new ApiErrorDto(unavailable.Code, unavailable.Message)
    ),
    GalateaDelegationUserUnavailableException => (
        StatusCodes.Status503ServiceUnavailable,
        new ApiErrorDto(
            "delegation-unavailable",
            "Durable delegation is unavailable for this user."
        )
    ),
    GalateaTurnException turn when turn.FailureReason is { } reason
        && reason.StartsWith("delegation-", StringComparison.Ordinal) => (
        string.Equals(
            reason,
            "delegation-state-changed",
            StringComparison.Ordinal
        )
            ? StatusCodes.Status409Conflict
            : string.Equals(
                reason,
                "delegation-state-invalid",
                StringComparison.Ordinal
            )
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status503ServiceUnavailable,
        new ApiErrorDto(reason, turn.Message)
    ),
    GalateaRecentProjectionException projection when
        string.Equals(
            projection.Code,
            "session-invalid",
            StringComparison.Ordinal
        ) => (
            StatusCodes.Status500InternalServerError,
            new ApiErrorDto(
                "session-invalid",
                "Session data is invalid."
            )
        ),
    GalateaRecentProjectionException projection => (
        StatusCodes.Status503ServiceUnavailable,
        new ApiErrorDto(projection.Code, projection.Message)
    ),
    _ => (
        StatusCodes.Status500InternalServerError,
        new ApiErrorDto(
            "internal-error",
            "The server could not complete the request."
        )
    )
    };
}

static bool ContainsRequestBodyLimitException(Exception exception) {
    for (Exception? current = exception;
         current is not null;
         current = current.InnerException) {
        if (current is RequestBodyLimitExceededException) {
            return true;
        }
    }
    return false;
}

public partial class Program;

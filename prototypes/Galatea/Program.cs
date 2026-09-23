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

if (GalateaCharacterMemoryStoreUpgrade.IsInvocation(args)) {
    Environment.ExitCode = GalateaCharacterMemoryStoreUpgrade.Run(args, Console.Out, Console.Error);
    return;
}

if (GalateaDelegationStoreUpgrade.IsInvocation(args)) {
    Environment.ExitCode = GalateaDelegationStoreUpgrade.Run(args, Console.Out, Console.Error);
    return;
}

if (GalateaCodexBindingReset.IsInvocation(args)) {
    Environment.ExitCode = GalateaCodexBindingReset.Run(args, Console.Out, Console.Error);
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
builder.Services.AddSingleton<GalateaCharacterMailRelay>();
builder.Services.AddHostedService<GalateaServerAgentHostedService>();
builder.Services.AddHostedService(static services =>
    services.GetRequiredService<GalateaCharacterMailRelay>());
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
        options.Cookie.Name = "galatea_player_auth";
        options.Events.OnValidatePrincipal = context => {
            string? playerId = context.Principal?.FindFirstValue(GalateaClaimTypes.PlayerId);
            var host = context.HttpContext.RequestServices.GetRequiredService<GalateaHostService>();
            if (playerId is null || !host.TryGetPlayer(playerId, out _)) {
                context.RejectPrincipal();
            }
            return Task.CompletedTask;
        };
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
                $"API request failed: method={context.Request.Method}, path={context.Request.Path}, status={statusCode}, code={error.Code}; exceptionType={exception.GetType().FullName}"
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
    if (context.Request.RouteValues.ContainsKey("characterId")) {
        if (!GalateaHttpV1.TryReadCharacterRouteId(context, out string characterId)
            || !eagerHost.TryGetCharacter(characterId, out _)) {
            await Results.NotFound(new ApiErrorDto("character-not-found", "The target character is not configured."))
                .ExecuteAsync(context);
            return;
        }
        // Resolve before Minimal API binds its arguments; no handler infers a
        // Character from the authenticated Player or a partially decoded ID.
        context.Request.RouteValues["characterId"] = characterId;
        context.Response.Headers["Galatea-Character-Id"] = Uri.EscapeDataString(characterId);
    }
    await next(context);
});
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
        string playerId = form["playerId"].ToString();
        string password = form["password"].ToString();

        if (!hostService.TryGetPlayer(playerId, out var player) || !hostService.ValidatePassword(player, password)) {
            return Results.Content(
                GalateaHtml.RenderLoginPage(invalidCredentials: true, assetVersion),
                "text/html; charset=utf-8",
                Encoding.UTF8,
                StatusCodes.Status401Unauthorized
            );
        }

        var claims = new[] {
            new Claim(GalateaClaimTypes.PlayerId, player.PlayerId),
            new Claim(ClaimTypes.Name, player.Name.Value),
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
    (HttpContext httpContext, ClaimsPrincipal user, GalateaHostService hostService) => {
        httpContext.Response.Headers.CacheControl = "no-store";
        GalateaPlayerConfig player = RequirePlayer(user, hostService);
        return Results.Content(
            GalateaHtml.RenderCharacterDirectory(player, config.Characters, assetVersion),
            "text/html; charset=utf-8"
        );
    }
).RequireAuthorization();

app.MapGet(
    "/characters/{characterId}",
    (HttpContext httpContext, string characterId, ClaimsPrincipal user, GalateaHostService hostService) => {
        httpContext.Response.Headers.CacheControl = "no-store";
        GalateaPlayerConfig player = RequirePlayer(user, hostService);
        if (!hostService.TryGetCharacter(characterId, out var character)) {
            return Results.NotFound();
        }
        return Results.Content(
            GalateaHtml.RenderAppPage(character, player, hostService.Connections,
                config.MaintenanceMode, assetVersion),
            "text/html; charset=utf-8"
        );
    }
).RequireAuthorization();

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.AddEndpointFilter(async (context, next) => {
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
});
api.MapGet("/me", (ClaimsPrincipal user, GalateaHostService hostService) => {
    GalateaPlayerConfig player = RequirePlayer(user, hostService);
    return Results.Ok(new GalateaMeDto(player.PlayerId, player.Name.Value, config.MaintenanceMode));
});
api.MapGet("/characters", () => Results.Ok(config.Characters.Select(character => new {
    characterId = character.CharacterId,
    name = character.CharacterName.Value
})));

// Authentication identifies the visitor; this route identifies the owner of
// every read, writer lock, recovery action, and stream below.
var characterApi = api.MapGroup("/characters/{characterId}");

characterApi.MapGet(
    "/recent-turns",
    async (string characterId, GalateaHostService hostService, CancellationToken ct) => {
        var session = await hostService.GetSessionAsync(characterId, ct);
        var response = await hostService.GetRecentTurnsAsync(session, ct);
        DebugUtil.Debug(
            "Galatea.Api",
            $"GET /api/v1/characters/{characterId}/recent-turns character={characterId}, items={response.Turns.Count}, rewindEligible={response.RewindLatestToken is not null}"
        );
        return Results.Ok(response);
    }
);

characterApi.MapGet(
    "/recap-cadence-progress",
    async (
        string characterId,
        GalateaHostService hostService,
        CancellationToken ct
    ) => {
        CharacterSessionHost session = await hostService.GetSessionAsync(
            characterId,
            ct
        );
        RecapCadenceProgressSnapshotDto response = await hostService
            .GetRecapCadenceProgressAsync(session, ct);
        DebugUtil.Debug(
            "Galatea.Api",
            $"GET /api/v1/characters/{characterId}/recap-cadence-progress "
                + $"character={characterId}, freshness={response.Freshness}, "
                + $"state={response.State}, "
                + $"head={response.ObservedRawHead ?? "<none>"}"
        );
        return Results.Ok(response);
    }
);

characterApi.MapGet(
    "/mailbox/status",
    (
        HttpContext httpContext,
        string characterId,
        GalateaHostService hostService
    ) => {
        httpContext.Response.Headers.CacheControl = "no-store";
        GalateaMailboxStatusDto response = hostService.ReadMailboxStatus(
            characterId
        );
        return Results.Ok(response);
    }
);

characterApi.MapPost(
    "/chat/turns",
    async (
        HttpContext httpContext,
        string characterId,
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

        var session = await hostService.GetSessionAsync(characterId, httpContext.RequestAborted);

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
                    .LegacyFailedTurnBlocked => false,
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
                    session.Character,
                    request.ConnectionId,
                    out CompletionConnectionConfig connection
                )) {
                return Results.BadRequest(new ApiErrorDto(
                    "unknown-connection",
                    $"Unknown completion connection '{request.ConnectionId}'."
                ));
            }
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
                new GalateaTurnOptions(connection.Id),
                PlayerSender(user, hostService)
            );
            DebugUtil.Debug("Galatea.Api", $"POST /api/v1/characters/{characterId}/chat/turns character={characterId}, turnId={liveTurn.TurnId}, connectionId={connection.Id}, head={session.Engine.ReadCurrentHead()}");
            IResult result = BuildAcceptedTurnResult(liveTurn);
            _ = turnRunner.Start(session, liveTurn);
            writerOwnershipTransferred = true;
            return result;
        }
        catch (Exception original) when (
            liveTurn is not null && !writerOwnershipTransferred) {
            try {
                hostService.ReconcileAcceptanceCleanup(session);
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

characterApi.MapPost(
    "/chat/turns/resume",
    async (
        HttpContext httpContext,
        string characterId,
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
        var session = await hostService.GetSessionAsync(
            characterId,
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
                    .LegacyFailedTurnBlocked) {
                return RecoveryConflict(
                    recovery,
                    "failed-turn-must-be-abandoned",
                    "旧失败轮次必须在精确边界显式结束。"
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

            string connectionId;
            if (recovery is SessionRuntimeRecoveryRequirements
                    .NewRequestRequired) {
                if (!hostService.TryGetConnection(
                        session.Character,
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
                    ?? session.Character.DefaultConnectionId;
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
                    expectedHead
                )
            );
            IResult result = BuildAcceptedTurnResult(liveTurn);
            session.GenerationBlocked = false;
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

characterApi.MapPost(
    "/mailbox/ready-turn",
    async (HttpContext httpContext, string characterId, GalateaAutomaticTurnCoordinator coordinator) => {
        _ = await GalateaHttpV1.ReadJsonBodyAsync<ReadyReplyTurnRequest>(httpContext);
        GalateaAutomaticTurnResult result = await coordinator.TryPulseAsync(characterId, httpContext.RequestAborted);
        return result switch {
            GalateaAutomaticTurnResult.Started started => BuildAcceptedTurnResult(started.Turn, started.Origin),
            GalateaAutomaticTurnResult.Status status => Results.Ok(new LoopPulseStatusDto(
                status.Value.State,
                status.Value.NextActivationAtUnixTimeMilliseconds,
                status.Value.LastActivationAtUnixTimeMilliseconds,
                status.Value.Code
            )),
            GalateaAutomaticTurnResult.Busy busy => Results.Json(
                new TurnBusyErrorDto("turn-busy", "该角色当前正在生成，请稍后。", busy.TurnId),
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

characterApi.MapGet(
    "/agent/status",
    (HttpContext httpContext, string characterId, GalateaAutomaticTurnCoordinator coordinator) => {
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(coordinator.ReadStatus(characterId));
    }
);

characterApi.MapPost(
    "/agent/retry-admission",
    async (HttpContext httpContext, string characterId, GalateaAutomaticTurnCoordinator coordinator) => {
        _ = await GalateaHttpV1.ReadJsonBodyAsync<RetryAdmissionRequest>(httpContext);
        GalateaAutomaticTurnResult result = await coordinator.RetryAdmissionAsync(characterId, httpContext.RequestAborted);
        return result switch {
            GalateaAutomaticTurnResult.Status status => Results.Ok(status.Value),
            GalateaAutomaticTurnResult.Busy busy => Results.Json(
                new TurnBusyErrorDto("turn-busy", "该角色正在处理其他请求，请稍后重试。", busy.TurnId),
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

characterApi.MapGet("/agent/admission", (string characterId, GalateaHostService hostService) =>
    Results.Ok(hostService.ReadAttachedSession(characterId)?.ReadAdmissionStatus() ?? new GalateaAdmissionStatusDto(null, "idle")));

characterApi.MapPost("/agent/admission/{operationId}/stop", (string characterId, string operationId, GalateaHostService hostService) => {
    if (!GalateaHttpV1.IsCanonicalTurnId(operationId)) {
        return Results.BadRequest(new ApiErrorDto("invalid-operation-id", "operationId格式无效。"));
    }
    if (hostService.ReadAttachedSession(characterId)?.StopAdmission(operationId) != true) {
        return Results.Conflict(new ApiErrorDto("admission-changed", "整理操作已结束或已变化。"));
    }
    return Results.Accepted();
}).WithMetadata(GalateaHttpV1.MaintenanceWrite);

characterApi.MapPost(
    "/mailbox/inbound",
    async (
        HttpContext httpContext,
        string characterId,
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

        CharacterSessionHost session = await hostService.GetSessionAsync(
            characterId,
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
                    .LegacyFailedTurnBlocked => false,
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
                    session.Character,
                    request.ConnectionId,
                    out CompletionConnectionConfig connection)) {
                return Results.BadRequest(new ApiErrorDto(
                    "unknown-connection",
                    $"Unknown completion connection '{request.ConnectionId}'."
                ));
            }
            MailboxMessage message = MailboxMessage.CreateInbound(
                session.Character.CharacterName,
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
                new GalateaTurnOptions(connection.Id),
                injectedBy: PlayerSender(user, hostService)
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

characterApi.MapPost(
    "/chat/turns/pop-latest",
    async (
        HttpContext httpContext,
        string characterId,
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
        var session = await hostService.GetSessionAsync(characterId, httpContext.RequestAborted);

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
                DebugUtil.Warning("Galatea.Api", $"POST /api/v1/characters/{characterId}/chat/turns/pop-latest character={characterId} returned null, head={session.Engine.ReadCurrentHead()}");
                return Results.Json(new ApiErrorDto(
                    "rewind-not-available",
                    "当前没有可取出的最近一轮，或会话边界已变化。"
                ), statusCode: StatusCodes.Status409Conflict);
            }

            DebugUtil.Debug("Galatea.Api", $"POST /api/v1/characters/{characterId}/chat/turns/pop-latest character={characterId} succeeded, head={session.Engine.ReadCurrentHead()}");
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

characterApi.MapGet(
    "/chat/turns/current",
    async (string characterId, GalateaHostService hostService, CancellationToken ct) => {
        var session = await hostService.GetSessionAsync(characterId, ct);
        var currentTurn = await hostService.GetCurrentTurnAsync(
            session,
            ct
        );
        DebugUtil.Debug("Galatea.Api", $"GET /api/v1/characters/{characterId}/chat/turns/current character={characterId}, status={currentTurn.Status}, turnId={currentTurn.TurnId ?? "<none>"}");
        return Results.Ok(currentTurn);
    }
);

characterApi.MapPost(
    "/chat/turns/pending/stop",
    async (HttpContext context, string characterId, GalateaHostService hostService) => {
        var request = await GalateaHttpV1.ReadJsonBodyAsync<StopPendingTurnRequest>(context);
        if (!GalateaHttpV1.TryParseCanonicalEventAddress(request.ExpectedHead, out var expectedHead)) {
            return Results.BadRequest(new ApiErrorDto("invalid-expected-head", "expectedHead格式无效。"));
        }
        var session = await hostService.GetSessionAsync(characterId, context.RequestAborted);
        if (!session.TurnLock.Wait(0)) { return BuildTurnBusyConflict(hostService, session); }
        try {
            hostService.RequireRunning();
            if (session.GetCurrentTurn() is not null || !hostService.EndPendingTurn(session, expectedHead, context.RequestAborted)) {
                return Results.Conflict(new ApiErrorDto("unsafe-turn-boundary", "会话边界已变化或工具尚未结算。"));
            }
            await hostService.RefreshRecentTurnsBestEffortAsync(session, context.RequestAborted);
            return Results.NoContent();
        }
        finally { session.TurnLock.Release(); }
    }
).WithMetadata(GalateaHttpV1.JsonBody, GalateaHttpV1.MaintenanceWrite);

characterApi.MapPost(
    "/chat/turns/{turnId}/stop",
    async (HttpContext httpContext, string characterId, GalateaHostService hostService, string turnId) => {
        if (!GalateaHttpV1.IsCanonicalTurnId(turnId)) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-turn-id",
                "turnId格式无效。"
            ));
        }
        var session = await hostService.GetSessionAsync(characterId, httpContext.RequestAborted);
        if (!hostService.RequestStop(session, turnId)) {
            return Results.NotFound(new ApiErrorDto(
                "turn-not-found",
                "turn not found or already finished."
            ));
        }

        DebugUtil.Warning("Galatea.Api", $"POST /api/v1/characters/{characterId}/chat/turns/{turnId}/stop character={characterId}");
        return Results.NoContent();
    }
).WithMetadata(GalateaHttpV1.MaintenanceWrite);

characterApi.MapGet(
    "/chat/turns/{turnId}/events",
    async (HttpContext httpContext, string characterId, GalateaHostService hostService, string turnId) => {
        if (!GalateaHttpV1.IsCanonicalTurnId(turnId)) {
            return Results.BadRequest(new ApiErrorDto(
                "invalid-turn-id",
                "turnId格式无效。"
            ));
        }
        var session = await hostService.GetSessionAsync(characterId, httpContext.RequestAborted);
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

static GalateaPlayerConfig RequirePlayer(ClaimsPrincipal principal, GalateaHostService host) {
    string? playerId = principal.FindFirstValue(GalateaClaimTypes.PlayerId);
    if (playerId is null || !host.TryGetPlayer(playerId, out var player)) {
        throw new InvalidOperationException("Authenticated principal does not identify a configured Player.");
    }
    return player;
}

static GalateaSenderSnapshot PlayerSender(ClaimsPrincipal principal, GalateaHostService host) {
    GalateaPlayerConfig player = RequirePlayer(principal, host);
    return GalateaSenderSnapshot.Player(player);
}

static IResult BuildTurnBusyConflict(GalateaHostService hostService, CharacterSessionHost session) {
    var runningTurn = hostService.BuildLiveCurrentTurn(session);
    DebugUtil.Warning(
        "Galatea.Api",
        $"Turn busy conflict: character={session.Character.CharacterId}, runningTurn={runningTurn.TurnId ?? "<none>"}"
    );
    return Results.Json(
        new TurnBusyErrorDto(
            "turn-busy",
            "该角色当前正在生成，请稍后。",
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
        "delegate-reply" or "heartbeat-activation" or "recovery" => Results.Json(
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
    GalateaTurnException { FailureReason: "admission-stopped" } => (
        StatusCodes.Status409Conflict,
        new ApiErrorDto("admission-stopped", "本次整理已停止；原持久化目标保留。")
    ),
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
    GalateaDelegationCharacterUnavailableException => (
        StatusCodes.Status503ServiceUnavailable,
        new ApiErrorDto(
            "delegation-unavailable",
            "Durable delegation is unavailable for this character."
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

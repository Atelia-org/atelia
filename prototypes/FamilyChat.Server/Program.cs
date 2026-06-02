using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.Channels;
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
        if (!hostService.TryGetUser(userId, out var configUser)) {
            return Results.Unauthorized();
        }

        return Results.Content(FamilyChatHtml.RenderAppPage(configUser.DisplayName), "text/html; charset=utf-8");
    }
).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet(
    "/me",
    (ClaimsPrincipal user, FamilyChatHostService hostService) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        if (!hostService.TryGetUser(userId, out var configUser)) {
            return Results.Unauthorized();
        }

        return Results.Ok(new FamilyChatMeDto(configUser.UserId, configUser.DisplayName));
    }
);

api.MapGet(
    "/recent-turns",
    async (ClaimsPrincipal user, FamilyChatHostService hostService, CancellationToken ct) => {
        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, ct);
        return Results.Ok(hostService.BuildRecentTurns(session.Engine));
    }
);

api.MapPost(
    "/chat/stream",
    async (HttpContext httpContext, ClaimsPrincipal user, FamilyChatHostService hostService, ChatStreamRequest request) => {
        if (string.IsNullOrWhiteSpace(request.Message)) {
            return Results.BadRequest(new { error = "message must not be blank." });
        }

        string userId = user.FindFirstValue(FamilyChatClaimTypes.UserId)
            ?? throw new InvalidOperationException("Authenticated principal is missing user id.");
        var session = await hostService.GetSessionAsync(userId, httpContext.RequestAborted);

        if (!session.TurnLock.Wait(0)) {
            return Results.Json(
                new { error = "该账号当前正在生成，请稍后。" },
                statusCode: StatusCodes.Status409Conflict
            );
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-store";

        var channel = Channel.CreateUnbounded<StreamEventDto>();
        var runTask = Task.Run(
            async () => {
                try {
                    await hostService.RunTurnAsync(session, request.Message, channel.Writer, httpContext.RequestAborted);
                }
                catch (OperationCanceledException) {
                    // Browser disconnected or request cancelled.
                }
                catch (Exception ex) {
                    await channel.Writer.WriteAsync(
                        new StreamEventDto("error", new { message = ex.Message }),
                        CancellationToken.None
                    );
                }
                finally {
                    channel.Writer.TryComplete();
                    session.TurnLock.Release();
                }
            },
            CancellationToken.None
        );

        await foreach (var streamEvent in channel.Reader.ReadAllAsync(httpContext.RequestAborted)) {
            await FamilyChatSseWriter.WriteEventAsync(httpContext.Response, streamEvent.Type, streamEvent.Payload, httpContext.RequestAborted);
        }

        await runTask;
        return Results.Empty;
    }
);

app.Run();

public partial class Program;

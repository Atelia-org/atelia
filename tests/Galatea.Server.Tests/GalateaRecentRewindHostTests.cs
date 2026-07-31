using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecentRewindHostTests {
    private static readonly TimeSpan OperationDeadline =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Recent_IsNewestFirstAndTokenIsExactTerminalHead() {
        var completion = new QueueCompletionClient(
            "assistant one",
            "assistant two"
        );
        await using var host = CreateHost(completion);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);

        await CompleteTurnAsync(
            client,
            service,
            session,
            "user one"
        );
        await CompleteTurnAsync(
            client,
            service,
            session,
            "user two"
        );

        RecentTurnsResponseDto recent = await GetRecentAsync(client);

        Assert.Collection(
            recent.Turns,
            turn => AssertTurn(
                turn,
                "user two",
                "assistant two"
            ),
            turn => AssertTurn(
                turn,
                "user one",
                "assistant one"
            )
        );
        Assert.True(EventAddressTextCodec.TryParse(
            recent.RewindLatestToken,
            out var rewindHead
        ));
        Assert.Equal(session.Engine.ReadCurrentHead(), rewindHead);

        SessionCompletedTurnsSnapshot raw =
            session.Engine.ReadRecentCompletedTurns(2);
        Assert.Equal(raw.CapturedHead, rewindHead);
        Assert.Equal(
            raw.Turns[0].TerminalAction.Address,
            rewindHead
        );
        Assert.Equal(2, completion.DispatchCallCount);
    }

    [Fact]
    public async Task ExactUndo_ReturnsMovedTurnAndReopenKeepsItOffLineage() {
        var completion = new QueueCompletionClient(
            "assistant one",
            "assistant two"
        );
        GalateaTestHost? host = CreateHost(
            completion,
            deleteFilesOnDispose: false
        );
        string rootDirectory = host.RootDirectory;
        string sessionDirectory = host.SessionDirectory;
        try {
            using (HttpClient client = host.CreateClient()) {
                await LoginAsync(client);
                (GalateaHostService service, UserSessionHost session) =
                    await GetSessionAsync(host);

                await CompleteTurnAsync(
                    client,
                    service,
                    session,
                    "user one"
                );
                await CompleteTurnAsync(
                    client,
                    service,
                    session,
                    "user two"
                );
                RecentTurnsResponseDto before =
                    await GetRecentAsync(client);
                string token = Assert.IsType<string>(
                    before.RewindLatestToken
                );
                Assert.True(EventAddressTextCodec.TryParse(
                    token,
                    out var oldHead
                ));

                PopLatestTurnResponseDto moved =
                    await PopLatestAsync(client, token);

                AssertTurn(
                    moved.Turn,
                    "user two",
                    "assistant two"
                );
                RecentTurnDto remaining = Assert.Single(
                    moved.Recent.Turns
                );
                AssertTurn(
                    remaining,
                    "user one",
                    "assistant one"
                );
                string remainingToken = Assert.IsType<string>(
                    moved.Recent.RewindLatestToken
                );
                Assert.True(EventAddressTextCodec.TryParse(
                    remainingToken,
                    out var newHead
                ));
                Assert.Equal(
                    newHead,
                    session.Engine.ReadCurrentHead()
                );
                Assert.NotEqual(oldHead, newHead);

                await host.DisposeAsync();
                host = null;

                using var reopened =
                    SessionJournalEngine.OpenReadOnly(
                        sessionDirectory
                    );
                SessionCompletedTurnProjection reopenedCurrent =
                    Assert.Single(
                        reopened.ReadRecentCompletedTurns(10).Turns
                    );
                Assert.Contains(
                    "user one",
                    reopenedCurrent.ObservationContent,
                    StringComparison.Ordinal
                );
                Assert.DoesNotContain(
                    reopened.ReadRecentCompletedTurns(10).Turns,
                    static turn => turn.ObservationContent.Contains(
                        "user two",
                        StringComparison.Ordinal
                    )
                );

                SessionCompletedTurnProjection historicalNewest =
                    reopened.ReadRecentCompletedTurnsAt(
                        oldHead,
                        10
                    ).Turns[0];
                Assert.Contains(
                    "user two",
                    historicalNewest.ObservationContent,
                    StringComparison.Ordinal
                );
            }
        }
        finally {
            if (host is not null) {
                await host.DisposeAsync();
            }
            if (Directory.Exists(rootDirectory)) {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StaleToken_ReturnsConflictWithoutMovingHead() {
        var completion = new QueueCompletionClient(
            "assistant one",
            "assistant two"
        );
        await using var host = CreateHost(completion);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);

        await CompleteTurnAsync(
            client,
            service,
            session,
            "user one"
        );
        string staleToken = Assert.IsType<string>(
            (await GetRecentAsync(client)).RewindLatestToken
        );
        await CompleteTurnAsync(
            client,
            service,
            session,
            "user two"
        );
        var expectedHead = session.Engine.ReadCurrentHead();

        using HttpResponseMessage response = await PostPopAsync(
            client,
            staleToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(expectedHead, session.Engine.ReadCurrentHead());
        RecentTurnsResponseDto after = await GetRecentAsync(client);
        Assert.Equal(2, after.Turns.Count);
        AssertTurn(after.Turns[0], "user two", "assistant two");
        Assert.NotEqual(staleToken, after.RewindLatestToken);
    }

    [Fact]
    public async Task WriterBusy_ReturnsConflictWithoutMovingHead() {
        var completion = new QueueCompletionClient("assistant one");
        await using var host = CreateHost(completion);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);

        await CompleteTurnAsync(
            client,
            service,
            session,
            "user one"
        );
        string token = Assert.IsType<string>(
            (await GetRecentAsync(client)).RewindLatestToken
        );
        var expectedHead = session.Engine.ReadCurrentHead();

        await session.TurnLock.WaitAsync();
        try {
            using HttpResponseMessage busy = await PostPopAsync(
                    client,
                    token
                )
                .WaitAsync(OperationDeadline);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal(
                expectedHead,
                session.Engine.ReadCurrentHead()
            );
        }
        finally {
            session.TurnLock.Release();
        }

        PopLatestTurnResponseDto moved = await PopLatestAsync(
            client,
            token
        );
        AssertTurn(moved.Turn, "user one", "assistant one");
    }

    private static GalateaTestHost CreateHost(
        ICompletionClient client,
        bool deleteFilesOnDispose = true
    ) => GalateaTestHost.Create(
        new SingleClientFactory(client),
        DisabledGalateaUserMessageNormalizer.Instance,
        deleteFilesOnDispose
    );

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<(
        GalateaHostService Service,
        UserSessionHost Session
    )> GetSessionAsync(GalateaTestHost host) {
        var service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        return (service, session);
    }

    private static async Task CompleteTurnAsync(
        HttpClient client,
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(message, ConnectionId: "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        Task runTask = Assert.IsAssignableFrom<Task>(
            liveTurn.RunTask
        );
        await runTask.WaitAsync(OperationDeadline);
        Assert.Equal("completed", liveTurn.Status);
    }

    private static async Task<RecentTurnsResponseDto> GetRecentAsync(
        HttpClient client
    ) {
        RecentTurnsResponseDto? recent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>(
                "/api/recent-turns"
            );
        return Assert.IsType<RecentTurnsResponseDto>(recent);
    }

    private static async Task<HttpResponseMessage> PostPopAsync(
        HttpClient client,
        string token
    ) => await client.PostAsJsonAsync(
        "/api/chat/turns/pop-latest",
        new PopLatestTurnRequestDto(token)
    );

    private static async Task<PopLatestTurnResponseDto> PopLatestAsync(
        HttpClient client,
        string token
    ) {
        using HttpResponseMessage response = await PostPopAsync(
            client,
            token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PopLatestTurnResponseDto? moved = await response.Content
            .ReadFromJsonAsync<PopLatestTurnResponseDto>();
        return Assert.IsType<PopLatestTurnResponseDto>(moved);
    }

    private static void AssertTurn(
        RecentTurnDto turn,
        string user,
        string assistant
    ) {
        Assert.Equal(user, turn.UserText);
        Assert.Equal(assistant, turn.Assistant.Text);
    }

    private sealed class QueueCompletionClient
        : ICompletionClient {
        private readonly ConcurrentQueue<string> _replies;
        private int _dispatchCallCount;

        internal QueueCompletionClient(params string[] replies) {
            _replies = new ConcurrentQueue<string>(replies);
        }

        public string Name => "galatea-recent-rewind-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_replies.TryDequeue(out string? reply)) {
                throw new InvalidOperationException(
                    "No scripted completion reply remains."
                );
            }
            Interlocked.Increment(ref _dispatchCallCount);
            observer?.OnTextDelta(reply);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(reply)]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class SingleClientFactory(ICompletionClient client)
        : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return client;
        }
    }
}

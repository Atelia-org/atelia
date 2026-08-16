using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecentRewindHostTests {
    private static readonly TimeSpan OperationDeadline =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Recent_DefaultLimitIsSixCompletedTurns() {
        await using var host = CreateHost(new QueueCompletionClient());
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (_, UserSessionHost session) = await GetSessionAsync(host);
        for (int ordinal = 1; ordinal <= 7; ordinal++) {
            _ = session.Engine.AppendObservation(
                GalateaUserMessageEnvelope.Wrap($"user {ordinal}")
            );
            _ = session.Engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"assistant {ordinal}")
                ]),
                new CompletionDescriptor(
                    "recent-limit-fixture",
                    "fixture-v1",
                    "model-a"
                )
            );
        }

        RecentTurnsResponseDto recent = await GetRecentAsync(client);

        Assert.Equal(6, GalateaHostService.RecentTurnLimit);
        Assert.Equal(6, recent.Turns.Count);
        Assert.Equal(
            ["user 7", "user 6", "user 5", "user 4", "user 3", "user 2"],
            recent.Turns.Select(static turn => turn.UserText)
        );
        Assert.DoesNotContain(
            recent.Turns,
            static turn => turn.UserText == "user 1"
        );
    }

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

        RecentTurnsResponseDto completedCache =
            session.GetRecentTurns();
        Assert.Equal(2, completedCache.Turns.Count);
        Assert.NotNull(completedCache.RewindLatestToken);
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
            session.Engine.ReadRecentCompletedTurns(2).RequireSnapshot();
        Assert.Equal(raw.CapturedHead, rewindHead);
        Assert.Equal(
            raw.Turns[0].TerminalAction.Address,
            rewindHead
        );
        Assert.Equal(2, completion.DispatchCallCount);
    }

    [Fact]
    public async Task SuccessfulTurn_DoneCarriesExactTerminalRecapSnapshot() {
        await using var host = CreateHost(
            new QueueCompletionClient("assistant result")
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);

        GalateaLiveTurn liveTurn = await CompleteTurnAsync(
            client,
            service,
            session,
            "user input"
        );

        using GalateaTurnSubscription subscription = liveTurn.Subscribe();
        GalateaSseFrame done = Assert.Single(
            subscription.ReplayFrames,
            static item => item.EventName == "done"
        );
        string dataLine = Encoding.UTF8.GetString(done.Utf8.Span)
            .Split('\n')[1];
        using JsonDocument document = JsonDocument.Parse(
            dataLine["data: ".Length..]
        );
        JsonElement payload = document.RootElement;
        JsonElement recap = payload.GetProperty("recent")
            .GetProperty("recapGridReadiness");
        Assert.Equal("exact", recap.GetProperty("freshness").GetString());
        Assert.Equal(
            EventAddressTextCodec.Format(
                Assert.IsType<Atelia.EventJournal.EventAddress>(
                    session.Engine.ReadCurrentHead()
                )
            ),
            recap.GetProperty("observedRawHead").GetString()
        );
    }

    [Fact]
    public async Task FailedTurn_FinallyRefreshesExactRecapSnapshot() {
        await using var host = CreateHost(new QueueCompletionClient());
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        RecentTurnsResponseDto before = await GetRecentAsync(client);
        Assert.Equal("exact", before.RecapGridReadiness?.Freshness);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/chat/turns",
            new ChatStreamRequest("will fail", ConnectionId: "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);
        GalateaLiveTurn liveTurn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(liveTurn.RunTask)
            .WaitAsync(OperationDeadline);

        Assert.Equal("failed", liveTurn.Status);
        RecentTurnsResponseDto cached = session.GetRecentTurns();
        RecapGridReadinessSnapshotDto recap = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(cached.RecapGridReadiness);
        Assert.Equal("exact", recap.Freshness);
        Assert.Equal(
            EventAddressTextCodec.Format(
                Assert.IsType<Atelia.EventJournal.EventAddress>(
                    session.Engine.ReadCurrentHead()
                )
            ),
            recap.ObservedRawHead
        );
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
                RecapGridReadinessSnapshotDto beforeRecap = Assert.IsType<
                    RecapGridReadinessSnapshotDto
                >(before.RecapGridReadiness);
                Assert.Equal("exact", beforeRecap.Freshness);
                Assert.Equal(
                    EventAddressTextCodec.Format(oldHead),
                    beforeRecap.ObservedRawHead
                );

                PopResult moved =
                    await PopLatestAsync(client, token);

                Assert.Equal("user two", moved.PoppedUserText);
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
                RecapGridReadinessSnapshotDto movedRecap = Assert.IsType<
                    RecapGridReadinessSnapshotDto
                >(moved.Recent.RecapGridReadiness);
                Assert.Equal("exact", movedRecap.Freshness);
                Assert.Equal(
                    EventAddressTextCodec.Format(newHead),
                    movedRecap.ObservedRawHead
                );
                RecentTurnsResponseDto undoCache =
                    session.GetRecentTurns();
                Assert.Equal(
                    moved.Recent.RewindLatestToken,
                    undoCache.RewindLatestToken
                );
                RecentTurnDto cachedRemaining = Assert.Single(
                    undoCache.Turns
                );
                AssertTurn(
                    cachedRemaining,
                    "user one",
                    "assistant one"
                );
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
                        reopened.ReadRecentCompletedTurns(10).RequireSnapshot().Turns
                    );
                Assert.Contains(
                    "user one",
                    reopenedCurrent.ObservationContent,
                    StringComparison.Ordinal
                );
                Assert.DoesNotContain(
                    reopened.ReadRecentCompletedTurns(10).RequireSnapshot().Turns,
                    static turn => turn.ObservationContent.Contains(
                        "user two",
                        StringComparison.Ordinal
                    )
                );

                SessionCompletedTurnProjection historicalNewest =
                    reopened.ReadRecentCompletedTurnsAt(
                        oldHead,
                        10
                    ).RequireSnapshot().Turns[0];
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
    public async Task ConsecutiveUndo_UsesEachFreshTokenAndMovesOneCompletedTurnPerRequest() {
        var completion = new QueueCompletionClient(
            "assistant one",
            "assistant two",
            "assistant three"
        );
        await using var host = CreateHost(completion);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);

        await CompleteTurnAsync(client, service, session, "user one");
        await CompleteTurnAsync(client, service, session, "user two");
        await CompleteTurnAsync(client, service, session, "user three");

        RecentTurnsResponseDto before = await GetRecentAsync(client);
        string thirdToken = Assert.IsType<string>(
            before.RewindLatestToken
        );
        PopResult first = await PopLatestAsync(
            client,
            thirdToken
        );

        Assert.Equal("user three", first.PoppedUserText);
        Assert.Collection(
            first.Recent.Turns,
            turn => AssertTurn(turn, "user two", "assistant two"),
            turn => AssertTurn(turn, "user one", "assistant one")
        );
        string secondToken = Assert.IsType<string>(
            first.Recent.RewindLatestToken
        );
        Assert.NotEqual(thirdToken, secondToken);

        PopResult second = await PopLatestAsync(
            client,
            secondToken
        );

        Assert.Equal("user two", second.PoppedUserText);
        AssertTurn(
            Assert.Single(second.Recent.Turns),
            "user one",
            "assistant one"
        );
        string firstToken = Assert.IsType<string>(
            second.Recent.RewindLatestToken
        );
        Assert.NotEqual(secondToken, firstToken);

        PopResult third = await PopLatestAsync(
            client,
            firstToken
        );

        Assert.Equal("user one", third.PoppedUserText);
        Assert.Empty(third.Recent.Turns);
        Assert.Null(third.Recent.RewindLatestToken);
        Assert.Equal(3, completion.DispatchCallCount);
        Assert.Empty(session.Engine.ReadRecentCompletedTurns(10).RequireSnapshot().Turns);
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

        PopResult moved = await PopLatestAsync(
            client,
            token
        );
        Assert.Equal("user one", moved.PoppedUserText);
    }

    [Fact]
    public async Task RefreshFailure_PreservesCachedTurnsButInvalidatesRewindToken() {
        await using var host = CreateHost(new QueueCompletionClient());
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        _ = session.Engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap("cached user")
        );
        _ = session.Engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("cached assistant")
            ]),
            new CompletionDescriptor(
                "refresh-fallback-fixture",
                "fixture-v1",
                "model-a"
            )
        );
        RecentTurnsResponseDto cached =
            await service.GetRecentTurnsAsync(
                session,
                CancellationToken.None
            );
        Assert.NotNull(cached.RewindLatestToken);
        session.Engine.Dispose();

        RecentTurnsResponseDto fallback =
            await service.RefreshRecentTurnsBestEffortAsync(session);

        RecentTurnDto turn = Assert.Single(fallback.Turns);
        AssertTurn(turn, "cached user", "cached assistant");
        Assert.Null(fallback.RewindLatestToken);
        Assert.Equal("stale", fallback.RecapGridReadiness?.Freshness);
        Assert.Same(fallback, session.GetRecentTurns());
    }

    [Fact]
    public async Task PopReceiptLimit_IsCheckedBeforeRefCas() {
        await using var host = CreateHost(new QueueCompletionClient());
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        string oversized = new(
            'x',
            GalateaHostService.MaximumPoppedUserTextUtf8Bytes + 1
        );
        _ = session.Engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap(oversized)
        );
        EventAddress terminal = session.Engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            new CompletionDescriptor("fixture", "fixture-v1", "model-a")
        );

        GalateaRecentProjectionException error = Assert.Throws<
            GalateaRecentProjectionException
        >(() => {
            _ = service.PrepareAndCommitPopLatestTurn(
                session,
                terminal
            );
        });
        Assert.Equal("popped-user-text-limit-exceeded", error.Code);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        using HttpResponseMessage response = await PostPopAsync(
            client,
            EventAddressTextCodec.Format(terminal)
        );
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            response.StatusCode
        );
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(
            "popped-user-text-limit-exceeded",
            body.RootElement.GetProperty("code").GetString()
        );
        Assert.Equal(terminal, session.Engine.ReadCurrentHead());
    }

    [Fact]
    public async Task PopReceipt_WorstCaseEscapingFitsLockedRelationBeforeCas() {
        await using var host = CreateHost(new QueueCompletionClient());
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        string worstCase = new(
            '\0',
            GalateaHostService.MaximumPoppedUserTextUtf8Bytes
        );
        EventAddress before = session.Engine.ReadCurrentHead()!.Value;
        _ = session.Engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap(worstCase)
        );
        EventAddress terminal = session.Engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("done")]),
            new CompletionDescriptor("fixture", "fixture-v1", "model-a")
        );

        GalateaPreparedPopLatestTurn moved = Assert.IsType<
            GalateaPreparedPopLatestTurn
        >(service.PrepareAndCommitPopLatestTurn(session, terminal));
        Assert.Equal(
            ["PoppedUserText", "ReceiptUtf8Bytes"],
            typeof(GalateaPreparedPopLatestTurn).GetProperties()
                .Select(static property => property.Name)
                .Order()
                .ToArray()
        );
        Assert.Equal(worstCase, moved.PoppedUserText);
        Assert.True(
            moved.ReceiptUtf8Bytes.Length
                <= GalateaHostService.MaximumPopReceiptUtf8Bytes
        );
        using JsonDocument receipt = JsonDocument.Parse(
            moved.ReceiptUtf8Bytes
        );
        JsonProperty property = Assert.Single(
            receipt.RootElement.EnumerateObject()
        );
        Assert.Equal("poppedUserText", property.Name);
        Assert.Equal(worstCase, property.Value.GetString());
        Assert.Equal(before, session.Engine.ReadCurrentHead());
    }

    [Fact]
    public void PopCommitTail_PerformsNoProjectionOrSerializationAfterCas() {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "Galatea",
            "GalateaServices.cs"
        ));
        int methodStart = source.IndexOf(
            "PrepareAndCommitPopLatestTurn(",
            StringComparison.Ordinal
        );
        int methodEnd = source.IndexOf(
            "internal GalateaLiveTurn? FindTurn(",
            methodStart,
            StringComparison.Ordinal
        );
        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        string method = source[methodStart..methodEnd];
        int commit = method.IndexOf(
            "CommitPreparedCompletedTurnRewind(",
            StringComparison.Ordinal
        );
        Assert.True(commit > 0);
        string afterCommit = method[commit..];

        Assert.True(
            method.IndexOf(
                "JsonSerializer.SerializeToUtf8Bytes(",
                StringComparison.Ordinal
            ) < commit
        );
        Assert.True(
            method.IndexOf(
                "PrepareRecentSnapshotStale()",
                StringComparison.Ordinal
            ) < commit
        );
        Assert.DoesNotContain(
            "GalateaRecentTurnDisplayAdapter.Project(",
            afterCommit,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "JsonSerializer.",
            afterCommit,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "GalateaBoundedJson.",
            afterCommit,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            ".Turn",
            afterCommit,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void BoundedJson_ExactMaximumPassesAndMaximumPlusOneFails() {
        int maximumBytes =
            GalateaHostService.MaximumRecentResponseUtf8Bytes;
        GalateaBoundedJson.RequireFits(
            new string('a', maximumBytes - 2),
            maximumBytes,
            "recent-view-limit-exceeded"
        );

        GalateaRecentProjectionException error = Assert.Throws<
            GalateaRecentProjectionException
        >(() => GalateaBoundedJson.RequireFits(
            new string('a', maximumBytes - 1),
            maximumBytes,
            "recent-view-limit-exceeded"
        ));
        Assert.Equal("recent-view-limit-exceeded", error.Code);
    }

    [Fact]
    public async Task RecentEncodedLimit_IsNotSwallowedAsStaleCache() {
        await using var host = CreateHost(new QueueCompletionClient());
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        string largeAssistant = new('a', 750_000);
        for (int index = 0; index < GalateaHostService.RecentTurnLimit;
             index++) {
            _ = session.Engine.AppendObservation(
                GalateaUserMessageEnvelope.Wrap($"user-{index}")
            );
            _ = session.Engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(largeAssistant)
                ]),
                new CompletionDescriptor(
                    "fixture",
                    "fixture-v1",
                    "model-a"
                )
            );
        }

        GalateaRecentProjectionException error =
            await Assert.ThrowsAsync<GalateaRecentProjectionException>(
                async () => await service
                    .RefreshRecentTurnsBestEffortAsync(session)
            );
        Assert.Equal("recent-view-limit-exceeded", error.Code);
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/recent-turns"
        );
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            response.StatusCode
        );
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(
            "recent-view-limit-exceeded",
            body.RootElement.GetProperty("code").GetString()
        );
    }

    [Fact]
    public async Task DurableCompletion_WithOversizedRecentPublishesDoneNull() {
        string largeAssistant = new('\0', 700_000);
        await using var host = CreateHost(
            new QueueCompletionClient(largeAssistant)
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);

        GalateaLiveTurn liveTurn = await CompleteTurnAsync(
            client,
            service,
            session,
            "final user"
        );

        Assert.Single(
            session.Engine.ReadRecentCompletedTurns()
                .RequireSnapshot().Turns
        );
        Assert.True(liveTurn.PreviewSuppressed);
        using GalateaTurnSubscription subscription = liveTurn.Subscribe();
        GalateaSseFrame done = Assert.Single(
            subscription.ReplayFrames,
            static frame => frame.EventName == "done"
        );
        Assert.DoesNotContain(
            subscription.ReplayFrames,
            static frame => frame.EventName == "error"
        );
        string dataLine = Encoding.UTF8.GetString(done.Utf8.Span)
            .Split('\n')[1];
        using JsonDocument document = JsonDocument.Parse(
            dataLine["data: ".Length..]
        );
        Assert.Equal(JsonValueKind.Null, document.RootElement
            .GetProperty("recent").ValueKind);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/recent-turns"
        );
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            response.StatusCode
        );
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(
            "recent-view-limit-exceeded",
            body.RootElement.GetProperty("code").GetString()
        );
    }

    [Fact]
    public async Task CompletedBoundary_CancelledRecentRefreshPublishesDoneNull() {
        await using var host = CreateHost(
            new QueueCompletionClient("unused")
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        _ = session.Engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap("durable user")
        );
        _ = session.Engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("durable assistant")]),
            new CompletionDescriptor(
                "cancelled-refresh-fixture",
                "fixture-v1",
                "model-a"
            )
        );
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        RecentTurnsResponseDto? recent =
            await service.RefreshRecentTurnsForCompletedStreamAsync(
                session,
                stopping.Token
            );

        Assert.Null(recent);
        GalateaLiveTurn turn = new(
            "already durable",
            new GalateaTurnOptions("test")
        );
        turn.PublishDone(recent);
        using GalateaTurnSubscription replay = turn.Subscribe();
        GalateaSseFrame terminal = Assert.Single(replay.ReplayFrames);
        Assert.Equal("done", terminal.EventName);
        Assert.Contains(
            "\"recent\":null",
            Encoding.UTF8.GetString(terminal.Utf8.Span),
            StringComparison.Ordinal
        );
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

    private static async Task<GalateaLiveTurn> CompleteTurnAsync(
        HttpClient client,
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/chat/turns",
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
        return liveTurn;
    }

    private static async Task<RecentTurnsResponseDto> GetRecentAsync(
        HttpClient client
    ) {
        RecentTurnsResponseDto? recent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>(
                "/api/v1/recent-turns"
            );
        return Assert.IsType<RecentTurnsResponseDto>(recent);
    }

    private static async Task<HttpResponseMessage> PostPopAsync(
        HttpClient client,
        string token
    ) => await client.PostAsJsonAsync(
        "/api/v1/chat/turns/pop-latest",
        new PopLatestTurnRequestDto(token)
    );

    private static async Task<PopResult> PopLatestAsync(
        HttpClient client,
        string token
    ) {
        using HttpResponseMessage response = await PostPopAsync(
            client,
            token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType
        );
        byte[] receiptBytes = await response.Content
            .ReadAsByteArrayAsync();
        PopLatestTurnReceiptDto? receipt = JsonSerializer.Deserialize<
            PopLatestTurnReceiptDto
        >(receiptBytes, GalateaJson.Options);
        PopLatestTurnReceiptDto exactReceipt = Assert.IsType<
            PopLatestTurnReceiptDto
        >(receipt);
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(
                exactReceipt,
                GalateaJson.Options
            ),
            receiptBytes
        );
        return new PopResult(
            exactReceipt.PoppedUserText,
            await GetRecentAsync(client)
        );
    }

    private sealed record PopResult(
        string PoppedUserText,
        RecentTurnsResponseDto Recent
    );

    private static void AssertTurn(
        RecentTurnDto turn,
        string user,
        string assistant
    ) {
        Assert.Equal(user, turn.UserText);
        Assert.Equal(assistant, turn.Assistant.Text);
    }

    private static string FindRepositoryRoot() {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Atelia.sln"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root."
        );
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

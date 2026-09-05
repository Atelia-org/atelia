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

public sealed class GalateaMaintenanceModeTests {
    [Fact]
    public async Task MaintenanceMode_BlocksEveryChatPostBeforeWorkAndKeepsGetsReadable() {
        var factory = new TrackingCompletionClientFactory();
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            factory,
            normalizer,
            maintenanceMode: true,
            provisionRawOnly: false
        );
        EventAddress initialHead;
        using (SessionJournalEngine before =
               SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
            initialHead = Assert.IsType<EventAddress>(
                before.ReadCurrentHead()
            );
        }

        using HttpClient client = host.CreateClient();
        using HttpResponseMessage login =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        await AssertMaintenanceConflictAsync(
            await client.PostAsJsonAsync(
                "/api/v1/chat/turns",
                new ChatStreamRequest("must remain unconsumed", "test")
            )
        );
        await AssertMaintenanceConflictAsync(
            await client.PostAsJsonAsync(
                "/api/v1/chat/turns/resume",
                new ResumeTurnRequest(
                    EventAddressTextCodec.Format(initialHead),
                    "test"
                )
            )
        );
        await AssertMaintenanceConflictAsync(
            await client.PostAsJsonAsync(
                "/api/v1/mailbox/ready-turn",
                new ReadyReplyTurnRequest("test")
            )
        );
        await AssertMaintenanceConflictAsync(
            await client.PostAsJsonAsync(
                "/api/v1/chat/turns/pop-latest",
                new PopLatestTurnRequestDto(
                    EventAddressTextCodec.Format(initialHead)
                )
            )
        );
        await AssertMaintenanceConflictAsync(
            await client.PostAsync(
                "/api/v1/chat/turns/not-running/stop",
                content: null
            )
        );
        using var malformed = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/chat/turns"
        ) {
            Content = new StringContent(
                "{",
                Encoding.UTF8,
                "application/json"
            )
        };
        await AssertMaintenanceConflictAsync(
            await client.SendAsync(malformed)
        );

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.Client.DispatchCallCount);
        Assert.Equal(0, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);

        GalateaMeDto? me = await client.GetFromJsonAsync<GalateaMeDto>(
            "/api/v1/me"
        );
        Assert.NotNull(me);
        Assert.Equal("alice", me!.UserId);
        Assert.True(me.MaintenanceMode);

        GalateaMailboxStatusDto? mailboxStatus = await client
            .GetFromJsonAsync<GalateaMailboxStatusDto>(
                "/api/v1/mailbox/status"
            );
        Assert.NotNull(mailboxStatus);
        Assert.Equal("unavailable", mailboxStatus!.State);
        Assert.Equal("STORE_UNINITIALIZED", mailboxStatus.Code);
        Assert.Equal(0, mailboxStatus.QueuedCount);
        Assert.Equal(0, mailboxStatus.ReadyNoticeCount);

        RecentTurnsResponseDto? recent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>(
                "/api/v1/recent-turns"
            );
        Assert.NotNull(recent);
        Assert.Empty(recent!.Turns);
        RecapGridReadinessSnapshotDto recap = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("exact", recap.Freshness);
        Assert.Equal("unprovisioned", recap.State);
        Assert.Equal(
            EventAddressTextCodec.Format(initialHead),
            recap.ObservedRawHead
        );

        RecapCadenceProgressSnapshotDto? cadence = await client
            .GetFromJsonAsync<RecapCadenceProgressSnapshotDto>(
                "/api/v1/recap-cadence-progress"
            );
        Assert.NotNull(cadence);
        Assert.Equal("exact", cadence!.Freshness);
        Assert.Equal("unprovisioned", cadence.State);
        Assert.Equal(
            EventAddressTextCodec.Format(initialHead),
            cadence.ObservedRawHead
        );
        Assert.Null(cadence.RecentHistoryLoad);
        Assert.Null(cadence.RecapIntervalHistoryLoad);
        Assert.Null(cadence.MinimumRecentHistoryLoad);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.Client.DispatchCallCount);

        CurrentTurnDto? current = await client
            .GetFromJsonAsync<CurrentTurnDto>(
                "/api/v1/chat/turns/current"
            );
        Assert.NotNull(current);
        Assert.Equal("idle", current!.Status);
        Assert.Null(current.TurnId);
        Assert.Null(current.ConnectionId);
        Assert.False(current.RestartRequired);
        Assert.Null(current.RecoveryHead);

        string page = await client.GetStringAsync("/");
        Assert.Contains("维护模式：会话只读", page);
        Assert.Contains("maintenanceMode: true", page);
        Assert.Contains("id=\"message-input\"", page);
        Assert.Contains("id=\"recap-planning-status\"", page);
        Assert.Contains("HistoryLoad 不是模型 token 数", page);
        Assert.Contains("required disabled", page);
        Assert.Contains("id=\"send-button\" type=\"submit\" disabled", page);
        Assert.Contains(
            "id=\"mail-loop-enabled\" type=\"checkbox\" disabled",
            page
        );
        Assert.Contains(
            "页面打开时自动续接 Codex 回信，并在空闲 10 分钟后唤醒角色",
            page
        );
        Assert.Contains(
            "id=\"autonomy-state\" role=\"status\" aria-live=\"polite\">自主活动：未启用",
            page
        );
        Assert.Contains(
            "id=\"autonomy-countdown\" aria-live=\"off\"",
            page
        );
        Assert.Contains(
            "id=\"autonomy-last-activation\" aria-live=\"off\">上次自主激活：尚无",
            page
        );

        GalateaHostService hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        InvalidOperationException readOnly = Assert.Throws<
            InvalidOperationException
        >(() => session.Engine.ReconcileDesiredSetup(
            initialHead,
            new SessionDesiredSetup(
                "model-a",
                "openai-chat/strict",
                "test system prompt"
            )
        ));
        Assert.Contains("read-only", readOnly.Message);
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, normalizer.ShouldNormalizeCallCount);
    }

    private static async Task AssertMaintenanceConflictAsync(
        HttpResponseMessage response
    ) {
        using (response) {
            Assert.Equal(
                HttpStatusCode.ServiceUnavailable,
                response.StatusCode
            );
            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync()
            );
            Assert.Equal(
                "maintenance-mode",
                payload.RootElement.GetProperty("code").GetString()
            );
        }
    }

    private sealed class TrackingCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        internal TrackingCompletionClient Client { get; } = new();

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
            return Client;
        }
    }

    private sealed class TrackingCompletionClient : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-maintenance-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            throw new InvalidOperationException(
                "Maintenance mode must not dispatch Completion."
            );
        }
    }

    private sealed class TrackingNormalizer
        : IGalateaUserMessageNormalizer {
        private int _shouldNormalizeCallCount;
        private int _normalizeCallCount;

        internal int ShouldNormalizeCallCount => Volatile.Read(
            ref _shouldNormalizeCallCount
        );

        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            Interlocked.Increment(ref _shouldNormalizeCallCount);
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            Interlocked.Increment(ref _normalizeCallCount);
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(userMessage);
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaHttpV1ContractTests {
    [Fact]
    public async Task MailboxStatus_IsAuthenticatedNoStoreExactAndSessionFree() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();

        using (HttpResponseMessage anonymous = await client.GetAsync(
                   "/api/v1/mailbox/status")) {
            await AssertApiErrorAsync(
                anonymous,
                HttpStatusCode.Unauthorized,
                "authentication-required"
            );
        }
        _ = await GalateaTestHost.LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        Assert.Equal(0, GetSessionCount(service));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/mailbox/status"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "{\"state\":\"unavailable\",\"queuedCount\":0,"
                + "\"readyNoticeCount\":0,\"attemptCount\":0,"
                + "\"code\":\"STORE_UNINITIALIZED\","
                + "\"nextRetryAtUnixTimeMilliseconds\":null}",
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(0, GetSessionCount(service));
        Assert.False(Directory.Exists(host.DelegationStateDirectory));
    }

    [Fact]
    public async Task MailboxStatus_DoesNotAcquireSessionTurnLock() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        session.TurnLock.Wait();
        try {
            long revision = session.DelegationHandle!.Store
                .ReadSnapshot().StoreRevision;
            using HttpResponseMessage response = await client.GetAsync(
                "/api/v1/mailbox/status"
            ).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            GalateaMailboxStatusDto? status = await response.Content
                .ReadFromJsonAsync<GalateaMailboxStatusDto>();
            Assert.NotNull(status);
            Assert.Equal("no-mail", status!.State);
            Assert.Equal(
                revision,
                session.DelegationHandle.Store.ReadSnapshot().StoreRevision
            );
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task JsonBodyEndpoints_CarryPolicyMetadata() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await client.GetAsync("/api/v1/me");
        EndpointDataSource dataSource = host.Factory.Services
            .GetRequiredService<EndpointDataSource>();
        foreach (string route in new[] {
            "/api/v1/chat/turns",
            "/api/v1/mailbox/ready-turn",
        }) {
            RouteEndpoint endpoint = Assert.Single(
                dataSource.Endpoints.OfType<RouteEndpoint>(),
                endpoint => string.Equals(
                    endpoint.RoutePattern.RawText,
                    route,
                    StringComparison.Ordinal
                )
            );
            Assert.NotNull(endpoint.Metadata
                .GetMetadata<GalateaHttpV1.JsonBodyEndpointMetadata>());
            Assert.NotNull(endpoint.Metadata.GetMetadata<
                GalateaHttpV1.MaintenanceWriteEndpointMetadata>());
            Assert.Null(endpoint.Metadata
                .GetMetadata<IRequestSizeLimitMetadata>());
        }
    }

    [Fact]
    public async Task V1Cutover_ExposesOnlyVersionedApiSurface() {
        var completionFactory = new ZeroWorkCompletionClientFactory();
        var normalizer = new ZeroWorkNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            provisionRawOnly: false
        );
        Atelia.EventJournal.EventAddress? initialHead;
        using (SessionJournalEngine before =
               SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
            initialHead = before.ReadCurrentHead();
        }
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync(
            "/api/v1/me"
        );
        await AssertApiErrorAsync(
            anonymous,
            HttpStatusCode.Unauthorized,
            "authentication-required"
        );

        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(
            client
        );
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        string canonicalTurnId = new('a', 32);
        HttpRequestMessage[] retiredRequests = [
            new(HttpMethod.Get, "/api/me"),
            new(HttpMethod.Get, "/api/recent-turns"),
            new(HttpMethod.Get, "/api/recap-cadence-progress"),
            new(HttpMethod.Get, "/api/chat/turns/current"),
            new(HttpMethod.Get,
                $"/api/chat/turns/{canonicalTurnId}/events"),
            JsonRequest(HttpMethod.Post, "/api/chat/turns",
                "{\"message\":\"hello\"}"),
            JsonRequest(HttpMethod.Post, "/api/chat/turns/resume",
                "{\"expectedHead\":\"00000000:0000000000000000\"}"),
            JsonRequest(HttpMethod.Post, "/api/chat/turns/pop-latest",
                "{\"rewindLatestToken\":\"00000000:0000000000000000\"}"),
            new(HttpMethod.Post,
                $"/api/chat/turns/{canonicalTurnId}/stop"),
        ];
        foreach (HttpRequestMessage request in retiredRequests) {
            using (request) {
                using HttpResponseMessage retired = await client
                    .SendAsync(request);
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    retired.StatusCode
                );
            }
        }

        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        Assert.Equal(0, GetSessionCount(service));
        using (SessionJournalEngine after =
               SessionJournalEngine.OpenReadOnly(host.SessionDirectory)) {
            Assert.Equal(initialHead, after.ReadCurrentHead());
        }
    }

    [Fact]
    public async Task StartTurn_StrictJsonRejectsDuplicateProperties() {
        await using var host = GalateaTestHost.Create(
            new NoDispatchCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            provisionRawOnly: false
        );
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/chat/turns"
        ) {
            Content = new StringContent(
                "{\"message\":\"one\",\"message\":\"two\"}",
                Encoding.UTF8,
                "application/json"
            )
        };
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"code\":\"invalid-request\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Message\":\"hello\"}")]
    [InlineData("{\"message\":null}")]
    [InlineData("{\"message\":\"hello\",\"extra\":1}")]
    public async Task StartTurn_StrictJsonRejectsShapeMutations(
        string json
    ) {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage response = await PostRawAsync(
            client,
            json,
            "application/json"
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid-request"
        );
    }

    [Theory]
    [InlineData("{\"connectionId\":\"test\",\"connectionId\":\"other\"}")]
    [InlineData("{\"ConnectionId\":\"test\"}")]
    [InlineData("{\"connectionId\":1}")]
    [InlineData("{\"extra\":1}")]
    public async Task ReadyReplyTurn_StrictJsonRejectsShapeMutations(
        string json
    ) {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage response = await PostRawAsync(
            client,
            json,
            "application/json",
            "/api/v1/mailbox/ready-turn"
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid-request"
        );
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/problem+json")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; profile=v1")]
    public async Task JsonBody_RequiresExactApplicationJson(
        string contentType
    ) {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage response = await PostRawAsync(
            client,
            "{\"message\":\"hello\"}",
            contentType
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported-media-type"
        );
    }

    [Fact]
    public async Task AuthenticationPrecedesMediaAndBodyBinding() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await PostRawAsync(
            client,
            "not-json",
            "text/plain"
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.Unauthorized,
            "authentication-required"
        );
    }

    [Fact]
    public async Task JsonBody_RejectsContentEncoding() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/chat/turns"
        ) {
            Content = new StringContent(
                "{\"message\":\"hello\"}",
                Encoding.UTF8,
                "application/json"
            )
        };
        request.Content.Headers.ContentEncoding.Add("gzip");

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported-media-type"
        );
    }

    [Fact]
    public async Task KnownJsonBody_EmptyBodyIsInvalidRequest() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/chat/turns",
            content: null
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid-request"
        );
    }

    [Fact]
    public async Task UnknownLengthBody_OverOneMiBReturnsTyped413() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        byte[] oversized = Encoding.UTF8.GetBytes(
            "{\"message\":\""
            + new string('x', GalateaHttpV1.MaximumRequestBodyBytes)
            + "\"}"
        );
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/chat/turns"
        ) {
            Content = new UnknownLengthJsonContent(oversized)
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge,
            "request-too-large"
        );
    }

    [Fact]
    public async Task UnknownLengthBody_ExactMaximumPassesAndPlusOneFails() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage exact = await PostUnknownLengthAsync(
            client,
            BuildExactLengthJson(GalateaHttpV1.MaximumRequestBodyBytes)
        );
        await AssertApiErrorAsync(
            exact,
            HttpStatusCode.BadRequest,
            "invalid-message"
        );

        using HttpResponseMessage plusOne = await PostUnknownLengthAsync(
            client,
            BuildExactLengthJson(
                GalateaHttpV1.MaximumRequestBodyBytes + 1
            )
        );
        await AssertApiErrorAsync(
            plusOne,
            HttpStatusCode.RequestEntityTooLarge,
            "request-too-large"
        );
    }

    [Fact]
    public async Task KnownLengthBody_OverOneMiBReturnsTyped413() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage response = await PostRawAsync(
            client,
            "{\"message\":\""
            + new string('x', GalateaHttpV1.MaximumRequestBodyBytes)
            + "\"}",
            "application/json"
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge,
            "request-too-large"
        );
    }

    [Fact]
    public async Task CountingBodyStream_ProbesAtMostRemainingPlusOne() {
        byte[] bytes = new byte[
            GalateaHttpV1.MaximumRequestBodyBytes + 1
        ];
        var source = new RecordingReadStream(bytes);
        Stream bounded = GalateaHttpV1.CreateBoundedBodyStream(source);
        byte[] buffer = new byte[256 * 1024];

        await Assert.ThrowsAsync<RequestBodyLimitExceededException>(
            async () => {
                while (await bounded.ReadAsync(buffer) != 0) {
                }
            }
        );

        Assert.Equal(1, source.RequestSizes[^1]);
        Assert.Equal(
            GalateaHttpV1.MaximumRequestBodyBytes + 1,
            source.TotalBytesRead
        );
    }

    [Fact]
    public async Task OriginalMessage_Over64KiBIsRejectedBeforeRuntimeWork() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage response = await PostRawAsync(
            client,
            JsonSerializer.Serialize(new {
                message = new string(
                    'x',
                    GalateaHttpV1.MaximumMessageUtf8Bytes + 1
                )
            }),
            "application/json"
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.BadRequest,
            "invalid-message"
        );
    }

    [Fact]
    public async Task ConnectionId_RequiresNonblankOwnerBoundedText() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        foreach (string connectionId in new[] {
            " ",
            new string(
                'x',
                GalateaHttpV1.MaximumConnectionIdUtf8Bytes + 1
            ),
        }) {
            using HttpResponseMessage response = await PostRawAsync(
                client,
                JsonSerializer.Serialize(new {
                    message = "hello",
                    connectionId,
                }),
                "application/json"
            );
            await AssertApiErrorAsync(
                response,
                HttpStatusCode.BadRequest,
                "invalid-connection-id"
            );
        }
    }

    [Fact]
    public async Task PathIdentifiers_RequireCanonicalText() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage stop = await client.PostAsync(
            "/api/v1/chat/turns/NOT-A-TURN/stop",
            content: null
        );
        await AssertApiErrorAsync(
            stop,
            HttpStatusCode.BadRequest,
            "invalid-turn-id"
        );

        using HttpResponseMessage events = await client.GetAsync(
            "/api/v1/chat/turns/NOT-A-TURN/events"
        );
        await AssertApiErrorAsync(
            events,
            HttpStatusCode.BadRequest,
            "invalid-turn-id"
        );

        using HttpResponseMessage pop = await PostRawAsync(
            client,
            "{\"rewindLatestToken\":\"not-an-address\"}",
            "application/json",
            "/api/v1/chat/turns/pop-latest"
        );
        await AssertApiErrorAsync(
            pop,
            HttpStatusCode.BadRequest,
            "invalid-rewind-token"
        );
    }

    [Fact]
    public async Task CurrentResponse_HasExactFiveFieldEnvelope() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/chat/turns/current"
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()
        );
        Assert.Equal(
            [
                "connectionId",
                "recoveryHead",
                "restartRequired",
                "status",
                "turnId",
            ],
            body.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order()
                .ToArray()
        );
    }

    [Fact]
    public async Task Me_WhenAuthenticatedUserDisappearsReturnsTyped401() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var users = Assert.IsType<Dictionary<string, GalateaUserConfig>>(
            typeof(GalateaHostService)
                .GetField(
                    "_users",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                )!
                .GetValue(service)
        );
        Assert.True(users.Remove("alice"));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/me"
        );

        await AssertApiErrorAsync(
            response,
            HttpStatusCode.Unauthorized,
            "authentication-user-unknown"
        );
    }

    private static GalateaTestHost CreateHost() =>
        GalateaTestHost.Create(
            new NoDispatchCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            provisionRawOnly: false
        );

    private static HttpRequestMessage JsonRequest(
        HttpMethod method,
        string url,
        string json
    ) => new(method, url) {
        Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json"
        )
    };

    private static int GetSessionCount(GalateaHostService service) {
        object sessions = typeof(GalateaHostService)
            .GetField(
                "_sessions",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
            )!
            .GetValue(service)!;
        return Assert.IsType<int>(
            sessions.GetType().GetProperty("Count")!.GetValue(sessions)
        );
    }

    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string json,
        string contentType,
        string url = "/api/v1/chat/turns"
    ) {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            url
        ) {
            Content = new StringContent(json, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            contentType
        );
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage>
        PostUnknownLengthAsync(HttpClient client, byte[] body) {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/chat/turns"
        ) {
            Content = new UnknownLengthJsonContent(body)
        };
        return await client.SendAsync(request);
    }

    private static byte[] BuildExactLengthJson(int targetLength) {
        const string Prefix = "{\"message\":\"";
        const string Suffix = "\"}";
        int payloadLength = targetLength
            - Encoding.UTF8.GetByteCount(Prefix + Suffix);
        Assert.True(payloadLength > 0);
        byte[] body = Encoding.UTF8.GetBytes(
            Prefix + new string('x', payloadLength) + Suffix
        );
        Assert.Equal(targetLength, body.Length);
        return body;
    }

    private static async Task AssertApiErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code
    ) {
        using (response) {
            Assert.Equal(statusCode, response.StatusCode);
            Assert.Equal(
                "application/json",
                response.Content.Headers.ContentType?.MediaType
            );
            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync()
            );
            Assert.Equal(
                ["code", "error"],
                body.RootElement.EnumerateObject()
                    .Select(static property => property.Name)
                    .Order()
                    .ToArray()
            );
            Assert.Equal(
                code,
                body.RootElement.GetProperty("code").GetString()
            );
            Assert.False(string.IsNullOrWhiteSpace(
                body.RootElement.GetProperty("error").GetString()
            ));
        }
    }

    private sealed class NoDispatchCompletionClientFactory
        : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) =>
            throw new InvalidOperationException(
                "HTTP contract tests must not construct a completion client."
            );
    }

    private sealed class ZeroWorkCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "Retired routes must not create a completion client."
            );
        }
    }

    private sealed class ZeroWorkNormalizer
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
            Interlocked.Increment(ref _shouldNormalizeCallCount);
            return false;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult(userMessage);
        }
    }

    private sealed class UnknownLengthJsonContent : HttpContent {
        private readonly byte[] _bytes;

        internal UnknownLengthJsonContent(byte[] bytes) {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue(
                "application/json"
            );
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        ) => await stream.WriteAsync(_bytes);

        protected override bool TryComputeLength(out long length) {
            length = 0;
            return false;
        }
    }

    private sealed class RecordingReadStream(byte[] bytes) : Stream {
        private int _position;

        internal List<int> RequestSizes { get; } = [];
        internal int TotalBytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            RequestSizes.Add(count);
            int available = Math.Min(count, bytes.Length - _position);
            bytes.AsSpan(_position, available).CopyTo(
                buffer.AsSpan(offset, available)
            );
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            RequestSizes.Add(buffer.Length);
            int available = Math.Min(
                buffer.Length,
                bytes.Length - _position
            );
            bytes.AsMemory(_position, available).CopyTo(buffer);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

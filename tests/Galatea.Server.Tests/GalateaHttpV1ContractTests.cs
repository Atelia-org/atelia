using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaHttpV1ContractTests {
    [Fact]
    public async Task JsonBodyEndpoints_CarryPolicyMetadata() {
        await using var host = CreateHost();
        using HttpClient client = host.CreateClient();
        _ = await client.GetAsync("/api/v1/me");
        EndpointDataSource dataSource = host.Factory.Services
            .GetRequiredService<EndpointDataSource>();
        RouteEndpoint endpoint = Assert.Single(
            dataSource.Endpoints.OfType<RouteEndpoint>(),
            static endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                "/api/v1/chat/turns",
                StringComparison.Ordinal
            )
        );
        Assert.NotNull(endpoint.Metadata
            .GetMetadata<GalateaHttpV1.JsonBodyEndpointMetadata>());
        Assert.NotNull(endpoint.Metadata
            .GetMetadata<GalateaHttpV1.MaintenanceWriteEndpointMetadata>());
    }

    [Fact]
    public async Task V1Cutover_ExposesOnlyVersionedApiSurface() {
        await using var host = GalateaTestHost.Create(
            new NoDispatchCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            provisionRawOnly: false
        );
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

        using HttpResponseMessage current = await client.GetAsync(
            "/api/v1/chat/turns/current"
        );
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);

        string[] retiredRoutes = [
            "/api/recent-turns",
            "/api/chat/turns/current",
            "/api/chat/turns/not-a-turn/events",
        ];
        foreach (string route in retiredRoutes) {
            using HttpResponseMessage retired = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);
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

    private static GalateaTestHost CreateHost() =>
        GalateaTestHost.Create(
            new NoDispatchCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            provisionRawOnly: false
        );

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

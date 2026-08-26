using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.SessionJournal;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Atelia.Galatea.Server;

internal static class GalateaHttpV1 {
    internal const int MaximumRequestBodyBytes = 1024 * 1024;
    internal const int MaximumMessageUtf8Bytes = 64 * 1024;
    internal const int MaximumConnectionIdUtf8Bytes = 128;

    internal static readonly JsonBodyEndpointMetadata JsonBody = new();
    internal static readonly MaintenanceWriteEndpointMetadata
        MaintenanceWrite = new();

    internal static void ConfigureJson(JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.RespectRequiredConstructorParameters = true;
        options.RespectNullableAnnotations = true;
        options.AllowDuplicateProperties = false;
    }

    internal static bool HasJsonBody(HttpContext context) =>
        context.GetEndpoint()?.Metadata
            .GetMetadata<JsonBodyEndpointMetadata>() is not null;

    internal static bool IsMaintenanceWrite(HttpContext context) =>
        context.GetEndpoint()?.Metadata
            .GetMetadata<MaintenanceWriteEndpointMetadata>() is not null;

    internal static bool IsExactJsonContentType(string? contentType) {
        if (!MediaTypeHeaderValue.TryParse(
                contentType,
                out MediaTypeHeaderValue? parsed
            )
            || !string.Equals(
                parsed.MediaType.Value,
                "application/json",
                StringComparison.OrdinalIgnoreCase
            )) {
            return false;
        }

        bool sawCharset = false;
        foreach (NameValueHeaderValue parameter in parsed.Parameters) {
            if (!string.Equals(
                    parameter.Name.Value,
                    "charset",
                    StringComparison.OrdinalIgnoreCase
                )
                || sawCharset) {
                return false;
            }
            sawCharset = true;
            string? parameterValue = parameter.Value.Value;
            if (parameterValue is null) {
                return false;
            }
            string charset = parameterValue.Trim('"');
            if (!string.Equals(
                    charset,
                    "utf-8",
                    StringComparison.OrdinalIgnoreCase
                )) {
                return false;
            }
        }
        return true;
    }

    internal static Stream CreateBoundedBodyStream(Stream inner) =>
        new CountingReadStream(inner, MaximumRequestBodyBytes);

    internal static async ValueTask<T> ReadJsonBodyAsync<T>(
        HttpContext context
    ) where T : notnull {
        ArgumentNullException.ThrowIfNull(context);
        JsonSerializerOptions options = context.RequestServices
            .GetRequiredService<IOptions<
                Microsoft.AspNetCore.Http.Json.JsonOptions
            >>()
            .Value
            .SerializerOptions;
        T? value = await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                options,
                context.RequestAborted
            )
            .ConfigureAwait(false);
        return value ?? throw new JsonException(
            "Request JSON must not be null."
        );
    }

    internal static bool IsCanonicalTurnId(string? value) =>
        value is { Length: 32 }
        && value.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
        );

    internal static bool TryParseCanonicalEventAddress(
        string? value,
        out Atelia.EventJournal.EventAddress address
    ) {
        if (value is not null
            && EventAddressTextCodec.TryParse(value, out address)
            && string.Equals(
                value,
                EventAddressTextCodec.Format(address),
                StringComparison.Ordinal
            )) {
            return true;
        }
        address = default;
        return false;
    }

    internal static string? ValidateMessage(string? message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return "message must not be blank.";
        }
        try {
            if (GalateaBoundedJson.StrictUtf8.GetByteCount(message)
                > MaximumMessageUtf8Bytes) {
                return "message exceeds the 64 KiB UTF-8 limit.";
            }
        }
        catch (EncoderFallbackException) {
            return "message must contain valid Unicode.";
        }
        return null;
    }

    internal static string? ValidateConnectionId(string? connectionId) {
        if (connectionId is null) {
            return null;
        }
        if (string.IsNullOrWhiteSpace(connectionId)) {
            return "connectionId must not be blank.";
        }
        try {
            if (GalateaBoundedJson.StrictUtf8.GetByteCount(connectionId)
                > MaximumConnectionIdUtf8Bytes) {
                return "connectionId exceeds its 128-byte UTF-8 limit.";
            }
        }
        catch (EncoderFallbackException) {
            return "connectionId must contain valid Unicode.";
        }
        return null;
    }

    internal static string? ValidateMailboxText(
        string? value,
        string fieldName,
        int maximumUtf8Bytes,
        bool allowNull = false,
        bool allowLineBreaks = true
    ) {
        if (value is null && allowNull) { return null; }
        if (string.IsNullOrWhiteSpace(value)) {
            return $"{fieldName} must not be blank.";
        }
        try {
            if (GalateaBoundedJson.StrictUtf8.GetByteCount(value)
                    > maximumUtf8Bytes) {
                return $"{fieldName} exceeds its UTF-8 byte limit.";
            }
            if (!allowLineBreaks
                && GalateaMailboxText.ContainsHeaderLineBreak(value)) {
                return $"{fieldName} must be single-line text.";
            }
            _ = System.Xml.XmlConvert.VerifyXmlChars(value);
        }
        catch (Exception exception) when (exception is
            EncoderFallbackException or System.Xml.XmlException) {
            return $"{fieldName} must contain valid Unicode.";
        }
        return null;
    }

    internal sealed class JsonBodyEndpointMetadata;

    internal sealed class MaintenanceWriteEndpointMetadata;

    private sealed class CountingReadStream(
        Stream inner,
        long maximumBytes
    ) : Stream {
        private long _readBytes;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => _readBytes;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            int boundedCount = BoundReadCount(count);
            int read = inner.Read(buffer, offset, boundedCount);
            Account(read);
            return read;
        }

        public override int Read(Span<byte> buffer) {
            int boundedCount = BoundReadCount(buffer.Length);
            int read = inner.Read(buffer[..boundedCount]);
            Account(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            int boundedCount = BoundReadCount(buffer.Length);
            int read = await inner.ReadAsync(
                    buffer[..boundedCount],
                    cancellationToken
                )
                .ConfigureAwait(false);
            Account(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) {
            int boundedCount = BoundReadCount(count);
            int read = await inner.ReadAsync(
                    buffer,
                    offset,
                    boundedCount,
                    cancellationToken
                )
                .ConfigureAwait(false);
            Account(read);
            return read;
        }

        public override int ReadByte() {
            Span<byte> one = stackalloc byte[1];
            int read = Read(one);
            return read == 0 ? -1 : one[0];
        }

        private int BoundReadCount(int requested) {
            long remaining = maximumBytes - _readBytes;
            if (remaining < 0) {
                throw new RequestBodyLimitExceededException();
            }
            long probe = Math.Min(remaining + 1, requested);
            return checked((int)probe);
        }

        private void Account(int read) {
            _readBytes += read;
            if (_readBytes > maximumBytes) {
                throw new RequestBodyLimitExceededException();
            }
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

internal sealed class RequestBodyLimitExceededException : IOException;

internal sealed record ApiErrorDto(string Code, string Error);

internal sealed record TurnBusyErrorDto(
    string Code,
    string Error,
    string? TurnId
);

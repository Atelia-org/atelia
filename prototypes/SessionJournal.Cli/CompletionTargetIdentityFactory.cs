using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class CompletionTargetIdentityFactory {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static SJ.SessionCompletionTargetIdentity Create(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        return new SJ.SessionCompletionTargetIdentity(
            connection.Id,
            connection.Kind,
            ComputeConnectionFingerprint(connection),
            ComputeRequestAdapterFingerprint(client, connection)
        );
    }

    internal static string ComputeConnectionFingerprint(
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(
            new ConnectionFingerprintDto(
                connection.Id,
                connection.Kind,
                connection.ModelId,
                connection.CompletionSurfaceId,
                connection.BaseAddress,
                connection.MaxTokens
            )
        );
    }

    internal static string ComputeRequestAdapterFingerprint(
        ICompletionClient client,
        CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(connection);
        return ComputeFingerprint(
            new RequestAdapterFingerprintDto(
                client.Name,
                client.ApiSpecId,
                connection.Kind,
                connection.CompletionSurfaceId
            )
        );
    }

    private static string ComputeFingerprint<T>(T value) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions
        );
        return $"sha256:{Convert.ToHexStringLower(
            SHA256.HashData(bytes)
        )}";
    }

    private sealed record ConnectionFingerprintDto(
        string ConnectionId,
        string Kind,
        string ModelId,
        string CompletionSurfaceId,
        string BaseAddress,
        int? MaxTokens
    );

    private sealed record RequestAdapterFingerprintDto(
        string ClientName,
        string ClientApiSpecId,
        string ConnectionKind,
        string CompletionSurfaceId
    );
}

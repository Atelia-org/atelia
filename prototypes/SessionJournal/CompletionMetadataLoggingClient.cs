using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.SessionJournal;

/// <summary>
/// Best-effort call diagnostics containing identities, sizes and hashes only.
/// Request projections and provider output are inspected in memory and never
/// written to the log. This wrapper does not own the inner client and its call
/// ID is diagnostic correlation, not a durable dispatch identity.
/// </summary>
public sealed class CompletionMetadataLoggingClient : ICompletionClient {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ICompletionClient _inner;
    private readonly string _directory;
    private readonly string _command;
    private readonly string _connectionId;

    public CompletionMetadataLoggingClient(
        ICompletionClient inner,
        string connectionId,
        string directory,
        string command
    ) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _connectionId = connectionId;
        _directory = directory;
        _command = command;
    }

    public string Name => _inner.Name;
    public string ApiSpecId => _inner.ApiSpecId;

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => InvokeAsync(request, null, observer, cancellationToken);

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();
        return InvokeAsync(request, invocationOptions, observer, cancellationToken);
    }

    private async Task<CompletionResult> InvokeAsync(
        CompletionRequest request,
        CompletionInvocationOptions? options,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        string callId = Guid.NewGuid().ToString("N");
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        ContentFingerprint requestFingerprint = Fingerprint(
            () => CanonicalizeRequest(request),
            "completion-metadata-request-json-v1"
        );
        var stopwatch = Stopwatch.StartNew();
        CompletionResult? result = null;
        Exception? failure = null;
        try {
            result = options is null
                ? await _inner.StreamCompletionAsync(request, observer, cancellationToken).ConfigureAwait(false)
                : await _inner.StreamCompletionAsync(request, options, observer, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) {
            failure = exception;
            throw;
        }
        finally {
            stopwatch.Stop();
            TryWrite(callId, startedAt, stopwatch.ElapsedMilliseconds, request, requestFingerprint, options, result, failure);
        }
    }

    private void TryWrite(
        string callId,
        DateTimeOffset startedAt,
        long elapsedMs,
        CompletionRequest request,
        ContentFingerprint requestFingerprint,
        CompletionInvocationOptions? options,
        CompletionResult? result,
        Exception? failure
    ) {
        string? temporaryPath = null;
        try {
            var entry = new {
                schema = "atelia.completion.metadata-log.v1",
                callId,
                timestampUtc = startedAt,
                elapsedMs,
                connection = new { id = _connectionId, providerId = Name, apiSpecId = ApiSpecId, modelId = request.ModelId },
                context = new { command = _command },
                request = new {
                    // Diagnostic identity covers prefix, typed tail and output
                    // policy. It is not a durable SessionJournal commitment or
                    // a claim about native provider HTTP bytes.
                    content = requestFingerprint,
                    sharedMessageCount = request.PromptPrefix.SharedContextMessages.Length,
                    tailMessageCount = request.TailMessages.Length,
                    toolCount = request.PromptPrefix.OutputContract.Tools.Length
                },
                promptCacheReuseHint = (options ?? CompletionInvocationOptions.Default).PromptCacheReuseHint.ToString(),
                response = result is null ? null : new {
                    invocation = new {
                        result.Invocation.ProviderId,
                        result.Invocation.ApiSpecId,
                        result.Invocation.Model
                    },
                    termination = result.Termination.Kind.ToString(),
                    errorCount = result.Errors?.Count ?? 0,
                    content = Fingerprint(() => CanonicalizeAction(result.Message), "session-history-message-json-v2")
                },
                status = failure is OperationCanceledException ? "cancelled"
                    : failure is not null ? "exception"
                    : result!.Termination.Kind.ToString(),
                // Exception messages, stacks, provider reasons, details and stream
                // errors can echo prompts. Only the runtime type is admitted.
                exceptionType = failure?.GetType().FullName
            };
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, callId + ".json");
            temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, entry, JsonOptions);
            }
            File.Move(temporaryPath, path);
        }
        catch (Exception exception) {
            // Diagnostics must never replace a result, cancellation or provider
            // failure. Avoid exception text even on this secondary log path.
            try {
                DebugUtil.Warning("CompletionMetadataLogging", $"Call metadata write failed ({exception.GetType().FullName}).");
            }
            catch { }
        }
        finally {
            if (temporaryPath is not null) {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private static ContentFingerprint Fingerprint(Func<byte[]> serialize, string format) {
        try {
            byte[] bytes = serialize();
            return new(format, bytes.Length, SessionRequestCanonicalizer.Sha256Hex(bytes), null);
        }
        catch (Exception exception) {
            // No full-text fallback and no new restrictions on legal requests.
            return new(format, null, null, exception.GetType().FullName);
        }
    }

    private static byte[] CanonicalizeAction(ActionMessage message) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, SessionRequestCanonicalizer.WriterOptions)) {
            SessionRequestCanonicalizer.WriteHistoryMessage(writer, message);
        }
        return buffer.WrittenMemory.ToArray();
    }

    internal static byte[] CanonicalizeRequest(CompletionRequest request) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, SessionRequestCanonicalizer.WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteString("modelId", request.ModelId);
            writer.WriteString("systemPrompt", request.PromptPrefix.SystemPrompt);
            writer.WriteStartArray("sharedContext");
            foreach (IHistoryMessage message in request.PromptPrefix.SharedContextMessages) {
                SessionRequestCanonicalizer.WriteHistoryMessage(writer, message);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("tail");
            foreach (IHistoryMessage message in request.TailMessages) {
                SessionRequestCanonicalizer.WriteHistoryMessage(writer, message);
            }
            writer.WriteEndArray();
            CompletionOutputContract output = request.PromptPrefix.OutputContract;
            writer.WriteString("toolChoice", output.ToolChoice.Kind.ToString());
            writer.WriteString("requiredToolName", output.ToolChoice.RequiredToolName);
            if (output.AllowParallelToolCalls is bool parallel) {
                writer.WriteBoolean("allowParallelToolCalls", parallel);
            }
            else {
                writer.WriteNull("allowParallelToolCalls");
            }
            writer.WriteStartArray("tools");
            foreach (ToolDefinition tool in output.Tools) {
                SessionRequestCanonicalizer.WriteToolDefinition(writer, tool);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private sealed record ContentFingerprint(string Format, int? Utf8Bytes, string? Sha256, string? UnavailableReason);
}

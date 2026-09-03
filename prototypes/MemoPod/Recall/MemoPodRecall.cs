using System.Collections.Immutable;
using System.Net.Http;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.MemoPod;

public sealed partial class MemoPod {
    public async Task<MemoRecallResult> RecallAsync(
        ICompletionClient completionClient,
        string modelId,
        string query,
        MemoRecallOptions options,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfInvalidated();
        RequirePhase(MemoPodPhase.Frozen, nameof(RecallAsync));
        ArgumentNullException.ThrowIfNull(completionClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        MemoPodRecallValidation.RequireQuery(query);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        MemoPodFrozenPrompt frozenPrompt = _frozenPrompt
            ?? throw new InvalidOperationException(
                "The Frozen MemoPod has no cached prompt."
            );
        RequireSameFrozenEpoch(frozenPrompt);
        if (frozenPrompt.Utf8Length
            > options.MaximumFrozenPromptUtf8Bytes) {
            throw MemoPodRecallValidation.LocalLimit(
                $"MemoPod frozen prompt exceeds the configured {options.MaximumFrozenPromptUtf8Bytes} UTF-8 byte recall limit."
            );
        }

        string queryTail = MemoPodRecallQueryRenderer.Render(
            query,
            options.MaxResults
        );
        var request = new CompletionRequest(
            modelId,
            new CompletionPromptPrefix(
                MemoPodRecallProtocol.SystemPrompt,
                MemoPodRecallProtocol.OutputContract,
                [frozenPrompt.ToHistoryMessage()]
            ),
            [new ObservationMessage(queryTail)]
        );

        CompletionResult completionResult;
        try {
            completionResult = await completionClient.StreamCompletionAsync(
                request,
                MemoPodRecallProtocol.InvocationOptions,
                observer: null,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (OperationCanceledException exception) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider canceled without caller cancellation.",
                exception
            );
        }
        catch (HttpRequestException exception) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider transport failed.",
                exception
            );
        }
        catch (IOException exception) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider I/O failed.",
                exception
            );
        }
        catch (JsonException exception) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider protocol failed.",
                exception
            );
        }
        catch (TimeoutException exception) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider timed out.",
                exception
            );
        }
        catch (NotSupportedException exception) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider does not support the required invocation contract.",
                exception
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (completionResult is null) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider returned no completion result."
            );
        }
        CompletionDescriptor expectedInvocation = CompletionDescriptor.From(
            completionClient,
            request
        );
        if (completionResult.Invocation != expectedInvocation) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall completion invocation identity did not match the request."
            );
        }
        if (completionResult.Termination.Kind
            is not CompletionTerminationKind.Completed) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider did not complete successfully."
            );
        }
        if (completionResult.Errors is { Count: > 0 }) {
            throw MemoPodRecallValidation.ProviderFailure(
                "MemoPod recall provider reported completion errors."
            );
        }
        if (completionResult.Message.Blocks.Count != 1
            || completionResult.Message.Blocks[0]
                is not ActionBlock.ToolCall toolCall
            || toolCall.Call is null) {
            throw MemoPodRecallValidation.InvalidOutput(
                "MemoPod recall output must contain exactly one tool-call block."
            );
        }
        if (!string.Equals(
                toolCall.Call.ToolName,
                MemoPodRecallProtocol.ToolName,
                StringComparison.Ordinal
            )) {
            throw MemoPodRecallValidation.InvalidOutput(
                $"MemoPod recall output must call '{MemoPodRecallProtocol.ToolName}'."
            );
        }
        MemoPodRecallValidation.RequireToolCallId(
            toolCall.Call.ToolCallId
        );

        ImmutableArray<MemoId> ids = MemoPodRecallValidation.ParseMemoIds(
            toolCall.Call.RawArgumentsJson,
            options.MaxResults
        );
        RequireSameFrozenEpoch(frozenPrompt);

        var memos = ImmutableArray.CreateBuilder<Memo>(ids.Length);
        long hydratedUtf8Bytes = 0;
        foreach (MemoId id in ids) {
            if (!_working.TryGet(id, out Memo? memo) || memo is null) {
                throw MemoPodRecallValidation.InvalidOutput(
                    $"MemoPod recall output selected unknown or inactive MemoId '{id}'."
                );
            }
            hydratedUtf8Bytes = checked(
                hydratedUtf8Bytes + memo.ExactTextUtf8ByteCount
            );
            if (hydratedUtf8Bytes
                > options.MaximumHydratedExactTextUtf8Bytes) {
                throw MemoPodRecallValidation.LocalLimit(
                    $"Hydrated Memo exact text exceeds the configured {options.MaximumHydratedExactTextUtf8Bytes} UTF-8 byte recall limit."
                );
            }
            memos.Add(memo);
        }

        RequireSameFrozenEpoch(frozenPrompt);
        return new MemoRecallResult(
            memos.MoveToImmutable(),
            frozenPrompt.Sha256,
            MemoPodRecallValidation.SanitizeUsage(completionResult.Usage)
        );
    }

    private void RequireSameFrozenEpoch(MemoPodFrozenPrompt frozenPrompt) {
        ThrowIfInvalidated();
        if (_phase is not MemoPodPhase.Frozen
            || !ReferenceEquals(_frozenPrompt, frozenPrompt)) {
            throw new InvalidOperationException(
                "The MemoPod Frozen epoch changed during recall."
            );
        }
    }
}

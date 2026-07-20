using Atelia.ChatSession;
using Atelia.ChatSession.Memory;

namespace ChatSessionBacktestCli;

internal sealed record AutobiographicalCompressionBacktestRecord(
    string Schema,
    string ConnectionId,
    string InputPath,
    int BeforeTokens,
    int? AfterTokens,
    int TargetTokens,
    double? ActualCompressionPercent,
    bool? TargetReached,
    bool? FinalPassagePreserved,
    int EditCount,
    int ToolCallsExecuted,
    MemoryBlockPreview OldBlock,
    MemoryBlockPreview? NewBlock,
    string? NewText,
    IReadOnlyList<string> CallLogPaths,
    string Status,
    string? ExceptionType,
    string? ExceptionMessage,
    IReadOnlyList<string>? Diagnostics,
    IReadOnlyList<string>? Errors
) {
    public static AutobiographicalCompressionBacktestRecord Create(
        string connectionId,
        string inputPath,
        int targetTokens,
        string oldText,
        MemoryBlockMaintenanceResult? result,
        IReadOnlyList<string> callLogPaths,
        Exception? exception
    ) {
        string? newText = result?.NewBlock.Text;
        int beforeTokens = MemoryDocumentTokenEstimator.Estimate(oldText);
        int? afterTokens = newText is null ? null : MemoryDocumentTokenEstimator.Estimate(newText);
        double? actualCompressionPercent = afterTokens is null || beforeTokens == 0
            ? null
            : (beforeTokens - afterTokens.Value) * 100d / beforeTokens;

        return new AutobiographicalCompressionBacktestRecord(
            Schema: "atelia.chat-session.autobiographical-compression-backtest.v1",
            ConnectionId: connectionId,
            InputPath: Path.GetFullPath(inputPath),
            BeforeTokens: beforeTokens,
            AfterTokens: afterTokens,
            TargetTokens: targetTokens,
            ActualCompressionPercent: actualCompressionPercent,
            TargetReached: afterTokens is null ? null : afterTokens <= targetTokens,
            FinalPassagePreserved: newText is null ? null : GetFinalBlock(oldText) == GetFinalBlock(newText),
            EditCount: GetDiagnosticInt(result?.Diagnostics, "editCount"),
            ToolCallsExecuted: result?.ToolCallsExecuted ?? 0,
            OldBlock: BacktestOutputUtil.CreateBlockPreview(oldText)!,
            NewBlock: BacktestOutputUtil.CreateBlockPreview(newText),
            NewText: newText,
            CallLogPaths: callLogPaths,
            Status: exception is null ? "succeeded" : "failed",
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception?.Message,
            Diagnostics: result?.Diagnostics,
            Errors: result?.Errors
        );
    }

    private static int GetDiagnosticInt(IReadOnlyList<string>? diagnostics, string key) {
        var prefix = key + "=";
        var value = diagnostics?.FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
        return value is not null && int.TryParse(value[prefix.Length..], out var parsed) ? parsed : 0;
    }

    private static string GetFinalBlock(string text) {
        var document = new MemoryDocumentEditingSession(text).WorkingDocument;
        return document.Blocks.Count == 0 ? string.Empty : document.Blocks[^1].Content;
    }
}

// Source: Atelia.StateJournal - MetaRecordReader
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.2

using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// Meta 记录解析结果。
/// </summary>
/// <param name="Address">帧在文件中的地址。</param>
/// <param name="Record">解析后的 MetaCommitRecord。</param>
/// <param name="Status">帧状态（Valid/Tombstone）。</param>
public readonly record struct ParsedMetaRecord(
    <deleted-place-holder> Address,
    MetaCommitRecord Record,
    FrameStatus Status);

/// <summary>
/// Meta 文件记录读取器。封装 IRbfScanner，提供语义化的读取 API。
/// </summary>
/// <remarks>
/// <para>用于从 Meta RBF 文件读取 MetaCommitRecord。</para>
/// <para><b>设计</b>：</para>
/// <list type="bullet">
///   <item>封装 RBF 层扫描细节（FrameTag 过滤、Payload 解析）</item>
///   <item>提供语义化 API：ScanReverse 逆向扫描、TryReadAt 随机读取</item>
///   <item>过滤非 Meta FrameTag 的帧</item>
///   <item>Tombstone 帧会产出（Status=Tombstone）</item>
/// </list>
/// </remarks>
public sealed class MetaRecordReader {
    private readonly IRbfScanner _scanner;

    /// <summary>
    /// 创建 MetaRecordReader。
    /// </summary>
    /// <param name="scanner">RBF 帧扫描器。</param>
    public MetaRecordReader(IRbfScanner scanner) {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
    }

    /// <summary>
    /// 逆向扫描所有有效 MetaCommitRecord。
    /// </summary>
    /// <returns>解析后的 MetaCommitRecord 枚举（从尾到头）。</returns>
    /// <remarks>
    /// <para>过滤规则：</para>
    /// <list type="bullet">
    ///   <item>非 MetaCommit FrameTag 的帧被跳过</item>
    ///   <item>Payload 解析失败的帧被跳过</item>
    ///   <item>Tombstone 帧会产出（Status=Tombstone）</item>
    /// </list>
    /// </remarks>
    public IEnumerable<ParsedMetaRecord> ScanReverse() {
        foreach (var frame in _scanner.ScanReverse()) {
            // 过滤非 Meta FrameTag
            var frameTag = new FrameTag(frame.FrameTag);
            if (!FrameTags.IsMetaFrameTag(frameTag)) { continue; }

            // 读取并解析 payload
            var payload = _scanner.ReadPayload(frame);
            var parseResult = MetaCommitRecordSerializer.TryRead(payload);

            // 过滤解析失败的帧
            if (parseResult.IsFailure) { continue; }

            // 产出有效记录（包括 Tombstone）
            yield return new ParsedMetaRecord(
                Address: <deleted-place-holder>.FromOffset(frame.FileOffset),
                Record: parseResult.Value,
                Status: frame.Status
            );
        }
    }

    /// <summary>
    /// 随机读取指定地址的 MetaCommitRecord。
    /// </summary>
    /// <param name="address">帧起始地址。</param>
    /// <returns>成功时返回 ParsedMetaRecord；失败时返回错误。</returns>
    /// <remarks>
    /// <para>错误情况：</para>
    /// <list type="bullet">
    ///   <item>地址无效或帧读取失败</item>
    ///   <item>FrameTag 不是 MetaCommit</item>
    ///   <item>Payload 解析失败</item>
    /// </list>
    /// </remarks>
    public AteliaResult<ParsedMetaRecord> TryReadAt(<deleted-place-holder> address) {
        // 尝试读取帧
        if (!_scanner.TryReadAt(address, out var frame)) {
            return AteliaResult<ParsedMetaRecord>.Failure(
                new MetaRecordReadError(address, "Failed to read frame at address.")
            );
        }

        // 验证 FrameTag
        var frameTag = new FrameTag(frame.FrameTag);
        if (!FrameTags.IsMetaFrameTag(frameTag)) {
            return AteliaResult<ParsedMetaRecord>.Failure(
                new MetaRecordFrameTagMismatchError(address, frame.FrameTag)
            );
        }

        // 读取并解析 payload
        var payload = _scanner.ReadPayload(frame);
        var parseResult = MetaCommitRecordSerializer.TryRead(payload);

        if (parseResult.IsFailure) {
            return AteliaResult<ParsedMetaRecord>.Failure(
                new MetaRecordParseError(address, parseResult.Error!)
            );
        }

        return AteliaResult<ParsedMetaRecord>.Success(
            new ParsedMetaRecord(
                Address: address,
                Record: parseResult.Value,
                Status: frame.Status
            )
        );
    }
}

// ============================================================================
// MetaRecordReader Errors
// ============================================================================

/// <summary>
/// Meta 记录读取错误基类。
/// </summary>
public abstract record MetaRecordReaderError(
    string ErrorCode,
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : StateJournalError(ErrorCode, Message, RecoveryHint, Details, Cause);

/// <summary>
/// Meta 记录读取失败错误（帧级别）。
/// </summary>
public sealed record MetaRecordReadError(
    <deleted-place-holder> Address,
    string Reason
) : MetaRecordReaderError(
    "StateJournal.MetaRecordReader.ReadFailed",
    $"Failed to read MetaCommitRecord at address 0x{Address.Value:X16}: {Reason}",
    "The address may be invalid or the frame may be corrupted.",
    new Dictionary<string, string> {
        ["Address"] = $"0x{Address.Value:X16}"
    });

/// <summary>
/// Meta 记录 FrameTag 不匹配错误。
/// </summary>
public sealed record MetaRecordFrameTagMismatchError(
    <deleted-place-holder> Address,
    uint ActualFrameTag
) : MetaRecordReaderError(
    "StateJournal.MetaRecordReader.FrameTagMismatch",
    $"Frame at address 0x{Address.Value:X16} has FrameTag 0x{ActualFrameTag:X8}, expected MetaCommit (0x{FrameTags.MetaCommit.Value:X8}).",
    "The address may point to a different record type.",
    new Dictionary<string, string> {
        ["Address"] = $"0x{Address.Value:X16}",
        ["ActualFrameTag"] = $"0x{ActualFrameTag:X8}",
        ["ExpectedFrameTag"] = $"0x{FrameTags.MetaCommit.Value:X8}"
    });

/// <summary>
/// Meta 记录 Payload 解析错误。
/// </summary>
public sealed record MetaRecordParseError(
    <deleted-place-holder> Address,
    AteliaError Cause
) : MetaRecordReaderError(
    "StateJournal.MetaRecordReader.ParseFailed",
    $"Failed to parse MetaCommitRecord payload at address 0x{Address.Value:X16}.",
    "The payload may be corrupted or truncated.",
    new Dictionary<string, string> {
        ["Address"] = $"0x{Address.Value:X16}"
    },
    Cause);

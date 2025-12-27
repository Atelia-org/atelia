// Source: Atelia.StateJournal - DataRecordReader
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.5

using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// Data 记录解析结果。
/// </summary>
/// <param name="Address">帧在文件中的地址。</param>
/// <param name="Kind">对象类型（Dict/Array，从 FrameTag 解析）。</param>
/// <param name="PrevVersionPtr">上一版本指针（0 表示 Base Version）。</param>
/// <param name="DiffPayload">Diff 数据（Memory 切片，不复制）。</param>
/// <param name="Status">帧状态（Valid/Tombstone）。</param>
public readonly record struct ParsedDataRecord(
    Address64 Address,
    ObjectKind Kind,
    ulong PrevVersionPtr,
    ReadOnlyMemory<byte> DiffPayload,
    FrameStatus Status) {
    /// <summary>
    /// 是否为 Base Version（Genesis Base 或 Checkpoint Base）。
    /// </summary>
    public bool IsBaseVersion => ObjectVersionRecord.IsBaseVersion(PrevVersionPtr);
}

/// <summary>
/// Data 文件记录读取器。封装 IRbfScanner，提供语义化的读取 API。
/// </summary>
/// <remarks>
/// <para>用于从 Data RBF 文件读取 ObjectVersionRecord。</para>
/// <para><b>设计</b>：</para>
/// <list type="bullet">
///   <item>封装 RBF 层扫描细节（FrameTag 过滤、Payload 解析）</item>
///   <item>提供语义化 API：ScanReverse 逆向扫描、TryReadAt 随机读取</item>
///   <item>过滤非 ObjectVersion FrameTag 的帧</item>
///   <item>从 FrameTag 提取 ObjectKind</item>
///   <item>Tombstone 帧会产出（Status=Tombstone）</item>
/// </list>
/// </remarks>
public sealed class DataRecordReader {
    private readonly IRbfScanner _scanner;

    /// <summary>
    /// 创建 DataRecordReader。
    /// </summary>
    /// <param name="scanner">RBF 帧扫描器。</param>
    public DataRecordReader(IRbfScanner scanner) {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
    }

    /// <summary>
    /// 逆向扫描所有有效 ObjectVersionRecord。
    /// </summary>
    /// <returns>解析后的 ObjectVersionRecord 枚举（从尾到头）。</returns>
    /// <remarks>
    /// <para>过滤规则：</para>
    /// <list type="bullet">
    ///   <item>非 ObjectVersion FrameTag 的帧被跳过</item>
    ///   <item>Payload 解析失败的帧被跳过</item>
    ///   <item>Tombstone 帧会产出（Status=Tombstone）</item>
    /// </list>
    /// </remarks>
    public IEnumerable<ParsedDataRecord> ScanReverse() {
        foreach (var frame in _scanner.ScanReverse()) {
            // 过滤非 Data FrameTag
            var frameTag = new FrameTag(frame.FrameTag);
            if (!FrameTags.IsDataFrameTag(frameTag)) { continue; }

            // 从 FrameTag 提取 ObjectKind
            var kind = frameTag.GetObjectKind();

            // 读取并解析 payload
            var payload = _scanner.ReadPayload(frame);
            var parseResult = ObjectVersionRecord.TryParse(payload);

            // 过滤解析失败的帧
            if (parseResult.IsFailure) { continue; }

            var parsed = parseResult.Value;

            // 产出有效记录（包括 Tombstone）
            yield return new ParsedDataRecord(
                Address: Address64.FromOffset(frame.FileOffset),
                Kind: kind,
                PrevVersionPtr: parsed.PrevVersionPtr,
                DiffPayload: parsed.DiffPayload,
                Status: frame.Status
            );
        }
    }

    /// <summary>
    /// 随机读取指定地址的 ObjectVersionRecord。
    /// </summary>
    /// <param name="address">帧起始地址。</param>
    /// <returns>成功时返回 ParsedDataRecord；失败时返回错误。</returns>
    /// <remarks>
    /// <para>错误情况：</para>
    /// <list type="bullet">
    ///   <item>地址无效或帧读取失败</item>
    ///   <item>FrameTag 不是 ObjectVersion</item>
    ///   <item>Payload 解析失败</item>
    /// </list>
    /// </remarks>
    public AteliaResult<ParsedDataRecord> TryReadAt(Address64 address) {
        // 尝试读取帧
        if (!_scanner.TryReadAt(address, out var frame)) {
            return AteliaResult<ParsedDataRecord>.Failure(
                new DataRecordReadError(address, "Failed to read frame at address.")
            );
        }

        // 验证 FrameTag
        var frameTag = new FrameTag(frame.FrameTag);
        if (!FrameTags.IsDataFrameTag(frameTag)) {
            return AteliaResult<ParsedDataRecord>.Failure(
                new DataRecordFrameTagMismatchError(address, frame.FrameTag)
            );
        }

        // 从 FrameTag 提取 ObjectKind
        var kind = frameTag.GetObjectKind();

        // 读取并解析 payload
        var payload = _scanner.ReadPayload(frame);
        var parseResult = ObjectVersionRecord.TryParse(payload);

        if (parseResult.IsFailure) {
            return AteliaResult<ParsedDataRecord>.Failure(
                new DataRecordParseError(address, parseResult.Error!)
            );
        }

        var parsed = parseResult.Value;

        return AteliaResult<ParsedDataRecord>.Success(
            new ParsedDataRecord(
                Address: address,
                Kind: kind,
                PrevVersionPtr: parsed.PrevVersionPtr,
                DiffPayload: parsed.DiffPayload,
                Status: frame.Status
            )
        );
    }
}

// ============================================================================
// DataRecordReader Errors
// ============================================================================

/// <summary>
/// Data 记录读取错误基类。
/// </summary>
public abstract record DataRecordReaderError(
    string ErrorCode,
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : StateJournalError(ErrorCode, Message, RecoveryHint, Details, Cause);

/// <summary>
/// Data 记录读取失败错误（帧级别）。
/// </summary>
public sealed record DataRecordReadError(
    Address64 Address,
    string Reason
) : DataRecordReaderError(
    "StateJournal.DataRecordReader.ReadFailed",
    $"Failed to read ObjectVersionRecord at address 0x{Address.Value:X16}: {Reason}",
    "The address may be invalid or the frame may be corrupted.",
    new Dictionary<string, string> {
        ["Address"] = $"0x{Address.Value:X16}"
    });

/// <summary>
/// Data 记录 FrameTag 不匹配错误。
/// </summary>
public sealed record DataRecordFrameTagMismatchError(
    Address64 Address,
    uint ActualFrameTag
) : DataRecordReaderError(
    "StateJournal.DataRecordReader.FrameTagMismatch",
    $"Frame at address 0x{Address.Value:X16} has FrameTag 0x{ActualFrameTag:X8}, expected ObjectVersion record type.",
    "The address may point to a different record type.",
    new Dictionary<string, string> {
        ["Address"] = $"0x{Address.Value:X16}",
        ["ActualFrameTag"] = $"0x{ActualFrameTag:X8}"
    });

/// <summary>
/// Data 记录 Payload 解析错误。
/// </summary>
public sealed record DataRecordParseError(
    Address64 Address,
    AteliaError Cause
) : DataRecordReaderError(
    "StateJournal.DataRecordReader.ParseFailed",
    $"Failed to parse ObjectVersionRecord payload at address 0x{Address.Value:X16}.",
    "The payload may be corrupted or truncated.",
    new Dictionary<string, string> {
        ["Address"] = $"0x{Address.Value:X16}"
    },
    Cause);

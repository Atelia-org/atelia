// Source: Atelia.StateJournal - Workspace Recovery
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Recovery

namespace Atelia.StateJournal;

/// <summary>
/// Workspace 恢复逻辑。
/// </summary>
/// <remarks>
/// <para>
/// Recovery 流程：
/// <list type="number">
///   <item>读取最新的 MetaCommitRecord</item>
///   <item>比较 data file 实际长度与 DataTail</item>
///   <item>若 data &gt; DataTail：截断 data file</item>
///   <item>加载 VersionIndex</item>
///   <item>重建 _nextObjectId（扫描 VersionIndex 最大 key + 1）</item>
/// </list>
/// </para>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[R-META-AHEAD-BACKTRACK]</c>: 若 MetaCommitRecord 的 DataTail 大于 data file 实际长度，继续回扫上一条</item>
///   <item><c>[R-DATATAIL-TRUNCATE-SAFETY]</c>: data file 截断到 DataTail 是安全的</item>
/// </list>
/// </para>
/// </remarks>
public static class WorkspaceRecovery {
    /// <summary>
    /// 从 MetaCommitRecord 列表恢复（模拟扫描 meta file）。
    /// </summary>
    /// <param name="metaRecords">meta file 中的记录（按写入顺序）。</param>
    /// <param name="actualDataSize">data file 实际大小。</param>
    /// <returns>恢复结果。</returns>
    /// <remarks>
    /// <para>
    /// 从后向前扫描 meta records，找到第一个 DataTail &lt;= actualDataSize 的记录。
    /// </para>
    /// <para>
    /// 对应条款：<c>[R-META-AHEAD-BACKTRACK]</c>
    /// </para>
    /// </remarks>
    public static RecoveryInfo Recover(
        IReadOnlyList<MetaCommitRecord> metaRecords,
        ulong actualDataSize
    ) {
        if (metaRecords.Count == 0) { return RecoveryInfo.Empty; }

        // 从后向前扫描，找到第一个 DataTail <= actualDataSize 的记录
        for (int i = metaRecords.Count - 1; i >= 0; i--) {
            var record = metaRecords[i];

            if (record.DataTail <= actualDataSize) {
                // 找到有效的 commit point
                var wasTruncated = actualDataSize > record.DataTail;

                return new RecoveryInfo {
                    EpochSeq = record.EpochSeq,
                    NextObjectId = record.NextObjectId,
                    VersionIndexPtr = record.VersionIndexPtr,
                    DataTail = record.DataTail,
                    WasTruncated = wasTruncated,
                    OriginalDataSize = wasTruncated ? actualDataSize : 0,
                };
            }
            // else: meta 领先 data，继续回扫 [R-META-AHEAD-BACKTRACK]
        }

        // 所有记录都无效，返回空仓库状态
        return RecoveryInfo.Empty;
    }

    /// <summary>
    /// 验证 MetaCommitRecord 是否与 data file 一致。
    /// </summary>
    /// <param name="record">要验证的 MetaCommitRecord。</param>
    /// <param name="actualDataSize">data file 实际大小。</param>
    /// <returns>如果记录有效（DataTail &lt;= actualDataSize）则返回 true。</returns>
    public static bool IsRecordValid(MetaCommitRecord record, ulong actualDataSize) {
        return record.DataTail <= actualDataSize;
    }
}

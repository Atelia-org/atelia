using System.Text;

namespace Atelia.Rbf.ReadCache;

/// <summary>
/// 生成请求范围与缓存分布的拓扑关系字符画。
/// <para>
/// 区段类型：<c>M</c>=miss, <c>H</c>=hit, <c>C</c>=unused cache, <c>_</c>=gap。
/// 相邻同类型子区间合并为一个逻辑段。每段的字符数按对数刻度映射长度比例
/// （最短段=1字符，最长段=maxChars字符）。
/// </para>
/// </summary>
internal static class CacheHitMap {

    /// <summary>一个带类型和长度的合并段。</summary>
    internal readonly record struct Segment(char Type, long Length);

    // ── Phase 1: Sweep (Event Sweep-Line) ──────────────────────────

    /// <summary>
    /// 事件扫描线，产出合并后的 <see cref="Segment"/> 序列。
    /// <para>
    /// 为请求和每个缓存段各生成 +1（进入）/ −1（离开）事件，
    /// 按位置排序后单趟扫描，用 reqDepth / cacheDepth 计数器确定区间类型。
    /// 相邻同类型子区间合并（长度累加）。
    /// </para>
    /// </summary>
    internal static List<Segment> Sweep(
        long reqOffset,
        int reqLength,
        List<FileSegment> cacheSegments
    ) {
        // Build events: (position, delta, isReq).
        var events = new List<(long Pos, int Delta, bool IsReq)>();

        if (reqLength > 0) {
            long reqEnd = reqOffset + (long)reqLength;
            events.Add((reqOffset, +1, true));
            events.Add((reqEnd, -1, true));
        }
        foreach (var seg in cacheSegments) {
            if (seg.Length <= 0) { continue; }
            events.Add((seg.Offset, +1, false));
            events.Add((seg.End, -1, false));
        }

        if (events.Count == 0) { return []; }

        events.Sort((a, b) => a.Pos.CompareTo(b.Pos));

        int reqDepth = 0;
        int cacheDepth = 0;
        long prevPos = events[0].Pos;
        var segments = new List<Segment>();

        foreach (var (pos, delta, isReq) in events) {
            if (pos != prevPos) {
                long length = pos - prevPos;
                char type = (reqDepth > 0, cacheDepth > 0) switch {
                    (true, true) => 'H',
                    (true, false) => 'M',
                    (false, true) => 'C',
                    _ => '_',
                };

                // Merge with previous if same type.
                if (segments.Count > 0 && segments[^1].Type == type) {
                    var prev = segments[^1];
                    segments[^1] = new(type, prev.Length + length);
                }
                else {
                    segments.Add(new(type, length));
                }
                prevPos = pos;
            }
            if (isReq) { reqDepth += delta; }
            else { cacheDepth += delta; }
        }

        return segments;
    }

    // ── Phase 2: Render ─────────────────────────────────────────────

    /// <summary>
    /// 生成请求范围与缓存段的拓扑关系字符画。
    /// </summary>
    /// <param name="reqOffset">请求起始偏移</param>
    /// <param name="reqLength">请求长度</param>
    /// <param name="cacheSegments">缓存中的数据段 (Offset, Length) 列表</param>
    /// <param name="maxChars">最长段对应的最大字符数（最短段固定 1 字符，中间按 log 插值）</param>
    /// <returns>由 M/H/C/_ 组成的字符串</returns>
    public static string Render(
        long reqOffset,
        int reqLength,
        List<FileSegment> cacheSegments,
        int maxChars = 1
    ) {
        var segments = Sweep(reqOffset, reqLength, cacheSegments);
        if (segments.Count == 0) { return ""; }

        // Find Lmin / Lmax across all segments (including gaps).
        long lMin = long.MaxValue, lMax = 0;
        foreach (var seg in segments) {
            if (seg.Length < lMin) { lMin = seg.Length; }
            if (seg.Length > lMax) { lMax = seg.Length; }
        }

        var sb = new StringBuilder();
        foreach (var seg in segments) {
            int count = ScaleLength(seg.Length, lMin, lMax, maxChars);
            sb.Append(seg.Type, count);
        }
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// 对数刻度映射：最短段→1字符，最长段→maxChars字符。
    /// </summary>
    private static int ScaleLength(long length, long lMin, long lMax, int maxChars) {
        if (maxChars <= 1 || lMax <= lMin) { return 1; }
        double ratio = Math.Log((double)length / lMin) / Math.Log((double)lMax / lMin);
        int chars = 1 + (int)Math.Round(ratio * (maxChars - 1));
        return Math.Clamp(chars, 1, maxChars);
    }
}

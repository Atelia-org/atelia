namespace Atelia.StateJournal.Pools;

/// <summary>
/// 支持 Mark-Sweep GC 的值池。扩展 <see cref="IValuePool{T}"/>，
/// 增加三阶段回收协议：<see cref="BeginMark"/> → <see cref="MarkReachable"/> → <see cref="Sweep"/>。
/// </summary>
/// <remarks>
/// 使用方式：
/// - <see cref="IValuePool{T}.Store"/> 存入值，获得 handle。
/// - <see cref="BeginMark"/> 开始标记阶段（所有 slot 初始为不可达）。
/// - 对每个可达 handle 调用 <see cref="MarkReachable"/>。
/// - <see cref="Sweep"/> 回收所有不可达 slot，返回释放数量。
///
/// 当前假设 Mark-Sweep 是 stop-the-world 的，
/// 即 <see cref="BeginMark"/> 和 <see cref="Sweep"/> 之间不应调用 <see cref="IValuePool{T}.Store"/>。
/// </remarks>
/// <typeparam name="T">值类型，必须是 notnull。</typeparam>
internal interface IMarkSweepPool<T> : IValuePool<T> where T : notnull {
    /// <summary>
    /// 开始标记阶段：将所有 slot 标记为不可达。
    /// 调用后，对每个可达 handle 调用 <see cref="MarkReachable"/>。
    /// </summary>
    void BeginMark();

    /// <summary>
    /// 标记 handle 为可达。必须在 <see cref="BeginMark"/> 和 <see cref="Sweep"/> 之间调用。
    /// </summary>
    /// <param name="handle">待标记的 handle，必须是有效且已占用的 slot。</param>
    void MarkReachable(SlotHandle handle);

    /// <summary>
    /// 回收所有不可达且已占用的 slot。返回实际释放的 slot 数量。
    /// </summary>
    /// <exception cref="InvalidOperationException">未先调用 <see cref="BeginMark"/>。</exception>
    /// <returns>本轮 sweep 释放的 slot 数量。</returns>
    int Sweep();
}

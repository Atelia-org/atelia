using System.Globalization;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 加载阶段用于承接“历史帧里出现，但在目标 head 的 SymbolTable 中已不存在”的 SymbolId。
/// 每个缺失 SymbolId 会被映射为一个本次加载私有的 placeholder string。
/// 历史回放完成后，若最终 surviving 数据里仍残留 placeholder，说明数据损坏或内部 bug。
/// </summary>
internal sealed class LoadPlaceholderTracker {
    private readonly string _noncePrefix = "\u0001SJ.LoadPlaceholder." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".";
    private readonly HashSet<string> _placeholders = new(ReferenceEqualityComparer.Instance);

    internal string Create(SymbolId id) {
        string placeholder = _noncePrefix + id.Value.ToString(CultureInfo.InvariantCulture);
        _placeholders.Add(placeholder);
        return placeholder;
    }

    internal bool IsPlaceholder(string? value) => value is not null && _placeholders.Contains(value);
}

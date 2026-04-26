namespace Atelia.StateJournal;

/// <summary>
/// 显式的 symbol-backed 字符串包装。
/// 用作 typed durable 容器的 key / value / tuple 元素类型，语义上表示“通过 per-Revision symbol table 编码的字符串身份”。
/// </summary>
/// <remarks>
/// <para><see cref="Value"/> 契约上始终非 <c>null</c>，代表一个合法的字符串身份（可以是空字符串）。</para>
/// <para><c>default(Symbol)</c> 等价于 <see cref="Empty"/>，<see cref="Value"/> 返回 <see cref="string.Empty"/>。
/// 空字符串也是一个合法 symbol：会被 intern 到 symbol pool，获得非零 <see cref="Internal.SymbolId"/>。</para>
/// <para><c>null</c> 不是合法的 symbol 身份：构造器与隐式转换遇到 <c>null</c> 均会抛 <see cref="ArgumentNullException"/>。
/// 需要表达“无”请使用 <see cref="Empty"/>。</para>
/// <para>这是一层 API 语义包装，不改变底层 intern / SymbolTable / SymbolId 机制。</para>
/// </remarks>
public readonly struct Symbol : IEquatable<Symbol>, IComparable<Symbol> {
    private readonly string? _value;

    public Symbol(string value) {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>空 symbol，<see cref="Value"/> 为 <see cref="string.Empty"/>。与 <c>default(Symbol)</c> 等价。</summary>
    public static Symbol Empty => default;

    /// <summary>契约非 <c>null</c>；default 实例返回 <see cref="string.Empty"/>。</summary>
    public string Value => _value ?? string.Empty;

    public bool Equals(Symbol other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public int CompareTo(Symbol other) => string.Compare(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Symbol other && Equals(other);
    public override int GetHashCode() => string.GetHashCode(Value, StringComparison.Ordinal);
    public override string ToString() => Value;

    public static bool operator ==(Symbol left, Symbol right) => left.Equals(right);
    public static bool operator !=(Symbol left, Symbol right) => !left.Equals(right);

    /// <summary>
    /// 允许直接用非空字符串构造 <see cref="Symbol"/>，例如 <c>dict.Upsert("key", "value")</c>。
    /// 遇到 <c>null</c> 会抛 <see cref="ArgumentNullException"/>；需要“空 symbol”请显式使用 <see cref="Empty"/>。
    /// 反向（<c>Symbol → string?</c>）刻意不提供隐式转换：避免与 <c>object</c> 重载、字符串拼接、
    /// 泛型推断等场景产生意外行为；调用方需要 <see cref="Value"/> 时请显式取用。
    /// </summary>
    public static implicit operator Symbol(string value) => new(value);
}

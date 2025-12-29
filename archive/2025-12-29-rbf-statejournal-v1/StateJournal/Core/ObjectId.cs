// Source: Atelia.StateJournal - 对象唯一标识符
// Spec: atelia/docs/StateJournal/mvp-design-v2.md (术语表)

namespace Atelia.StateJournal;

/// <summary>
/// 持久化对象的唯一标识符。
/// 使用专用类型而非 ulong 以避免与 Ptr64 等其他 ulong 值语义混淆。
/// </summary>
public readonly record struct ObjectId(ulong Value) {
    public static implicit operator ulong(ObjectId id) => id.Value;
    public static explicit operator ObjectId(ulong value) => new(value);

    public override string ToString() => $"ObjectId({Value})";
}

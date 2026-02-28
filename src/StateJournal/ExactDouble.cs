// ai:note 这是一项技术储备，暂未启用
// namespace Atelia.StateJournal;

// /// <summary>用于向<see cref="DurableDict{TKey}"/>与<see cref="DurableList"/>这类异构容器中写入精确的double值。</summary>
// public readonly struct ExactDouble {
//     public readonly double Value;
//     public ExactDouble(double value) => Value = value;
//     // 显式转换（不建议隐式，否则失去标记意义）
//     public static explicit operator ExactDouble(double v) => new(v);
//     public static implicit operator double(ExactDouble v) => v.Value;
// }

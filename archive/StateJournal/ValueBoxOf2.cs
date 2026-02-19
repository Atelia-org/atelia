// using Atelia.StateJournal.Serialization;

// namespace Atelia.StateJournal;

// internal class ValueBox<T, TOp> : ValueBox, IValueBox<T>
//     where T: notnull
//     where TOp: struct, IValueOps<T> {
//     T _value;
//     internal ValueBox(T value) {
//         _value = value;
//     }
//     public T GetContent() => _value;

//     public override bool Equals(DurableBase? other) {
//         return (other is IValueBox<T> otherBox) && TOp.Equals(otherBox.GetContent(), GetContent());
//     }

//     public override Type ContentType => typeof(T);
// }

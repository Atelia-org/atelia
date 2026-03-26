using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

[MixedValueType(typeof(bool), typeof(ValueBox.BooleanFace), "Bool")]
[MixedValueType(typeof(string), typeof(ValueBox.SymbolIdFace), "String", SpecialHandling = MixedValueSpecialHandling.SymbolString)]
[MixedValueType(typeof(DurableObject), typeof(ValueBox.DurableRefFace), "DurableObject", SpecialHandling = MixedValueSpecialHandling.DurableObject)]
[MixedValueType(typeof(double), typeof(ValueBox.RoundedDoubleFace), "Double")]
[MixedValueType(typeof(float), typeof(ValueBox.SingleFace), "Single")]
[MixedValueType(typeof(Half), typeof(ValueBox.HalfFace), "Half")]
[MixedValueType(typeof(ulong), typeof(ValueBox.UInt64Face), "UInt64")]
[MixedValueType(typeof(uint), typeof(ValueBox.UInt32Face), "UInt32")]
[MixedValueType(typeof(ushort), typeof(ValueBox.UInt16Face), "UInt16")]
[MixedValueType(typeof(byte), typeof(ValueBox.ByteFace), "Byte")]
[MixedValueType(typeof(long), typeof(ValueBox.Int64Face), "Int64")]
[MixedValueType(typeof(int), typeof(ValueBox.Int32Face), "Int32")]
[MixedValueType(typeof(short), typeof(ValueBox.Int16Face), "Int16")]
[MixedValueType(typeof(sbyte), typeof(ValueBox.SByteFace), "SByte")]
internal static class MixedValueCatalog;

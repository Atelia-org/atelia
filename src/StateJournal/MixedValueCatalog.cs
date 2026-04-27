using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

// ─────────────────────────────────────────────────────────────────────────────
// 术语对照表（CMS 项目设计决议；见 ByteString.cs XML doc / docs/StateJournal/usage-guide.md）：
//
//   层级               String 链路              Blob 链路
//   ─────────────────  ──────────────────────  ──────────────────────
//   业务 API           string (BCL)            ByteString
//   内部前缀           StringPayload           Blob (= BlobPayload 简称)
//   HeapValueKind      StringPayload           BlobPayload
//   ValueKind          String                  Blob
//   Pool               OfOwnedString           OfOwnedBlob
//   Face               StringPayloadFace       BlobPayloadFace
//   Wire codec         BareStringPayload       BareBlobPayload
//   Wire tag           0xC0                    0xC1
//   View property      OfString                OfBlob
//   displayName        "String"                "Blob"
//
// 设计哲学：业务 API 暴露用户友好的简短名 (BCL string / 自家 ByteString)；
// 内部所有 payload 通路统一用 payload 概念专有术语 (StringPayload / Blob)。
// 两条链路命名分工完全对称——这是设计特性，不是疏忽。
// ─────────────────────────────────────────────────────────────────────────────

[MixedValueType(typeof(bool), typeof(ValueBox.BooleanFace), "Bool")]
[MixedValueType(typeof(Symbol), typeof(ValueBox.SymbolIdFace), "Symbol", SpecialHandling = MixedValueSpecialHandling.Symbol)]
[MixedValueType(typeof(string), typeof(ValueBox.StringPayloadFace), "String")]
[MixedValueType(typeof(ByteString), typeof(ValueBox.BlobPayloadFace), "Blob")]
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

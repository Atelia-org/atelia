using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.StateJournal.Generators;

[Generator]
public sealed class MixedValueContainerGenerator : IIncrementalGenerator {
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        context.RegisterPostInitializationOutput(MixedTypeGenerationCommon.EmitAttributeSources);

        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            MixedTypeGenerationCommon.UseMixedValueCatalogAttributeMetadataName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => MixedTypeGenerationCommon.CreateTarget(ctx)
        )
        .Where(static target => target is not null);

        context.RegisterSourceOutput(targets,
            static (ctx, target) => {
                string suffix = target!.ContainerKind switch {
                    MixedTypeGenerationCommon.MixedContainerKind.Deque => "MixedDeque",
                    MixedTypeGenerationCommon.MixedContainerKind.OrderedDict => "MixedOrderedDict",
                    _ => "MixedDict",
                };
                ctx.AddSource(
                    $"{target.TypeName}.{suffix}.g.cs",
                    SourceText.From(RenderTarget(target), Encoding.UTF8)
                );
            }
        );
    }

    private static string RenderTarget(MixedTypeGenerationCommon.TargetSpec target) {
        var builder = new StringBuilder();
        MixedTypeGenerationCommon.AppendFilePreamble(builder);
        MixedTypeGenerationCommon.AppendNamespace(builder, target.Namespace);

        if (target.ContainerKind == MixedTypeGenerationCommon.MixedContainerKind.Deque) {
            MixedTypeGenerationCommon.AppendPartialClassHeader(builder, target);
            builder.AppendLine("    // Partial implementations \u2014 interface list & declaring declarations are in the hand-written source.");
            builder.AppendLine();
            EmitDequeGenericDispatch(builder, target.Types);
            EmitDequeTypedViews(builder, target.Types);
            EmitDequeTrustedOverloads(builder, target.Types);
        }
        else {
            // Dict and OrderedDict share identical dispatch logic (Upsert/Get/Of + typed views)
            string keyType = target.JoinedTypeParameterNames;
            string containerDisplayName = target.ContainerKind == MixedTypeGenerationCommon.MixedContainerKind.OrderedDict
                ? "DurableOrderedDict"
                : "DurableDict";
            MixedTypeGenerationCommon.AppendPartialClassHeader(builder, target);
            builder.AppendLine("    // Partial implementations \u2014 interface list & declaring declarations are in the hand-written source.");
            builder.AppendLine();
            EmitDictGenericDispatch(builder, target.Types, keyType, containerDisplayName);
            EmitDictTypedViews(builder, target.Types, keyType);
            EmitDictTrustedOverloads(builder, target.Types, keyType);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitDequeGenericDispatch(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        EmitDequeWriteDispatchMethod(builder, "PushFront", "PushCore", "front: true", types);
        EmitDequeWriteDispatchMethod(builder, "PushBack", "PushCore", "front: false", types);
        EmitDequeTrySetAtDispatchMethod(builder, types);
        EmitDequeTryWriteDispatchMethod(builder, "TrySetFront", "TrySetCore", "front: true", types);
        EmitDequeTryWriteDispatchMethod(builder, "TrySetBack", "TrySetCore", "front: false", types);

        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("    public partial global::Atelia.StateJournal.IDeque<TValue> Of<TValue>() where TValue : notnull {");
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            builder.Append("            return (global::Atelia.StateJournal.IDeque<TValue>)(object)(global::Atelia.StateJournal.IDeque<")
                .Append(type.ValueType)
                .AppendLine(">)this;");
            builder.AppendLine("        }");
        }
        builder.AppendLine("        throw new NotSupportedException($\"Type {typeof(TValue)} is not a supported value type for DurableDeque\");");
        builder.AppendLine("    }");
        builder.AppendLine();

        EmitDequeReadDispatchMethod(builder, "PeekCore", "bool front", "front", types);
        EmitDequeReadDispatchMethod(builder, "GetCore", "int index", "index", types);
    }

    private static void EmitDequeWriteDispatchMethod(StringBuilder builder, string methodName, string coreMethodName, string coreArgumentPrefix, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.Append("    public partial void ").Append(methodName).AppendLine("<TValue>(TValue? value) where TValue : notnull {");
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("            ").Append(methodName).Append("(")
                        .Append("(").Append(type.RenderNullableValueType()).Append(")(object?)value")
                        .AppendLine(");");
                    break;
                default:
                    if (type.IsValueType) {
                        builder.Append("            ").Append(coreMethodName).Append("<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(")
                            .Append(coreArgumentPrefix).Append(", Unsafe.As<TValue, ").Append(type.ValueType).Append(">(ref value));")
                            .AppendLine();
                    }
                    else {
                        builder.Append("            ").Append(coreMethodName).Append("<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(")
                            .Append(coreArgumentPrefix).Append(", (").Append(type.RenderNullableValueType()).Append(")(object?)value);")
                            .AppendLine();
                    }
                    break;
            }
            builder.AppendLine("            return;");
            builder.AppendLine("        }");
        }
        builder.AppendLine("        if (typeof(global::Atelia.StateJournal.DurableObject).IsAssignableFrom(typeof(TValue))) {");
        builder.Append("            ").Append(methodName).AppendLine("((global::Atelia.StateJournal.DurableObject?)(object?)value);");
        builder.AppendLine("            return;");
        builder.AppendLine("        }");
        builder.AppendLine("        throw new NotSupportedException($\"Type {typeof(TValue)} is not a supported value type for DurableDeque\");");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitDequeTryWriteDispatchMethod(StringBuilder builder, string methodName, string coreMethodName, string coreArgumentPrefix, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.Append("    public partial bool ").Append(methodName).AppendLine("<TValue>(TValue? value) where TValue : notnull {");
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("            return ").Append(methodName).Append("(")
                        .Append("(").Append(type.RenderNullableValueType()).Append(")(object?)value")
                        .AppendLine(");");
                    break;
                default:
                    if (type.IsValueType) {
                        builder.Append("            return ").Append(coreMethodName).Append("<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(")
                            .Append(coreArgumentPrefix).Append(", Unsafe.As<TValue, ").Append(type.ValueType).Append(">(ref value));")
                            .AppendLine();
                    }
                    else {
                        builder.Append("            return ").Append(coreMethodName).Append("<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(")
                            .Append(coreArgumentPrefix).Append(", (").Append(type.RenderNullableValueType()).Append(")(object?)value);")
                            .AppendLine();
                    }
                    break;
            }
            builder.AppendLine("        }");
        }
        builder.AppendLine("        if (typeof(global::Atelia.StateJournal.DurableObject).IsAssignableFrom(typeof(TValue))) {");
        builder.Append("            return ").Append(methodName).AppendLine("((global::Atelia.StateJournal.DurableObject?)(object?)value);");
        builder.AppendLine("        }");
        builder.AppendLine("        throw new NotSupportedException($\"Type {typeof(TValue)} is not a supported value type for DurableDeque\");");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitDequeTrySetAtDispatchMethod(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("    public partial bool TrySetAt<TValue>(int index, TValue? value) where TValue : notnull {");
        foreach (var type in types) {
            EmitDequeTrySetAtDispatchBranch(builder, type);
        }
        builder.AppendLine("        if (typeof(global::Atelia.StateJournal.DurableObject).IsAssignableFrom(typeof(TValue))) {");
        builder.AppendLine("            return ((global::Atelia.StateJournal.IDeque<global::Atelia.StateJournal.DurableObject>)this).TrySetAt(index, (global::Atelia.StateJournal.DurableObject?)(object?)value);");
        builder.AppendLine("        }");
        builder.AppendLine("        throw new NotSupportedException($\"Type {typeof(TValue)} is not a supported value type for DurableDeque\");");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitDequeTrySetAtDispatchBranch(StringBuilder builder, MixedTypeGenerationCommon.TypeSpec type) {
        builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
        switch (type.SpecialHandling) {
            case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
            case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                builder.Append("            return ((global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">)this).TrySetAt(index, (")
                    .Append(type.RenderNullableValueType()).Append(")(object?)value);")
                    .AppendLine();
                break;
            default:
                if (type.IsValueType) {
                    builder.Append("            return TrySetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(index, Unsafe.As<TValue, ").Append(type.ValueType).Append(">(ref value));")
                        .AppendLine();
                }
                else {
                    builder.Append("            return TrySetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(index, (").Append(type.RenderNullableValueType()).Append(")(object?)value);")
                        .AppendLine();
                }
                break;
        }
        builder.AppendLine("        }");
    }

    private static void EmitDequeReadDispatchMethod(StringBuilder builder, string methodName, string methodParameter, string coreArgument, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.Append("    private partial global::Atelia.StateJournal.GetIssue ").Append(methodName).Append("<TValue>(").Append(methodParameter).Append(", out TValue? value) where TValue : notnull {").AppendLine();
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                    builder.Append("            var exactIssue = ")
                        .Append(methodName == "PeekCore" ? "PeekDurableObject" : "GetDurableObjectAt")
                        .Append("(").Append(coreArgument).Append(", out ").Append(type.RenderNullableValueType()).AppendLine(" typedValue);");
                    builder.AppendLine("            value = (TValue?)(object?)typedValue;");
                    break;
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("            var exactIssue = ")
                        .Append(methodName == "PeekCore" ? "PeekSymbol" : "GetSymbolAt")
                        .Append("(").Append(coreArgument).Append(", out ").Append(type.RenderNullableValueType()).AppendLine(" typedValue);");
                    builder.AppendLine("            value = (TValue?)(object?)typedValue;");
                    break;
                default:
                    builder.Append("            var exactIssue = ").Append(methodName).Append("<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(")
                        .Append(coreArgument).Append(", out ").Append(type.RenderNullableValueType()).AppendLine(" typedValue);");
                    if (type.IsValueType) {
                        builder.Append("            value = Unsafe.As<").Append(type.ValueType).Append(", TValue>(ref typedValue);").AppendLine();
                    }
                    else {
                        builder.AppendLine("            value = (TValue?)(object?)typedValue;");
                    }
                    break;
            }
            builder.AppendLine("            return exactIssue;");
            builder.AppendLine("        }");
        }

        builder.AppendLine("        if (!typeof(global::Atelia.StateJournal.DurableObject).IsAssignableFrom(typeof(TValue))) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return global::Atelia.StateJournal.GetIssue.UnsupportedType;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        global::Atelia.StateJournal.DurableObject? baseVal;");
        if (methodName == "PeekCore") {
            builder.AppendLine("        var issue = front");
            builder.AppendLine("            ? PeekFront(out baseVal)");
            builder.AppendLine("            : PeekBack(out baseVal);");
        }
        else {
            builder.AppendLine("        var issue = GetDurableObjectAt(index, out baseVal);");
        }
        builder.AppendLine("        if (issue != global::Atelia.StateJournal.GetIssue.None) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return issue;");
        builder.AppendLine("        }");
        builder.AppendLine("        if (baseVal is null) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return global::Atelia.StateJournal.GetIssue.None;");
        builder.AppendLine("        }");
        builder.AppendLine("        if (baseVal is not TValue castVal) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return global::Atelia.StateJournal.GetIssue.TypeMismatch;");
        builder.AppendLine("        }");
        builder.AppendLine("        value = castVal;");
        builder.AppendLine("        return global::Atelia.StateJournal.GetIssue.None;");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitDequeTypedViews(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        builder.AppendLine("    #region IDeque<TValue>");
        builder.AppendLine();
        foreach (var type in types) {
            var nullableValueType = type.RenderNullableValueType();
            builder.Append("    public global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append("> Of").Append(type.PropertySuffix).AppendLine(" => this;");

            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                    builder.Append("    global::Atelia.StateJournal.GetIssue global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">.GetAt(int index, out ").Append(nullableValueType).AppendLine(" value) => GetDurableObjectAt(index, out value);");
                    builder.Append("    public void PushFront(").Append(nullableValueType).AppendLine(" value) => PushCore<global::Atelia.StateJournal.Internal.DurableRef, global::Atelia.StateJournal.Internal.ValueBox.DurableRefFace>(front: true, ToDurableRef(value));");
                    builder.Append("    public void PushBack(").Append(nullableValueType).AppendLine(" value) => PushCore<global::Atelia.StateJournal.Internal.DurableRef, global::Atelia.StateJournal.Internal.ValueBox.DurableRefFace>(front: false, ToDurableRef(value));");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PeekFront(out ").Append(nullableValueType).AppendLine(" value) => PeekDurableObject(front: true, out value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PeekBack(out ").Append(nullableValueType).AppendLine(" value) => PeekDurableObject(front: false, out value);");
                    builder.Append("    bool global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">.TrySetAt(int index, ").Append(nullableValueType).AppendLine(" value) => TrySetCore<global::Atelia.StateJournal.Internal.DurableRef, global::Atelia.StateJournal.Internal.ValueBox.DurableRefFace>(index, ToDurableRef(value));");
                    builder.Append("    public bool TrySetFront(").Append(nullableValueType).AppendLine(" value) => TrySetCore<global::Atelia.StateJournal.Internal.DurableRef, global::Atelia.StateJournal.Internal.ValueBox.DurableRefFace>(front: true, ToDurableRef(value));");
                    builder.Append("    public bool TrySetBack(").Append(nullableValueType).AppendLine(" value) => TrySetCore<global::Atelia.StateJournal.Internal.DurableRef, global::Atelia.StateJournal.Internal.ValueBox.DurableRefFace>(front: false, ToDurableRef(value));");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PopFront(out ").Append(nullableValueType).AppendLine(" value) => PopCore<global::Atelia.StateJournal.DurableObject>(front: true, out value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PopBack(out ").Append(nullableValueType).AppendLine(" value) => PopCore<global::Atelia.StateJournal.DurableObject>(front: false, out value);");
                    break;
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("    global::Atelia.StateJournal.GetIssue global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">.GetAt(int index, out ").Append(nullableValueType).AppendLine(" value) => GetSymbolAt(index, out value);");
                    builder.Append("    public void PushFront(").Append(nullableValueType).AppendLine(" value) => PushSymbol(front: true, value);");
                    builder.Append("    public void PushBack(").Append(nullableValueType).AppendLine(" value) => PushSymbol(front: false, value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PeekFront(out ").Append(nullableValueType).AppendLine(" value) => PeekSymbol(front: true, out value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PeekBack(out ").Append(nullableValueType).AppendLine(" value) => PeekSymbol(front: false, out value);");
                    builder.Append("    bool global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">.TrySetAt(int index, ").Append(nullableValueType).AppendLine(" value) => TrySetSymbol(index, value);");
                    builder.Append("    public bool TrySetFront(").Append(nullableValueType).AppendLine(" value) => TrySetSymbol(front: true, value);");
                    builder.Append("    public bool TrySetBack(").Append(nullableValueType).AppendLine(" value) => TrySetSymbol(front: false, value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PopFront(out ").Append(nullableValueType).AppendLine(" value) => PopCore<global::Atelia.StateJournal.Symbol>(front: true, out value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PopBack(out ").Append(nullableValueType).AppendLine(" value) => PopCore<global::Atelia.StateJournal.Symbol>(front: false, out value);");
                    break;
                default:
                    builder.Append("    global::Atelia.StateJournal.GetIssue global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">.GetAt(int index, out ").Append(nullableValueType).Append(" value) => GetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(index, out value);");
                    builder.Append("    public void PushFront(").Append(nullableValueType).Append(" value) => PushCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(front: true, value);");
                    builder.Append("    public void PushBack(").Append(nullableValueType).Append(" value) => PushCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(front: false, value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PeekFront(out ").Append(nullableValueType).Append(" value) => PeekCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(front: true, out value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PeekBack(out ").Append(nullableValueType).Append(" value) => PeekCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(front: false, out value);");
                    builder.Append("    bool global::Atelia.StateJournal.IDeque<").Append(type.ValueType).Append(">.TrySetAt(int index, ").Append(nullableValueType).Append(" value) => TrySetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(index, value);");
                    builder.Append("    public bool TrySetFront(").Append(nullableValueType).Append(" value) => TrySetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(front: true, value);");
                    builder.Append("    public bool TrySetBack(").Append(nullableValueType).Append(" value) => TrySetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(front: false, value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PopFront(out ").Append(nullableValueType).Append(" value) => PopCore<").Append(type.ValueType).AppendLine(">(front: true, out value);");
                    builder.Append("    public global::Atelia.StateJournal.GetIssue PopBack(out ").Append(nullableValueType).Append(" value) => PopCore<").Append(type.ValueType).AppendLine(">(front: false, out value);");
                    break;
            }
            builder.AppendLine();
        }
        builder.AppendLine("    #endregion");
        builder.AppendLine();
    }

    private static void EmitDictGenericDispatch(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types, string keyType, string containerDisplayName = "DurableDict") {
        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.Append("    public partial global::Atelia.StateJournal.UpsertStatus Upsert<TValue>(").Append(keyType).AppendLine(" key, TValue? value) where TValue : notnull {");
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                    builder.Append("            return Upsert(key, (").Append(type.RenderNullableValueType()).Append(")(object?)value);").AppendLine();
                    break;
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("            return UpsertSymbol(key, (").Append(type.RenderNullableValueType()).Append(")(object?)value);").AppendLine();
                    break;
                default:
                    if (type.IsValueType) {
                        builder.Append("            return UpsertCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(key, Unsafe.As<TValue, ").Append(type.ValueType).Append(">(ref value));").AppendLine();
                    }
                    else {
                        builder.Append("            return UpsertCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(key, (").Append(type.RenderNullableValueType()).Append(")(object?)value);").AppendLine();
                    }
                    break;
            }
            builder.AppendLine("        }");
        }
        builder.AppendLine("        if (typeof(global::Atelia.StateJournal.DurableObject).IsAssignableFrom(typeof(TValue))) {");
        builder.AppendLine("            return Upsert(key, (global::Atelia.StateJournal.DurableObject?)(object?)value);");
        builder.AppendLine("        }");
        builder.Append("        throw new NotSupportedException($\"Type {typeof(TValue)} is not a supported value type for ").Append(containerDisplayName).AppendLine("\");");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.Append("    public partial global::Atelia.StateJournal.IDict<").Append(keyType).Append(", TValue> Of<TValue>() where TValue : notnull {").AppendLine();
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            builder.Append("            return (global::Atelia.StateJournal.IDict<").Append(keyType).Append(", TValue>)(object)(global::Atelia.StateJournal.IDict<").Append(keyType).Append(", ").Append(type.ValueType).AppendLine(">)this;");
            builder.AppendLine("        }");
        }
        builder.Append("        throw new NotSupportedException($\"Type {typeof(TValue)} is not a supported value type for ").Append(containerDisplayName).AppendLine("\");");
        builder.AppendLine("    }");
        builder.AppendLine();

        builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.Append("    public partial global::Atelia.StateJournal.GetIssue Get<TValue>(").Append(keyType).AppendLine(" key, out TValue? value) where TValue : notnull {");
        foreach (var type in types) {
            builder.Append("        if (typeof(TValue) == typeof(").Append(type.ValueType).AppendLine(")) {");
            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                    builder.Append("            var exactIssue = GetDurableObject(key, out ").Append(type.RenderNullableValueType()).AppendLine(" typedValue);");
                    builder.AppendLine("            value = (TValue?)(object?)typedValue;");
                    break;
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("            var exactIssue = GetSymbol(key, out ").Append(type.RenderNullableValueType()).AppendLine(" typedValue);");
                    builder.AppendLine("            value = (TValue?)(object?)typedValue;");
                    break;
                default:
                    builder.Append("            var exactIssue = GetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).Append(">(key, out ").Append(type.RenderNullableValueType()).AppendLine(" typedValue);");
                    if (type.IsValueType) {
                        builder.Append("            value = Unsafe.As<").Append(type.ValueType).Append(", TValue>(ref typedValue);").AppendLine();
                    }
                    else {
                        builder.AppendLine("            value = (TValue?)(object?)typedValue;");
                    }
                    break;
            }
            builder.AppendLine("            return exactIssue;");
            builder.AppendLine("        }");
        }
        builder.AppendLine("        if (!typeof(global::Atelia.StateJournal.DurableObject).IsAssignableFrom(typeof(TValue))) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return global::Atelia.StateJournal.GetIssue.UnsupportedType;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var issue = GetDurableObject(key, out global::Atelia.StateJournal.DurableObject? baseVal);");
        builder.AppendLine("        if (issue != global::Atelia.StateJournal.GetIssue.None) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return issue;");
        builder.AppendLine("        }");
        builder.AppendLine("        if (baseVal is null) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return global::Atelia.StateJournal.GetIssue.None;");
        builder.AppendLine("        }");
        builder.AppendLine("        if (baseVal is not TValue castVal) {");
        builder.AppendLine("            value = default;");
        builder.AppendLine("            return global::Atelia.StateJournal.GetIssue.TypeMismatch;");
        builder.AppendLine("        }");
        builder.AppendLine("        value = castVal;");
        builder.AppendLine("        return global::Atelia.StateJournal.GetIssue.None;");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void EmitDictTypedViews(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types, string keyType) {
        builder.AppendLine("    #region IDict<TKey, TValue>");
        builder.AppendLine();
        foreach (var type in types) {
            var nullableValueType = type.RenderNullableValueType();
            builder.Append("    public global::Atelia.StateJournal.IDict<").Append(keyType).Append(", ").Append(type.ValueType).Append("> Of").Append(type.PropertySuffix).AppendLine(" => this;");

            switch (type.SpecialHandling) {
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.DurableObject:
                    builder.Append("    public global::Atelia.StateJournal.GetIssue Get(").Append(keyType).Append(" key, out ").Append(nullableValueType).AppendLine(" value) => GetDurableObject(key, out value);");
                    builder.Append("    public global::Atelia.StateJournal.UpsertStatus Upsert(").Append(keyType).Append(" key, ").Append(nullableValueType).AppendLine(" value) => UpsertCore<global::Atelia.StateJournal.Internal.DurableRef, global::Atelia.StateJournal.Internal.ValueBox.DurableRefFace>(key, ToDurableRef(value));");
                    break;
                case MixedTypeGenerationCommon.MixedValueSpecialHandling.Symbol:
                    builder.Append("    public global::Atelia.StateJournal.GetIssue Get(").Append(keyType).Append(" key, out ").Append(nullableValueType).AppendLine(" value) => GetSymbol(key, out value);");
                    builder.Append("    public global::Atelia.StateJournal.UpsertStatus Upsert(").Append(keyType).Append(" key, ").Append(nullableValueType).AppendLine(" value) => UpsertSymbol(key, value);");
                    break;
                default:
                    builder.Append("    public global::Atelia.StateJournal.GetIssue Get(").Append(keyType).Append(" key, out ").Append(nullableValueType).Append(" value) => GetCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(key, out value);");
                    builder.Append("    public global::Atelia.StateJournal.UpsertStatus Upsert(").Append(keyType).Append(" key, ").Append(nullableValueType).Append(" value) => UpsertCore<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(key, value);");
                    break;
            }
            builder.AppendLine();
        }
        builder.AppendLine("    #endregion");
        builder.AppendLine();
    }

    /// <summary>
    /// CMS Step E：为标注 <c>SupportsTrustedFromCallerOwnedBuffer = true</c> 的 type 生成
    /// <c>UpsertTrusted{PropertySuffix}</c> opt-in overload，dispatch 到手写
    /// <c>UpsertCoreTrusted&lt;TValue, VFace&gt;</c>。
    /// </summary>
    private static void EmitDictTrustedOverloads(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types, string keyType) {
        bool any = false;
        foreach (var type in types) {
            if (!type.SupportsTrustedFromCallerOwnedBuffer) { continue; }
            if (!any) {
                builder.AppendLine("    #region Trusted (caller-owned buffer) zero-copy overloads");
                builder.AppendLine();
                any = true;
            }
            string nullableValueType = type.RenderNullableValueType();
            string docTypeName = RenderDocTypeName(type.ValueType);
            AppendTrustedOverloadDoc(
                builder,
                $"Trusted zero-copy variant of <c>Upsert(key, {docTypeName})</c>.",
                docTypeName
            );
            builder.Append("    public global::Atelia.StateJournal.UpsertStatus UpsertTrusted").Append(type.PropertySuffix).Append("(").Append(keyType).Append(" key, ").Append(nullableValueType).Append(" value)").AppendLine();
            builder.Append("        => UpsertCoreTrusted<").Append(type.ValueType).Append(", ").Append(type.FaceType).AppendLine(">(key, value);");
            builder.AppendLine();
        }
        if (any) {
            builder.AppendLine("    #endregion");
            builder.AppendLine();
        }
    }

    /// <summary>
    /// CMS Step E：为标注 <c>SupportsTrustedFromCallerOwnedBuffer = true</c> 的 type 生成
    /// <c>PushFrontTrusted{PropertySuffix}</c> / <c>PushBackTrusted{PropertySuffix}</c> /
    /// <c>TrySetFrontTrusted{PropertySuffix}</c> / <c>TrySetBackTrusted{PropertySuffix}</c> /
    /// <c>TrySetAtTrusted{PropertySuffix}</c> opt-in overload。
    /// </summary>
    private static void EmitDequeTrustedOverloads(StringBuilder builder, ImmutableArray<MixedTypeGenerationCommon.TypeSpec> types) {
        bool any = false;
        foreach (var type in types) {
            if (!type.SupportsTrustedFromCallerOwnedBuffer) { continue; }
            if (!any) {
                builder.AppendLine("    #region Trusted (caller-owned buffer) zero-copy overloads");
                builder.AppendLine();
                any = true;
            }
            string nullableValueType = type.RenderNullableValueType();
            string suffix = type.PropertySuffix;
            string vt = type.ValueType;
            string face = type.FaceType;
            string docTypeName = RenderDocTypeName(type.ValueType);
            AppendTrustedOverloadDoc(builder, $"Trusted zero-copy variant of <c>PushFront({docTypeName})</c>.", docTypeName);
            builder.Append("    public void PushFrontTrusted").Append(suffix).Append("(").Append(nullableValueType).Append(" value) => PushCoreTrusted<").Append(vt).Append(", ").Append(face).AppendLine(">(front: true, value);");
            AppendTrustedOverloadDoc(builder, $"Trusted zero-copy variant of <c>PushBack({docTypeName})</c>.", docTypeName);
            builder.Append("    public void PushBackTrusted").Append(suffix).Append("(").Append(nullableValueType).Append(" value) => PushCoreTrusted<").Append(vt).Append(", ").Append(face).AppendLine(">(front: false, value);");
            AppendTrustedOverloadDoc(builder, $"Trusted zero-copy variant of <c>TrySetFront({docTypeName})</c>.", docTypeName);
            builder.Append("    public bool TrySetFrontTrusted").Append(suffix).Append("(").Append(nullableValueType).Append(" value) => TrySetCoreTrusted<").Append(vt).Append(", ").Append(face).AppendLine(">(front: true, value);");
            AppendTrustedOverloadDoc(builder, $"Trusted zero-copy variant of <c>TrySetBack({docTypeName})</c>.", docTypeName);
            builder.Append("    public bool TrySetBackTrusted").Append(suffix).Append("(").Append(nullableValueType).Append(" value) => TrySetCoreTrusted<").Append(vt).Append(", ").Append(face).AppendLine(">(front: false, value);");
            AppendTrustedOverloadDoc(builder, $"Trusted zero-copy variant of <c>TrySetAt(index, {docTypeName})</c>.", docTypeName);
            builder.Append("    public bool TrySetAtTrusted").Append(suffix).Append("(int index, ").Append(nullableValueType).Append(" value) => TrySetAtCoreTrusted<").Append(vt).Append(", ").Append(face).AppendLine(">(index, value);");
            builder.AppendLine();
        }
        if (any) {
            builder.AppendLine("    #endregion");
            builder.AppendLine();
        }
    }

    private static string RenderDocTypeName(string valueType) {
        const string globalPrefix = "global::";
        if (valueType.StartsWith(globalPrefix, System.StringComparison.Ordinal)) {
            valueType = valueType.Substring(globalPrefix.Length);
        }

        const string stateJournalPrefix = "Atelia.StateJournal.";
        if (valueType.StartsWith(stateJournalPrefix, System.StringComparison.Ordinal)) {
            valueType = valueType.Substring(stateJournalPrefix.Length);
        }

        return valueType;
    }

    private static void AppendTrustedOverloadDoc(StringBuilder builder, string summary, string docTypeName) {
        builder.Append("    /// <summary>").Append(summary).AppendLine("</summary>");
        builder.Append("    /// <remarks>Caller must pass a ").Append(docTypeName).AppendLine(" constructed via <c>ByteString.FromTrustedOwned(byte[])</c>");
        builder.AppendLine("    /// (or an equivalent caller-owned immutable-buffer contract). This overload skips the face-layer defensive clone;");
        builder.AppendLine("    /// mutating the transferred buffer afterwards violates the StateJournal immutability contract.</remarks>");
    }
}

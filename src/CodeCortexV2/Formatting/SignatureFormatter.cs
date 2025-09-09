using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Formatting;

public enum NameStyle {
    Short,
    FullyQualified
}

public sealed class SignatureOptions {
    public NameStyle NameStyle { get; init; } = NameStyle.Short;
    public bool IncludeContainingType { get; init; } = false;
    public bool IncludeNamespace { get; init; } = false;
}

public static class SignatureFormatter {
    public static string RenderSignature(ISymbol s, SignatureOptions? options = null) {
        options ??= new SignatureOptions();

        if (s is INamespaceSymbol ns) {
            var name = options.NameStyle == NameStyle.FullyQualified
                ? ns.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)
                : ns.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return $"namespace {name}";
        }

        if (s is IPropertySymbol ps) {
            var acc = new StringBuilder();
            acc.Append("{ ");
            if (ps.GetMethod != null && IsPublicApiMember(ps.GetMethod)) {
                acc.Append("get; ");
            }

            if (ps.SetMethod != null && IsPublicApiMember(ps.SetMethod)) {
                acc.Append("set; ");
            }

            acc.Append("}");
            var typeStr = DisplayType(ps.Type, options);
            var namePart = ps.IsIndexer
                ? $"this[{string.Join(", ", ps.Parameters.Select(p => DisplayType(p.Type, options) + " " + p.Name))}]"
                : QualifiedName(ps, options);
            return $"{typeStr} {namePart} {acc}";
        }

        if (s is INamedTypeSymbol nt) {
            if (nt.TypeKind == TypeKind.Delegate) {
                var invoke = nt.DelegateInvokeMethod;
                var ret = invoke?.ReturnType is null ? "void" : DisplayType(invoke.ReturnType, options);
                var nameDisplay = QualifiedName(nt, options);
                var parms = invoke == null ? string.Empty : string.Join(", ", invoke.Parameters.Select(p => DisplayType(p.Type, options) + " " + p.Name));
                return $"delegate {ret} {nameDisplay}({parms})";
            }
            var display = QualifiedName(nt, options);
            return $"{TypeKindKeyword(nt.TypeKind)} {display}";
        }

        if (s is IMethodSymbol ms) {
            if (ms.MethodKind == MethodKind.Constructor) {
                var parms = string.Join(", ", ms.Parameters.Select(p => DisplayType(p.Type, options) + " " + p.Name));
                var typeName = QualifiedName(ms.ContainingType, new SignatureOptions { NameStyle = options.NameStyle, IncludeContainingType = true, IncludeNamespace = options.IncludeNamespace });
                return $"{typeName}({parms})";
            }
            var ret = DisplayType(ms.ReturnType, options);
            var methodName = ms.Name + (ms.IsGenericMethod ? $"<{string.Join(", ", ms.TypeParameters.Select(tp => tp.Name))}>" : string.Empty);
            var args = string.Join(", ", ms.Parameters.Select(p => DisplayType(p.Type, options) + " " + p.Name));
            return $"{ret} {methodName}({args})";
        }

        if (s is IEventSymbol ev) {
            var t = DisplayType(ev.Type, options);
            return $"event {t} {QualifiedName(ev, options)}";
        }

        if (s is IFieldSymbol fl) {
            var t = DisplayType(fl.Type, options);
            return $"{t} {QualifiedName(fl, options)}";
        }

        return s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static string DisplayType(ITypeSymbol t, SignatureOptions options) {
        return options.NameStyle == NameStyle.FullyQualified
            ? t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)
            : t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static string QualifiedName(ISymbol s, SignatureOptions options) {
        if (options.NameStyle == NameStyle.FullyQualified) {
            return s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        }
        // Short name: optionally include containing type or namespace
        var parts = new System.Collections.Generic.List<string>();
        if (options.IncludeNamespace && s.ContainingNamespace != null && !s.ContainingNamespace.IsGlobalNamespace) {
            parts.Add(s.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
        if (options.IncludeContainingType && s.ContainingType != null) {
            parts.Add(s.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
        parts.Add(
            s switch {
                INamedTypeSymbol nt => nt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                _ => s.Name
            }
        );
        return string.Join('.', parts);
    }

    private static string TypeKindKeyword(TypeKind k) => k switch {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => k.ToString().ToLowerInvariant()
    };

    private static bool IsPublicApiMember(ISymbol s) =>
        s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
        && !s.IsImplicitlyDeclared;
}


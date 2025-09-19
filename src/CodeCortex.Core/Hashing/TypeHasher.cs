
using System.Text;
using CodeCortex.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Atelia.Diagnostics;

namespace CodeCortex.Core.Hashing;

/// <summary>
/// Minimal Phase1 implementation of type hashing (structure/publicImpl/internalImpl/xmlDoc/cosmetic/impl).
/// <para>NOTE: simplified; TODO refine per full design (ordering, partial aggregation, trivia normalization).</para>
/// </summary>
public sealed class TypeHasher : ITypeHasher {
    private readonly IHashFunction _hash;
    private readonly ITriviaStripper _trivia;

    // 调试输出统一入口
    private static void DebugPrint(string msg) => DebugUtil.Print("TypeHash", msg);

    /// <summary>Create a TypeHasher with optional injected hash and trivia stripping strategies.</summary>
    public TypeHasher(IHashFunction? hashFunction = null, ITriviaStripper? triviaStripper = null) {
        _hash = hashFunction ?? new DefaultHashFunction();
        _trivia = triviaStripper ?? new DefaultTriviaStripper();
    }

    /// &lt;inheritdoc /&gt;
    public TypeHashes Compute(INamedTypeSymbol symbol, IReadOnlyList<string> partialFilePaths, HashConfig config) {
        try {
            var intermediate = Collect(symbol, config);
            return Hash(intermediate);
        }
        catch {
            return TypeHashes.Empty; // Phase1 swallow
        }
    }

    /// <summary>Collect non-hashed artifacts (public for test visibility).</summary>
    public HasherIntermediate Collect(INamedTypeSymbol symbol, HashConfig config) {
        var structureLines = new List<string>();
        structureLines.Add(symbol.TypeKind + " " + symbol.Name + GenericArity(symbol));
        var publicBodies = new List<string>();
        var internalBodies = new List<string>();
        var cosmeticSb = new StringBuilder();
        var members = symbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .OrderBy(MemberKindPriority)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        DebugPrint($"Collect Type={symbol.Name} MembersRaw=" + string.Join(",", symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared).Select(m => $"{m.Kind}:{m.Name}")));

        foreach (var member in members) {
            switch (member) {
                case IMethodSymbol ms when ms.MethodKind is not MethodKind.PropertyGet and not MethodKind.PropertySet and not MethodKind.EventAdd and not MethodKind.EventRemove and not MethodKind.EventRaise and not MethodKind.StaticConstructor:
                    DebugPrint($"Method {ms.Name} Kind={ms.MethodKind} PublicLike={IsPublicLike(ms)}");

                    if (IsConsideredStructure(ms, config)) {
                        structureLines.Add(BuildMethodSignature(ms, config));
                    }

                    CollectBody(ms, publicBodies, internalBodies);
                    break;
                case IPropertySymbol ps:
                    DebugPrint($"Prop {ps.Name} PublicLike={IsPublicLike(ps)}");

                    if (IsConsideredStructure(ps, config)) {
                        structureLines.Add(BuildPropertySignature(ps));
                    }

                    CollectAccessorBodies(ps, publicBodies, internalBodies);
                    break;
                case IFieldSymbol fs:
                    DebugPrint($"Field {fs.Name} PublicLike={IsPublicLike(fs)}");

                    if (IsConsideredStructure(fs, config)) {
                        structureLines.Add(BuildFieldSignature(fs));
                    }

                    break;
                case IEventSymbol es:
                    DebugPrint($"Event {es.Name} PublicLike={IsPublicLike(es)}");

                    if (IsConsideredStructure(es, config)) {
                        structureLines.Add(BuildEventSignature(es));
                    }

                    break;
                case INamedTypeSymbol nts:
                    DebugPrint($"NestedType {nts.Name} PublicLike={IsPublicLike(nts)}");

                    if (IsConsideredStructure(nts, config)) {
                        structureLines.Add("nested " + nts.TypeKind + " " + nts.Name + GenericArity(nts));
                    }

                    break;
            }
        }

        foreach (var decl in symbol.DeclaringSyntaxReferences.OrderBy(r => r.SyntaxTree.FilePath).ThenBy(r => r.Span.Start)) {
            if (decl.GetSyntax() is TypeDeclarationSyntax node) {
                cosmeticSb.AppendLine(NormalizeWhitespaceForCosmetic(node.ToFullString()));
            }
        }

        // Supplement: ensure all public/protected methods from syntax are present (covers any Roslyn edge cases in symbol enumeration we observed in tests)
        try {
            var existingSigNames = new HashSet<string>(structureLines);
            foreach (var decl in symbol.DeclaringSyntaxReferences) {
                if (decl.GetSyntax() is TypeDeclarationSyntax tds) {
                    foreach (var m in tds.Members.OfType<MethodDeclarationSyntax>()) {
                        var mods = m.Modifiers.ToString();
                        bool isPublicLike = mods.Contains("public") || mods.Contains("protected");
                        if (!isPublicLike) { continue; }
                        var paramTypes = string.Join(',', m.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
                        var candidate = "public " + (m.ReturnType?.ToString() ?? "void") + " " + m.Identifier.Text + "(" + paramTypes + ")";
                        if (!structureLines.Any(l => l.Contains(m.Identifier.Text + "("))) {
                            structureLines.Add(candidate);
                        }
                        // Add body if missing
                        var bodyText = (m.Body != null ? m.Body.ToFullString() : m.ExpressionBody?.Expression.ToFullString()) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(bodyText)) {
                            var stripped = _trivia.Strip(bodyText);
                            // Avoid duplicate by checking size equality and content
                            if (!publicBodies.Contains(stripped)) {
                                publicBodies.Add(stripped);
                            }
                        }
                    }
                }
            }
        }
        catch { /* non-fatal */ }

        // Fallback: if we somehow failed to capture any method bodies though methods exist, re-scan syntax.
        if (publicBodies.Count == 0 && internalBodies.Count == 0) {
            foreach (var decl in symbol.DeclaringSyntaxReferences) {
                if (decl.GetSyntax() is TypeDeclarationSyntax tds) {
                    foreach (var m in tds.Members.OfType<MethodDeclarationSyntax>()) {
                        var accessibility = m.Modifiers.ToString();
                        bool isPublicLike = accessibility.Contains("public") || accessibility.Contains("protected");
                        var bodyText = (m.Body != null ? m.Body.ToFullString() : m.ExpressionBody?.Expression.ToFullString()) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(bodyText)) { continue; }
                        bodyText = _trivia.Strip(bodyText);
                        if (isPublicLike) {
                            publicBodies.Add(bodyText);
                        }
                        else {
                            internalBodies.Add(bodyText);
                        }
                        // Ensure structure line has at least method name if missing
                        var sigMissing = !structureLines.Any(l => l.Contains(m.Identifier.Text + "("));
                        if (sigMissing) {
                            var paramTypeList = string.Join(',', m.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
                            structureLines.Add("public " + (m.ReturnType?.ToString() ?? "void") + " " + m.Identifier.Text + "(" + paramTypeList + ")");
                        }
                    }
                }
            }
        }

        var xml = symbol.GetDocumentationCommentXml() ?? string.Empty;
        var xmlFirst = ExtractFirstLine(xml);
        DebugPrint("Result.StructureLines " + string.Join("|", structureLines));
        DebugPrint("Result.PublicBodiesCount " + publicBodies.Count + " InternalBodiesCount=" + internalBodies.Count);
        if (publicBodies.Count > 0) {
            DebugPrint("Result.PublicBodiesHashPreviews " + string.Join("|", publicBodies.Select(b => _hash.Compute(b).Substring(0, 4))));
        }
        return new HasherIntermediate(structureLines, publicBodies, internalBodies, xmlFirst, cosmeticSb.ToString());
    }

    /// <summary>Hash intermediate data to final TypeHashes.</summary>
    public TypeHashes Hash(HasherIntermediate intermediate) {
        DebugPrint("Hash StructLineCount=" + intermediate.StructureLines.Count + " First=" + (intermediate.StructureLines.FirstOrDefault() ?? "<none>"));

        var structureHash = _hash.Compute("STRUCT|" + string.Join("\n", intermediate.StructureLines));
        var publicImplHash = _hash.Compute("PUB|" + string.Join("\n", intermediate.PublicBodies));
        var internalImplHash = _hash.Compute("INT|" + string.Join("\n", intermediate.InternalBodies));
        var xmlDocHash = string.IsNullOrWhiteSpace(intermediate.XmlFirstLine) ? string.Empty : _hash.Compute("XML|" + intermediate.XmlFirstLine.Trim());
        var cosmeticHash = _hash.Compute("COS|" + intermediate.CosmeticRaw);
        var implHash = _hash.Compute("IMPL|v1|" + publicImplHash + "|" + internalImplHash);
        return new TypeHashes(structureHash, publicImplHash, internalImplHash, xmlDocHash, cosmeticHash, implHash);
    }

    private static bool IsConsideredStructure(ISymbol symbol, HashConfig cfg) {
        return symbol.DeclaredAccessibility switch {
            Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => cfg.IncludeInternalInStructureHash,
            _ => false
        };
    }

    private void CollectBody(IMethodSymbol method, List<string> publicBodies, List<string> internalBodies) {
        if (method.DeclaringSyntaxReferences.Length == 0) { return; }
        foreach (var r in method.DeclaringSyntaxReferences) {
            if (r.GetSyntax() is MethodDeclarationSyntax mds) {
                var body = mds.Body != null ? mds.Body.ToFullString() : mds.ExpressionBody?.Expression.ToFullString() ?? string.Empty;
                body = _trivia.Strip(body);
                DebugPrint($"Body {method.Name} RawLen={body.Length} HashInputPreview={(body.Length > 40 ? body.Substring(0, 40) : body)}");

                if (IsPublicLike(method)) {
                    publicBodies.Add(body);
                }
                else {
                    internalBodies.Add(body);
                }
            }
        }
    }

    private void CollectAccessorBodies(IPropertySymbol prop, List<string> publicBodies, List<string> internalBodies) {
        foreach (var r in prop.DeclaringSyntaxReferences) {
            if (r.GetSyntax() is PropertyDeclarationSyntax pds) {
                foreach (var acc in pds.AccessorList?.Accessors ?? default!) {
                    var body = acc.Body != null ? acc.Body.ToFullString() : acc.ExpressionBody?.Expression.ToFullString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(body)) { continue; }
                    body = _trivia.Strip(body);
                    if (IsPublicLike(prop)) {
                        publicBodies.Add(body);
                    }
                    else {
                        internalBodies.Add(body);
                    }
                }
            }
        }
    }

    private static bool IsPublicLike(ISymbol s) => s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

    private static string SignatureOf(ISymbol s) => s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string GenericArity(INamedTypeSymbol s) => s.Arity == 0 ? string.Empty : "`" + s.Arity;

    private static int MemberKindPriority(ISymbol m) => m switch {
        INamedTypeSymbol => 0, // nested type first
        IFieldSymbol => 1,
        IPropertySymbol => 2,
        IEventSymbol => 3,
        IMethodSymbol => 4,
        _ => 9
    };

    private static string BuildAccessibility(ISymbol s) => s.DeclaredAccessibility switch {
        Accessibility.Public => "public ",
        Accessibility.Protected => "protected ",
        Accessibility.ProtectedOrInternal => "protected internal ",
        Accessibility.Internal => "internal ",
        _ => string.Empty
    };

    private static string BuildMethodSignature(IMethodSymbol ms, HashConfig cfg) {
        var sb = new StringBuilder();
        sb.Append(BuildAccessibility(ms));
        if (ms.IsStatic) {
            sb.Append("static ");
        }

        if (ms.IsAbstract) {
            sb.Append("abstract ");
        }
        else if (ms.IsVirtual && !ms.IsOverride) {
            sb.Append("virtual ");
        }

        sb.Append(ms.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        sb.Append(' ');
        sb.Append(ms.Name);
        if (ms.Arity > 0) {
            sb.Append('<').Append(string.Join(',', ms.TypeParameters.Select(tp => tp.Name))).Append('>');
        }

        var paramTypes = ms.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        sb.Append('(').Append(string.Join(',', paramTypes)).Append(')');
        return sb.ToString().Trim();
    }

    private static string BuildPropertySignature(IPropertySymbol ps) {
        var sb = new StringBuilder();
        sb.Append(BuildAccessibility(ps));
        if (ps.IsStatic) {
            sb.Append("static ");
        }

        sb.Append(ps.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Append(' ').Append(ps.Name).Append(" { ");
        if (ps.GetMethod != null && IsPublicLike(ps.GetMethod)) {
            sb.Append("get; ");
        }

        if (ps.SetMethod != null && IsPublicLike(ps.SetMethod)) {
            sb.Append("set; ");
        }

        return sb.Append('}').ToString().Replace("; }", ";}").Trim();
    }

    private static string BuildFieldSignature(IFieldSymbol fs) {
        var sb = new StringBuilder();
        sb.Append(BuildAccessibility(fs));
        if (fs.IsConst) {
            sb.Append("const ");
        }
        else if (fs.IsStatic) {
            sb.Append("static ");
        }

        sb.Append(fs.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Append(' ').Append(fs.Name);
        return sb.ToString().Trim();
    }

    private static string BuildEventSignature(IEventSymbol es) {
        var sb = new StringBuilder();
        sb.Append(BuildAccessibility(es));
        if (es.IsStatic) {
            sb.Append("static ");
        }

        sb.Append("event ").Append(es.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Append(' ').Append(es.Name);
        return sb.ToString().Trim();
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string NormalizeWhitespaceForCosmetic(string text) => text; // TODO: implement minimal normalization

    private static string ExtractFirstLine(string xml) {
        if (string.IsNullOrWhiteSpace(xml)) { return string.Empty; }
        using var reader = new System.IO.StringReader(xml);
        string? line;
        while ((line = reader.ReadLine()) != null) {
            var t = line.Trim();
            if (t.Length == 0) { continue; }
            return t.Length > 160 ? t[..160] : t;
        }
        return string.Empty;
    }

    // Strip handled by injected ITriviaStripper.
}

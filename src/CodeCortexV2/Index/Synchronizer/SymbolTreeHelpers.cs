using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeCortexV2.Index.Synchronizer {
    internal static class SymbolTreeHelpers {
        public readonly record struct ReferenceKey(string DocId, string? AssemblyName);
        public readonly record struct NameAlias(string Name, string NameType, bool IsExact);

        public static IEnumerable<ReferenceKey> EnumerateReferenceChain(INamedTypeSymbol type) {
            if (type is null) {
                yield break;
            }

            var docId = DocumentationCommentId.CreateDeclarationId(type) ?? "T:" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            var asm = type.ContainingAssembly?.Name;
            yield return new ReferenceKey(docId, asm);

            var ns = type.ContainingNamespace;
            while (ns != null && !ns.IsGlobalNamespace) {
                var nsFqn = ns.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
                yield return new ReferenceKey("N:" + nsFqn, null);
                ns = ns.ContainingNamespace;
            }
        }

        public static IEnumerable<NameAlias> EnumerateTypeAliases(INamedTypeSymbol type) {
            if (type is null) {
                yield break;
            }

            var simple = type.Name; // e.g., List`1 -> Name == "List"
            var arity = type.Arity;
            var docIdName = arity > 0 ? simple + "`" + arity.ToString() : simple; // List`1
            var fqnSimple = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat); // List<T>
            var genericBase = simple; // List

            // Exact
            yield return new NameAlias(docIdName, "DocId", true);
            yield return new NameAlias(fqnSimple, "FQN", true);

            // Non-exact (generic base)
            yield return new NameAlias(genericBase, "GenericBase", false);

            // Ignore-case variants (only if different)
            var docIdLower = docIdName.ToLowerInvariant();
            var fqnLower = fqnSimple.ToLowerInvariant();
            var baseLower = genericBase.ToLowerInvariant();

            if (!string.Equals(docIdLower, docIdName, StringComparison.Ordinal)) {
                yield return new NameAlias(docIdLower, "DocId+IgnoreCase", false);
            }
            if (!string.Equals(fqnLower, fqnSimple, StringComparison.Ordinal)) {
                yield return new NameAlias(fqnLower, "FQN+IgnoreCase", false);
            }
            if (!string.Equals(baseLower, genericBase, StringComparison.Ordinal)) {
                yield return new NameAlias(baseLower, "GenericBase+IgnoreCase", false);
            }
        }

        public static IEnumerable<NameAlias> EnumerateNamespaceAliases(INamespaceSymbol ns) {
            if (ns is null || ns.IsGlobalNamespace) {
                yield break;
            }

            var name = ns.Name;
            if (string.IsNullOrEmpty(name)) {
                yield break;
            }

            yield return new NameAlias(name, "Exact", true);
            var lower = name.ToLowerInvariant();
            if (!string.Equals(lower, name, StringComparison.Ordinal)) {
                yield return new NameAlias(lower, "IgnoreCase", false);
            }
        }
    }
}


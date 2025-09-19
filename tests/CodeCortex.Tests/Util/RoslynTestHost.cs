using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeCortex.Tests.Util;

internal static class RoslynTestHost {
    public static (Compilation compilation, INamedTypeSymbol type) CreateSingleType(string source, string typeName) {
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var refs = new List<MetadataReference> {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)
        };
        var compilation = CSharpCompilation.Create("TestAsm", new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var type = compilation.GetTypeByMetadataName(typeName);
        if (type == null) {
            // Fallback: iterate global namespace
            var stack = new Stack<INamespaceOrTypeSymbol>();
            stack.Push(compilation.GlobalNamespace);
            while (stack.Count > 0 && type == null) {
                var cur = stack.Pop();
                foreach (var member in cur.GetMembers()) {
                    if (member is INamespaceOrTypeSymbol nts) {
                        stack.Push(nts);
                    }

                    if (member is INamedTypeSymbol nts2) {
                        var fqn = nts2.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).TrimStart(new char[] { 'g', 'l', 'o', 'b', 'a', 'l', ':', ':' }); // global:: prefix removal
                        if (fqn == typeName || nts2.Name == typeName || fqn.EndsWith(typeName)) {
                            type = nts2;
                            break;
                        }
                    }
                }
            }
        }
        if (type == null) {
            // collect diagnostics for easier debugging
            var all = new List<string>();
            void Collect(INamespaceSymbol ns, string prefix) {
                foreach (var m in ns.GetTypeMembers()) {
                    all.Add(prefix + m.Name);
                }

                foreach (var child in ns.GetNamespaceMembers()) {
                    Collect(child, prefix + child.Name + ".");
                }
            }
            Collect(compilation.GlobalNamespace, string.Empty);
            throw new System.InvalidOperationException("Type not found: " + typeName + "; available: " + string.Join(',', all));
        }
        return (compilation, type);
    }
}

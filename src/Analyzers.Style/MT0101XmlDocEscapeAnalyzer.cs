using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style {
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MT0101XmlDocEscapeAnalyzer : DiagnosticAnalyzer {
        public const string DiagnosticId = "MT0101";
        private static readonly LocalizableString Title = "Unescaped angle bracket in XML doc text";
        private static readonly LocalizableString Message = "XML documentation text contains unescaped '<' or '>' (use &lt; and &gt;)";
        private const string Category = "Documentation";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            Message,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeDocTrivia,
                SyntaxKind.SingleLineDocumentationCommentTrivia,
                SyntaxKind.MultiLineDocumentationCommentTrivia);
        }

        private static void AnalyzeDocTrivia(SyntaxNodeAnalysisContext ctx) {
            if (ctx.Node is not DocumentationCommentTriviaSyntax doc) {
                return;
            }

            var original = doc.ToFullString();
            // Fast check: if no angle brackets at all, skip
            if (original.IndexOf('<') < 0 && original.IndexOf('>') < 0) {
                return;
            }

            var fixedText = XmlDocAngleEscaper.EscapeAngles(doc, original);
            if (!string.Equals(fixedText, original, StringComparison.Ordinal)) {
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, doc.GetLocation()));
            }
        }
    }
}


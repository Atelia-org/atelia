using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.Analyzers.Style {
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MT0101XmlDocEscapeCodeFix)), Shared]
    public sealed class MT0101XmlDocEscapeCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(MT0101XmlDocEscapeAnalyzer.DiagnosticId);
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) {
                return;
            }

            var diag = context.Diagnostics.First();
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Escape angle brackets in XML doc text",
                    createChangedDocument: ct => FixAsync(context.Document, diag.Location.SourceSpan, ct),
                    equivalenceKey: "EscapeXmlDocAngles"),
                diag);
        }

        private static async Task<Document> FixAsync(Document doc, TextSpan span, CancellationToken ct) {
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) {
                return doc;
            }

            var trivia = root.FindTrivia(span.Start, findInsideTrivia: true);
            var docTrivia = trivia.GetStructure() as DocumentationCommentTriviaSyntax;
            if (docTrivia == null) {
                docTrivia = root.DescendantTrivia(span, descendIntoTrivia: true)
                    .Select(t => t.GetStructure())
                    .OfType<DocumentationCommentTriviaSyntax>()
                    .FirstOrDefault();
            }

            if (docTrivia == null) {
                return doc;
            }

            var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
            var original = text.ToString(docTrivia.FullSpan);
            var fixedText = XmlDocAngleEscaper.EscapeAngles(docTrivia, original);
            if (string.Equals(fixedText, original, StringComparison.Ordinal)) {
                return doc;
            }

            var newText = text.Replace(docTrivia.FullSpan, fixedText);
            return doc.WithText(newText);
        }

    }
}


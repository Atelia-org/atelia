using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style;

/// <summary>
/// Preserves the source-module dependency direction of the consolidated
/// SessionJournal RecapGrid assembly.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RG0001RecapGridModuleDependencyAnalyzer
    : DiagnosticAnalyzer {
    public const string DiagnosticId = "RG0001";

    public static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "RecapGrid module dependency must follow the locked graph",
        "Module '{0}' must not depend on module '{1}' through symbol '{2}'",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "RecapGrid source modules must retain their locked dependency "
            + "direction after their former project boundaries are consolidated."
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSemanticModelAction(AnalyzeSemanticModel);
    }

    private static void AnalyzeSemanticModel(SemanticModelAnalysisContext context) {
        string path = context.SemanticModel.SyntaxTree.FilePath;
        SourceModule? source = ClassifySourceModule(path);
        if (source is null) {
            return;
        }

        SyntaxNode root = context.SemanticModel.SyntaxTree.GetRoot(
            context.CancellationToken
        );
        var violations = new Dictionary<TargetModule, Violation>();

        foreach (SyntaxNode node in root.DescendantNodesAndSelf()) {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (node is ExpressionSyntax expression) {
                SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(
                    expression,
                    context.CancellationToken
                );
                InspectSymbol(symbolInfo.Symbol, expression.GetLocation());
                foreach (ISymbol candidate in symbolInfo.CandidateSymbols) {
                    InspectSymbol(candidate, expression.GetLocation());
                }

            }
            else if (node is AttributeSyntax attribute) {
                SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(
                    attribute,
                    context.CancellationToken
                );
                InspectSymbol(
                    symbolInfo.Symbol,
                    attribute.GetLocation()
                );
                foreach (ISymbol candidate in symbolInfo.CandidateSymbols) {
                    InspectSymbol(candidate, attribute.GetLocation());
                }
            }
            else if (node is ConstructorInitializerSyntax initializer) {
                SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(
                    initializer,
                    context.CancellationToken
                );
                InspectSymbol(
                    symbolInfo.Symbol,
                    initializer.GetLocation()
                );
                foreach (ISymbol candidate in symbolInfo.CandidateSymbols) {
                    InspectSymbol(candidate, initializer.GetLocation());
                }
            }
        }

        foreach (KeyValuePair<TargetModule, Violation> entry in
                 violations.OrderBy(static item => item.Key)) {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                entry.Value.Location,
                Display(source.Value),
                Display(entry.Key),
                entry.Value.SymbolName
            ));
        }

        void InspectSymbol(ISymbol? symbol, Location location) {
            if (symbol is null) {
                return;
            }
            if (symbol is IAliasSymbol alias) {
                InspectSymbol(alias.Target, location);
                return;
            }
            if (symbol is IMethodSymbol method
                && method.MethodKind is
                    MethodKind.UserDefinedOperator
                    or MethodKind.Conversion) {
                // An allowed contract can expose a foreign value type whose
                // operator is then selected without the source naming that
                // foreign module. The named contract edge is checked instead.
                return;
            }
            if (symbol is ITypeSymbol type) {
                InspectType(type, location, depth: 0);
                return;
            }
            if (symbol is INamespaceSymbol) {
                // Namespace qualifiers do not by themselves create a runtime
                // dependency. The referenced type/member is inspected separately.
                return;
            }
            if (!symbol.IsStatic
                && symbol is not IMethodSymbol {
                    MethodKind: MethodKind.Constructor
                }
                && symbol is not IMethodSymbol { ReducedFrom: not null }) {
                // Instance members reached through an allowed contract do not
                // create a new explicit module edge. An explicitly named
                // receiver type is diagnosed at its TypeSyntax instead.
                return;
            }

            InspectNamespace(
                symbol.ContainingNamespace,
                location,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            );
        }

        void InspectType(
            ITypeSymbol? type,
            Location location,
            int depth
        ) {
            if (type is null || depth > 16) {
                return;
            }

            switch (type) {
                case IArrayTypeSymbol array:
                    InspectType(array.ElementType, location, depth + 1);
                    return;
                case IPointerTypeSymbol pointer:
                    InspectType(pointer.PointedAtType, location, depth + 1);
                    return;
                case IFunctionPointerTypeSymbol functionPointer:
                    InspectType(
                        functionPointer.Signature.ReturnType,
                        location,
                        depth + 1
                    );
                    foreach (IParameterSymbol parameter in
                             functionPointer.Signature.Parameters) {
                        InspectType(parameter.Type, location, depth + 1);
                    }
                    return;
                case INamedTypeSymbol named:
                    InspectNamespace(
                        named.ContainingNamespace,
                        location,
                        named.ToDisplayString(
                            SymbolDisplayFormat.CSharpErrorMessageFormat
                        )
                    );
                    foreach (ITypeSymbol argument in named.TypeArguments) {
                        InspectType(argument, location, depth + 1);
                    }
                    if (named.ContainingType is not null) {
                        InspectType(named.ContainingType, location, depth + 1);
                    }
                    return;
            }
        }

        void InspectNamespace(
            INamespaceSymbol? namespaceSymbol,
            Location location,
            string symbolName
        ) {
            if (namespaceSymbol is null || namespaceSymbol.IsGlobalNamespace) {
                return;
            }
            TargetModule? target = ClassifyTargetModule(
                namespaceSymbol.ToDisplayString()
            );
            if (target is null || IsAllowed(source.Value, target.Value)) {
                return;
            }
            if (!violations.ContainsKey(target.Value)) {
                violations.Add(
                    target.Value,
                    new Violation(location, symbolName)
                );
            }
        }
    }

    private static SourceModule? ClassifySourceModule(string path) {
        string normalized = "/" + path.Replace('\\', '/').TrimStart('/');
        if (ContainsPathSegment(normalized, "/bin/")
            || ContainsPathSegment(normalized, "/obj/")) {
            return null;
        }
        if (ContainsPathSegment(normalized, "/prototypes/Galatea.RecapGrid/")) {
            return SourceModule.GalateaAssets;
        }

        foreach (SourceModule module in RecapGridSourceModules) {
            string name = Display(module);
            if (ContainsPathSegment(
                    normalized,
                    $"/prototypes/SessionJournal.RecapGrid.{name}/"
                )
                || ContainsPathSegment(
                    normalized,
                    $"/prototypes/SessionJournal.RecapGrid/{name}/"
                )) {
                return module;
            }
        }
        return null;
    }

    private static TargetModule? ClassifyTargetModule(string namespaceName) {
        const string recapGrid = "Atelia.SessionJournal.RecapGrid";
        const string timeline = "Atelia.SessionJournal.HistoryTimeline";

        if (IsNamespace(namespaceName, timeline)) {
            return TargetModule.HistoryTimeline;
        }
        foreach (TargetModule module in NamedRecapGridTargetModules) {
            if (IsNamespace(namespaceName, recapGrid + "." + Display(module))) {
                return module;
            }
        }
        if (IsNamespace(namespaceName, recapGrid)) {
            // Abstractions owns the root namespace, including future nested
            // contract namespaces not claimed by a named product module.
            return TargetModule.Abstractions;
        }
        return null;
    }

    private static bool IsAllowed(SourceModule source, TargetModule target) {
        if (source != SourceModule.GalateaAssets
            && string.Equals(
                Display(source),
                Display(target),
                StringComparison.Ordinal
            )) {
            return true;
        }

        return source switch {
            SourceModule.Abstractions => target is TargetModule.HistoryTimeline,
            SourceModule.Control => target is
                TargetModule.HistoryTimeline
                or TargetModule.Abstractions,
            SourceModule.Store => target is TargetModule.Abstractions,
            SourceModule.Manager => target is
                TargetModule.HistoryTimeline
                or TargetModule.Abstractions
                or TargetModule.Control
                or TargetModule.Store,
            SourceModule.Runtime => target is
                TargetModule.Abstractions
                or TargetModule.Manager,
            SourceModule.Getter => target is
                TargetModule.HistoryTimeline
                or TargetModule.Cadence
                or TargetModule.Abstractions
                or TargetModule.Control
                or TargetModule.Store,
            SourceModule.Online => target is
                TargetModule.HistoryTimeline
                or TargetModule.Cadence
                // Online explicitly uses RecapGridLimits from Abstractions;
                // the old project received that compile asset transitively.
                or TargetModule.Abstractions
                or TargetModule.Manager
                or TargetModule.Getter,
            SourceModule.AgentControl => target is
                TargetModule.HistoryTimeline
                or TargetModule.Abstractions
                or TargetModule.Control
                or TargetModule.Manager,
            SourceModule.GalateaAssets => target is
                TargetModule.Abstractions
                or TargetModule.Control,
            _ => false
        };
    }

    private static bool ContainsPathSegment(string path, string segment) =>
        path.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsNamespace(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.Ordinal)
        || actual.StartsWith(expected + ".", StringComparison.Ordinal);

    private static string Display(SourceModule module) => module switch {
        SourceModule.GalateaAssets => "Galatea.RecapGrid",
        _ => module.ToString()
    };

    private static string Display(TargetModule module) => module switch {
        TargetModule.HistoryTimeline => "HistoryTimeline",
        _ => module.ToString()
    };

    private static readonly ImmutableArray<SourceModule> RecapGridSourceModules =
        ImmutableArray.Create(
            SourceModule.Abstractions,
            SourceModule.Control,
            SourceModule.Store,
            SourceModule.Manager,
            SourceModule.Runtime,
            SourceModule.Getter,
            SourceModule.Online,
            SourceModule.AgentControl
        );

    private static readonly ImmutableArray<TargetModule>
        NamedRecapGridTargetModules = ImmutableArray.Create(
            TargetModule.Control,
            TargetModule.Store,
            TargetModule.Manager,
            TargetModule.Runtime,
            TargetModule.Getter,
            TargetModule.Online,
            TargetModule.AgentControl,
            TargetModule.Cadence,
            TargetModule.Hosting
        );

    private enum SourceModule {
        Abstractions,
        Control,
        Store,
        Manager,
        Runtime,
        Getter,
        Online,
        AgentControl,
        GalateaAssets
    }

    private enum TargetModule {
        HistoryTimeline,
        Abstractions,
        Control,
        Store,
        Manager,
        Runtime,
        Getter,
        Online,
        AgentControl,
        Cadence,
        Hosting
    }

    private sealed class Violation {
        public Violation(Location location, string symbolName) {
            Location = location;
            SymbolName = symbolName;
        }

        public Location Location { get; }
        public string SymbolName { get; }
    }
}

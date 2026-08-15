using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Atelia.Analyzers.Style.Tests;

public sealed class RG0001RecapGridModuleDependencyTests {
    private const string ModuleDeclarations = """
        namespace Atelia.SessionJournal.HistoryTimeline {
            public readonly struct TimelineValue {
                public static bool operator ==(
                    TimelineValue left,
                    TimelineValue right
                ) => true;
                public static bool operator !=(
                    TimelineValue left,
                    TimelineValue right
                ) => false;
                public override bool Equals(object value) =>
                    value is TimelineValue;
                public override int GetHashCode() => 0;
                public string Value => "timeline";
            }
        }
        namespace Atelia.SessionJournal.RecapGrid {
            public sealed class AbstractValue { }
            public static class RecapGridLimits {
                public const int MaximumColumnCount = 10;
            }
            public sealed class AbstractCarrier {
                public Atelia.SessionJournal.HistoryTimeline.TimelineValue? Timeline {
                    get;
                } = new();
            }
        }
        namespace Atelia.SessionJournal.RecapGrid.Control {
            public sealed class ControlValue { }
            public static class ControlExtensions {
                public static void ForbiddenExtension(
                    this Atelia.SessionJournal.RecapGrid.AbstractValue value
                ) { }
            }
        }
        namespace Atelia.SessionJournal.RecapGrid.Store {
            public sealed class StoreValue { }
        }
        namespace Atelia.SessionJournal.RecapGrid.Manager {
            public sealed class ManagerValue { }
            public interface IManagerConstraint { }
        }
        namespace Atelia.SessionJournal.RecapGrid.Runtime {
            public sealed class RuntimeValue { }
        }
        namespace Atelia.SessionJournal.RecapGrid.Contracts {
            public sealed class NestedAbstractValue { }
        }
        """;

    [Fact]
    public async Task AllowsLockedEdgesFromOldAndConsolidatedSourcePaths() {
        Diagnostic[] diagnostics = await AnalyzeAsync(
            (
                "/repo/prototypes/SessionJournal.RecapGrid.Manager/Allowed.cs",
                """
                using Atelia.SessionJournal.HistoryTimeline;
                using Atelia.SessionJournal.RecapGrid.Control;
                using Atelia.SessionJournal.RecapGrid.Store;
                namespace Atelia.SessionJournal.RecapGrid.Manager;
                public sealed class AllowedOldPath {
                    public ControlValue Signature(ControlValue value) {
                        _ = new TimelineValue();
                        _ = new StoreValue();
                        return value;
                    }
                }
                """
            ),
            (
                "prototypes/SessionJournal.RecapGrid/Runtime/Allowed.cs",
                """
                using Atelia.SessionJournal.RecapGrid;
                using Atelia.SessionJournal.RecapGrid.Contracts;
                using Atelia.SessionJournal.RecapGrid.Manager;
                namespace Atelia.SessionJournal.RecapGrid.Runtime;
                public sealed class AllowedNewPath {
                    public AbstractValue Signature(
                        ManagerValue value,
                        NestedAbstractValue nested
                    ) => new();
                }
                """
            ),
            (
                "/repo/prototypes/SessionJournal.RecapGrid.Store/Allowed.cs",
                """
                using Atelia.SessionJournal.RecapGrid;
                namespace Atelia.SessionJournal.RecapGrid.Store;
                public sealed class AllowedPublicSignatureClosure {
                    public string Read(AbstractCarrier carrier) {
                        _ = carrier.Timeline!.Value == default;
                        return carrier.Timeline?.Value ?? "missing";
                    }
                }
                """
            ),
            (
                "/repo/prototypes/SessionJournal.RecapGrid.Online/Limits.cs",
                """
                using Atelia.SessionJournal.RecapGrid;
                namespace Atelia.SessionJournal.RecapGrid.Online;
                public static class AllowedExplicitCurrentEdge {
                    public static int MaximumCalls =>
                        RecapGridLimits.MaximumColumnCount;
                }
                """
            )
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RejectsForbiddenSignatureEdgeFromConsolidatedPath() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            "/repo/prototypes/SessionJournal.RecapGrid/Abstractions/Forbidden.cs",
            """
            using Atelia.SessionJournal.RecapGrid.Manager;
            namespace Atelia.SessionJournal.RecapGrid;
            public interface IForbiddenSignature {
                ManagerValue Value { get; }
            }
            """
        )));

        Assert.Equal("RG0001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Abstractions", diagnostic.GetMessage());
        Assert.Contains("Manager", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RejectsForbiddenGenericConstraintWithoutMethodBody() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            "/repo/prototypes/SessionJournal.RecapGrid/Abstractions/Constraint.cs",
            """
            using Atelia.SessionJournal.RecapGrid.Manager;
            namespace Atelia.SessionJournal.RecapGrid;
            public sealed class ForbiddenConstraint<T>
                where T : IManagerConstraint { }
            """
        )));

        Assert.Equal("RG0001", diagnostic.Id);
        Assert.Contains("Abstractions", diagnostic.GetMessage());
        Assert.Contains("Manager", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RejectsForbiddenMethodBodyEdgeFromOldPath() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            "/repo/prototypes/SessionJournal.RecapGrid.Store/Forbidden.cs",
            """
            using Atelia.SessionJournal.RecapGrid.Control;
            namespace Atelia.SessionJournal.RecapGrid.Store;
            public sealed class ForbiddenBody {
                public void Execute() {
                    _ = new ControlValue();
                }
            }
            """
        )));

        Assert.Equal("RG0001", diagnostic.Id);
        Assert.Contains("Store", diagnostic.GetMessage());
        Assert.Contains("Control", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RejectsForbiddenExtensionFromAllowedReceiver() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            "/repo/prototypes/SessionJournal.RecapGrid.Store/Extension.cs",
            """
            using Atelia.SessionJournal.RecapGrid;
            using Atelia.SessionJournal.RecapGrid.Control;
            namespace Atelia.SessionJournal.RecapGrid.Store;
            public sealed class ForbiddenExtensionUse {
                public void Execute(AbstractValue value) {
                    value.ForbiddenExtension();
                }
            }
            """
        )));

        Assert.Equal("RG0001", diagnostic.Id);
        Assert.Contains("Store", diagnostic.GetMessage());
        Assert.Contains("Control", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RejectsGalateaAssetDependencyOnRuntime() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            "/repo/prototypes/Galatea.RecapGrid/Assets.cs",
            """
            using Atelia.SessionJournal.RecapGrid.Runtime;
            namespace Atelia.Galatea.RecapGrid;
            public sealed class Assets {
                public RuntimeValue Create() => new();
            }
            """
        )));

        Assert.Equal("RG0001", diagnostic.Id);
        Assert.Contains("Galatea.RecapGrid", diagnostic.GetMessage());
        Assert.Contains("Runtime", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("/repo/prototypes/SessionJournal.RecapGrid/Loose.cs")]
    [InlineData(
        "/repo/prototypes/SessionJournal.RecapGrid/FutureModule/Loose.cs"
    )]
    public async Task RejectsUnclassifiedConsolidatedSource(string path) {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            path,
            """
            namespace Atelia.SessionJournal.RecapGrid;
            public sealed class LooseSource { }
            """
        )));

        Assert.Equal("RG0002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("eight owned module directories", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RejectsModulePathAndNamespaceOwnerMismatch() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync((
            "/repo/prototypes/SessionJournal.RecapGrid/Manager/Mismatch.cs",
            """
            namespace Atelia.SessionJournal.RecapGrid;
            public sealed class MismatchedManagerSource { }
            """
        )));

        Assert.Equal("RG0002", diagnostic.Id);
        Assert.Contains("Manager", diagnostic.GetMessage());
        Assert.Contains("Atelia.SessionJournal.RecapGrid", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RejectsPartialTypeCrossingModuleRoots() {
        Diagnostic diagnostic = Assert.Single(await AnalyzeAsync(
            (
                "/repo/prototypes/SessionJournal.RecapGrid/Manager/Part1.cs",
                """
                namespace Atelia.SessionJournal.RecapGrid.Manager;
                public sealed partial class CrossOwnedType { }
                """
            ),
            (
                "/repo/prototypes/SessionJournal.RecapGrid/Store/Part2.cs",
                """
                namespace Atelia.SessionJournal.RecapGrid.Manager;
                public sealed partial class CrossOwnedType { }
                """
            )
        ));

        Assert.Equal("RG0002", diagnostic.Id);
        Assert.EndsWith(
            "/Store/Part2.cs",
            diagnostic.Location.GetLineSpan().Path,
            StringComparison.Ordinal
        );
        Assert.Contains("Store", diagnostic.GetMessage());
        Assert.Contains("RecapGrid.Manager", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DeclaringPathPreventsNamespaceLaunderingWithinCompilation() {
        Diagnostic[] diagnostics = await AnalyzeAsync(
            (
                "/repo/prototypes/SessionJournal.RecapGrid/Manager/Laundered.cs",
                """
                namespace Atelia.SessionJournal.RecapGrid;
                public sealed class LaunderedManagerValue { }
                """
            ),
            (
                "/repo/prototypes/SessionJournal.RecapGrid/Store/Use.cs",
                """
                using Atelia.SessionJournal.RecapGrid;
                namespace Atelia.SessionJournal.RecapGrid.Store;
                public sealed class LaunderingConsumer {
                    public object Create() => new LaunderedManagerValue();
                }
                """
            )
        );

        Diagnostic ownership = Assert.Single(diagnostics, static diagnostic =>
            diagnostic.Id == "RG0002"
        );
        Assert.EndsWith(
            "/Manager/Laundered.cs",
            ownership.Location.GetLineSpan().Path,
            StringComparison.Ordinal
        );

        Diagnostic edge = Assert.Single(diagnostics, static diagnostic =>
            diagnostic.Id == "RG0001"
        );
        Assert.EndsWith(
            "/Store/Use.cs",
            edge.Location.GetLineSpan().Path,
            StringComparison.Ordinal
        );
        Assert.Contains("Store", edge.GetMessage());
        Assert.Contains("Manager", edge.GetMessage());
    }

    private static async Task<Diagnostic[]> AnalyzeAsync(
        params (string Path, string Source)[] sources
    ) {
        SyntaxTree[] syntaxTrees = sources.Select(source =>
            CSharpSyntaxTree.ParseText(source.Source, path: source.Path)
        ).ToArray();

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Atelia.RecapGrid.ModuleDependency.Tests",
            syntaxTrees,
            GetMetadataReferences().Append(CreateModuleMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
        );

        var analyzer = new RG0001RecapGridModuleDependencyAnalyzer();
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Where(static diagnostic => diagnostic.Id is "RG0001" or "RG0002")
            .OrderBy(static diagnostic =>
                diagnostic.Location.GetLineSpan().Path,
                StringComparer.Ordinal
            )
            .ThenBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .ToArray();
    }

    private static MetadataReference CreateModuleMetadataReference() {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Atelia.RecapGrid.ModuleFixtures",
            [CSharpSyntaxTree.ParseText(ModuleDeclarations)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics)
        );
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly =>
                !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location)
            )
            .Select(static assembly =>
                MetadataReference.CreateFromFile(assembly.Location)
            );
}

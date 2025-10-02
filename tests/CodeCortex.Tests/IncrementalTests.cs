using CodeCortex.Core.Index;
using CodeCortex.Workspace.Incremental;
using Xunit;

namespace Atelia.CodeCortex.Tests;

public class IncrementalTests {
    [Fact]
    public void ChangeClassifier_Folds_RenameAndModify() {
        var clf = new ChangeClassifier();
        var raw = new List<RawFileChange> {
            RawFileChange.Renamed("A.cs","B.cs"),
            RawFileChange.Changed("B.cs"),
        };
        var cs = clf.Classify(raw);
        Assert.Contains(cs, c => c.Kind == ClassifiedKind.Rename && c.OldPath == "B.cs");
        Assert.Contains(cs, c => c.Kind == ClassifiedKind.Add && c.Path == "B.cs");
    }

    [Fact]
    public void ImpactAnalyzer_Delete_RemovesMappedTypes() {
        var idx = new CodeCortexIndex();
        idx.Types.Add(new TypeEntry { Id = "T1", Fqn = "X.A", Kind = "Class", Files = new List<string> { "D1.cs" } });
        idx.Maps.FqnIndex["X.A"] = "T1";
        var analyzer = new ImpactAnalyzer();
        var changes = new List<ClassifiedFileChange> { new ClassifiedFileChange("D1.cs", ClassifiedKind.Delete) };
        var r = analyzer.Analyze(idx, changes, _ => null!, default);
        Assert.Empty(r.AffectedTypeIds);
        Assert.Contains("T1", r.RemovedTypeIds);
    }
}

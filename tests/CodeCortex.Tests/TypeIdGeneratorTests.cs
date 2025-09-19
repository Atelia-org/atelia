using Xunit;
using CodeCortex.Core.Ids;
using CodeCortex.Tests.Util;

namespace CodeCortex.Tests;

public class TypeIdGeneratorTests {
    [Fact]
    public void GeneratesStableId_ForSameType() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var src = "namespace N { public class C {} }";
        var (_, t1) = RoslynTestHost.CreateSingleType(src, "N.C");
        var (_, t2) = RoslynTestHost.CreateSingleType(src, "N.C");
        var id1 = TypeIdGenerator.GetId(t1);
        var id2 = TypeIdGenerator.GetId(t2);
        Assert.Equal(id1, id2);
    }
}

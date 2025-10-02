using Xunit;
using CodeCortex.Core.Hashing;
using Atelia.CodeCortex.Tests.Util;

namespace Atelia.CodeCortex.Tests;

public class TypeHasherTests {
    // Use real newlines so that methods are not swallowed by a line comment.
    private const string Sample = @"namespace N {
public class C {
/*comment*/
public int Add(int a,int b){ // add
    return a+b; }
internal void X(){ /*noop*/ }
}
}"; // baseline
    private const string SampleBodyChanged = @"namespace N {
public class C {
/*comment*/
public int Add(int a,int b){ // add
    return a-b; }
internal void X(){ /*noop*/ }
}
}"; // only method body changed
    private const string SampleMethodAdded = @"namespace N {
public class C {
/*comment*/
public int Add(int a,int b){ return a+b; }
public int Sub(int a,int b){ return a-b; }
internal void X(){ }
}
}"; // new public method added

    [Fact]
    public void StructureHash_Stable_WhenOnlyBodyChanges() {
        var (comp, type) = RoslynTestHost.CreateSingleType(Sample, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var h1 = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        // modify body (simulate by re-parsing with different impl):
        var (comp2, type2) = RoslynTestHost.CreateSingleType(SampleBodyChanged, "N.C");
        var h2 = hasher.Compute(type2, System.Array.Empty<string>(), new HashConfig());
        System.Console.WriteLine("[DBG] Structure1=" + h1.Structure + " Structure2=" + h2.Structure + " Impl1=" + h1.Impl + " Impl2=" + h2.Impl);
        Assert.Equal(h1.Structure, h2.Structure); // structure unchanged
        Assert.NotEqual(h1.Impl, h2.Impl); // impl changed
    }

    [Fact]
    public void StructureHash_Changes_WhenPublicSignatureChanges() {
        var (_, t1) = RoslynTestHost.CreateSingleType(Sample, "N.C");
        var (_, t2) = RoslynTestHost.CreateSingleType(SampleMethodAdded, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var h1 = hasher.Compute(t1, System.Array.Empty<string>(), new HashConfig());
        var h2 = hasher.Compute(t2, System.Array.Empty<string>(), new HashConfig());
        System.Console.WriteLine("[DBG] StructChange S1=" + h1.Structure + " S2=" + h2.Structure + " Bodies1=" + h1.PublicImpl + " Bodies2=" + h2.PublicImpl);
        Assert.NotEqual(h1.Structure, h2.Structure);
    }

    private sealed class FakeHash : IHashFunction {
        public string Compute(string input, int length = 8) {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            var hex = System.BitConverter.ToString(bytes).Replace("-", string.Empty);
            return hex.Substring(0, length);
        }
    }
    private sealed class PassTrivia : ITriviaStripper { public string Strip(string c) => c; }
}

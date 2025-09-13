using System;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests {
    public class V2_QueryPreprocessor_Tests {
        [Fact]
        public void DocId_Type_IsDetected() {
            var q = "T:System.Collections.Generic.List`1";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.Equal(SymbolKinds.Type, qi.DocIdKind);
            Assert.True(qi.IsRootAnchored);
            Assert.Equal(new[] { "System", "Collections", "Generic", "List`1" }, qi.NormalizedSegments);
        }

        [Fact]
        public void GlobalRoot_And_Generic_To_DocId_Arity() {
            var q = "global::System.Collections.Generic.List<int>";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.True(qi.IsRootAnchored);
            Assert.Equal(new[] { "System", "Collections", "Generic", "List`1" }, qi.NormalizedSegments);
            Assert.Equal("list`1", qi.LowerNormalizedSegments[^1]);
            Assert.Equal("List<T>", QueryPreprocessor.ParseTypeSegment(qi.NormalizedSegments[^1]).baseName + "<T>");
        }

        [Fact]
        public void Nested_Generic_Segments_Normalized() {
            var q = "Namespace.Outer<T1>.Inner<T2>";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.False(qi.IsRootAnchored);
            Assert.Equal(new[] { "Namespace", "Outer`1", "Inner`1" }, qi.NormalizedSegments);
        }

        [Fact]
        public void Lowercase_Intent_IsTracked() {
            var q = "system.collections.generic.list<int>";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.Equal(qi.NormalizedSegments.Select(s => s.ToLowerInvariant()).ToArray(), qi.LowerNormalizedSegments);
            Assert.Equal("list`1", qi.LowerNormalizedSegments[^1]);
        }

        [Fact]
        public void Unbalanced_Generic_Is_Rejected() {
            var q = "System.Func<T"; // missing closing '>'
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.False(qi.IsRootAnchored);
            Assert.Empty(qi.NormalizedSegments);
        }
    }
}


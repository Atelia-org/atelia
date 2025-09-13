using System;
using System.Linq;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests {
    public class V2_QueryPreprocessor_Tests {
        [Fact]
        public void DocId_Type_IsDetected() {
            var q = "T:System.Collections.Generic.List`1";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.Equal(QueryPreprocessor.DocIdKind.Type, qi.Kind);
            // New behavior: Effective excludes DocId prefix ("T:") and segments are populated
            Assert.True(qi.RootConstraint);
            Assert.Equal("System.Collections.Generic.List`1", qi.Effective);
            Assert.Equal(new[] { "System", "Collections", "Generic", "List`1" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void GlobalRoot_And_Generic_To_DocId_Arity() {
            var q = "global::System.Collections.Generic.List<int>";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.True(qi.RootConstraint);
            Assert.Equal(new[] { "System", "Collections", "Generic", "List`1" }, qi.SegmentsNormalized);
            Assert.False(qi.LastIsLower);
            Assert.Equal("List<T>", QueryPreprocessor.ParseTypeSegment(qi.SegmentsOriginal[^1]).baseName + "<T>");
        }

        [Fact]
        public void Nested_Generic_Segments_Normalized() {
            var q = "Namespace.Outer<T1>.Inner<T2>";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.False(qi.RootConstraint);
            Assert.Equal(new[] { "Namespace", "Outer`1", "Inner`1" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void Lowercase_Intent_IsTracked() {
            var q = "system.collections.generic.list<int>";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.True(qi.SegmentIsLower.All(b => b));
            Assert.True(qi.LastIsLower);
            Assert.Equal("list`1", qi.LastNormalized);
        }

        [Fact]
        public void Unbalanced_Generic_Is_Rejected() {
            var q = "System.Func<T"; // missing closing '>'
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.False(qi.RootConstraint);
            Assert.Empty(qi.SegmentsNormalized);
            Assert.Empty(qi.SegmentsOriginal);
        }
    }
}


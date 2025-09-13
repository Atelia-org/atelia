using System;
using System.Linq;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests {
    public class V2_QueryPreprocessor_MoreTests {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_Or_Whitespace_Returns_Empty(string q) {
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.False(qi.RootConstraint);
            Assert.Empty(qi.SegmentsNormalized);
            Assert.Empty(qi.SegmentsOriginal);
        }

        [Fact]
        public void DocId_Namespace_IsDetected() {
            var q = "N:System.Collections";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.Equal(QueryPreprocessor.DocIdKind.Namespace, qi.Kind);
            Assert.True(qi.RootConstraint);
            Assert.Equal("System.Collections", qi.Effective);
            Assert.Equal(new[] { "System", "Collections" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void DocId_Member_IsDetected() {
            var q = "M:System.String.Length";
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.Equal(QueryPreprocessor.DocIdKind.Member, qi.Kind);
            Assert.True(qi.RootConstraint);
            Assert.Equal("System.String.Length", qi.Effective);
        }

        [Fact]
        public void RootPrefix_Is_Case_Sensitive() {
            var qi1 = QueryPreprocessor.Preprocess("global::X.Y");
            var qi2 = QueryPreprocessor.Preprocess("Global::X.Y");
            Assert.True(qi1.RootConstraint);
            Assert.False(qi2.RootConstraint);
        }

        [Fact]
        public void Segments_Split_Dotted_And_NestedPlus_On_Last() {
            var qi = QueryPreprocessor.Preprocess("Namespace.Outer+Inner<T>");
            Assert.Equal(new[] { "Namespace", "Outer", "Inner`1" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void Normalize_Generic_SingleLevel_Dictionary() {
            var qi = QueryPreprocessor.Preprocess("System.Collections.Generic.Dictionary<string,int>");
            Assert.Equal(new[] { "System", "Collections", "Generic", "Dictionary`2" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void Normalize_Nested_Generic_NoComma_Inner() {
            var qi = QueryPreprocessor.Preprocess("Wrapper<List<int>>");
            Assert.Equal(new[] { "Wrapper`1" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void Lowercase_Intent_Per_Segment_Mixed() {
            var qi = QueryPreprocessor.Preprocess("system.Collections.generic.List<int>");
            Assert.Equal(new[] { true, false, true, false }, qi.SegmentIsLower);
            Assert.False(qi.LastIsLower);
        }

        [Theory]
        [InlineData("System.Func<T")]
        [InlineData("List<int>>")]
        [InlineData("List<<int>")]
        public void Unbalanced_Angles_Are_Rejected(string q) {
            var qi = QueryPreprocessor.Preprocess(q);
            Assert.Empty(qi.SegmentsNormalized);
            Assert.Empty(qi.SegmentsOriginal);
        }

        [Fact]
        public void Double_Dots_Compressed_By_Split() {
            var qi = QueryPreprocessor.Preprocess("System..Collections");
            Assert.Equal(new[] { "System", "Collections" }, qi.SegmentsNormalized);
        }

        [Fact]
        public void No_Generic_Simple_Type() {
            var qi = QueryPreprocessor.Preprocess("System.String");
            Assert.Equal(new[] { "System", "String" }, qi.SegmentsNormalized);
        }
    }
}


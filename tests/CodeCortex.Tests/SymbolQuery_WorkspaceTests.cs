using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using CodeCortex.Workspace.SymbolQuery;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;

namespace Atelia.CodeCortex.Tests {
    public class SymbolQuery_WorkspaceTests {
        private static TypeEntry E(string fqn, string simple) => new TypeEntry(fqn, simple);

        [Fact]
        public void SimpleSymbolMatcher_NoWildcard_Categories() {
            var entries = new List<TypeEntry> {
                E("global::Foo.Bar.Baz", "Baz"),
            };

            // Exact (with global prefix)
            var exact1 = SimpleSymbolMatcher.Match(entries, "global::Foo.Bar.Baz");
            Assert.Single(exact1);
            Assert.Equal(MatchCategory.Exact, exact1[0].Category);

            // Exact (without global prefix)
            var exact2 = SimpleSymbolMatcher.Match(entries, "Foo.Bar.Baz");
            Assert.Single(exact2);
            Assert.Equal(MatchCategory.Exact, exact2[0].Category);

            // SimpleExact
            var simple = SimpleSymbolMatcher.Match(entries, "Baz");
            Assert.Single(simple);
            Assert.Equal(MatchCategory.SimpleExact, simple[0].Category);

            // Prefix
            var prefix = SimpleSymbolMatcher.Match(entries, "global::Foo.Bar");
            Assert.Single(prefix);
            Assert.Equal(MatchCategory.Prefix, prefix[0].Category);

            // Suffix (use suffix longer than simple name to avoid SimpleExact overshadow)
            var suffix = SimpleSymbolMatcher.Match(entries, "Bar.Baz");
            Assert.Single(suffix);
            Assert.Equal(MatchCategory.Suffix, suffix[0].Category);

            // Contains (case-insensitive)
            var contains = SimpleSymbolMatcher.Match(entries, "bar");
            Assert.Single(contains);
            Assert.Equal(MatchCategory.Contains, contains[0].Category);
        }

        [Fact]
        public void SimpleSymbolMatcher_Wildcard_Categories() {
            var entries = new List<TypeEntry> {
                E("global::Foo.Bar.Baz", "Baz"),
            };

            // Wildcard FQN
            var w1 = SimpleSymbolMatcher.Match(entries, "*Baz");
            Assert.Single(w1);
            Assert.Equal(MatchCategory.WildcardFqn, w1[0].Category);

            // Wildcard simple
            var w2 = SimpleSymbolMatcher.Match(entries, "B?z");
            Assert.Single(w2);
            Assert.Equal(MatchCategory.WildcardSimple, w2[0].Category);
        }

        private sealed class FakeTypeSource : ITypeSource {
            private readonly IReadOnlyList<TypeEntry> _entries;
            public FakeTypeSource(IEnumerable<TypeEntry> entries) { _entries = entries.ToList(); }
            public Task<IReadOnlyList<TypeEntry>> ListAsync(LoadedSolution loaded, CancellationToken ct = default)
                => Task.FromResult(_entries);
        }

        private static LoadedSolution DummyLoaded() {
            var adhoc = new AdhocWorkspace();
            var projId = ProjectId.CreateNewId();
            var projInfo = ProjectInfo.Create(projId, VersionStamp.Create(), "T", "T", LanguageNames.CSharp);
            adhoc.AddProject(projInfo);
            var sol = adhoc.CurrentSolution;
            var proj = sol.GetProject(projId)!;
            return new LoadedSolution(sol, new List<Project> { proj });
        }

        [Fact]
        public async Task DefaultSymbolResolver_SortsAndPages_ByFqn() {
            var entries = new[] {
                E("global::Zed.A", "A"),
                E("global::Alpha.B", "B"),
                E("global::Mary.N", "N"),
            };
            var resolver = new DefaultSymbolResolver(new FakeTypeSource(entries));
            var loaded = DummyLoaded();

            var res = await resolver.SearchAsync(loaded, pattern: "*", offset: 1, limit: 2);
            Assert.Equal(3, res.Total);
            // Sorted ascending by FQN: Alpha.B, Mary.N, Zed.A -> offset 1, take 2 => Mary.N, Zed.A
            Assert.Equal(new[] { "global::Mary.N", "global::Zed.A" }, res.Items);
        }
    }
}


using MemoTree.Core.Types;
using MemoTree.Core.Storage.Relations;

namespace MemoTree.Tests.Storage;

public class RelationGraphAndPathTests {
    [Fact]
    public void GetRelationTypes_Should_Return_Set_Of_Types() {
        var a = NodeId.Generate();
        var b = NodeId.Generate();
        var r = NodeRelation.Create(a, b, RelationType.References);
        var path = new RelationPath {
            StartNodeId = a,
            EndNodeId = b,
            NodePath = new[] { a, b },
            Relations = new[] { r }
        };

        var types = path.GetRelationTypes();
        Assert.Single(types);
        Assert.Contains(RelationType.References, types);
    }

    [Fact]
    public void AreDirectlyConnected_Should_Work_For_Direct_Edges() {
        var a = NodeId.Generate();
        var b = NodeId.Generate();
        var r = NodeRelation.Create(a, b, RelationType.References);

        var graph = new RelationGraph {
            RootNodeId = a,
            Nodes = new HashSet<NodeId> { a, b },
            Relations = new[] { r },
            OutgoingRelations = new Dictionary<NodeId, IReadOnlyList<NodeRelation>> {
                [a] = new List<NodeRelation> { r }
            },
            IncomingRelations = new Dictionary<NodeId, IReadOnlyList<NodeRelation>> {
                [b] = new List<NodeRelation> { r }
            },
            MaxDepth = 1
        };

        Assert.True(graph.AreDirectlyConnected(a, b));
        Assert.True(graph.AreDirectlyConnected(b, a));
    }
}


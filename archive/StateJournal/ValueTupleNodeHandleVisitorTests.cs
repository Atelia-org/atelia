using Atelia.StateJournal.NodeContainers;
using Xunit;

using NodeHandleTupleSampleNode = System.ValueTuple<uint,string>;

namespace Atelia.StateJournal.Internal.Tests;

public class ValueTupleNodeHandleVisitorTests {
    [Fact]
    public void NodeHandleHelper_ExposesNodeHandleTraversal() {
        Assert.True(NodeHandleHelper<NodeHandleTupleSampleNode>.NeedVisitNodeHandles);
    }

    [Fact]
    public void ValueTupleHelper_VisitsNestedNodeHandles() {
        var value = new ValueTuple<int, ValueTuple<NodeHandle<NodeHandleTupleSampleNode>, NodeHandle<NodeHandleTupleSampleNode>>>(
            1,
            new(new(11), new(22))
        );
        var visitor = new CollectingVisitor();

        ValueTuple2Helper<
            int,
            ValueTuple<NodeHandle<NodeHandleTupleSampleNode>, NodeHandle<NodeHandleTupleSampleNode>>,
            Int32Helper,
            ValueTuple2Helper<
                NodeHandle<NodeHandleTupleSampleNode>,
                NodeHandle<NodeHandleTupleSampleNode>,
                NodeHandleHelper<NodeHandleTupleSampleNode>,
                NodeHandleHelper<NodeHandleTupleSampleNode>>>
            .VisitNodeHandles(ref value, ref visitor);

        Assert.Equal([11u, 22u], visitor.Sequences);
    }

    private struct CollectingVisitor : INodeHandleVisitor {
        public List<uint> Sequences { get; }

        public CollectingVisitor() {
            Sequences = [];
        }

        public void VisitNodeHandle<TNode>(ref NodeHandle<TNode> handle) where TNode : struct {
            Sequences.Add(handle.Sequence);
        }
    }
}

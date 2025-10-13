using System.Collections.Concurrent;
using System.Text;

namespace MemoFileProto.Models;

/// <summary>
/// 累积助手流式响应中的工具调用片段，组装成完整的 <see cref="UniversalToolCall"/>。
/// </summary>
public class ToolCallAccumulator {
    private readonly ConcurrentDictionary<string, ToolCallBuilder> _builders = new();
    private int _autoId;

    public void AddDelta(UniversalToolCall delta) {
        if (delta is null) { return; }

        var id = string.IsNullOrWhiteSpace(delta.Id) ? GetNextId() : delta.Id;
        var builder = _builders.GetOrAdd(id, key => new ToolCallBuilder(key));

        if (!string.IsNullOrWhiteSpace(delta.Name)) {
            builder.FunctionName = delta.Name;
        }

        if (!string.IsNullOrEmpty(delta.Arguments)) {
            builder.Arguments.Append(delta.Arguments);
        }
    }

    public bool HasPendingToolCalls => !_builders.IsEmpty;

    public List<UniversalToolCall> BuildFinalCalls() {
        return _builders.Values
            .OrderBy(b => b.Sequence)
            .Select(
            b => new UniversalToolCall {
                Id = b.Id,
                Name = b.FunctionName,
                Arguments = b.Arguments.ToString()
            }
        )
            .ToList();
    }

    public void Clear() {
        _builders.Clear();
        _autoId = 0;
    }

    private string GetNextId() {
        var id = Interlocked.Increment(ref _autoId);
        return $"auto_tool_call_{id}";
    }

    private class ToolCallBuilder {
        private static long _sequenceSeed;

        public ToolCallBuilder(string id) {
            Id = id;
            Sequence = Interlocked.Increment(ref _sequenceSeed);
        }

        public string Id { get; }
        public long Sequence { get; }
        public string FunctionName { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}

using System.Collections.Concurrent;
using System.Text;

namespace MemoFileProto.Models;

/// <summary>
/// 累积助手流式响应中的工具调用片段，组装成完整的 <see cref="ToolCall"/>。
/// </summary>
public class ToolCallAccumulator {
    private readonly ConcurrentDictionary<string, ToolCallBuilder> _builders = new();
    private int _autoId;

    public void AddDelta(ToolCall delta) {
        if (delta is null) { return; }

        var id = string.IsNullOrWhiteSpace(delta.Id) ? GetNextId() : delta.Id;
        var builder = _builders.GetOrAdd(id, key => new ToolCallBuilder(key));

        if (!string.IsNullOrWhiteSpace(delta.Type)) {
            builder.Type = delta.Type;
        }

        if (delta.Function is null) { return; }

        if (!string.IsNullOrWhiteSpace(delta.Function.Name)) {
            builder.FunctionName = delta.Function.Name;
        }

        if (!string.IsNullOrEmpty(delta.Function.Arguments)) {
            builder.Arguments.Append(delta.Function.Arguments);
        }
    }

    public bool HasPendingToolCalls => !_builders.IsEmpty;

    public IReadOnlyList<ToolCall> BuildFinalCalls() {
        return _builders.Values
            .OrderBy(b => b.Sequence)
            .Select(
            b => new ToolCall {
                Id = b.Id,
                Type = string.IsNullOrWhiteSpace(b.Type) ? "function" : b.Type,
                Function = new FunctionCall {
                    Name = b.FunctionName,
                    Arguments = b.Arguments.ToString()
                }
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
        public string? Type { get; set; }
        public string FunctionName { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}

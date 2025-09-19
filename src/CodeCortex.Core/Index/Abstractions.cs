using Microsoft.CodeAnalysis;
namespace CodeCortex.Core.Index;
#pragma warning disable 1591
public interface IClock { DateTime UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; public static readonly SystemClock Instance = new(); }
public interface IOutlineWriter { void EnsureDirectory(); void Write(string typeId, string outlineMarkdown); }
public sealed class FileOutlineWriter : IOutlineWriter {
    private readonly string _dir; public FileOutlineWriter(string dir) { _dir = dir; }
    public void EnsureDirectory() => Directory.CreateDirectory(_dir); public void Write(string typeId, string outlineMarkdown) {
        var path = Path.Combine(_dir, typeId + ".outline.md");
        File.WriteAllText(path, outlineMarkdown, System.Text.Encoding.UTF8);
    }
}
public interface IIndexBuildObserver { void OnStart(IndexBuildRequest req); void OnTypeAdded(string typeId, string fqn); void OnOutlineWritten(string typeId); void OnCompleted(CodeCortexIndex index, long durationMs); }
public sealed class NullIndexBuildObserver : IIndexBuildObserver { public static readonly NullIndexBuildObserver Instance = new(); public void OnStart(IndexBuildRequest req) { } public void OnTypeAdded(string typeId, string fqn) { } public void OnOutlineWritten(string typeId) { } public void OnCompleted(CodeCortexIndex index, long durationMs) { } }
public sealed record IndexBuildRequest(string SolutionPath, IReadOnlyList<Project> Projects, bool GenerateOutlines, CodeCortex.Core.Hashing.HashConfig HashConfig, CodeCortex.Core.Outline.OutlineOptions OutlineOptions, IClock Clock, IOutlineWriter OutlineWriter);
#pragma warning restore 1591

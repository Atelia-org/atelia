using System.Collections.Immutable;

namespace CodeCortex.Core.Models;

/// <summary>
/// 表示索引中一条类型记录（标识 + 元数据 + 哈希 + 结构深度提示）。
/// </summary>
/// <param name="Id">类型唯一标识（可为稳定 GUID/哈希截断）。</param>
/// <param name="Fqn">类型完全限定名（含命名空间 + 泛型参数 arity）。</param>
/// <param name="ProjectId">所属项目标识（后续可映射到项目节点）。</param>
/// <param name="Kind">类型分类（class/struct/interface/record/enum 等）。</param>
/// <param name="Files">该类型涉及的源文件集合（可能部分类/分布式定义）。</param>
/// <param name="Hashes">预先计算的多层哈希。</param>
/// <param name="OutlineVersion">Outline 版本号（结构摘要格式的演进控制）。</param>
/// <param name="DepthHint">层次深度提示（构建上下文/提示窗口聚合策略用）。</param>
public sealed record TypeRecord(
    string Id,
    string Fqn,
    string ProjectId,
    string Kind,
    ImmutableArray<string> Files,
    TypeHashes Hashes,
    int OutlineVersion,
    int DepthHint
) {
    /// <summary>
    /// 工厂方法：将可枚举 &lt;paramref name="files"/&gt; 归一为不可变数组后创建记录。
    /// </summary>
    public static TypeRecord Create(string id, string fqn, string projectId, string kind, IEnumerable<string> files, TypeHashes hashes, int outlineVersion, int depthHint)
        => new(id, fqn, projectId, kind, files.ToImmutableArray(), hashes, outlineVersion, depthHint);
}

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 收口 sample-world 这类开发态 bootstrap 流程，避免宿主主路径继续直接混用
/// “只打开既有 repo” 与 “必要时创建样例世界” 两种生命周期语义。
/// </summary>
public static class TextAdv2SampleWorldDevBootstrap {
    public static TextAdv2Runtime CreateTemporaryRuntime()
        => TextAdv2Runtime.CreateTemporarySampleWorld();

    public static TextAdv2Runtime CreateFreshRuntime(string repoDir)
        => TextAdv2Runtime.CreateSampleWorld(repoDir);

    public static TextAdv2Runtime OpenOrCreateRuntime(string repoDir)
        => TextAdv2Runtime.OpenOrCreateSampleWorld(repoDir);

    public static TextAdv2Runtime ResetRuntime(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }

        return TextAdv2Runtime.CreateSampleWorld(repoDir);
    }
}

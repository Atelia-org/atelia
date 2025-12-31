// DocGraph v0.1 - 帮助命令工具
// 参考：spec.md §11.3 用户体验标准

namespace Atelia.DocGraph.Commands;

/// <summary>
/// 帮助信息工具类。
/// 提供详细的命令帮助和使用示例。
/// </summary>
public static class HelpInfo
{
    /// <summary>
    /// 版本号。
    /// </summary>
    public const string Version = "0.1.0";

    /// <summary>
    /// 打印欢迎信息和快速入门指南。
    /// </summary>
    public static void PrintWelcome()
    {
        Console.WriteLine($"""
            
            ╔═══════════════════════════════════════════════════════════╗
            ║                    DocGraph v{Version}                       ║
            ║           文档关系图验证和生成工具                          ║
            ╚═══════════════════════════════════════════════════════════╝

            快速入门：
              docgraph validate           验证当前目录的文档关系
              docgraph stats              显示文档图统计信息
              docgraph fix                修复可自动修复的问题

            获取帮助：
              docgraph --help             显示所有命令
              docgraph <command> --help   显示命令详情

            更多信息：
              https://github.com/example/docgraph

            """);
    }

    /// <summary>
    /// 打印退出码说明。
    /// </summary>
    public static void PrintExitCodes()
    {
        Console.WriteLine("""
            
            退出码说明：
            ═══════════════════════════════════════════════════════════
            
            基础退出码：
              0  成功      无错误，验证通过
              1  警告      有警告，无错误
              2  错误      有验证错误
              3  致命      无法执行（配置错误、IO 错误）
            
            修复模式退出码（--fix）：
              0  成功      验证通过 + 修复全部成功（或无需修复）
              1  警告      验证有警告 + 修复成功
              2  错误      验证有错误，未执行修复
              3  致命      Fatal错误或修复执行失败

            """);
    }

    /// <summary>
    /// 打印常见问题和解决方案。
    /// </summary>
    public static void PrintCommonIssues()
    {
        Console.WriteLine("""
            
            常见问题和解决方案：
            ═══════════════════════════════════════════════════════════
            
            1. 悬空引用 (DOCGRAPH_RELATION_DANGLING_LINK)
               问题：Wish 文档的 produce 字段引用了不存在的文件
               解决：使用 `docgraph fix` 创建缺失文件，或修正路径
            
            2. 必填字段缺失 (DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING)
               问题：文档缺少必填的 frontmatter 字段
               解决：添加缺失的字段（title, produce, docId, produce_by）
            
            3. 路径越界 (DOCGRAPH_PATH_OUT_OF_WORKSPACE)
               问题：路径引用超出工作区范围
               解决：确保所有路径都在工作区内，不使用 ../
            
            4. YAML 语法错误 (DOCGRAPH_YAML_PARSE_ERROR)
               问题：frontmatter 的 YAML 语法不正确
               解决：检查 YAML 缩进和格式

            """);
    }

    /// <summary>
    /// 打印示例工作流。
    /// </summary>
    public static void PrintWorkflow()
    {
        Console.WriteLine("""
            
            典型工作流：
            ═══════════════════════════════════════════════════════════
            
            1. 查看统计信息
               $ docgraph stats
            
            2. 验证文档关系
               $ docgraph validate
            
            3. 预览修复操作（不实际执行）
               $ docgraph fix --dry-run
            
            4. 执行修复
               $ docgraph fix
            
            5. CI/CD 自动化（跳过确认）
               $ docgraph validate --fix --yes

            """);
    }
}

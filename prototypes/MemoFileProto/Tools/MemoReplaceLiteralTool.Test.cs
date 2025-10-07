using System;
using System.Threading.Tasks;

namespace MemoFileProto.Tools;

/// <summary>
/// 简单的手动测试程序，验证 MemoReplaceLiteral 的三种模式
/// </summary>
public class MemoReplaceLiteralTest {
    private static string _memory = string.Empty;

    public static async Task RunTests() {
        Console.WriteLine("=== MemoReplaceLiteral 功能测试 ===\n");

        await TestMode1_UniqueMatch();
        await TestMode1_MultipleMatches();
        await TestMode2_EmptySearchAfter();
        await TestMode3_NonEmptySearchAfter();
        await TestMode_Append();

        Console.WriteLine("\n=== 所有测试完成 ===");
    }

    private static async Task TestMode1_UniqueMatch() {
        Console.WriteLine("测试 1: 唯一匹配（无 search_after）");
        _memory = "项目状态：进行中\n任务数：5";

        var tool = CreateTool();
        var args = """{"old_text": "项目状态：进行中", "new_text": "项目状态：已完成"}""";
        var result = await tool.ExecuteAsync(args);

        Console.WriteLine($"结果: {result}");
        Console.WriteLine($"更新后记忆: {_memory}");
        Console.WriteLine();
    }

    private static async Task TestMode1_MultipleMatches() {
        Console.WriteLine("测试 2: 多个匹配（应该报错并展示上下文）");
        _memory = """
            void Func1() {
                int result = 0;
            }
            void Func2() {
                int result = 0;
            }
            """;

        var tool = CreateTool();
        var args = """{"old_text": "int result = 0;", "new_text": "int result = 1;"}""";
        var result = await tool.ExecuteAsync(args);

        Console.WriteLine($"结果: {result}");
        Console.WriteLine();
    }

    private static async Task TestMode2_EmptySearchAfter() {
        Console.WriteLine("测试 3: 空 search_after（从开头取第一个）");
        _memory = """
            __m256i vec = _mm256_loadu_si256(&data[0]);
            __m256i vec = _mm256_loadu_si256(&data[8]);
            """;

        var tool = CreateTool();
        var args = """{"old_text": "__m256i vec = _mm256_loadu_si256(&data[0]);", "new_text": "__m256i vec = _mm256_load_si256(&data[0]);", "search_after": ""}""";
        var result = await tool.ExecuteAsync(args);

        Console.WriteLine($"结果: {result}");
        Console.WriteLine($"更新后记忆: {_memory}");
        Console.WriteLine();
    }

    private static async Task TestMode3_NonEmptySearchAfter() {
        Console.WriteLine("测试 4: 非空 search_after（从锚点后取第一个）");
        _memory = """
            void Func1() {
                int result = 0;
            }
            void Func2() {
                int result = 0;
            }
            """;

        var tool = CreateTool();
        var args = """{"old_text": "int result = 0;", "new_text": "int result = 2;", "search_after": "void Func2() {"}""";
        var result = await tool.ExecuteAsync(args);

        Console.WriteLine($"结果: {result}");
        Console.WriteLine($"更新后记忆: {_memory}");
        Console.WriteLine();
    }

    private static async Task TestMode_Append() {
        Console.WriteLine("测试 5: 追加模式（old_text 为空）");
        _memory = "## 第一章\n内容";

        var tool = CreateTool();
        var args = """{"old_text": "", "new_text": "## 第二章\n新内容"}""";
        var result = await tool.ExecuteAsync(args);

        Console.WriteLine($"结果: {result}");
        Console.WriteLine($"更新后记忆: {_memory}");
        Console.WriteLine();
    }

    private static MemoReplaceLiteral CreateTool() {
        return new MemoReplaceLiteral(
            getMemory: () => _memory,
            setMemory: (newMemory) => _memory = newMemory
        );
    }
}

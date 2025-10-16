# Copilot 可用工具接口说明

本文档以 C# 接口伪代码形式列出当前会话中可用的全部工具，覆盖其用途、关键参数以及典型注意事项，便于在自动化编排或自定义代理时快速参考。

## 约定

```csharp
public interface IAssistantTool<TCommand, TResult>
{
    /// <summary>
    /// 以异步方式执行工具命令。
    /// </summary>
    Task<TResult> InvokeAsync(TCommand command, CancellationToken cancellationToken = default);
}
```

以下每个工具接口均继承自 `IAssistantTool` 并以伪代码表示，不代表真实 SDK，仅作为语义参考。

## 文件编辑与管理

```csharp
public interface IApplyPatchTool : IAssistantTool<ApplyPatchCommand, ApplyPatchResult>
{
    // 描述：对现有文本文件执行 V4A 格式补丁更新，适合精确控制多处修改。
}

public sealed record ApplyPatchCommand
(
    string Input,      // 必填：*** Begin Patch / End Patch 包裹的补丁内容
    string Explanation // 必填：对补丁意图的简短说明，便于审计
);

public sealed record ApplyPatchResult
(
    bool Success,
    string? ErrorMessage
);
```

```csharp
public interface ICreateDirectoryTool : IAssistantTool<CreateDirectoryCommand, Unit>
{
    // 描述：按照绝对路径创建目录，等价于 mkdir -p。
}

public sealed record CreateDirectoryCommand(string DirPath);
```

```csharp
public interface ICreateFileTool : IAssistantTool<CreateFileCommand, Unit>
{
    // 描述：新建文件并写入内容；目标存在时会出错。
}

public sealed record CreateFileCommand
(
    string FilePath,
    string Content
);
```

## 内容检索与读写

```csharp
public interface IFetchWebpageTool : IAssistantTool<FetchWebpageCommand, FetchWebpageResult>
{
    // 描述：离线抓取网页正文，可指定查询语句过滤。
}

public sealed record FetchWebpageCommand
(
    IReadOnlyList<Uri> Urls,
    string Query // 期望在页面内容内匹配的描述
);

public sealed record FetchWebpageResult(IReadOnlyDictionary<Uri, string> Contents);
```

```csharp
public interface IFileSearchTool : IAssistantTool<FileSearchCommand, FileSearchResult>
{
    // 描述：使用 glob 模式在工作区定位文件路径。
}

public sealed record FileSearchCommand
(
    string Query,     // 必填：glob 模式
    int? MaxResults   // 选填：最大匹配数
);

public sealed record FileSearchResult(IReadOnlyList<string> Matches);
```

```csharp
public interface IGrepSearchTool : IAssistantTool<GrepSearchCommand, GrepSearchResult>
{
    // 描述：在文件中执行正则或文本搜索，可限制文件范围。
}

public sealed record GrepSearchCommand
(
    string Query,
    bool IsRegexp,
    string? IncludePattern,
    int? MaxResults
);

public sealed record GrepSearchResult(IReadOnlyList<GrepMatch> Matches);

public sealed record GrepMatch(string FilePath, int Line, string Preview);
```

```csharp
public interface IListDirTool : IAssistantTool<ListDirCommand, ListDirResult>
{
    // 描述：列出指定路径下的子项，区分文件与文件夹。
}

public sealed record ListDirCommand(string Path);

public sealed record ListDirResult(IReadOnlyList<ListDirEntry> Entries);

public sealed record ListDirEntry(string Name, bool IsDirectory);
```

```csharp
public interface IReadFileTool : IAssistantTool<ReadFileCommand, ReadFileResult>
{
    // 描述：读取指定文件内容，支持偏移与行数限制。
}

public sealed record ReadFileCommand
(
    string FilePath,
    int? Offset = null,
    int? Limit = null
);

public sealed record ReadFileResult(string Content);
```

```csharp
public interface ISemanticSearchTool : IAssistantTool<SemanticSearchCommand, SemanticSearchResult>
{
    // 描述：基于语义相似度在仓库中检索片段，适合广义探索。
}

public sealed record SemanticSearchCommand(string Query);

public sealed record SemanticSearchResult(IReadOnlyList<SemanticMatch> Matches);

public sealed record SemanticMatch(string FilePath, string Snippet, double Score);
```

## 版本控制与诊断

```csharp
public interface IGetChangedFilesTool : IAssistantTool<GetChangedFilesCommand, GetChangedFilesResult>
{
    // 描述：查询当前工作树的改动文件，可按暂存状态过滤。
}

public sealed record GetChangedFilesCommand
(
    string? RepositoryPath,
    IReadOnlyList<string>? SourceControlState // staged, unstaged, merge-conflicts
);

public sealed record GetChangedFilesResult(IReadOnlyList<string> Files);
```

```csharp
public interface IGetErrorsTool : IAssistantTool<GetErrorsCommand, GetErrorsResult>
{
    // 描述：获取指定文件或整个项目的编译/诊断错误。
}

public sealed record GetErrorsCommand(IReadOnlyList<string>? FilePaths);

public sealed record GetErrorsResult(IReadOnlyList<BuildError> Errors);

public sealed record BuildError(string FilePath, int Line, string Message, string Severity);
```

```csharp
public interface IGetSearchViewResultsTool : IAssistantTool<Unit, SearchViewResult>
{
    // 描述：读取 IDE 搜索面板缓存的结果集。
}

public sealed record SearchViewResult(IReadOnlyList<SearchEntry> Entries);
```

```csharp
public interface IGetVscodeApiTool : IAssistantTool<GetVscodeApiCommand, GetVscodeApiResult>
{
    // 描述：检索 VS Code 扩展开发文档，需提供具体 API 名称。
}

public sealed record GetVscodeApiCommand(string Query);

public sealed record GetVscodeApiResult(string DocumentationExcerpt);
```

```csharp
public interface ITestFailureTool : IAssistantTool<Unit, TestFailureResult>
{
    // 描述：返回最近测试失败详情。
}

public sealed record TestFailureResult(IReadOnlyList<TestFailureEntry> Failures);
```

## 代码理解与引用

```csharp
public interface IListCodeUsagesTool : IAssistantTool<ListCodeUsagesCommand, ListCodeUsagesResult>
{
    // 描述：列出符号的引用/实现，有助于全局重构。
}

public sealed record ListCodeUsagesCommand
(
    string SymbolName,
    IReadOnlyList<string>? FilePathsHint
);

public sealed record ListCodeUsagesResult(IReadOnlyList<CodeUsage> Usages);

public sealed record CodeUsage(string FilePath, int Line, string Context);
```

## 构建与运行

```csharp
public interface IRunInTerminalTool : IAssistantTool<RunInTerminalCommand, RunInTerminalResult>
{
    // 描述：在持久终端中执行单行命令，可前台或后台运行。
}

public sealed record RunInTerminalCommand
(
    string Command,
    string Explanation,
    bool IsBackground
);

public sealed record RunInTerminalResult(string TerminalId, string? Output, int ExitCode);
```

```csharp
public interface IRunTestsTool : IAssistantTool<RunTestsCommand, RunTestsResult>
{
    // 描述：按照测试文件与模式运行单测，可选覆盖率模式。
}

public sealed record RunTestsCommand
(
    IReadOnlyList<string>? Files,
    IReadOnlyList<string>? TestNames,
    string Mode, // run 或 coverage
    IReadOnlyList<string>? CoverageFiles
);

public sealed record RunTestsResult(IReadOnlyList<TestRun> Runs);

public sealed record TestRun(string Name, bool Passed, string? Output);
```

```csharp
public interface IGetTerminalOutputTool : IAssistantTool<GetTerminalOutputCommand, TerminalOutputResult>
{
    // 描述：获取背景终端的最新输出。
}

public sealed record GetTerminalOutputCommand(string TerminalId);

public sealed record TerminalOutputResult(string Output, int? ExitCode);
```

```csharp
public interface ITerminalLastCommandTool : IAssistantTool<Unit, TerminalCommandInfo>
{
    // 描述：查看当前活动终端最近执行的命令。
}

public sealed record TerminalCommandInfo(string Command, string WorkingDirectory, int? ExitCode);
```

```csharp
public interface ITerminalSelectionTool : IAssistantTool<Unit, TerminalSelectionInfo>
{
    // 描述：读取当前终端选中文本（若支持）。
}

public sealed record TerminalSelectionInfo(string? Selection);
```

## 网络检索

```csharp
public interface IVscodeWebSearchTool : IAssistantTool<VscodeWebSearchCommand, VscodeWebSearchResult>
{
    // 描述：通过浏览器执行网络搜索，通常用于最新信息查找，需谨慎使用。
}

public sealed record VscodeWebSearchCommand(string Query);

public sealed record VscodeWebSearchResult(IReadOnlyList<WebSearchHit> Hits);
```

## 高级调度

```csharp
public interface IExecutePromptTool : IAssistantTool<ExecutePromptCommand, ExecutePromptResult>
{
    // 描述：启动独立代理执行复杂任务；需提供完整指令和返回期望。
}

public sealed record ExecutePromptCommand
(
    string Prompt,
    string Description
);

public sealed record ExecutePromptResult(string Report);
```

```csharp
public interface IMultiToolParallelExecutor : IAssistantTool<ParallelToolBatch, ParallelToolResult>
{
    // 描述：并行触发多个只读工具调用（例如查询/读取），需确保相互独立。
}

public sealed record ParallelToolBatch(IReadOnlyList<ParallelToolInvocation> ToolUses);

public sealed record ParallelToolInvocation
(
    string ToolName,
    object Parameters // 与各工具命令结构一致
);

public sealed record ParallelToolResult(IReadOnlyList<object> Results);
```

## 通用值类型

```csharp
public readonly struct Unit
{
    public static readonly Unit Value = new();
}
```

## 使用建议

- 所有路径参数都必须使用工作区内的绝对路径（Windows 需注意盘符）。
- 网络相关工具应在本地信息不足、且用户许可的情况下使用。
- 执行代码修改后，建议使用构建/测试工具验证变更，遵守“绿色后提交”的准则。
- 并行调度只适合无副作用的读取类操作；编辑或依赖性强的命令请顺序执行。

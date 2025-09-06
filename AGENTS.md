# Atelia.Diagnostics.DebugUtil 用法说明
- 使用 DebugUtil.Print("类别", "内容") 输出调试信息。
- 日志文件“始终写入”，控制台打印由环境变量 ATELIA_DEBUG_CATEGORIES 控制（类别用逗号/分号分隔，如：TypeHash,Test,Outline；设置为 ALL 打印所有类别）。
- 默认日志目录：.codecortex/ldebug-logs/{category}.log（便于 Agent 实时尾随读取）；若不可用则回退至 gitignore/debug-logs/{category}.log，最终回退到当前目录。
- 推荐在调试代码、测试代码中统一使用本工具，便于全局开关与后续维护；单元测试默认不会被调试输出干扰（除非开启 ATELIA_DEBUG_CATEGORIES）。
- 可用 DebugUtil.ClearLog("类别") 清空某类别日志。
- 实现细节见 src/Diagnostics/DebugUtil.cs。

---

# CodeCortex CLI 使用指南 (AI Agent专用)

## 核心命令
```bash
# 检查服务状态
dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- status

# 搜索类型 (支持通配符 *.* 和精确匹配)
dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- search "Atelia.*"
dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- search "Writer"

# 获取类型详细信息 (使用类型名或TypeId)
dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- outline "IndentationHelper"
dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- outline "T_NQUKU4SA"

# 符号解析 (类似search但更精确)
dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- resolve "TypeName"
```

## AI工作流
1. **探索项目**: `status` → `search "ProjectName.*"` → `search "ProjectName.Core.*"`
2. **问题解决**: `search "关键词"` → `outline "目标类型"` → 分析API设计
3. **最佳实践**: 优先通配符搜索、保存TypeId精确引用、关注XML文档

## 快捷别名
```powershell
function ccx-status { dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- status }
function ccx-search($query) { dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- search $query }
function ccx-outline($type) { dotnet run --project .\src\CodeCortex.Cli\CodeCortex.Cli.csproj -- outline $type }
```

---

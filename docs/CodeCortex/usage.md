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

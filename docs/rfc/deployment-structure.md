# Atelia 技术栈部署结构 RFC

> 日期: 2025-12-09
> 状态: 草案
> 作者: TeamLeader

## 背景

focus 生态中的项目（PipeMux, DocUI, PieceTreeSharp, atelia/prototypes）需要：

1. **便捷调用**：`pmux` 等命令直接可用，无需每次 `dotnet run`
2. **Broker 自动管理**：CLI 调用时自动启动 Broker，无需手动管理
3. **程序集共享**：编译后的 DLL 可被其他项目引用
4. **源码隔离**：孵化成熟的项目可移出 focus，减少认知负荷

## 目录结构设计

```
/repos/focus/
├── atelia/
│   ├── bin/                    # 可执行文件入口（添加到 PATH）
│   │   ├── pmux               # Shell wrapper 或 symlink
│   │   └── ...
│   │
│   ├── lib/                    # 编译后程序集
│   │   ├── PipeMux.CLI.dll
│   │   ├── PipeMux.Broker.dll
│   │   ├── PipeMux.Sdk.dll
│   │   ├── PipeMux.Shared.dll
│   │   ├── DocUI.Text.dll
│   │   └── ...
│   │
│   ├── etc/                    # 配置文件
│   │   └── pmux/
│   │       └── apps.toml      # App 注册配置
│   │
│   ├── var/                    # 运行时数据
│   │   └── pmux/
│   │       ├── broker.pid     # Broker PID 文件
│   │       └── logs/          # 日志目录
│   │
│   └── src/                    # 现有源码目录
│       ├── prototypes/
│       ├── scripts/
│       └── ...
│
├── PipeMux/                    # 源码（独立 git）
├── DocUI/                      # 源码（独立 git）
├── PieceTreeSharp/             # 源码（独立 git）
└── ...
```

## 环境变量

```bash
# ~/.bashrc 或 ~/.zshrc
export ATELIA_HOME="/repos/focus/atelia"
export PATH="$ATELIA_HOME/bin:$PATH"
```

## Broker 生命周期管理

### 方案 A：Shell Wrapper (推荐)

```bash
#!/bin/bash
# atelia/bin/pmux

ATELIA_HOME="${ATELIA_HOME:-/repos/focus/atelia}"
BROKER_PID="$ATELIA_HOME/var/pmux/broker.pid"
CLI="$ATELIA_HOME/lib/PipeMux.CLI.dll"
BROKER="$ATELIA_HOME/lib/PipeMux.Broker.dll"

ensure_broker() {
    # 检查 PID 文件和进程是否存活
    if [ -f "$BROKER_PID" ]; then
        local pid=$(cat "$BROKER_PID")
        if kill -0 "$pid" 2>/dev/null; then
            return 0  # Broker 运行中
        fi
    fi
    
    # 启动 Broker
    mkdir -p "$ATELIA_HOME/var/pmux"
    nohup dotnet "$BROKER" > "$ATELIA_HOME/var/pmux/logs/broker.log" 2>&1 &
    echo $! > "$BROKER_PID"
    sleep 0.5  # 等待 Named Pipe 就绪
}

ensure_broker
exec dotnet "$CLI" "$@"
```

**优点**：
- 实现简单，易于调试
- 跨平台（bash/zsh/PowerShell 各写一份）
- 不修改 CLI 代码

**缺点**：
- Windows 需要单独的 `.ps1` 或 `.cmd`
- 启动延迟约 0.5s

### 方案 B：CLI 内置 Lazy Start

```csharp
// PipeMux.CLI/BrokerLauncher.cs
public static class BrokerLauncher
{
    public static async Task EnsureBrokerAsync()
    {
        if (await BrokerClient.TryConnectAsync())
            return;  // Broker 已运行
        
        // 启动 Broker 进程
        var brokerPath = Path.Combine(AteliaHome, "lib", "PipeMux.Broker.dll");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = brokerPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        
        var process = Process.Start(psi);
        WritePidFile(process.Id);
        
        // 等待 Named Pipe 就绪
        await WaitForBrokerReadyAsync(timeout: TimeSpan.FromSeconds(5));
    }
}
```

**优点**：
- 纯 .NET，跨平台一致
- 更精细的错误处理
- 可复用 `BrokerClient` 逻辑

**缺点**：
- 需要修改 CLI 代码
- 增加 CLI 复杂度

### 推荐：方案 A (Shell Wrapper) + 方案 B 作为 Fallback

1. 首选 Shell Wrapper（零代码改动，立即可用）
2. 后续考虑迁移到 CLI 内置（更健壮）

## 编译输出配置

修改各项目的 `.csproj`，统一输出到 `atelia/lib/`：

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <AteliaLibPath>$(MSBuildThisFileDirectory)../atelia/lib/</AteliaLibPath>
</PropertyGroup>

<!-- PipeMux.CLI.csproj -->
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <OutputPath>$(AteliaLibPath)</OutputPath>
  <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup>
```

或使用发布脚本：

```bash
#!/bin/bash
# atelia/scripts/publish.sh

ATELIA_LIB="/repos/focus/atelia/lib"
mkdir -p "$ATELIA_LIB"

dotnet publish /repos/focus/PipeMux/src/PipeMux.CLI -c Release -o "$ATELIA_LIB"
dotnet publish /repos/focus/PipeMux/src/PipeMux.Broker -c Release -o "$ATELIA_LIB"
dotnet publish /repos/focus/DocUI/src/DocUI.Text -c Release -o "$ATELIA_LIB"
# ...
```

## 源码毕业机制

当项目孵化成熟后：

1. 移出 focus 目录到独立仓库
2. 发布 NuGet 包或保持源码引用
3. 更新 `atelia/lib/` 中的程序集

```
# 孵化中
/repos/focus/PipeMux/        # 源码在 focus 内

# 毕业后
/repos/PipeMux/              # 独立仓库
/repos/focus/atelia/lib/     # 仍然包含编译后的 DLL
```

## 实施计划

| 任务 | 优先级 | 说明 |
|------|--------|------|
| 创建目录结构 | P1 | `atelia/bin`, `atelia/lib`, `atelia/etc`, `atelia/var` |
| 创建 pmux wrapper | P1 | Shell 脚本 + Lazy Start |
| 创建 publish 脚本 | P1 | 编译输出到 `lib/` |
| 环境变量文档 | P2 | README 说明 |
| CLI 内置 Lazy Start | P3 | 可选增强 |

## 待讨论

1. **Windows 支持**：是否需要 `.ps1` / `.cmd` wrapper？
2. **多版本共存**：是否需要版本化 lib 目录？
3. **日志管理**：Broker 日志轮转策略？

---

*RFC 草案: 2025-12-09*

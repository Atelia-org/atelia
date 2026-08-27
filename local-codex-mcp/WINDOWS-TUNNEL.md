# Windows Secure MCP Tunnel 配置

本文记录 `local-codex-mcp` 在 Windows 上通过 OpenAI Secure MCP Tunnel 连接 ChatGPT，并只允许本机 Codex 操作指定 repo 的配置方法。

数据路径如下：

```text
ChatGPT -> Secure MCP Tunnel -> local-codex-mcp -> codex app-server -> allowed repo
```

本配置不开放入站端口。`tunnel-client` 只向 OpenAI 发起出站 HTTPS 连接，并在本机以 stdio 启动 Bridge。

## 当前机器配置

截至 2026-08-18，本机配置为：

- allowed root / default cwd：`E:\repos\drama-board`
- Bridge：`E:\repos\Atelia-org\atelia\local-codex-mcp`
- tunnel-client：`E:\greensoft\tunnel-client-v0.0.11\tunnel-client.exe`
- tunnel-client SHA-256：`7D3C7D492CE84B52835E11865A835A8A5BCD4A669DEE84E169AA11B314DC952A`
- profile：`local-codex-drama-board`
- profile 文件：`C:\Users\gdtut\AppData\Roaming\tunnel-client\local-codex-drama-board.yaml`
- 本地状态页：`http://127.0.0.1:18080/ui`
- Node.js：`D:\Program Files\nodejs\node.exe`，已验证版本 `24.11.1`
- Codex：profile 中已钉住初始化时实际使用的 VS Code 扩展内 binary，已验证版本 `0.148.0-alpha.9`

profile 只保存 `env:CONTROL_PLANE_API_KEY` 引用，不保存 runtime key 本身。Tunnel 与 ChatGPT workspace 的远端关联仍由 Platform / ChatGPT 设置管理。

## 首次构建

```powershell
Set-Location 'E:\repos\Atelia-org\atelia\local-codex-mcp'
npm ci
npm run build
```

Bridge 不自动读取 `.env`。Windows profile 通过 [`scripts/Start-Bridge.Windows.ps1`](./scripts/Start-Bridge.Windows.ps1) 注入 allowed root、default cwd 和固定 Codex binary。脚本会在启动 Node 前删除子进程环境中的 `CONTROL_PLANE_API_KEY`，避免 Bridge 和 `codex app-server` 继承 tunnel credential。

## 创建或重建 profile

当前机器已经创建 profile。只有更换 tunnel、repo、Bridge 路径或 Codex binary 后才需要用 `--force` 重建：

```powershell
$tunnelClient = 'E:\greensoft\tunnel-client-v0.0.11\tunnel-client.exe'
$mcpCommand = 'powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File E:/repos/Atelia-org/atelia/local-codex-mcp/scripts/Start-Bridge.Windows.ps1 -AllowedRoot E:/repos/drama-board -CodexCommand C:/ABSOLUTE/PATH/TO/codex.exe'

& $tunnelClient init `
  --force `
  --sample sample_mcp_stdio_local `
  --profile local-codex-drama-board `
  --tunnel-id tunnel_REPLACE_ME `
  --mcp-command $mcpCommand `
  --health-listen-addr 127.0.0.1:18080
```

`tunnel-client v0.0.11` 的 stdio command parser 会把反斜杠当作 shell escape。profile 的 `mcp.commands[].command` 内必须使用 Windows 也能识别的正斜杠路径；例如 `E:/repos/drama-board`，不能写成 `E:\repos\drama-board`。直接在 PowerShell 里执行的普通路径不受此限制。

用以下命令确认实际 Codex binary 和版本：

```powershell
Get-Command codex | Select-Object -ExpandProperty Source
codex --version
codex login status
```

Codex 升级后，按主 README 重新生成 schema、构建并测试，再用新 binary 路径重建 profile。

## 安全启动

不要把 runtime key 写进 `.env`、YAML、repo、PowerShell 命令历史或 Windows 用户级环境变量。使用启动脚本的隐藏输入：

```powershell
Set-Location 'E:\repos\Atelia-org\atelia\local-codex-mcp'
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File '.\scripts\Start-Tunnel.Windows.ps1' `
  -TunnelClientPath 'E:\greensoft\tunnel-client-v0.0.11\tunnel-client.exe'
```

脚本会：

1. 隐藏读取 `CONTROL_PLANE_API_KEY`；
2. 运行 `doctor --explain`，失败则不启动；
3. 前台运行 `local-codex-drama-board` profile；
4. tunnel 退出后清理当前进程里的明文环境变量并清零临时 BSTR。

保持该 PowerShell 窗口运行。按 `Ctrl+C` 停止 tunnel。前台运行便于第一阶段观察日志；确认长期稳定后再评估 Windows Service 或 `tunnel-client runtimes connect`，但仍应使用安全 secret source，不能把 literal key 写入 profile。

> 如果 runtime key 曾粘贴到聊天、日志或其他非 secret channel，建议在 Platform Runtime API keys 页面撤销它并创建新 key 后再启动。

## 验证

tunnel 启动后依次检查：

1. 打开 `http://127.0.0.1:18080/ui`，确认 healthy、ready、polling 状态；
2. 在 ChatGPT 中刷新已经绑定该 tunnel 的 developer-mode app；
3. 确认出现 `codex_delegate`、`codex_continue`、`codex_status`、`codex_read`、`codex_interrupt`；
4. 先做只读测试：

```text
请调用 codex_delegate，让本地 Codex 以 research 模式、local_command_network=false、web_search=disabled 调查
E:\repos\drama-board：用短摘要说明项目用途，并列出最多 8 个关键文件。
```

本机初始化期间已完成以下验证：

- `npm run build`：通过；
- Windows stdio wrapper 的 MCP `initialize` handshake：通过；
- 真实 `codex app-server` integration（调查、Bridge 重启、同 thread continuation）：通过；
- `npm test`：20 个启用测试中 19 个通过；`stdin closure fails requests without an unhandled EPIPE` 在 Windows 上稳定失败，表现为关闭 stdin 后的下一条 request 仍可能先成功，需另行修复或调整平台相关断言；
- 无 runtime key 时 `doctor` 的 profile/config 检查通过，并按预期只阻塞在 `CONTROL_PLANE_API_KEY` 未设置。

## 排障

查看 profile：

```powershell
& 'E:\greensoft\tunnel-client-v0.0.11\tunnel-client.exe' profiles list --json
Get-Content -Raw 'C:\Users\gdtut\AppData\Roaming\tunnel-client\local-codex-drama-board.yaml'
```

常见问题：

- `CONTROL_PLANE_API_KEY is not set`：用安全启动脚本输入 runtime key，不要把 key 加进 profile。
- stdio command 立即以 `0xfffd0000` 退出，且 PowerShell 报 `E:repos... does not exist`：profile command 的反斜杠被当作 escape；按“创建或重建 profile”一节改用正斜杠并用 `--force` 重建。
- tunnel 不可见：检查 tunnel 是否关联目标 ChatGPT workspace，以及操作者和 runtime-key principal 是否有 Tunnels Read + Use。
- MCP command 启动失败：确认 `npm run build` 已生成 `dist/src/index.js`，并确认 profile 中的 Codex binary 仍存在。
- ChatGPT tool discovery 失败：保持 tunnel 前台进程运行，重新执行 `doctor --explain` 并检查本地 `/ui`。
- `CWD_NOT_ALLOWED`：请求 cwd 必须是 `E:\repos\drama-board` 或其真实子目录；symlink/traversal 逃逸会被拒绝。

官方流程：[Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)。

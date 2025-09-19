读取 format.ps1 以了解其参数和工作方式，从而设计 pre-commit 钩子集成方案。 任务: 设计在 git commit 阶段自动执行 `.format.ps1 -Scope diff` 的钩子技术方案（含策略、风险与推荐实现路径）。

## 1 目标拆解
- 自动化: 在提交前执行格式化，保证进入仓库的 C# 代码符合统一风格。
- 最小侵入: 不破坏开发者“部分暂存 / 局部提交”习惯。
- 可控: 支持跳过、失败策略（自动补格式 vs 阻断提交让人确认）。
- 可扩展: 后续可加入统计 / 增量报告。

## 2 钩子选型对比
| 钩子 | 时机 | 适用 | 问题 |
|------|------|------|------|
| pre-commit | 写 commit 对象前 | 主流做法，可修改/追加暂存内容 | 需要谨慎处理“仅提交部分修改” |
| prepare-commit-msg | 填写消息前 | 不适合格式化 | 太早 |
| commit-msg | 已生成树 | 不能再改内容 | 只能阻断，不能修复 |
| pre-push | 推送时 | 兜底 | 太晚，diff 噪音已产生 |
建议: 主钩子用 pre-commit；可选再加 pre-push 兜底校验（只验证不改）。

## 3 关键策略决策
1. 格式作用域
   - 直接用当前 `-Scope diff`（含 unstaged + staged + untracked）会把未计划提交的改动也格式化并潜在 git add（污染提交）。
   - 推荐新增 `-Scope staged`（仅 `git diff --name-only --cached`）或新增参数 `-Include <paths>`。
2. 结果处理模式（Policy）
   - AutoStage 模式（默认）：格式器修改后自动 `git add` 受影响文件，提交继续。
   - FailOnChanges 模式：检测到修改则退出非 0，中断提交，提示“请查看并重新 git add”。
   - 建议默认 AutoStage，提供 ENV 开关。
3. 可跳过机制
   - 环境变量：`MT_SKIP_FORMAT=1 git commit ...`
   - 或 commit message 前缀 `[skip-format]`（pre-commit 中读取 COMMIT_EDITMSG 不方便，需 prepare-commit-msg；不推荐第一版实现）。
4. 性能
   - 仅对 .cs 运行；当前脚本内部已有 diff 枚举与批处理迭代，可复用。
5. 幂等 / 防死循环
   - format.ps1 本身内部迭代直至收敛；pre-commit 不需循环再调。
6. 并发/IDE 兼容
   - 用 `pwsh` shebang；为 Windows GUI 客户端（如 VS / Rider）准备一个 .cmd 入口或纯 shell wrapper。

## 4 对现有 format.ps1 的最小增量改造
新增参数（可选但推荐）：
- `[ValidateSet('full','diff','staged')]$Scope`
- 或增加 `-IncludeFileList <path>`：若提供则跳过内部文件枚举逻辑。
新增可选行为参数：
- `-FailOnChanges`：若最终有任何“HasChanges” => exit 2。
脚本内部在 `Scope` 分支中添加：
```powershell
function Get-StagedFiles(){
  git diff --name-only --cached | Where-Object { $_ -and [IO.Path]::GetExtension($_) -eq '.cs' -and (Test-Path $_) }
}
# $allFiles = switch($Scope){ 'staged' { Get-StagedFiles } 'diff' {...} 'full' {...} }
```
最后统计阶段返回值逻辑：
```
if($FailOnChanges -and $reportAggregate.HasChanges){ exit 2 }
```
(或基于内部已知 changed 文件集合)

## 5 pre-commit 钩子脚本设计
路径: `.git/hooks/pre-commit`（无扩展名，Unix 换行，chmod +x）

核心流程:
1. 检查跳过变量: `if ($env:MT_SKIP_FORMAT) { exit 0 }`
2. 捕获当前已暂存 C# 文件列表（保存到临时文件 A）。
3. 调用 `pwsh format.ps1 -Scope staged`（或生成临时包含清单传给 `-IncludeFileList`）。
4. 若退出码 != 0（1=脚本错误） => abort。
5. 重新 diff cached vs worktree：
   - 找出步骤 2 列表内被修改的文件（以及新增由格式化触发的文件——根据策略决定是否允许）。
6. AutoStage 模式：`git add <受影响文件>`。
7. FailOnChanges 模式：如果有修改 => 打印提示并 `exit 1`。
8. 可选：输出摘要（修改文件计数、规则触发总数若后续暴露）。

PowerShell 版本（简化示例，未含错误处理增强）：
```
#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

if($env:MT_SKIP_FORMAT){ Write-Host '[format] skipped by MT_SKIP_FORMAT' -ForegroundColor Yellow; exit 0 }

# 保留原始 staged 列表
$staged = git diff --name-only --cached | Where-Object { $_ -like '*.cs' }
$stagedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$staged | ForEach-Object { $stagedSet.Add($_) | Out-Null }

Write-Host "[format] staged cs count: $($staged.Count)" -ForegroundColor Cyan

# 运行格式化 (仅对已暂存文件)
pwsh ./format.ps1 -Scope staged | Write-Host

if($LASTEXITCODE -ne 0){
  Write-Host "[format] formatter failed (exit=$LASTEXITCODE)" -ForegroundColor Red
  exit 1
}

# 检测受影响文件（仅重新 add 原本 staged 的 + 其语义依赖的新生成改动可选）
$changed = git diff --name-only | Where-Object { $_ -like '*.cs' }
$toAdd = @()
foreach($f in $changed){
  if($stagedSet.Contains($f)){
    $toAdd += $f
  }
}

if($toAdd.Count -gt 0){
  git add -- $toAdd
  Write-Host "[format] auto-added: $($toAdd.Count) files" -ForegroundColor Green
}

exit 0
```
若采用 `-IncludeFileList` 参数，可先写 `$staged > gitignore/staged-files.txt` 然后 `pwsh format.ps1 -IncludeFileList gitignore/staged-files.txt`。

Linux / macOS 兼容: 保持同一个脚本；Windows Git Bash 也能用 env pwsh。为纯 cmd 用户可增加 `.git/hooks/pre-commit.cmd` 调用：
```
@echo off
pwsh -NoLogo -File "%~dp0pre-commit"
exit /b %ERRORLEVEL%
```

## 6 可选 pre-push 验证钩子
pre-push:
- 执行 `pwsh format.ps1 -Scope diff -FailOnChanges`
- 若 exit 2 -> 打印“远端拒绝：请先提交格式化结果”。

## 7 跳过/调试机制
- 环境变量: `MT_SKIP_FORMAT=1`
- 提供 `scripts/skip-format.ps1` 写入/清除 `.git/hooks/_skip_format` 标记文件，在钩子里检测。
- CI 中强制校验：CI 步骤执行 `pwsh format.ps1 -Scope full -FailOnChanges`，不自动改，只检测。

## 8 风险与缓解
| 风险 | 描述 | 缓解 |
|------|------|------|
| 部分暂存被“扩大” | diff 模式会触及未暂存文件 | 使用 staged 模式或 -Include 清单 |
| 意外长耗时 | 大批量初次引入文件 | 首次导入建议单独运行全量格式 commit，后续钩子执行规模小 |
| GUI 客户端忽略钩子 | 某些托管服务或安全策略禁用 | 在 CI 再加只读验证 |
| Hook 被覆盖 | 重装工具或多人冲突 | 用脚本再生成（idempotent installer） |

## 9 后续增强
- 在 format.ps1 输出最终“变更文件数 / 未收敛文件列表”以便钩子解析。
- 输出 machine-readable 摘要 JSON（写 `gitignore/format-summary.json`）。
- 统计 7 日滚动平均“每次提交格式修改行数”，用于评估 AI 生成阶段风格贴合度改进效果。
- 为 VS/Rider 提供 EditorConfig 内联提示以减少提交时修改。

## 10 实施顺序建议
1. 扩展 format.ps1：新增 `staged` Scope（最小改动）。
2. 新建 `scripts/install-hooks.ps1`：复制/生成 pre-commit（幂等，可多次运行）。
3. 添加文档 `docs/DevWorkflow/FormattingHooks.md`：说明跳过方式、故障排查。
4. CI 添加验证 job。
5. 观察两周统计，视需要再引入 FailOnChanges 模式。

如果你确认采用 “新增 Scope=staged + AutoStage” 路线，我可以下一步直接补齐 format.ps1 的参数扩展及 hook 安装脚本。回复“继续实现”即可。需要调整策略（比如默认 FailOnChanges）也告诉我。

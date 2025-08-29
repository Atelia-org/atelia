#!/usr/bin/env pwsh

# 仅格式化“已更改但尚未提交”的 .NET 源代码文件。
# 涵盖：
#   - 已修改但未暂存 (git diff)
#   - 已暂存但未提交 (git diff --cached)
#   - 未追踪的新文件 (git ls-files --others --exclude-standard)

# 使用方式（在仓库根目录）：
#   pwsh ./do-format-diff.ps1

# 可选参数：
#   -Verbose : 显示详细信息

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info($msg){ Write-Host $msg -ForegroundColor Cyan }
function Write-Warn($msg){ Write-Host $msg -ForegroundColor Yellow }
function Write-Ok($msg){ Write-Host $msg -ForegroundColor Green }
function Write-ErrMsg($msg){ Write-Host $msg -ForegroundColor Red }

# 1. 定位解决方案文件（若存在）
$solution = Get-ChildItem -Path . -Filter *.sln -File | Select-Object -First 1
if(-not $solution){
    Write-Warn "未找到 .sln，dotnet format 将以当前目录为根运行"
}
else {
    Write-Info "使用解决方案: $($solution.Name)"
}

# 2. 收集未提交的文件列表
$modifiedUnstaged = git diff --name-only | Where-Object { $_ }
$modifiedStaged   = git diff --name-only --cached | Where-Object { $_ }
$untracked        = git ls-files --others --exclude-standard | Where-Object { $_ }

$all = @($modifiedUnstaged + $modifiedStaged + $untracked) | Sort-Object -Unique
if(-not $all){
    Write-Ok '当前没有未提交的变更文件。'
    return
}

# 3. 过滤可能被 dotnet format 处理的文件类型
$codeExtensions = @('.cs', '.vb')
$targetFiles = $all | Where-Object { $codeExtensions -contains [IO.Path]::GetExtension($_) } |
    Where-Object { Test-Path $_ }

if(-not $targetFiles){
    Write-Warn '未提交变更中没有需要格式化的 C#/VB 文件。'
    return
}

Write-Info "待格式化文件数: $($targetFiles.Count)"
if($PSBoundParameters.ContainsKey('Verbose')){
    $targetFiles | ForEach-Object { Write-Host "  - $_" }
}

# 4. 为防止命令长度过长，可分批（例如每 80 个文件一批）
$batchSize = 80
$index = 0
$total = $targetFiles.Count
$exitCodes = @()

while($index -lt $total){
    $batch = $targetFiles[$index..([Math]::Min($index+$batchSize-1, $total-1))]
    $rangeLabel = "[$($index+1)-$([Math]::Min($index+$batchSize,$total))/$total]"
    Write-Info "格式化批次 $rangeLabel ..."

    $args = @()
    if($solution){ $args += $solution.FullName }
    $args += '--include'
    $args += $batch

    # 使用 --verify-no-changes 先判断是否需要实际格式化?
    # 直接执行格式化即可（更简单），若需要可后续扩展。
    dotnet format @args
    $exitCodes += $LASTEXITCODE

    $index += $batchSize
}

if($exitCodes | Where-Object { $_ -ne 0 }){
    Write-ErrMsg "部分批次 dotnet format 返回非零退出码，请检查上方输出。"
    exit 1
}

Write-Ok '已完成对未提交变更文件的格式化。'

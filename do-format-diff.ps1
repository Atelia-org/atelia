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
# 使用示例
# 开发阶段（仅自研建议级别 auto-fix）： pwsh do-format-diff.ps1 -Mode dev
# 提交前严格（提升指定规则为 warning + 修复）： pwsh do-format-diff.ps1 -Mode enforce
# 仅验证无剩余可修（CI 可用）： pwsh do-format-diff.ps1 -Mode enforce -Verify
# 跳过还原（离线更快）： pwsh do-format-diff.ps1 -Mode dev -NoRestore

param(
    [ValidateSet('dev','enforce')][string]$Mode = 'enforce',
    [switch]$Verify,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info($msg){ Write-Host $msg -ForegroundColor Cyan }
function Write-Warn($msg){ Write-Host $msg -ForegroundColor Yellow }
function Write-Ok($msg){ Write-Host $msg -ForegroundColor Green }
function Write-ErrMsg($msg){ Write-Host $msg -ForegroundColor Red }

# 0. 读取白名单与 override
$diagWhitelistFile = Join-Path -Path 'config' -ChildPath 'format-diagnostics.txt'
$overrideFile      = Join-Path -Path 'config' -ChildPath 'enforce.editorconfig'
$diagnostics = @()
if(Test-Path $diagWhitelistFile){
    $diagnostics = Get-Content $diagWhitelistFile | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}
if(-not $diagnostics){ Write-Warn "诊断白名单为空，默认不传 --diagnostics，可能触发大量第三方修复。" }

Write-Info "运行模式: $Mode"
if($Mode -eq 'enforce' -and -not (Test-Path $overrideFile)){
    Write-Warn "未找到 override 文件 $overrideFile，仍按 dev 模式。"
    $Mode = 'dev'
}

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

try {
    # 若 enforce 模式，合成临时 editorconfig
    $editorConfig = '.editorconfig'
    $backup = "$editorConfig.__devbak"
    $temp   = "$editorConfig.__enforce"
    $usingEnforce = $false
    if($Mode -eq 'enforce' -and (Test-Path $editorConfig)){
        Write-Info '合成 enforce 配置...'
        Copy-Item $editorConfig $backup -Force
        $baseContent = Get-Content $editorConfig -Raw
        $ovrContent = Get-Content $overrideFile -Raw
        ($baseContent + "`n# ==== ENFORCE OVERRIDES (temp) ==== `n" + $ovrContent + "`n") | Set-Content $temp -Encoding UTF8
        Move-Item -Force $temp $editorConfig
        $usingEnforce = $true
    }

    while($index -lt $total){
    $batch = $targetFiles[$index..([Math]::Min($index+$batchSize-1, $total-1))]
    $rangeLabel = "[$($index+1)-$([Math]::Min($index+$batchSize,$total))/$total]"
    Write-Info "格式化批次 $rangeLabel ..."

    $args = @()
    if($solution){ $args += $solution.FullName }
    $args += '--include'
    $args += $batch
        if($diagnostics){ $args += '--diagnostics'; $args += ($diagnostics -join ' ') }
        if($NoRestore){ $args += '--no-restore' }
        if($Verify){ $args += '--verify-no-changes' }

        # 统一使用 analyzers（包含 style + 第三方 + 自研）
        # 阈值选 info：允许 suggestion (Info) 被处理，但通过白名单限制范围
        $cmd = @('dotnet','format','analyzers','--severity','info') + $args
        Write-Info ("执行: " + ($cmd -join ' '))
        & $cmd
        $exitCodes += $LASTEXITCODE
        $index += $batchSize
    }
}
finally {
    if($usingEnforce -and (Test-Path $backup)){
        Write-Info '恢复原始 .editorconfig'
        Move-Item -Force $backup '.editorconfig'
    }
}

if($exitCodes | Where-Object { $_ -ne 0 }){
    Write-ErrMsg "部分批次 dotnet format 返回非零退出码，请检查上方输出。"
    exit 1
}

Write-Ok '已完成对未提交变更文件的格式化。'

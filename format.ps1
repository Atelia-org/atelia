#!/usr/bin/env pwsh
<#!
统一格式化脚本 (MVP)

特性:
  - Scope = full(默认)/diff
  - 始终合并 config/enforce.editorconfig -> 临时 .editorconfig (退出后恢复)
  - 每批次(60文件)独立迭代 (最多5轮) 直到该批无新增修改
  - 使用: dotnet format analyzers --severity info (允许 suggestion 级规则修复)
  - 不使用诊断白名单 (会应用所有可自动修复的 analyzers，含第三方)
  - 退出码: 0=成功; 1=内部错误/格式工具异常

使用示例:
  pwsh ./format.ps1                  # 全仓
  pwsh ./format.ps1 -Scope diff      # 仅改动文件

后续可扩展: 白名单 / FailOnChanges / 报告输出 / 并行等。
!#>
param(
    [ValidateSet('full','diff')][string]$Scope = 'full'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 常量
$MaxIterations = 5
$BatchSize = 60

function Write-Info($m){ Write-Host $m -ForegroundColor Cyan }
function Write-Warn($m){ Write-Host $m -ForegroundColor Yellow }
function Write-Ok($m){ Write-Host $m -ForegroundColor Green }
function Write-Err($m){ Write-Host $m -ForegroundColor Red }

# 解决方案发现
$solution = Get-ChildItem -Path . -Filter *.sln -File | Select-Object -First 1
if($solution){ Write-Info "使用解决方案: $($solution.Name)" } else { Write-Warn '未找到 .sln，按当前目录工作。' }

# 合并 enforce 配置
$editorConfig = '.editorconfig'
$overrideFile = 'config/enforce.editorconfig'
$backup = "$editorConfig.__devbak"
$usingEnforce = $false
if((Test-Path $editorConfig) -and (Test-Path $overrideFile)){
    Write-Info '应用 enforce 覆盖...'
    $baseContent = Get-Content $editorConfig -Raw
    $ovrContent  = Get-Content $overrideFile -Raw
    Copy-Item $editorConfig $backup -Force
    ($baseContent + "`n# ==== ENFORCE OVERRIDES (temp merged) ==== `n" + $ovrContent + "`n") | Set-Content $editorConfig -Encoding UTF8
    $usingEnforce = $true
} else {
    Write-Warn '缺少 .editorconfig 或 enforce 覆盖文件，跳过合并。'
}

function Get-FullFiles(){
    # 使用 git ls-files 提供稳定列表
    $files = (& git ls-files *.cs 2>$null) | Where-Object { $_ }
    return $files
}

function Get-DiffFiles(){
    $unstaged = git diff --name-only | Where-Object { $_ }
    $staged   = git diff --name-only --cached | Where-Object { $_ }
    $untracked= git ls-files --others --exclude-standard | Where-Object { $_ }
    $all = @($unstaged + $staged + $untracked) | Sort-Object -Unique
    $files = $all | Where-Object { [IO.Path]::GetExtension($_) -eq '.cs' -and (Test-Path $_) }
    return $files
}

try {
    $allFiles = if($Scope -eq 'full'){ Get-FullFiles } else { Get-DiffFiles }
    if(-not $allFiles -or $allFiles.Count -eq 0){ Write-Ok '无目标文件，结束。'; exit 0 }

    Write-Info "Scope=$Scope 文件数: $($allFiles.Count)"

    # 批次分组
    $batches = @()
    for($i=0; $i -lt $allFiles.Count; $i += $BatchSize){
        $batches += ,($allFiles[$i..([Math]::Min($i+$BatchSize-1,$allFiles.Count-1))])
    }

    $globalError = $false
    $nonConverged = @()
    $batchIndex = 0

    foreach($batch in $batches){
        $batchIndex++
        Write-Info "处理批次 $batchIndex/$($batches.Count) (文件数: $($batch.Count))"
        $iteration = 0
        while($iteration -lt $MaxIterations){
            $iteration++
            Write-Info "  迭代 #$iteration ..."
            # $args = @('format','analyzers')
            $args = @('format')
            if($solution){ $args += $solution.FullName }
            $args += '--include'
            $args += $batch
            # 调试输出（可注释）
            # Write-Info ("    执行: dotnet " + ($args -join ' '))
            & dotnet @args
            $exit = $LASTEXITCODE
            if($exit -eq 0){ Write-Info '  无修改，批次收敛'; break }
            elseif($exit -eq 2){ Write-Info '  已应用修改，继续迭代'; continue }
            else { Write-Err "  dotnet format 异常退出码: $exit"; $globalError = $true; break }
        }
        if($globalError){ break }
        if($iteration -ge $MaxIterations -and $exit -eq 2){
            Write-Warn '  达到最大迭代仍有修改（可能存在来回改动的 CodeFix）'
            $nonConverged += ,@{ Batch=$batchIndex; Files=$batch }
        }
    }

    if($globalError){ Write-Err '格式化过程中出现错误'; exit 1 }
    if($nonConverged.Count -gt 0){
        Write-Warn "存在未收敛批次: $($nonConverged.Count)"
    }
    Write-Ok '格式化完成'
    exit 0
}
finally {
    if($usingEnforce -and (Test-Path $backup)){
        Write-Info '恢复原始 .editorconfig'
        Move-Item -Force $backup $editorConfig
    }
}

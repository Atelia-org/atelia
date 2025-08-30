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
$BatchSize = 96

# 计时起点
$scriptStart = Get-Date

function Write-Info($m){ Write-Host $m -ForegroundColor Cyan }
function Write-Warn($m){ Write-Host $m -ForegroundColor Yellow }
function Write-Ok($m){ Write-Host $m -ForegroundColor Green }
function Write-Err($m){ Write-Host $m -ForegroundColor Red }

# 解析 dotnet format --report 生成的 JSON（数组）并返回是否有修改
function Parse-FormatReport {
    param(
        [Parameter(Mandatory)][string]$Path
    )
    if(-not (Test-Path $Path)){
        throw "报告文件不存在: $Path"
    }
    $raw = Get-Content $Path -Raw
    if(-not $raw.Trim()){
        return [pscustomobject]@{ HasChanges=$false; FileCount=0; ChangeCount=0; Items=@() }
    }
    try { $data = $raw | ConvertFrom-Json } catch { throw "报告 JSON 解析失败: $Path - $($_.Exception.Message)" }
    if(-not $data){ return [pscustomobject]@{ HasChanges=$false; FileCount=0; ChangeCount=0; Items=@() } }
    # 按当前格式：数组元素含 FileChanges
    $changedItems = $data | Where-Object { $_.FileChanges -and $_.FileChanges.Count -gt 0 }
    $changeCount = 0
    if($changedItems){ $changeCount = ($changedItems | ForEach-Object { $_.FileChanges.Count } | Measure-Object -Sum).Sum }
    return [pscustomobject]@{
        HasChanges  = ($changedItems.Count -gt 0)
        FileCount   = $changedItems.Count
        ChangeCount = $changeCount
        Items       = $changedItems
    }
}

# 解决方案发现
$solution = Get-ChildItem -Path . -Filter *.sln -File | Select-Object -First 1
if($solution){ Write-Info "使用解决方案: $($solution.Name)" } else { Write-Warn '未找到 .sln，按当前目录工作。' }

# 合并 enforce 配置
$editorConfig = '.editorconfig'
$overrideFile = 'config/enforce.editorconfig'
$backup = "gitignore/$editorConfig.__devbak"
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
    $totalIterations = 0

    foreach($batch in $batches){
        $batchIndex++
        $batchStart = Get-Date
        Write-Info "处理批次 $batchIndex/$($batches.Count) (文件数: $($batch.Count))"
        $iteration = 0
        while($iteration -lt $MaxIterations){
            $iteration++
            $totalIterations++
            Write-Info "  迭代 #$iteration ..."

            # 使用单次格式化 + 报告方式判断是否还有修改
            if(-not (Test-Path 'gitignore')){ New-Item -ItemType Directory -Path 'gitignore' | Out-Null }
            $reportPath = Join-Path 'gitignore' 'format-report.json'
            if(Test-Path $reportPath){ Remove-Item $reportPath -Force -ErrorAction SilentlyContinue }

            $formatArgs = @('format')
            if($solution){ $formatArgs += $solution.FullName }
            $formatArgs += '--include'; $formatArgs += $batch
            $formatArgs += '--report'; $formatArgs += $reportPath
            $formatArgs += '--verbosity'; $formatArgs += 'minimal'

            & dotnet @formatArgs
            $formatExit = $LASTEXITCODE
            if($formatExit -ne 0){
                Write-Err "  dotnet format 退出码: $formatExit"
                $globalError = $true
                break
            }

            $changedThisIteration = $false
            try {
                $report = Parse-FormatReport -Path $reportPath
                if($report.HasChanges){
                    Write-Info "  本轮修改: 文件=$($report.FileCount) 条目=$($report.ChangeCount) -> 继续迭代"
                    $changedThisIteration = $true
                } else {
                    Write-Info '  无修改，批次收敛'
                    break
                }
            } catch {
                Write-Warn "  报告解析失败，退回哈希/时间戳快速检测"
                # 回退: 使用 LastWriteTime 判断（简单版）
                $anyTouched = $false
                foreach($f in $batch){
                    # 简易：若在过去 5 秒内更新，视为修改（可进一步增强为哈希）
                    if((Get-Item $f).LastWriteTime -gt (Get-Date).AddSeconds(-5)) { $anyTouched = $true; break }
                }
                if($anyTouched){
                    Write-Info '  估测有修改(回退策略)，继续迭代'
                    $changedThisIteration = $true
                } else {
                    Write-Info '  回退检测：无修改，批次收敛'
                    break
                }
            }
        }
        if($globalError){ break }
        # 批次耗时与速率
        $batchElapsed = (Get-Date) - $batchStart
        $seconds = [Math]::Max($batchElapsed.TotalSeconds, 0.0001)
        $rate = '{0:N2}' -f ($batch.Count / $seconds)
        Write-Info ("批次完成: 耗时={0:s\.fff} 文件/秒={1} 迭代={2}" -f $batchElapsed,$rate,$iteration)
        if($iteration -ge $MaxIterations -and $changedThisIteration){
            Write-Warn '  达到最大迭代仍有修改（可能存在来回改动的 CodeFix）'
            $nonConverged += ,@{ Batch=$batchIndex; Files=$batch }
        }
    }

    $totalElapsed = (Get-Date) - $scriptStart
    if($globalError){
        Write-Err ("格式化过程中出现错误 (总耗时={0:c})" -f $totalElapsed)
        exit 1
    }
    if($nonConverged.Count -gt 0){
        Write-Warn "存在未收敛批次: $($nonConverged.Count)"
    }
    $totalFiles = $allFiles.Count
    $tSeconds = [Math]::Max($totalElapsed.TotalSeconds,0.0001)
    $overallRate = '{0:N2}' -f ($totalFiles / $tSeconds)
    Write-Ok ("格式化完成: 总文件={0} 批次={1} 迭代总数={2} 总耗时={3:c} 平均文件/秒={4}" -f $totalFiles,$batches.Count,$totalIterations,$totalElapsed,$overallRate)
    exit 0
}
finally {
    if($usingEnforce -and (Test-Path $backup)){
        Write-Info '恢复原始 .editorconfig'
        Move-Item -Force $backup $editorConfig
    }
}

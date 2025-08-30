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
# $MaxIterations 语义调整为: 单文件允许的最大格式化迭代次数 (防止振荡)
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

    # ===== 动态补位批次机制 =====
    $globalError = $false
    $totalRuns = 0                # 执行 dotnet format 次数
    $state = @{}                  # path -> @{ Iter = <int> }
    $converged = New-Object System.Collections.ArrayList
    $nonConverged = New-Object System.Collections.ArrayList
    $active = New-Object System.Collections.ArrayList
    $script:pendingIndex = 0      # 指向 $allFiles 中尚未进入 active 的起始位置 (script 作用域)

    function Add-Active([string]$f){
        if(-not $state.ContainsKey($f)){
            [void]$active.Add($f)
            $state[$f] = @{ Iter = 0 }
        }
    }
    function Fill-Active(){
        while($active.Count -lt $BatchSize -and $script:pendingIndex -lt $allFiles.Count){
            $next = $allFiles[$script:pendingIndex]
            $script:pendingIndex++
            Add-Active $next
        }
    }

    Fill-Active
    if($active.Count -eq 0){
        Write-Ok '无目标文件 (active 空)，结束。'
        exit 0
    }

    $repoRoot = (Get-Location).ProviderPath

    while($active.Count -gt 0){
        $runStart = Get-Date
        $totalRuns++
    # 剩余待进入的文件数（不含当前 active / 已收敛 / 未收敛）
    $remainingPending = $allFiles.Count - $script:pendingIndex
        Write-Info ("运行 #$totalRuns Active={0} Pending={1}" -f $active.Count,$remainingPending)

        # 准备报告文件
        if(-not (Test-Path 'gitignore')){ New-Item -ItemType Directory -Path 'gitignore' | Out-Null }
        $reportPath = Join-Path 'gitignore' 'format-report.json'
        if(Test-Path $reportPath){ Remove-Item $reportPath -Force -ErrorAction SilentlyContinue }

        # 组装 format 参数
        $formatArgs = @('format')
        if($solution){ $formatArgs += $solution.FullName }
        $formatArgs += '--include'; $formatArgs += @($active)
        $formatArgs += '--report'; $formatArgs += $reportPath
        $formatArgs += '--verbosity'; $formatArgs += 'minimal'

        & dotnet @formatArgs
        $formatExit = $LASTEXITCODE
        if($formatExit -ne 0){
            Write-Err "  dotnet format 退出码: $formatExit"
            $globalError = $true
            break
        }

        $report = $null
        $changedFull = @{}
        $changeFileCount = 0
        $changeEntryCount = 0
        try {
            $report = Parse-FormatReport -Path $reportPath
            if($report.HasChanges){
                $changeFileCount = $report.FileCount
                $changeEntryCount = $report.ChangeCount
                foreach($item in $report.Items){
                    $full = [IO.Path]::GetFullPath($item.FilePath)
                    $changedFull[$full.ToLowerInvariant()] = $true
                }
            }
        } catch {
            Write-Warn "  报告解析失败: $($_.Exception.Message)"
            # 若解析失败，退化为全部 active 继续一次（计入迭代）
            foreach($f in $active){
                $full = [IO.Path]::GetFullPath($f)
                $changedFull[$full.ToLowerInvariant()] = $true
            }
            $changeFileCount = $active.Count
        }

        $nextActive = New-Object System.Collections.ArrayList
        $kept = 0
        $newlyAdded = 0

        foreach($f in @($active)){
            $full = [IO.Path]::GetFullPath($f).ToLowerInvariant()
            if($changedFull.ContainsKey($full)){
                $state[$f].Iter++
                if($state[$f].Iter -lt $MaxIterations){
                    [void]$nextActive.Add($f)
                    $kept++
                } else {
                    Write-Warn "  未收敛: $f (迭代=$($state[$f].Iter))"
                    [void]$nonConverged.Add($f)
                }
            } else {
                [void]$converged.Add($f)
            }
        }

        # 用 pending 填充
        while($nextActive.Count -lt $BatchSize -and $script:pendingIndex -lt $allFiles.Count){
            $candidate = $allFiles[$script:pendingIndex]
            $script:pendingIndex++
            if(-not $state.ContainsKey($candidate)){
                $state[$candidate] = @{ Iter = 0 }
                [void]$nextActive.Add($candidate)
                $newlyAdded++
            }
        }

        $runElapsed = (Get-Date) - $runStart
        $seconds = [Math]::Max($runElapsed.TotalSeconds,0.0001)
        $rate = '{0:N2}' -f ($active.Count / $seconds)
    Write-Info ("  本轮: 改动文件={0} 改动条目={1} 保留继续={2} 新增补位={3} 耗时={4:c} 处理速率={5} 文件/秒" -f $changeFileCount,$changeEntryCount,$kept,$newlyAdded,$runElapsed,$rate)

        $active = $nextActive

    if($active.Count -eq 0 -and $script:pendingIndex -ge $allFiles.Count){
            Write-Info '所有文件均收敛/终止，结束循环。'
            break
        }
    }

    $totalElapsed = (Get-Date) - $scriptStart
    if($globalError){
        Write-Err ("格式化过程中出现错误 (总耗时={0:c})" -f $totalElapsed)
        exit 1
    }

    $processed = $converged.Count + $nonConverged.Count
    $tSeconds = [Math]::Max($totalElapsed.TotalSeconds,0.0001)
    $overallRate = '{0:N2}' -f ($processed / $tSeconds)
    if($nonConverged.Count -gt 0){
        Write-Warn ("未收敛文件: {0}" -f $nonConverged.Count)
    }
    Write-Ok ("格式化完成: 总文件={0} 已处理={1} 收敛={2} 未收敛={3} 运行次数={4} 总耗时={5:c} 平均文件/秒={6}" -f $allFiles.Count,$processed,$converged.Count,$nonConverged.Count,$totalRuns,$totalElapsed,$overallRate)
    exit 0
}
finally {
    if($usingEnforce -and (Test-Path $backup)){
        Write-Info '恢复原始 .editorconfig'
        Move-Item -Force $backup $editorConfig
    }
}

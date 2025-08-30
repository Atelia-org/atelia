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
    [ValidateSet('full','diff')][string]$Scope = 'full',
    [int]$MaxIterations = 5,                 # 单文件最大迭代次数
    [int]$MaxFilesPerRun = 512,              # 每次运行最大文件数（软上限）
    [int]$MaxCmdChars = 30000,               # 非响应文件模式下的命令行字符上限
    [switch]$UseResponseFile                 # 使用响应文件规避命令行长度限制（默认开启；传 -UseResponseFile:$false 关闭）
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 常量 (BatchSize 已被长度/数量双阈值替换，仅保留注释说明)
# 旧参数 BatchSize -> 以 $MaxFilesPerRun / $MaxCmdChars / 响应文件 组合实现动态批次
if(-not $PSBoundParameters.ContainsKey('UseResponseFile')){ $UseResponseFile = $true }

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

    # ===== 单循环 + 顶部统一动态补位策略 =====
    $globalError = $false
    $totalRuns = 0
    $state = @{}                       # path -> @{ Iter = <int> }
    $converged = New-Object System.Collections.ArrayList
    $nonConverged = New-Object System.Collections.ArrayList
    $carry = New-Object System.Collections.ArrayList  # 上轮需要继续 (changed) 的文件
    $script:pendingIndex = 0

    function New-RunFiles([System.Collections.ArrayList]$carryOver){
        $run = New-Object System.Collections.ArrayList
        foreach($f in $carryOver){ [void]$run.Add($f) }
        if($UseResponseFile){
            while($run.Count -lt $MaxFilesPerRun -and $script:pendingIndex -lt $allFiles.Count){
                $cand = $allFiles[$script:pendingIndex]; $script:pendingIndex++
                if(-not $state.ContainsKey($cand)){ $state[$cand] = @{ Iter = 0 } }
                if(-not $run.Contains($cand)){ [void]$run.Add($cand) }
            }
            return ,$run
        }
        # 非响应文件模式: 基于命令行长度动态填充
        $baseArgs = @('format')
        if($solution){ $baseArgs += $solution.FullName }
        $baseArgs += '--include'
        function Get-Len([object[]]$arr){ (($arr -join ' ') | Measure-Object -Character).Characters }
        $currentLen = Get-Len ($baseArgs + $run)
        while($script:pendingIndex -lt $allFiles.Count -and $run.Count -lt $MaxFilesPerRun){
            $cand = $allFiles[$script:pendingIndex]
            $increment = $cand.Length + 1  # 近似: 空格 + 路径
            if(($currentLen + $increment) -gt $MaxCmdChars){
                if($run.Count -eq 0){ # 强制至少一个
                    $script:pendingIndex++
                    if(-not $state.ContainsKey($cand)){ $state[$cand] = @{ Iter = 0 } }
                    [void]$run.Add($cand)
                }
                break
            }
            $script:pendingIndex++
            if(-not $state.ContainsKey($cand)){ $state[$cand] = @{ Iter = 0 } }
            [void]$run.Add($cand)
            $currentLen += $increment
        }
        return ,$run
    }

    while($true){
        $runFiles = New-RunFiles $carry
        if($runFiles.Count -eq 0){ Write-Info '无待处理文件，结束循环。'; break }
        $carry = New-Object System.Collections.ArrayList  # 清空，待本轮结果填充
        $totalRuns++
        $runStart = Get-Date
        $remainingPending = $allFiles.Count - $script:pendingIndex
        Write-Info ("运行 #$totalRuns 文件数={0} CarryOver={1} Pending={2} (UseRsp={3})" -f $runFiles.Count,($carry.Count),$remainingPending,$UseResponseFile)

        if(-not (Test-Path 'gitignore')){ New-Item -ItemType Directory -Path 'gitignore' | Out-Null }
        $reportPath = Join-Path 'gitignore' 'format-report.json'
        if(Test-Path $reportPath){ Remove-Item $reportPath -Force -ErrorAction SilentlyContinue }

        if($UseResponseFile){
            $rspPath = Join-Path 'gitignore' 'format_args.rsp'
            if(Test-Path $rspPath){ Remove-Item $rspPath -Force -ErrorAction SilentlyContinue }
            $rspLines = @('format')
            if($solution){ $rspLines += $solution.FullName }
            $rspLines += '--include'
            $rspLines += $runFiles
            $rspLines += '--report'; $rspLines += $reportPath
            $rspLines += '--verbosity'; $rspLines += 'minimal'
            $rspLines | Set-Content $rspPath -Encoding UTF8
            & dotnet "@$rspPath"
        } else {
            $formatArgs = @('format')
            if($solution){ $formatArgs += $solution.FullName }
            $formatArgs += '--include'; $formatArgs += $runFiles
            $formatArgs += '--report'; $formatArgs += $reportPath
            $formatArgs += '--verbosity'; $formatArgs += 'minimal'
            & dotnet @formatArgs
        }
        $formatExit = $LASTEXITCODE
        if($formatExit -ne 0){ Write-Err "  dotnet format 退出码: $formatExit"; $globalError = $true; break }

        $report = $null
        $changed = @{}
        $changeFileCount = 0
        $changeEntryCount = 0
        try {
            $report = Parse-FormatReport -Path $reportPath
            if($report.HasChanges){
                $changeFileCount = $report.FileCount
                $changeEntryCount = $report.ChangeCount
                foreach($item in $report.Items){
                    $changed[[IO.Path]::GetFullPath($item.FilePath).ToLowerInvariant()] = $true
                }
            }
        } catch {
            Write-Warn "  报告解析失败: $($_.Exception.Message) -> 假定全部变更继续"
            foreach($f in $runFiles){ $changed[[IO.Path]::GetFullPath($f).ToLowerInvariant()] = $true }
            $changeFileCount = $runFiles.Count
        }

        $kept = 0
        foreach($f in $runFiles){
            $fullKey = [IO.Path]::GetFullPath($f).ToLowerInvariant()
            if($changed.ContainsKey($fullKey)){
                if(-not $state.ContainsKey($f)){ $state[$f] = @{ Iter = 0 } }
                $state[$f].Iter++
                if($state[$f].Iter -lt $MaxIterations){ [void]$carry.Add($f); $kept++ } else { [void]$nonConverged.Add($f); Write-Warn "  未收敛: $f (迭代=$($state[$f].Iter))" }
            } else {
                [void]$converged.Add($f)
            }
        }

        $runElapsed = (Get-Date) - $runStart
        $seconds = [Math]::Max($runElapsed.TotalSeconds,0.0001)
        $rate = '{0:N2}' -f ($runFiles.Count / $seconds)
        Write-Info ("  本轮: 改动文件={0} 改动条目={1} 保留继续={2} 耗时={3:c} 速率={4} 文件/秒" -f $changeFileCount,$changeEntryCount,$kept,$runElapsed,$rate)

        if($carry.Count -eq 0 -and $script:pendingIndex -ge $allFiles.Count){
            Write-Info '所有文件收敛/处理完毕。'
            break
        }
    }

    $totalElapsed = (Get-Date) - $scriptStart
    if($globalError){ Write-Err ("格式化过程中出现错误 (总耗时={0:c})" -f $totalElapsed); exit 1 }
    $processed = $converged.Count + $nonConverged.Count
    $tSeconds = [Math]::Max($totalElapsed.TotalSeconds,0.0001)
    $overallRate = '{0:N2}' -f ($processed / $tSeconds)
    if($nonConverged.Count -gt 0){ Write-Warn ("未收敛文件: {0}" -f $nonConverged.Count) }
    Write-Ok ("格式化完成: 总文件={0} 已处理={1} 收敛={2} 未收敛={3} 运行次数={4} 总耗时={5:c} 平均文件/秒={6} (Rsp={7})" -f $allFiles.Count,$processed,$converged.Count,$nonConverged.Count,$totalRuns,$totalElapsed,$overallRate,$UseResponseFile)
    exit 0
}
finally {
    if($usingEnforce -and (Test-Path $backup)){
        Write-Info '恢复原始 .editorconfig'
        Move-Item -Force $backup $editorConfig
    }
}

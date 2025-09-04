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
    # Scope:
    #   full   -> 全仓所有已跟踪的 *.cs (git ls-files)
    #   diff   -> 工作区/暂存/未跟踪改动合集 (unstaged + staged + untracked)
    #   staged -> 仅当前已暂存准备提交的 *.cs (git diff --cached)
    [ValidateSet('full','diff','staged')][string]$Scope = 'diff',
    [ValidateRange(1,100)][int]$MaxIterations = 5,       # 单文件最大迭代次数
    [ValidateRange(1,4096)][int]$MaxFilesPerRun = 512,   # 每批最大文件数
    [string]$SummaryJson = 'gitignore/format-summary.json' # 汇总输出路径
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 始终使用响应文件模式（移除 UseResponseFile 参数）
$UseResponseFile = $true

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
    try { $data = $raw | ConvertFrom-Json -ErrorAction Stop } catch { throw "报告 JSON 解析失败: $Path - $($_.Exception.Message)" }
    if(-not $data){ return [pscustomobject]@{ HasChanges=$false; FileCount=0; ChangeCount=0; Items=@() } }

    # 兼容两种形态：数组根 或 单对象根
    if($data -isnot [System.Collections.IEnumerable] -or $data -is [string]){
        $data = @($data)
    }

    $changedItems = @()
    $totalEntryCount = 0
    foreach($item in $data){
        if(-not $item){ continue }
        # 优先使用 FileChanges 属性（dotnet format 现有输出）
        if($item.PSObject.Properties.Name -contains 'FileChanges' -and $item.FileChanges){
            $fc = $item.FileChanges | Where-Object { $_ }
            if($fc.Count -gt 0){
                $changedItems += $item
                $totalEntryCount += $fc.Count
                continue
            }
        }
        # 退化：有 FilePath 且标记了 Changed / Formatted 等信息
        $maybeFilePath = $item.FilePath
        $flagProps = @('Changed','Formatted','Updated','Fixed') | Where-Object { $item.PSObject.Properties.Name -contains $_ }
        $isChanged = $false
        foreach($p in $flagProps){ if($item.$p){ $isChanged = $true; break } }
        if($maybeFilePath -and $isChanged){
            # 人工包装成 FileChanges 兼容后续逻辑
            if(-not ($item.PSObject.Properties.Name -contains 'FileChanges')){
                $item | Add-Member -NotePropertyName FileChanges -NotePropertyValue @(@{ Id='(unknown)'; Description='(inferred change)'; })
            }
            $changedItems += $item
            $totalEntryCount += 1
        }
    }

    return [pscustomobject]@{
        HasChanges  = ($changedItems.Count -gt 0)
        FileCount   = $changedItems.Count
        ChangeCount = $totalEntryCount
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

function Normalize-Path([string]$p){ return ([IO.Path]::GetFullPath($p)) }

function Get-FullFiles(){
    # 使用 git ls-files (全部) 然后过滤扩展，确保递归；避免 "git ls-files *.cs" 只匹配根目录的问题
    $files = (& git ls-files 2>$null) | Where-Object { $_ }
    $files = $files | Where-Object { [IO.Path]::GetExtension($_) -eq '.cs' }
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

function Get-StagedFiles(){
    # 仅取已暂存的更改/新增（排除删除的路径）
    $staged = git diff --name-only --cached | Where-Object { $_ }
    $files = $staged | Where-Object { [IO.Path]::GetExtension($_) -eq '.cs' -and (Test-Path $_) }
    return $files
}

function Invoke-FormatBatch {
    param(
        [string[]]$Files,
        [string]$ReportPath,
        [System.IO.FileInfo]$Solution
    )
    # 生成响应文件（全部路径加引号，处理空格）
    $rspPath = Join-Path 'gitignore' 'format_args.rsp'
    if(Test-Path $rspPath){ Remove-Item $rspPath -Force -ErrorAction SilentlyContinue }
    $rspLines = @('format')
    if($Solution){ $rspLines += $Solution.FullName }
    $rspLines += '--include'
    # 仅在包含空格时加引号，避免被当成字面字符
    foreach($f in $Files){
        if($f -match '\s'){ $rspLines += '"{0}"' -f $f } else { $rspLines += $f }
    }
    $rspLines += '--report'; $rspLines += $ReportPath
    $rspLines += '--verbosity'; $rspLines += 'minimal'
    $rspLines | Set-Content $rspPath -Encoding UTF8
    & dotnet "@$rspPath"
    return $LASTEXITCODE
}

function Write-SummaryJson {
    param(
        [string]$Path,
        [hashtable]$Data
    )
    try {
        ($Data | ConvertTo-Json -Depth 4) | Set-Content $Path -Encoding UTF8
        Write-Info "已写入汇总 JSON: $Path"
    } catch {
        Write-Warn "写入汇总 JSON 失败: $($_.Exception.Message)"
    }
}

$summary = @{}
$globalError = $false
try {
    $allFiles = switch($Scope){
        'full'   { Get-FullFiles }
        'diff'   { Get-DiffFiles }
        'staged' { Get-StagedFiles }
        default  { throw "未知 Scope: $Scope" }
    }
    $allFiles = @($allFiles)  # 保证始终为数组，防止单文件降级为字符串
    if(-not $allFiles -or $allFiles.Count -eq 0){
        Write-Ok '无目标文件，结束。';
        $summary = @{ total=0 };
        return;
    }

    Write-Info "Scope=$Scope 文件数: $($allFiles.Count)"

    # 初始化队列 & 状态
    $queue = [System.Collections.Generic.Queue[string]]::new() # 保存相对路径
    $state = @{}   # 绝对规范路径 -> @{ Iter = <int> }
    foreach($f in $allFiles){
        $abs = Normalize-Path $f
        $queue.Enqueue($f)      # 使用原始（git 输出的）相对路径以便 include
        $state[$abs] = @{ Iter = 0 }
    }
    $converged = New-Object System.Collections.Generic.List[string]
    $nonConverged = New-Object System.Collections.Generic.List[string]
    $totalRuns = 0
    $runReports = New-Object System.Collections.Generic.List[string]
    $reportDir = 'gitignore/format-reports'
    if(-not (Test-Path $reportDir)){ New-Item -ItemType Directory -Path $reportDir | Out-Null }

    while($queue.Count -gt 0){
        $batch = @()  # 相对路径集合
        while($queue.Count -gt 0 -and $batch.Count -lt $MaxFilesPerRun){
            $batch += $queue.Dequeue()  # 相对路径
        }
        $totalRuns++
        Write-Info ("运行 #$totalRuns 文件数={0} 队列剩余={1}" -f $batch.Count,$queue.Count)

    if(-not (Test-Path 'gitignore')){ New-Item -ItemType Directory -Path 'gitignore' | Out-Null }
    # 为每次运行生成独立报告文件，保留历史供调试
    $reportPath = Join-Path $reportDir ('format-report_run{0:000}.json' -f $totalRuns)
    if(Test-Path $reportPath){ Remove-Item $reportPath -Force -ErrorAction SilentlyContinue }
    $runReports.Add($reportPath) | Out-Null

    $exit = Invoke-FormatBatch -Files $batch -ReportPath $reportPath -Solution $solution
        if($exit -ne 0){ Write-Err "dotnet format 退出码: $exit"; $globalError = $true; break }

        $changeFileCount = 0; $changeEntryCount = 0
        $changedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        try {
            $report = Parse-FormatReport -Path $reportPath
            if($report.HasChanges){
                $changeFileCount = $report.FileCount
                $changeEntryCount = $report.ChangeCount
                foreach($item in $report.Items){ $null = $changedSet.Add( (Normalize-Path $item.FilePath) ) }
            }
        } catch {
            Write-Err "报告解析失败: $($_.Exception.Message)"; $globalError = $true; break
        }

        $requeue = 0
        foreach($rel in $batch){
            $absKey = Normalize-Path $rel
            if($changedSet.Contains($absKey)){
                if(-not $state.ContainsKey($absKey)){ $state[$absKey] = @{ Iter = 0 } }
                $state[$absKey].Iter++
                if($state[$absKey].Iter -lt $MaxIterations){
                    $queue.Enqueue($rel); $requeue++
                } else {
                    $nonConverged.Add($rel); Write-Warn "  未收敛: $rel (迭代=$($state[$absKey].Iter))"
                }
            } else {
                $converged.Add($rel)
            }
        }
        Write-Info ("  本轮: 改动文件={0} 改动条目={1} 重新入队={2}" -f $changeFileCount,$changeEntryCount,$requeue)
    }

    $elapsed = (Get-Date) - $scriptStart
    $processed = $converged.Count + $nonConverged.Count
    $rate = if($elapsed.TotalSeconds -gt 0){ '{0:N2}' -f ($processed / $elapsed.TotalSeconds) } else { 'N/A' }
    if($nonConverged.Count -gt 0){ Write-Warn "未收敛文件: $($nonConverged.Count)" }
    if(-not $globalError){
        Write-Ok ("格式化完成: 总文件={0} 已处理={1} 收敛={2} 未收敛={3} 运行次数={4} 总耗时={5:c} 平均文件/秒={6}" -f $allFiles.Count,$processed,$converged.Count,$nonConverged.Count,$totalRuns,$elapsed,$rate)
    }

    $summary = @{
        scope = $Scope
        totalFiles = $allFiles.Count
        processedFiles = $processed
        convergedFiles = $converged.Count
        nonConvergedFiles = $nonConverged.Count
        runs = $totalRuns
        elapsedSeconds = [Math]::Round($elapsed.TotalSeconds,3)
        filesPerSecond = $rate
        nonConvergedList = $nonConverged
    reportFiles = $runReports
        maxIterations = $MaxIterations
        timestamp = (Get-Date).ToString('o')
        success = (-not $globalError)
    }
 } catch {
    Write-Warn "主循环异常: $($_.Exception.Message)"
 } finally {
    if($usingEnforce -and (Test-Path $backup)){
        Write-Info '恢复原始 .editorconfig'
        Move-Item -Force $backup $editorConfig
    }
    if($SummaryJson){ Write-SummaryJson -Path $SummaryJson -Data $summary }
    if($globalError){ exit 1 } else { exit 0 }
}

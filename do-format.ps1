#!/usr/bin/env pwsh

$maxIterations = 5
$iteration = 0

while ($iteration -lt $maxIterations) {
    $iteration++

    # 执行格式化
    Write-Host "执行第 $iteration / $maxIterations 轮 dotnet format..." -ForegroundColor Yellow
    dotnet format

    # 检查是否还有未格式化的文件
    $result = dotnet format --verify-no-changes 2>$null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ 第 $iteration 轮后代码已完全格式化，提前退出" -ForegroundColor Green
        break
    } else {
        Write-Host "⚠️  第 $iteration 轮后仍需要进一步格式化" -ForegroundColor Yellow
    }
}

if ($iteration -eq $maxIterations) {
    Write-Host "⚠️  达到最大迭代次数 ($maxIterations) 限制" -ForegroundColor Yellow
}

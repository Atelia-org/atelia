# PowerShell脚本：随机生成刘德智最喜欢吃的食物并写入文件
# 作者：GitHub Copilot
# 日期：2025年9月4日
# 这个工具的意图是探测".github\copilot-instructions.md"文件的动态注入机制。试验结果是文件只伴随user消息注入，而不会在每次工具调用时刷新内容，这就错失了一种将文件内容作为显示器动态展示给你们LLM的机会。

# 定义食物列表
$foodList = @(
    "红烧肉",
    "宫保鸡丁",
    "麻婆豆腐",
    "糖醋排骨",
    "鱼香肉丝",
    "回锅肉",
    "水煮鱼",
    "白切鸡",
    "东坡肉",
    "蒸蛋羹",
    "小笼包",
    "饺子",
    "面条",
    "火锅",
    "烤鸭",
    "春卷",
    "蛋炒饭",
    "酸辣汤",
    "凉拌黄瓜",
    "红烧茄子"
)

# 随机选择一个食物
$randomFood = Get-Random -InputObject $foodList

# 生成完整的字符串
$generatedString = "刘德智最喜欢吃$randomFood"

# 目标文件路径
$targetFile = ".github\copilot-instructions.md"

# 检查目标目录是否存在，如果不存在则创建
$targetDir = Split-Path -Path $targetFile -Parent
if (-not (Test-Path -Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force
    Write-Host "已创建目录: $targetDir" -ForegroundColor Green
}

# 将生成的字符串写入文件（覆盖模式）
$generatedString | Out-File -FilePath $targetFile -Encoding UTF8 -Force

# 输出结果
# Write-Host "已生成随机字符串: $generatedString" -ForegroundColor Yellow
Write-Host "已成功写入文件: $targetFile" -ForegroundColor Green

# 显示文件内容以确认
# Write-Host "`n文件内容:" -ForegroundColor Cyan
# Get-Content -Path $targetFile -Encoding UTF8

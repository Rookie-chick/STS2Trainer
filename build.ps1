# 构建 / 打包脚本（Windows PowerShell）
# 用法： powershell -ExecutionPolicy Bypass -File build.ps1
# 产出： dist\STS2Trainer.exe（self-contained 单文件，目标机无需安装 .NET）

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

Write-Host "[1/3] 构建控制台版（核心库 + 调试前端）" -ForegroundColor Cyan
dotnet build "src\sts2trainer\sts2trainer.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "控制台版构建失败" }

Write-Host "[2/3] 发布界面版（self-contained 单文件）" -ForegroundColor Cyan
dotnet publish "src\sts2trainer-ui\sts2trainer-ui.csproj" -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "dist"
if ($LASTEXITCODE -ne 0) { throw "界面版发布失败" }

Write-Host "[3/3] 结果" -ForegroundColor Cyan
Get-ChildItem "dist" | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host "完成：dist\STS2Trainer.exe" -ForegroundColor Green

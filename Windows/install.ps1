# 安装 OpenCode 红绿灯（Windows）
# 用法：右键「使用 PowerShell 运行」，或在 PowerShell 里执行：
#   powershell -ExecutionPolicy Bypass -File .\install.ps1

$ErrorActionPreference = "Stop"

$appName = "OpenCodeTrafficLight"
$appDir  = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$source  = Join-Path $PSScriptRoot "$appName.exe"
$target  = Join-Path $appDir "$appName.exe"
$runKey  = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

if (-not (Test-Path $source)) {
    Write-Host "错误：找不到 $appName.exe，请先运行 build.sh / dotnet build，或解压发布包到当前目录。" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

Write-Host "== 安装 OpenCode 红绿灯 ==" -ForegroundColor Cyan

# 结束正在运行的实例
Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item $source $target -Force

# 登录自启
if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
Set-ItemProperty -Path $runKey -Name $appName -Value "`"$target`""

# 解除从网络下载文件的锁定标记
Unblock-File -Path $target -ErrorAction SilentlyContinue

Start-Process $target

Write-Host ""
Write-Host "安装完成：$target" -ForegroundColor Green
Write-Host "已注册登录自启（HKCU\...\Run\$appName）。"
Write-Host "终端里运行 opencode 时悬浮窗自动出现；OpenCode 退出后自动隐藏。"
Write-Host ""
Read-Host "按回车关闭此窗口"

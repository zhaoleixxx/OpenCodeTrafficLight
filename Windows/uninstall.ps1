# 卸载 OpenCode 红绿灯（Windows）

$ErrorActionPreference = "SilentlyContinue"

$appName = "OpenCodeTrafficLight"
$appDir  = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$prefs   = Join-Path $env:APPDATA $appName
$runKey  = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

Write-Host "== 卸载 OpenCode 红绿灯 ==" -ForegroundColor Cyan

Get-Process -Name $appName | Stop-Process -Force
Remove-Item -Path $appDir -Recurse -Force
Remove-Item -Path $prefs -Recurse -Force
Remove-ItemProperty -Path $runKey -Name $appName -ErrorAction SilentlyContinue

Write-Host "已卸载。" -ForegroundColor Green
Read-Host "按回车关闭此窗口"

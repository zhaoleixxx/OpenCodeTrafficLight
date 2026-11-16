#!/bin/bash
# 构建 Windows 版（可在 macOS / Linux 上交叉编译，无需 Windows 机器）
#
# 产物：dist/OpenCodeTrafficLight.exe
#   - .NET Framework 4.8，Windows 10/11 自带运行时
#   - 单文件、无第三方 DLL、无需安装
set -euo pipefail

cd "$(dirname "$0")"

echo "== 构建 Windows 版 =="
dotnet build -c Release

mkdir -p dist
cp bin/Release/net48/OpenCodeTrafficLight.exe dist/

echo
echo "构建完成: $(pwd)/dist/OpenCodeTrafficLight.exe"
ls -lh dist/OpenCodeTrafficLight.exe

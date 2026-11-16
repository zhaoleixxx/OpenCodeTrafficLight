#!/bin/bash
# 双击即可卸载。
set -euo pipefail

APP_NAME="OpenCodeTrafficLight"
LABEL="local.opencode.trafficlight"
UID_NUM="$(id -u)"

echo "== 卸载 OpenCode 红绿灯 =="

launchctl bootout "gui/$UID_NUM/$LABEL" 2>/dev/null || true
pkill -f "$APP_NAME.app/Contents/MacOS/$APP_NAME" 2>/dev/null || true

rm -rf "$HOME/Applications/$APP_NAME.app"
rm -rf "$HOME/Library/Application Support/OpenCodeTrafficLight"
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"

echo "已卸载。"
echo
printf "按任意键关闭此窗口…"
read -r -n 1 -s || true
echo

#!/bin/bash
# 双击即可安装：把 App 复制到 ~/Applications 并注册登录自启。
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="OpenCodeTrafficLight"
LABEL="local.opencode.trafficlight"
INSTALL_DIR="$HOME/Applications"
APP="$INSTALL_DIR/$APP_NAME.app"
SUPPORT_DIR="$HOME/Library/Application Support/OpenCodeTrafficLight"
AGENT="$HOME/Library/LaunchAgents/$LABEL.plist"
UID_NUM="$(id -u)"

echo "== 安装 OpenCode 红绿灯 v2 =="

if [ ! -d "$APP_NAME.app" ]; then
    echo "错误：安装包不完整，找不到 $APP_NAME.app。先运行 ./build.sh。"
    exit 1
fi

pkill -f "$APP_NAME.app/Contents/MacOS/$APP_NAME" 2>/dev/null || true
launchctl bootout "gui/$UID_NUM/$LABEL" 2>/dev/null || true

mkdir -p "$INSTALL_DIR" "$SUPPORT_DIR" "$(dirname "$AGENT")" "$HOME/Library/Logs"

# 备份旧版本（若有）
if [ -d "$APP" ]; then
    BACKUP="$SUPPORT_DIR/backup-$(date +%Y%m%d-%H%M%S).app"
    ditto "$APP" "$BACKUP" 2>/dev/null || true
fi

rm -rf "$APP"
ditto "$APP_NAME.app" "$APP"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
codesign --force --sign - "$APP" >/dev/null 2>&1 || true

cp watcher.sh "$SUPPORT_DIR/watcher.sh"
chmod +x "$SUPPORT_DIR/watcher.sh"
rm -f "$SUPPORT_DIR/quit-flag"

cat > "$AGENT" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/bash</string>
        <string>$SUPPORT_DIR/watcher.sh</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ThrottleInterval</key>
    <integer>10</integer>
    <key>LimitLoadToSessionType</key>
    <string>Aqua</string>
    <key>StandardErrorPath</key>
    <string>$HOME/Library/Logs/OpenCodeTrafficLight.watcher.log</string>
    <key>StandardOutPath</key>
    <string>$HOME/Library/Logs/OpenCodeTrafficLight.watcher.log</string>
</dict>
</plist>
PLIST

launchctl bootstrap "gui/$UID_NUM" "$AGENT" 2>/dev/null || true

echo
echo "安装完成：$APP"
echo "打开 OpenCode 时悬浮窗会自动出现，关闭 OpenCode 后自动退出。"
echo "已注册登录自启（LaunchAgent: ${LABEL}）。"
echo
printf "按任意键关闭此窗口…"
read -r -n 1 -s || true
echo

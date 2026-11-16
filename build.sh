#!/bin/bash
# 构建 OpenCode 红绿灯 v2（通用二进制：Apple 芯片 + Intel）
set -euo pipefail

cd "$(dirname "$0")"

APP="OpenCodeTrafficLight.app"
BIN="build/OpenCodeTrafficLight"
ARM="build/arm64"
X86="build/x86_64"

mkdir -p build "$APP/Contents/MacOS"

echo "== 编译 arm64 =="
swiftc -O -swift-version 5 -target arm64-apple-macosx13.0 -framework AppKit \
    -o "$ARM" Sources/main.swift

echo "== 编译 x86_64 =="
if swiftc -O -swift-version 5 -target x86_64-apple-macosx13.0 -framework AppKit \
    -o "$X86" Sources/main.swift 2>/dev/null; then
    lipo -create "$ARM" "$X86" -output "$BIN"
else
    echo "（x86_64 编译不可用，仅构建 arm64）"
    cp "$ARM" "$BIN"
fi

cp "$BIN" "$APP/Contents/MacOS/OpenCodeTrafficLight"
cp Info.plist "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
chmod +x "$APP/Contents/MacOS/OpenCodeTrafficLight"

echo "== 签名 =="
codesign --force --sign - "$APP" >/dev/null 2>&1 || true

echo "构建完成: $(pwd)/$APP"
file "$APP/Contents/MacOS/OpenCodeTrafficLight"

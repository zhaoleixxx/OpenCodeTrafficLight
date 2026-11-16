#!/bin/bash
# 守护 OpenCode 红绿灯悬浮窗：
#  - 检测到终端版 OpenCode 正在运行 -> 启动悬浮窗
#  - 终端版 OpenCode 全部关闭 -> 关闭悬浮窗
#  - 手动退出后的暂停标记最多保留 15 分钟，避免永久锁死
# 由 LaunchAgent 常驻运行。

APP_NAME="OpenCodeTrafficLight"
APP="$HOME/Applications/$APP_NAME.app"
BIN="$APP/Contents/MacOS/$APP_NAME"
FLAG="$HOME/Library/Application Support/OpenCodeTrafficLight/quit-flag"
CLI_BIN="$HOME/.opencode/bin/opencode"
INTERVAL=2
PAUSE_SECONDS=900
LOG="$HOME/Library/Logs/OpenCodeTrafficLight.watcher.log"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG" 2>/dev/null || true
}

opencode_running() {
    # 只识别终端版 CLI 的实际进程，不跟随桌面版 OpenCode。
    # `opencode serve --service` 是后台服务，不能单独触发红绿灯；
    # `opencode-pty` 也不是一次终端会话。
    [ -x "$CLI_BIN" ] || return 1

    local pid cmd tty
    for pid in $(pgrep -x opencode 2>/dev/null); do
        cmd=$(ps -p "$pid" -o command= 2>/dev/null || true)
        tty=$(ps -p "$pid" -o tty= 2>/dev/null | tr -d '[:space:]')

        # 只认附着在真实终端上的交互式 CLI。
        # 后台 `opencode run`、OpenCode 服务和其他无 TTY 进程全部忽略。
        [ -n "$tty" ] && [ "$tty" != "??" ] || continue

        case "$cmd" in
            "$CLI_BIN"|opencode)
                return 0
                ;;
            "$CLI_BIN "*|opencode\ *)
                case "$cmd" in
                    *serve\ --service*) ;;
                    *) return 0 ;;
                esac
                ;;
        esac
    done

    return 1
}

widget_running() {
    # pgrep -x 按进程名匹配，避免同一个 widget 被重复拉起。
    pgrep -x OpenCodeTrafficLight >/dev/null 2>&1
}

pause_active() {
    [ -f "$FLAG" ] || return 1

    local now modified age
    now=$(date +%s)
    modified=$(stat -f %m "$FLAG" 2>/dev/null || echo 0)
    age=$((now - modified))

    if [ "$age" -ge "$PAUSE_SECONDS" ]; then
        rm -f "$FLAG"
        log "pause expired; cleared quit flag"
        return 1
    fi

    return 0
}

log "watcher start"

while true; do
    if opencode_running; then
        if ! pause_active && ! widget_running; then
            # 直接启动，避免 LaunchServices 的启动延迟；应用内有单实例锁
            ( "$BIN" >/dev/null 2>&1 & )
            log "launch widget"
        fi
    else
        # OpenCode 关闭：清除"本次退出"标记并关闭悬浮窗
        rm -f "$FLAG"
        if widget_running; then
            log "quit widget (OpenCode closed)"
            pkill -f "$BIN" 2>/dev/null || true
        fi
    fi
    sleep "$INTERVAL"
done

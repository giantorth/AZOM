#!/usr/bin/env bash
# Start/stop SimHub installed as a non-Steam game in its own Proton prefix.
# Mirrors the launch recipe in ~/src/linux-sim-launcher/sim-launcher:
# protontricks-launch with a scrubbed environment so Steam's runtime vars
# from the parent shell don't contaminate the SimHub prefix.

set -euo pipefail

APPID=2825720939
SIMHUB_EXE="$HOME/.local/share/Steam/steamapps/compatdata/2825720939/pfx/drive_c/Program Files (x86)/SimHub/SimHubWPF.exe"
PROC_NAME=SimHubWPF.exe
PT_LAUNCHER=$(command -v protontricks-launch || true)

usage() {
    echo "Usage: $0 {start|stop|status}" >&2
    exit 2
}

is_running() {
    pgrep -f "$PROC_NAME" >/dev/null 2>&1
}

start_simhub() {
    if is_running; then
        echo "SimHub already running (pid(s): $(pgrep -f "$PROC_NAME" | tr '\n' ' '))"
        return 0
    fi
    [[ -n "$PT_LAUNCHER" ]] || { echo "protontricks-launch not found" >&2; exit 1; }
    [[ -f "$SIMHUB_EXE" ]]  || { echo "SimHub exe missing: $SIMHUB_EXE" >&2; exit 1; }

    # Scrub Steam/Proton/Wine env so protontricks picks fresh settings for the
    # SimHub prefix instead of inheriting some other game's context. Unset by
    # name so env-values containing spaces/JSON/backslashes pass through intact.
    local v
    for v in $(compgen -e | awk '/^(STEAM_COMPAT|PROTON|WINE|SteamGame|SteamAppId|SteamOverlay)/'); do
        unset "$v"
    done
    unset LD_LIBRARY_PATH PYTHONPATH PYTHONHOME STEAM_RUNTIME_LIBRARY_PATH \
          WINEPREFIX WINEESYNC WINEFSYNC WINELOADERNOEXEC WINEDLLPATH WINEDEBUG

    local log="${TMPDIR:-/tmp}/simhub-launch-${USER}.log"
    echo "Launching SimHub (appid=$APPID), log: $log"
    nohup setsid "$PT_LAUNCHER" --appid "$APPID" "$SIMHUB_EXE" \
        </dev/null >"$log" 2>&1 &
    disown
    echo "Launch issued. SimHub takes ~10s to initialise."
}

graceful_close() {
    # Send Escape twice to dismiss any modal/popup, then Alt+F4 to SimHub's X
    # window so WPF runs Closing handlers and disposes NotifyIcon — avoids
    # stale tray icon left by SIGTERM.
    command -v xdotool >/dev/null 2>&1 || return 1
    local wins
    wins=$(xdotool search --name 'SimHub' 2>/dev/null) || return 1
    [[ -z "$wins" ]] && return 1
    echo "Requesting graceful close via xdotool (Escape x2, Alt+F4)..."
    local w
    for w in $wins; do
        xdotool windowactivate --sync "$w" key --window "$w" Escape 2>/dev/null || true
        sleep 0.1
        xdotool key --window "$w" Escape 2>/dev/null || true
        sleep 0.1
        xdotool key --window "$w" alt+F4 2>/dev/null || true
    done
    for _ in {1..30}; do
        is_running || return 0
        sleep 0.5
    done
    return 1
}

stop_simhub() {
    if ! is_running; then
        echo "SimHub not running"
    else
        echo "Stopping SimHub..."
        if ! graceful_close; then
            pkill -TERM -f "$PROC_NAME" || true
            for _ in {1..20}; do
                is_running || break
                sleep 0.5
            done
            if is_running; then
                echo "Forcing SIGKILL..."
                pkill -KILL -f "$PROC_NAME" || true
            fi
        fi
    fi
    # Kill leftover protontricks wrappers tied to this appid only.
    pkill -TERM -f "protontricks.App${APPID}" 2>/dev/null || true
    sleep 0.3
    pkill -KILL -f "protontricks.App${APPID}" 2>/dev/null || true
    echo "Stopped."
}

status_simhub() {
    if is_running; then
        echo "SimHub running: $(pgrep -af "$PROC_NAME")"
    else
        echo "SimHub not running"
    fi
}

case "${1:-}" in
    start)  start_simhub  ;;
    stop)   stop_simhub   ;;
    status) status_simhub ;;
    *)      usage         ;;
esac

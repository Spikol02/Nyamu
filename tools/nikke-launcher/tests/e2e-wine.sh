#!/bin/sh -e
# SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
# SPDX-License-Identifier: GPL-3.0-or-later

# Runs the Windows build of the session agent under Wine against stand-in programs.
# Needs: dotnet, x86_64-w64-mingw32-gcc, wine, xvfb-run. Usage: tests/e2e-wine.sh [workdir]

here=$(cd "$(dirname "$0")/.." && pwd)
work=${1:-$(mktemp -d)}
export WINEPREFIX="$work/prefix" WINEDEBUG=-all WINEDLLOVERRIDES="mshtml=d"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$work"
dotnet publish "$here/src/Nyakke" -c Release -r win-x64 --self-contained -o "$work/app" >/dev/null
x86_64-w64-mingw32-gcc -O1 -mwindows -o "$work/stub.exe" "$here/tests/stubs/stub.c"
wineboot -i >/dev/null 2>&1
c="$WINEPREFIX/drive_c"
fail=0

run() { # name, expected exit code, expected outcome, then agent options
    name=$1; want=$2; outcome=$3; shift 3
    rm -rf "$c/NIKKE" "$c/nyakke"
    mkdir -p "$c/NIKKE/Launcher/log/tinyget" "$c/NIKKE/NIKKE/game" "$c/nyakke"
    cp "$work/stub.exe" "$c/NIKKE/Launcher/nikke_launcher.exe"
    cp "$work/stub.exe" "$c/NIKKE/Launcher/tbs_browser.exe"
    cp "$work/stub.exe" "$c/NIKKE/NIKKE/game/nikke.exe"
    set +e
    xvfb-run -a wine "$work/app/Nyakke.exe" --agent --root 'C:\NIKKE' --status 'C:\nyakke\status.json' \
        --stop 'C:\nyakke\stop' --log 'C:\nyakke\agent.log' --evidence 'C:\nyakke\evidence' "$@" >/dev/null 2>&1
    got=$?
    set -e
    game=$(wine tasklist 2>/dev/null | grep -ci '^nikke.exe' || true)
    left=$(wine tasklist 2>/dev/null | grep -ci -e '^nikke_launcher' -e '^tbs_browser' || true)
    wineserver -k || true
    if [ "$got" = "$want" ] && grep -q "\"outcome\": \"$outcome\"" "$c/nyakke/status.json"; then
        echo "ok    $name (exit $got, $outcome, game processes $game, launcher processes left $left)"
    else
        echo "FAIL  $name: exit $got (wanted $want), wanted $outcome"; cat "$c/nyakke/agent.log" || true; fail=1
    fi
    cp "$c/nyakke/agent.log" "$work/$name.log" 2>/dev/null || true
}

STUB_GAME_AFTER=3 run closes-launcher 0 LauncherClosed --close 1 --close-delay 5
STUB_GAME_AFTER=3 STUB_IGNORE_CLOSE=1 run kills-stubborn-launcher 0 LauncherClosed --close 1 --close-delay 5
STUB_DROP_AFTER=3 run tripwire 3 Tripwire --close 1 --close-delay 5
exit $fail

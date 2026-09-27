#!/bin/sh
# Mission Planner (Turbo) launcher for Wine on Linux.
#
# Wine cannot stop the host from sleeping (its SetThreadExecutionState is a
# stub), so a laptop can idle-suspend mid-flight and drop the telemetry link.
# systemd-inhibit holds a sleep/idle lock for as long as Mission Planner runs.
# It does not prevent a forced suspend (lid close, low battery, power key).
#
# Usage: sh run-wine.sh [Mission Planner arguments]
# Uses the WINEPREFIX from your environment, like plain `wine` does.

dir=$(cd "$(dirname "$0")" && pwd) || exit 1

if command -v systemd-inhibit >/dev/null 2>&1; then
    exec systemd-inhibit --what=sleep:idle --who="Mission Planner" --why="GCS link" \
        wine "$dir/MissionPlanner.exe" "$@"
fi

echo "run-wine.sh: systemd-inhibit not found; the host may still suspend while flying." >&2
exec wine "$dir/MissionPlanner.exe" "$@"

#!/bin/sh
# What has to be true before the worker takes a class, and what has to be running beside it.
#
# Two things the image alone cannot do:
#
#   * ZAA_HEADLESS=false is the documented answer to Zoom refusing a headless browser. Xvfb was in
#     the image and nothing ever started it, and DISPLAY was never set - so the setting could not
#     work and would have failed at the first class with a browser that could not find a screen.
#
#   * The preflight belongs here, once, rather than in a HEALTHCHECK that launched a second
#     Chromium every minute inside the container holding a live class.

set -eu

if [ "${ZAA_HEADLESS:-true}" = "false" ] || [ "${ZAA_HEADLESS:-true}" = "0" ]; then
    : "${DISPLAY:=:99}"
    export DISPLAY
    echo "[entrypoint] ZAA_HEADLESS is off: starting Xvfb on ${DISPLAY}"
    # -nolisten tcp: the display is for this container and reachable from nowhere else.
    Xvfb "${DISPLAY}" -screen 0 1920x1080x24 -nolisten tcp &
    xvfb=$!

    # A browser started before the display is ready dies without saying why, so this waits for it
    # rather than sleeping a guessed number of seconds.
    ready=0
    i=0
    while [ "$i" -lt 50 ]; do
        if [ -e "/tmp/.X11-unix/X${DISPLAY#:}" ]; then ready=1; break; fi
        if ! kill -0 "$xvfb" 2>/dev/null; then
            echo "[entrypoint] Xvfb exited while starting. A display cannot be had; leave ZAA_HEADLESS at true." >&2
            exit 1
        fi
        i=$((i + 1))
        sleep 0.2
    done
    [ "$ready" = "1" ] || { echo "[entrypoint] Xvfb did not come up within ten seconds." >&2; exit 1; }
    echo "[entrypoint] Xvfb is up"
fi

# Once, before anything takes work: the clock, the volumes, /dev/shm and a browser that actually
# launches with its sandbox. A machine that fails this cannot run a class, and finding out now is
# the whole point.
if [ "${1:-run}" = "run" ]; then
    echo "[entrypoint] checking the machine"
    dotnet ZoomAutoAdmit.CloudWorker.dll preflight
fi

# exec, so the worker becomes this shell's process and tini's child: SIGTERM reaches it directly
# and a class being held is closed properly instead of killed.
exec dotnet ZoomAutoAdmit.CloudWorker.dll "$@"

#!/usr/bin/env bash
# Brings a fresh Linux box to the point where the backend, the dashboard and the two
# platform-independent .NET projects all build and their tests all run. Safe to run twice.
#
#   ./Scripts/cloud-setup.sh
#
# What it does not do: the Windows agent, the WPF window, the macOS app, Playwright against live
# Zoom. Those need their own machines and no script here can stand in for them.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m    %s\033[0m\n' "$*"; }
ok() { printf '\033[1;32m    %s\033[0m\n' "$*"; }

have() { command -v "$1" >/dev/null 2>&1; }

sudo_if_needed() {
  if [ "$(id -u)" -eq 0 ]; then "$@"; elif have sudo; then sudo "$@"; else
    warn "not root and no sudo; skipping: $*"
    return 1
  fi
}

# --------------------------------------------------------------------------- PostgreSQL
# The backend's tests build their own throwaway cluster, but only if initdb exists on the box.
# Without it the entire suite skips, which is easy to mistake for a pass.
say "PostgreSQL"
if have initdb; then
  ok "initdb already present: $(command -v initdb)"
else
  found="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1 || true)"
  if [ -n "$found" ] && [ -x "$found/initdb" ]; then
    ok "found $found"
    echo "export CENTRAL_TEST_PG_BIN=$found" >>"$root/.cloud-env"
  else
    warn "installing postgresql"
    if have apt-get; then
      sudo_if_needed env DEBIAN_FRONTEND=noninteractive apt-get update -qq || true
      sudo_if_needed env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq postgresql || \
        warn "apt-get failed; the backend tests will skip"
    elif have dnf; then
      sudo_if_needed dnf install -y -q postgresql-server || warn "dnf failed"
    else
      warn "no apt-get or dnf; install PostgreSQL by hand or the backend tests skip"
    fi
    found="$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1 || true)"
    [ -n "$found" ] && echo "export CENTRAL_TEST_PG_BIN=$found" >>"$root/.cloud-env"
  fi
fi

# CENTRAL_TEST_PG_BIN must reach the shell that runs pytest; .cloud-env is sourced by
# cloud-verify.sh and can be sourced by hand.
if [ -n "${found:-}" ]; then
  export CENTRAL_TEST_PG_BIN="$found"
  ok "CENTRAL_TEST_PG_BIN=$found"
fi

# --------------------------------------------------------------------------- Backend
say "Backend (Python)"
if ! have python3; then
  warn "python3 missing; the backend cannot be set up"
else
  python3 --version
  [ -d Backend/.venv ] || python3 -m venv Backend/.venv
  venv_py=Backend/.venv/bin/python
  [ -x "$venv_py" ] || venv_py=Backend/.venv/Scripts/python.exe
  "$venv_py" -m pip install --quiet --upgrade pip
  "$venv_py" -m pip install --quiet -e "./Backend[dev]"
  ok "Backend/.venv ready"
fi

# --------------------------------------------------------------------------- Dashboard
say "Dashboard (Node)"
if ! have npm; then
  warn "npm missing; the dashboard cannot be set up"
else
  node --version
  (cd Dashboard && npm ci --silent)
  ok "Dashboard/node_modules ready"
fi

# --------------------------------------------------------------------------- .NET
say ".NET"
if have dotnet; then
  dotnet --version
  dotnet restore Windows/src/ZoomAutoAdmit.Roster/ZoomAutoAdmit.Roster.csproj --verbosity quiet
  dotnet restore Windows/src/ZoomAutoAdmit.AttendanceMatching/ZoomAutoAdmit.AttendanceMatching.csproj --verbosity quiet
  dotnet restore Windows/tests/ZoomAutoAdmit.Roster.Tests/ZoomAutoAdmit.Roster.Tests.csproj --verbosity quiet
  dotnet restore Windows/tests/ZoomAutoAdmit.AttendanceMatching.Tests/ZoomAutoAdmit.AttendanceMatching.Tests.csproj --verbosity quiet
  ok "the two platform-independent projects are restored"
  warn "everything else under Windows/ targets net8.0-windows and cannot build here"
else
  warn "dotnet missing; skipping the .NET projects entirely"
fi

say "Done"
echo "    Next:  ./Scripts/cloud-verify.sh"

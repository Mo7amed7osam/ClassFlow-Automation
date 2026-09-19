#!/usr/bin/env bash
# Runs every check this repository can honestly run on a Linux box, and says plainly which ones
# ran, which failed, and which were skipped for want of a platform. Run Scripts/cloud-setup.sh first.
#
#   ./Scripts/cloud-verify.sh
#
# The exit code is non-zero if anything FAILED. A SKIPPED check is not a pass: read the summary.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

[ -f "$root/.cloud-env" ] && . "$root/.cloud-env"

results=()
failed=0

say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
record() { results+=("$1|$2"); [ "$1" = FAILED ] && failed=1; return 0; }

# Whether the backend tests can build their database. This looks in the same places
# Backend/tests/conftest.py does, so the script never skips a suite that would have run.
have_postgres() {
  [ -n "${CENTRAL_TEST_DATABASE_URL:-}" ] && return 0
  [ -n "${CENTRAL_TEST_PG_BIN:-}" ] && return 0
  command -v initdb >/dev/null 2>&1 && return 0
  for dir in "/usr/lib/postgresql/"*/bin "/opt/homebrew/opt/postgresql"*/bin "/c/Program Files/PostgreSQL/"*/bin; do
    if [ -x "$dir/initdb" ] || [ -x "$dir/initdb.exe" ]; then
      # Python reads this variable, so on Git Bash it needs the Windows spelling of the path.
      if command -v cygpath >/dev/null 2>&1; then
        export CENTRAL_TEST_PG_BIN="$(cygpath -w "$dir")"
      else
        export CENTRAL_TEST_PG_BIN="$dir"
      fi
      return 0
    fi
  done
  return 1
}

# --------------------------------------------------------------------------- Backend
say "Backend tests"
# Linux and macOS put it in bin/, Windows in Scripts/. Both appear here at times.
py=""
for candidate in Backend/.venv/bin/python Backend/.venv/Scripts/python.exe; do
  [ -x "$candidate" ] && py="${candidate#Backend/}" && break
done
if [ -z "$py" ]; then
  record SKIPPED "Backend tests — no virtual environment; run Scripts/cloud-setup.sh"
elif ! have_postgres; then
  record SKIPPED "Backend tests — no PostgreSQL; the suite would skip itself and exit 0, which reads as a pass"
else
  out="$(cd Backend && "$py" -m pytest -q 2>&1)"
  status=$?
  printf '%s\n' "$out" | tail -25
  # pytest exits 0 when every test skips, so the summary line decides.
  if [ $status -ne 0 ]; then
    record FAILED "Backend tests"
  elif printf '%s' "$out" | grep -qE '[0-9]+ passed'; then
    record PASSED "Backend tests — $(printf '%s' "$out" | grep -oE '[0-9]+ passed[^,]*' | tail -1)"
  else
    record SKIPPED "Backend tests — nothing actually ran (no PostgreSQL)"
  fi
fi

# --------------------------------------------------------------------------- Dashboard
say "Dashboard tests"
if [ ! -d Dashboard/node_modules ]; then
  record SKIPPED "Dashboard tests — no node_modules; run Scripts/cloud-setup.sh"
else
  (cd Dashboard && npm test --silent) && record PASSED "Dashboard tests" || record FAILED "Dashboard tests"
fi

say "Dashboard type check"
if [ ! -d Dashboard/node_modules ]; then
  record SKIPPED "Dashboard type check — no node_modules"
else
  (cd Dashboard && npm run typecheck --silent) && record PASSED "Dashboard type check" || record FAILED "Dashboard type check"
fi

# --------------------------------------------------------------------------- .NET
say ".NET tests (the two projects that build off Windows)"
if ! command -v dotnet >/dev/null 2>&1; then
  record SKIPPED ".NET tests — dotnet is not installed"
else
  for proj in Windows/tests/ZoomAutoAdmit.Roster.Tests Windows/tests/ZoomAutoAdmit.AttendanceMatching.Tests; do
    if dotnet test "$proj" --nologo --verbosity quiet; then
      record PASSED "$(basename "$proj")"
    else
      record FAILED "$(basename "$proj")"
    fi
  done
fi

# --------------------------------------------------------------------------- Out of reach here
record SKIPPED "Windows agent and WPF window — 21 projects target net8.0-windows; need Windows"
record SKIPPED "macOS app (swift test) — needs macOS"
record SKIPPED "Waiting-room admission, Zoom UI Automation, Playwright against live Zoom — need a desktop with Zoom signed in"

# --------------------------------------------------------------------------- Summary
printf '\n\033[1;36m==> Summary\033[0m\n'
for r in "${results[@]}"; do
  state="${r%%|*}"; text="${r#*|}"
  case "$state" in
    PASSED)  printf '  \033[1;32m%-8s\033[0m %s\n' "$state" "$text" ;;
    FAILED)  printf '  \033[1;31m%-8s\033[0m %s\n' "$state" "$text" ;;
    *)       printf '  \033[1;33m%-8s\033[0m %s\n' "$state" "$text" ;;
  esac
done

printf '\n'
if [ $failed -ne 0 ]; then
  printf '\033[1;31mSomething failed. Do not report this as green.\033[0m\n'
else
  printf '\033[1;32mNothing failed.\033[0m Say which parts were skipped when reporting this.\n'
fi
exit $failed

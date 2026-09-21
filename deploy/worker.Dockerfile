# The cloud worker: .NET 8 and a Chromium that Playwright drives.
#
# Build from the repository root, because the worker's project references reach up into Windows/src:
#   docker build -f deploy/worker.Dockerfile -t classflow-worker .
#
# The image carries no secret. The backend address, the enrolment token and the rest arrive as
# environment variables at run time, and the device token that replaces the enrolment one is written
# to the state volume, never into a layer.

# ---------------------------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src

# Restore before the sources are copied, so a change to a .cs file does not re-download NuGet.
COPY Windows/Directory.Build.props Windows/Directory.Build.targets Windows/
COPY Windows/src/ZoomAutoAdmit.Core/ZoomAutoAdmit.Core.csproj                         Windows/src/ZoomAutoAdmit.Core/
COPY Windows/src/ZoomAutoAdmit.Roster/ZoomAutoAdmit.Roster.csproj                     Windows/src/ZoomAutoAdmit.Roster/
COPY Windows/src/ZoomAutoAdmit.AttendanceMatching/ZoomAutoAdmit.AttendanceMatching.csproj Windows/src/ZoomAutoAdmit.AttendanceMatching/
COPY Windows/src/ZoomAutoAdmit.WebAutomation/ZoomAutoAdmit.WebAutomation.csproj       Windows/src/ZoomAutoAdmit.WebAutomation/
COPY Windows/src/ZoomAutoAdmit.CentralAgent/ZoomAutoAdmit.CentralAgent.csproj         Windows/src/ZoomAutoAdmit.CentralAgent/
COPY Windows/src/ZoomAutoAdmit.Attendance/ZoomAutoAdmit.Attendance.csproj             Windows/src/ZoomAutoAdmit.Attendance/
# Only its project file: Attendance names it for its Windows target, and restore reads every target.
# Nothing of it is built for Linux.
COPY Windows/src/ZoomAutoAdmit.UIAutomation/ZoomAutoAdmit.UIAutomation.csproj         Windows/src/ZoomAutoAdmit.UIAutomation/
COPY Windows/src/ZoomAutoAdmit.CloudWorker/ZoomAutoAdmit.CloudWorker.csproj           Windows/src/ZoomAutoAdmit.CloudWorker/
# EnableWindowsTargeting, and it is not a contradiction. The worker itself is net8.0 alone, but the
# libraries it references multi-target, and `restore` evaluates every target framework a referenced
# project declares - including net8.0-windows, which Linux refuses to restore without this. The
# build that follows still picks each library's net8.0 output; this only lets restore read past the
# Windows one rather than stopping at it.
RUN dotnet restore Windows/src/ZoomAutoAdmit.CloudWorker/ZoomAutoAdmit.CloudWorker.csproj \
      -p:EnableWindowsTargeting=true

COPY Windows/src/ZoomAutoAdmit.Core/               Windows/src/ZoomAutoAdmit.Core/
COPY Windows/src/ZoomAutoAdmit.Roster/             Windows/src/ZoomAutoAdmit.Roster/
COPY Windows/src/ZoomAutoAdmit.AttendanceMatching/ Windows/src/ZoomAutoAdmit.AttendanceMatching/
COPY Windows/src/ZoomAutoAdmit.WebAutomation/      Windows/src/ZoomAutoAdmit.WebAutomation/
COPY Windows/src/ZoomAutoAdmit.CentralAgent/       Windows/src/ZoomAutoAdmit.CentralAgent/
COPY Windows/src/ZoomAutoAdmit.Attendance/         Windows/src/ZoomAutoAdmit.Attendance/
COPY Windows/src/ZoomAutoAdmit.CloudWorker/        Windows/src/ZoomAutoAdmit.CloudWorker/

# The worker targets net8.0 alone. If a Windows-only project ever creeps into its references, this
# line fails on Linux, which is the point of keeping it net8.0 and not multi-targeted.
RUN dotnet publish Windows/src/ZoomAutoAdmit.CloudWorker/ZoomAutoAdmit.CloudWorker.csproj \
      -c Release -o /app --no-restore -p:EnableWindowsTargeting=true

# The package's MSBuild targets are meant to copy the .playwright driver - the bundled node and
# the CLI it runs - into the output. They do not here, whether the reference is direct or
# transitive, and publish leaves only playwright.ps1, a PowerShell wrapper this image cannot run.
# The driver is in the package either way, so it is taken from there: one path, and no dependence
# on which target happened to fire.
RUN set -eux; \
    driver="$(find /root/.nuget/packages/microsoft.playwright -maxdepth 2 -name .playwright -type d | head -1)"; \
    test -n "$driver"; \
    cp -r "$driver" /app/.playwright

# ---------------------------------------------------------------------------------- run
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS runtime

# tzdata, because class times are Africa/Cairo and a slim image has no zones at all.
# xvfb, for the day Zoom's web client refuses a headless browser (ZAA_HEADLESS=false).
# The rest are what Chromium itself links against; `playwright install --with-deps` adds the others.
# tini, because Chromium forks helper processes and a .NET worker as PID 1 reaps none of them:
# over a day of classes they pile up as zombies until the container runs out of process slots.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tini tzdata xvfb ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# Playwright's browsers go somewhere every user can read, not into root's home: the worker runs as
# an unprivileged user and would not find them there.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
# `dotnet publish` leaves only playwright.ps1 - a PowerShell script this image has no shell for.
# The Playwright CLI is inside the assembly, so it is driven through the app's own dependency
# graph instead: `dotnet ... --% install` is what playwright.sh would have run anyway.
# `dotnet publish` leaves playwright.ps1, a PowerShell script this image has no shell for. The CLI
# itself is in Microsoft.Playwright.dll, but it is a library: running it needs the worker's own
# runtime configuration and dependency list, which is exactly what playwright.sh would have passed.
# Driven by the node that ships with the driver, so nothing here depends on a Node installed in
# the image. --with-deps is what pulls in the libraries Chromium links against, and it needs root,
# which is why this runs before the unprivileged user is switched to below.
RUN ./.playwright/node/linux-x64/node ./.playwright/package/cli.js install --with-deps chromium \
 && chmod -R a+rX /ms-playwright

# An unprivileged user, and Chromium keeps its own sandbox: --no-sandbox is a common piece of advice
# for containers and is exactly the wrong trade for a browser that visits pages on the open web.
# The compose file gives the container SYS_ADMIN-free seccomp instead; see COOLIFY_DEPLOYMENT.md.
RUN useradd --create-home --shell /usr/sbin/nologin worker \
 && mkdir -p /var/lib/classflow \
 && chown -R worker:worker /var/lib/classflow /app
USER worker

# XDG_DATA_HOME is what .NET resolves Environment.SpecialFolder.LocalApplicationData from on Linux,
# and a great deal hangs off that one folder: the browser profile a signed-in Zoom or LMS session
# lives in, the admission ledger, the live-meeting files. Left alone it lands in the container's own
# writable layer, so every deployment signs every account out again and loses the day's ledger.
# Pointing it at the volume puts all of them somewhere that survives.
ENV ZAA_STATE_DIR=/var/lib/classflow \
    XDG_DATA_HOME=/var/lib/classflow \
    HOME=/var/lib/classflow \
    ZAA_TIMEZONE=Africa/Cairo \
    ZAA_HEADLESS=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# The state volume: the device token, the browser profiles and the journals. Losing it means
# enrolling again and signing every account in again, so it is a named volume, never a bind into the
# image.
VOLUME ["/var/lib/classflow"]

# No HEALTHCHECK. The obvious one - run the preflight - launches a whole second Chromium every
# minute inside the container that is holding a live class, competing for the memory and the
# /dev/shm that class needs. The preflight belongs at startup, and the entrypoint runs it once.
#
# Whether this worker is alive is the backend's question, and the backend already answers it: the
# agent heartbeats every 30 seconds and a device is offline after 90 without one. That is what the
# Server page reports, and it notices a hung Chromium, which a probe on the process would not.

COPY deploy/worker-entrypoint.sh /usr/local/bin/worker-entrypoint.sh

# tini as PID 1: it reaps Chromium's orphans and passes SIGTERM through to the worker, which is
# what lets a class be closed properly rather than killed.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/worker-entrypoint.sh"]
CMD ["run"]

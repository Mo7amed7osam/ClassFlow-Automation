# The central backend, serving its own API and the built dashboard.
#
#   docker build -f deploy/backend.Dockerfile -t classflow-backend .
#
# Two stages: the dashboard is built with Node and its output is copied in, so the running image has
# no Node and no node_modules. It carries no secret; everything arrives as environment variables.

# ---------------------------------------------------------------------------------- dashboard
FROM node:22-bookworm-slim AS dashboard
WORKDIR /dashboard
COPY Dashboard/package.json Dashboard/package-lock.json ./
RUN npm ci
COPY Dashboard/ ./
# `npm run build` is tsc --noEmit then vite build, so a type error stops the image being made.
RUN npm run build

# ---------------------------------------------------------------------------------- backend
FROM python:3.12-slim-bookworm AS runtime

# tzdata for Africa/Cairo; curl for the health check. No compiler: asyncpg and cryptography ship
# wheels for this platform, and a build toolchain in a running image is a liability.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata curl \
 && rm -rf /var/lib/apt/lists/*

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    TZ=Africa/Cairo

WORKDIR /app

COPY Backend/pyproject.toml Backend/requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY Backend/ ./
COPY --from=dashboard /dashboard/dist /dashboard/dist
ENV CENTRAL_DASHBOARD_DIST=/dashboard/dist

# Unprivileged, and the app owns nothing it writes to: everything durable is in PostgreSQL.
RUN useradd --create-home --shell /usr/sbin/nologin central \
 && chown -R central:central /app
USER central

EXPOSE 8000

# Asks the app, not the port: a process that is listening but cannot reach its database is not ready,
# and a restart is the right answer.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8000/health || exit 1

# --proxy-headers so the scheme the app sees is the one the browser used. Without it, and with
# CENTRAL_ENVIRONMENT=production, every request looks like plain http and is refused with 403.
# --forwarded-allow-ips must name the proxy; COOLIFY_DEPLOYMENT.md says how to find it.
CMD ["sh", "-c", "exec uvicorn central_backend.main:app --host 0.0.0.0 --port 8000 --proxy-headers --forwarded-allow-ips=\"${CENTRAL_FORWARDED_ALLOW_IPS:-127.0.0.1}\""]

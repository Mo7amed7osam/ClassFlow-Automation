#!/bin/sh
# Database schema is application state. Apply additive Alembic revisions before accepting traffic,
# so a deployment cannot run new code against yesterday's schema after a VPS restart.
set -eu

python -m central_backend.cli migrate
python -m central_backend.cli seed-production-data
exec "$@"

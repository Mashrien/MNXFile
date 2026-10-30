#!/usr/bin/env bash
set -euo pipefail
mnx_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
exec "$mnx_root/build.sh" "$@"

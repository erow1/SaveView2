#!/usr/bin/env bash
# SafeView — restart aplikacji (stop + start)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Restart SafeView ==="

"$SCRIPT_DIR/stop.sh"
echo ""
"$SCRIPT_DIR/start.sh"

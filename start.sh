#!/usr/bin/env bash
# SafeView — start aplikacji Web w tle
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

URL="${SAFEVIEW_URL:-http://localhost:5998}"
PID_FILE="$SCRIPT_DIR/.safeview.pid"
LOG_FILE="$SCRIPT_DIR/logs/safeview.out.log"

mkdir -p "$SCRIPT_DIR/logs"

if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
    echo "SafeView już działa (PID $(cat "$PID_FILE")). Użyj ./stop.sh aby zatrzymać."
    exit 1
fi

echo "Start SafeView → $URL"
nohup dotnet run --project src/SafeView.Web \
    --no-launch-profile \
    --urls "$URL" \
    > "$LOG_FILE" 2>&1 &

echo $! > "$PID_FILE"
echo "PID $(cat "$PID_FILE") — logi: $LOG_FILE"

# Poczekaj aż aplikacja przyjmuje połączenia (max ~30s)
for i in {1..30}; do
    if curl -sS -o /dev/null -w "%{http_code}" "$URL/login" 2>/dev/null | grep -qE "^(200|302)$"; then
        echo "Gotowe. Otwórz $URL"
        exit 0
    fi
    sleep 1
done

echo "Ostrzeżenie: serwer nie odpowiada po 30s — sprawdź $LOG_FILE"
exit 2

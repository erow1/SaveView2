#!/usr/bin/env bash
# SafeView — zatrzymanie aplikacji
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="$SCRIPT_DIR/.safeview.pid"

if [[ ! -f "$PID_FILE" ]]; then
    echo "Brak pliku PID ($PID_FILE). Próba znalezienia procesu po nazwie…"
    PIDS="$(pgrep -f 'dotnet.*SafeView.Web' || true)"
    if [[ -z "$PIDS" ]]; then
        echo "SafeView nie działa."
        exit 0
    fi
    echo "Znalezione PID-y: $PIDS"
    kill $PIDS
    echo "Wysłano SIGTERM."
    exit 0
fi

PID="$(cat "$PID_FILE")"

if ! kill -0 "$PID" 2>/dev/null; then
    echo "Proces $PID nie istnieje. Usuwam stary PID-file."
    rm -f "$PID_FILE"
    exit 0
fi

echo "Zatrzymuję SafeView (PID $PID)…"
kill "$PID"

# Poczekaj do 10s na graceful shutdown, potem SIGKILL
for i in {1..10}; do
    if ! kill -0 "$PID" 2>/dev/null; then
        rm -f "$PID_FILE"
        echo "Zatrzymano."
        exit 0
    fi
    sleep 1
done

echo "Proces nie reaguje — SIGKILL."
kill -9 "$PID" 2>/dev/null || true
rm -f "$PID_FILE"
echo "Zatrzymano (wymuszony)."

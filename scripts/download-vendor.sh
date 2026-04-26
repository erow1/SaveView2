#!/usr/bin/env bash
# Pobiera biblioteki JS używane przez aplikację (Cytoscape, dagre, SignalR client)
# do src/SafeView.Web/wwwroot/vendor/. Wymagane raz, po klonie repo lub upgrade wersji.
# Aplikacja MUSI działać offline na produkcji — zero odwołań do CDN.

set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)"
VENDOR="$DIR/src/SafeView.Web/wwwroot/vendor"
mkdir -p "$VENDOR"

# Piny wersji — przy upgrade zmień tutaj + zaktualizuj App.razor jeśli schema się zmieniła.
CYTOSCAPE_VERSION="3.30.2"
DAGRE_VERSION="0.8.5"
CYTOSCAPE_DAGRE_VERSION="2.5.0"
SIGNALR_VERSION="8"

echo "→ cytoscape $CYTOSCAPE_VERSION"
curl -sfL -o "$VENDOR/cytoscape.min.js" \
    "https://cdn.jsdelivr.net/npm/cytoscape@${CYTOSCAPE_VERSION}/dist/cytoscape.min.js"

echo "→ dagre $DAGRE_VERSION"
curl -sfL -o "$VENDOR/dagre.min.js" \
    "https://cdn.jsdelivr.net/npm/dagre@${DAGRE_VERSION}/dist/dagre.min.js"

echo "→ cytoscape-dagre $CYTOSCAPE_DAGRE_VERSION"
curl -sfL -o "$VENDOR/cytoscape-dagre.js" \
    "https://cdn.jsdelivr.net/npm/cytoscape-dagre@${CYTOSCAPE_DAGRE_VERSION}/cytoscape-dagre.js"

echo "→ @microsoft/signalr $SIGNALR_VERSION"
curl -sfL -o "$VENDOR/signalr.min.js" \
    "https://cdn.jsdelivr.net/npm/@microsoft/signalr@${SIGNALR_VERSION}/dist/browser/signalr.min.js"

echo ""
echo "✅ Vendor libs pobrane do $VENDOR"
ls -lh "$VENDOR"

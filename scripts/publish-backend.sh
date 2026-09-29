#!/usr/bin/env bash
# Publishes the backend as a self-contained app (no .NET install needed) into the extension's server/<rid>/ folder,
# where the extension starts it from.
#   ./scripts/publish-backend.sh              # linux-x64
#   ./scripts/publish-backend.sh osx-arm64
set -euo pipefail
RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/ai-chat-extension/server/$RID"

rm -rf "$OUT"
dotnet publish "$ROOT/Ai-Agent/Ai-Agent/Ai-Agent.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=None \
  -o "$OUT"
chmod +x "$OUT/Ai-Agent"
echo "Backend published to $OUT"

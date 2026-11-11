#!/bin/bash

PROJECT_DIR="$HOME/Library/CloudStorage/Dropbox/AAA CODEX/Codex/HomeRobot"
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

if ! cd "$PROJECT_DIR"; then
    echo "HomeRobot project directory not found: $PROJECT_DIR"
    read -r -p "Press Enter to close this window."
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo ".NET SDK was not found."
    read -r -p "Press Enter to close this window."
    exit 1
fi

if [ -f "$PROJECT_DIR/.local/esp32-tts/server.env" ]; then
    . "$PROJECT_DIR/.local/esp32-tts/server.env"
fi

echo "Starting HomeRobot at http://localhost:5080. Press Ctrl+C to stop."
dotnet run --project src/HomeRobot.Server -- --urls "${HOMEROBOT_URLS:-http://localhost:5080}"
result=$?
if [ "$result" -ne 0 ]; then
    echo "HomeRobot stopped with exit code $result."
    read -r -p "Press Enter to close this window."
fi
exit "$result"

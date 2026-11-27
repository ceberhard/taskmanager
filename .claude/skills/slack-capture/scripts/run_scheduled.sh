#!/bin/bash
# Unattended Slack capture, run by Windows Task Scheduler. Appends each run's report to a log
# next to the app's key, so the results can be read from Windows.
LOCALAPPDATA_WIN=$(powershell.exe -NoProfile -Command '[Environment]::GetFolderPath("LocalApplicationData")' | tr -d '\r')
LOG_DIR="$(wslpath -u "$LOCALAPPDATA_WIN")/TaskOrganizer"
mkdir -p "$LOG_DIR"
LOG="$LOG_DIR/slack-capture.log"

cd /mnt/c/gitrepos/taskmanager || { echo "$(date -Is) repository not found" >> "$LOG"; exit 1; }
{
    echo "=== $(date -Is)"
    claude -p '/slack-capture scheduled' --model haiku
    echo "--- exit $?"
} >> "$LOG" 2>&1

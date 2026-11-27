"""Sends one Slack message to the Task Organizer app as a task.

Reads exactly three lines on standard input, so nothing in them is ever interpreted by a shell:
    1. the message key, "<channel id>/<message ts>", exactly as Slack search gives them
    2. the permalink, as Slack search gives it (Markdown and escaped slashes are cleaned here)
    3. the task title, one line

Prints one line: "created", "already a task" or "error", a tab, then the title the app holds
(or the reason). Exits 0 for created or already a task, 1 for anything else. Never prints the key.
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def windows_to_wsl(path: str) -> str:
    if re.match(r"^[A-Za-z]:[\\/]", path):
        return subprocess.run(["wslpath", "-u", path], capture_output=True, text=True, check=True).stdout.strip()
    return path


def default_key_file() -> str:
    local_app_data = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command", '[Environment]::GetFolderPath("LocalApplicationData")'],
        capture_output=True, text=True, check=True).stdout.strip()
    return str(Path(windows_to_wsl(local_app_data)) / "TaskOrganizer" / "local-api-key.txt")


def clean_permalink(raw: str) -> str:
    text = raw.strip().replace("\\/", "/")
    markdown = re.search(r"\((https?://[^)\s]+)\)", text)
    return markdown.group(1) if markdown else text


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", default="http://localhost:5142")
    parser.add_argument("--key-file", default=None, help="Windows or WSL path; default is the app's own key file")
    args = parser.parse_args()

    lines = sys.stdin.read().splitlines()
    if len(lines) < 3:
        print("error\texpected three lines: key, permalink, title")
        return 1

    key, permalink, title = lines[0].strip(), clean_permalink(lines[1]), lines[2].strip()
    if not re.fullmatch(r"[A-Z0-9]+/\d+\.\d+", key):
        print(f"error\tkey '{key}' is not '<channel id>/<message ts>'")
        return 1
    if not permalink.startswith("https://"):
        print(f"error\tpermalink '{permalink}' is not an https link")
        return 1
    if not title:
        print("error\ttitle is empty")
        return 1

    key_file = Path(windows_to_wsl(args.key_file)) if args.key_file else Path(default_key_file())
    if not key_file.is_file():
        print(f"error\tno key file at {key_file}: start the app once, or check --key-file")
        return 1
    local_key = key_file.read_text().strip()

    body = json.dumps({"source": "slack", "key": key, "title": title, "url": permalink})

    # The key goes to curl on standard input ("-H @-"), never as a command-line argument, which
    # other programs can read. The body goes through a short-lived file next to the key file.
    body_file = key_file.parent / f"slack-capture-{os.getpid()}.json"
    body_file.write_text(body, encoding="utf-8")
    try:
        body_file_windows = subprocess.run(["wslpath", "-w", str(body_file)], capture_output=True, text=True, check=True).stdout.strip()
        # Windows curl.exe: from WSL, Linux tools cannot reach the app on Windows localhost.
        result = subprocess.run(
            ["curl.exe", "-s", "-w", "\n%{http_code}", "-X", "POST", args.app.rstrip("/") + "/api/external-tasks",
             "-H", "Content-Type: application/json", "-H", "@-", "--data-binary", "@" + body_file_windows],
            input="X-TaskOrganizer-Key: " + local_key + "\n", capture_output=True, text=True)
    finally:
        body_file.unlink(missing_ok=True)
    response, _, status = result.stdout.rpartition("\n")
    status = status.strip()
    if result.returncode != 0 or status == "000":
        print(f"error\tthe app is not answering at {args.app}: is it running?")
        return 1

    try:
        parsed = json.loads(response) if response else {}
    except json.JSONDecodeError:
        parsed = {}
    if status in ("200", "201"):
        print(("created" if status == "201" else "already a task") + "\t" + parsed.get("title", title))
        return 0

    reason = parsed.get("detail") or parsed.get("title") or response[:200]
    print(f"error\t{status}: {reason}")
    return 1


if __name__ == "__main__":
    sys.exit(main())

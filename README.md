# Task Organizer

A personal, single-user task organizer that runs on localhost. ASP.NET Core Razor Pages (.NET 10) with a SQLite database.

## Run

From the repo root (in WSL use `dotnet.exe`; there is no Linux SDK on this machine):

```
dotnet.exe run --project src/TaskOrganizer.Web
```

Then open http://localhost:5142. The database is created on first run at `src/TaskOrganizer.Web/App_Data/taskorganizer.db` and upgraded by migrations on every start.

To try things out without touching real data, point the app at a scratch database:

```
dotnet.exe run --project src/TaskOrganizer.Web -- --Database:Path=../../tmp-verify/scratch.db
```

## Build and test

```
dotnet.exe build
dotnet.exe test
```

`dotnet.exe test` runs two projects:

- `tests/TaskOrganizer.Tests` – fast tests of the rules and pages, no browser. Run it alone with `dotnet.exe test tests/TaskOrganizer.Tests`.
- `tests/TaskOrganizer.BrowserTests` – Playwright tests that start the real app on a free local port and drive it in a real browser (about 20 seconds). They use the Edge already installed on this machine, so there is nothing to download.
  - Use Chrome instead: set `TASKORGANIZER_BROWSER_CHANNEL=chrome`.
  - Watch them run: set `TASKORGANIZER_BROWSER_HEADED=1`.
  - Each test gets its own empty database and a fixed clock (19 November 2026, 10:00), so they never touch real data.

## Using it

- **Interruption** (beside Add, or `Alt+I`) uses whatever is typed in the quick-add fields; with nothing typed it logs "Interruption at HH:mm" in Today.
- **Quick-add** is at the top of every page. Press `/` to jump to it, type a title, press Enter. Project, priority, expected delivery date, size in hours and "today" are optional. If it is rejected, the reason appears under the box and what you typed is kept.
- **Task states.** A task is To do, In progress, On hold, Done for today, Done or Dropped.
  - **In progress** rows are blue with a badge. Today shows how many are in progress at once: 1 to 3 OK, 4 to 5 Warning, 6 to 7 Too many, 8 or more Danger.
  - **On hold** parks a task with a reason in your own words (under "Edit, hold, more" on any task), with an optional check-back date. It is left out of planned hours and suggestions. When the check-back day arrives it appears under "Follow up" at the top of Today and of Suggest tasks. Saved reasons are offered as you type and are managed in Settings.
  - **Done for today** (the main button on Today) keeps the task on show in green. From the next day it appears under "Finished earlier, confirm" with three choices: Done, Re-open for today, or Backlog. Signing it off dates the completion to when you marked it.
  - **Done** closes a task as finished. **Drop** closes it without doing it; only Done counts as delivered work. Both can be reversed from "Closed in the last 14 days" on the Backlog.
  - **Delete** (under "Edit, hold, more", after ticking the confirmation) removes a task and its history for good. It is for tasks created by mistake.
  - **History** (same place) lists every status change with its reason, and every change of size, priority and planned day.
- **Today** counts a task with no size at the assumed size, the same as a suggested day does.
- **Today** starts with a time section (start, stop, log or correct today's time), then what is planned for today, the planned hours against your daily target, and anything carried over from earlier days.
- **Suggest tasks** (button on Today) plans a day in three steps: tick today's focus projects and set the hours available, read the proposal and the reason given for each task, then untick or swap in tasks and press "Plan these for today". Nothing is planned until that button is pressed. The rules, in order:
  - Always included, most pressing first: Critical, overdue, due today, in progress, carried over. If these alone go past the hours available, nothing else is proposed and the ones that do not fit are marked.
  - One Low task when it fits, picked at random each day and unchanged for the rest of that day.
  - Very High tasks, from any project.
  - The rest of the day is split between focus projects and everything else (70/30 by default), best first; a task due within 3 working days ranks one priority level higher.
  - A task with no size counts as 1 hour and is marked "assumed".
  - Focus share, assumed size and the due-soon window are in Settings.
  - Step 3 lists the left-out tasks by project, focus projects first, with a title filter.
  - Accepting a proposal records the day's hours available and focus projects. Today then measures against those hours, and asking for another suggestion the same day starts from the same choices.
- **Backlog** lists open tasks by project, with a count of open tasks at each priority.
  - Filter by project and priority; the filter is part of the address, so a view can be bookmarked.
  - Collapse or expand projects; the browser remembers which are collapsed.
  - Filter by status as well (To do, In progress, On hold, Done for today).
  - In edit mode each project has a "Set for all" control that fills priority and/or date into every row of that project; adjust rows, then Save.
  - "Edit priorities, dates and hours" turns every row into inputs with one Save. Ctrl+Up / Ctrl+Down moves down a column. If any row is invalid, nothing is saved.
- While the clock is running, the time figures on every page update once a minute without reloading.
- **Time entries** can be flagged as Work (the default), PTO, Holiday or Sick with the picker on each entry; non-work entries are coloured, count toward the daily and weekly totals, and are listed under the weekly total. Entries may overlap (odd hours, or working on a day off); each counts in full. Only one entry can be running at a time.
- **Time** is the weekly clock-in/out log with the weekly goal, remaining hours and rollover balance.
- **Settings** holds the daily target, weekly goal, opening rollover balance, projects, and the imports from `refs/Time_Log.xlsx`: the "Daily Punchdown" task list and the start/end times on the "Overall" sheet. The time import skips and lists rows with no end time and rows in the undated weekend block, and offers the sheet's Rollover figure as your opening balance.

## Integrations

All three only read from the outside service; nothing is ever changed in Jira, Azure DevOps or Slack.

- **Jira and pull requests.** Type one or more Jira keys or pull request numbers (or paste their links) in the "Jira key or PR number" box under quick-add, on any page.
  - A Jira ticket becomes `ROC-4698: <summary>` with the ticket's due date, filed under the project its prefix is mapped to (Settings, "Ticket prefix to project"; unmapped goes to Unfiled).
  - A pull request becomes `PR 26512 - <author first name> - <title>`, 0.5 h, High, under the project chosen in Settings.
  - The same ticket or pull request is never added twice; each task links back to it.
  - Set-up, in Settings under Integrations: the Jira site address (https://yourcompany.atlassian.net), your Jira email and an API token from id.atlassian.com (Security, API tokens); the Azure DevOps organisation address (https://dev.azure.com/yourorganisation) and a personal access token with the Code (Read) scope only. "Test connection" checks each.
  - Tokens are encrypted for your Windows user and stored in `%LOCALAPPDATA%\TaskOrganizer\credentials.json`, never in the database or the repository, and never shown again.
- **Slack.** React to a message with :mag_right: and run `/slack-capture` in Claude Code from this repository. Each reacted message becomes one task linked to the message. Run interactively, it also suggests requests from recent DMs and mentions for you to pick from. It also runs by itself every hour from 04:00 to 18:00 on weekdays, on Haiku (Windows Task Scheduler, "TaskOrganizer Slack capture"), logging to `%LOCALAPPDATA%\TaskOrganizer\slack-capture.log`. See `.claude/skills/slack-capture/SKILL.md`.
  - The skill adds tasks through `POST /api/external-tasks` on the running app, using the key the app writes to `%LOCALAPPDATA%\TaskOrganizer\local-api-key.txt` at startup. Web pages cannot call it.

## Changing the schema

Never delete the database to get a change through. Add a migration (command below). On start, if a migration is pending, the app first copies the database to `App_Data/backups/` and keeps the newest 10 copies.

```
dotnet.exe dotnet-ef migrations add <Name> --project src/TaskOrganizer.Web -o Data/Migrations
```

## Layout

- `src/TaskOrganizer.Web/Domain` – entities and enums
- `src/TaskOrganizer.Web/Services` – all business rules; the pages only call these, so an API can be added over the same services later
- `src/TaskOrganizer.Web/Pages` – Razor Pages
- `tests/TaskOrganizer.BrowserTests` – end-to-end browser tests (Playwright for .NET); reuses the app host from `tests/TaskOrganizer.Tests`
- `tests/TaskOrganizer.Tests` – tests for the services, run against an in-memory SQLite database built by the real migrations, plus page tests (`PageTests.cs`) that start the real app against a throwaway database file

## Notes

- `nuget.config` restricts restores to nuget.org so they do not depend on private feeds.
- `.gitignore` excludes the database and `refs/`, which contain real work data.
- When checking the app by hand from WSL, stop it by its Windows process ID (`taskkill.exe /PID <id> /F`). Stopping the WSL wrapper leaves the Windows process running and locks the build output.

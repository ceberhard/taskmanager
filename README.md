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

- **Quick-add** is at the top of every page. Press `/` to jump to it, type a title, press Enter. Project, priority, expected delivery date, size in hours and "today" are optional. If it is rejected, the reason appears under the box and what you typed is kept.
- **Interruption** (or `Alt+I`) logs an interruption now and puts it in Today. Rename it afterwards.
- **Today** starts with a time section (start, stop, log or correct today's time), then what is planned for today, the planned hours against your daily target, and anything carried over from earlier days.
- **Backlog** lists open tasks by project, with a count of open tasks at each priority.
  - Filter by project and priority; the filter is part of the address, so a view can be bookmarked.
  - Collapse or expand projects; the browser remembers which are collapsed.
  - "Edit priorities, dates and hours" turns every row into inputs with one Save. Ctrl+Up / Ctrl+Down moves down a column. If any row is invalid, nothing is saved.
- **Time** is the weekly clock-in/out log with the weekly goal, remaining hours and rollover balance.
- **Settings** holds the daily target, weekly goal, opening rollover balance, projects, and the imports from `refs/Time_Log.xlsx`: the "Daily Punchdown" task list and the start/end times on the "Overall" sheet. The time import skips and lists rows with no end time and rows in the undated weekend block, and offers the sheet's Rollover figure as your opening balance.

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

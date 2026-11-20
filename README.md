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

## Using it

- **Quick-add** is at the top of every page. Press `/` to jump to it, type a title, press Enter. Project, priority, expected delivery date, size in hours and "today" are optional.
- **Interruption** (or `Alt+I`) logs an interruption now and puts it in Today. Rename it afterwards.
- **Today** shows what is planned for today, the planned hours against your daily target, and anything carried over from earlier days.
- **Backlog** lists open tasks by project, with a count of open tasks at each priority.
- **Time** is the weekly clock-in/out log with the weekly goal, remaining hours and rollover balance.
- **Settings** holds the daily target, weekly goal, opening rollover balance, projects, and the one-time import of the "Daily Punchdown" sheet from `refs/Time_Log.xlsx`.

## Changing the schema

Never delete the database to get a change through. Add a migration (command below). On start, if a migration is pending, the app first copies the database to `App_Data/backups/` and keeps the newest 10 copies.

```
dotnet.exe dotnet-ef migrations add <Name> --project src/TaskOrganizer.Web -o Data/Migrations
```

## Layout

- `src/TaskOrganizer.Web/Domain` – entities and enums
- `src/TaskOrganizer.Web/Services` – all business rules; the pages only call these, so an API can be added over the same services later
- `src/TaskOrganizer.Web/Pages` – Razor Pages
- `tests/TaskOrganizer.Tests` – tests for the services, run against an in-memory SQLite database built by the real migrations

## Notes

- `nuget.config` restricts restores to nuget.org so they do not depend on private feeds.
- `.gitignore` excludes the database and `refs/`, which contain real work data.

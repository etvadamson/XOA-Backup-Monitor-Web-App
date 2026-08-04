# XOA Backup Monitor (Web App)

A clean **web-based** rewrite of [XOABackupMonitor](https://github.com/etvadamson/XOABackupMonitor).
This repo contains **only** the web application - no WPF/XAML, no Windows-only
dependencies, no desktop tray-icon code. Everything here runs as a normal
ASP.NET Core web app on Linux, Windows, or in Docker.

## What was carried over from the desktop app

- **XOA REST API polling logic** (`Services/XoaApiService.cs`) - ported from
  `Services/XOAApiService.cs` in the desktop repo, including the VM to host
  mapping, backup-log matching, and the "canceled job" detection fix
  (XOA reports a canceled backup as `status: success` at the job level, so we
  walk the task/sub-task `message` chain to catch cancellations and report
  them as **Warning** instead of a false **Success**).
- **Status model** (`Models/BackupStatus.cs`, `Models/VMBackupStatus.cs`,
  `Models/XOAInstance.cs`) - same status categories (Success / Warning /
  Failed / Error / Unknown) and VM fields as the desktop app.
- **Multi-instance configuration** - same idea as the desktop `ConfigViewModel`
  / `ConfigurationManager`: each XOA instance has a name, URL, API token,
  and enabled flag.
- **Local caching** - same idea as `CacheManager`: last-known results are
  cached to disk so the dashboard has data immediately on startup instead of
  waiting for the first poll.
- **CSV export** - same idea as `MainViewModel.ExportToCsv`.

## What was intentionally NOT carried over

- WPF windows, XAML, system tray icon, `System.Windows.Forms` - all
  Windows-desktop-only and the source of the "web conversion" issues.
- The hardcoded encryption password in the old `ConfigurationManager`
  (`"XOABackupMonitor_SecureKey_2026_ChangeThis!"`) and its **static IV**
  (derived from MD5 of that same password) - this is a real crypto weakness
  (a static IV means identical plaintext always produces identical
  ciphertext). The web app instead generates a random 256-bit key on first
  run (stored in `data/encryption.key`, gitignored) and uses a **random IV
  per encryption**, stored alongside the ciphertext.
- The shared, mutable `CookieContainer` on a singleton `HttpClient` - in the
  desktop app this worked because instances were polled sequentially. The
  web app polls instances **concurrently**, so cookies are now set per
  request via an explicit `Cookie` header instead of a shared container,
  avoiding cross-instance/thread state issues.

## Project layout

```
XOABackupMonitorWeb.csproj
Program.cs                        # minimal API endpoints + startup
Models/
  BackupStatus.cs
  VMBackupStatus.cs
  XOAInstance.cs
Services/
  XoaApiService.cs                # talks to the XOA REST API
  ConfigService.cs                # instance config + token encryption
  CacheService.cs                 # disk cache of last known results
  MonitorEngine.cs                # orchestrates refresh + in-memory state
  RefreshBackgroundService.cs     # periodic background polling
wwwroot/
  index.html                      # dashboard UI
  app.js
  styles.css
Dockerfile
docker-compose.yml
```

## Running it locally (for testing)

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet restore
dotnet run
```

Then open **http://localhost:5000** (or whatever port the console prints).

On first launch there are no XOA instances configured - use the **Configure**
button in the dashboard (or `POST /api/instances`, see below) to add one,
then click **Refresh Now**.

## Running with Docker

```bash
docker compose up --build
```

This maps port `8080` and persists `./data` (config, cache, encryption key)
as a volume, so your instances and cache survive container restarts.

## API reference (used by the dashboard, also usable directly)

| Method | Path                          | Description                                   |
|--------|-------------------------------|------------------------------------------------|
| GET    | `/api/status`                 | Current grouped backup status (from memory)    |
| POST   | `/api/refresh`                | Force refresh of all enabled instances          |
| POST   | `/api/refresh/{instanceName}` | Force refresh of a single instance              |
| GET    | `/api/instances`               | List configured instances (token not returned) |
| POST   | `/api/instances`               | Add or update an instance                       |
| DELETE | `/api/instances/{name}`       | Remove an instance                              |
| POST   | `/api/instances/{name}/test`  | Test connection for an instance                 |
| GET    | `/api/settings`                | Get global refresh interval (minutes)           |
| POST   | `/api/settings`                | Set global refresh interval (minutes)           |
| GET    | `/api/export/csv`             | Download current status as CSV                  |
| GET    | `/api/health`                 | Health check                                    |

Example: add an instance

```bash
curl -X POST http://localhost:5000/api/instances \
  -H "Content-Type: application/json" \
  -d '{"name":"Primary XOA","url":"https://xoa.example.com","apiToken":"YOUR_TOKEN","isEnabled":true}'
```

## Notes on the XOA API token

Generate an API token in Xen Orchestra under **Settings -> Users**. The web
app sends it as the `authenticationToken` cookie on REST calls, exactly like
the desktop app did.

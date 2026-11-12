# SafeClass Monitor

IoT-based silent emergency alert and classroom status monitoring for schools: an ASP.NET Core API with a SQLite
database, a SignalR live channel, and the browser dashboard that was prototyped earlier.

> **Status: written but not yet compiled.** The environment this code was produced in had no .NET SDK and no access to
> NuGet, so nothing here has been built or run, EF Core migrations have not been generated, and package versions are
> unverified. Follow **First run** below; expect to fix a handful of compile errors or version bumps on the first build.

```
SafeClass.sln
src/
  SafeClass.Domain/          entities + enums (no dependencies)
  SafeClass.Application/     DTOs, interfaces, validation, business rules (services)
  SafeClass.Infrastructure/  EF Core DbContext, configurations, migrations, seeding, bootstrap
  SafeClass.Api/             controllers, SignalR hub, auth, middleware, background services, Program.cs
frontend/index.html          dashboard (single file, talks to the API)
tests/SafeClass.UnitTests/         service tests on real SQLite (in-memory)
tests/SafeClass.IntegrationTests/  full HTTP tests against a temporary SQLite file
prototype/                   the original mock-data prototype (kept for reference)
```

## Requirements

.NET 10 SDK and the EF Core CLI: `dotnet tool install --global dotnet-ef`

## First run

```bash
dotnet restore
dotnet build

# Generate the first migration (none is committed yet). Until you do, the app falls back to EnsureCreated.
dotnet ef migrations add InitialCreate -p src/SafeClass.Infrastructure -s src/SafeClass.Api -o Migrations

dotnet test                                   # unit + integration tests
dotnet dev-certs https --trust                # once, for local HTTPS
dotnet run --project src/SafeClass.Api --launch-profile https
```

Open `https://localhost:7080`. In Development the app seeds **clearly marked demo data** (school `DEMO-001`) and enables
demo mode. Demo sign-ins use the password from `appsettings.Development.json` (`Seed:DemoPassword`):

| Account | Role |
|---|---|
| `admin@demo.safeclass.local` | Administrator |
| `maria.reyes@demo.safeclass.local` | Safety Coordinator |
| `teacher@demo.safeclass.local` | Teacher |
| `viewer@demo.safeclass.local` | Viewer (read-only) |

API docs: `https://localhost:7080/swagger` (Development only; OpenAPI JSON at `/openapi/v1.json`). Sign in with
`POST /api/v1/auth/login`, paste the token into the box on the Swagger page.

The frontend can also be opened from a different origin as `index.html?api=https://host:7080` (add that origin to `Cors:AllowedOrigins`).

## Database

* SQLite file, default `Data/safeclass.db` (`Database:Path`) or any `Database:ConnectionString`. Foreign keys are enforced
  on every connection; the file runs in WAL mode; all timestamps are UTC.
* EF Core code-first. Apply migrations by starting the app (it migrates on startup) or `dotnet ef database update`.
* Incident history (alerts, alert events, status history, activity, audit) is **never deleted automatically**. Only
  heartbeat observations are purged after `SafeClass:HeartbeatHistoryRetentionDays` (default 30; 0 = keep).
* Classrooms are soft-deleted so past incidents stay reportable.
* The SQLite file lives in `Data/`, which is not served by the static file host and is git-ignored. Never place it under `wwwroot`/`frontend`.

### Backups

Use SQLite's online backup, not a plain file copy (WAL mode keeps recent writes in the `-wal` file):

```bash
sqlite3 Data/safeclass.db ".backup 'backups/safeclass-$(date +%Y%m%d-%H%M).db'"
# or:  sqlite3 Data/safeclass.db "VACUUM INTO 'backups/safeclass-snapshot.db'"
```

Schedule it (cron / Task Scheduler), keep copies off the server, and test a restore: stop the app, replace
`safeclass.db`, delete any stale `safeclass.db-wal` / `-shm`, start the app.

## Configuration (environment variables use `__`)

| Setting | Purpose |
|---|---|
| `Jwt__Key` | **Required.** 32+ random characters. Never commit it. |
| `Database__Path` / `Database__ConnectionString` | Database location |
| `Cors__AllowedOrigins__0` | Browser origins allowed to call the API cross-origin |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` (12+ chars), `Bootstrap__SchoolName`, `Bootstrap__SchoolCode` | Creates the first school and administrator on an empty database. No sample data. |
| `SafeClass__HeartbeatOfflineThresholdSeconds` | Silence before a device is marked offline (60) |
| `SafeClass__DemoModeEnabled` | Demo buttons + demo heartbeats. **Keep false in production.** |
| `Seed__Enabled`, `Seed__DemoPassword`, `Seed__DemoDeviceKey` | Demo fixtures (Development / demo only) |
| `RateLimiting__LoginPerMinute`, `RateLimiting__DevicePerMinute` | Rate limits |
| `Docs__Enabled` | Expose Swagger outside Development |

Production checklist: HTTPS terminated by the app or a proxy you configure (HSTS + redirect are on outside Development),
`Jwt__Key` from a secret store, bootstrap admin password changed/stored safely, `DemoModeEnabled=false`, `Seed__Enabled=false`,
CORS limited to your origin, SQLite file on a protected path with regular backups. No production credential lives in the repository;
the values in `appsettings.Development.json` are throwaway local ones.

## Security model

* JWT sign-in (PBKDF2-SHA256 password hashes, generic login errors, rate limited). Roles: Administrator, SafetyCoordinator, SecurityStaff, Teacher, Viewer.
* The school comes from the token, never from the client. Every query is scoped to it; another school's ids return 404, and an explicit `schoolId` for another school returns 403.
* Devices authenticate with `X-Device-Identifier` + `X-Device-Key`. Only a SHA-256 hash is stored; the key is shown once (register / rotate) and can be revoked.
* Validation on every request, RFC 7807 errors with no stack traces, audit log for sign-ins and administrative actions, CSV export neutralises spreadsheet formulas.

| Action | Roles |
|---|---|
| View classrooms, alerts, devices, dashboard | all signed-in users |
| Create alert from the app | Administrator, SafetyCoordinator, SecurityStaff, Teacher |
| Acknowledge / resolve | Administrator, SafetyCoordinator, SecurityStaff |
| Classroom create / edit / delete, demo controls | Administrator, SafetyCoordinator |
| School settings, register / rotate / revoke devices | Administrator |
| Reports and exports | all except Teacher |

## Business rules (all in `SafeClass.Application`, not the UI)

* Room status precedence: **Emergency** (any unresolved alert) > **Offline** (no online device) > **Normal**.
* Acknowledging never changes room status. Resolving returns a room to Normal only if no other alert is open and its device is online (otherwise Offline).
* Resolving an Active alert records an implicit acknowledgement so response-time reports stay complete.
* Each state change is one database transaction; live notifications are sent after commit and never roll it back.
* Offline detection is a background monitor; one offline transition per outage, not one per missed heartbeat.

## ESP32 integration

```bash
# heartbeat (every ~20 s)
curl -k -X POST https://localhost:7080/api/v1/devices/heartbeat \
  -H "X-Device-Identifier: ESP32-A101" -H "X-Device-Key: <key>" \
  -H "Content-Type: application/json" -d '{"signalStrength":3}'

# button press: send a unique eventId per press; retries return 200 with the same alert, new alerts return 201
curl -k -X POST https://localhost:7080/api/v1/devices/alerts \
  -H "X-Device-Identifier: ESP32-A101" -H "X-Device-Key: <key>" \
  -H "Content-Type: application/json" -d '{"alertType":"Silent Panic Button","eventId":"A101-000123"}'
```

## Live updates

Hub `/hubs/monitoring` (JWT via `access_token` query string). Events: `ClassroomStatusChanged`, `EmergencyAlertCreated`,
`EmergencyAlertAcknowledged`, `EmergencyAlertResolved`, `DeviceConnectionChanged`. Clients join their school's group
automatically. The frontend reconnects automatically, fetches fresh data after every reconnect, and falls back to 15 s
polling if SignalR cannot load.

## API summary (`/api/v1`)

`POST auth/login` · `GET config` · `GET dashboard/summary|activity` · `GET schools/current`, `PUT schools/{id}` ·
`classrooms` (CRUD, `{id}/history`, `{id}/alerts`; filters `status building floor search`, `page`, `pageSize`) ·
`alerts` (list, get, create, `{id}/acknowledge|resolve|history`) ·
`devices` (list, get, register, `{id}/credentials/rotate|revoke`, `{id}/alerts`, `{id}/history`, `heartbeat`, `alerts`) ·
`users/me`, `users/me/preferences` ·
`reports/alerts|alerts/by-classroom|alerts/by-type|response-times|classroom-history|device-uptime|dashboard`, `reports/export/alerts?format=csv|json` ·
`demo/simulate-alert`, `demo/reset` (demo mode only; reset removes only simulator-created alerts).
Report filters: `startDate`, `endDate`, `classroomId`, `building`, `alertType`, `status`.

## Known gaps

Not compiled or run yet (see top). No password-change or user-management endpoints. The "Add classroom" button in Settings
is still disabled in the UI (the API supports it). Signal strength is a 0–4 level mapped by the firmware.

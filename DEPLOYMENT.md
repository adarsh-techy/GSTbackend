# GSTAutoPilot — Deployment Guide (Windows Server, self-contained)

This package is a **self-contained Windows x64 build**. The API and the React
web app ship together in one folder and run as **one process** — the API serves
the web UI and the `/api` endpoints from the same origin. **No .NET runtime needs
to be installed on the server** (it is bundled).

---

## 1. Package contents

```
GSTAutoPilot.API.exe        ← the application (run this)
appsettings.json            ← config — SECRET-FREE; supply secrets via env vars (§4)
appsettings.Production.json ← production overrides (also secret-free)
web.config                  ← only used if hosting under IIS
wwwroot\                    ← the web UI (served at "/") + uploaded logos
*.dll                       ← bundled .NET runtime + libraries
DEPLOYMENT.md               ← this file
```

## 2. Prerequisites on the VPS

- Windows Server x64 (no .NET install required).
- **Network access from the VPS to the SQL Server(s):** the GSTAutoPilot master
  DB, the per-tenant app DBs, and each client's CarolERP DB. Confirm the server
  can reach the SQL host/port before first run.
- A way to serve **HTTPS** (recommended): IIS or a reverse proxy (nginx) with a
  TLS certificate in front of the app. The app itself can run plain HTTP on an
  internal port behind that proxy.

## 3. Quick start (smoke test)

From an elevated PowerShell **in the package folder** (the app needs at least the
master DB connection and a JWT key — see §4):

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:ConnectionStrings__MasterConnection = "Data Source=<host>;Initial Catalog=GSTAutoPilot_Master;User ID=<user>;Password=<pwd>;TrustServerCertificate=True"
$env:Jwt__Key = "<any-long-random-string>"
.\GSTAutoPilot.API.exe
```

Open `http://localhost:5000/` — the login screen should load. Press Ctrl+C to stop.
(This is just to confirm it runs; use a Windows Service or IIS for the real
deployment — see §5.)

## 4. Configuration & secrets

**This package ships with NO secrets** — the bundled `appsettings.json` and
`appsettings.Production.json` have every secret blank. You **must** supply them as
**environment variables** on the server. (Env vars override the JSON; a config
key's `:` becomes `__`, double underscore.)

The app runs in the **Production** environment by default (loads the secret-free
`appsettings.Production.json`). Leave `ASPNETCORE_ENVIRONMENT` unset or set it to
`Production`.

**Required — the app will not start without these:**

| Setting | Environment variable |
|---|---|
| Master DB connection | `ConnectionStrings__MasterConnection` |
| JWT signing key — any long random string; keep it **stable** (changing it invalidates existing logins) | `Jwt__Key` |

**Common optional:**

| Setting | Environment variable |
|---|---|
| Listen URL(s) | `ASPNETCORE_URLS` (e.g. `http://localhost:5000`) |
| HTTPS redirect on/off | `Hosting__EnableHttpsRedirection` |
| **Advisor — enable** | `Advisor__Enabled` = `true` |
| **Advisor — API key** | `Advisor__ApiKey` = `sk-ant-…` |
| GSP creds for e-Invoice / GST / e-Way Bill (only if testing those) | `WhiteBooksEInvoice__Sandbox__ClientId`, `…__ClientSecret`, `WhiteBooksGst__ClientId`, etc. |

> The per-tenant app DBs and each client's CarolERP DB connection strings are
> stored in the **master DB** (per tenant), not in config — so only the master
> connection above is needed here.

### GST Advisor (enabled for this build)

The AI advisor is **requested ON** for this deployment. It needs an Anthropic API
key set on the server (the key is **not** in this package — set it as an env var):

```powershell
# Machine-level so a Windows Service picks it up (run elevated):
setx /M Advisor__Enabled "true"
setx /M Advisor__ApiKey  "sk-ant-…the key…"
```

After setting machine env vars, restart the service (or the server) so they take
effect. The advisor is billed per use; to switch it off later set
`Advisor__Enabled` to `false` and restart — no re-deploy needed.

## 5. Running it for real

### Option A — Windows Service (recommended)

The app integrates with the Windows Service Control Manager. Set any secret/URL
env vars at **machine** level first (`setx /M …`, see §4), then:

```powershell
# Create the service (run elevated). Note the space after binPath= and the quotes.
sc.exe create GSTAutoPilot binPath= "\"C:\path\to\GSTAutoPilot.API.exe\"" start= auto
sc.exe description GSTAutoPilot "GSTAutoPilot GST filing application"
# Set the listen URL for the service via machine env (if not already):
setx /M ASPNETCORE_URLS "http://localhost:5000"
sc.exe start GSTAutoPilot
```

Then put IIS or nginx in front for HTTPS, reverse-proxying 443 → `http://localhost:5000`.
Logs go to the Windows Event Log; to also write a console window for debugging,
run the exe directly instead (Option, §3).

To update later: `sc.exe stop GSTAutoPilot` → replace the files → `sc.exe start GSTAutoPilot`.

### Option B — IIS

Requires the **ASP.NET Core Hosting Bundle** installed in IIS (it provides the
IIS↔Kestrel module; the .NET runtime itself is still bundled in this package).
Point an IIS site at this folder; the included `web.config` launches
`GSTAutoPilot.API.exe`. Bind an HTTPS certificate on the IIS site.

## 6. TLS / reverse-proxy notes

- The app honours `X-Forwarded-Proto` / `X-Forwarded-For`, so HTTPS is detected
  correctly behind a TLS-terminating proxy.
- If the app runs **HTTP-only behind a proxy** and you see redirect loops, set
  `Hosting__EnableHttpsRedirection=false` and restart.

## 7. First login & tenants

- The UI opens to a login screen. Use the application logins provided separately
  by the GSTAutoPilot team (not in this package).
- The build's **default tenant** is pre-set; testers can switch tenant/company
  using the selectors at the top-left of the app. Changing the *default* requires
  a new web build from the dev team.

## 8. Health check

- `GET /` → returns the web app (HTTP 200) — good for an uptime probe.
- `GET /api/advisor/status` → `{"enabled":true|false}` after login (requires auth).

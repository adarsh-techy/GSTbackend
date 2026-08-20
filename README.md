# GSTAutoPilot

Multi-tenant GST compliance SaaS (.NET 10 + SQL Server backend, React/TypeScript frontend) — invoicing, e-invoice/IRN, e-way bills, GSTR-1/2B/3B, reconciliation, filing, and a read-only **GST Advisor** chatbot.

## Projects

- `GSTAutoPilot.Domain` / `GSTAutoPilot.Application` / `GSTAutoPilot.Infrastructure` / `GSTAutoPilot.API` — Clean Architecture backend.
- `GSTAutoPilot.Web` — Vite + React frontend (proxies `/api` to the backend).

## Configuration

`GSTAutoPilot.API/appsettings.json` is **committed**, so **never put real secrets in it**.
See `GSTAutoPilot.API/appsettings.sample.json` for the full structure. Provide
secrets out-of-band via **environment variables** or **.NET user-secrets**
(both override `appsettings.json`; neither is committed).

> ⚠️ A secret committed to `appsettings.json` / `appsettings.Development.json` is
> public on the remote and permanent in git history — rotate it immediately if it
> happens. Keep connection strings, the JWT key, GSP credentials, and the Advisor
> API key out of tracked files.

In config keys, `:` is written as `__` (double underscore) in environment variables
— e.g. `Advisor:ApiKey` → `Advisor__ApiKey`.

### First-time local setup (required)

`appsettings.json` ships with **placeholders**, not values, for the two secrets the
app cannot start without. A fresh clone will fail to reach the database until you
supply them. Both are per-developer and never committed:

```powershell
cd GSTAutoPilot.API
dotnet user-secrets set "ConnectionStrings:MasterConnection" "Data Source=<host>;Initial Catalog=GSTAutoPilot_Master;User ID=<user>;Password=<pw>;TrustServerCertificate=True;Connection Timeout=60"
dotnet user-secrets set "Jwt:Key" "<a long random string>"
```

| Secret | Symptom if missing |
|---|---|
| `ConnectionStrings:MasterConnection` | Login returns 500; log shows a SQL connection failure |
| `Jwt:Key` | API refuses to start: *"Jwt:Key is not configured."* (must be ≥32 bytes) |

The API deliberately **refuses to start** on a missing, blank, or still-placeholder
`Jwt:Key` rather than falling back to the committed placeholder text — a public
signing key would let anyone mint a token for any tenant.

Ask a teammate for the connection string — do not copy one back into a tracked file.
User-secrets load **only** when `ASPNETCORE_ENVIRONMENT=Development`; deployed
environments read the same keys from env vars (see `appsettings.Production.json`,
which is intentionally secret-free).

Then run the two processes in separate terminals:

```powershell
cd GSTAutoPilot.API ; dotnet run --launch-profile https   # https://localhost:7124
cd GSTAutoPilot.Web ; npm install ; npm run dev            # http://localhost:5173
```

### Database migrations

Migrations are **not** applied automatically at startup, and `dotnet-ef` is not
assumed to be installed. Each migration under
`GSTAutoPilot.Infrastructure/Migrations/TenantDb/` ships with a matching
idempotent `.sql` that must be run **against every tenant database** — tenant
connection strings live in the master `Tenants` table, and each tenant may sit on
a different server.

```powershell
# per tenant DB, e.g.
sqlcmd -S <tenant-host> -U <user> -P <pw> -d <tenant-db> -C -b `
  -i GSTAutoPilot.Infrastructure/Migrations/TenantDb/<migration>.sql
```

Forgetting a tenant shows up as `SqlException: Invalid column name '<NewColumn>'`
on the pages that read the new columns.

### GST Advisor (Claude) settings

The advisor is **off by default** (`Advisor:Enabled=false`, empty key); the UI
launcher stays hidden until `GET /api/advisor/status` reports enabled.

| Key | Env var | Notes |
|---|---|---|
| `Advisor:Enabled` | `Advisor__Enabled` | `true` to turn the advisor on |
| `Advisor:ApiKey` | `Advisor__ApiKey` | Anthropic API key (`sk-ant-…`) — **secret** |
| `Advisor:Model` | `Advisor__Model` | default `claude-opus-4-8` |
| `Advisor:MaxTokens` | `Advisor__MaxTokens` | default `8000` |

**Enable for a local test (PowerShell, same window that launches the API):**

```powershell
$env:Advisor__Enabled = "true"
$env:Advisor__ApiKey  = "sk-ant-…"
# then start the API; Start-Process inherits these
```

Env vars load in any environment and override `appsettings.json`. To turn it off,
clear the variable (`Remove-Item Env:Advisor__ApiKey`) or close the window.

**Alternative — user-secrets (loads only when `ASPNETCORE_ENVIRONMENT=Development`):**

```powershell
cd GSTAutoPilot.API
dotnet user-secrets set "Advisor:Enabled" "true"
dotnet user-secrets set "Advisor:ApiKey" "sk-ant-…"
```

Other secrets follow the same pattern: `ConnectionStrings__MasterConnection`,
`Jwt__Key`, `WhiteBooksEInvoice__Sandbox__ClientSecret`, etc.

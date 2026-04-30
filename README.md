# DocControl App (Backend + Frontend)
Project tracking, document numbering, code catalog, audit log, and basic AI helpers. Backend runs on Azure Functions (isolated) with PostgreSQL; frontend is React/Vite.

## Requirements
- .NET 8 SDK
- Node.js 20+
- PostgreSQL (Neon friendly; SSL required)

## Environment (Functions)
- Configure database connection (case matters on Linux):
  - `ConnectionStrings__Db=<Npgsql connection string>` **or** `DbConnection=<Npgsql connection string>`
- Optional: `ApiKeysPath` for AI keys (defaults to a writable temp path like `%HOME%/doccontrol/apikeys.json`)
- Auth/dev flow: register first (`POST /auth/register` or via the UI) to get a bearer token. MFA is mandatory for password auth: call `/auth/mfa/start` then `/auth/mfa/verify` (the UI also offers QR). Browser Microsoft auth comes through Azure Static Web Apps `x-ms-client-principal`; CLI Microsoft auth uses device-code login and exchanges a validated Microsoft id token for a DocControl bearer token.

### Neon connection tips
- Remove unsupported params like `channel_binding` (the runtime also sanitizes).
- Ensure `SSL Mode=Require`.

## Backend (Azure Functions isolated)
```bash
cd DocControl.Api
# local.settings.json contains a sample; replace DbConnection with your Postgres string
dotnet build
func start   # or: dotnet run
```
Key endpoints (all prefixed with `/api`):
- Projects: `GET/POST /projects`, `GET /projects/{projectId}`
- Members/Invites: `GET /projects/{id}/members`, `POST /projects/{id}/invites`, `POST /invites/accept`, `POST /projects/{id}/members/{userId}/role`, `DELETE /projects/{id}/members/{userId}`
- Codes: `GET /projects/{id}/codes`, `POST /projects/{id}/codes`, `DELETE /projects/{id}/codes/{codeSeriesId}`, `POST /projects/{id}/codes/import` (CSV `Level,Code,Code Description`)
- Documents: `GET /projects/{id}/documents`, `GET /projects/{id}/documents/{docId}`, `POST /projects/{id}/documents`, `POST /projects/{id}/documents/import` (lines: `CODE filename`), `DELETE /projects/{id}/documents` (owner-only purge)
- Audit: `GET /projects/{id}/audit`
- Settings: `GET/POST /projects/{id}/settings`
- AI: `POST /projects/{id}/ai/interpret`, `POST /projects/{id}/ai/recommend`
- Auth: `POST /auth/register`, `POST /auth/login`, `GET /auth/me`, `POST /auth/mfa/start`, `POST /auth/mfa/verify`, `GET /auth/microsoft/device-code/config`, `POST /auth/microsoft/cli-token`

## Frontend (React + Vite)
```bash
cd web
npm install
npm run dev     # or: npm run build
```
Features: project switcher, code catalog table, document generator/importer, members & roles, audit, settings, AI recommend/interpret, management (document purge), MFA setup with QR.

### CLI Microsoft Login
Set these app settings on the API deployment to enable `bin/doccontrol login microsoft`:

```bash
MicrosoftAuth__ClientId="<public-client-app-id>"
MicrosoftAuth__TenantId="<tenant-id-or-common>"
MicrosoftAuth__DeviceCodeScopes="openid profile email"
AuthTokenLifetimeHours="720"
```

The CLI fetches this public config, completes Microsoft device-code auth directly with Microsoft, sends the returned id token to `/auth/microsoft/cli-token`, and stores only the minted DocControl bearer token in the local user config file.

### Dev auth shortcut
Use the Register screen or set localStorage directly after calling `/auth/register`:
```js
localStorage.setItem('dc.userId', '<user id>');
localStorage.setItem('dc.email', '<email>');
localStorage.setItem('dc.name', '<display name>');
localStorage.setItem('dc.mfa', 'true'); // set true after /auth/mfa/verify
```
Pick a project on the Projects page; selection is persisted.

## Production TODOs
- Add configurable expiry/rotation for minted DocControl bearer tokens.
- Swap CredentialManagement for a secret store/Key Vault to clear NuGet warnings.
- Add richer UX (toasts/spinners) as desired.

## Quick checks
- `dotnet build DocControlApp.sln`
- `cd web && npm run build`

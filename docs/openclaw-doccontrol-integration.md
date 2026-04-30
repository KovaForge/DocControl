# OpenClaw ↔ DocControl Integration Instructions

Purpose: enable OpenClaw to interface with the deployed DocControl app at `https://dc.delpach.com` so it can list/search existing file names and allocate new file names remotely.

Deployment evidence gathered 2026-05-01:

- `dc.delpach.com` serves a Vite React app titled `DocControl`.
- The deployed `index.html` matches this repo's `web/index.html` structure.
- The deployed frontend calls `/api/projects/{projectId}/documents` and expects document objects with `fileName`.
- The likely source repo is `https://github.com/KovaForge/DocControl`.

## Stage 1 — Confirm API and auth model

```text
Investigate KovaForge/DocControl and the deployed site https://dc.delpach.com.

Goal:
Confirm the exact API/auth path OpenClaw should use to retrieve and allocate document file names.

Check:
- How frontend authenticates: password, Microsoft login, MFA, bearer token, localStorage keys, cookies, headers.
- Which API endpoint lists documents. Likely:
  GET /api/projects/{projectId}/documents
- Which API endpoint previews a new document/file name. Likely:
  POST /api/projects/{projectId}/documents/preview
- Which API endpoint creates/saves a new document/file name. Likely:
  POST /api/projects/{projectId}/documents
- Required project discovery endpoint. Likely:
  GET /api/projects
- Whether document objects expose `fileName`, `id`, `number`, metadata, project id/name.
- Whether CORS/session/auth allows a CLI/OpenClaw-side integration.

Output:
- Auth flow summary.
- Endpoint list.
- Required headers.
- Example curl commands with placeholders only.
- Any blocker.

Do not hardcode credentials.
Do not print tokens/passwords/secrets.
```

## Stage 2 — Build minimal DocControl CLI/helper

```text
Create a small local helper for DocControl access.

Goal:
A script/CLI OpenClaw can call to list/search DocControl file names and allocate new ones.

Requirements:
- Config from environment variables only:
  DOCCONTROL_BASE_URL=https://dc.delpach.com
  DOCCONTROL_TOKEN or equivalent auth surface
- Commands:
  doccontrol projects
  doccontrol files --project <id>
  doccontrol search --project <id> --query <text>
  doccontrol preview-name --project <id> [naming fields...]
  doccontrol allocate-name --project <id> [naming fields...]
- Output JSON by default.
- Never print secrets.
- Include clear auth errors.
- Add README usage with placeholder env vars.

Acceptance:
- Can list projects.
- Can list document file names for a project.
- Can search/filter by text.
- Can preview the next candidate file name without saving.
- Can allocate/create a new saved document remotely and return its final `fileName`.
```

## Stage 3 — Wire into OpenClaw

```text
Create an OpenClaw-facing DocControl integration/skill.

Goal:
Let OpenClaw answer requests like:
- “list DocControl file names”
- “find files matching MIC-GAI”
- “allocate a new DocControl file name for this document”
- “what DocControl documents exist for project X?”

Requirements:
- Use the local DocControl helper/API wrapper.
- Store credentials outside repo via env/OpenClaw secret surface.
- Add safe commands:
  list-projects
  list-files
  search-files
  preview-name
  allocate-name
- Return compact summaries plus JSON path/output when useful.
- For actual allocation, persist the new document remotely in DocControl.
- Return the allocated `fileName`, document id/number, and project metadata to OpenClaw.
- Do not expose destructive operations.
- No purge/delete/update endpoints exposed in this stage.
- Do not hardcode credentials.

Acceptance:
OpenClaw can retrieve existing file names from dc.delpach.com and allocate/save new file names remotely without browser scraping.
```

## Stage 3B — Allocate new DocControl file names

```text
Extend the DocControl integration so OpenClaw can allocate a new file name, save it remotely in DocControl, and receive the allocated file name back.

Required behavior:
- Accept project id/name plus document naming fields.
- Call the DocControl create/allocate endpoint, likely:
  POST /api/projects/{projectId}/documents
- Support preview-only separately, likely:
  POST /api/projects/{projectId}/documents/preview
- For actual allocation, the integration must persist the new document remotely.
- Return the allocated `fileName`, document id/number, and project metadata to OpenClaw.
- Do not expose destructive operations.
- Do not hardcode credentials.

CLI/API wrapper commands:
- doccontrol preview-name --project <id> [fields...]
- doccontrol allocate-name --project <id> [fields...]
- doccontrol files --project <id>
- doccontrol search --project <id> --query <text>

Acceptance:
- Preview can show the next candidate file name without saving.
- Allocate creates/saves the new DocControl document remotely.
- OpenClaw receives the final saved `fileName`.
- Re-running allocation does not silently duplicate unless explicitly requested.
```

## Stage 4 — Allocation safeguards

```text
Add safeguards around new file-name allocation.

Required behavior:
- Preview-only and allocate/save must be separate commands.
- Preview must not mutate remote DocControl state.
- Allocation must clearly state that it creates/saves a new document record remotely.
- Re-running allocation must not silently duplicate unless explicitly requested.
- If DocControl has an idempotency or duplicate-detection mechanism, use it.
- If no server-side idempotency exists, implement a client-side preflight search using the proposed fields/free text.
- Log sanitized request metadata only: project id/name, field names, allocated fileName, document id/number.
- Never log auth headers or tokens.

Acceptance:
- Preview can be used safely.
- Allocate creates exactly one remote saved record per explicit request.
- Duplicate-risk cases return a clear warning or require explicit confirmation.
```

## Stage 5 — Validate and document

```text
Run an end-to-end validation.

Checks:
- Auth works from OpenClaw runtime.
- Project listing works.
- File-name listing works.
- Search works.
- Preview-name works without saving.
- Allocate-name saves remotely and returns the allocated fileName.
- Bad auth gives a clean error.
- No credentials committed or printed.

Deliver:
- Commands run.
- Example sanitized output.
- Repo/file changes.
- Any follow-up recommendations.
```

## Implemented local integration

Added a direct API helper at `bin/doccontrol` backed by `tools/doccontrol/doccontrol.py`.

Confirmed API/auth model from repo source:

- Auth uses `Authorization: Bearer <token>` for deployed password-auth sessions. Tokens are issued by `POST /api/auth/login` as `authToken` when `AuthTokenSecret` is configured. Static Web Apps auth can also bind via `x-ms-client-principal`; local legacy `x-user-id` headers are dev/legacy only and are not used by the CLI.
- Project discovery: `GET /api/projects`.
- Document listing/search: `GET /api/projects/{projectId}/documents?q=<text>&take=<n>&skip=<n>`.
- Safe preview: `POST /api/projects/{projectId}/documents/preview`.
- Remote allocation/save: `POST /api/projects/{projectId}/documents`.
- Required allocation payload fields depend on project settings, but the default app requires `level1`, `level2`, and `level3`; optional fields are `level4`, `level5`, `level6`, `freeText`, `extension`, and `originalQuery`.

Sanitized curl examples:

```bash
curl -H "Authorization: Bearer <DOCCONTROL_TOKEN>" \
  "https://dc.delpach.com/api/projects"

curl -H "Authorization: Bearer <DOCCONTROL_TOKEN>" \
  "https://dc.delpach.com/api/projects/<projectId>/documents?q=MIC-GAI&take=50"

curl -X POST "https://dc.delpach.com/api/projects/<projectId>/documents/preview" \
  -H "Authorization: Bearer <DOCCONTROL_TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"level1":"MIC","level2":"GAI","level3":"DOC","freeText":"Example","extension":"pdf"}'

curl -X POST "https://dc.delpach.com/api/projects/<projectId>/documents" \
  -H "Authorization: Bearer <DOCCONTROL_TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"level1":"MIC","level2":"GAI","level3":"DOC","freeText":"Example","extension":"pdf"}'
```

OpenClaw-facing skill instructions live at `docs/openclaw-doccontrol-skill/SKILL.md`.

Live E2E listing/preview/allocation remains blocked until a valid `DOCCONTROL_TOKEN` is available in the OpenClaw runtime. Bad-token validation against `https://dc.delpach.com` returns a sanitized `401 Unauthorized` JSON error.

## Recommendation

Use direct API integration first. Browser automation should only be a fallback if auth/API access blocks a proper CLI/OpenClaw integration.

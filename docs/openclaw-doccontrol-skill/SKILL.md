# DocControl

Use this skill when OpenClaw needs to list DocControl projects, list/search document file names, preview a new document name, or allocate a new saved document name through the DocControl API.

## Configuration

Require environment-only configuration:

```bash
export DOCCONTROL_BASE_URL="https://dc.delpach.com"
export DOCCONTROL_TOKEN="<bearer token from DocControl auth>"
```

Never paste or log `DOCCONTROL_TOKEN`. If a live command fails with auth errors, report that auth is blocked and ask for a valid token or equivalent configured secret.

## Commands

Run from the DocControl repo:

```bash
bin/doccontrol list-projects
bin/doccontrol list-files --project <id-or-exact-name> --take 50
bin/doccontrol search-files --project <id-or-exact-name> --query "<text>"
bin/doccontrol preview-name --project <id-or-exact-name> --level1 <code> --level2 <code> --level3 <code> --free-text "<title>" --extension <ext>
bin/doccontrol allocate-name --project <id-or-exact-name> --level1 <code> --level2 <code> --level3 <code> --free-text "<title>" --extension <ext>
```

Optional fields for preview/allocation: `--level4`, `--level5`, `--level6`, `--original-query`.

## Behavior

- Return compact summaries based on the JSON output.
- Use `preview-name` for safe planning; it must not mutate remote state.
- Use `allocate-name` only when the user explicitly wants a new saved DocControl document name.
- If `allocate-name` returns `duplicate-risk`, do not re-run with `--force` unless the user explicitly confirms a duplicate allocation.
- Do not expose delete, purge, update, import, member, invite, settings, or auth management commands through this skill.

## API Surface

The CLI wraps these deployed endpoints under `/api`:

- `GET /projects`
- `GET /projects/{projectId}/documents?q=&take=&skip=`
- `POST /projects/{projectId}/documents/preview`
- `POST /projects/{projectId}/documents`

Required header:

```text
Authorization: Bearer <DOCCONTROL_TOKEN>
```

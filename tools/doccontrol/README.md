# DocControl CLI for OpenClaw

Small env-configured helper for safe DocControl reads and remote document-name allocation.

## Configuration

Credentials stay outside the repo:

```bash
export DOCCONTROL_BASE_URL="https://dc.delpach.com"
export DOCCONTROL_TOKEN="<doccontrol bearer token>"
```

`DOCCONTROL_TOKEN` is the bearer token returned by DocControl password auth (`authToken`) or an equivalent deployed auth token. The CLI never prints the token.

## Commands

```bash
bin/doccontrol projects
bin/doccontrol files --project 1 --take 50
bin/doccontrol search --project 1 --query MIC-GAI
bin/doccontrol preview-name --project 1 --level1 MIC --level2 GAI --level3 DOC --free-text "Example" --extension pdf
bin/doccontrol allocate-name --project 1 --level1 MIC --level2 GAI --level3 DOC --free-text "Example" --extension pdf
```

Aliases are also exposed for OpenClaw skill wording:

```bash
bin/doccontrol list-projects
bin/doccontrol list-files --project 1
bin/doccontrol search-files --project 1 --query MIC-GAI
```

## Safety

- JSON output is the default and only output format.
- `preview-name` calls `/documents/preview` and reports `"mutated": false`.
- `allocate-name` calls `/documents` and creates a saved remote document record.
- Before allocation, the CLI searches existing documents for matching levels and free text. If matches exist, it returns `duplicate-risk` and does not create anything unless `--force` is supplied.
- No destructive DocControl endpoints are exposed.
- Auth failures are sanitized and do not echo tokens.

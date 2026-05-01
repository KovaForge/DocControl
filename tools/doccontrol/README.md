# DocControl CLI for OpenClaw

Small env-configured helper for safe DocControl reads and remote document-name allocation.

## Configuration

Credentials stay outside the repo. Preferred setup is Microsoft device-code login:

```bash
bin/doccontrol login microsoft
```

The command opens a Microsoft device-code flow, exchanges the verified Microsoft identity for a DocControl bearer token that satisfies DocControl's MFA gate, and stores only the DocControl token in `~/.config/doccontrol/config.json` with user-only file permissions. Microsoft tokens are not stored.

Environment overrides still work:

```bash
export DOCCONTROL_BASE_URL="https://dc.delpach.com"
export DOCCONTROL_TOKEN="<doccontrol bearer token>"
```

`DOCCONTROL_TOKEN` is the bearer token returned by DocControl auth. The CLI never prints the token.
Authenticated CLI requests send this token in `X-DocControl-Token` because Azure Static Web Apps can consume the standard `Authorization` header before forwarding requests to Functions. The API still validates the same signed DocControl token server-side.

## Commands

```bash
bin/doccontrol openclaw manifest
bin/doccontrol login microsoft
bin/doccontrol projects
bin/doccontrol files --project 1 --take 50
bin/doccontrol search --project 1 --query MIC-GAI
bin/doccontrol level-codes --project 1 --level 1
bin/doccontrol upsert-level-code --project 1 --level 1 --code SHX --description "ShareX Team"
bin/doccontrol get-level-code --project 1 --level 1 --code SHX
bin/doccontrol preview-name --project 1 --level1 MIC --level2 GAI --level3 DOC --free-text "Example" --extension pdf
bin/doccontrol allocate-name --project 1 --level1 MIC --level2 GAI --level3 DOC --free-text "Example" --extension pdf
```

Aliases are also exposed for OpenClaw skill wording:

```bash
bin/doccontrol list-projects
bin/doccontrol list-files --project 1
bin/doccontrol search-files --project 1 --query MIC-GAI
```

Standalone level code commands manage project-level dictionaries such as Level 1 owners without creating a document or changing a numbering series. Existing `codes` APIs remain for full/hierarchical catalog combinations.

## OpenClaw manifest

`bin/doccontrol openclaw manifest` prints a machine-readable capability manifest for OpenClaw agents. It does not require auth, does not read stored token values, and emits JSON to stdout.

## Safety

- JSON output is the default and only output format for automation commands.
- `preview-name` calls `/documents/preview` and reports `"mutated": false`.
- `allocate-name` calls `/documents` and creates a saved remote document record.
- Before allocation, the CLI searches existing documents for matching levels and free text. If matches exist, it returns `duplicate-risk` and does not create anything unless `--force` is supplied.
- No destructive DocControl endpoints are exposed.
- Auth failures are sanitized and do not echo tokens.
- `login microsoft` stores only the minted DocControl bearer token locally, never the Microsoft id token.
- `logout` clears the stored DocControl token.

#!/usr/bin/env python3
"""Small DocControl API CLI for OpenClaw integrations."""

from __future__ import annotations

import argparse
import json
import os
import stat
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any


DEFAULT_BASE_URL = "https://dc.delpach.com"
DEFAULT_TIMEOUT_SECONDS = 30


class DocControlError(Exception):
    def __init__(self, message: str, *, status: int | None = None):
        super().__init__(message)
        self.status = status


@dataclass(frozen=True)
class BaseConfig:
    base_url: str
    timeout: int


@dataclass(frozen=True)
class Config(BaseConfig):
    token: str


def config_file_path() -> Path:
    explicit = os.environ.get("DOCCONTROL_CONFIG")
    if explicit:
        return Path(explicit).expanduser()
    base = Path(os.environ.get("XDG_CONFIG_HOME", "~/.config")).expanduser()
    return base / "doccontrol" / "config.json"


def load_stored_config() -> dict[str, Any]:
    path = config_file_path()
    if not path.exists():
        return {}
    try:
        with path.open("r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, json.JSONDecodeError):
        return {}


def save_stored_config(data: dict[str, Any]) -> Path:
    path = config_file_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    fd = os.open(path, flags, stat.S_IRUSR | stat.S_IWUSR)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, sort_keys=True)
            f.write("\n")
    finally:
        try:
            os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
        except OSError:
            pass
    return path


def load_base_config(args: argparse.Namespace) -> BaseConfig:
    stored = load_stored_config()
    base_url = (args.base_url or os.environ.get("DOCCONTROL_BASE_URL") or stored.get("baseUrl") or DEFAULT_BASE_URL).strip()
    timeout = int(os.environ.get("DOCCONTROL_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS))
    return BaseConfig(base_url=base_url.rstrip("/"), timeout=timeout)


def load_config(args: argparse.Namespace) -> Config:
    stored = load_stored_config()
    base = load_base_config(args)
    token = (args.token or os.environ.get("DOCCONTROL_TOKEN") or stored.get("token") or "").strip()
    if not token:
        raise DocControlError("Missing DocControl token. Run `doccontrol login microsoft` or set DOCCONTROL_TOKEN; credentials stay outside the repo.")
    return Config(base_url=base.base_url, token=token, timeout=base.timeout)


def api_url(base_url: str, path: str, query: dict[str, Any] | None = None) -> str:
    base = base_url
    if not urllib.parse.urlparse(base).path.rstrip("/").endswith("/api"):
        base = f"{base}/api"
    url = f"{base}{path}"
    if query:
        clean = {k: str(v) for k, v in query.items() if v is not None and str(v) != ""}
        if clean:
            url = f"{url}?{urllib.parse.urlencode(clean)}"
    return url


def request_json(config: Config, method: str, path: str, payload: dict[str, Any] | None = None, query: dict[str, Any] | None = None) -> Any:
    headers = {
        "Accept": "application/json",
        "Content-Type": "application/json",
        "X-DocControl-Token": config.token,
        "User-Agent": "OpenClaw-DocControl/1.0",
    }
    return request_json_url(api_url(config.base_url, path, query), method, config.timeout, payload, headers)


def request_public_json(config: BaseConfig, method: str, path: str, payload: dict[str, Any] | None = None, query: dict[str, Any] | None = None) -> Any:
    headers = {
        "Accept": "application/json",
        "Content-Type": "application/json",
        "User-Agent": "OpenClaw-DocControl/1.0",
    }
    return request_json_url(api_url(config.base_url, path, query), method, config.timeout, payload, headers)


def request_json_url(url: str, method: str, timeout: int, payload: dict[str, Any] | None, headers: dict[str, str]) -> Any:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            raw = res.read()
            if not raw:
                return None
            return json.loads(raw.decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        raise DocControlError(sanitize_http_error(exc.code, exc.reason, raw), status=exc.code) from None
    except urllib.error.URLError as exc:
        raise DocControlError(f"Network error contacting DocControl: {exc.reason}") from None
    except json.JSONDecodeError:
        raise DocControlError("DocControl returned non-JSON response") from None


def request_form_url(url: str, timeout: int, fields: dict[str, str]) -> Any:
    body = urllib.parse.urlencode(fields).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={
            "Accept": "application/json",
            "Content-Type": "application/x-www-form-urlencoded",
            "User-Agent": "OpenClaw-DocControl/1.0",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            return json.loads(res.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            data = {"error": raw.strip() or exc.reason}
        data["status"] = exc.code
        return data
    except urllib.error.URLError as exc:
        raise DocControlError(f"Network error contacting Microsoft login: {exc.reason}") from None
    except json.JSONDecodeError:
        raise DocControlError("Microsoft login returned non-JSON response") from None


def sanitize_http_error(status: int, reason: str, body: str) -> str:
    body = body.strip()
    if len(body) > 500:
        body = f"{body[:500]}..."
    if status in (401, 403):
        return f"DocControl auth error ({status} {reason}). Check login state, MFA status, and project role."
    if body:
        return f"DocControl API error ({status} {reason}): {body}"
    return f"DocControl API error ({status} {reason})"


def output(data: Any) -> None:
    print(json.dumps(data, indent=2, sort_keys=True))


def openclaw_manifest() -> dict[str, Any]:
    return {
        "schema": "https://openclaw.ai/schemas/tool-manifest/v1",
        "name": "doccontrol",
        "displayName": "DocControl CLI",
        "description": "First-party DocControl automation CLI for OpenClaw agents to list, search, preview, and allocate controlled document file names.",
        "invocation": "doccontrol",
        "versionCommand": None,
        "healthCommand": None,
        "bootstrapCommand": "doccontrol login microsoft",
        "principles": [
            "Non-interactive commands by default except explicit login bootstrap",
            "Stable exit codes: 0 on success, 1 on sanitized operational errors",
            "Machine-readable JSON on stdout for automation paths",
            "Microsoft tokens are never stored; only the minted DocControl bearer token may be saved locally",
            "Secrets, config contents, and auth headers are never printed",
            "Preview document names before mutating allocation/import operations",
        ],
        "commands": [
            {
                "command": "openclaw manifest",
                "description": "Describe CLI capabilities for OpenClaw agents.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": False,
            },
            {
                "command": "login microsoft [--no-store]",
                "description": "Start Microsoft device-code login and exchange the verified identity for a DocControl bearer token.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "logout",
                "description": "Clear the stored DocControl bearer token from the local config file.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "projects",
                "description": "List accessible DocControl projects.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "list-projects",
                "description": "Alias for projects.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "files --project <id-or-name> [--query <text>] [--take <n>] [--skip <n>]",
                "description": "List document file names for a project.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "list-files --project <id-or-name> [--query <text>] [--take <n>] [--skip <n>]",
                "description": "Alias for files.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "search --project <id-or-name> --query <text> [--take <n>] [--skip <n>]",
                "description": "Search document file names and free text for a project.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "search-files --project <id-or-name> --query <text> [--take <n>] [--skip <n>]",
                "description": "Alias for search.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "level-codes --project <id-or-name> [--level <1-6>]",
                "description": "List standalone project level-code catalog entries.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "get-level-code --project <id-or-name> --level <1-6> --code <code>",
                "description": "Get one standalone project level-code catalog entry.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "upsert-level-code --project <id-or-name> --level <1-6> --code <code> --description <text>",
                "description": "Create or update a standalone project level-code catalog entry.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "preview-name --project <id-or-name> --level1 <code> --level2 <code> --level3 <code> [--level4 <code>] [--level5 <code>] [--level6 <code>] [--free-text <text>] [--extension <ext>] [--original-query <text>]",
                "description": "Preview the next controlled file name without saving a document record.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
            {
                "command": "allocate-name --project <id-or-name> --level1 <code> --level2 <code> --level3 <code> [--level4 <code>] [--level5 <code>] [--level6 <code>] [--free-text <text>] [--extension <ext>] [--original-query <text>] [--force]",
                "description": "Create and save a new controlled document name remotely after duplicate-risk preflight.",
                "jsonOutput": True,
                "mayUseNetworkOrMutate": True,
            },
        ],
    }


def normalize_items(response: Any) -> list[dict[str, Any]]:
    if isinstance(response, dict) and isinstance(response.get("items"), list):
        return response["items"]
    if isinstance(response, list):
        return response
    return []


def resolve_project(config: Config, project: str) -> tuple[str, dict[str, Any] | None]:
    if project.isdigit():
        return project, None
    projects = request_json(config, "GET", "/projects")
    matches = [
        p for p in normalize_items(projects)
        if str(p.get("name", "")).casefold() == project.casefold()
    ]
    if not matches:
        raise DocControlError(f"Project not found by name: {project}")
    if len(matches) > 1:
        ids = ", ".join(str(p.get("id")) for p in matches)
        raise DocControlError(f"Project name is ambiguous: {project} matched ids {ids}")
    return str(matches[0]["id"]), matches[0]


def document_payload(args: argparse.Namespace) -> dict[str, Any]:
    payload = {
        "level1": args.level1,
        "level2": args.level2,
        "level3": args.level3,
        "level4": args.level4,
        "level5": args.level5,
        "level6": args.level6,
        "freeText": args.free_text,
        "extension": args.extension,
        "originalQuery": args.original_query,
    }
    return {k: v for k, v in payload.items() if v is not None and str(v) != ""}


def docs_for_duplicate_check(config: Config, project_id: str, payload: dict[str, Any]) -> list[dict[str, Any]]:
    query = payload.get("freeText") or payload.get("level3") or payload.get("level2") or payload.get("level1")
    response = request_json(config, "GET", f"/projects/{project_id}/documents", query={"q": query, "take": 200})
    docs = normalize_items(response)
    matches: list[dict[str, Any]] = []
    for doc in docs:
        if not same_or_empty(doc.get("level1"), payload.get("level1")):
            continue
        if not same_or_empty(doc.get("level2"), payload.get("level2")):
            continue
        if not same_or_empty(doc.get("level3"), payload.get("level3")):
            continue
        if not same_or_empty(doc.get("level4"), payload.get("level4")):
            continue
        if not same_or_empty(doc.get("level5"), payload.get("level5")):
            continue
        if not same_or_empty(doc.get("level6"), payload.get("level6")):
            continue
        if (doc.get("freeText") or "").strip().casefold() == (payload.get("freeText") or "").strip().casefold():
            matches.append(doc)
    return matches


def same_or_empty(left: Any, right: Any) -> bool:
    return (left or "").strip().casefold() == (right or "").strip().casefold()


def cmd_login_microsoft(config: BaseConfig, args: argparse.Namespace) -> None:
    auth_config = request_public_json(config, "GET", "/auth/microsoft/device-code/config")
    client_id = str(auth_config.get("clientId") or "").strip()
    tenant_id = str(auth_config.get("tenantId") or "common").strip()
    scopes = str(auth_config.get("scopes") or "openid profile email").strip()
    if not client_id:
        raise DocControlError("DocControl Microsoft CLI login is not configured")

    device_url = f"https://login.microsoftonline.com/{urllib.parse.quote(tenant_id)}/oauth2/v2.0/devicecode"
    token_url = f"https://login.microsoftonline.com/{urllib.parse.quote(tenant_id)}/oauth2/v2.0/token"
    device = request_form_url(device_url, config.timeout, {"client_id": client_id, "scope": scopes})
    if "error" in device:
        raise DocControlError(f"Microsoft device-code start failed: {device.get('error_description') or device.get('error')}")

    message = device.get("message")
    if message:
        print(message, file=sys.stderr)
    else:
        print(f"Open {device.get('verification_uri')} and enter code {device.get('user_code')}", file=sys.stderr)

    token_result = poll_microsoft_token(token_url, config.timeout, client_id, str(device["device_code"]), int(device.get("interval", 5)), int(device.get("expires_in", 900)))
    id_token = token_result.get("id_token")
    if not id_token:
        raise DocControlError("Microsoft login did not return an id token")

    login = request_public_json(config, "POST", "/auth/microsoft/cli-token", {"idToken": id_token})
    auth_token = str(login.get("authToken") or "").strip()
    if not auth_token:
        raise DocControlError("DocControl did not return a bearer token")

    stored_path: str | None = None
    if not args.no_store:
        stored = load_stored_config()
        stored["baseUrl"] = config.base_url
        stored["token"] = auth_token
        stored_path = str(save_stored_config(stored))

    output({
        "status": "ok",
        "stored": not args.no_store,
        "configPath": stored_path,
        "user": {
            "id": login.get("id"),
            "email": login.get("email"),
            "displayName": login.get("displayName"),
            "provider": login.get("provider"),
        },
    })


def poll_microsoft_token(token_url: str, timeout: int, client_id: str, device_code: str, interval: int, expires_in: int) -> dict[str, Any]:
    deadline = time.monotonic() + expires_in
    wait_seconds = max(interval, 1)
    while time.monotonic() < deadline:
        time.sleep(wait_seconds)
        result = request_form_url(token_url, timeout, {
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "client_id": client_id,
            "device_code": device_code,
        })
        error = result.get("error")
        if not error:
            return result
        if error == "authorization_pending":
            continue
        if error == "slow_down":
            wait_seconds += 5
            continue
        if error in ("authorization_declined", "expired_token", "bad_verification_code"):
            raise DocControlError(f"Microsoft login failed: {result.get('error_description') or error}")
        raise DocControlError(f"Microsoft token request failed: {result.get('error_description') or error}")
    raise DocControlError("Microsoft login timed out before authorization completed")


def cmd_logout(_config: BaseConfig, _args: argparse.Namespace) -> None:
    stored = load_stored_config()
    had_token = "token" in stored
    stored.pop("token", None)
    path = save_stored_config(stored)
    output({"status": "ok", "cleared": had_token, "configPath": str(path)})


def cmd_openclaw_manifest(_config: BaseConfig, _args: argparse.Namespace) -> None:
    output(openclaw_manifest())


def cmd_projects(config: Config, _args: argparse.Namespace) -> None:
    output({"projects": request_json(config, "GET", "/projects")})


def cmd_files(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    response = request_json(config, "GET", f"/projects/{project_id}/documents", query={"q": args.query, "take": args.take, "skip": args.skip})
    output({"project": project or {"id": int(project_id)}, "documents": response})


def cmd_search(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    response = request_json(config, "GET", f"/projects/{project_id}/documents", query={"q": args.query, "take": args.take, "skip": args.skip})
    output({"project": project or {"id": int(project_id)}, "query": args.query, "documents": response})


def cmd_level_codes(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    response = request_json(config, "GET", f"/projects/{project_id}/level-codes", query={"level": args.level})
    output({"project": project or {"id": int(project_id)}, "levelCodes": response})


def cmd_get_level_code(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    response = request_json(config, "GET", f"/projects/{project_id}/level-codes/{args.level}/{urllib.parse.quote(args.code)}")
    output({"project": project or {"id": int(project_id)}, "levelCode": response})


def cmd_upsert_level_code(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    payload = {"level": args.level, "code": args.code, "description": args.description}
    response = request_json(config, "POST", f"/projects/{project_id}/level-codes", payload=payload)
    output({"status": "ok", "project": project or {"id": int(project_id)}, "levelCode": response})


def cmd_preview(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    payload = document_payload(args)
    preview = request_json(config, "POST", f"/projects/{project_id}/documents/preview", payload=payload)
    output({"project": project or {"id": int(project_id)}, "preview": preview, "mutated": False})


def cmd_allocate(config: Config, args: argparse.Namespace) -> None:
    project_id, project = resolve_project(config, args.project)
    payload = document_payload(args)
    duplicates = docs_for_duplicate_check(config, project_id, payload)
    if duplicates and not args.force:
        output({
            "status": "duplicate-risk",
            "message": "Matching document fields already exist. Re-run with --force to allocate another saved document.",
            "project": project or {"id": int(project_id)},
            "matches": [
                {
                    "id": d.get("id"),
                    "number": d.get("number"),
                    "fileName": d.get("fileName"),
                    "freeText": d.get("freeText"),
                    "createdAtUtc": d.get("createdAtUtc"),
                }
                for d in duplicates
            ],
            "created": False,
        })
        return
    created = request_json(config, "POST", f"/projects/{project_id}/documents", payload=payload)
    output({"status": "created", "project": project or {"id": int(project_id)}, "document": created, "created": True})


def add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--base-url", help="DocControl base URL. Defaults to stored config, DOCCONTROL_BASE_URL, or https://dc.delpach.com.")
    parser.add_argument("--token", help=argparse.SUPPRESS)


def add_project(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--project", required=True, help="Project id or exact project name.")


def add_paging(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--take", type=int, default=50)
    parser.add_argument("--skip", type=int, default=0)


def add_level_code_fields(parser: argparse.ArgumentParser) -> None:
    add_project(parser)
    parser.add_argument("--level", type=int, required=True, choices=range(1, 7), metavar="1-6")
    parser.add_argument("--code", required=True)


def add_doc_fields(parser: argparse.ArgumentParser) -> None:
    add_project(parser)
    parser.add_argument("--level1", required=True)
    parser.add_argument("--level2", required=True)
    parser.add_argument("--level3", required=True)
    parser.add_argument("--level4")
    parser.add_argument("--level5")
    parser.add_argument("--level6")
    parser.add_argument("--free-text", default="")
    parser.add_argument("--extension")
    parser.add_argument("--original-query")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="doccontrol", description="OpenClaw-safe DocControl API helper.")
    add_common(parser)
    sub = parser.add_subparsers(dest="command", required=True)

    login = sub.add_parser("login", help="Authenticate the CLI.")
    login_sub = login.add_subparsers(dest="provider", required=True)
    microsoft = login_sub.add_parser("microsoft", help="Sign in with Microsoft device-code login.")
    microsoft.add_argument("--no-store", action="store_true", help="Do not save the minted DocControl token to the local config file.")
    microsoft.set_defaults(func=cmd_login_microsoft, auth_required=False)

    logout = sub.add_parser("logout", help="Clear the stored DocControl token.")
    logout.set_defaults(func=cmd_logout, auth_required=False)

    openclaw = sub.add_parser("openclaw", help="OpenClaw agent integration helpers.")
    openclaw_sub = openclaw.add_subparsers(dest="openclaw_command", required=True)
    manifest = openclaw_sub.add_parser("manifest", help="Print an OpenClaw capability manifest as JSON.")
    manifest.set_defaults(func=cmd_openclaw_manifest, auth_required=False)

    for name in ("projects", "list-projects"):
        p = sub.add_parser(name, help="List accessible projects.")
        p.set_defaults(func=cmd_projects, auth_required=True)

    for name in ("files", "list-files"):
        p = sub.add_parser(name, help="List document file names for a project.")
        add_project(p)
        p.add_argument("--query")
        add_paging(p)
        p.set_defaults(func=cmd_files, auth_required=True)

    for name in ("search", "search-files"):
        p = sub.add_parser(name, help="Search document file names/free text for a project.")
        add_project(p)
        p.add_argument("--query", required=True)
        add_paging(p)
        p.set_defaults(func=cmd_search, auth_required=True)

    p = sub.add_parser("level-codes", help="List standalone project level codes.")
    add_project(p)
    p.add_argument("--level", type=int, choices=range(1, 7), metavar="1-6", help="Optional level filter.")
    p.set_defaults(func=cmd_level_codes, auth_required=True)

    p = sub.add_parser("get-level-code", help="Get a standalone project level code.")
    add_level_code_fields(p)
    p.set_defaults(func=cmd_get_level_code, auth_required=True)

    p = sub.add_parser("upsert-level-code", help="Create or update a standalone project level code.")
    add_level_code_fields(p)
    p.add_argument("--description", required=True)
    p.set_defaults(func=cmd_upsert_level_code, auth_required=True)

    p = sub.add_parser("preview-name", help="Preview next file name without saving.")
    add_doc_fields(p)
    p.set_defaults(func=cmd_preview, auth_required=True)

    p = sub.add_parser("allocate-name", help="Create/save a new document name remotely.")
    add_doc_fields(p)
    p.add_argument("--force", action="store_true", help="Allow allocation when matching fields already exist.")
    p.set_defaults(func=cmd_allocate, auth_required=True)
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    try:
        if getattr(args, "auth_required", True):
            config = load_config(args)
        else:
            config = load_base_config(args)
        args.func(config, args)
        return 0
    except DocControlError as exc:
        print(json.dumps({"error": str(exc), "status": exc.status}, indent=2, sort_keys=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

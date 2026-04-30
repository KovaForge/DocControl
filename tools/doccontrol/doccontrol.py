#!/usr/bin/env python3
"""Small DocControl API CLI for OpenClaw integrations."""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Any


DEFAULT_BASE_URL = "https://dc.delpach.com"
DEFAULT_TIMEOUT_SECONDS = 30


class DocControlError(Exception):
    def __init__(self, message: str, *, status: int | None = None):
        super().__init__(message)
        self.status = status


@dataclass(frozen=True)
class Config:
    base_url: str
    token: str
    timeout: int


def load_config(args: argparse.Namespace) -> Config:
    base_url = (args.base_url or os.environ.get("DOCCONTROL_BASE_URL") or DEFAULT_BASE_URL).strip()
    token = (args.token or os.environ.get("DOCCONTROL_TOKEN") or "").strip()
    timeout = int(os.environ.get("DOCCONTROL_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS))
    if not token:
        raise DocControlError("Missing DOCCONTROL_TOKEN. Set it to a DocControl bearer token; credentials stay outside the repo.")
    return Config(base_url=base_url.rstrip("/"), token=token, timeout=timeout)


def api_url(config: Config, path: str, query: dict[str, Any] | None = None) -> str:
    base = config.base_url
    if not urllib.parse.urlparse(base).path.rstrip("/").endswith("/api"):
        base = f"{base}/api"
    url = f"{base}{path}"
    if query:
        clean = {k: str(v) for k, v in query.items() if v is not None and str(v) != ""}
        if clean:
            url = f"{url}?{urllib.parse.urlencode(clean)}"
    return url


def request_json(config: Config, method: str, path: str, payload: dict[str, Any] | None = None, query: dict[str, Any] | None = None) -> Any:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        api_url(config, path, query),
        data=body,
        method=method,
        headers={
            "Accept": "application/json",
            "Content-Type": "application/json",
            "Authorization": f"Bearer {config.token}",
            "User-Agent": "OpenClaw-DocControl/1.0",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=config.timeout) as res:
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


def sanitize_http_error(status: int, reason: str, body: str) -> str:
    body = body.strip()
    if len(body) > 500:
        body = f"{body[:500]}..."
    if status in (401, 403):
        return f"DocControl auth error ({status} {reason}). Check DOCCONTROL_TOKEN, MFA status, and project role."
    if body:
        return f"DocControl API error ({status} {reason}): {body}"
    return f"DocControl API error ({status} {reason})"


def output(data: Any) -> None:
    print(json.dumps(data, indent=2, sort_keys=True))


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
    parser.add_argument("--base-url", help="DocControl base URL. Defaults to DOCCONTROL_BASE_URL or https://dc.delpach.com.")
    parser.add_argument("--token", help=argparse.SUPPRESS)


def add_project(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--project", required=True, help="Project id or exact project name.")


def add_paging(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--take", type=int, default=50)
    parser.add_argument("--skip", type=int, default=0)


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

    for name in ("projects", "list-projects"):
        p = sub.add_parser(name, help="List accessible projects.")
        p.set_defaults(func=cmd_projects)

    for name in ("files", "list-files"):
        p = sub.add_parser(name, help="List document file names for a project.")
        add_project(p)
        p.add_argument("--query")
        add_paging(p)
        p.set_defaults(func=cmd_files)

    for name in ("search", "search-files"):
        p = sub.add_parser(name, help="Search document file names/free text for a project.")
        add_project(p)
        p.add_argument("--query", required=True)
        add_paging(p)
        p.set_defaults(func=cmd_search)

    p = sub.add_parser("preview-name", help="Preview next file name without saving.")
    add_doc_fields(p)
    p.set_defaults(func=cmd_preview)

    p = sub.add_parser("allocate-name", help="Create/save a new document name remotely.")
    add_doc_fields(p)
    p.add_argument("--force", action="store_true", help="Allow allocation when matching fields already exist.")
    p.set_defaults(func=cmd_allocate)
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    try:
        config = load_config(args)
        args.func(config, args)
        return 0
    except DocControlError as exc:
        print(json.dumps({"error": str(exc), "status": exc.status}, indent=2, sort_keys=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

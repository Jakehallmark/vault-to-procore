"""Incremental Autodesk Vault to Procore document synchronization.

The sync engine is deliberately separate from the HTTP clients. This allows the
matching, versioning, and idempotency behavior to be tested without credentials.
The API routes in the HTTP clients are configurable because Vault deployments
and Procore upload workflows vary by API/application configuration.
"""

from __future__ import annotations

import argparse
import contextlib
import ctypes
import hashlib
import json
import logging
from logging.handlers import RotatingFileHandler
import os
import re
import sqlite3
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path, PurePosixPath
from collections.abc import Iterable, Mapping
from typing import Protocol, TypeAlias, cast

import requests


JsonObject: TypeAlias = dict[str, object]
# Requests supports many more shapes than this connector needs. Keeping these
# aliases narrow makes the live API boundary easier to review and test.
RequestParams: TypeAlias = Mapping[str, str | int]
RequestData: TypeAlias = Mapping[str, str]
RequestFiles: TypeAlias = Mapping[str, tuple[str, bytes]]


LOG = logging.getLogger("vault_to_procore")
# These are the current business rules, kept in one obvious place so an IT
# reviewer does not have to hunt through the sync loop to find them.
DEFAULT_ALLOWED_EXTS = frozenset(
    {".pdf", ".wset", ".wtool", ".urs", ".rdb", ".zip", ".cmt", ".cd3",
     ".cd31", ".cd32", ".docx", ".sid", ".evt", ".csv", ".usw", ".sup"}
)
DEFAULT_PATH_MARKERS = frozenset(
    {"APS", "AASB", "ASB", "GCC", "ECC", "ATS", "CC1", "CC2", "CC3"}
)


@dataclass(frozen=True)
class Settings:
    """Runtime configuration collected once when the process starts.

    The defaults are deliberately cautious: dry-run is on and live API use is
    blocked until the customer's integration contract has been confirmed.
    """
    vault_base_url: str
    procore_base_url: str
    procore_company_id: str
    vault_root_path: str
    state_file: Path
    dry_run: bool
    request_timeout: int
    verify_after_hours: int
    log_file: Path
    lock_file: Path
    retry_attempts: int
    service_interval_seconds: int
    api_contract_confirmed: bool
    vault_token: str | None
    procore_token: str | None

    @classmethod
    def from_env(cls, dry_run: bool | None = None) -> "Settings":
        """Load the local file first, then let server environment values win."""
        load_env_file(Path(".env"))
        return cls(
            vault_base_url=os.getenv("VAULT_BASE_URL", "").rstrip("/"),
            procore_base_url=os.getenv("PROCORE_BASE_URL", "https://api.procore.com").rstrip("/"),
            procore_company_id=os.getenv("PROCORE_COMPANY_ID", ""),
            vault_root_path=os.getenv("VAULT_ROOT_PATH", "$/"),
            state_file=Path(os.getenv("SYNC_STATE_FILE", "sync_state.db")),
            dry_run=_env_bool("DRY_RUN", True) if dry_run is None else dry_run,
            request_timeout=int(os.getenv("REQUEST_TIMEOUT_SECONDS", "60")),
            verify_after_hours=int(os.getenv("VERIFY_AFTER_HOURS", "24")),
            log_file=Path(os.getenv("SYNC_LOG_FILE", "logs/vault-to-procore.log")),
            lock_file=Path(os.getenv("SYNC_LOCK_FILE", "vault-to-procore.lock")),
            retry_attempts=int(os.getenv("RETRY_ATTEMPTS", "4")),
            service_interval_seconds=int(os.getenv("SERVICE_INTERVAL_SECONDS", "900")),
            api_contract_confirmed=_env_bool("API_CONTRACT_CONFIRMED", False),
            vault_token=os.getenv("VAULT_ACCESS_TOKEN"),
            procore_token=os.getenv("PROCORE_ACCESS_TOKEN"),
        )

    def validate_for_live_run(self) -> None:
        """Provide a small credential-only check for non-CLI callers."""
        missing: list[str] = []
        for key, value in (
            ("VAULT_BASE_URL", self.vault_base_url),
            ("PROCORE_COMPANY_ID", self.procore_company_id),
            ("VAULT_ACCESS_TOKEN", self.vault_token),
            ("PROCORE_ACCESS_TOKEN", self.procore_token),
        ):
            if not value:
                missing.append(key)
        if missing:
            raise ValueError("Live sync requires: " + ", ".join(missing))


@dataclass(frozen=True)
class Project:
    """Only the Procore project fields needed for project-number matching."""
    id: str
    number: str
    name: str


@dataclass(frozen=True)
class VaultFile:
    """A Vault document and the exact source version considered by this run."""
    id: str
    name: str
    full_path: str
    version_id: str
    version_number: str
    checksum: str | None = None


@dataclass(frozen=True)
class ProcoreDocument:
    """A Procore document and any Vault identity stored with it."""
    id: str
    path: str
    version_id: str | None = None
    source_vault_file_id: str | None = None
    source_vault_version_id: str | None = None


class VaultGateway(Protocol):
    """What the sync engine needs from a Vault implementation."""
    def walk_files(self, root_path: str) -> Iterable[VaultFile]: ...
    def download(self, file: VaultFile) -> bytes: ...


class ProcoreGateway(Protocol):
    """What the sync engine needs from a Procore implementation."""
    def list_projects(self) -> Iterable[Project]: ...
    def find_document(self, project_id: str, path: str) -> ProcoreDocument | None: ...
    def upload_new(self, project_id: str, path: str, content: bytes, metadata: dict[str, str]) -> ProcoreDocument: ...
    def upload_version(self, project_id: str, document_id: str, content: bytes, metadata: dict[str, str]) -> ProcoreDocument: ...


class SyncState:
    """SQLite ledger for fast no-op decisions and operational history."""

    def __init__(self, path: Path):
        # The process lock prevents two scheduled runs from writing this database
        # at the same time, so one short-lived connection is enough here.
        self.connection = sqlite3.connect(path)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute("PRAGMA foreign_keys = ON")
        self.connection.executescript(
            """
            CREATE TABLE IF NOT EXISTS sync_records (
                vault_file_id TEXT NOT NULL,
                procore_project_id TEXT NOT NULL,
                project_number TEXT NOT NULL,
                vault_path TEXT NOT NULL,
                vault_version_id TEXT NOT NULL,
                vault_version_number TEXT NOT NULL,
                vault_checksum TEXT,
                procore_document_id TEXT NOT NULL,
                procore_version_id TEXT,
                synced_at TEXT NOT NULL,
                verified_at TEXT NOT NULL,
                PRIMARY KEY (vault_file_id, procore_project_id)
            );
            CREATE TABLE IF NOT EXISTS sync_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                action TEXT NOT NULL,
                project_number TEXT,
                vault_file_id TEXT,
                vault_version_id TEXT,
                detail TEXT
            );
            """
        )
        self.connection.commit()

    def get(self, vault_file_id: str, project_id: str) -> sqlite3.Row | None:
        """Return the most recent confirmed mapping for this file and project."""
        return self.connection.execute(
            "SELECT * FROM sync_records WHERE vault_file_id = ? AND procore_project_id = ?",
            (vault_file_id, project_id),
        ).fetchone()

    def record_success(self, project: Project, file: VaultFile, document: ProcoreDocument) -> None:
        """Record an upload only after Procore has returned successfully."""
        self._upsert(project, file, document)
        self.event("uploaded", project.number, file, document.id)
        self.connection.commit()

    def record_reconciled(self, project: Project, file: VaultFile, document: ProcoreDocument) -> None:
        """Adopt an already-correct remote file without uploading it again."""
        self._upsert(project, file, document)
        self.event("reconciled", project.number, file, document.id)
        self.connection.commit()

    def _upsert(self, project: Project, file: VaultFile, document: ProcoreDocument) -> None:
        # Vault file ID is the durable identity. A path is useful context, but it
        # is not reliable enough to identify a file after a rename or move.
        now = _utc_now()
        self.connection.execute(
            """
            INSERT INTO sync_records (
                vault_file_id, procore_project_id, project_number, vault_path,
                vault_version_id, vault_version_number, vault_checksum,
                procore_document_id, procore_version_id, synced_at, verified_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(vault_file_id, procore_project_id) DO UPDATE SET
                project_number=excluded.project_number, vault_path=excluded.vault_path,
                vault_version_id=excluded.vault_version_id,
                vault_version_number=excluded.vault_version_number,
                vault_checksum=excluded.vault_checksum,
                procore_document_id=excluded.procore_document_id,
                procore_version_id=excluded.procore_version_id,
                synced_at=excluded.synced_at, verified_at=excluded.verified_at
            """,
            (file.id, project.id, project.number, file.full_path, file.version_id,
             file.version_number, file.checksum, document.id, document.version_id, now, now),
        )

    def record_verified(self, vault_file_id: str, project_id: str) -> None:
        """Refresh the last-check time without claiming that an upload occurred."""
        self.connection.execute(
            "UPDATE sync_records SET verified_at = ? WHERE vault_file_id = ? AND procore_project_id = ?",
            (_utc_now(), vault_file_id, project_id),
        )
        self.connection.commit()

    def event(self, action: str, project_number: str | None, file: VaultFile | None, detail: str) -> None:
        """Append a compact audit event that an operator can troubleshoot later."""
        self.connection.execute(
            "INSERT INTO sync_events (occurred_at, action, project_number, vault_file_id, vault_version_id, detail) VALUES (?, ?, ?, ?, ?, ?)",
            (_utc_now(), action, project_number, file.id if file else None,
             file.version_id if file else None, detail),
        )
        self.connection.commit()

    def close(self) -> None:
        self.connection.close()


class SyncEngine:
    """Make synchronization decisions without depending on HTTP details."""

    def __init__(
        self,
        vault: VaultGateway,
        procore: ProcoreGateway,
        state: SyncState,
        *,
        dry_run: bool = True,
        verify_after_hours: int = 24,
    ):
        self.vault, self.procore, self.state, self.dry_run = vault, procore, state, dry_run
        self.verify_after = timedelta(hours=verify_after_hours)

    def run(self, root_path: str) -> dict[str, int]:
        """Run one idempotent pass and return a short operator summary."""
        counts = {"created": 0, "versioned": 0, "unchanged": 0, "skipped": 0, "conflicts": 0}
        projects = self._projects_by_number()
        LOG.info("Loaded %d uniquely numbered Procore projects", len(projects))

        for file in self.vault.walk_files(root_path):
            # Filter before any Procore lookup or download. A normal no-change run
            # should avoid the expensive work whenever the ledger can answer it.
            if not is_relevant_file(file):
                counts["skipped"] += 1
                continue
            number = extract_project_number(file.full_path)
            project = projects.get(number or "")
            if not project:
                counts["skipped"] += 1
                self.state.event("unmatched_project", number, file, file.full_path)
                continue

            record = self.state.get(file.id, project.id)
            destination = build_procore_path(file.full_path, number)
            same_recorded_version = bool(record and record["vault_version_id"] == file.version_id)
            if same_recorded_version and record is not None and not self._verification_due(record["verified_at"]):
                counts["unchanged"] += 1
                continue

            existing = self.procore.find_document(project.id, destination)
            ledger_matches_document = bool(record and existing and record["procore_document_id"] == existing.id)
            source_matches = bool(existing and existing.source_vault_file_id == file.id)

            if existing and not source_matches and not ledger_matches_document:
                # A matching path does not prove ownership. It is safer to stop
                # than to put a new version on a document somebody added by hand.
                counts["conflicts"] += 1
                owner = existing.source_vault_file_id or "an untagged/manual Procore document"
                self.state.event("conflict", number, file, f"Document {existing.id} belongs to {owner}")
                continue

            if existing and (source_matches or ledger_matches_document):
                version_matches = existing.source_vault_version_id == file.version_id
                legacy_ledger_match = bool(
                    same_recorded_version
                    and ledger_matches_document
                    and existing.source_vault_version_id is None
                )
                if version_matches or legacy_ledger_match:
                    # This also repairs local state when a prior upload succeeded
                    # remotely but its response never made it back to this process.
                    if record:
                        self.state.record_verified(file.id, project.id)
                    else:
                        self.state.record_reconciled(project, file, existing)
                    counts["unchanged"] += 1
                    continue

            action = "versioned" if existing else "created"
            if self.dry_run:
                # Dry-run can read both systems and write audit events, but it may
                # not download document bytes or modify anything in Procore.
                counts[action] += 1
                self.state.event("dry_run_" + action, number, file, destination)
                LOG.info("DRY RUN: would %s %s", action, destination)
                continue

            content = self.vault.download(file)
            checksum = hashlib.sha256(content).hexdigest()
            metadata = source_metadata(file, checksum)
            if existing:
                document = self.procore.upload_version(project.id, existing.id, content, metadata)
            else:
                document = self.procore.upload_new(project.id, destination, content, metadata)
            self.state.record_success(project, file, document)
            counts[action] += 1
        return counts

    def _projects_by_number(self) -> dict[str, Project]:
        """Build a safe lookup and reject duplicate Procore project numbers."""
        result: dict[str, Project] = {}
        ambiguous: set[str] = set()
        for project in self.procore.list_projects():
            number = normalize_project_number(project.number)
            if not number:
                continue
            if number in result:
                ambiguous.add(number)
            else:
                result[number] = project
        for number in ambiguous:
            result.pop(number, None)
            self.state.event("ambiguous_project", number, None, "Duplicate project number in Procore")
        return result

    def _verification_due(self, verified_at: str) -> bool:
        """Decide when a fast ledger answer needs a fresh Procore check."""
        try:
            verified = datetime.fromisoformat(verified_at)
        except ValueError:
            return True
        if verified.tzinfo is None:
            verified = verified.replace(tzinfo=timezone.utc)
        return datetime.now(timezone.utc) - verified >= self.verify_after


class JsonHttpClient:
    """Shared authenticated HTTP behavior with conservative retry handling."""
    def __init__(self, base_url: str, token: str | None, timeout: int = 60, retry_attempts: int = 4):
        self.base_url, self.timeout, self.retry_attempts = base_url.rstrip("/"), timeout, retry_attempts
        self.session = requests.Session()
        self.session.headers.update({"Accept": "application/json"})
        if token:
            self.session.headers.update({"Authorization": f"Bearer {token}"})

    def request(
        self,
        method: str,
        path: str,
        *,
        params: RequestParams | None = None,
        data: RequestData | None = None,
        files: RequestFiles | None = None,
        headers: Mapping[str, str] | None = None,
    ) -> requests.Response:
        """Retry safe reads; never blindly replay a write request."""
        for attempt in range(1, self.retry_attempts + 1):
            try:
                response = self.session.request(
                    method,
                    self.base_url + "/" + path.lstrip("/"),
                    timeout=self.timeout,
                    params=params,
                    data=data,
                    files=files,
                    headers=headers,
                )
                if response.status_code not in {408, 429, 500, 502, 503, 504}:
                    response.raise_for_status()
                    return response
                response.raise_for_status()
            except (requests.ConnectionError, requests.Timeout, requests.HTTPError):
                # If a POST response is lost, the server may still have accepted
                # it. Replaying that POST could create a duplicate file version.
                retry_safe = method.upper() in {"GET", "HEAD", "OPTIONS"}
                if attempt >= self.retry_attempts or not retry_safe:
                    raise
                delay = min(2 ** (attempt - 1), 30)
                LOG.warning("API request failed; retrying in %ss (attempt %s/%s)", delay, attempt, self.retry_attempts)
                time.sleep(delay)
        raise RuntimeError("unreachable")


class VaultHttpGateway(JsonHttpClient):
    """Adapter for a Vault Data API deployment; route templates are environment-configurable."""

    def walk_files(self, root_path: str) -> Iterable[VaultFile]:
        """Yield normalized file records from every page returned by Vault."""
        route = os.getenv("VAULT_FILES_ROUTE", "/folders/files")
        page: str | None = None
        while True:
            params = {"path": root_path}
            if page:
                params["page_token"] = page
            payload = _response_object(self.request("GET", route, params=params))
            for item in _object_list(payload.get("items"), "items"):
                item_type = _optional_string(item.get("type"), "file.type") or "file"
                if item_type.lower() == "file":
                    name = _required_string(item.get("name"), "file.name")
                    yield VaultFile(
                        id=_required_string(item.get("id"), "file.id"),
                        name=name,
                        full_path=_optional_string(item.get("fullName") or item.get("full_path"), "file.full_path") or name,
                        version_id=_optional_string(item.get("versionId") or item.get("version_id") or item.get("version"), "file.version_id") or "latest",
                        version_number=_optional_string(item.get("version") or item.get("versionNumber"), "file.version_number") or "latest",
                        checksum=_optional_string(item.get("checksum"), "file.checksum"),
                    )
            page = _optional_string(payload.get("next_page_token"), "next_page_token")
            if not page:
                break

    def download(self, file: VaultFile) -> bytes:
        """Download the exact version selected during the discovery pass."""
        route = os.getenv("VAULT_DOWNLOAD_ROUTE", "/files/{file_id}/download").format(file_id=file.id)
        return self.request("GET", route, params={"version": file.version_id}, headers={"Accept": "application/octet-stream"}).content


class ProcoreHttpGateway(JsonHttpClient):
    """Procore integration scaffold with normalized response parsing.

    The upload-UUID and folder workflow still needs customer integration testing,
    which is why the live safety gate defaults to off.
    """

    def __init__(self, base_url: str, token: str | None, company_id: str, timeout: int = 60, retry_attempts: int = 4):
        super().__init__(base_url, token, timeout, retry_attempts)
        self.company_id = company_id
        self.session.headers.update({"Procore-Company-Id": company_id})

    def list_projects(self) -> Iterable[Project]:
        """Page through projects visible to the integration identity."""
        route = os.getenv("PROCORE_PROJECTS_ROUTE", "/rest/v1.1/projects")
        page = 1
        while True:
            response = self.request("GET", route, params={"company_id": self.company_id, "page": page, "per_page": 100})
            items = _response_items(response)
            if not items:
                break
            for item in items:
                yield Project(
                    _required_string(item.get("id"), "project.id"),
                    _optional_string(item.get("project_number") or item.get("number"), "project.number") or "",
                    _optional_string(item.get("name"), "project.name") or "",
                )
            if len(items) < 100:
                break
            page += 1

    def find_document(self, project_id: str, path: str) -> ProcoreDocument | None:
        """Look up the destination used for reconciliation and versioning."""
        route = os.getenv("PROCORE_DOCUMENT_LOOKUP_ROUTE", "/rest/v1.0/documents")
        response = self.request("GET", route, params={"project_id": project_id, "path": path})
        items = _response_items(response)
        if not items:
            return None
        return _parse_procore_document(items[0], path)

    def upload_new(self, project_id: str, path: str, content: bytes, metadata: dict[str, str]) -> ProcoreDocument:
        """Create a document through the configured and approved route."""
        route = os.getenv("PROCORE_DOCUMENT_UPLOAD_ROUTE", "/rest/v1.0/documents")
        response = self.request("POST", route, data={"project_id": project_id, "path": path, "metadata": json.dumps(metadata)}, files={"file": (PurePosixPath(path).name, content)})
        return _parse_procore_document(_response_object(response), path)

    def upload_version(self, project_id: str, document_id: str, content: bytes, metadata: dict[str, str]) -> ProcoreDocument:
        """Append a version through the configured and approved route."""
        route = os.getenv("PROCORE_VERSION_UPLOAD_ROUTE", "/rest/v1.0/documents/{document_id}/versions").format(document_id=document_id)
        response = self.request("POST", route, data={"project_id": project_id, "metadata": json.dumps(metadata)}, files={"file": (metadata["filename"], content)})
        return _parse_procore_document(_response_object(response), metadata["source_path"])


def _parse_procore_document(item: JsonObject, path: str) -> ProcoreDocument:
    """Convert a validated API object into the engine's document model."""
    metadata_value = item.get("metadata") or item.get("custom_fields")
    metadata = _object(metadata_value, "document.metadata") if metadata_value is not None else {}
    return ProcoreDocument(
        id=_required_string(item.get("id"), "document.id"),
        path=_optional_string(item.get("path"), "document.path") or path,
        version_id=_optional_string(item.get("version_id") or item.get("current_version_id"), "document.version_id"),
        source_vault_file_id=_optional_string(metadata.get("source_vault_file_id"), "metadata.source_vault_file_id"),
        source_vault_version_id=_optional_string(metadata.get("source_vault_version_id"), "metadata.source_vault_version_id"),
    )


def _response_object(response: requests.Response) -> JsonObject:
    """Require an API response containing one JSON object."""
    return _object(cast(object, response.json()), "response")


def _response_items(response: requests.Response) -> list[JsonObject]:
    """Accept the list wrappers returned by the supported API versions."""
    value = cast(object, response.json())
    if isinstance(value, list):
        return _object_list(cast(list[object], value), "response")
    payload = _object(value, "response")
    return _object_list(payload.get("items", payload.get("data", [])), "response.items")


def _object(value: object, field: str) -> JsonObject:
    """Validate untrusted JSON before the rest of the code relies on it."""
    if not isinstance(value, dict):
        raise ValueError(f"{field} must be a JSON object")
    untyped = cast(dict[object, object], value)
    if not all(isinstance(key, str) for key in untyped):
        raise ValueError(f"{field} must have string keys")
    return {str(key): item for key, item in untyped.items()}


def _object_list(value: object, field: str) -> list[JsonObject]:
    """Validate a JSON array and each object contained in it."""
    if not isinstance(value, list):
        raise ValueError(f"{field} must be a JSON array")
    return [_object(item, f"{field}[]") for item in cast(list[object], value)]


def _optional_string(value: object, field: str) -> str | None:
    """Normalize string-like identifiers without hiding bad structures."""
    if value is None:
        return None
    if isinstance(value, (str, int)):
        return str(value)
    raise ValueError(f"{field} must be a string or number")


def _required_string(value: object, field: str) -> str:
    """Read a required value and report its field name when it is missing."""
    result = _optional_string(value, field)
    if result is None or not result:
        raise ValueError(f"{field} is required")
    return result


def normalize_project_number(value: str) -> str | None:
    """Return the first standalone five- or six-digit project number."""
    match = re.search(r"(?<!\d)(\d{5,6})(?!\d)", value or "")
    return match.group(1) if match else None


def extract_project_number(path_or_name: str) -> str | None:
    """Name project-number extraction in the terms used by the sync engine."""
    return normalize_project_number(path_or_name)


def is_relevant_file(file: VaultFile) -> bool:
    """Apply the approved extension and exact folder-marker rules."""
    if Path(file.name).suffix.lower() not in DEFAULT_ALLOWED_EXTS:
        return False
    components = {component.upper() for component in re.split(r"[\\/]", file.full_path)}
    return bool(components & DEFAULT_PATH_MARKERS)


def build_procore_path(vault_path: str, project_number: str | None = None) -> str:
    """Remove Vault/project roots while keeping the useful folder structure."""
    normalized = vault_path.replace("\\", "/").removeprefix("$/").strip("/")
    parts = [part for part in normalized.split("/") if part not in ("", ".", "..")]
    if project_number:
        for index, part in enumerate(parts):
            if extract_project_number(part) == project_number:
                parts = parts[index + 1:]
                break
    return "/".join(parts)


def source_metadata(file: VaultFile, checksum: str) -> dict[str, str]:
    """Build the source and version trail supplied with every Procore write."""
    return {
        "filename": file.name,
        "source": "Autodesk Vault",
        "source_path": file.full_path,
        "source_vault_file_id": file.id,
        "source_vault_version_id": file.version_id,
        "source_vault_version_number": file.version_number,
        "source_sha256": checksum,
        "version_comment": f"Autodesk Vault version {file.version_number} (ID {file.version_id})",
        "synced_at": _utc_now(),
    }


def _env_bool(name: str, default: bool) -> bool:
    """Read the common human spellings used for environment booleans."""
    raw = os.getenv(name)
    return default if raw is None else raw.strip().lower() in {"1", "true", "yes", "on"}


def load_env_file(path: Path) -> None:
    """Load simple KEY=VALUE settings without overriding the process environment."""
    if not path.is_file():
        return
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key:
            os.environ.setdefault(key, value)


def _utc_now() -> str:
    """Use timezone-aware timestamps everywhere the ledger records time."""
    return datetime.now(timezone.utc).isoformat()


class AlreadyRunningError(RuntimeError):
    """Raised when a scheduled run overlaps an active run."""
    pass


class WindowsProcessApi(Protocol):
    """The small Windows API surface needed for a safe PID check."""
    def OpenProcess(self, desired_access: int, inherit_handle: bool, process_id: int) -> int: ...
    def WaitForSingleObject(self, handle: int, milliseconds: int) -> int: ...
    def CloseHandle(self, handle: int) -> bool: ...


class SingleInstanceLock:
    """Portable lock based on exclusive file creation, with stale-PID recovery.

    The owner PID lets a future scheduled run recover cleanly after a crash.
    """

    def __init__(self, path: Path):
        self.path = path
        self.acquired = False

    def __enter__(self):
        """Take the lock or identify the process that already owns it."""
        self.path.parent.mkdir(parents=True, exist_ok=True)
        try:
            descriptor = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            try:
                pid = int(self.path.read_text(encoding="utf-8").strip())
            except (OSError, ValueError):
                pid = -1
            if pid > 0 and _pid_is_running(pid):
                raise AlreadyRunningError(f"Another sync process is active (PID {pid})")
            with contextlib.suppress(OSError):
                self.path.unlink()
            descriptor = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            handle.write(str(os.getpid()))
        self.acquired = True
        return self

    def __exit__(self, *_):
        """Remove only the lock this instance successfully acquired."""
        if self.acquired:
            with contextlib.suppress(OSError):
                self.path.unlink()


def _pid_is_running(pid: int) -> bool:
    """Check a PID without sending it a real signal on Windows."""
    if os.name == "nt":
        # os.kill(pid, 0) is not a portable no-signal probe on Windows.
        kernel32 = cast(WindowsProcessApi, ctypes.WinDLL("kernel32", use_last_error=True))
        synchronize = 0x00100000
        wait_timeout = 0x00000102
        handle = kernel32.OpenProcess(synchronize, False, pid)
        if not handle:
            return ctypes.get_last_error() == 5  # Access denied means the process exists.
        try:
            return kernel32.WaitForSingleObject(handle, 0) == wait_timeout
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except (ProcessLookupError, OSError):
        return False


def configure_logging(path: Path, verbose: bool = False) -> None:
    """Log to the console and bounded rotating files for unattended runs."""
    path.parent.mkdir(parents=True, exist_ok=True)
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    handlers: list[logging.Handler] = [logging.StreamHandler()]
    file_handler = RotatingFileHandler(path, maxBytes=5_000_000, backupCount=5, encoding="utf-8")
    handlers.append(file_handler)
    for handler in handlers:
        handler.setFormatter(formatter)
    logging.basicConfig(level=logging.DEBUG if verbose else logging.INFO, handlers=handlers, force=True)


def validate_configuration(settings: Settings, live: bool) -> list[str]:
    """Return all startup problems together so setup is not trial and error."""
    problems: list[str] = []
    if not settings.vault_base_url:
        problems.append("VAULT_BASE_URL is missing")
    if not settings.procore_company_id:
        problems.append("PROCORE_COMPANY_ID is missing")
    if live and not settings.vault_token:
        problems.append("VAULT_ACCESS_TOKEN is missing")
    if live and not settings.procore_token:
        problems.append("PROCORE_ACCESS_TOKEN is missing")
    if live and not settings.api_contract_confirmed:
        problems.append(
            "API_CONTRACT_CONFIRMED must be true after the Vault routes and Procore upload workflow pass integration testing"
        )
    if settings.retry_attempts < 1:
        problems.append("RETRY_ATTEMPTS must be at least 1")
    if settings.service_interval_seconds < 60:
        problems.append("SERVICE_INTERVAL_SECONDS must be at least 60")
    return problems


def run_once(settings: Settings) -> dict[str, int]:
    """Run one pass and always release the SQLite connection afterward."""
    state = SyncState(settings.state_file)
    try:
        vault = VaultHttpGateway(settings.vault_base_url, settings.vault_token, settings.request_timeout, settings.retry_attempts)
        procore = ProcoreHttpGateway(settings.procore_base_url, settings.procore_token, settings.procore_company_id, settings.request_timeout, settings.retry_attempts)
        return SyncEngine(
            vault,
            procore,
            state,
            dry_run=settings.dry_run,
            verify_after_hours=settings.verify_after_hours,
        ).run(settings.vault_root_path)
    finally:
        state.close()


def main(argv: list[str] | None = None) -> int:
    """Entry point used by people, schedulers, and service wrappers."""
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--dry-run", action="store_true", help="Plan changes without downloading or uploading")
    mode.add_argument("--live", action="store_true", help="Perform uploads (requires credentials)")
    parser.add_argument("--service", action="store_true", help="Run continuously at SERVICE_INTERVAL_SECONDS")
    parser.add_argument("--validate-config", action="store_true", help="Validate configuration and exit")
    parser.add_argument("--status", action="store_true", help="Print synchronization record/event counts and exit")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)
    settings = Settings.from_env(dry_run=not args.live)
    configure_logging(settings.log_file, args.verbose)
    if args.status:
        state = SyncState(settings.state_file)
        try:
            records = state.connection.execute("SELECT COUNT(*) FROM sync_records").fetchone()[0]
            events = state.connection.execute("SELECT COUNT(*) FROM sync_events").fetchone()[0]
            latest = state.connection.execute("SELECT MAX(occurred_at) FROM sync_events").fetchone()[0]
            print(json.dumps({"records": records, "events": events, "latest_event": latest}, indent=2))
            return 0
        finally:
            state.close()
    problems = validate_configuration(settings, live=not settings.dry_run)
    if args.validate_config:
        print("Configuration valid" if not problems else "\n".join(problems))
        return 0 if not problems else 1
    if problems:
        raise ValueError("; ".join(problems))
    with SingleInstanceLock(settings.lock_file):
        while True:
            result = run_once(settings)
            LOG.info("Sync result: %s", result)
            if not args.service:
                return 0 if result["conflicts"] == 0 else 2
            LOG.info("Next synchronization in %s seconds", settings.service_interval_seconds)
            time.sleep(settings.service_interval_seconds)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (requests.RequestException, ValueError, AlreadyRunningError) as exc:
        LOG.error("Sync failed: %s", exc)
        raise SystemExit(1)

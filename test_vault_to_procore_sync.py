import os
import tempfile
import unittest
from pathlib import Path
from collections.abc import Iterator

from vault_to_procore_sync import (
    Project, ProcoreDocument, SyncEngine, SyncState, VaultFile,
    AlreadyRunningError, SingleInstanceLock, build_procore_path,
    extract_project_number, is_relevant_file,
)


class FakeVault:
    """Small in-memory Vault used to prove the engine does not need HTTP."""
    def __init__(self, files: list[VaultFile]) -> None:
        self.files: list[VaultFile] = files
        self.downloads: int = 0

    def walk_files(self, root_path: str) -> Iterator[VaultFile]:
        return iter(self.files)

    def download(self, file: VaultFile) -> bytes:
        self.downloads += 1
        return b"document"


class FakeProcore:
    """Records requested writes so each test can inspect exactly what happened."""
    def __init__(
        self,
        projects: list[Project],
        documents: dict[tuple[str, str], ProcoreDocument] | None = None,
    ) -> None:
        self.projects: list[Project] = projects
        self.documents: dict[tuple[str, str], ProcoreDocument] = documents or {}
        self.created: list[tuple[str, str, dict[str, str]]] = []
        self.versioned: list[tuple[str, str, dict[str, str]]] = []

    def list_projects(self) -> Iterator[Project]:
        return iter(self.projects)

    def find_document(self, project_id: str, path: str) -> ProcoreDocument | None:
        return self.documents.get((project_id, path))

    def upload_new(
        self,
        project_id: str,
        path: str,
        content: bytes,
        metadata: dict[str, str],
    ) -> ProcoreDocument:
        self.created.append((project_id, path, metadata))
        return ProcoreDocument("doc-1", path, "pv-1", metadata["source_vault_file_id"], metadata["source_vault_version_id"])

    def upload_version(
        self,
        project_id: str,
        document_id: str,
        content: bytes,
        metadata: dict[str, str],
    ) -> ProcoreDocument:
        self.versioned.append((project_id, document_id, metadata))
        return ProcoreDocument(document_id, metadata["source_path"], "pv-2", metadata["source_vault_file_id"], metadata["source_vault_version_id"])


class SyncTests(unittest.TestCase):
    """Cover the decisions that protect files from duplicates and overwrites."""
    temp: tempfile.TemporaryDirectory[str]
    state: SyncState
    file: VaultFile
    project: Project

    def setUp(self) -> None:
        # Every test gets a new ledger. State leaking between tests would make the
        # no-op and version checks look more reliable than they really are.
        self.temp = tempfile.TemporaryDirectory[str]()
        self.state = SyncState(Path(self.temp.name) / "state.db")
        self.file = VaultFile("vf-1", "drawing.pdf", "$/103002 - Store/GCC/Plans/drawing.pdf", "vv-1", "1")
        self.project = Project("p-1", "103002", "Store")

    def tearDown(self) -> None:
        self.state.close()
        self.temp.cleanup()

    def test_matching_and_path_rules(self) -> None:
        """Project extraction, folder filtering, and path trimming stay aligned."""
        self.assertEqual(extract_project_number(self.file.full_path), "103002")
        self.assertTrue(is_relevant_file(self.file))
        self.assertEqual(build_procore_path(self.file.full_path, "103002"), "GCC/Plans/drawing.pdf")

    def test_new_file_is_uploaded_and_recorded(self) -> None:
        """A first-time Vault file creates one document and one ledger record."""
        vault, procore = FakeVault([self.file]), FakeProcore([self.project])
        result = SyncEngine(vault, procore, self.state, dry_run=False).run("$/")
        self.assertEqual(result["created"], 1)
        self.assertEqual(len(procore.created), 1)
        record = self.state.get("vf-1", "p-1")
        assert record is not None
        self.assertEqual(record["vault_version_id"], "vv-1")

    def test_same_version_does_nothing_without_procore_lookup_or_download(self) -> None:
        """The common no-change path avoids the expensive work entirely."""
        vault, procore = FakeVault([self.file]), FakeProcore([self.project])
        SyncEngine(vault, procore, self.state, dry_run=False).run("$/")
        second_vault = FakeVault([self.file])
        second_procore = FakeProcore([self.project])
        result = SyncEngine(second_vault, second_procore, self.state, dry_run=False).run("$/")
        self.assertEqual(result["unchanged"], 1)
        self.assertEqual(second_vault.downloads, 0)
        self.assertEqual(second_procore.created, [])

    def test_new_vault_version_appends_procore_version(self) -> None:
        """A changed Vault version updates the existing Procore document."""
        existing = ProcoreDocument("doc-1", "GCC/Plans/drawing.pdf", "pv-1", "vf-1", "vv-1")
        updated = VaultFile(self.file.id, self.file.name, self.file.full_path, "vv-2", "2")
        vault = FakeVault([updated])
        procore = FakeProcore([self.project], {("p-1", "GCC/Plans/drawing.pdf"): existing})
        result = SyncEngine(vault, procore, self.state, dry_run=False).run("$/")
        self.assertEqual(result["versioned"], 1)
        self.assertEqual(len(procore.versioned), 1)

    def test_dry_run_never_downloads(self) -> None:
        """Dry-run reports the action without touching document bytes."""
        vault, procore = FakeVault([self.file]), FakeProcore([self.project])
        result = SyncEngine(vault, procore, self.state, dry_run=True).run("$/")
        self.assertEqual(result["created"], 1)
        self.assertEqual(vault.downloads, 0)

    def test_existing_matching_version_is_reconciled_without_upload(self) -> None:
        """Remote truth repairs a missing ledger instead of creating a duplicate."""
        existing = ProcoreDocument("doc-1", "GCC/Plans/drawing.pdf", "pv-1", "vf-1", "vv-1")
        vault = FakeVault([self.file])
        procore = FakeProcore([self.project], {("p-1", "GCC/Plans/drawing.pdf"): existing})
        result = SyncEngine(vault, procore, self.state, dry_run=False).run("$/")
        self.assertEqual(result["unchanged"], 1)
        self.assertEqual(vault.downloads, 0)
        self.assertIsNotNone(self.state.get("vf-1", "p-1"))

    def test_untagged_existing_document_is_a_conflict(self) -> None:
        """A same-name manual document is left alone for a person to resolve."""
        existing = ProcoreDocument("manual-1", "GCC/Plans/drawing.pdf")
        vault = FakeVault([self.file])
        procore = FakeProcore([self.project], {("p-1", "GCC/Plans/drawing.pdf"): existing})
        result = SyncEngine(vault, procore, self.state, dry_run=False).run("$/")
        self.assertEqual(result["conflicts"], 1)
        self.assertEqual(vault.downloads, 0)
        self.assertEqual(procore.created, [])

    def test_stale_ledger_recreates_a_missing_procore_document(self) -> None:
        """Periodic verification catches a document removed outside the connector."""
        first_vault, first_procore = FakeVault([self.file]), FakeProcore([self.project])
        SyncEngine(first_vault, first_procore, self.state, dry_run=False).run("$/")
        self.state.connection.execute(
            "UPDATE sync_records SET verified_at = ? WHERE vault_file_id = ?",
            ("2000-01-01T00:00:00+00:00", self.file.id),
        )
        self.state.connection.commit()

        second_vault, second_procore = FakeVault([self.file]), FakeProcore([self.project])
        result = SyncEngine(second_vault, second_procore, self.state, dry_run=False).run("$/")
        self.assertEqual(result["created"], 1)
        self.assertEqual(len(second_procore.created), 1)

    def test_single_instance_lock_prevents_overlap_and_cleans_up(self) -> None:
        """A second scheduled run cannot overlap the first one."""
        lock_path = Path(self.temp.name) / "sync.lock"
        with SingleInstanceLock(lock_path):
            self.assertEqual(lock_path.read_text(encoding="utf-8"), str(os.getpid()))
            with self.assertRaises(AlreadyRunningError):
                with SingleInstanceLock(lock_path):
                    pass
        self.assertFalse(lock_path.exists())


if __name__ == "__main__":
    unittest.main()

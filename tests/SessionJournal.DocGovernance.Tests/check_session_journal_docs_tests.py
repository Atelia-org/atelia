from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO_ROOT = Path(__file__).resolve().parents[2]
CHECKER = REPO_ROOT / "scripts/check_session_journal_docs.py"
FIXTURES = Path(__file__).with_name("fixtures")
SCOPE_PATH = "docs/SessionJournal/session-journal-doc-check-scope.txt"


class SessionJournalDocCheckerTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.repo = Path(self._temporary.name)
        self._git("init", "-q")
        self._git("config", "user.email", "doc-check@example.invalid")
        self._git("config", "user.name", "Doc Check")

    def tearDown(self) -> None:
        self._temporary.cleanup()

    def _git(self, *arguments: str) -> None:
        subprocess.run(
            ["git", "-C", os.fspath(self.repo), *arguments],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

    def _install_fixture(
        self,
        name: str,
        scope_entries: tuple[str, ...] = ("docs/SessionJournal/README.md",),
    ) -> None:
        shutil.copytree(FIXTURES / name, self.repo, dirs_exist_ok=True)
        scope = self.repo / SCOPE_PATH
        scope.parent.mkdir(parents=True, exist_ok=True)
        scope.write_text("\n".join(scope_entries) + "\n", encoding="utf-8")
        self._git("add", "--", ".")

    def _run(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                sys.executable,
                os.fspath(CHECKER),
                "--repo-root",
                os.fspath(self.repo),
                "--scope",
                SCOPE_PATH,
                *arguments,
            ],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )

    def test_valid_links_duplicate_roles_and_fenced_fake_link(self) -> None:
        self._install_fixture("valid")

        result = self._run()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("SUMMARY files=1 diagnostics=0 mode=scoped", result.stdout)

    def test_missing_target_is_nonzero(self) -> None:
        self._install_fixture("missing_target")

        result = self._run()

        self.assertEqual(1, result.returncode)
        self.assertIn("MISSING_TARGET docs/SessionJournal/README.md:1", result.stdout)

    def test_case_mismatch_is_distinct_from_missing(self) -> None:
        self._install_fixture("case_mismatch")

        result = self._run()

        self.assertEqual(1, result.returncode)
        self.assertIn("CASE_MISMATCH docs/SessionJournal/README.md:1", result.stdout)
        self.assertNotIn("MISSING_TARGET", result.stdout)

    def test_repo_escape_is_rejected(self) -> None:
        self._install_fixture("repo_escape")

        result = self._run()

        self.assertEqual(1, result.returncode)
        self.assertIn("REPO_ESCAPE docs/SessionJournal/README.md:1", result.stdout)

    def test_duplicate_current_owner_is_reported_for_each_owner(self) -> None:
        self._install_fixture("duplicate_current_owner")

        result = self._run()

        self.assertEqual(1, result.returncode)
        self.assertEqual(2, result.stdout.count("DUPLICATE_CURRENT_OWNER"))

    def test_short_and_missing_baselines_are_rejected(self) -> None:
        self._install_fixture("invalid_baselines")

        result = self._run()

        self.assertEqual(1, result.returncode)
        self.assertEqual(2, result.stdout.count("MISSING_BASELINE"))

    def test_closed_lifecycle_cannot_enter_current_ledger(self) -> None:
        self._install_fixture("noncurrent_in_current")

        result = self._run()

        self.assertEqual(1, result.returncode)
        self.assertIn("NONCURRENT_IN_CURRENT_LEDGER", result.stdout)

    def test_untracked_markdown_is_never_read_by_scoped_mode(self) -> None:
        self._install_fixture("valid")
        untracked = self.repo / "docs/SessionJournal/untracked-review.md"
        untracked.write_text("[not read](missing.md)\n", encoding="utf-8")

        result = self._run()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("untracked-review.md", result.stdout)

    def test_all_tracked_report_only_observes_noise_and_returns_zero(self) -> None:
        self._install_fixture(
            "all_tracked_noise",
            scope_entries=("docs/SessionJournal/README.md",),
        )

        scoped = self._run()
        report = self._run("--all-tracked", "--report-only")

        self.assertEqual(0, scoped.returncode, scoped.stdout + scoped.stderr)
        self.assertEqual(0, report.returncode, report.stdout + report.stderr)
        self.assertIn(
            "MISSING_TARGET docs/SessionJournal/historical.md:1", report.stdout
        )
        self.assertIn("mode=all-tracked", report.stdout)

    def test_non_report_all_tracked_returns_nonzero(self) -> None:
        self._install_fixture("all_tracked_noise")

        result = self._run("--all-tracked")

        self.assertEqual(1, result.returncode)
        self.assertIn("MISSING_TARGET", result.stdout)


if __name__ == "__main__":
    unittest.main()

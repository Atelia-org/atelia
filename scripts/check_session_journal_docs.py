#!/usr/bin/env python3
"""Read-only structural checks for governed SessionJournal Markdown.

The default mode reads only the explicit tracked scope.  The optional
``--all-tracked`` mode is intentionally observational: it discovers the
tracked SessionJournal Markdown corpus from Git, never from the filesystem.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import os
from pathlib import Path, PurePosixPath
import posixpath
import re
import stat
import subprocess
import sys
from urllib.parse import unquote


CURRENT_LEDGER_HEADING = "## Current verified claim ledger"
CLOSED_LEDGER_HEADING = "## Normative、frozen 与 closed entries"
DEFAULT_SCOPE = PurePosixPath(
    "docs/SessionJournal/session-journal-doc-check-scope.txt"
)
LINK_PATTERN = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
REFERENCE_PATTERN = re.compile(r"^\s*\[[^\]]+\]:\s*(\S+)")
FULL_COMMIT_PATTERN = re.compile(
    r"(?<![0-9a-fA-F])[0-9a-fA-F]{40}(?![0-9a-fA-F])"
)
CLAIM_ID_PATTERN = re.compile(r"^`([^`]+)`$")
FENCE_PATTERN = re.compile(r"^\s*(`{3,}|~{3,})")
IGNORED_SCHEMES = ("http://", "https://", "mailto:")


@dataclass(frozen=True, order=True)
class Diagnostic:
    code: str
    path: str
    line: int
    detail: str

    def render(self) -> str:
        return f"{self.code} {self.path}:{self.line} {self.detail}"


@dataclass(frozen=True)
class LedgerRow:
    claim_id: str
    path: str
    line: int
    lifecycle_cell: str
    raw_line: str
    table: str

    @property
    def is_current_owner(self) -> bool:
        if self.table == "current":
            return True
        return bool(re.search(r"`current`|\bcurrent\b", self.lifecycle_cell))


def _git(repo_root: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", "-C", os.fspath(repo_root), *arguments],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip()
        raise RuntimeError(detail or f"git {' '.join(arguments)} failed")
    return completed.stdout


def _repo_root(value: str | None) -> Path:
    candidate = Path(value).absolute() if value else Path.cwd().absolute()
    root = _git(candidate, "rev-parse", "--show-toplevel").strip()
    return Path(root).absolute()


def _tracked_paths(repo_root: Path) -> set[str]:
    output = subprocess.run(
        ["git", "-C", os.fspath(repo_root), "ls-files", "-z"],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if output.returncode != 0:
        detail = output.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(detail or "git ls-files failed")
    return {
        item.decode("utf-8", errors="strict")
        for item in output.stdout.split(b"\0")
        if item
    }


def _tracked_directories(tracked: set[str]) -> set[str]:
    directories: set[str] = {"."}
    for path in tracked:
        parent = PurePosixPath(path).parent
        while parent != PurePosixPath("."):
            directories.add(parent.as_posix())
            parent = parent.parent
    return directories


def _normalize_repo_path(raw_path: str) -> str | None:
    if not raw_path or raw_path.startswith("/"):
        return None
    normalized = posixpath.normpath(raw_path)
    if normalized == ".." or normalized.startswith("../"):
        return None
    return PurePosixPath(normalized).as_posix()


def _read_tracked_text(
    repo_root: Path,
    relative_path: str,
    diagnostics: list[Diagnostic],
    line: int = 1,
) -> str | None:
    path = repo_root / PurePosixPath(relative_path)
    try:
        current = repo_root
        for part in PurePosixPath(relative_path).parts:
            current = current / part
            mode = current.lstat().st_mode
            if stat.S_ISLNK(mode):
                diagnostics.append(Diagnostic(
                    "SOURCE_NOT_REGULAR",
                    relative_path,
                    line,
                    "tracked source or ancestor is a symbolic link",
                ))
                return None
        if not stat.S_ISREG(path.lstat().st_mode):
            diagnostics.append(Diagnostic(
                "SOURCE_NOT_REGULAR",
                relative_path,
                line,
                "tracked source is not a regular file",
            ))
            return None
        return path.read_text(encoding="utf-8")
    except FileNotFoundError:
        diagnostics.append(Diagnostic(
            "TRACKED_SOURCE_MISSING",
            relative_path,
            line,
            "tracked source is missing from the worktree",
        ))
    except UnicodeDecodeError:
        diagnostics.append(Diagnostic(
            "INVALID_UTF8",
            relative_path,
            line,
            "tracked Markdown is not valid UTF-8",
        ))
    return None


def _scope_paths(
    repo_root: Path,
    scope_path: str,
    tracked: set[str],
    diagnostics: list[Diagnostic],
) -> list[str]:
    normalized_scope = _normalize_repo_path(scope_path)
    if normalized_scope is None:
        diagnostics.append(Diagnostic(
            "INVALID_SCOPE_PATH", scope_path, 1, "scope path escapes repo"
        ))
        return []
    if normalized_scope not in tracked:
        diagnostics.append(Diagnostic(
            "UNTRACKED_SCOPE_FILE",
            normalized_scope,
            1,
            "scope file must be tracked before it can be read",
        ))
        return []
    text = _read_tracked_text(
        repo_root, normalized_scope, diagnostics
    )
    if text is None:
        return []

    selected: list[str] = []
    seen: set[str] = set()
    for line_number, raw_line in enumerate(text.splitlines(), start=1):
        entry = raw_line.strip()
        if not entry or entry.startswith("#"):
            continue
        normalized = _normalize_repo_path(entry)
        if normalized is None:
            diagnostics.append(Diagnostic(
                "INVALID_SCOPE_ENTRY",
                normalized_scope,
                line_number,
                f"entry escapes repo: {entry}",
            ))
            continue
        if normalized not in tracked:
            diagnostics.append(Diagnostic(
                "UNTRACKED_SCOPE_ENTRY",
                normalized_scope,
                line_number,
                f"entry is not tracked: {normalized}",
            ))
            continue
        if not normalized.lower().endswith(".md"):
            diagnostics.append(Diagnostic(
                "NON_MARKDOWN_SCOPE_ENTRY",
                normalized_scope,
                line_number,
                f"entry is not Markdown: {normalized}",
            ))
            continue
        if normalized not in seen:
            selected.append(normalized)
            seen.add(normalized)
    return selected


def _is_session_journal_markdown(path: str) -> bool:
    if not path.lower().endswith(".md"):
        return False
    if path.startswith("docs/SessionJournal/"):
        return True
    return bool(re.fullmatch(
        r"prototypes/SessionJournal(?:\.[^/]+)?/README\.md", path
    ))


def _markdown_without_fences(text: str) -> list[tuple[int, str]]:
    result: list[tuple[int, str]] = []
    fence_marker: str | None = None
    fence_length = 0
    for line_number, line in enumerate(text.splitlines(), start=1):
        match = FENCE_PATTERN.match(line)
        if match:
            marker = match.group(1)
            if fence_marker is None:
                fence_marker = marker[0]
                fence_length = len(marker)
                continue
            if marker[0] == fence_marker and len(marker) >= fence_length:
                fence_marker = None
                fence_length = 0
                continue
        if fence_marker is None:
            result.append((line_number, line))
    return result


def _destination(raw_destination: str) -> str:
    destination = raw_destination.strip()
    if destination.startswith("<"):
        closing = destination.find(">")
        if closing >= 0:
            return destination[1:closing]
    return destination.split(maxsplit=1)[0] if destination else ""


def _local_destinations(line: str) -> list[str]:
    destinations = [
        _destination(match.group(1)) for match in LINK_PATTERN.finditer(line)
    ]
    reference = REFERENCE_PATTERN.match(line)
    if reference:
        destinations.append(_destination(reference.group(1)))
    return destinations


def _check_link(
    source_path: str,
    line_number: int,
    raw_destination: str,
    tracked_targets: set[str],
    target_casefold: dict[str, list[str]],
    diagnostics: list[Diagnostic],
) -> None:
    if not raw_destination:
        return
    lowered = raw_destination.lower()
    if raw_destination.startswith("#") or lowered.startswith(IGNORED_SCHEMES):
        return

    decoded = unquote(raw_destination)
    target_without_fragment = decoded.split("#", 1)[0].split("?", 1)[0]
    if not target_without_fragment:
        return
    if target_without_fragment.startswith("/"):
        diagnostics.append(Diagnostic(
            "REPO_ESCAPE",
            source_path,
            line_number,
            f"absolute target is outside the relative-link contract: {raw_destination}",
        ))
        return

    joined = posixpath.normpath(posixpath.join(
        PurePosixPath(source_path).parent.as_posix(),
        target_without_fragment,
    ))
    if joined == ".." or joined.startswith("../"):
        diagnostics.append(Diagnostic(
            "REPO_ESCAPE",
            source_path,
            line_number,
            f"resolved target escapes repo: {raw_destination}",
        ))
        return

    normalized = PurePosixPath(joined).as_posix()
    if normalized in tracked_targets:
        return
    case_matches = target_casefold.get(normalized.casefold(), [])
    if case_matches:
        diagnostics.append(Diagnostic(
            "CASE_MISMATCH",
            source_path,
            line_number,
            f"target {raw_destination} differs from tracked {' or '.join(case_matches)}",
        ))
        return
    diagnostics.append(Diagnostic(
        "MISSING_TARGET",
        source_path,
        line_number,
        f"target is not tracked: {raw_destination}",
    ))


def _parse_ledger_rows(path: str, text: str) -> list[LedgerRow]:
    rows: list[LedgerRow] = []
    active_table: str | None = None
    for line_number, line in _markdown_without_fences(text):
        if line.startswith("## "):
            if line == CURRENT_LEDGER_HEADING:
                active_table = "current"
            elif line == CLOSED_LEDGER_HEADING:
                active_table = "closed"
            else:
                active_table = None
            continue
        if active_table is None or not line.lstrip().startswith("|"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 2:
            continue
        claim_match = CLAIM_ID_PATTERN.match(cells[0])
        if not claim_match or claim_match.group(1) == "claim_id":
            continue
        lifecycle_index = 2 if active_table == "current" else 1
        lifecycle = cells[lifecycle_index] if len(cells) > lifecycle_index else ""
        rows.append(LedgerRow(
            claim_match.group(1), path, line_number, lifecycle, line, active_table
        ))
    return rows


def _check_ledgers(
    rows: list[LedgerRow], diagnostics: list[Diagnostic]
) -> None:
    current_owners: dict[str, list[LedgerRow]] = {}
    for row in rows:
        if row.table == "current":
            forbidden = [
                state for state in ("closed", "frozen", "deferred")
                if re.search(rf"`{state}`|\b{state}\b", row.lifecycle_cell)
            ]
            if forbidden:
                diagnostics.append(Diagnostic(
                    "NONCURRENT_IN_CURRENT_LEDGER",
                    row.path,
                    row.line,
                    f"claim {row.claim_id} has lifecycle {','.join(forbidden)}",
                ))
            if not FULL_COMMIT_PATTERN.search(row.raw_line):
                diagnostics.append(Diagnostic(
                    "MISSING_BASELINE",
                    row.path,
                    row.line,
                    f"current claim {row.claim_id} lacks a full 40-hex baseline",
                ))
        if row.is_current_owner:
            current_owners.setdefault(row.claim_id, []).append(row)

    for claim_id, owners in current_owners.items():
        if len(owners) <= 1:
            continue
        locations = ", ".join(
            f"{owner.path}:{owner.line}" for owner in owners
        )
        for owner in owners:
            diagnostics.append(Diagnostic(
                "DUPLICATE_CURRENT_OWNER",
                owner.path,
                owner.line,
                f"claim {claim_id} has current owners at {locations}",
            ))


def run_checks(
    repo_root: Path,
    scope_path: str,
    all_tracked: bool,
) -> tuple[list[Diagnostic], int]:
    diagnostics: list[Diagnostic] = []
    tracked = _tracked_paths(repo_root)
    directories = _tracked_directories(tracked)
    tracked_targets = tracked | directories
    target_casefold: dict[str, list[str]] = {}
    for target in sorted(tracked_targets):
        target_casefold.setdefault(target.casefold(), []).append(target)

    if all_tracked:
        selected = sorted(path for path in tracked if _is_session_journal_markdown(path))
    else:
        selected = _scope_paths(
            repo_root, scope_path, tracked, diagnostics
        )

    ledger_rows: list[LedgerRow] = []
    read_count = 0
    for path in selected:
        text = _read_tracked_text(repo_root, path, diagnostics)
        if text is None:
            continue
        read_count += 1
        for line_number, line in _markdown_without_fences(text):
            for destination in _local_destinations(line):
                _check_link(
                    path,
                    line_number,
                    destination,
                    tracked_targets,
                    target_casefold,
                    diagnostics,
                )
        ledger_rows.extend(_parse_ledger_rows(path, text))

    _check_ledgers(ledger_rows, diagnostics)
    return sorted(diagnostics), read_count


def _arguments(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--repo-root",
        help="repository path; defaults to the current Git worktree",
    )
    parser.add_argument(
        "--scope",
        default=DEFAULT_SCOPE.as_posix(),
        help="tracked explicit scope file used by default mode",
    )
    parser.add_argument(
        "--all-tracked",
        action="store_true",
        help="scan the entire tracked SessionJournal Markdown corpus",
    )
    parser.add_argument(
        "--report-only",
        action="store_true",
        help="report diagnostics but return success",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _arguments(argv if argv is not None else sys.argv[1:])
    try:
        repo_root = _repo_root(args.repo_root)
        diagnostics, read_count = run_checks(
            repo_root, args.scope, args.all_tracked
        )
    except (OSError, RuntimeError, UnicodeError) as exception:
        print(f"CHECKER_ERROR .:1 {exception}")
        return 1

    for diagnostic in diagnostics:
        print(diagnostic.render())
    mode = "all-tracked" if args.all_tracked else "scoped"
    print(
        f"SUMMARY files={read_count} diagnostics={len(diagnostics)} mode={mode}"
    )
    if diagnostics and not args.report_only:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

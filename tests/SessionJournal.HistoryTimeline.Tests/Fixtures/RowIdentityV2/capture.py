"""Capture read-only SQL/file evidence from the old generator's repository ZIP.

Usage: python3 capture.py GENERATED_DIRECTORY NEW_FIXTURE_DIRECTORY
Run only after the old production generator has closed every repository handle.
"""
import base64
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import sys
import tempfile
import zipfile


def capture(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=False)
    for name in ("repository.zip", "control-backup.zip", "expected.json", "commands.json"):
        shutil.copy2(source / name, destination / name)
    with tempfile.TemporaryDirectory(prefix="atelia-row-fixture-inspect-") as temporary:
        root = Path(temporary)
        with zipfile.ZipFile(destination / "repository.zip") as archive:
            archive.extractall(root)
        databases = []
        for path in sorted(root.rglob("*.sqlite")):
            connection = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)
            tables = []
            names = connection.execute(
                "SELECT name FROM sqlite_schema WHERE type='table' "
                "AND name NOT LIKE 'sqlite_%' ORDER BY name"
            ).fetchall()
            for (name,) in names:
                quoted = '"' + name.replace('"', '""') + '"'
                columns = [row[1] for row in connection.execute("PRAGMA table_info(" + quoted + ")")]
                rows = [
                    [{"base64": base64.b64encode(value).decode()} if isinstance(value, bytes) else value
                     for value in row]
                    for row in connection.execute("SELECT * FROM " + quoted)
                ]
                rows.sort(key=lambda row: json.dumps(row, ensure_ascii=True))
                tables.append({"name": name, "columns": columns, "rows": rows})
            databases.append({
                "relativePath": str(path.relative_to(root)),
                "userVersion": connection.execute("PRAGMA user_version").fetchone()[0],
                "tables": tables,
            })
            connection.close()
        files = [{
            "path": str(path.relative_to(root)),
            "length": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        } for path in sorted(root.rglob("*")) if path.is_file()]
        (destination / "sqlite-snapshot.json").write_text(
            json.dumps(databases, indent=2, ensure_ascii=False) + "\n")
        (destination / "repository-files.json").write_text(json.dumps(files, indent=2) + "\n")


if __name__ == "__main__":
    capture(Path(sys.argv[1]), Path(sys.argv[2]))

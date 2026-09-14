#!/usr/bin/env python3
"""递归扫描当前工作目录下的空目录，并把它们移动到其中的 ``tobe-remove`` 目录。

默认是"预览"模式，只列出会被移动的目录；加 ``--apply`` 才真正执行移动。

判定与行为规则：

- 空目录 = 目录下没有任何**文件**条目；只含空目录的目录同样算空（即"递归为空"）。
- 只移动最外层的空目录，不逐级下钻。例如 ``a/b/c`` 全为空时，只移动 ``a``，
  其内部结构原样带到 ``<cwd>/tobe-remove/a/b/c``。
- 目标已存在时追加 ``-1``、``-2`` 后缀，绝不覆盖既有内容。
- 扫描根目录本身、目标目录、跳过名单中的目录（默认 ``.git`` / ``.hg`` / ``.svn``）
  不会被移动；符号链接与目录联接既不被跟随，也不被移动。

用法::

    python scripts/move_empty_dirs.py                  # 预览
    python scripts/move_empty_dirs.py --apply           # 执行
    cd path/to/dir
    python path/to/move_empty_dirs.py --apply --skip node_modules
"""

from __future__ import annotations

import argparse
import itertools
import os
import shutil
import sys
from pathlib import Path

DEFAULT_TARGET_NAME = "tobe-remove"
DEFAULT_SKIP_NAMES = (".git", ".hg", ".svn")


def _is_link_like(path: str) -> bool:
    """符号链接或目录联接：既不跟随，也不当作可移动目录。"""
    if os.path.islink(path):
        return True
    is_junction = getattr(os.path, "isjunction", None)
    return bool(is_junction is not None and is_junction(path))


def _unique_destination(path: Path) -> Path:
    if not os.path.lexists(os.fspath(path)):
        return path
    for index in itertools.count(1):
        candidate = path.with_name(f"{path.name}-{index}")
        if not os.path.lexists(os.fspath(candidate)):
            return candidate
    raise AssertionError("unreachable")


def _walk_error(error: OSError) -> None:
    print(f"[警告] 无法遍历 {error.filename}: {error.strerror}", file=sys.stderr)


def _collect_subtree_flags(root: Path, target: Path, skip_names: frozenset[str]) -> list[tuple[Path, bool]]:
    """前序遍历整棵被扫描的子树，返回 ``(目录, 是否递归为空)`` 列表。

    文件、符号链接与联接都会让所在目录"非空"；被跳过的目录同样计入其父目录的条目。
    """
    directories: list[Path] = []
    for dirpath, dirnames, _filenames in os.walk(
        os.fspath(root), topdown=True, followlinks=False, onerror=_walk_error
    ):
        current = Path(dirpath)
        kept: list[str] = []
        for name in dirnames:
            child = current / name
            if child == target or os.path.normcase(name) in skip_names:
                continue
            if _is_link_like(os.fspath(child)):
                continue
            kept.append(name)
        dirnames[:] = kept
        if current != root:
            directories.append(current)

    # 自底向上（前序的反序）计算"递归为空"：所有条目都是被扫描到且同样递归为空的目录。
    empties: dict[Path, bool] = {}
    for directory in reversed(directories):
        if not directory.is_dir():
            empties[directory] = False
            continue
        try:
            with os.scandir(os.fspath(directory)) as entries:
                empties[directory] = all(empties.get(Path(entry.path), False) for entry in entries)
        except OSError as error:
            _walk_error(error)
            empties[directory] = False

    return [(directory, empties[directory]) for directory in directories]


def plan_empty_dirs(root: Path, target: Path, skip_names: frozenset[str]) -> list[Path]:
    """返回将被移走的最外层空目录（不含 root 与 target 本身）。"""
    planned: list[Path] = []
    planned_set: set[Path] = set()
    for directory, is_empty in _collect_subtree_flags(root, target, skip_names):
        if not is_empty:
            continue
        # 前序遍历保证祖先先出现；祖先已在计划内时，本目录会随之被带走。
        if any(ancestor in planned_set for ancestor in _ancestors_within(directory, root)):
            continue
        planned.append(directory)
        planned_set.add(directory)
    return planned


def _ancestors_within(path: Path, root: Path) -> list[Path]:
    ancestors: list[Path] = []
    current = path.parent
    while current != root and root in current.parents:
        ancestors.append(current)
        current = current.parent
    return ancestors


def move_empty_dirs(root: Path, target: Path, planned: list[Path], dry_run: bool) -> int:
    errors = 0
    for source in planned:
        relative = source.relative_to(root)
        if dry_run:
            print(f"[预览] {relative.as_posix()}  ->  {(target / relative)}")
            continue
        destination = _unique_destination(target / relative)
        try:
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(os.fspath(source), os.fspath(destination))
        except OSError as error:
            errors += 1
            print(f"[错误] 移动 {relative.as_posix()} 失败: {error}", file=sys.stderr)
            continue
        print(f"[移动] {relative.as_posix()}  ->  {destination}")
    return errors


def _parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="递归扫描当前工作目录下的空目录，并移动到 tobe-remove 目录（默认只预览）。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--skip",
        default="",
        help="额外跳过的目录名，逗号分隔（如 node_modules,bin）",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="真正执行移动；省略时只打印预览",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")
    args = _parse_args(argv)

    root = Path.cwd()
    target = root / DEFAULT_TARGET_NAME
    extra_skips = {name.strip() for name in args.skip.split(",") if name.strip()}
    skip_names = frozenset(os.path.normcase(name) for name in {*DEFAULT_SKIP_NAMES, *extra_skips})

    planned = plan_empty_dirs(root, target, skip_names)
    mode = "执行" if args.apply else "预览"
    print(f"[{mode}] 根目录: {root}")
    print(f"[{mode}] 目标目录: {target}")
    print(f"[{mode}] 将移走空目录 {len(planned)} 个")

    errors = move_empty_dirs(root, target, planned, dry_run=not args.apply)

    if args.apply:
        print(f"[执行] 完成，成功 {len(planned) - errors} 个，失败 {errors} 个")
    else:
        print("[预览] 未做任何改动，加 --apply 执行移动")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())

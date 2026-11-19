#!/usr/bin/env python3
"""把当前提交的短 hash 写入主文档的「提交版本」一行。

用法：
    python3 tools/stamp_version.py          # 取 git HEAD
    python3 tools/stamp_version.py 05cd62a  # 指定 hash
"""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOC = ROOT / "docs" / "大作业主文档.md"


def main() -> None:
    if len(sys.argv) > 1:
        sha = sys.argv[1]
    else:
        sha = subprocess.check_output(
            ["git", "rev-parse", "--short=7", "HEAD"], cwd=ROOT, text=True
        ).strip()

    text = DOC.read_text(encoding="utf-8")
    new, count = re.subn(
        r"(commit `)(?:\{\{COMMIT\}\}|[0-9a-f]{7,40})(`)",
        lambda m: m.group(1) + sha + m.group(2),
        text,
    )
    if new != text:
        DOC.write_text(new, encoding="utf-8")
    print(f"stamped {sha} ({count} place)")


if __name__ == "__main__":
    main()

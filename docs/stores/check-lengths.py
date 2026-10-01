#!/usr/bin/env python3
"""Checks the field lengths of the store listing drafts against the store limits.

    python3 docs/stores/check-lengths.py          # report
    python3 docs/stores/check-lengths.py --fix    # also rewrite the "Long."/"Len." column

Reads the tables of listing-fr.md and listing-en.md (columns: field, limit, value, length) and the fenced `text` description block
(limit 4000). Exit code 1 when a value is over its limit or a declared length is wrong.
"""
import re
import sys
from pathlib import Path

fix = "--fix" in sys.argv
status = 0
for name in ("listing-fr.md", "listing-en.md"):
    path = Path(__file__).with_name(name)
    lines = path.read_text(encoding="utf-8").split("\n")
    for i, line in enumerate(lines):
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) == 4 and cells[1].isdigit():
            limit, value, declared = int(cells[1]), cells[2].strip("`"), cells[3]
            actual = len(value)
            problem = actual > limit or declared != str(actual)
            print(f"{name}: {cells[0][:44]:<44} {actual:>4}/{limit:<4} {'OVER LIMIT' if actual > limit else ('declared ' + declared if problem else 'ok')}")
            if actual > limit:
                status = 1
            elif problem:
                status = 1
                if fix:
                    cells[3] = str(actual)
                    lines[i] = "| " + " | ".join(cells) + " |"
    text = path.read_text(encoding="utf-8")
    block = re.search(r"```text\n(.*?)\n```", text, re.S)
    if block:
        size = len(block.group(1))
        print(f"{name}: full description {size}/4000 {'ok' if size <= 4000 else 'OVER LIMIT'}")
        if size > 4000:
            status = 1
    if fix:
        path.write_text("\n".join(lines), encoding="utf-8")
sys.exit(status)

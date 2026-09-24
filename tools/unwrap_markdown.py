#!/usr/bin/env python3
"""Join hard-wrapped Markdown paragraph/list-item lines (stdin -> stdout).

GitHub release bodies and Discord render every newline as a line break, so the
wrapped CHANGELOG.md text shows ragged. Block structure (headings, list items,
quotes, tables, fences, blank lines, explicit hard breaks) is kept as-is.
"""
import re
import sys

BLOCK_START = re.compile(r"^\s*(#{1,6}\s|[-*+]\s|\d+[.)]\s|>|\||<|```|~~~)|^\s*([-*_])(\s*\2){2,}\s*$")
FENCE = re.compile(r"^\s*(```|~~~)")


def unwrap(text: str) -> str:
    out = []
    in_fence = False
    joinable = False
    for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        if FENCE.match(line):
            in_fence = not in_fence
            out.append(line)
            joinable = False
            continue
        if in_fence or not line.strip():
            out.append(line)
            joinable = False
            continue
        if joinable and not BLOCK_START.match(line):
            out[-1] = out[-1].rstrip() + " " + line.strip()
        else:
            out.append(line)
        last = out[-1]
        joinable = not (
            last.lstrip().startswith(("#", "|", "<"))
            or last.endswith(("  ", "\\"))
        )
    return "\n".join(out)


if __name__ == "__main__":
    sys.stdout.write(unwrap(sys.stdin.read()))

#!/usr/bin/env python3
"""Remove keys from every MozaPlugin resx file + Strings.Designer.cs.

Counterpart to tools/add_resx_strings.py for retiring a string. Every key is
removed from all locales and from the Designer accessor in one run, so the
files cannot drift out of parity. A key absent from a file is skipped.

Usage:
    python3 tools/remove_resx_key.py Key [Key ...] [--repo .] [--check]

--check reports where each key is still present and exits non-zero if any is.
"""
import argparse
import os
import re
import sys

# Same list as add_resx_strings.py — keep the two in sync.
RESX_FILES = [
    "Strings.resx",
    "Strings.de.resx",
    "Strings.el.resx",
    "Strings.es.resx",
    "Strings.fr.resx",
    "Strings.it.resx",
    "Strings.ko.resx",
    "Strings.nb.resx",
    "Strings.pt.resx",
    "Strings.qps-ploc.resx",
    "Strings.ru.resx",
    "Strings.vi.resx",
    "Strings.zh-Hans.resx",
]


def data_pattern(key):
    # Whole <data name="Key" ...>...</data> element, single- or multi-line,
    # plus the line break that follows it.
    return re.compile(
        r'[ \t]*<data name="' + re.escape(key) + r'"[^>]*>.*?</data>[ \t]*\r?\n',
        re.DOTALL,
    )


def strip_file(path, keys, check):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    present = [k for k in keys if f'name="{k}"' in text]
    if check or not present:
        return present, []
    for k in present:
        text, n = data_pattern(k).subn("", text)
        if n != 1:
            raise SystemExit(f"{path}: expected 1 <data> for '{k}', matched {n}")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return [], present


def strip_designer(path, keys, check):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    present = [k for k in keys if f'Get("{k}")' in text]
    if check or not present:
        return present, []
    for k in present:
        pat = re.compile(
            r'[ \t]*public static string ' + re.escape(k) + r' => Get\("' + re.escape(k) + r'"\);[ \t]*\r?\n'
        )
        text, n = pat.subn("", text)
        if n != 1:
            raise SystemExit(f"{path}: expected 1 accessor for '{k}', matched {n}")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return [], present


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("keys", nargs="+")
    ap.add_argument("--repo", default=".")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    res_dir = os.path.join(args.repo, "Resources")
    any_present = False
    for fname in RESX_FILES:
        path = os.path.join(res_dir, fname)
        present, removed = strip_file(path, args.keys, args.check)
        if args.check and present:
            any_present = True
            print(f"PRESENT in {fname}: {present}")
        elif removed:
            print(f"{fname}: -{len(removed)}")

    designer = os.path.join(res_dir, "Strings.Designer.cs")
    present, removed = strip_designer(designer, args.keys, args.check)
    if args.check and present:
        any_present = True
        print(f"PRESENT in Strings.Designer.cs: {present}")
    elif removed:
        print(f"Strings.Designer.cs: -{len(removed)}")

    if args.check:
        print("CLEAN" if not any_present else "STILL PRESENT")
        sys.exit(1 if any_present else 0)
    print(f"Done: {len(args.keys)} keys processed.")


if __name__ == "__main__":
    main()

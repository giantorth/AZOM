#!/usr/bin/env python3
"""
Add localized string keys to all MozaPlugin resx files + Strings.Designer.cs.

Input: a JSON file mapping each key to a dict of locale -> value. The English
value lives under "en" (written to Resources/Strings.resx, the neutral file);
the rest map to Strings.<locale>.resx. Every key must supply a value for every
supported locale (parity is the whole point).

Idempotent: keys already present in a file are skipped, so re-running after a
partial edit is safe.

Usage:
    python3 tools/add_resx_strings.py tools/<data>.json [--repo .] [--check]

--check exits non-zero if any key is missing from any file (no writes).
"""
import argparse
import json
import os
import sys
from xml.sax.saxutils import escape

# resx filename suffix -> locale key used in the JSON data.
LOCALES = {
    "Strings.resx": "en",
    "Strings.de.resx": "de",
    "Strings.el.resx": "el",
    "Strings.es.resx": "es",
    "Strings.fr.resx": "fr",
    "Strings.it.resx": "it",
    "Strings.ko.resx": "ko",
    "Strings.nb.resx": "nb",
    "Strings.ru.resx": "ru",
    "Strings.vi.resx": "vi",
    "Strings.zh-Hans.resx": "zh-Hans",
}


def resx_entry(key, value):
    # xml:space="preserve" keeps literal newlines; escape &, <, > for XML safety.
    return f'  <data name="{key}" xml:space="preserve"><value>{escape(value)}</value></data>\n'


def patch_resx(path, locale, data, check):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    missing, added = [], []
    insert = ""
    for key, vals in data.items():
        if locale not in vals:
            raise SystemExit(f"key '{key}' missing locale '{locale}'")
        if f'name="{key}"' in text:
            continue
        missing.append(key)
        insert += resx_entry(key, vals[locale])
    if check:
        return missing, []
    if insert:
        idx = text.rfind("</root>")
        if idx < 0:
            raise SystemExit(f"{path}: no </root>")
        text = text[:idx] + insert + text[idx:]
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        added = missing
    return [], added


def patch_designer(path, data, check):
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    missing = [k for k in data if f'Get("{k}")' not in text]
    if check or not missing:
        return missing if check else [], []
    lines = "".join(
        f'        public static string {k} => Get("{k}");\n' for k in missing
    )
    # Insert before the final "    }\n}" (class + namespace close).
    marker = "    }\n}"
    idx = text.rfind(marker)
    if idx < 0:
        raise SystemExit(f"{path}: no class/namespace close found")
    text = text[:idx] + lines + text[idx:]
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return [], missing


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data")
    ap.add_argument("--repo", default=".")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    with open(args.data, "r", encoding="utf-8") as f:
        data = json.load(f)

    res_dir = os.path.join(args.repo, "Resources")
    any_missing = False
    for fname, locale in LOCALES.items():
        path = os.path.join(res_dir, fname)
        missing, added = patch_resx(path, locale, data, args.check)
        if args.check and missing:
            any_missing = True
            print(f"MISSING in {fname}: {len(missing)} -> {missing}")
        elif added:
            print(f"{fname}: +{len(added)}")

    designer = os.path.join(res_dir, "Strings.Designer.cs")
    missing, added = patch_designer(designer, data, args.check)
    if args.check and missing:
        any_missing = True
        print(f"MISSING in Strings.Designer.cs: {len(missing)} -> {missing}")
    elif added:
        print(f"Strings.Designer.cs: +{len(added)}")

    if args.check:
        print("OK" if not any_missing else "INCOMPLETE")
        sys.exit(1 if any_missing else 0)
    print(f"Done: {len(data)} keys processed.")


if __name__ == "__main__":
    main()

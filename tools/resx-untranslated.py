#!/usr/bin/env python3
"""Report resx keys whose value is byte-identical to the neutral English one.

Strings.Designer.cs Get() walks the culture chain and falls back to the neutral
resource when a key is missing or empty, so an English-valued entry in a locale
file is runtime-identical to having no entry at all — but it hides the gap from
anyone auditing what still needs translating.

  tools/resx-untranslated.py              # per-locale counts
  tools/resx-untranslated.py fr           # list the offending keys for one locale
  tools/resx-untranslated.py --prune fr   # delete them from that locale
  tools/resx-untranslated.py --prune all  # delete them everywhere
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

RES = pathlib.Path(__file__).resolve().parent.parent / "Resources"
# Pseudo-locale: its whole job is to prove a string flows through the resource
# system, so an entry there is meaningful even when it mirrors English.
SKIP = {"qps-ploc"}

# Proper nouns and acronyms that are genuinely identical in every locale we
# ship, so an English-looking value is correct rather than missing.
#
# Keep this list SHORT and add only on evidence. The two mistakes are not
# symmetric: a string wrongly left off shows up as a translation gap and is one
# edit to fix, while a string wrongly added is hidden from every future audit.
# When in doubt, leave it out. In particular a word is not exempt for being
# technical-sounding — "Port", "Motor", "Little-endian" and "S curve" all
# translate. Nor is a Brand_* key name sufficient: Brand_Motor's value is the
# translatable label "MOTOR", not a brand.
BRAND_VALUES = {
    # Company / product names
    "AB9", "mBooster", "AZOM", "GitHub", "Bluetooth", "SimHub ShakeIt",
    "HGP", "SGP",
    # Acronyms left untranslated across all our locales
    "ABS", "RPM", "SDK", "MCU", "MOSFET", "MD5:", "LEDs", "LEDS", "D-Pad",
    # Acronym + hardware index
    "LED 01", "LED 1 (S1)", "LED 2 (S2)",
    # GitHub pull-request reference
    "PR #{0}: {1}",
}

# Values whose correct translation is identical to English in every locale that
# uses them — loanwords and cognates. Verified per locale when the 2026-09-05
# translation pass went through all 401 English-valued entries; these 127 came
# out the same on purpose. Adding here is a claim that the word is genuinely
# correct in the target languages, not that it looks technical.
IDENTICAL_TRANSLATIONS = {
    "Start", "LINEAR", "OK", "STOP", "■ STOP", "Test 1s", "Test", "Hash",
    "Little-endian", "MOTOR", "PEDAL", "Angle", "CLUTCH", "Clutch",
    "Dashboard", "Dashboard:", "Mode", "Mode 1", "Mode 2", "MODE", "PALETTE",
    "Pedal {0}", "Port 1", "Port 2", "Port 3", "Position", "POSITION",
    "SESSION", "SOURCE", "Torque", "version —", "Cycle", "Full", "Stable",
    "CALIBRATION", "DISPLAY", "JOYSTICK", "MOZA DASHBOARD", "PROTECTION",
    "Cyan", "Orange", "Color", "Formula", "Interpolation", "Name", "Trigger",
    "Base", "Options", "Hub",
    "5+R Layout 1", "5+R Layout 2", "6+R Layout 1", "6+R Layout 2",
    "7+R Layout 1", "7+R Layout 2",
    "R+5 Layout", "R+6 Layout", "R+8 Layout",
    "1 minute", "2 minutes", "3 minutes", "4 minutes", "5 minutes",
    "10 minutes", "15 minutes", "20 minutes", "25 minutes", "30 minutes",
    "35 minutes", "40 minutes", "45 minutes",
}


def is_brand(key, value):
    if value.strip() in IDENTICAL_TRANSLATIONS:
        return True
    value = value.strip()
    if value in BRAND_VALUES:
        return True
    # Bare URLs / handles.
    return bool(re.match(r"^[\w.-]+\.(com|cc|org|dev|io)(/|$)", value))


def load(path):
    root = ET.parse(path).getroot()
    out = {}
    for d in root.findall("data"):
        name = d.get("name")
        value = d.find("value")
        if name is not None:
            out[name] = (value.text or "") if value is not None else ""
    return out


def locale_of(path):
    return path.name[len("Strings."):-len(".resx")]


def main():
    args = [a for a in sys.argv[1:]]
    prune = False
    if "--prune" in args:
        prune = True
        args.remove("--prune")
    target = args[0] if args else None

    base = load(RES / "Strings.resx")
    total = 0
    for path in sorted(RES.glob("Strings*.resx")):
        loc = locale_of(path)
        if not loc or loc in SKIP:
            continue
        if target and target != "all" and loc != target:
            continue

        cur = load(path)
        same = sorted(k for k, v in cur.items()
                      if k in base and v == base[k] and v.strip() and not is_brand(k, v))
        total += len(same)
        print(f"  {loc:8s} {len(same):4d} / {len(cur):4d} entries are English")

        if target and not prune:
            for k in same:
                print(f"      {k}")

        if prune and same:
            text = path.read_text(encoding="utf-8")
            removed = 0
            for k in same:
                text, n = re.subn(rf'^ *<data name="{re.escape(k)}"[^\n]*\n', "", text, flags=re.M)
                removed += n
            path.write_text(text, encoding="utf-8")
            print(f"      pruned {removed} entries")

    if not target:
        print(f"\n  {total} English-valued entries across the translated locales")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Regression check for the mBooster transport->serial settings re-key merge.

Replays the merge over the real pre-merge settings in bug bundle QR3760VJ and
asserts that every value bundle A6N521CS lost (pedal 1 Direction, its output
curve, and the whole pedal-2 row) now survives, while the transport-keyed side
keeps the one value it actually held (pedal 1 MaxForceKg = 24).

The logic here MIRRORS MozaPlugin.MBooster.cs -- IsUntouchedPedalConfig,
IsUntouchedMBoosterPlaceholder, MergeMBoosterSettings and
MergeMBoosterPedalConfig. There is no C# test project in this repo, so this is
the only executable check on that code; update it alongside any change to those
methods or it will drift into being worthless.

Needs worker/bundles/QR3760VJ/ unpacked:
    python3 worker/tools/bugreports.py fetch QR3760VJ -x

Run from the repo root. Exit 0 = pass.
"""
import json, sys

SERIAL_KEY = "mbooster:xCRtCY7sQf258jf0JGgD2R/ODpCPuVL1"
XPORT_KEY = "9&17d10cc2&0&0000"

NUM = ["Direction","Min","Max","SensorOutputRatioPct","MaxThresholdKg","DeadzoneKg",
       "MaxForceKg","TravelStartMm","TravelEndMm","EndstopFrontStiffness",
       "EndstopEndStiffness","NaturalFrictionPct","DampingPressPct","DampingReleasePct"]
ARR = ["CurveY","CurveX","InputCurveY","InputCurveX"]
SD  = ["Divider1Pressed","Divider2Pressed","Seg1Pressed","Seg2Pressed","Seg3Pressed",
       "Divider1Released","Divider2Released","Seg1Released","Seg2Released","Seg3Released"]

def untouched_cfg(c):
    if any(c.get(f, -1) >= 0 for f in NUM): return False
    if any(c.get(f) is not None for f in ARR): return False
    sd = c.get("SegmentedDamping") or {}
    return not any(sd.get(f, -1) >= 0 for f in SD)

def untouched_placeholder(s):
    return (s.get("Role", 0) == 0 and s.get("AxisRoles") is None
            and not s.get("DisplayName") and untouched_cfg(s)
            and all(r is None or untouched_cfg(r) for r in (s.get("Pedals") or {}).values()))

def merge_cfg(frm, into, conflicts, label):
    """Backfill `into` from `frm`; record fields real on both sides."""
    for f in NUM:
        if into.get(f, -1) < 0:
            if frm.get(f, -1) >= 0: into[f] = frm[f]
        elif frm.get(f, -1) >= 0 and abs(frm[f] - into[f]) > 1e-4: conflicts.append(label + f)
    for f in ARR:
        if into.get(f) is None:
            if frm.get(f) is not None: into[f] = frm[f]
        elif frm.get(f) is not None: conflicts.append(label + f)
    sd, fd = into.setdefault("SegmentedDamping", {}), frm.get("SegmentedDamping") or {}
    for f in SD:
        if sd.get(f, -1) < 0:
            if fd.get(f, -1) >= 0: sd[f] = fd[f]
        elif fd.get(f, -1) >= 0 and abs(fd[f] - sd[f]) > 1e-4: conflicts.append(label + "SegmentedDamping." + f)

def merge(frm, into):
    """`into` (= transport-keyed `stale`) survives, backfilled from `frm`."""
    conflicts = []
    if into.get("Role", 0) == 0: into["Role"] = frm.get("Role", 0)
    elif frm.get("Role", 0) != 0 and frm["Role"] != into["Role"]: conflicts.append("Role")
    if into.get("AxisRoles") is None: into["AxisRoles"] = frm.get("AxisRoles")
    elif frm.get("AxisRoles") is not None: conflicts.append("AxisRoles")
    if not into.get("DisplayName"): into["DisplayName"] = frm.get("DisplayName", "")
    elif frm.get("DisplayName") and frm["DisplayName"] != into["DisplayName"]: conflicts.append("DisplayName")
    merge_cfg(frm, into, conflicts, "")
    merged = dict(into.get("Pedals") or {})
    for k, v in (frm.get("Pedals") or {}).items():
        if v is None: continue
        if k not in merged or merged[k] is None: merged[k] = v
        else: merge_cfg(v, merged[k], conflicts, f"pedal {k} ")
    into["Pedals"] = merged
    return conflicts

d = json.load(open("worker/bundles/QR3760VJ/files/plugin-settings.json"))
prof = next(p for p in d["ProfileStore"]["Profiles"] if p["Name"] == "Euro Truck Simulator 2")
ms = prof["MBoosterSettings"]
stale, existing = ms[XPORT_KEY], ms[SERIAL_KEY]

print("BEFORE")
print(f"  transport {XPORT_KEY}: pedal1 MaxForceKg={stale['Pedals']['1']['MaxForceKg']}, "
      f"Direction={stale['Pedals']['1']['Direction']}, CurveY={'set' if stale['Pedals']['1']['CurveY'] else 'null'}, "
      f"rows={sorted(stale['Pedals'])}")
print(f"  serial    ...uVL1: pedal1 MaxForceKg={existing['Pedals']['1']['MaxForceKg']}, "
      f"Direction={existing['Pedals']['1']['Direction']}, CurveY={'set' if existing['Pedals']['1']['CurveY'] else 'null'}, "
      f"rows={sorted(existing['Pedals'])}")
print(f"  stale is untouched placeholder: {untouched_placeholder(stale)}  -> field-level merge runs")

conflicts = merge(existing, stale)
p1 = stale["Pedals"]["1"]
print("\nAFTER (surviving entry, stored under the serial key)")
print(f"  pedal1 MaxForceKg={p1['MaxForceKg']}  Direction={p1['Direction']}  "
      f"CurveY={'set' if p1['CurveY'] else 'null'}  rows={sorted(stale['Pedals'])}")
print(f"  conflicts reported: {conflicts or 'none'}")

# A6N521CS observed: Direction 0 -> -1, CurveY set -> null, pedal 2 dropped.
checks = {
    "transport-side MaxForceKg kept (24.0)":      p1["MaxForceKg"] == 24.0,
    "serial-side Direction preserved (0)":        p1["Direction"] == 0,
    "serial-side CurveY preserved":               p1["CurveY"] is not None,
    "serial-side pedal row 2 preserved":          set(stale["Pedals"]) == {"1", "2"},
    "no spurious conflict warning":               conflicts == [],
}
print()
for k, v in checks.items():
    print(f"  [{'ok' if v else 'FAIL'}] {k}")
print("\nRESULT:", "PASS" if all(checks.values()) else "FAIL")
sys.exit(0 if all(checks.values()) else 1)

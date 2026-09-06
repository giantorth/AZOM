#!/usr/bin/env python3
"""Strip Telemetry.json down to the fields used by DashboardProfileStore."""

import json
import sys
from pathlib import Path

KEEP_FIELDS = {"name", "url", "compression", "package_level"}

src = Path(__file__).resolve().parent.parent / "Data" / "Telemetry.json"
with open(src) as f:
    data = json.load(f)

data["sectors"] = [
    {k: v for k, v in sector.items() if k in KEEP_FIELDS}
    for sector in data["sectors"]
]

with open(src, "w") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")

count = len(data["sectors"])
print(f"Wrote {count} sectors with fields {sorted(KEEP_FIELDS)} to {src}")

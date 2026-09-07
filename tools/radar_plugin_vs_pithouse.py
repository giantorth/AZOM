#!/usr/bin/env python3
"""Layer-B diff: compare the PLUGIN's radar emission to PitHouse's, same session.

Both the plugin (replaying radar6) and PitHouse (original capture) drove the
*same* car positions, so at a matched lap time their radar tiers should agree.
This decodes both, aligns each to the replay by lap time, and reports — per lap
bucket — populated-slot count and decoded |relZ| spread for each, plus how many
cars the plugin emits that fall in WRAP territory (|relZ| > ~24 m) where PitHouse
emits none. That over-population + wrap is the streaming-clutter signature.

Usage:
    python tools/radar_plugin_vs_pithouse.py <plugin.jsonl> <pithouse.jsonl> <replay-stem>
"""
import os, sys, json, struct, bisect, math
from collections import Counter
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import radar_verify as rv

WRAP = (1 << 20) / rv.RADAR_Z_SCALE   # ~48.46 m; principal window = +/- WRAP/2


def decode_radar(path):
    """Return list of (lap, {slot: field}) for radar-tier frames (any tier size)."""
    out = []
    for line in open(path):
        line = line.strip()
        if not line:
            continue
        try:
            o = json.loads(line)
        except Exception:
            continue
        if o.get('dir') != 'h2b':
            continue
        b = bytes.fromhex(o['hex'])
        if len(b) < 6 or b[2] != 0x43:
            continue
        pl = b[4:-1]
        if len(pl) < 8 or pl[0] != 0x7d or pl[1] != 0x23:
            continue
        d = pl[8:]
        tb = len(d) * 8
        if tb < 131 + 32 or rv.bget(d, 131, 32) != rv.RADAR_MAGIC:
            continue
        lap = struct.unpack_from('<f', d, 0)[0]
        nri = (tb - 131) // 32
        ri = {k: rv.bget(d, 131 + 32 * k, 32) for k in range(1, nri)}
        out.append((lap, ri))
    return out


def summarize(name, frames):
    nz = [sum(1 for f in ri.values() if f != 0) for _lap, ri in frames]
    relz = [rv.ri_relZ_shipped(f) for _lap, ri in frames for f in ri.values() if f != 0]
    highs = Counter((f >> 20) & 0xFFF for _lap, ri in frames for f in ri.values() if f != 0)
    print(f"\n[{name}] radar frames={len(frames)}")
    if nz:
        print(f"  populated slots/frame: mean={sum(nz)/len(nz):.2f} max={max(nz)} "
              f"hist={dict(sorted(Counter(nz).items()))}")
    if relz:
        av = sorted(abs(z) for z in relz)
        over = sum(1 for z in av if z > WRAP/2 - 0.3)
        print(f"  decoded |relZ| (n={len(av)}): p50={av[len(av)//2]:.1f} "
              f"p95={av[int(.95*len(av))]:.1f} max={av[-1]:.1f} m")
        print(f"  in WRAP territory (|relZ|>{WRAP/2:.0f}m): {over} ({100*over/len(av):.0f}%)")
    print(f"  high-12-bit: {{ {', '.join(f'0x{h:03X}:{c}' for h,c in highs.most_common(5))} }}")


def main():
    plug = decode_radar(sys.argv[1])
    pit = decode_radar(sys.argv[2])
    summarize("PLUGIN", plug)
    summarize("PITHOUSE", pit)

    # Optional: bucket by lap and show side-by-side populated counts.
    def bucketed(frames):
        b = {}
        for lap, ri in frames:
            if not math.isfinite(lap) or lap < 0:
                continue
            nz = sum(1 for f in ri.values() if f != 0)
            b.setdefault(int(lap // 5), []).append(nz)
        return {k: sum(v)/len(v) for k, v in b.items()}
    pb, qb = bucketed(plug), bucketed(pit)
    print("\nmean populated slots by 5s lap bucket (plugin | pithouse):")
    for k in sorted(set(pb) | set(qb)):
        print(f"  lap {k*5:3d}-{k*5+5:3d}s: {pb.get(k, float('nan')):5.2f} | {qb.get(k, float('nan')):5.2f}")


if __name__ == '__main__':
    main()

#!/usr/bin/env python3
"""Align the plugin's radar emission to PitHouse's capture by the preamble lap
time (the in-frame time code), then match the cars shown on each side by
position+orientation. The plugin's replay slot NUMBERS differ from PitHouse's
carIds (SimHub hides carId in replay), so we compare the SET of cars, not slots.

Each ri slot = position (relZ, low 20 bits) + orientation (relHeadDeg, high 12,
centre 0x167). A car matches if relZ within --ztol m and relHead within --htol deg.

Usage: python tools/radar_match.py <plugin.jsonl> <pithouse.jsonl>
"""
import os, sys, json, struct, bisect, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import radar_verify as rv

WRAP = (1 << 20) / rv.RADAR_Z_SCALE


def cars(path):
    """lap -> list of (relZ, relHeadDeg) for populated ri slots."""
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
        d = pl[8:]; tb = len(d) * 8
        if tb < 163 or rv.bget(d, 131, 32) != rv.RADAR_MAGIC:
            continue
        lap = struct.unpack_from('<f', d, 0)[0]
        if not (lap == lap) or lap < 0 or lap > 1e6:   # skip NaN/garbage
            continue
        cs = []
        for k in range(1, (tb - 131) // 32):
            f = rv.bget(d, 131 + 32 * k, 32)
            if f == 0:
                continue
            relz = rv.ri_relZ_shipped(f)
            relh = ((f >> 20) & 0xFFF) - 0x167
            if relh > 2048:
                relh -= 4096
            cs.append((relz, relh))
        out.append((lap, cs))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('plugin'); ap.add_argument('pithouse')
    ap.add_argument('--tol', type=float, default=0.03)   # lap-time match (s)
    ap.add_argument('--ztol', type=float, default=1.5)   # position match (m)
    ap.add_argument('--htol', type=float, default=20.0)  # heading match (deg)
    a = ap.parse_args()

    P = cars(a.plugin)
    Q = cars(a.pithouse)
    Q.sort(key=lambda r: r[0])
    qlaps = [r[0] for r in Q]
    print(f"plugin frames={len(P)} pithouse frames={len(Q)}")

    def qmatch(lap):
        j = bisect.bisect_left(qlaps, lap)
        best = None; bd = a.tol
        for jj in (j - 1, j, j + 1):
            if 0 <= jj < len(Q) and abs(qlaps[jj] - lap) <= bd:
                bd = abs(qlaps[jj] - lap); best = Q[jj]
        return best

    matched_frames = 0
    pl_cars = pl_hit = ph_cars = ph_hit = 0
    zres = []; hres = []
    for lap, pc in P:
        if not pc:
            continue
        m = qmatch(lap)
        if m is None:
            continue
        qc = m[1]
        matched_frames += 1
        used = [False] * len(qc)
        for (pz, ph) in pc:
            pl_cars += 1
            bestj = -1; bestc = 1e9
            for j, (qz, qh) in enumerate(qc):
                if used[j]:
                    continue
                dz = abs(((pz - qz) + WRAP / 2) % WRAP - WRAP / 2)
                dh = abs(((ph - qh) + 180) % 360 - 180)
                if dz <= a.ztol and dh <= a.htol and dz + dh * 0.05 < bestc:
                    bestc = dz + dh * 0.05; bestj = j
            if bestj >= 0:
                used[bestj] = True
                pl_hit += 1
                qz, qh = qc[bestj]
                zres.append(abs(((pz - qz) + WRAP / 2) % WRAP - WRAP / 2))
                hres.append(abs(((ph - qh) + 180) % 360 - 180))
        ph_cars += len(qc)
        ph_hit += sum(used)

    print(f"\nlap-aligned frames: {matched_frames}")
    if pl_cars and ph_cars:
        print(f"plugin cars matched to a PitHouse car: {pl_hit}/{pl_cars} ({100*pl_hit/pl_cars:.0f}%)")
        print(f"PitHouse cars matched by a plugin car: {ph_hit}/{ph_cars} ({100*ph_hit/ph_cars:.0f}%)")
    if zres:
        zres.sort(); hres.sort()
        n = len(zres)
        print(f"matched-pair residuals (n={n}): "
              f"posRMS={ (sum(z*z for z in zres)/n)**0.5:.2f}m  posMed={zres[n//2]:.2f}m  "
              f"headMed={hres[n//2]:.1f}deg")


if __name__ == '__main__':
    main()

#!/usr/bin/env python3
"""Deep re-derivation of the radar `ri` 32-bit encoding from PitHouse data.

NO circular assumptions: match PitHouse ri values to replay opponents ONLY on
frames where exactly one opponent is in range AND PitHouse shows exactly one ri,
so (ri <-> car) is unambiguous. Then correlate every candidate bit-field against
world (relX,relZ) and player-frame (forward,lateral) to find what ri encodes.

Usage: radar_ri_crack.py <pithouse.jsonl> <replay_stem>
"""
import sys, json, struct, zlib, bisect, math
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import radar_verify as rv

PH = sys.argv[1]; STEM = sys.argv[2]
RANGE2D = 40.0   # "in range" gate for the unambiguous match

# --- build replay index ---
tj = Path(STEM + '.telemetry.json').read_bytes()
ir = Path(STEM + '.telemetry.jsonidx').read_bytes()
offs = []; p = 5
while p + 25 <= len(ir):
    if ir[p] == 1: offs.append(struct.unpack_from('<I', ir, p + 1)[0])
    p += 25
rep = []
for i in range(len(offs)):
    o0 = offs[i]; o1 = offs[i + 1] if i + 1 < len(offs) else len(tj)
    try: f = json.loads(zlib.decompress(tj[o0:o1][5:], -15))
    except: continue
    g = f.get('Graphics') or {}; phy = f.get('Physics') or {}
    cc = g.get('CarCoordinates')
    if not cc or len(cc) < 3: continue
    px, pz = float(cc[0]), float(cc[2]); hd = float(phy.get('Heading', 0))
    lap = float(g.get('iCurrentTime', 0)) / 1000.0
    veh = (f.get('Opponents') or {}).get('vehicle') or []
    cars = []
    for v in veh:
        wp = v.get('worldPosition') or {}
        if 'x' not in wp: continue
        cars.append((float(wp['x']), float(wp['z'])))
    # player is nearest car (carId of player not in opponents list reliably) ->
    # use CarCoordinates as player; opponents are the vehicle list
    rep.append(dict(lap=lap, px=px, pz=pz, hd=hd, cars=cars))
rep.sort(key=lambda r: r['lap']); rl = [r['lap'] for r in rep]

# --- collect (ri, position) pairs via relZ-anchored unique match ---
# low-20 -> relZ is independently verified (0.24m); use it ONLY to pick the car,
# then study the OTHER bits. A pair is kept only if exactly ONE opponent matches
# the decoded relZ within 0.5m AND within 30m 2D (so the match is unambiguous).
pairs = []
for line in open(PH):
    line = line.strip()
    if not line: continue
    try: o = json.loads(line)
    except: continue
    if o.get('dir') not in ('b2h', 'h2b'): continue
    b = bytes.fromhex(o['hex'])
    if len(b) < 6 or b[2] != 0x43: continue
    pl = b[4:-1]
    if len(pl) < 8 or pl[0] != 0x7d or pl[1] != 0x23: continue
    d = pl[8:]; tb = len(d) * 8
    if tb < 163 or rv.bget(d, 131, 32) != rv.RADAR_MAGIC: continue
    lap = struct.unpack_from('<f', d, 0)[0]
    if not (lap == lap) or lap < 0 or lap > 1e6: continue
    i = bisect.bisect_left(rl, lap); m = None
    for j in (i - 1, i):
        if 0 <= j < len(rep):
            if m is None or abs(rep[j]['lap'] - lap) < abs(m['lap'] - lap): m = rep[j]
    if m is None or abs(m['lap'] - lap) > 0.05: continue
    px, pz, hd = m['px'], m['pz'], m['hd']
    cs = math.cos(-hd); sn = math.sin(-hd)
    for k in range(1, (tb - 131) // 32):
        ri = rv.bget(d, 131 + 32 * k, 32)
        if not ri: continue
        dec = rv.ri_relZ_shipped(ri)
        cands = [(x, z) for (x, z) in m['cars']
                 if abs((z - pz) - dec) < 0.5 and (x - px) ** 2 + (z - pz) ** 2 < 900]
        if len(cands) != 1: continue
        x, z = cands[0]
        relx = x - px; relz = z - pz
        fwd = relx * sn + relz * cs
        lat = relx * cs - relz * sn
        pairs.append(dict(ri=ri, relx=relx, relz=relz, fwd=fwd, lat=lat,
                          dist=math.hypot(relx, relz), brg=math.degrees(math.atan2(lat, fwd)),
                          hd=hd))

print(f"relZ-anchored (ri<->car) pairs: {len(pairs)}")
if len(pairs) < 20:
    print("too few pairs for a stable fit"); sys.exit(0)
import statistics
for q in ('relx', 'relz', 'fwd', 'lat', 'hd'):
    vs = [p[q] for p in pairs]
    print(f"  {q}: min={min(vs):.1f} max={max(vs):.1f} stdev={statistics.pstdev(vs):.2f}")

def corr(xs, ys):
    n = len(xs); mx = sum(xs)/n; my = sum(ys)/n
    sx = sum((a-mx)**2 for a in xs); sy = sum((b-my)**2 for b in ys)
    if sx == 0 or sy == 0: return 0.0
    return sum((a-mx)*(b-my) for a,b in zip(xs,ys)) / math.sqrt(sx*sy)

def field(ri, lo, width):
    return (ri >> lo) & ((1 << width) - 1)

def signed(v, width):
    h = 1 << (width - 1)
    return v - (1 << width) if v >= h else v

# Candidate fields to test
targets = {'relx': [p['relx'] for p in pairs], 'relz': [p['relz'] for p in pairs],
           'fwd': [p['fwd'] for p in pairs], 'lat': [p['lat'] for p in pairs],
           'dist': [p['dist'] for p in pairs], 'brg': [p['brg'] for p in pairs]}

print("\n=== correlation of candidate ri bit-fields vs geometry ===")
print(f"{'field':>16} " + " ".join(f"{t:>7}" for t in targets))
candidates = [('low20', 0, 20), ('high12', 20, 12), ('low16', 0, 16), ('high16', 16, 16),
              ('low10', 0, 10), ('bits10-19', 10, 10), ('low12', 0, 12), ('bits12-23', 12, 12)]
for name, lo, w in candidates:
    vals = [signed(field(p['ri'], lo, w), w) for p in pairs]
    row = " ".join(f"{corr(vals, targets[t]):>7.2f}" for t in targets)
    print(f"{name:>16} {row}")

# Best-fit linear for low20 vs relz and high12 vs lat/relx (scale + offset)
def linfit(xs, ys):
    n=len(xs); mx=sum(xs)/n; my=sum(ys)/n
    sx=sum((a-mx)**2 for a in xs)
    if sx==0: return 0,0
    b=sum((a-mx)*(c-my) for a,c in zip(xs,ys))/sx; a=my-b*mx
    return b,a
lo20=[field(p['ri'],0,20) for p in pairs]
b,a=linfit(lo20,[p['relz'] for p in pairs])
print(f"\nlow20 -> relz: relz = {b:.6f}*field + {a:.2f}  (1/scale={1/b:.0f} if linear)")
hi12=[field(p['ri'],20,12) for p in pairs]
for tgt in ('lat','relx','brg'):
    b,a=linfit(hi12,[p[tgt] for p in pairs])
    print(f"high12 -> {tgt}: {tgt} = {b:.5f}*field + {a:.2f}")

print("\n=== sample pairs (sorted by lat) ===")
print(f"{'ri(hex)':>9} {'low20':>7} {'high12':>6} | {'relx':>6} {'relz':>6} {'fwd':>6} {'lat':>6} {'brg':>5}")
for p in sorted(pairs, key=lambda p: p['lat'])[::max(1,len(pairs)//30)]:
    print(f"{p['ri']:>9X} {field(p['ri'],0,20):>7} {field(p['ri'],20,12):>6} | "
          f"{p['relx']:>6.1f} {p['relz']:>6.1f} {p['fwd']:>6.1f} {p['lat']:>6.1f} {p['brg']:>5.0f}")

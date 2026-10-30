# Track-map & radar channels (`patch/Location*`, `patch/ri*`)

How PitHouse encodes the MOZA "track map" (everyone's dots on a mini circuit
map) and "radar" (close-proximity spotter) channels. Reverse-engineered from
paired captures: a USBPcap recording of PitHouse↔wheel serial traffic during an
Assetto Corsa session, correlated against a **parallel SimHub telemetry replay**
of the same session (the replay is the game-side ground truth — see
[Method](#method)). Verified to <0.3 m on two tracks (Imola, Spa).

## Channel split

| Feature | Channels | Count | Indexing | Meaning |
|---|---|---|---|---|
| **Track map** | `patch/Location`, `patch/Location_0..63` | 1 + 64 | static — `Location_N` = car index `N` (player at `PlayerIndex`) | **absolute** world position of every car |
| **Radar** | `patch/ri0..ri63` (8 fast + overflow) | up to 64 | **stable per-car slot** (kept while in range; `ri0` = player magic) | packed **(lateral, forward, orientation)** of nearby cars, player-relative world frame |

Both are gated behind `EnableRadarTrackMapChannels`; `OpponentCount` /
`PlayerIndex` are not (ordinary dashboards read them). The wheel masks unused
`Location_N` / `ri_N` slots via `OpponentCount`: it draws `Location_0..OpponentCount`,
so a car in a higher slot is invisible even with a valid position (bundle XMVWAYC1:
`OpponentCount` 22, opponents sent in `Location_33+`, only the player drawn).
AC slots are carIds (dense); for list-only games (iRacing, replays) the plugin
assigns dense slots `1..opponents` per driver Id (`AssignOpponentSlots`): kept while
present, a new car takes the lowest slot no present car holds.

## Track map — `location_t` (64-bit) — SOLVED

`location_t` is advertised in the tier-def as **compression code `0x09`, bit
width 64** (confirmed from the cold-start tier-def capture; the wheel packs it
as the last field of each per-car tier). The 64 bits are **packed fixed-point
integers — NOT two `float32`** (the earlier guess). Little-endian byte layout:

| Bytes | Field | Width | Encoding |
|---|---|---|---|
| `[0:2]` | **Y** (elevation) | u16 LE | `clamp(centerY + round(S·y))` |
| `[2:5]` | **Z** (world z) | u24 LE | `clamp(centerZ + round(16·S·z))` |
| `[5:8]` | **X** (world x) | u24 LE | `clamp(centerX + round(S·x))` |

It is provably integer, not float: regressing world x/z against the raw bytes is
linear (R²=1.0) **across x=0**, where a float's sign bit would flip and break
linearity. The high byte of each field also ramps smoothly (a float exponent
would not).

### Scale & offset are track-specific

The per-axis scale `S` and the centre offsets differ by track and are **not**
derivable from spline length or SCALE_FACTOR alone (`S/SCALE_FACTOR` is
459.8 for Imola vs 379.0 for Spa — not constant):

| Track | S (X & Y) | Z scale | Z/X ratio |
|---|---|---|---|
| Imola (spline 4864 m) | 551.7 /m | 8815 /m | 15.98 |
| Spa (spline 6946 m) | 492.7 /m | 7881 /m | 16.00 |

Two robust facts hold on both tracks:

- **X and Y share one scale `S`; Z always uses `16·S`** (exact).
- The track occupies only a small, off-centre slice of the field (PitHouse's
  values sit around 4.3–4.9 M of the 16.7 M u24 range).

The 16× ratio is a normalisation, not a squash: PitHouse's X always sits at
`0x48xxxx` (top nibble 4), and its **low 20 bits** are centred on `2^19` exactly
as Z's 24 bits are centred on `2^23`. So `X_low20 / 2^20` and `Z / 2^24` are the
same unit — one window, one scale:

```
X = 0x480000 + S·(x − cx)        Z = 0x800000 + 16·S·(z − cz)
```

**`S = 10^6 / E`**, with `E` the larger dimension of the track's outline bounds
(below): `S·E` = 999,900 / 999,875 / 1,000,246 on Imola / Spa / Zandvoort. The
window does not auto-fit (on hardware a mismatched scale/centre drew nothing);
fields are clamped.

### Wheel outlines (`.mztrack`)

`patch/TrackName` = `<game>/<name>`; the wheel loads
`/home/moza/resource/maps/<game>/<name>.mztrack` from its firmware (its display
log prints `setCurTrack` / `loading …` or `not exist …`). PitHouse keeps the same
set in `bin/GameConfigs/TrackData/<game>/` for the dashboard builder: folders
`ac`, `acc`, `f1`, `iracing`, `lemans`; JSON, XOR-0x5A obfuscated
(`tools/mztrack_compare.py` decodes). Each file has `x/y/z_min/max` and
`sectors[].data` points `[x, y, z]` scaled 0..10000 per axis (`transform` 0.0001).

- **World-coordinate games** (AC, ACC, LMU, F1): bounds are world metres —
  mztrack x/y = world X/Z, z = elevation — the raw extent of the line.
- **iRacing**: normalised (`x_min=y_min=0`, larger axis 1, z 0) — the shape of
  SimHub's lap-relative map with **Z negated** (Daytona 2011 road: 0.2 % mean
  error; Road Atlanta 1.8 %). World-coordinate outlines run +Z, so iRacing's Z
  field is sent inverted.

`tools/gen_track_outlines.py` embeds the names/bounds as `Data/TrackOutlines.json`
and the normalised (iRacing) lines as `Data/TrackOutlinePoints.gz` (466 lines,
443k points, 249 KB: delta-of-delta varints). The lines are near-evenly spaced (median longest/median
segment 1.05; worst ~2×, Monza/Bathurst), point 0 on the S/F line, ≤ 1.5 %
closure gap. `TrackOutlines.Find` matches SimHub's
`TrackId+TrackConfig`, `TrackId`, then `TrackName` on lowercase letters/digits
(AC `ks_nordschleife-nordschleife` → `ks_nordschleifenordschleife`; ACC TrackId
`nurburgring_24h`, whose TrackName is "24H Nürburgring"). Keys are sent
**lowercase**: the firmware's files differ in case from PitHouse's — the wheel
logged `not exist …/iracing/nurburgring combinedShortB.mztrack` while
`iracing/nurburgring combined` loaded.

### Transform source by game

- **AC** — `TrackMapTransform.ResolveMapIni` (track's map.ini). The result is
  cached per track folder for the process lifetime, a "no map.ini" null
  included, so builders re-resolving on track change don't re-read the file on
  the tick thread. AC's `content/` directory (running AC process, else Steam
  libraries) is cached only once found; a miss is retried on the next uncached
  track lookup.
- **Outline with world bounds** (ACC, LMU, F1, AC without map.ini) —
  `FromBounds` on the outline's bounds, centred.
- **iRacing with an outline line** — `FromBounds` on the line's extent (below).
- **Otherwise** (iRacing without a line, untabled tracks) — `FromBounds` on SimHub's recorded
  map (`PersistantTrackerPlugin.GetMap()`), refit whenever SimHub swaps the
  record. Lap-relative maps: raw min/max, Z inverted; world maps: 1 %-trimmed
  (pit-lane tails).
- Y (u16) gets a smaller scale when the elevation range would overflow it
  (Bathurst: 191 m) — a clamped 0 there collides with the empty-slot marker. Live
  fields clamp to ≥ 1 for the same reason.
- **No map yet** — fixed `Fallback()` (SCALE_FACTOR 1.2 at world origin). Large
  tracks clamp; `tools/trackmap_replay_sim.py` measures it on a replay (iRacing
  Road Atlanta: 28 % of positions clamped).

### iRacing (lap-relative coordinates)

iRacing has no world positions. SimHub reports every car, player included, as
`[LapDistPct, 0, 0]` and resolves it through the recorded map
(`DataRecordBase.ToAbsoluteCoordinates` → interpolated `CarCoordinates[].Value`,
built by `CoordinatesCreator` from YawNorth/LapDist/Pitch). SimHub may have no
map for a layout (bundle E1K79CQM, Nordschleife VLN: no map → empty slots → the
wheel showed one dot dead centre), so the plugin prefers the outline line:

- **Outline line** (`TrackOutline.AtLapFraction`): pct → point at that fraction of
  the line's length, interpolated, closed loop. Metres = SimHub `TrackLength` /
  line length; stored in SimHub's lap-relative frame (Z = −outline y), offset one
  unit so nothing is (0, 0). No elevation (Y = 0). Without a track length
  (0.5–30 km) the map still fits but the radar is skipped.
  `tools/outline_line_check.py`: Daytona 2011 road 1.5 m median / 9.6 m max vs
  SimHub's map; Road Atlanta 2–8 m outside the replay map's broken S/F stretch.
- **The player** (`patch/Location`) comes from the player's own opponent entry
  (`CarIdxLapDistPct[PlayerCarIdx]`), not `CarCoordinates`: iRacing's player-car
  `LapDistPct` is 0 (`IsOnTrack` false) while spectating — team race watched from
  the pit wall — which put the player marker on the S/F line.
- **SimHub's map** otherwise; with neither there is nothing to plot (raw pct
  would collapse every car onto one point) and the plugin sends empty slots.
- The map's own points are already in the resolved space — they must not be run
  through `ToAbsoluteCoordinates` again (that reads X as a lap fraction).
- Cars are on the recorded line: no lateral position, so the radar shows gaps
  along the line only.

### Empty slots

An absent car (no position, or a slot past `OpponentCount`) is sent as **all
zero** (8 zero bytes). The plugin emits the same and treats `X==0 && Z==0` (and
any non-finite coordinate) as the empty marker.

## Radar — `patch/ri*` (32-bit) — SOLVED

`ri_k` is a 32-bit slot ("Player N+1 Radar Position"). `ri0` is the constant
magic `0x1687FDFF` (the player, pinned at the radar centre). Each `ri_k` (k≥1)
packs **three fields** — a 2-D position plus an orientation:

| Bits | Field | Encoding |
|---|---|---|
| `0..9`   | **lateral**     (world relX gap, m)    | `clamp(512 + round((512/24)·relX), 0, 1023)` |
| `10..19` | **forward**     (world relZ gap, m)    | `clamp(512 + round((512/24)·relZ), 0, 1023)` |
| `20..31` | **orientation** (relative heading, °)  | `0x167 + round(relHeadingDeg)` |

- `relX = oppWorldX − playerWorldX`, `relZ = oppWorldZ − playerWorldZ` (signed,
  metres, **world frame**). The wheel rotates the `(relX, relZ)` vector by the
  preamble **Heading** channel so the player's forward points "up" — that is why
  Heading rides in the preamble and why a flat/missing Heading makes the radar
  scatter.
- Each axis is a 10-bit field centred at **512** with scale **512/24 ≈ 21.33
  units/m**, covering **±24 m**; out-of-range cars clamp to the field edge.
- bits 20..31 centre on `0x167` (= "aligned"); the firmware draws the car's
  rectangle rotated by this. The plugin derives it from world-motion deltas (no
  sim exposes per-opponent heading), ~0 when cars run parallel.

**Verification** (relZ-anchored car match vs the 60 Hz replay, radar6): bits 0–9
correlate **1.00** with lateral; per-axis regression gives `lateral = 21.33·relX
+ 511`, `forward = 21.30·relZ + 512`. Re-encoding the matched cars with the
formula above reproduces PitHouse's actual `ri` values to **0.047 m median**
error (one field unit), p95 ≤ 0.19 m. Tool: `tools/radar_ri_crack.py`.

### Why the earlier "relZ-only" decode was wrong
A prior pass concluded `ri`'s low-20 bits were a single signed `relZ` (scale
21630, wrap ~48 m) with "no lateral" (claimed to be a separate spotter signal).
That is incorrect: the **lateral lives in the low 10 bits**. Reading all 20 low
bits as one relZ works to ~0.05 m only because the forward field (high 10 bits)
dominates the magnitude and the lateral field perturbs it by ≤ 1023/21630 ≈
0.05 m — under the 0.24 m tolerance that pass used, so the real low-order field
was never noticed. The plugin that shipped the relZ-only formula wrote the
forward value across all 20 bits, so the lateral field decoded to
`(21630·relZ) mod 1024` = garbage → opponents drew at the right distance but a
random sideways offset ("cars sliding sideways / scatter").

### Tier / selection / slotting
- **Tier split** (to fit 115200 baud): a fast tier carries the 131-bit preamble
  + `ri0..ri8`; an overflow tier (no preamble, no magic) carries `ri9..` for
  extra cars. The two carry disjoint cars.
- **Selection**: opponents within ~24 m (the field range) get a slot.
- **Slotting**: each car holds a **stable** slot for as long as it's relevant —
  a car entering/leaving the set does not reshuffle the others. (Re-packing the
  near set into `ri1,ri2,…` every frame made every dot jump the instant cars
  moved.) The player's own slot is `ri0` (magic).

Implemented in `TelemetryFrameBuilder.WriteRadarPair` (field packing) and
`GameDataSnapshot.AssignStableRadarSlots` (stable slot assignment).

## Method

Tooling lives under `tools/` + scratch (not committed):

1. **pcapng → frames**: extract USBPcap bulk transfers, 0x7E-destuff both
   directions, emit `moza_trace`-format JSONL (so `tierdef-decode` /
   `catalog-decode` run on it). The `tools/pcap_to_jsonl.py` extractor depends on
   `extract_moza_frames.py` (now in the moza-simulator repo); reconstruct it if
   absent. On Windows set `PYTHONUTF8=1` (the tools read the CJK-bearing
   `Telemetry.json`).
2. **SimHub replay → ground truth**: a `.telemetry.json` replay is `FF01` +
   per-frame `[01][u32 len][raw-deflate]`, indexed by `.jsonidx`
   (25-byte records: `01 [u24 offset] …`). Each frame inflates to the full AC
   shared-memory JSON (`Graphics.CarCoordinates`, `Opponents.vehicle[].worldPosition`).
3. **Correlate**: align capture↔replay by wall-clock UTC (same machine; refine
   by motion cross-correlation), then regress decoded wire fields against known
   world positions. Use the 8 opponents (varied positions) to decorrelate x/z —
   a single player path can't separate the axes.

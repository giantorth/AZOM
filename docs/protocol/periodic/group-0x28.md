### Group `0x28` host base-settings read (host → dev `0x13`, occasional)

Reads stored base-settings parameters. Group `0x28` = read; group `0x29` =
write companion (see [`group-0x29.md`](group-0x29.md)). Each cmd ID maps to
one slot in the wheelbase parameter store — full table in
[`../devices/wheelbase-0x13.md` § Group `0x28` / `0x29`](../devices/wheelbase-0x13.md).

**Frame layout (request):**

```
7E [N] 28 13 [cmd_id 1..2 B] [00 00 …] [checksum]
```

Request payload is the cmd ID followed by trailing zeros to make a 3-byte
read frame.

**Frame layout (response):**

```
7E [N] A8 31 [cmd_id echo] [value: 2 B] [checksum]
```

`A8` = `0x28 | 0x80`; `0x31` = nibble-swap of `0x13`. Value bytes are BE
u16, decoded per the rs21_parameter.db `ServiceParameter` transform for the
target slot (most are `multiply 0.1` → display unit ×10 raw).

**Observed during connect** (`connect-wheel-start-game.json`, sent twice
about 2 s apart):

| Cmd | Value | DB name | Decoded |
|-----|-------|---------|---------|
| `0x01` | `01 C2` | `limit` | BE u16 = 450 (steering angle limit) |
| `0x02` | `03 E8` | `ffb-strength` | BE u16 = 1000 (raw → 1000 × 0.01 = 10.0 ?) |
| `0x17` | `01 C2` | `max-angle` | BE u16 = 450 |

**Also fires at game start.** Live capture 2026-04-29 (R5 base, W17, in-game) shows the same triplet (`01/02/17`) re-emitted within 1 s of the first game-tick frame, alongside `0x2B/0x13 02 00 00 → 0xAB/0x31 02 00 00` and the slot-03 commit marker. So this read group is also part of the post-connect "telemetry-engaged" handshake, not just the cold-attach phase.

The FFB-strength encoding above does not yet match the `0–10000 → 0–100%`
transform documented in [`../telemetry/service-parameter-transforms.md`](../telemetry/service-parameter-transforms.md);
PitHouse may store strength as a fraction-of-max in this slot rather than
the raw 10000-scale used in the settings UI. Verification welcome.

Plugin does not implement; sim answers via
[`sim/wheel_sim.py`](https://github.com/giantorth/moza-simulator) param-replay tables when
PitHouse polls during connect.

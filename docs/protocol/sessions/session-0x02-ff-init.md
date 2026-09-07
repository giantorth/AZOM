# Session 0x02 FF-record init handshake

PitHouse opens session 0x02 immediately after the management session and
sends a four-record FF-property init handshake. Without this handshake
the wheel silently ignores subsequent dashboard-switch (`kind=4`)
records, and the host's "switch" produces no visible change on the
display.

This doc is the canonical reference for the handshake protocol, the
inner FF-record envelope, and the known-broken shortcut of replaying
captured bytes verbatim.

## Outer wire format

Each FF-property record rides inside a normal SerialStream chunk on
session 0x02 (see [`chunk-format.md`](chunk-format.md) and
[`../wire/frame-format.md`](../wire/frame-format.md)). The chunk payload
is the FF-record envelope; multi-chunk records span multiple sequential
chunks and the wheel reassembles before parsing.

```
[0xFF]                    sentinel (1 byte)
[size:u32 LE]             length of kindAndValue (4 bytes)
[inner_crc:u32 LE]        zlib.crc32 of kindAndValue (4 bytes)
[kindAndValue]            size bytes:
    [kind:u32 LE]         record discriminator
    [payload]             kind-specific, size-4 bytes
```

`size` includes the 4-byte kind prefix; total wire bytes per record =
`size + 9`. The inner CRC covers the entire kindAndValue blob, not
just the payload. Builder is `Telemetry/Sessions/SessionPropertyPushBuilder.WrapFfRecord`.

## Init handshake (host → wheel)

Four FF records, in this order, on session 0x02 within ~150 ms of session
open. Plugin emits them from
[`Telemetry/TelemetrySender.SendSessionInitHandshake`](../../../Telemetry/TelemetrySender.cs).

| Kind | Name             | Wire size | Status in plugin (2026-05-13) |
|-----:|------------------|----------:|-------------------------------|
|    2 | `init_nonce`     |      16 B | Sent. Body partially decoded. |
|    7 | `init_enum`      |      12 B | Sent. Value is the **constant `[3][0]`** — see below. |
|    8 | `init_catalog_a` |  ~1.7–2 KB| **NOT sent.** Decode incomplete; replay tested and locks wheel. |
|   11 | `init_catalog_b` |    ~2.5 KB| **NOT sent.** Decode incomplete; replay tested and locks wheel. |

## Wheel response (wheel → host)

~3.5 s after the host's full four-record burst, the wheel emits two FF
records on session 0x02 acknowledging the handshake:

| Kind | Name           | Wire size |
|-----:|----------------|----------:|
|   10 | `wheel_init_a` |      12 B |
|   16 | `wheel_init_b` |      20 B |

The ~3.5 s gap is consistent with the wheel decompressing and validating
the ~10 KB of inflated kind=8 + kind=11 catalogs against its internal
master tables. Until the host receives both kind=10 and kind=16, the
wheel will not echo `kind=4` dashboard-switch records and will not bind
post-switch tier-defs to display widgets.

### kind=7 — `init_enum` is a constant, NOT a dashboard slot

```
ff 0c 00 00 00  03 28 c2 81  07 00 00 00  03 00 00 00  00 00 00 00
│  └ size = 12  └ inner CRC  └ kind = 7   └ value[0] = 3 (constant)
└ FF property record                                    └ value[1] = 0
```

All **34** kind=7 records in `sim/logs/bridge-20260731-064830.jsonl` are
byte-identical, across several reconnects and a dashboard switch — so the field
cannot be slot-dependent. Matches the `[3 u32][0 u32]` decode in
[`../findings/2026-05-04-init-sequence.md`](../findings/2026-05-04-init-sequence.md).

The plugin previously emitted a dashboard slot index here (0 in practice), on
the mistaken reading that this record "sets the initial active dashboard slot".
With value=0 the wheel's FF-record layer stayed **dormant**: it sent no kind=9
ticker, no kind=10/16 init ack, and never answered a kind=14 device-log request.
Corrected 2026-08-07 in `SessionPropertyPushBuilder.BuildSessionInitField7Body`,
which now takes no argument and emits the constant.

## Runtime property pushes (post-handshake)

After the init handshake, the host pushes individual wheel-integrated-display
settings as one-shot FF records on session 0x02 — one record per user change,
**not** retransmitted periodically (distinct from the kind=9 ticker and the
kind=14/15 log pull below).
Each rides the same envelope above and its own chunk CRC. The inner CRC is
`zlib.crc32(kindAndValue)` and is a deterministic integrity check over
`(kind ‖ value)`, not a nonce (see
[`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md)).

| Kind | Name | Value width | Encoding | Builder |
|-----:|------|-------------|----------|---------|
| 1  | display brightness | u32 LE (0–100) | `size=8`  | `BuildU32Body` |
| 10 | display standby     | u64 LE (ms)   | `size=12` | `BuildU64Body` |
| 5  | display rotation    | **u8** (0/1/2)| `size=5`  | `BuildU8Body`  |

### kind=5 — VGS display-rotation mode

The **VGS (Vision GS)** wheel has an internal IMU and can counter-rotate its
dashboard so it stays upright as the rim turns. `kind=5` selects the mode; the
wheel does all the angle sensing itself — the host streams no angle data.

```
7e <N> 43 17  7c 00 02 01 <seq:u16 LE>   ff 05 00 00 00  <inner_crc32 LE>  05 00 00 00  <mode:u8>   <chunk_crc32 LE> <chk>
                          │                │  └ size = 5 (kind u32 + 1-byte value)      │           └ mode: 0=off, 1=smooth, 2=immediate
                          │                └ FF property record                         └ kind = 5
                          └ sess=0x02, type=0x01 (data)
```

| `mode` | Meaning | inner CRC32 (LE) |
|-------:|---------|------------------|
| 0 | Off — display fixed to the rim (no counter-rotation) | `6d 78 c2 0e` *(inferred, see below)* |
| 1 | Smooth — gradual counter-rotation | `fb 48 c5 79` |
| 2 | Immediate — snap counter-rotation | `41 19 cc e0` |

Capture-verified: `mode=1` and `mode=2` observed on the wire in both VGS
rotation captures (`VGS-rotation-off-smooth-immediate-horizontal{Off,On}.pcapng`,
PitHouse), each sent once at the moment the user changed the setting. Their inner
CRCs match `crc32(struct.pack("<I",5)+bytes([mode]))` exactly, confirming the
record is genuine.

`mode=0` (off) is **inferred, not observed**: it was the pre-capture starting
state and was never re-emitted. `6d 78 c2 0e` is the computed inner CRC for
`crc32(5‖0)` if/when the host emits it.

### Open item — "horizontal" toggle

The two capture files differ only by the PitHouse "horizontal" on/off toggle,
yet are **byte-identical on the wire** apart from session seq/ack counters — the
`kind=5` records (`mode=1`, `mode=2`) are the same in both. So toggling
"horizontal" produced **no distinct wire command** in either capture; it was a
fixed precondition set before recording, not exercised during it. It is a
separate PitHouse on/off toggle that likely sends its own small FF flag (an
un-captured `kind`), but that has **not** been observed. Do not guess its kind
number — capture a session that actually toggles it during recording, then add
it here.

Plugin support: `TelemetrySender.SendDashDisplayRotation(mode)` →
`SessionPropertyPushBuilder.BuildU8Body(kind=5, mode)`; gated to VGS via
`WheelModelInfo.SupportsDisplayRotation`. UI: the display-rotation dropdown in
`DashboardManagementControl` (VGS wheels only). Decode tooling:
`tools/pcap_to_jsonl.py` + `tools/moza_trace.py` (the `verify.py` scratch script
that confirmed the inner CRCs is reproducible from those two primitives).

## Device log pull — kind=14 (request + payload) / kind=15 (receipt)

> Decoded 2026-08-07 from `sim/logs/bridge-20260731-064830.jsonl` (PitHouse,
> KS-Pro-era firmware) with `tools/ff-record-decode`: 1 657 requests, 161
> receipts and 174 payloads, every payload decoding with zero parse failures.
> Supersedes the earlier "brightness baseline" / "heartbeat A+B" readings in
> [`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md)
> and [`../findings/2026-05-04-init-sequence.md`](../findings/2026-05-04-init-sequence.md).

The wheel display runs a Linux (aarch64) application called **MOZADash** with
its own logger. `kind=14` / `kind=15` are a pull-and-acknowledge pair that
drains that log to the host — they are **not** keepalives, and nothing in the
session layer requires them (the plugin ran for months sending neither, with
sessions staying up).

### Request — h2b `kind=14`, value 4 B

```
ff 08 00 00 00  0f ad ec c4  0e 00 00 00  64 00 00 00
│  └ size = 8   └ inner CRC  └ kind = 14  └ maxLines : u32 LE = 100
└ FF property record
```

`maxLines` is the most lines the host will accept. PitHouse asks for **100**
every time (1 657/1 657 records).

### Payload — b2h `kind=14`, value 4 B + zlib

```
[reserved : 4 B] [zlib stream]
```

inflating to a length-prefixed UTF-16**BE** line list — the same idiom
`kind=8` uses:

```
[count   : u32 BE]                       number of lines in this payload
count × ( [byteLen : u32 BE]             length of the line IN BYTES (= chars × 2)
          [text    : UTF-16BE] )
```

Worked example (first payload in the capture, value 3 151 B → 37 226 B inflated):

```
00 00 00 64                          count = 100
00 00 00 b0                          byteLen = 176 (= 88 chars)
00 5b 00 54 00 68 00 75 …            "[Thu Jul 23 06:38:48 2026]
                                      [ld-linux-aarch64.so.1][7]
                                      ./MOZADash(+0x96090) [0x7fa77c6090]"
00 00 00 c2                          byteLen = 194 → next line …
```

Across all 174 payloads the walk consumed the inflated buffer **exactly**
(`consumed == len`, zero trailing bytes), so the layout is complete. Content is
the display application's own log, and it is genuinely diagnostic:

```
[…]Uncaught C++ exception!
[…] /app/libQt5Core.so.5(_ZN16QCoreApplication4execEv+0xa0)
[…] [warning] MEMORY_MEDIUM: System memory usage 98.9%, Process RSS 87MB
[…] [info] Memory details: VmSize=905MB, VmRSS=87MB(18.67%), VmPeak=970MB, …
[…] Debug: set current location:  "en_US"
[…] Debug: recv invalid setting cmd:  24
[…] [error] [json.exception.parse_error.101] parse error at line 1, column 1:
    syntax error while parsing value - unexpected end of input
```

Some lines are Chinese-localised (`内存使用: VmSize=…`), so consumers must treat
the text as full Unicode, not ASCII.

Note the `recv invalid setting cmd: 24` line: the same capture contains a host
FF record with `kind=24` (one per session, otherwise undocumented), and the
display is rejecting it. That is a worked example of the log's value — it
attributes a wire record to a device-side outcome, which no other channel does.

### Receipt — h2b `kind=15`, value 4 B

```
ff 08 00 00 00  <inner CRC>  0f 00 00 00  <linesRead : u32 LE>
```

`linesRead` is how many lines the host consumed; the device **drops that many**
from its buffer. This is what advances the read cursor — without it the next
request re-returns the same head. Observed values are line counts, nothing else:
`{1 ×135, 5 ×10, 60 ×1, 100 ×15}` (100 = a full backlog drain,
1 = the steady state where one new line accrued since the last poll).

Under-acking stalls the cursor; the receipt must carry the count the **device**
sent, not the count the host chose to keep after any host-side filtering.

**Acking is also what keeps payloads small.** The receipt histogram is 15 full
drains and 117 single-line pulls: the first pull on a fresh connection returns
the accumulated backlog (~2.7–3.2 KB compressed), and every pull after that
returns only what has accrued since — typically one line, a couple of hundred
bytes. A peek-only host that never sent kind=15 would re-receive the whole
backlog on every poll instead. This matters beyond bandwidth: b2h chunks on
these sessions are also appended to `ChannelCatalogParser`'s per-session buffer,
which is hard-limit wiped (with a warning, losing in-flight catalog data) at
64 KB. Watch `Parser: buf=<N>B` in the Diagnostics tab's wheel-catalog section
if the pull cadence is ever raised.

### Session

Both host records ride the **FF session** — the same one as the `kind=2`/`kind=7`
init handshake, i.e. `ResolveFfSession()`, the mirror of the tier-def session.

The capture spans several reconnects and shows the pair on **both** `0x01`
(1 436 requests / 135 receipts / 131 payloads) and `0x02` (221 / 26 / 43) — and
in each case the init handshake for that connection is on the same session.
So the rule is "wherever the FF handshake went", not a fixed session number.
Readers must therefore scan **both** catalog sessions for inbound payloads.

### Cadence and silence

The device answers **only when it has new lines** — 1 657 requests produced 174
payloads. Silence is the normal steady state and must not be read as a fault or
used to infer the wrong session was picked. PitHouse polls roughly every 2 s in
some sessions and ~40 s in others; neither is required.

### Plugin support

`TelemetrySender.TickEmitDeviceLogPoll()` (slow path, ~1/min) emits the request
via `PropertyPushQueue.SendU32(kind, value, sessionOverride)`. Inbound records
are lifted off sessions 0x01/0x02 by `Telemetry/Sessions/FfRecordStream` +
`Telemetry/Sessions/FfRecordReader` (CRC-validated — see below), parsed by
`Diagnostics/DeviceLogParser`, and stored in `Diagnostics/DeviceLogStore` for
the Diagnostics tab and the `device-display-log.txt` bundle entry. The receipt
is emitted from the tick thread, never the serial read thread, so the session
seq locks stay off the ack path. Disable with `EnableDeviceLogPull: false`.

### Reading FF records off a mixed stream

FF records are interleaved with the typed sub-msg / catalog TLV stream on the
same session, so a scan for the `0xFF` sentinel **will** hit `0xFF` bytes inside
other records. The inner CRC is the only reliable discriminator: validate
`crc32(kindAndValue) == inner_crc` and resync one byte forward on a miss. Over
the capture that recovered 2 395 real records while rejecting 5 false sentinels.

Two further requirements, both learned the hard way:

- **Drain incrementally**, chunk by chunk, rather than concatenating a whole
  stream and scanning once. The device restarts its outbound seq counter on
  session reopen, and a restart invalidates the in-progress buffer; records
  extracted before that point must survive it. Scanning only at the end
  recovers 21 records where incremental draining recovers 1 627.
- **Dedup retransmits by seq** before appending, or repeated chunk bytes
  desync the record framing.

`tools/ff-record-decode` implements both and mirrors
`Telemetry/Sessions/FfRecordStream` exactly. Prefer it over
`tools/bridge-decode-ff-init`, whose reassembly does neither (and which slices
bodies 4 bytes too long, reading `size` bytes from offset 13 instead of
`size - 4`).

## Why the host must send kind=8 / kind=11

`kind=2` (`init_nonce`) and `kind=7` (`init_enum`) alone are insufficient.
Cross-capture validation in `tools/bridge-decode-ff-init` against
`sim/logs/bridge-20260429-163951.jsonl` shows the wheel responds kind=10
+ kind=16 ONLY after all four records. The kind=8 and kind=11 records
carry zlib-compressed catalogs — kind=8 is a wheel-side dashboard slot
catalog (RpmAbsolute/RpmPercent slot names + wheel-internal property
names) and kind=11 is the FFB-property catalog (~250 increment/decrement
parameter names). See [`../findings/2026-05-07-sess02-ff-kinds-reference.md`](../findings/2026-05-07-sess02-ff-kinds-reference.md)
for the body decoding.

### Body decode — kind=8 and kind=11 record envelope (2026-05-15)

Both inflated catalogs use the **same record envelope**: a header, a
UTF-16-BE name, and a per-record TLV trailer whose `type` determines the
value layout:

```
record = [id:u16 BE] [name_len:u32 BE] [UTF-16BE name (name_len bytes)] [TLV trailer]
TLV trailer = [type:u32 BE] [reserved:u8] [value: type-determined layout]
```

| `type` | Trailer total | Value layout | Where seen |
|------:|--------------:|--------------|------------|
| 0  | 5 B  | (no value; reserved byte = `0x01`) | RpmAbsolute / RpmPercent slot records (kind=8) |
| 2  | 9 B  | `[u32 BE]` | enum / index / bool wheel settings (kind=8) |
| 4  | 13 B | `[u64 BE]` (integer property, e.g. brightness=80, breath interval=3000 ms) | kind=8 |
| 6  | 13 B | `[u64 BE]` interpreted as `double BE` (e.g. `equalizerGain1 = 90.0`, `naturalDamper = 42.0`) | kind=8 |
| 10 | 9 + strlen B | `[strlen:u32 BE] [UTF-16BE string]` (e.g. `__location → "en_US"`) | kind=8 |
| 9  | variable | nested preset block (UTF-16-LE-encoded inner sub-records, partially decoded) | kind=8 |

`kind=11` uses the same envelope but only emits records with the
simplest trailer shape (`type=0`, name-only), one per FFB property name
(`decrementEqualizerGain1`, …). `kind=8` emits the full mix.

Full byte-exact examples and the type=9 inner-block partial decode live
in [`../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md`](../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md).

## **Do not replay captured kind=8 / kind=11 bytes verbatim**

The kind=8 and kind=11 records from a PitHouse capture cannot be shipped
back to the wheel as a static snapshot — the wheel locks. This was
verified destructively on a W17 (CS Pro) wheel on 2026-05-13 and the
binary captures have been deleted from the repo precisely so a future
agent can't grab them as a shortcut.

**Verified failure, 2026-05-13:** the plugin briefly wired
`SendSessionInitHandshake` to ship verbatim kind=8 (2059 B) and kind=11
(2581 B) bytes extracted from `sim/logs/bridge-20260429-163951.jsonl` on
every `StartInner` cycle. With a W17 / CS Pro wheel:

- First emission: handshake completed, wheel began echoing `kind=4`
  switches, dashboard switching worked for ~5 minutes.
- Second/third emission across dashboard-switch restart cycles: wheel
  locked into a state where it stopped responding to any further
  commands and required a physical power-cycle to recover.

Diagnostic bundle: `~/CS-Pro-moza-diagnostics-bundle-20260513-122621.zip`
captured the lock-up. The replay path was reverted in the same session
and the source `.bin` files were deleted (previously at
`Resources/sess02_init_kind{8,11}_pithouse.bin`) so they can't be
re-embedded thoughtlessly. Re-derive byte content from
`sim/logs/bridge-20260429-163951.jsonl` via
`tools/bridge-decode-ff-init` for decode work; do NOT ship them.

The records carry session-bound state that becomes invalid when replayed:

- `kind=2` body offsets 0..3 are a fresh Unix timestamp (the plugin
  already regenerates this — see
  [`../findings/2026-05-07-sess02-ff-kinds-reference.md`](../findings/2026-05-07-sess02-ff-kinds-reference.md)
  body-decode section), but offsets 12..15 vary per capture and are
  almost certainly a derived value (CRC of the timestamp+magic, or a
  session salt the wheel verifies).
- `kind=8` size grows monotonically within a single PitHouse session
  (1740, 1769, 1780, …, 2050 B observed in one capture) which strongly
  suggests it encodes session-cumulative state, not a static blob.
- `kind=11` similarly trends with session activity.

The wheel apparently validates these against state it built up in
parallel during the same session; presenting it with a snapshot from
a different session at handshake re-emission desynchronises that state
beyond what the wheel's input handler can recover from cleanly.

## Required work before re-enabling kind=8 / kind=11 emission

1. ~~Decode the inner structure of kind=8 sub-format B (the wheel-config
   property records — sub-format A is already understood). Identify
   which fields are wheel-static (constants the host can replay) vs
   session-derived.~~ **Done 2026-05-15** for trailer types 0/2/4/6/10
   — see the TLV trailer table above and
   [`../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md`](../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md).
   Type=9 (nested preset blocks) is partially decoded — outer envelope
   known, inner field semantics still TBD. Most type=4/6 values are
   user-settable wheel settings (brightness, EQ gains, intervals); these
   are session-derived in the sense that they reflect user state at
   record-emission time, not in the wheel-validates-cryptographic-token
   sense.
2. Decode `kind=11` body record boundaries (currently parsed as a flat
   `[id:u32 BE][name_len:u32 BE][name:UTF-16-BE]` stream — verify there
   are no embedded sub-fields or session-derived values).
3. Decode `kind=2` body offsets 12..15. Compare against a CRC32 of the
   leading 12 bytes; if not a CRC, treat as a salt and search for the
   generator (e.g. wheel-side nonce echoed in `wheel_init_a`/`b`).
4. Re-derive whether the wheel actually requires the FULL kind=8 / kind=11
   payloads, or whether a minimal valid record (empty list, or only the
   records the host has authority over) is sufficient.
5. Build `BuildSessionInitField8Body` / `BuildSessionInitField11Body` in
   `Telemetry/Sessions/SessionPropertyPushBuilder` that mint per-session-correct
   records. Then emit from `SendSessionInitHandshake` ONCE per cold
   start, gated against re-emission across restart cycles unless the
   wheel itself invalidates session state (e.g. across a sess=0x02
   close+reopen).

Only after all four are answered can `SendSessionInitHandshake` resume
shipping kind=8 / kind=11. The kind=2 / kind=7 records continue to be
emitted; that is necessary but not sufficient to engage dashboard
switching, and the user-visible consequence of insufficient-handshake is
documented (kind=4 silently ignored) but at least non-destructive.

## Related docs

- Body decoding details and capture-by-capture diff:
  [`../findings/2026-05-07-sess02-ff-kinds-reference.md`](../findings/2026-05-07-sess02-ff-kinds-reference.md)
- Why kind=4 needs the full handshake (with wire-trace evidence):
  [`../findings/2026-05-07-sess02-init-protocol-and-stale-catalog.md`](../findings/2026-05-07-sess02-init-protocol-and-stale-catalog.md)
- Inner FF-record envelope detail:
  [`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md)
- `kind=4` dashboard switch wire signal:
  [`../findings/2026-04-30-dashboard-switch-3f27.md`](../findings/2026-04-30-dashboard-switch-3f27.md)

## Decode tools

- `tools/bridge-decode-ff-init <capture>` — reassembles multi-chunk FF
  records from a PitHouse bridge JSONL and (where applicable) zlib-
  decompresses the body. Use this to validate body changes per
  firmware version before reimplementing the host emit.
- `tools/trace-sess02-decode <wire-trace>` — same shape against our
  own wire-trace JSONL. `h2b sess=0x02: no chunks` (or "kind=2/7 only")
  is the signature that the plugin's emit path is incomplete.

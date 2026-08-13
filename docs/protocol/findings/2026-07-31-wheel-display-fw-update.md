# Wheel display-MCU firmware update — staged image, no bulk at apply time

Live capture of the wheel's **display firmware update 1.2.6.8 → 1.2.6.17**
(RS21-W17-MC SW, immediately after the main-MCU update in
[`2026-07-31-wheel-firmware-update-protocol.md`](2026-07-31-wheel-firmware-update-protocol.md);
same capture `sim/logs/bridge-20260731-064830.jsonl`, user clicked update
at t≈1785514975). PitHouse reported success; user-confirmed.

**Headline: the display update uses a completely different path from the
main-MCU update.** No flash-transfer groups (`0x15`–`0x19`), no CDC
reconnect, and — at apply time — no image transfer at all: only **~25 KB
h2b total** crossed the bus in the whole 169 s observation window.

## Display-MCU version query

The display firmware version is **not** on the standard version group.
It answers on the group-`0x43` channel: request `0x04` + 4 zero bytes
(dev `0x17`), reply on `0xC3` = `84 [4 version bytes]`, same
wire-order/display-swap convention as other MOZA versions:

```
7e 05 43 17 04 00 00 00 00 ee            → host request
7e 05 c3 71 84 01 02 08 06 59            ← 1.2.6.8  (before)
7e 05 c3 71 84 01 02 11 06 62            ← 1.2.6.17 (after)
```

PitHouse polls it as part of the wheel-detect catalog sweep
([`2026-04-28-wheel-catalog-read.md`](2026-04-28-wheel-catalog-read.md)).

## Apply-time sequence (t≈4973–5033)

1. t≈4973–4976: **file-transfer session negotiation** on sessions
   `0x03`/`0x04`/`0x06` (`7C 00 0x` chunks + `FC 00 0x` acks) carrying
   UTF-16LE host paths and MD5-tagged temp names:
   `/tmp/_moza_filetransfer_tmp_1785514974189`,
   `C:/Users/<user>/AppData/Local/Temp/__firm_tmp_<ms>_<pid>`,
   `_moza_filetransfer_md5_8c81f985070b39eac29b8fc09562984b` (file size
   field `0x33E` = 830 B), a second file
   `_moza_filetransfer_md5_c4d5220964ffefc1b7475cefd189e1c8`, and a
   reference to **`/config/start.json`**. Total session payload both
   directions ≈ 2–3 KB — manifest/trigger files only.
2. t≈4976–5032: **~56 s of near-idle bus** while the wheel's main MCU
   flashes the display controller internally. Keepalives continue;
   settings polling stays suspended.
3. t=5032.6: the wheel re-announces (display rebooted) — PitHouse runs
   its standard **catalog re-sweep** (`0x43` cmds `04 05 02 09 07 08 0F
   10 11…`), and the `04` probe returns the **new version**
   `84 01 02 11 06` (1.2.6.17).

## Where the image went — staged in advance (attribution open)

The image bytes crossed the bus **before** the apply click: a burst of
≈13,200 large session-`0x01`/`0x02` chunks (~850 KB h2b) ran for ~8 min,
ending ~3.5 min before the user clicked update (t≈1785514290–4770).
Outside that burst the between-updates window is flat keepalive traffic.
No `firm` string markers inside the chunks (expected — mid-file data),
and no other bulk h2b window exists between the two updates, so this
burst is the only candidate carrier for the ~real-sized display image.

**Open questions:**
- Attribution of the staging burst — what UI action triggered it
  (opening the firmware page vs. automatic pre-fetch) is not visible on
  the wire.
- Byte-exact contents of the two small apply-time files (830 B one and
  the `c4d5220964…` file) and of `/config/start.json`.
- Semantics of the `7C 27 0F 00 0x 00` frames sent at apply start.

## Contrast with main-MCU update

| | Main MCU (`0x15`–`0x19`) | Display MCU |
|---|---|---|
| Bulk channel | dedicated groups, 58 B frames | session file-transfer (`0x43` `7C 00`), staged in advance |
| Volume at apply | ~2.74 MB streamed live | ~25 KB (manifest/trigger only) |
| Reconnect | CDC re-enum at initiation | none |
| Version query | group `0x04` probe | group `0x43` cmd `04` → `0xC3` `84`-reply |
| Post-apply | version probe retries ~4 s | catalog re-sweep at ~56 s |

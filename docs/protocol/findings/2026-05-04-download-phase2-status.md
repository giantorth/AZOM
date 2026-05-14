# Dashboard download phase 2 — status (Phase 0 deliverable)

## TL;DR

Phase 2 of session 0x0B dashboard download (the actual file-data transfer) **is present in `bridge-20260501-073603.jsonl`**, contrary to the assumption in `HANDOFF.md` that the protocol stopped at the 249B staging ack. The data exists; full decoding is deferred per the refactor plan's "Defer until decoded" choice for download work. This document records what is observable so a future decoding task has a head start.

## Methodology

Searched all `sim/logs/bridge-2026050*.jsonl` captures for session 0x0B traffic. Ran a per-direction byte counter + content sniff (zlib stream detection, PNG magic check) against captures that had non-trivial 0x0B activity.

## Capture inventory

| Capture | h2b 0x0B | b2h 0x0B | Notes |
|---------|---------:|---------:|-------|
| 2026-04-29..2026-05-01 most | 0 | 0 | no download in capture |
| `bridge-20260501-073603` | 3379 chunks / 181KB | **17684 chunks / 951KB** | PNG image data + 42 zlib streams in b2h |
| `bridge-20260501-134243` | 2081 chunks / partial | 12 chunks | staging ack only (matches HANDOFF) |
| `bridge-20260501-153909` | 2805 chunks / partial | 22 chunks | staging ack only (matches HANDOFF, was the authoritative reference for HANDOFF.md) |
| `bridge-20260503-113616` | 5 chunks | 76 chunks (3923B) | wheel-initiated configJson refresh on 0x0B (NOT download — content matches session 0x09 catalog dump) |
| All other 2026-05-03 | 0 | 0 | no download |

**`bridge-20260501-073603.jsonl` is the only capture with phase 2 data.** Duration of the 0x0B exchange: 283 seconds. Total bytes transferred: ~1.13 MB (host→wheel + wheel→host).

## Phase 2 structural observations

These are facts derivable without a full decode pass; they bound the scope of a future decoding task.

### Phase 2 stream contains real file content

The b2h stream's last 200 bytes ends with `... 0000000049454e44ae426082 1421226c3f` — `49454e44ae426082` is the PNG IEND chunk magic + standard IEND CRC. PNG files end with this byte sequence. So the wheel is pushing image data (likely the dashboard preview PNGs referenced in `WheelDashboardState.PreviewImageFilePaths`).

### 42 zlib streams in b2h

Found 42 occurrences of `78 9c | 78 da | 78 01` (zlib magic bytes) scattered through the b2h stream. Likely each represents one dashboard's mzdash content (or a chunked-up portion). 18 dashboards were requested per HANDOFF.md, so 42 ≠ 18 — the streams are either smaller than per-dashboard granularity, or include images, or both.

### h2b carries the request

h2b first 200 bytes (UTF-16LE decoded): `/home/moza/resource/dashes/Core/Core.mzdash,/home/moza/resource/dashes/Grids/Grids.mzdash,/home/moza/resource/dashes/...` — this is the **request manifest** PitHouse sends listing all dashboards it wants downloaded. The 6174B request format documented in HANDOFF.md is correct for phase 1.

### b2h begins with staging-path response

b2h first 200 bytes (UTF-16LE-decoded path portion): `/tmp/_moza_filetransfer_tmp_1777646209820` — wheel acknowledges with its temp staging path. This is the 249B staging ack from HANDOFF.md, except in this capture the response continues past 249B into phase 2.

## Status

Download is deferred to a later milestone. The existing `Telemetry/Dashboard/DashboardDownloader.cs` (924 LOC) stays in-tree as reference material.

When download work resumes, the starting point is `bridge-20260501-073603.jsonl`, not "needs fresh capture." The phase-2 protocol can be reverse-engineered from existing data.

## Specific decoding tasks for the future download milestone

1. Reassemble the b2h session 0x0B stream by ascending seq (1.13 MB unique).
2. Identify framing between zlib streams — is each preceded by a header (size+CRC+target-path)? PitHouse uses 9-byte headers in other contexts (`SessionDataReassembler.cs:78`); check if this stream uses the same.
3. Decompress each zlib stream and identify by content (mzdash JSON vs PNG image vs other).
4. Cross-reference with the wheel's `EnabledDashboards` list (from session 0x09 configJson) to confirm which dashboard each stream belongs to.
5. Decode the host's h2b stream to find the per-file requests/acks (3379 chunks / 181KB suggests host is doing pacing or per-segment acks).
6. Document MD5 + token semantics: HANDOFF.md flags MD5 `1cd4b5bc861a0b1653e7602cc9608aae` and token `0x0005203d` as hardcoded from a single capture. Check if 073603 uses the same values or different — if different, the values are derivable from session state, not embedded.
7. Verify against `bridge-20260501-134243.jsonl` and `bridge-20260501-153909.jsonl` (staging-ack-only captures) for the request format — confirm 073603 phase 1 matches.

This work would belong in a Phase-0.4-followup task before any code changes to `DashboardDownloadOp`. Estimated effort: 4–8 hours of capture decoding + documentation, before any Op implementation begins.

## Phase 0 task #4 outcome

Marked complete: phase 2 data **exists** in capture 073603. Download remains deferred per the refactor plan. No code changes required for this milestone.

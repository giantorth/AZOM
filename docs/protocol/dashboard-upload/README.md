# Dashboard upload protocol

`.mzdash` file uploads + dashboard config RPCs. Three upload-protocol variants observed across firmware generations.

## Firmware version matrix

| Firmware | Upload session | Sub-msg types | Paths marker | File |
|----------|----------------|---------------|--------------|------|
| 2026-04 (legacy, `09-04-26/dash-upload.pcapng`) | Session 0x01 mgmt | FF-prefix envelopes, 3 fields | n/a (binary fields) | [`path-a-session-01-ff.md`](path-a-session-01-ff.md) |
| 2025-11 (latestcaps) | Session 0x04 | type=0x02/0x03 host, 0x01/0x11 wheel | `0x8A` LOCAL + `0x84` REMOTE | [`path-b-session-04.md`](path-b-session-04.md) |
| 2026-04+ (PCAP captures) | Session 0x05/0x07/0x09 (dynamic via `7c:23` trigger) | type=0x02/0x03 host, 0x01/0x11 wheel | `0x8C` LOCAL + `0x70` REMOTE | [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) |
| 2026-05+ (bridge capture) | Session 0x05 confirmed (still within 0x04..0x0a dynamic range) | type=0x02 metadata, N × type=0x03 content | `0x8C` LOCAL **×2 duplicate** (no REMOTE in metadata) | [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) + [`sess05-bundle-contents.md`](sess05-bundle-contents.md) |
| **2026-07/08 (current, fw 1.2.6.17-era W17)** | Host acquires sess 0x04 per-upload via `PORT-OPEN(5,3)`+`FT-ACT(6,4)`; fresh device-init rebases seqs (data = open-seq+3) | Same 0x02/0x03 host + 0x01/0x11 wheel, **acks on the SAME session** (cross-session 0x04 pairing gone) | Same `0x8C`×2 metadata; content `0x70` REMOTE = bare `/_moza_filetransfer_md5_<hex>` | [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) § 2026-07/08 |

**Plugin** currently implements Path B (session 0x04, `0x84`/`0x8C` markers, BE sizes). Sim implements the 2026-04+ variant (dynamic session, `0x8C` marker). For current PitHouse builds, see [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) and [`sess05-bundle-contents.md`](sess05-bundle-contents.md) — Paths A and B are retained for historical firmware compatibility but neither matches current wire behavior.

## Files

| File | Topic |
|------|-------|
| [`path-a-session-01-ff.md`](path-a-session-01-ff.md) | Path A — session 0x01 host-initiated FF-prefix upload (legacy 2026-04, plugin impl) |
| [`path-b-session-04.md`](path-b-session-04.md) | Path B — session 0x04 device-initiated sub-msg 1/2 (2025-11) |
| [`session-04-root-dir.md`](session-04-root-dir.md) | Session 0x04 device → host root directory listing (2025-11 firmware) |
| [`config-rpc-session-09.md`](config-rpc-session-09.md) | Dashboard config RPC (session 0x09, compressed transfer) |
| [`session-01-mgmt-rpc.md`](session-01-mgmt-rpc.md) | Session 0x01 management RPC envelope |
| [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) | 2026-04+ firmware upload handshake: full sequence, `ff*4` sentinel + 1-byte XOR status, multi-round zlib, TLV firmware variance table, cross-session ack flow (wheel acks on b2h sess=0x04 for any host upload session) |
| [`sess05-bundle-contents.md`](sess05-bundle-contents.md) | 2026-05+ multi-file bundle structure: one upload session carries `.mzdash` + content-addressed PNG dependencies (`/home/moza/resource/images/MD5/<hash>.png`) |
| [`implementation-plan.md`](implementation-plan.md) | Ordered checklist of plugin gaps against the decoded protocol — cross-session ack handling, multi-file bundle, type=0x03 chunking, staging path, compressed-header layout |
| [`6-byte-submsg-header.md`](6-byte-submsg-header.md) | New-firmware 6-byte sub-msg header (2026-04+) |
| [`per-chunk-trailer.md`](per-chunk-trailer.md) | Per-chunk metadata trailer (continuation chunks) |
| [`multi-attempt-interleaving.md`](multi-attempt-interleaving.md) | Multi-attempt upload interleaving in the buffer |

Underlying transport: [`../sessions/`](../sessions/).

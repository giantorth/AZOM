# PitHouse UDP control server: CBOR parse-fail log flood

**Status:** investigated, not fixed. Written up from bug-report bundle `GY9RWKMR`
(plugin 1.5.7) while tracing an unrelated LED-brightness issue.

## Symptom

`moza-log.txt` in a diagnostics bundle is almost entirely one repeated line:

```
2026-08-21 07:57:26.115 DEBUG [PitHouseUdp] CBOR parse failed from 127.0.0.1:60000: Major type 7 is not supported by this subset (only 0, 3, 4, 5).
```

Measured in that bundle:

| | |
|---|---|
| Ring entries used by this one line | **4 641 of 5 000 (93 %)** |
| Distinct message texts | 1 |
| Distinct peers | 1 — `127.0.0.1:60000` (source port), every datagram |
| Rate while active | ~19–20 per second |
| Window | 07:57:26 → 08:09:21 |

`MozaLog`'s ring is 5 000 entries, so the flood evicted everything older than
07:57:26 — the plugin had been running since **06:12:45** (per the frozen startup
capture in the same bundle), and roughly **1 h 45 min of log** was gone before the
bundle was taken. That is the real cost: the flood destroys the diagnostic value of
`moza-log.txt` for any machine that has a CBOR-speaking peer on the control port.

## Mechanism

The plugin's PitHouse stub binds the protocol-fixed control port
`127.0.0.1:40288` (`Sdk/PitHouseUdp/MozaControlUdpServer.cs`, `ControlPort = 40288`)
and decodes every datagram as CBOR:

- `Sdk/PitHouseUdp/MozaControlUdpServer.cs` — `HandleDatagram` catches the decode
  exception and logs it at Debug, once **per datagram**, with no dedupe and no rate
  limit. It also pushes a `parse-fail` row into the 20-entry `RecentRequests` buffer.
- `Sdk/Cbor/CborReader.cs` — `ReadItem`'s `default:` arm throws
  `CborFormatException($"Major type {majorType} is not supported by this subset (only 0, 3, 4, 5).")`.
  The reader implements a deliberate subset: major 0 (uint), 3 (text), 4 (array),
  5 (map). Unsupported: **1** (negative int), **2** (byte string), **6** (tag) and
  **7** (float / `true` / `false` / `null` / simple values).

Major type 7 is what a peer sends for booleans, nulls and floats — i.e. entirely
ordinary CBOR. So a client speaking real CBOR at us gets every one of its packets
rejected, and each rejection costs a log line.

Two independent problems, and they want different fixes:

1. **The log line is unbounded.** Even after the decoder is fixed, any malformed or
   foreign traffic on that port can flood the ring again. A repeated-message rate
   limit is the durable fix.
2. **The decoder cannot parse valid CBOR.** Every packet from this peer is dropped,
   so whatever it was asking for never worked. Whether that matters depends on who
   the peer is — see below.

## What is not established

- **Who the peer is.** Source port `60000` on loopback, nothing else identifies it.
  The stub exists for PitHouse-protocol clients (RallySimFans is the confirmed
  consumer of `settings.ini` → `[Application] udpPort`), but this bundle does not
  prove it was RSF. Do not assume.
- **What the payloads contain.** The bundle carries no capture of the UDP traffic —
  `SerialTrafficCapture` only records serial pipes. So it is unknown whether the
  major-7 item sits in the `{Head, Payload}` envelope (a `ReplyPort` as a float, a
  boolean flag) or deeper in a payload the plugin would have ignored anyway.
- Bundle settings were `SdkEmulationEnabled: false`, `UdpControlEnabled: true` — so
  the CoAP stub was off and only the UDP control server was listening.

## Candidate fixes

1. **Rate-limit / dedupe the log line** (`MozaControlUdpServer.HandleDatagram`).
   Keep the first occurrence per peer, then collapse repeats — e.g. one line per
   30 s per remote with an occurrence count, matching how other hot-path Debug lines
   in this codebase are gated. Cheap, no protocol risk, restores the ring buffer.
   The `RecentRequests` rows already give the UI a per-packet record, so nothing is
   lost by not logging each one.
2. **Extend `CborReader` to major type 7** (and probably 1 and 2 while there):
   `false`/`true`/`null`/`undefined` simple values, the 1-byte simple-value form, and
   half/single/double floats. This is the fix that makes the peer actually work. It
   touches the SDK decode path that the CoAP stub and the UDP control server share,
   so it needs its own review — the reader's length caps and depth limits exist
   because it parses untrusted loopback input.

## Diagnostics gap worth closing alongside

The diagnostics dump has no SDK / PitHouse-UDP section, so `RecentRequests`
(including the `parse-fail` rows, their peer and their timing) never reaches a
bundle. A bundle from a machine with this flood therefore shows the *symptom* in the
log but none of the per-packet detail the server already retains. Adding that section
would have answered "who is the peer" without a packet capture.

### Session `0x02` — host response: version 0 URL subscription (CSP)

For CSP wheels, Pithouse responds to the channel catalog by **echoing back
the URLs** the wheel just advertised. Wheel firmware resolves
compression types internally (the wheel knows that `v1/gameData/Rpm` is
`uint16_t` because that's part of its built-in metadata), so the host
doesn't need to declare bit widths — only confirm subscription.

> **Used by:** CSP wheel (W17 family). VGS / KS Pro use the v2 compact
> format instead (see [`version-2-compact-vgs.md`](version-2-compact-vgs.md)).

### TLV stream layout

```
[0xff]                                              — sentinel / reset
[0x03] [04 00 00 00] [01 00 00 00]                 — config (value=1)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel subscription (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE]      — end marker
```

**Identical structure** to the wheel's catalog message
([`session-02-channel-catalog.md`](session-02-channel-catalog.md)) — only
the direction differs.

### Tag detail

| Tag | Field | Notes |
|-----|-------|-------|
| `0xff` | sentinel | Reset marker; signals "host subscription stream begins" |
| `0x03` | config param | Constant `1` — same value on both directions |
| `0x04` | channel subscription | Echo of channel index + URL the wheel advertised. Host can omit channels it doesn't intend to push |
| `0x06` | end marker | 4-byte length (`04`) + 4-byte LE u32 confirmed channel count |

### Why URLs not numeric IDs

V0 trades wire compactness for forward-compatibility. The wheel's
firmware metadata maps URL → compression code → bit width internally;
the host doesn't need to know any of that. Adding a new channel to the
firmware doesn't require host updates as long as URL conventions hold.

### Double-send

Pithouse sends the v0 subscription **twice** in rapid succession:

1. Immediately after session 0x02 opens.
2. Again after the wheel's `fc:00` ACKs for session 0x02 arrive.

This redundancy is observed across multiple captures and is not a
retransmit triggered by missing ACKs — both sends precede any
subsequent telemetry. Plugin's
[`TierDefinitionBuilder.BuildV0UrlSubscription`](../../../Telemetry/Frames/TierDefinitionBuilder.cs)
mirrors the double-send for VGS-protocol parity.

### Worked example: confirm CSP `Rpm` subscription

```
04                                  — tag
0F 00 00 00                         — size = 15 (1 byte index + 14 byte URL)
0A                                  — ch_index = 10 (alphabetic 'R' position)
76 31 2F 67 61 6D 65 44             "v1/gameD"
61 74 61 2F 52 70 6D                "ata/Rpm"
```

20-byte TLV entry on wire including `04` tag and 4-byte length prefix.

### Channel ordering

Indices follow the wheel's catalog ordering verbatim — host doesn't
reorder, it just echoes. Ordering is **alphabetical by URL** across the
full catalog (1-based), per
[`../telemetry/channels.md` § Channel ordering](../telemetry/channels.md).

### Cross-references

- [`session-02-channel-catalog.md`](session-02-channel-catalog.md) —
  same TLV form sent by the wheel in the opposite direction
- [`version-2-compact-vgs.md`](version-2-compact-vgs.md) — VGS uses a
  binary-encoded format with bit widths instead of URLs
- [`tag-03-config-param.md`](tag-03-config-param.md) — config-param
  value semantics by version / direction

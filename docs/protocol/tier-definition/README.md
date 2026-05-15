# Tier definition protocol

Session 0x01/0x02 two-way handshake: wheel declares its channel catalog, host tells wheel how to decode incoming telemetry. TLV-encoded, transported as `7c:00` session data chunks.

> **2026-05-14 update — sess=0x01 typed sub-msg framing.** A separate
> tier-def transport on **sess=0x01** with non-TLV `[type:u8][size_LE u32][body]`
> framing was decoded from live PitHouse traffic. Wheel announces catalog
> as `type=0x04` records (URL ASCII + idx); host emits per-tier
> subscriptions as `type=0x01` records (16-byte `idx/comp/width/reserved`
> quads); string channels are pushed out-of-band as `type=0x05` records
> (ASCII bytes, not bit-packed). Full reference:
> [`../sessions/session-0x01-channel-protocol.md`](../sessions/session-0x01-channel-protocol.md).
> Discovery: [`../findings/2026-05-14-sess01-channel-protocol-and-string-values.md`](../findings/2026-05-14-sess01-channel-protocol-and-string-values.md).
>
> The TLV-based pages below describe the **sess=0x02 FF-record path**, which
> still carries the master channel catalog (kind=8) and FFB property
> catalog (kind=11) in parallel. Whether 2026-04+ firmware accepts a
> TLV-style tier-def on sess=0x02 OR strictly requires the sess=0x01
> typed sub-msg form is not yet verified — the bridge capture only
> observed PitHouse using the sess=0x01 path. Plugin implementation
> work-in-progress: see tasks #13–#16 in the active task list.

| File | Topic |
|------|-------|
| [`handshake.md`](handshake.md) | Full bidirectional sequence from frame traces |
| [`session-01-device-desc.md`](session-01-device-desc.md) | Session 0x01 device description (both directions, both models) |
| [`session-02-channel-catalog.md`](session-02-channel-catalog.md) | Session 0x02 channel catalog (wheel → host) |
| [`version-0-url-csp.md`](version-0-url-csp.md) | Host response: version 0 URL subscription (CSP wheel) |
| [`version-2-compact-vgs.md`](version-2-compact-vgs.md) | Host response: version 2 compact tier definitions (VGS wheel) |
| [`tag-03-config-param.md`](tag-03-config-param.md) | Tag `0x03` config parameter |
| [`chunking.md`](chunking.md) | Chunking rules (both versions, both directions) |

Underlying transport: see [`../sessions/`](../sessions/).

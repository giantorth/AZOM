# SimHub plugin implementation

Notes on how the SimHub plugin replicates the protocol — phases, session handling, schema-tolerant parsing.

| File | Topic |
|------|-------|
| [`startup-phases.md`](startup-phases.md) | Connect-time phase sequence (probe → handshake → tier-def → telemetry) |
| [`session-management.md`](session-management.md) | Session open/close, port allocation, concurrent map |
| [`tier-impl.md`](tier-impl.md) | Tier-definition implementation: how plugin builds the version-2 compact response |
| [`reassembly-fallback.md`](reassembly-fallback.md) | Session-data reassembly: zlib located by magic, verified by envelope sizes or Adler-32 trailer, one message at a time |

PitHouse-divergence findings that drove specific impl choices: [`../findings/`](../findings/).

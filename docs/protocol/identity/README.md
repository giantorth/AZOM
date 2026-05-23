# Identity & probes

Device discovery, identity-string responses, sub-device wrapping, and per-device identity quirks.

| File | Topic |
|------|-------|
| [`wheel-probe-sequence.md`](wheel-probe-sequence.md) | Standard probe order during connect; wrapped + unwrapped response shapes |
| [`display-sub-device.md`](display-sub-device.md) | Display sub-device responses wrapped in `0x43`; handling inside VGS-class wheels |
| [`known-wheel-models.md`](known-wheel-models.md) | Observed model-name strings; ES wheel identity caveat |
| [`hub-base-cascade.md`](hub-base-cascade.md) | Hub (`0x12`) and base (`0x13`) identity bytes are byte-identical across CSP/KSP |
| [`dev-type-table.md`](dev-type-table.md) | Per-wheel 4-byte dev-type field |
| [`pedal-0x19.md`](pedal-0x19.md) | Dev `0x19` pedal identity (KS Pro firmware) |
| [`device-catalog-manifest.md`](device-catalog-manifest.md) | CoAP `/MOZARacing/ProductDevice` device-list and per-device manifest format — six product types, parent/child topology, byte-exact CBOR field order |

Per-device command tables (settings, output, calibration): see [`../devices/`](../devices/).

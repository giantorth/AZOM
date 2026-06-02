# Glossary

One-line definitions for jargon used throughout `docs/protocol/`. Hardware names, firmware-build labels, and protocol-layer terms.

## Software / vendors

| Term | Meaning |
|------|---------|
| **PitHouse** / **Pit House** | Moza's official Windows configuration + dashboard tool. Reference implementation we reverse-engineer against |
| **SimHub** | Third-party sim racing dashboard tool. Hosts the plugin in this repo |
| **Boxflat** | Open-source Linux Moza driver ([github.com/Lawstorant/boxflat](https://github.com/Lawstorant/boxflat)). Original reverse-engineering source |
| **plugin** | Unqualified — refers to this repo's SimHub plugin |

## Wheelbases

| Term | Meaning |
|------|---------|
| **base** | Wheelbase motor unit (device `0x13`) |
| **R9** | Moza R9 base — 9 Nm direct-drive |
| **R12** | R12 base — 12 Nm |
| **R21**, **R25**, **R27** Ultra | Higher-torque bases |
| **D11** | A base model (omits internal bus 5 in `monitor.json`) |
| **hub** | Composite USB endpoint (device `0x12`). Identity bytes byte-identical to base |

## Wheels

| Term | Meaning |
|------|---------|
| **VGS** | Moza VGS Formula wheel — has integrated display, version 2 compact tier defs |
| **CS** / **CS V2.1** | Moza CS wheel — integrated display, version 2 compact tier defs |
| **CSP** | Moza CSP wheel — integrated display, **version 0 URL-subscription** tier defs (different from VGS/CS) |
| **KS Pro** / **KSP** | Moza KS Pro wheel — newer firmware era (2026-04+), dynamic upload sessions |
| **ES** | Moza ES wheel — has identity caveat (responses don't match standard pattern; see [`identity/known-wheel-models.md`](identity/known-wheel-models.md)) |
| **FSR V1** | First-gen Moza FSR display wheel (box name "FSR1"). Identity: model-name `FSR`, hw `RS21-D03-HW FW-C`. Uses the **group `0x42`** fixed-schema display push (no tier-def/catalog) — distinct from FSR V2. See [`devices/wheel-0x17.md`](devices/wheel-0x17.md) § Group 0x42 |
| **FSR V2** | Newer Moza FSR display wheel — firmware reports model-name **`W13`**. Uses the standard tier-definition telemetry path (NOT group `0x42`). A different wheel from FSR V1 despite the shared "FSR" branding |
| **RS21** | Internal product family name appearing in command DB (`rs21_parameter.db`) and identity strings (`RS21-W08-HW SM-C`, `RS21-D03` for FSR V1, etc.) |

## Other peripherals

| Term | Meaning |
|------|---------|
| **MDD** | Standalone Moza display peripheral (device `0x14`). Distinct from wheels with built-in screens |
| **AB9** | Moza AB9 active shifter (separate USB device, settings via `Group 0x1F → dev 0x12`) |
| **S09 CM2** | A dash variant — connects as bus 19 directly off bus 2 |

## Firmware eras

| Era | Characteristics |
|-----|-----------------|
| **2025-11** | Older firmware. Dashboard upload via session 0x04 with `0x8A` LOCAL marker |
| **2026-04** (legacy) | Path A upload: session 0x01 mgmt with FF-prefix envelopes |
| **2026-04+** (current) | Dynamic upload session (0x05 or 0x06) opened on demand via `7c:23` trigger; `0x8C` LOCAL marker |

## Protocol layers

| Term | Meaning |
|------|---------|
| **frame** | Single wire unit: `7E [N] [group] [device] [payload] [checksum]` |
| **N** / **payload length** | Byte count of payload only (1–64) |
| **group** | Command category byte (e.g. `0x3F` wheel config, `0x43` telemetry/sessions) |
| **device** | Internal-bus address byte (e.g. `0x17` wheel) |
| **escape** / **byte stuffing** | When body byte equals `0x7E`, sender doubles it on wire to disambiguate from frame start |
| **session** | TCP-like virtual channel inside the SerialStream layer. Identified by 1-byte session ID (0x01–0x0a etc.) |
| **port** | Synonym for session-byte allocation slot during session open |
| **sub-msg** / **sub-message** | Application-layer message inside a session-data chunk stream |
| **TLV** | Tag-length-value encoding (used by tier defs, channel catalog) |
| **tier** | A telemetry update-rate bucket. `package_level` IDs `30`, `500`, `2000` correspond to ~30 Hz, 2 Hz, 0.5 Hz update rates. See [`telemetry/tiers.md`](telemetry/tiers.md) for the concept reference |
| **tier def** / **tier definition** | Host's response to wheel's channel catalog declaring which channels go in which tier |
| **package_level** | Per-channel cadence selector (ms interval) in `Telemetry.json` — the field that assigns a channel to a tier |
| **channel catalog** | Wheel's session-0x02 declaration of every telemetry channel it can decode |
| **flag byte** | Byte 4 of `7D 23` live telemetry header — selects which tier this frame carries |
| **keepalive** / **heartbeat** | Periodic group `0x00` to known device IDs (~1 Hz) |
| **probe** | Identity-query frame sent during connect (groups `0x07`/`0x08`/`0x0F`/`0x10`) |

## Files / data sources

| Term | Meaning |
|------|---------|
| **rs21_parameter.db** | SQLite DB shipped in PitHouse install (`Pit House/bin/`). 919 commands, authoritative source |
| **monitor.json** | PitHouse file mapping internal-bus topology per base model |
| **Telemetry.json** | PitHouse file enumerating all 410 telemetry channels |
| **mzdash** | PitHouse dashboard package (zip-like) |
| **configJson** | Wheel-stored JSON describing installed dashboards (see [`dashboard-upload/config-rpc-session-09.md`](dashboard-upload/config-rpc-session-09.md)) |
| **fw_debug** | Firmware debug-log subscription (intentionally not implemented in plugin) |

## Captures + analysis

See [`../../usb-capture/CAPTURES.md`](../../usb-capture/CAPTURES.md) for the full per-capture inventory (wheel model, software, scenario, observed traffic).

## Endianness

Big-endian (BE) and little-endian (LE) both appear:

- **BE**: tier-def TLV sizes, dashboard upload uncompressed-size headers, identity byte counts
- **LE**: session ACK seq counters, CRC32 trailers, sequence counter values, zlib pre-headers

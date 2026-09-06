### Hub (`0x12`) and base (`0x13`) identity are byte-identical aliases

Real wheelbase firmware answers the identity probe cascade on **both**
device IDs `0x12` (hub / main controller) and `0x13` (base / motor
controller) with the **same** values. They are aliases for a single
physical device exposing two bus addresses. Probes on either ID return the
same name, HW version, FW version, serial, capability flags, and hardware
ID byte-for-byte.

### Verified on

| Wheelbase | Capture |
|-----------|---------|
| CSP on R9 base (`346e:0002`) | `usb-capture/12-04-26-2/moza-startup-1.pcapng`, `usb-capture/csp_r9_*.json` |
| KSP on R12 base | `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng`, `usb-capture/kspro_*.json` |

### Probe cascade

Identity is queried via groups `0x02`, `0x04`, `0x05`, `0x06`, `0x07`,
`0x08`, `0x09`, `0x0F`, `0x10`, `0x11`. See
[`wheel-probe-sequence.md`](wheel-probe-sequence.md) for the per-group
request format. Each command sent to dev `0x12` and dev `0x13` returns the
**same** response payload byte-for-byte.

### Example: R9 base identity (CSP capture)

| Group / cmd | Field | Value (16-byte ASCII) |
|-------------|-------|-----------------------|
| `0x07 / 01` | name part 1 | `R9 Black # MOT-1` |
| `0x07 / 02` | name part 2 | `-V01` |
| `0x08 / 01` | HW version | `RS21-D01-HW BM-C` |
| `0x08 / 02` | HW sub | `U-V40` |
| `0x0F / 01` | FW version | `RS21-D01-MC WB` |
| `0x10 / 00` | serial part 1 | `R9BASE0000000000` |
| `0x10 / 01` | serial part 2 | `R9BASE0000000001` |
| `0x06` | hardware ID (12 B) | all-zero placeholder in capture |
| `0x05 / 0000 0000` | capabilities (4 B) | varies per base — KSP `01 02 4B 00` |

These are returned from BOTH `dev=0x12` AND `dev=0x13` queries.

### Implementation

Sim: [`_build_device_identity`](https://github.com/giantorth/moza-simulator) installs the
`base_identity` block under both `dev=0x12` and `dev=0x13` keys
explicitly. Plugin reads identity from either alias and stores in the same
field — there's no "hub identity" vs "base identity" distinction
internally.

### Implication

Implementations must NOT synthesize differing values between hub and base.
A bus snooper that observes only `dev=0x12` traffic can answer `dev=0x13`
probes by replaying the same bytes (and vice versa). Conversely, a sim
that diverges (e.g. forgets to install base_identity at one of the two
addresses) leaves PitHouse unable to enumerate that side and stalls the
detection phase.

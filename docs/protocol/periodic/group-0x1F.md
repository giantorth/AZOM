### Group `0x1F` host hub-config polling (host → dev `0x12`, ~3 Hz)

PitHouse polls a fixed set of main-controller settings via `Group 0x1F`. Each
poll is a 1–3 byte sub-cmd; the wheel echoes the sub-cmd and appends the
current value. Sub-cmd `0x4F 0X` is the read-companion of `0x4E 0X`
(get-/set-spring-gain, etc.) — see
[`../devices/main-hub-0x12.md` § Group `0x1F`](../devices/main-hub-0x12.md).

**Frame layout (request):**

```
7E [N] 1F 12 [sub-cmd 1..3 B] [checksum]
```

**Frame layout (response):**

```
7E [N] 9F 21 [sub-cmd echo] [value bytes] [checksum]
```

`9F` = `0x1F | 0x80`; `0x21` = nibble-swap of `0x12`.

**Verified sub-cmds and response shapes** (from
[`sim/wheel_sim.py`](https://github.com/giantorth/moza-simulator) `_HUB_CFG_VALUES`,
captures: `pithouse-switch-list-delete-upload-reupload.pcapng`,
`moza-startup.pcapng`, `putOnWheelAndOpenPitHouse.pcapng`):

| Req payload | Resp payload | DB name | Notes |
|-------------|--------------|---------|-------|
| `0a` | `0a 01` | MainComCtrl_GetCompatMode? | |
| `0f` | `0f 00` | unknown | possibly status |
| `10` | `10 27 10` | unknown | value `0x2710` = 10000 (capacity?) |
| `18` | `18 00 00` | unknown | |
| `19` | `19 00 00` | unknown | |
| `20` | `20 00 00` | unknown | |
| `21` | `21 00 00` | unknown | |
| `23` | `23 00 00 00 00` | unknown | 4-byte zero |
| `25` | `25 27 10` | unknown | value `0x2710` = 10000 |
| `4d` | `4d 00` | MainComCtrl_GetInterpolation | |
| `17 00` | `17 00` | MainComCtrl_GetModeGameCompat | echo only |
| `34 00` | `34 00` | MainComCtrl_GetWorkMode? | echo only |
| `36 00` | `36 00` | MainComCtrl_GetDefaultFFBStatus? | echo only |
| `46 00` | `46 00` | MainComCtrl_GetBleMode | echo only |
| `4c 00` | `4c 00` | unknown | echo only |
| `4e 0X ff` | `4e 0X ff` | KS Pro per-port status | X = 8..0B (spring/damper/inertia/friction port) |
| `4e 0X` | `4e 0X` | VGS variant | response shape unconfirmed |
| `4f 0X 00` | `4f 0X ff 00` | get-{spring,damper,inertia,friction}-gain | X = 8..0B |
| `55 00 …` | `55 00 ` + `43 ba 9b 40` | calibration scalar | BE float ≈ 373.21 |
| `56 00 …` | `56 00 ` + 12-byte triple | calibration triples | LE floats `8000.0`, `4000.0`, `10000.0` |

The `0x4F 0X 00` cycle (X cycles `08`→`0B`) seen in earlier docs corresponds
to spring → damper → inertia → friction gain reads. The trailing `0xFF` byte
in the response is a port/status nibble inserted before the raw value byte.

Plugin does not implement these polls — the sim answers them only because
real captures show PitHouse hammers them every connect.

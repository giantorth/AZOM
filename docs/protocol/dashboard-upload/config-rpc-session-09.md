### Dashboard config RPC (session 0x09, compressed transfer)

> **Schema differs across firmware eras** — `rootDirPath` field added in 2025-11; `enableManager.dashboards` factory-populated in 2026-04+. Captures: multiple. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

Chunk format is standard 9-byte compressed envelope (`flag + comp_sz + uncomp_sz + zlib`). Both directions use zlib-compressed JSON.

**Schema differs between firmware versions.**

**2026-04 firmware** (from `dash-upload.pcapng`):

Host → device `configJson()` canonical library list:
```json
{"configJson()":{"dashboards":["DNR endurance","Formula 1","GT V01","GT V02","GT V03","JDM Gauge Style 01","JDM Gauge Style 02","JDM Gauge Style 03","Lovely Dashboard for Vision GS","Rally V01","m Formula 1","rpm-only"],"dashboardRootDir":"","fontRootDir":"","fonts":[],"imageRootDir":"","sortTags":0},"id":11}
```

Device → host state (3 sequential blobs: `disabledManager` first, cleared mid state, then `enabledManager`):
```json
{"TitleId":4,"disabledManager":{"deletedDashboards":[],"updateDashboards":[{"createTime":"...","dirName":"rpm-only","hash":"...","id":"{uuid}","idealDeviceInfos":[{"deviceId":16,"hardwareVersion":"RS21-W08-HW SM-DU-V14","networkId":1,"productType":"Display"}],"lastModified":"...","previewImageFilePaths":[],"resouceImageFilePaths":[],"title":"rpm-only"}]},"enabledManager":{"deletedDashboards":[],"updateDashboards":[]},"imagePath":[{"md5":"...","modify":"...","url":"..."},...]}
```

**2025-11 firmware** (from `automobilista2-wheel-connect-dash-change.pcapng`) — renamed keys, different structure:

Host → device `configJson()` canonical library list:
```json
{"configJson()":{"dashboards":["Core","Grids","Mono","Nebula","Pulse","Rally V1","Rally V2","Rally V3","Rally V4","Rally V5","Rally V6"],"dashboardRootDir":"","fontRootDir":"","fonts":[],"imageRootDir":"","sortTags":0},"id":11}
```

Device → host state (single blob, no 3-sequence split):
```json
{"TitleId":1,"configJsonList":["Core","Grids",...,"Rally V6"],"disableManager":{"dashboards":[],"imageRefMap":{"MD5/abc.png":1,...},"rootPath":"/home/moza/resource/dashes"},"displayVersion":11,"enableManager":{"dashboards":[{"createTime":"","dirName":"Rally V1","hash":"...","id":"...","idealDeviceInfos":[{"deviceId":17,"hardwareVersion":"RS21-W08-HW SM-DU-V14","networkId":1,"productType":"W17 Display"}],"lastModified":"2025-11-21T07:45:36Z","previewImageFilePaths":["/home/moza/resource/dashes/Rally V1/Rally V1.mzdash_v2_10_3_05.png"],"resouceImageFilePaths":[],"title":"Rally V1"},...],"imageRefMap":{},"rootPath":"/home/moza/resource/dashes"}}
```

Key schema differences:

| Field | 2026-04 | 2025-11 |
|-------|---------|---------|
| Manager keys | `disabledManager` / `enabledManager` (with "d") | `disableManager` / `enableManager` (no "d") |
| Dashboard array | `updateDashboards` | `dashboards` |
| Also has | `deletedDashboards`, `imagePath` (top-level) | `imageRefMap` (nested), `rootPath`, `displayVersion`, `configJsonList` |
| `productType` | `"Display"` | `"W17 Display"` |
| `deviceId` | 16 | 17 |
| State blobs | 3 sequential (disable, empty, enable) | 1 blob |
| `TitleId` | 4 | 1 |

Both schemas list same per-dashboard metadata: `title`, `dirName`, `hash`, `id`, `idealDeviceInfos`, `lastModified`, `previewImageFilePaths`. Simulators must emit schema matching firmware host expects.

### What the state blob does NOT contain — active-dashboard signal

Neither firmware schema includes a field identifying **which dashboard the wheel is currently rendering**. The state push lists installed/enabled/disabled dashboards and their metadata, but there is no `activeSlot`, `currentDashboard`, `selectedIndex`, or equivalent field in any observed capture across either schema version.

Active-dashboard state is carried instead by the channel-config burst's `28:00` / `28:01` readbacks on group `0x40`. See [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md) §"28:00/28:01 response format" for the wire form and decode. The wheel retains the loaded dashboard across power cycles, so this readback is the authoritative resume signal after plugin restart / SimHub reload.

A host that relies on the configJson state blob alone for active-dashboard tracking will desync from the wheel after any restart cycle (host re-applies its saved profile; wheel keeps rendering whatever it was rendering). The only positive in-protocol confirmation of a switch is the wheel's echo of `kind=4` FF-records on session 0x02, which only fires when the host actually sends a switch — not at startup.

### configJson state `rootDirPath` changed between firmware versions

| Firmware | `rootDirPath` | `rootPath` (enableManager/disableManager) |
|----------|---------------|-------------------------------------------|
| 2025-11 | `/home/moza/resource` | `/home/moza/resource/dashes` |
| 2026-04 | `/home/root/resource` | `/home/root/resource/dashes` |

Sim updated 2026-04-24 to emit the `/home/root` variant. Previewed upload paths in session 0x04 type=0x03 sub-msg use `/home/root/resource/dashes/<Name>.mzdash` (flat — no subdirectory unlike older `/home/moza/resource/dashes/<Name>/<Name>.mzdash`).

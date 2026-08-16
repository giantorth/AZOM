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

### The library list is the wheel's enable authority AND slot table (2026-08-16, wire-verified)

The host's `configJson()` `dashboards` list is not informational — the wheel
**syncs against it** on receipt:

- **Enable**: a wheel-side dashboard whose `dirName` is in the list flips
  from `disabledManager` to `enabledManager` (observed flip ≈108 ms after
  the list re-send). Uploads always land in `disabledManager`; PitHouse
  enables them by re-sending its list right after the upload's type=0x11.
- **Sweep**: an ENABLED wheel-side dashboard absent from the list is
  **deleted** (PitHouse connect removed every plugin-uploaded dash not in
  its library).
- **Ghosts**: a listed name the wheel has NO files for becomes an enabled
  registry entry that fails with `dash load error` when cycled onto. Never
  declare names that aren't verifiably installed. (Cleanup: delete the ghost
  rows via `completelyRemove`, or let a PitHouse connect sweep them.)
- **Slot table**: the wheel adopts the list verbatim — order included — as
  its `configJsonList`, which is the slot numbering used by kind=4 switches
  and wheel-side cycling. PitHouse always sends the list **ordinal-sorted**
  (uppercase before lowercase: `Core < … < Simple… < generic… < jrams… <
  porn`); an unsorted list reorders the wheel's slots on every send.

Host-side bookkeeping this implies (all in `TelemetrySender`): build the
wire list from the wheel's current enabled `dirName`s + recent intentional
enables − recent intentional removes, ordinal-sorted
(`BuildWireLibraryList`); adopt the declared list into the cached state's
`ConfigJsonList` immediately on send (`AdoptDeclaredLibraryList`) so
name→slot mappings never lag the wheel.

### Envelope `comp_size` = zlib length + 4

The 9-byte envelope's `comp_size` field counts the zlib stream **plus 4**
(same +4 convention as the upload bundle preamble's `total_compressed`).
PitHouse: 204-byte zlib → `comp_size=208`. A bare-length value makes the
wheel slice the stream 4 bytes short and silently discard the message —
this is why plugin `configJson()` replies were ignored for months while
looking well-formed.

### RPC calls — fixed method ids, config session, no acks

- Host→wheel management RPCs ride the **config session itself** (0x09, or
  0x0a on KS Pro-era firmware) — not a separate channel. Chunks sent on a
  session the wheel didn't device-init are dropped without any fc:00.
- The JSON `id` field is a **method discriminator**, not a correlation
  counter: `configJson()` = 11, `completelyRemove()` = 10 (both PitHouse
  deletes used 10). A call with a wrong id is silently ignored.
- `completelyRemove(id-string)` deletes a dashboard by its per-entry `id`;
  PitHouse's "remove" and "delete" menu items both emit this one verb,
  followed by a library-list re-send without the name. The wheel answers
  with a TitleId=4 delta push (`deletedDashboards`), not a textual reply.
- **The wheel never fc:00-acks config-session chunks** on current firmware
  — host sends must be fire-once (retransmit-tracking a config chunk
  re-sends it forever; observed ×30 duplicates).

### TitleId semantics — full vs delta state pushes

`TitleId=1` blobs are FULL state (with `configJsonList`); `TitleId=4`
blobs are **deltas** (`updateDashboards` upserts + `deletedDashboards`
removals, no `configJsonList`). A consumer that treats every blob as full
state collapses its inventory to the last delta. Plugin: `WheelDashboardState.Merge`.

### What the state blob does NOT contain — active-dashboard signal

Neither firmware schema includes a field identifying **which dashboard the wheel is currently rendering**. The state push lists installed/enabled/disabled dashboards and their metadata, but there is no `activeSlot`, `currentDashboard`, `selectedIndex`, or equivalent field in any observed capture across either schema version.

Active-dashboard state is carried instead by the channel-config burst's `28:00` / `28:01` readbacks on group `0x40`. See [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md) §"28:00/28:01 response format" for the wire form and decode. The wheel retains the loaded dashboard across power cycles, so this readback is the authoritative resume signal after plugin restart / SimHub reload.

A host that relies on the configJson state blob alone for active-dashboard tracking will desync from the wheel after any restart cycle (host re-applies its saved profile; wheel keeps rendering whatever it was rendering). The only positive in-protocol confirmation of a switch is the wheel's echo of `kind=4` FF-records on session 0x02, which only fires when the host actually sends a switch — not at startup.

### configJson state `rootDirPath` — `/home/moza/resource` (current)

Current PitHouse (2026-05+, bridge capture `sim/logs/bridge-20260514-170002.jsonl`) reports:

| Field | Value |
|-------|-------|
| `rootDirPath` | `/home/moza/resource` |
| `rootPath` (enableManager / disableManager) | `/home/moza/resource/dashes` |

Upload destination paths in the type=0x03 content sub-msg are
`/home/moza/resource/dashes/<DisplayName>/<DisplayName>.mzdash` (per-
dashboard subdirectory). PNG resources land at
`/home/moza/resource/images/MD5/<md5hex>.png`. A 2026-04 sim-side
change briefly emitted a `/home/root/resource` variant; current
PitHouse + firmware use `/home/moza/resource`, so emit that.

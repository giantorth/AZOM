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
- **Slot table is the WHEEL's own, not the host's**: the wheel maintains
  its `configJsonList` itself, **ordinal-sorted** (uppercase before
  lowercase: `Core < … < Simple… < kenobi… < radarrr`), and that list is
  the slot numbering used by kind=4 switches and wheel-side cycling. It can
  contain entries the host's enabled-only declaration omits (wire-verified
  2026-08-16: a dash sat at wheel slot 3 that the host list lacked —
  mapping against the host list shifted every later slot by one and routed
  a switch to the wrong dash). PitHouse's declared list *coincides* with
  the wheel's table only because both are ordinal-sorted over the same
  set — do not model it as adoption. Name→slot mapping must always use the
  wheel-reported `configJsonList` from the latest TitleId=1 push; the wheel
  does NOT answer mid-session prime/open-request nudges with a fresh full
  push (neither for the plugin nor for PitHouse — full pushes happen only at
  connect).
- **Two tables, two rebuild rules** (wire+wheel-log verified 2026-08-17,
  superseding the 2026-08-16 "rebuilds only on port reconnect" model):
  the **registry/`configJsonList`** rebuilds at connect (the TitleId=1 boot
  push after a bounce was clean), but the **render/UI slot table** — the one
  the wheel's own cycling and kind=4 switches use ("size N" in its kind=14
  log) — is NOT rebuilt by a port bounce (post-bounce the wheel kept its
  16-entry render table against a clean 15-entry configJsonList: slot 5
  loaded Mono, slot 2 hit the deleted dash's file-less hole = "dash load
  error"). The render table compacts **mid-session, when the wheel ACCEPTS
  a host `configJson()` list** omitting the name: PitHouse delete → wheel
  log `deleteOps` / `sendIndexChanged` / `size 16→15` + a wheel kind=4
  announcing its shifted active index, zero port events in 513 s / 2300 s
  of captures; decisively the *kenobi event*
  (`moza-wire-20260816-210056`): a plugin list on **0x09** → 0.39 s later a
  full mid-session UI rebuild (old→new `Paths:` dumps,
  `sendIndexChanged -1 → 0`, preview regen) — no verb, no reconnect, zero
  0x0A traffic. The verb alone NEVER compacts (2026-08-17 negative
  control: verb executed, deltas pushed, files removed — render table
  unchanged through the bounce). The earlier "table rebuilt at t=13 of a
  PitHouse connect" observation was the effect of PitHouse's accepted
  connect-time list, not of the reconnect.
- **Mid-session slot allocator never renumbers on its own**: between
  accepted lists, a deleted entry stays as a dead hole and a (re-)enabled
  name takes its ordinal position (a re-upload re-occupies its own hole).
  Host-side table tracking mirrors the wheel: ordinal-insert on
  wheel-confirmed enable; compact on the wheel-confirmed delete delta
  (`CompactConfirmedDeletes`), in lockstep with the render-table compaction
  the follow-up accepted list triggers. Host *intent* must never move the
  table — only the wheel's confirming delta means the operation happened.
- **Persistent ghost warning**: repeatedly declaring a name the wheel
  refuses to enable properly manufactures a file-less registry entry that
  survives reboots and power cycles ('dash load error' at its ordinal
  slot). Only declare names the wheel has confirmed or that were just
  uploaded (verdict-bounded intents).

Host-side bookkeeping this implies (all in `TelemetrySender`): build the
wire list from the wheel's current enabled `dirName`s + recent intentional
enables − recent intentional removes, ordinal-sorted
(`BuildWireLibraryList`); never overwrite the cached wheel-reported
`ConfigJsonList` with it.

### Envelope `comp_size` = zlib length + 4

The 9-byte envelope's `comp_size` field counts the zlib stream **plus 4**
(same +4 convention as the upload bundle preamble's `total_compressed`).
PitHouse: 204-byte zlib → `comp_size=208`. A bare-length value makes the
wheel slice the stream 4 bytes short and silently discard the message —
this is why plugin `configJson()` replies were ignored for months while
looking well-formed.

### RPC calls — fixed method ids, config session, acked + retransmitting

- Host→wheel management RPCs ride the **config session itself** (0x09; the
  plugin's exchange stays there) — not a separate channel. PitHouse runs
  its ENTIRE exchange on **0x0A**, which the wheel device-inits only when
  solicited with the `7c:1e` open-request (`7c1e6c80 [seq] [0a00] fe01`);
  plugin traces contain zero 0x0A frames because the plugin never sends
  `7c:1e`. 0x0A is **parity, not a prerequisite** — the kenobi event
  proves an accepted 0x09 list drives the wheel's UI table (see above).
  With PitHouse, 0x09 carries only ~4 s keepalives (empty chunks).
- The JSON `id` field is a **method discriminator**, not a correlation
  counter: `configJson()` = 11, `completelyRemove()` = 10 (both PitHouse
  deletes used 10). A call with a wrong id is silently ignored.
- `completelyRemove(id-string)` deletes a dashboard by its per-entry `id`;
  PitHouse's "remove" and "delete" menu items both emit this one verb,
  followed by a library-list re-send without the name (~1 s after the
  verb, i.e. right at/after the wheel's confirm delta; 2–3 byte-identical
  copies plus ack-driven re-sends). The wheel answers with a PAIR of
  TitleId=4 deltas (`deletedDashboards` with the bare id — enabled-side
  first, disabled-side after the list), not a textual reply. New/re-uploaded
  names get APPENDED at the tail of PitHouse's list (not ordinal position);
  the wheel keeps its own table ordinal-sorted regardless.
- **h2b data seq base = devinit open-seq + 3** (ack floor open-seq + 2 —
  the same +3 rule as the FT sessions). Seqs below the base are silently
  ignored (a cold-start list sent at seqs 1–5 is inert); any SKIPPED seq
  pins the wheel's cumulative rx ack at the hole and everything behind it
  is received but never accepted (wire-verified 2026-08-17: the plugin's
  one-seq-hole-per-send bug made every post-delete list re-send inert
  while enables — first sends after a long keepalive run — worked).
- **The wheel DOES fc:00-ack config-session chunks and DOES retransmit**
  on current firmware (2026-08 W17), both directions, wire-verified in
  both PitHouse captures AND the plugin trace: lazy/partial cumulative
  acks; host re-sends the whole burst once ~103 ms out, then the unacked
  tail on a ~1.0–1.7 s timer; the wheel retransmits its own chunks on
  duplicate acks (byte-identical) and re-sends unacked keepalives ×10.
  Host config sends must therefore be **ack-tracked with retransmit** —
  the old "never acks → fire-once" doctrine (and the ×30-duplicate
  pathology that motivated it) was tracked chunks pinned behind the
  outbound seq hole, not acking being absent.

### TitleId semantics — full vs delta state pushes

`TitleId=1` blobs are FULL state (with `configJsonList`); `TitleId=4`
blobs are **deltas** (`updateDashboards` upserts + `deletedDashboards`
removals, no `configJsonList`). A consumer that treats every blob as full
state collapses its inventory to the last delta. Plugin: `WheelDashboardState.Merge`.

### What the state blob does NOT contain — active-dashboard signal

Neither firmware schema includes a field identifying **which dashboard the wheel is currently rendering**. The state push lists installed/enabled/disabled dashboards and their metadata, but there is no `activeSlot`, `currentDashboard`, `selectedIndex`, or equivalent field in any observed capture across either schema version.

Active-dashboard state was believed to be carried by the channel-config burst's `28:00` / `28:01` readbacks on group `0x40` (see [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md)). **Caveat (2026-08-17, W17 current firmware): the readback was constant `00 12` across multiple dashboard switches in the plugin trace — it carried no per-slot signal there.** Do not build rendered-dash detection on it without re-verifying per firmware. The wheel retains the loaded dashboard across power cycles.

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

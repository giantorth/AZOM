### Tier definition implementation

> Tier-concept reference: [`../telemetry/tiers.md`](../telemetry/tiers.md)
> (`package_level` semantics, flag-byte mapping, end-to-end channel
> example). This page covers how the plugin builds and sends tier defs.

Encoding selection is driven by the wheel-era policy, not a user "Protocol version" setting. `EraPolicy` (`Telemetry/Era/EraPolicy.cs`) resolves a `TierDefEncoding` from the per-wheel-page `MozaWheelEra` (Auto resolves via `EraPolicy.GuessFromWheelModel`):

- **V2Type02**: compact numeric tier definitions via `TierDefinitionBuilder.BuildTierDefinitionMessage()`.
- **V0Url**: URL subscription via `TierDefinitionBuilder.BuildV0UrlSubscription()`. Double-sent (once at startup, once after preamble) to match PitHouse.

The tier-def is emitted on whichever host session carries the wheel's real catalog + END, resolved per cycle by `TelemetrySender.ResolveTierDefSession()`:

- The wheel's channel catalog is **parsed from whichever session delivered it** (`ChannelCatalogParser.HasRealCatalogOnSession`). Form A puts it on `0x01`, Form B on `0x02`; the wheel picks per open and the same wheel can switch forms between cycles (see [`../sessions/lifecycle.md`](../sessions/lifecycle.md) § "Session roles are assigned per open").
- The tier-def follows the catalog, and its END u32 echoes that session's own END (`GetEndMarkerForSession`). Emitting on the other session, or with the other session's END, is rejected: the wheel sends one type-00 CLOSE for the session the tier-def rode and never binds (CS-Pro 2026-06-07, 2026-06-10, bundle 5NDCADWR 2026-10-11).
- The per-session catalog/END memory is cleared in `ChannelCatalogParser.ClearBuffer` on every `StartInner`, so `StartInner`'s pre-init catalog wait always holds for the current cycle's advertisement before the FF-init goes out. Reusing the previous cycle's roles sent both to the wrong sessions after a rim hot-swap (5NDCADWR).
- `DisplayWatchdog` treats a wheel-initiated CLOSE of the session the most recent tier-def rode, within 10 s of that emission, as a reject and requests a restart.

FF-init records, dashboard-switch (kind=4), and property pushes ride the **mirror** session (`ResolveFfSession()`, the *opposite* of the tier-def session; PitHouse-derived). The flag byte for value frames is 0x00-based, not session-port-based.

(The earlier `TelemetryProtocolVersion` and `FlagByteMode` settings were removed in favour of this era policy.)

Dashboard upload controlled by `TelemetryUploadDashboard` (UI: Telemetry > Advanced > Upload dashboard, default: on). Uploads `.mzdash` file to wheel on **session 0x04** (2025-11 firmware file-transfer path, Path B below) using TLV-path + MD5 sub-msg 1/2 framing. Path A (session 0x01 FF-prefix) was the original pre-2025-11 implementation; replaced because 2025-11 firmware only actions mzdash writes via session 0x04. Mzdash content loaded from user-selected file or from embedded resource matching active profile name.

Plugin parses wheel's incoming channel catalog (session 0x02 tag 0x04 URLs) during preamble and displays detected channels in UI. Under the `V2Type02` encoding the catalog is also used to **filter tier definition** (`TierDefinitionEmitter.RefilterLiveTiers`) before sending — channels whose URL has no idx in the wheel's live catalog are dropped from their tier and the rest are sorted by catalog idx. Match is case-insensitive on the full URL.

**Catalog-only synthesis dedups by URL.** When the host has no `.mzdash` and synthesises the profile straight from the wheel's catalog (`DashboardProfileStore.BuildProfileFromCatalog`), the same dash's channels can appear at *multiple* catalog idxs — the wheel re-advertises a dashboard at a fresh idx range on some switches (rather than back-referencing the old idxs), so e.g. `Rpm` shows up at both idx 39 and idx 104. Emitting one tier-def channel per catalog entry then doubled the subscription (CS-Pro Marco: 127 channels for ~75 real ones), splitting the fast tier into three 30 ms sub-tiers and lagging the wheel's render. The synth now skips a URL it has already emitted (first occurrence wins), so each URL yields exactly one channel regardless of how many idxs carry it.

Before transmitting tier definition, plugin calls `WaitForChannelCatalogQuiet(quietMs=200, timeoutMs=2000)` so wheel's pre-tier-def channel-registration burst (session 0x02 tag 0x04 entries) finishes arriving first. Without this wait, fast connections can race tier def against wheel's own catalog push and wheel rejects tier def.

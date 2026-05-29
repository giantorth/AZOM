### Tier definition implementation

> Tier-concept reference: [`../telemetry/tiers.md`](../telemetry/tiers.md)
> (`package_level` semantics, flag-byte mapping, end-to-end channel
> example). This page covers how the plugin builds and sends tier defs.

Encoding selection is driven by the wheel-era policy, not a user "Protocol version" setting. `EraPolicy` (`Telemetry/Era/EraPolicy.cs`) resolves a `TierDefEncoding` and a `TierDefSessionPolicy` from the per-wheel-page `MozaWheelEra` (Auto resolves via `EraPolicy.GuessFromWheelModel`):

- **V2Compact / V2Type02**: compact numeric tier definitions via `TierDefinitionBuilder.BuildTierDefinitionMessage()`. The tier-def session and flag byte come from `TierDefSessionPolicy` (FlagByte vs MgmtPort).
- **V0Url**: URL subscription via `TierDefinitionBuilder.BuildV0UrlSubscription()`. Double-sent (once at startup, once after preamble) to match PitHouse.

(The earlier `TelemetryProtocolVersion` and `FlagByteMode` settings were removed in favour of this era policy.)

Dashboard upload controlled by `TelemetryUploadDashboard` (UI: Telemetry > Advanced > Upload dashboard, default: on). Uploads `.mzdash` file to wheel on **session 0x04** (2025-11 firmware file-transfer path, Path B below) using TLV-path + MD5 sub-msg 1/2 framing. Path A (session 0x01 FF-prefix) was the original pre-2025-11 implementation; replaced because 2025-11 firmware only actions mzdash writes via session 0x04. Mzdash content loaded from user-selected file or from embedded resource matching active profile name.

Plugin parses wheel's incoming channel catalog (session 0x02 tag 0x04 URLs) during preamble and displays detected channels in UI. Catalog also used to **filter tier definition** (`FilterProfileToCatalog`) before sending — channels in profile whose URL doesn't appear in wheel's advertised set are dropped, along with any tier ending up empty. Match case-insensitive on full URL, with last-path-segment fallback. Falls back to unfiltered profile if filtering removes everything.

Before transmitting tier definition, plugin calls `WaitForChannelCatalogQuiet(quietMs=200, timeoutMs=2000)` so wheel's pre-tier-def channel-registration burst (session 0x02 tag 0x04 entries) finishes arriving first. Without this wait, fast connections can race tier def against wheel's own catalog push and wheel rejects tier def.

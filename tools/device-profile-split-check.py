#!/usr/bin/env python3
"""Mirror of Settings/DeviceProfiles/DeviceProfileMigration, for checking the
per-device profile split against a real SimHub install.

Pre-split settings file (DeviceProfilesSplit false): prints which device stores
the plugin will create and, per store, every profile's identity and the
device values it will carry.

Post-split settings file: rebuilds each device store from the saved
LegacyDeviceProfileSnapshot and diffs it against what the plugin wrote —
profile identity (ProfileId / Name / GameCode), the store's game links
(LastGameProfiles / activeProfileId / ProfileSwitchingMode) and every device
field. Fields a user has edited since the split will show as differences.
Also confirms the legacy fields on the main profiles are still intact (a
downgraded plugin build reads them).

Usage:
    python3 tools/device-profile-split-check.py "<SimHub install dir>" [--verbose]
"""
import json
import os
import sys

CM2_PAGE = "c97a4d00-a66d-4e2f-a9b4-e7fc348dcc33"
CM1_PAGE = "86f772c3-0cab-4f1b-baa8-39eb178420f9"   # MozaDeviceConstants.DashCm1Guid
DASH_CM2_DEVICE = "6a2d9b0f-8b1e-4d32-9c18-1f5ec8a81025"
WHEEL_GENERIC = "ed153fcb-774d-4cea-97db-5f7096cd1099"
WHEEL_OLDPROTO = "5e70f006-ba71-4987-9e88-840d650b12ef"
EMPTY = "00000000-0000-0000-0000-000000000000"

WHEEL_EFF = [  # overlay >= 0 wins, else the profile baseline
    "WheelTelemetryMode", "WheelKnobLedMode", "WheelButtonsLedMode", "WheelRpmBrightness",
    "WheelButtonsBrightness", "WheelFlagsBrightness", "WheelESRpmBrightness",
    "WheelRpmIndicatorMode", "WheelRpmDisplayMode", "WheelKnobRingBrightness",
    "WheelKnobStaticTimeoutMs",
]
WHEEL_OVERLAY_ONLY = ["WheelPaddlesMode", "WheelClutchPoint", "WheelKnobMode", "WheelStickMode"]
WHEEL_ARR = [  # overlay non-null wins, else baseline
    "WheelRpmColors", "WheelRpmBlinkColors", "WheelButtonColors", "WheelButtonDefaultDuringTelemetry",
    "WheelFlagColors", "WheelIdleColor", "WheelESRpmColors", "WheelKnobPrimaryColors",
    "WheelKnobRingColors", "WheelKnobDefaultDuringTelemetry",
]
DASH_FIELDS = [
    "DashRpmBrightness", "DashFlagsBrightness", "DashDisplayBrightness", "DashDisplayStandbyMin",
    "DashRpmIndicatorMode", "DashRpmDisplayMode", "DashFlagsIndicatorMode",
    "DashRpmColors", "DashRpmBlinkColors", "DashFlagColors",
]
BASE_FIELDS = [
    "BaseAmbientBrightness", "BaseAmbientStandbyMode", "BaseAmbientIndicatorState",
    "BaseAmbientSleepMode", "BaseAmbientSleepTimeout", "BaseAmbientStartupColor",
    "BaseAmbientShutdownColor", "BaseAmbientIdleColorsConstant", "BaseAmbientIdleColorsBreath",
    "BaseAmbientSleepColors", "BaseAmbientStandbyIntervals", "BaseAmbientSleepBreathInterval",
]
STORE_LINKS = ["LastGameProfiles", "activeProfileId", "LastUsedProfile", "ProfileSwitchingMode"]
IDENTITY = ["ProfileId", "Name", "GameCode"]


def norm(g):
    return (g or "").lower()


def lower_keys(d):
    return {norm(k): v for k, v in (d or {}).items()}


def eff(ov, base, default):
    return ov if ov is not None and ov >= 0 else (base if base is not None else default)


def to_wheel(p, guid):
    ov = lower_keys(p.get("WheelOverridesByPageGuid")).get(guid) or {}
    out = {}
    for f in WHEEL_EFF:
        default = 0 if f == "WheelKnobStaticTimeoutMs" else -1
        out[f] = eff(ov.get(f, -1), p.get(f), default)
    for f in WHEEL_OVERLAY_ONLY:
        out[f] = ov.get(f, -1)
    out["WheelKnobSignalModes"] = ov.get("WheelKnobSignalModes")
    for f in WHEEL_ARR:
        v = ov.get(f)
        out[f] = v if v is not None else p.get(f)
    for f in ("DashDisplayBrightness", "DashDisplayStandbyMin", "DashDisplayRotation"):
        out[f] = p.get(f, -1)
    out["TelemetryProfileName"] = ov.get("TelemetryProfileName")
    out["TelemetryMzdashPath"] = ov.get("TelemetryMzdashPath")
    out["TelemetryDashboardKey"] = p.get("TelemetryDashboardKey")
    out["ChannelMappings"] = lower_keys(p.get("TelemetryChannelMappings")).get(guid) or {}
    out["Fsr1Mappings"] = lower_keys(p.get("Fsr1DashboardMappings")).get(guid) or {}
    return out


def to_dash(p, guid):
    out = {f: p.get(f) for f in DASH_FIELDS}
    if guid == CM2_PAGE:
        out["TelemetryDashboardKey"] = p.get("TelemetryDashboardKey")
        out["ChannelMappings"] = lower_keys(p.get("TelemetryChannelMappings")).get(guid) or {}
    out["Cm1Mappings"] = lower_keys(p.get("Cm1FieldMappings")).get(guid) or {}
    return out


def to_base(p, _guid):
    return {f: p.get(f) for f in BASE_FIELDS}


def load_registry(simhub):
    path = os.path.join(simhub, "DevicesDefinitions", "User", "moza-wheel-guids.json")
    try:
        with open(path, encoding="utf-8-sig") as fh:
            prefix_to_guid = json.load(fh)
    except OSError:
        prefix_to_guid = {}
    return {norm(g): prefix for prefix, g in prefix_to_guid.items()}


def roster(simhub, guid_to_prefix):
    wheels, bases, dashes = set(), set(), set()
    root = os.path.join(simhub, "PluginsData", "Common", "Devices")
    if not os.path.isdir(root):
        return wheels, bases, dashes
    for folder in os.listdir(root):
        if folder.startswith("_"):
            continue
        path = os.path.join(root, folder, "settings.json")
        if not os.path.isfile(path):
            continue
        try:
            with open(path, encoding="utf-8-sig") as fh:
                type_id = norm(json.load(fh).get("DeviceTypeID"))
        except (OSError, ValueError):
            continue
        guid = type_id.split("_", 1)[0]
        prefix = guid_to_prefix.get(guid)
        if guid == DASH_CM2_DEVICE:
            dashes.add(CM2_PAGE)
        elif guid == CM1_PAGE:
            dashes.add(CM1_PAGE)
        elif prefix and prefix.startswith("base:"):
            bases.add(guid)
        elif prefix and guid not in (WHEEL_GENERIC, WHEEL_OLDPROTO):
            wheels.add(guid)
    return wheels, bases, dashes


def discover(settings, profiles, simhub, guid_to_prefix):
    wheels, bases, dashes = roster(simhub, guid_to_prefix)
    skip = {EMPTY, CM2_PAGE, CM1_PAGE, WHEEL_GENERIC, WHEEL_OLDPROTO}

    def add_wheels(d):
        for g in lower_keys(d):
            if g not in skip:
                wheels.add(g)

    for p in profiles:
        add_wheels(p.get("WheelOverridesByPageGuid"))
        add_wheels(p.get("Fsr1DashboardMappings"))
        for g in lower_keys(p.get("TelemetryChannelMappings")):
            if g == CM2_PAGE:
                dashes.add(CM2_PAGE)
            elif g not in skip:
                wheels.add(g)
        if CM1_PAGE in lower_keys(p.get("Cm1FieldMappings")):
            dashes.add(CM1_PAGE)
    for key in ("WheelSleepByPageGuid", "WheelIdleByPageGuid", "WheelMzdashFolderByPageGuid",
                "Fsr1ActiveDashboardByWheelGuid"):
        add_wheels(settings.get(key))
    for g in lower_keys(settings.get("WheelTelemetryEnabledByPageGuid")):
        if g == CM2_PAGE:
            dashes.add(CM2_PAGE)
        elif g not in skip:
            wheels.add(g)
    return wheels, bases, dashes


def label(guid, guid_to_prefix):
    if guid == CM2_PAGE:
        return "CM2"
    if guid == CM1_PAGE:
        return "CM1"
    return guid_to_prefix.get(guid, guid[:8])


def same(a, b):
    if isinstance(a, dict) and isinstance(b, dict):
        return lower_keys(a) == lower_keys(b)
    if a in (None, {}, []) and b in (None, {}, []):
        return True
    return a == b


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    simhub = sys.argv[1]
    verbose = "--verbose" in sys.argv
    path = os.path.join(simhub, "PluginsData", "Common", "MozaPlugin.MozaPluginSettings.json")
    with open(path, encoding="utf-8-sig") as fh:
        settings = json.load(fh)
    guid_to_prefix = load_registry(simhub)
    kinds = (("Wheels", to_wheel), ("Bases", to_base), ("Dashes", to_dash))

    if not settings.get("DeviceProfilesSplit"):
        main_store = settings.get("ProfileStore") or {}
        profiles = main_store.get("Profiles") or []
        wheels, bases, dashes = discover(settings, profiles, simhub, guid_to_prefix)
        print(f"Pre-split: {len(profiles)} main profile(s) → "
              f"{len(wheels)} wheel, {len(bases)} wheelbase, {len(dashes)} dash store(s)")
        print("  game links: " + ", ".join(f"{k}={json.dumps(main_store.get(k))[:80]}" for k in STORE_LINKS))
        for name, guids, conv in (("wheel", wheels, to_wheel), ("base", bases, to_base), ("dash", dashes, to_dash)):
            for g in sorted(guids):
                print(f"\n[{name} {label(g, guid_to_prefix)}  {g}]")
                for p in profiles:
                    vals = conv(p, g)
                    set_vals = {k: v for k, v in vals.items() if v not in (None, -1, {}, [])}
                    line = f"  {p.get('Name')!r:32} {p.get('ProfileId')}  {len(set_vals)} field(s) set"
                    print(line + (f"  {json.dumps(set_vals)[:400]}" if verbose else ""))
        return 0

    snapshot = settings.get("LegacyDeviceProfileSnapshot")
    registry = settings.get("DeviceProfiles") or {}
    if not snapshot:
        print("Split done with no snapshot (new install or no legacy profiles) — nothing to verify.")
        return 0
    snap_profiles = snapshot.get("Profiles") or []
    problems = 0

    # Legacy fields on the main profiles must be untouched (downgrade safety).
    main_by_id = {norm(p.get("ProfileId")): p for p in (settings.get("ProfileStore") or {}).get("Profiles") or []}
    for sp in snap_profiles:
        mp = main_by_id.get(norm(sp.get("ProfileId")))
        if mp is None:
            print(f"note: main profile {sp.get('Name')!r} deleted since the split (snapshot still seeds it)")
            continue
        for k, v in sp.items():
            if k in IDENTITY or k in ("CarChoice", "CarChoices", "LastLoaded"):
                continue
            if not same(mp.get(k), v):
                problems += 1
                print(f"LEGACY CHANGED: main {sp.get('Name')!r}.{k}")

    for key, conv in kinds:
        for g, store in (registry.get(key) or {}).items():
            g = norm(g)
            tag = f"{key[:-1].lower()} {label(g, guid_to_prefix)}"
            for link in STORE_LINKS:
                if not same(store.get(link), snapshot.get(link)):
                    print(f"[{tag}] store {link} differs from the main store's at split time "
                          f"(expected after a user switch or game load)")
            got = {norm(p.get("ProfileId")): p for p in store.get("Profiles") or []}
            for sp in snap_profiles:
                pid = norm(sp.get("ProfileId"))
                dp = got.get(pid)
                if dp is None:
                    problems += 1
                    print(f"[{tag}] MISSING profile {sp.get('Name')!r} ({pid})")
                    continue
                for k in IDENTITY:
                    if not same(dp.get(k), sp.get(k)):
                        problems += 1
                        print(f"[{tag}] {sp.get('Name')!r} identity {k}: {dp.get(k)!r} != {sp.get(k)!r}")
                for k, want in conv(sp, g).items():
                    have = dp.get(k)
                    if not same(have, want):
                        print(f"[{tag}] {sp.get('Name')!r}.{k}: store={json.dumps(have)[:120]} "
                              f"split={json.dumps(want)[:120]}")
            extra = set(got) - {norm(p.get("ProfileId")) for p in snap_profiles}
            if extra:
                print(f"[{tag}] {len(extra)} profile(s) added since the split")
            print(f"[{tag}] {len(got)} profile(s), current={store.get('activeProfileId')}")

    print(f"\n{problems} hard problem(s) (missing profiles / identity / legacy changes). "
          "Field differences above are only problems if no one has edited that device since the split.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

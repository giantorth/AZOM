using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Default SimHub property mappings for the 23 string-typed channels in
    /// <c>Data/Telemetry.json</c> (<c>compression: "string"</c>). Applied at
    /// profile build time only when the channel's <c>SimHubProperty</c> is
    /// empty — user overrides via the channel-mappings UI run later via
    /// <see cref="DashboardProfileStore.ApplyUserMappings"/> and trump these
    /// defaults.
    ///
    /// URLs key off the canonical <c>v1/gameData/...</c> string exactly as
    /// the wheel announces it on sess=0x01 type=0x04. Note that several
    /// short-name channels (<c>MapName</c>, <c>DisplayMapName</c>) live under
    /// <c>patch/</c> URLs.
    ///
    /// Property paths were verified against
    /// <c>libs/SimHub/GameReaderCommon.dll</c> exports — all right-hand
    /// values resolve to real properties on <c>StatusDataBase</c>. Mappings
    /// left empty (omitted) are channels with no unambiguous SimHub
    /// equivalent; the user can map those in the UI per-game.
    /// </summary>
    internal static class StringChannelDefaults
    {
        internal static readonly Dictionary<string, string> ByUrl =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Track identity. TrackId → TrackName (display) per user decision
                // 2026-05-15; PitHouse uses short codes (TrackCode) but display
                // strings are friendlier and StringValueBuilder caps at 127 bytes.
                ["v1/gameData/TrackId"]                    = "DataCorePlugin.GameData.TrackName",
                ["v1/gameData/TrackCode"]                  = "DataCorePlugin.GameData.TrackCode",
                ["v1/gameData/TrackConfig"]                = "DataCorePlugin.GameData.TrackConfig",
                ["v1/gameData/patch/TrackName"]            = "DataCorePlugin.GameData.TrackName",
                ["v1/gameData/patch/DisplayTrackName"]     = "DataCorePlugin.GameData.TrackName",

                // Car identity.
                ["v1/gameData/CarId"]                      = "DataCorePlugin.GameData.CarId",
                ["v1/gameData/CarModel"]                   = "DataCorePlugin.GameData.CarModel",
                ["v1/gameData/CarClass"]                   = "DataCorePlugin.GameData.CarClass",

                // Session / player.
                ["v1/gameData/SessionTypeName"]            = "DataCorePlugin.GameData.SessionTypeName",
                ["v1/gameData/PlayerName"]                 = "DataCorePlugin.GameData.PlayerName",
                // Computed: SimHub game code → MOZA short name (see GameNameMap).
                ["v1/gameData/patch/GameName"]             = "@internal/GameName",
                ["v1/gameData/Flag_Name"]                  = "DataCorePlugin.GameData.Flag_Name",
                ["v1/gameData/ReplayMode"]                 = "DataCorePlugin.GameData.ReplayMode",
                ["v1/gameData/PacketTime"]                 = "DataCorePlugin.GameData.PacketTime",

                // Tyres. SimHub exposes Front/Rear pairs (no per-corner split),
                // so both left/right corners share one SimHub source. User can
                // override per-game if their telemetry provides per-corner detail.
                ["v1/gameData/TyreCompoundTypeFrontLeft"]  = "DataCorePlugin.GameData.FrontTyreCompound",
                ["v1/gameData/TyreCompoundTypeFrontRight"] = "DataCorePlugin.GameData.FrontTyreCompound",
                ["v1/gameData/TyreCompoundTypeRearLeft"]   = "DataCorePlugin.GameData.RearTyreCompound",
                ["v1/gameData/TyreCompoundTypeRearRight"]  = "DataCorePlugin.GameData.RearTyreCompound",

                // VehicleFilename / VehicleTag{1,2,3} / DestinationCity:
                // no unambiguous SimHub equivalent across games. Left empty;
                // user maps these via the channel-mappings UI per their
                // specific sim's exposed properties.
            };

        /// <summary>
        /// Apply default mapping to a channel if it has no SimHubProperty set.
        /// No-op when the URL has no default or the channel already has a
        /// mapping. Returns true if a default was applied.
        /// </summary>
        internal static bool ApplyIfEmpty(ChannelDefinition ch)
        {
            if (ch == null || !string.IsNullOrEmpty(ch.SimHubProperty)) return false;
            if (string.IsNullOrEmpty(ch.Url)) return false;
            if (ByUrl.TryGetValue(ch.Url, out var path))
            {
                ch.SimHubProperty = path;
                return true;
            }
            return false;
        }
    }
}

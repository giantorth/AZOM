using System;
using System.Collections.Generic;
using System.Reflection;
using GameReaderCommon;
using MozaPlugin.Diagnostics;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// A flat snapshot of the game data fields used by the telemetry sender.
    /// Decouples TelemetryFrameBuilder from the SimHub StatusDataBase API,
    /// enabling test patterns and unit testing without live game data.
    /// </summary>
    public struct GameDataSnapshot
    {
        public double SpeedKmh;
        public double Rpms;
        public double Gear;         // 0=R/N, 1=1st, 2=2nd, …
        public double Throttle;     // 0.0–1.0 (converted from SimHub's 0–100)
        public double Brake;        // 0.0–1.0 (converted from SimHub's 0–100)
        public double BestLapTimeSeconds;
        public double CurrentLapTimeSeconds;
        public double LastLapTimeSeconds;
        public double DeltaToSessionBest;
        public double FuelPercent;  // 0–100
        public double DrsEnabled;   // 0 or 1
        public double ErsPercent;   // 0–100
        public double TyreWearFrontLeft;
        public double TyreWearFrontRight;
        public double TyreWearRearLeft;
        public double TyreWearRearRight;
        public double CurrentLap;       // lap counter (1+)

        // Track-map (patch/Location*) per-car WORLD position (X, Y=elevation, Z).
        // Each entry feeds one 64-bit location_t slot, packed by
        // TelemetryFrameBuilder as PitHouse's fixed-point layout
        // [u16 Y | u24 Z | u24 X] — reverse-engineered + verified to <0.3 m
        // against AC ground truth on Imola+Spa (see
        // docs/protocol/telemetry/track-map.md). NOT two float32 (the prior
        // guess). CarLocations is indexed by SimHub Opponents[] order;
        // PlayerLocation is the local car. (0,0,0) marks an empty slot.
        // Source: Opponent.Coordinates / StatusDataBase.CarCoordinates
        // (double[X,Y,Z]); index 0=X, 1=Y elevation, 2=Z.
        public (float X, float Y, float Z)[]? CarLocations;
        public (float X, float Y, float Z) PlayerLocation;
        // Index of the local car within CarLocations (= the IsPlayer opponent),
        // so the wheel can highlight "you" on the track map. 0 when unknown.
        public int PlayerIndex;

        // Slot-indexed map ri-slot -> CarLocations index (carId) for the radar, or
        // -1 for an empty slot. Index 0 is unused (ri0 is the magic header). PitHouse
        // gives each car a STABLE slot it keeps while relevant and emits ONLY the
        // in-range cars (~24 m 2-D); a car entering/leaving range never reshuffles the
        // others (re-packing every frame made the radar go wild the instant cars
        // moved and crossed the range boundary). Built by AssignStableRadarSlots.
        // Null when car positions weren't requested.
        public int[]? RadarSlotCarIds;

        // Track folder name (AC: content/tracks/<name>, e.g. "ks_zandvoort"),
        // used as the per-track cache key for the map bounds / transform.
        public string? TrackFolderName;

        // Bounding box of SimHub's recorded track map (PersistantTracker), in the
        // same space the car locations resolve to. Feeds TrackMapTransform.FromBounds
        // for tracks without an AC map.ini. Invalid until SimHub has a map.
        public bool MapBoundsValid;
        public double MapMinX, MapMaxX, MapMinY, MapMaxY, MapMinZ, MapMaxZ;
        // The recorded map is SimHub's lap-relative integration (iRacing), not world space.
        public bool MapLapRelative;
        // Where the map bounds came from ("simhub-map" or "outline-line"), for the transform log.
        public string? MapBoundsSource;

        // The wheel's outline for this track, when MOZA has one (TrackOutlines).
        public TrackOutline? Outline;

        /// <summary>Populate from a live StatusDataBase instance. When
        /// <paramref name="includeCarPositions"/> is false the per-car track-map /
        /// radar arrays are NOT built — skips the reflection chain and the
        /// per-opponent allocation/loop in <see cref="PopulateCarLocations"/>.
        /// Callers pass false when no active channel consumes those arrays
        /// (radar/track-map channels disabled), which is the shipped default.</summary>
        public static GameDataSnapshot FromStatusData(StatusDataBase? data, bool includeCarPositions = true)
        {
            if (data == null) return default;
            var snap = new GameDataSnapshot
            {
                SpeedKmh               = data.SpeedKmh,
                Rpms                   = data.Rpms,
                Gear                   = ParseGear(data.Gear),
                Throttle               = data.Throttle / 100.0,  // SimHub 0–100 → float_001 expects 0.0–1.0
                Brake                  = data.Brake    / 100.0,  // SimHub 0–100 → float_001 expects 0.0–1.0
                BestLapTimeSeconds     = data.BestLapTime.TotalSeconds,
                CurrentLapTimeSeconds  = data.CurrentLapTime.TotalSeconds,
                LastLapTimeSeconds     = data.LastLapTime.TotalSeconds,
                DeltaToSessionBest     = data.DeltaToSessionBest ?? 0.0,
                FuelPercent            = data.FuelPercent,
                DrsEnabled             = data.DRSEnabled != 0 ? 1.0 : 0.0,
                ErsPercent             = data.ERSPercent,
                TyreWearFrontLeft      = data.TyreWearFrontLeft,
                TyreWearFrontRight     = data.TyreWearFrontRight,
                TyreWearRearLeft       = data.TyreWearRearLeft,
                TyreWearRearRight      = data.TyreWearRearRight,
                CurrentLap             = data.CurrentLap,
            };
            if (includeCarPositions)
                PopulateCarPositionsShared(data, ref snap);
            return snap;
        }

        // Car-position state (track folder, map bounds, car locations, radar
        // slots) is profile-independent — identical for every sender that binds
        // radar/track-map channels. A dual-display setup (wheel screen + CM2
        // dash) runs TWO sender tick threads through here, and the slot maps /
        // bounds cache / reflection caches / scratch buffers below are process
        // statics — unsynchronized, they'd race. Compute once per game frame
        // (SimHub allocates a fresh StatusDataBase per DataUpdate — the
        // NewData/OldData swap — so the reference is the frame key) under a
        // leaf lock; the other sender's tick copies the cached fields and
        // shares the arrays (consumers only read them). Timer threads only —
        // never the serial read thread.
        private static readonly object s_carPosLock = new object();
        private static StatusDataBase? s_carPosSource;
        private static GameDataSnapshot s_carPosCache;   // car-position fields only

        private static void PopulateCarPositionsShared(StatusDataBase data, ref GameDataSnapshot snap)
        {
            lock (s_carPosLock)
            {
                if (!ReferenceEquals(data, s_carPosSource))
                {
                    var fresh = default(GameDataSnapshot);
                    fresh.TrackFolderName = ResolveTrackFolder(data);
                    fresh.Outline = ResolveOutline(data);
                    PopulateCarLocations(data, ref fresh);
                    s_carPosCache = fresh;
                    s_carPosSource = data;
                }
                snap.TrackFolderName = s_carPosCache.TrackFolderName;
                snap.MapBoundsValid  = s_carPosCache.MapBoundsValid;
                snap.MapMinX = s_carPosCache.MapMinX; snap.MapMaxX = s_carPosCache.MapMaxX;
                snap.MapMinY = s_carPosCache.MapMinY; snap.MapMaxY = s_carPosCache.MapMaxY;
                snap.MapMinZ = s_carPosCache.MapMinZ; snap.MapMaxZ = s_carPosCache.MapMaxZ;
                snap.MapLapRelative  = s_carPosCache.MapLapRelative;
                snap.MapBoundsSource = s_carPosCache.MapBoundsSource;
                snap.Outline         = s_carPosCache.Outline;
                snap.PlayerLocation  = s_carPosCache.PlayerLocation;
                snap.CarLocations    = s_carPosCache.CarLocations;
                snap.PlayerIndex     = s_carPosCache.PlayerIndex;
                snap.RadarSlotCarIds = s_carPosCache.RadarSlotCarIds;
            }
        }

        // The track's content-folder name. AC's raw Static.track is the
        // authoritative source (the content/tracks/<folder> id); SimHub's
        // TrackId is the fallback. Reflection-only (no compile-time game ref).
        // Member lookups are cached per runtime type — this runs every tick on
        // the radar path, and uncached GetProperty/GetField per call was
        // measurable churn. Statics guarded by s_carPosLock, like the slot
        // maps below.
        private static Type? s_rawType;
        private static PropertyInfo? s_rawStaticProp;
        private static Type? s_staticType;
        private static FieldInfo? s_staticTrackField;
        private static PropertyInfo? s_staticTrackProp;

        private static string? ResolveTrackFolder(StatusDataBase data)
        {
            try
            {
                object? raw = data.GetRawDataObject();
                if (raw != null)
                {
                    var rt = raw.GetType();
                    if (rt != s_rawType)
                    {
                        s_rawType = rt;
                        s_rawStaticProp = rt.GetProperty("Static");
                    }
                    object? st = s_rawStaticProp?.GetValue(raw);
                    if (st != null)
                    {
                        var stt = st.GetType();
                        if (stt != s_staticType)
                        {
                            s_staticType = stt;
                            s_staticTrackField = stt.GetField("track");
                            s_staticTrackProp = stt.GetProperty("Track");
                        }
                        var t = (s_staticTrackField?.GetValue(st) as string)
                              ?? (s_staticTrackProp?.GetValue(st) as string);
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                }
            }
            catch { /* not AC, or shape changed — fall back to SimHub's id */ }
            try { return data.TrackId; } catch { return null; }
        }

        private static TrackOutline? ResolveOutline(StatusDataBase data)
        {
            try
            {
                return TrackOutlines.Find(SimHub.Plugins.PluginManager.GetInstance()?.GameName,
                    data.TrackId, data.TrackConfig, data.TrackName);
            }
            catch { return null; }
        }

        // World position (X, Y=elevation, Z) for the local car and every
        // opponent, for the track-map location_t channels. Defensive against
        // null/short arrays (games that don't expose coordinates leave them null).
        private static void PopulateCarLocations(StatusDataBase data, ref GameDataSnapshot snap)
        {
            // SimHub's recorded per-track map (PersistantTrackerPlugin) converts
            // lap-relative coordinates to world space for games that report them
            // (iRacing); a passthrough for world-coord games (AC/AMS2) and null
            // until a lap has been recorded. Read reflectively to avoid a hard
            // SimHub.Plugins compile-time dependency.
            var map = TryGetMapRecord();

            // iRacing reports every car as [LapDistPct, 0, 0]. The wheel's own outline
            // line places it exactly on the drawn track; else SimHub's recorded map
            // resolves it. Without either there is nothing to plot.
            // Only normalised (iRacing) outlines carry a line.
            var line = snap.Outline != null && snap.Outline.HasLine ? snap.Outline : null;
            bool lapRelative = line != null || data.RelativeCarCoordinates != null;
            bool lineMetres = false;
            double lineScale = line != null ? LineMetresPerUnit(data, line, out lineMetres) : 0.0;
            if (line == null && map == null && lapRelative)
                return;

            if (line != null)
            {
                snap.MapBoundsValid = true;
                // Same frame as SimHub's lap-relative map (Z = −outline y, see ResolveXyz).
                snap.MapMinX = lineScale; snap.MapMaxX = (1.0 + line.XSpan) * lineScale;
                snap.MapMinZ = -(1.0 + line.YSpan) * lineScale; snap.MapMaxZ = -lineScale;
                snap.MapLapRelative = true;
                snap.MapBoundsSource = "outline-line";
            }
            else
            {
                var bounds = GetMapBounds(map);
                if (bounds.HasValue)
                {
                    var b = bounds.Value;
                    snap.MapBoundsValid = true;
                    snap.MapMinX = b.minX; snap.MapMaxX = b.maxX;
                    snap.MapMinY = b.minY; snap.MapMaxY = b.maxY;
                    snap.MapMinZ = b.minZ; snap.MapMaxZ = b.maxZ;
                    snap.MapLapRelative = map!.HasRelativeCarCoordinates;
                    snap.MapBoundsSource = "simhub-map";
                }
            }

            var opps = data.Opponents;

            // iRacing's player-car telemetry (LapDistPct → CarCoordinates) is 0 while
            // spectating, e.g. watching a team-mate from the pit wall; the player's
            // own entry (CarIdxLapDistPct[PlayerCarIdx]) is valid driving or not.
            var pc = data.CarCoordinates;
            if (lapRelative && opps != null)
                foreach (var o in opps)
                    if (o != null && o.IsPlayer && o.Coordinates != null && o.Coordinates.Length >= 3)
                    { pc = o.Coordinates; break; }
            float px = 0f, py = 0f, pz = 0f; bool havePlayer = false;
            if (pc != null && pc.Length >= 3 && (!lapRelative || pc[0] != 0.0 || pc[2] != 0.0))
            {
                (px, py, pz) = ResolveXyz(map, line, lineScale, pc);
                havePlayer = px != 0f || pz != 0f || line == null;
                if (havePlayer) snap.PlayerLocation = (px, py, pz);
            }

            if (opps == null || opps.Count == 0) return;
            int count = opps.Count;

            // Resolve each car's ABSOLUTE ground-plane position (X, Z) — this is
            // what the track-map patch/Location* channels emit; the wheel's Map
            // widget plots each car's path around the track from absolute world
            // positions (see TelemetryFrameBuilder.WriteLocationPair). Source
            // priority matches what each game exposes:
            //   1. AC raw struct (DataContainer.Graphics.CarCoordinates, a flat
            //      [x,y,z,…] per slot) — world coords, the only reliable source
            //      for AC where Opponent.Coordinates is ~98.5% empty. Read
            //      reflectively (no compile-time AC reference).
            //   2. SimHub's generic Opponent.Coordinates, normalised through the
            //      recorded track map (ToGroundPlane) so lap-relative-coordinate
            //      games (iRacing) map to world space; a passthrough otherwise.
            //      Mirrors SimHub's own RadarItem.UpdateData.
            float[]? raw = TryReadRawCarCoordinates(data);

            // The radar (patch/ri*) and track-map (patch/Location*) channels need
            // each car to keep a STABLE slot frame-to-frame (a car that jumps slots
            // streaks across the wheel's radar and corrupts its per-slot heading
            // history). SimHub's Opponents list is sorted by RACE POSITION, so the
            // list index is NOT stable. Two stable indexings, by data source:
            //
            //  • Live AC exposes the raw Graphics.CarCoordinates array, which is
            //    indexed by AC carId (slot i = the car with carId i) — so locs[i]
            //    is carId-indexed and slot i == carId i, matching PitHouse exactly.
            //  • iRacing and SimHub replays expose opponents only as a
            //    position-sorted list with a stable driver Id (no carId). We map
            //    Id -> a dense slot (player -> 0, see AssignOpponentSlots) that
            //    stays put while the driver is present.
            bool rawCarIdIndexed = raw != null && raw.Length >= count * 3;

            (float X, float Y, float Z)[] locs;
            int playerIdx = 0;

            if (rawCarIdIndexed)
            {
                locs = new (float X, float Y, float Z)[count];
                double bestD = double.MaxValue;
                bool playerByDist = false;
                for (int i = 0; i < count; i++)             // i == carId
                {
                    (float X, float Y, float Z) abs = (raw![i * 3] != 0f || raw[i * 3 + 2] != 0f)
                        ? (raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2])
                        : (0f, 0f, 0f);
                    locs[i] = abs;
                    if (havePlayer && (abs.X != 0f || abs.Z != 0f))
                    {
                        double d = (abs.X - px) * (abs.X - px) + (abs.Z - pz) * (abs.Z - pz);
                        if (d < bestD) { bestD = d; playerIdx = i; playerByDist = true; }
                    }
                    var opp = opps[i];
                    if (!playerByDist && opp != null && opp.IsPlayer) playerIdx = i;
                }
            }
            else
            {
                int[] slotOf = AssignOpponentSlots(opps, snap.TrackFolderName);
                int maxSlot = 0;
                for (int i = 0; i < count; i++)
                    if (slotOf[i] > maxSlot) maxSlot = slotOf[i];
                locs = new (float X, float Y, float Z)[maxSlot + 1];
                for (int i = 0; i < count; i++)
                {
                    int s = slotOf[i];
                    if (s < 0) continue;
                    var opp = opps[i];
                    var c = opp?.Coordinates;
                    (float X, float Y, float Z) abs = (c != null && c.Length >= 3 && (c[0] != 0.0 || c[2] != 0.0))
                        ? ResolveXyz(map, line, lineScale, c)
                        : (0f, 0f, 0f);
                    locs[s] = abs;
                    if (opp?.IsPlayer ?? false) playerIdx = s;
                }
            }

            snap.CarLocations = locs;
            snap.PlayerIndex = playerIdx;

            // The radar works in metres; an outline line without a track length has none.
            snap.RadarSlotCarIds = havePlayer && (line == null || lineMetres)
                ? AssignStableRadarSlots(locs, playerIdx, px, pz, snap.TrackFolderName)
                : null;
        }

        // Radar selection range (PitHouse shows opponents within ~24 m 2-D). 24 m
        // also bounds |relZ| < 24 m so the wrapping ri field stays in its principal
        // window — no un-wrap dependency for the shown set.
        private const float RadarSelectRange2DSq = 24f * 24f;
        private const int RadarMaxSlots = Dashboard.DashboardProfileStore.MaxRadarSlotIndex;   // ri1..ri47
        // Keep a car's slot for this long after it drops out of range, so brief
        // range-boundary flicker doesn't free+reassign (and reshuffle) slots.
        private const int RadarSlotHoldMs = 2000;
        private static readonly System.Collections.Generic.Dictionary<int, int> _radarSlotOf
            = new System.Collections.Generic.Dictionary<int, int>();        // carId -> slot
        private static readonly System.Collections.Generic.Dictionary<int, int> _radarLastInRangeMs
            = new System.Collections.Generic.Dictionary<int, int>();        // carId -> last in-range tick
        private static string? _radarSlotTrack;
        // Per-frame scratch, reused (guarded by s_carPosLock like the maps above).
        private static readonly bool[] s_slotUsedScratch = new bool[RadarMaxSlots + 1];
        private static readonly System.Collections.Generic.HashSet<int> s_inRangeScratch
            = new System.Collections.Generic.HashSet<int>();

        // Assign each in-range opponent a STABLE ri slot (matches PitHouse). A car
        // keeps its slot while it stays relevant, so one car entering/leaving radar
        // range never moves the others — the fix for the radar going wild the moment
        // cars started moving (re-packing every frame shuffled every slot). First car
        // into range takes the lowest free slot and holds it; the slot frees only
        // after RadarSlotHoldMs out of range. Returns slot->carId (-1 = empty); a
        // held-but-currently-out-of-range car emits 0 (its slot stays reserved).
        private static int[] AssignStableRadarSlots(
            (float X, float Y, float Z)[] locs, int playerIdx, float px, float pz, string? track)
        {
            if (track != _radarSlotTrack)
            {
                _radarSlotOf.Clear(); _radarLastInRangeMs.Clear(); _radarSlotTrack = track;
            }
            int now = Environment.TickCount;
            var slotUsed = s_slotUsedScratch;
            Array.Clear(slotUsed, 0, slotUsed.Length);
            foreach (var s in _radarSlotOf.Values) if (s >= 1 && s <= RadarMaxSlots) slotUsed[s] = true;

            var inRange = s_inRangeScratch;
            inRange.Clear();
            for (int idx = 0; idx < locs.Length; idx++)
            {
                if (idx == playerIdx) continue;
                var c = locs[idx];
                if ((c.X == 0f && c.Z == 0f) || float.IsNaN(c.X) || float.IsNaN(c.Z)) continue;
                float rx = c.X - px, rz = c.Z - pz;
                if (rx * rx + rz * rz > RadarSelectRange2DSq) continue;
                inRange.Add(idx);
                _radarLastInRangeMs[idx] = now;
                if (!_radarSlotOf.ContainsKey(idx))
                    for (int s = 1; s <= RadarMaxSlots; s++)
                        if (!slotUsed[s]) { _radarSlotOf[idx] = s; slotUsed[s] = true; break; }
            }

            System.Collections.Generic.List<int>? toFree = null;
            foreach (var kv in _radarSlotOf)
                if (!inRange.Contains(kv.Key)
                    && (!_radarLastInRangeMs.TryGetValue(kv.Key, out int t)
                        || unchecked(now - t) > RadarSlotHoldMs))
                    (toFree ??= new System.Collections.Generic.List<int>()).Add(kv.Key);
            if (toFree != null)
                foreach (var cid in toFree) { _radarSlotOf.Remove(cid); _radarLastInRangeMs.Remove(cid); }

            var arr = new int[RadarMaxSlots + 1];
            for (int i = 0; i < arr.Length; i++) arr[i] = -1;
            foreach (var kv in _radarSlotOf)
                if (inRange.Contains(kv.Key) && kv.Value >= 1 && kv.Value <= RadarMaxSlots)
                    arr[kv.Value] = kv.Key;   // emit only in-range cars; held-out slots stay empty (0)
            return arr;
        }

        // Per-car radar/track-map slot for SimHub-list games (iRacing). SimHub sorts
        // Opponents by race position, so the list index churns; each driver Id keeps
        // its slot while present (player -> 0, the ri0 magic-header slot). Slots stay
        // DENSE in 1..present-opponents: the wheel draws only Location_0..OpponentCount,
        // so first-seen numbering that never reused a departed driver's slot pushed new
        // cars past the mask (bundle XMVWAYC1: OpponentCount 22, cars in slots 33+).
        // A new car takes the lowest slot no present car holds; a departed driver
        // gets its slot back if it returns within the hold and nobody took it.
        // Guarded by s_carPosLock.
        private const int OppSlotHoldMs = 5000;
        private static readonly Dictionary<string, int> _oppSlotById = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _oppLastSeenMs = new Dictionary<string, int>();
        private static readonly HashSet<int> s_oppSlotUsed = new HashSet<int>();
        private static string? _oppSlotTrack;

        private static int[] AssignOpponentSlots(List<Opponent> opps, string? track)
        {
            if (track != _oppSlotTrack)
            {
                _oppSlotById.Clear(); _oppLastSeenMs.Clear(); _oppSlotTrack = track;
            }
            int now = Environment.TickCount;
            int count = opps.Count;
            var slotOf = new int[count];
            int present = 0;
            for (int i = 0; i < count; i++)
            {
                var o = opps[i];
                if (o != null && !o.IsPlayer && !string.IsNullOrEmpty(o.Id)) present++;
            }

            // Present drivers keep a slot that is still inside 1..present.
            var used = s_oppSlotUsed;
            used.Clear();
            for (int i = 0; i < count; i++)
            {
                var o = opps[i];
                if (o == null) { slotOf[i] = -1; continue; }
                if (o.IsPlayer) { slotOf[i] = 0; continue; }
                if (string.IsNullOrEmpty(o.Id)) { slotOf[i] = -1; continue; }
                _oppLastSeenMs[o.Id] = now;
                slotOf[i] = _oppSlotById.TryGetValue(o.Id, out int s) && s >= 1 && s <= present && used.Add(s)
                    ? s : int.MinValue;   // needs a slot
            }

            // Everyone else takes the lowest free slot, evicting a departed holder.
            int next = 1;
            for (int i = 0; i < count; i++)
            {
                if (slotOf[i] != int.MinValue) continue;
                while (used.Contains(next)) next++;
                string? holder = null;
                foreach (var kv in _oppSlotById)
                    if (kv.Value == next) { holder = kv.Key; break; }
                if (holder != null) _oppSlotById.Remove(holder);
                _oppSlotById[opps[i].Id] = next;
                used.Add(next);
                slotOf[i] = next;
            }

            // Forget drivers gone longer than the hold.
            List<string>? stale = null;
            foreach (var kv in _oppLastSeenMs)
                if (unchecked(now - kv.Value) > OppSlotHoldMs) (stale ??= new List<string>()).Add(kv.Key);
            if (stale != null)
                foreach (var id in stale) { _oppLastSeenMs.Remove(id); _oppSlotById.Remove(id); }
            return slotOf;
        }

        // Per-car absolute ground coordinates from the raw game struct, when the
        // game exposes them (AC: DataContainer.Graphics.CarCoordinates, a flat
        // float[] of x,y,z per slot). Reflection-only so we don't take a
        // compile-time dependency on any game plugin; any failure returns null
        // and the caller falls back to SimHub's generic fields.
        private static Type? s_gfxRawType;
        private static PropertyInfo? s_rawGraphicsProp;
        private static Type? s_gfxType;
        private static FieldInfo? s_gfxCarCoordsField;

        private static float[]? TryReadRawCarCoordinates(StatusDataBase data)
        {
            try
            {
                object? raw = data.GetRawDataObject();
                if (raw == null) return null;
                var rt = raw.GetType();
                if (rt != s_gfxRawType)
                {
                    s_gfxRawType = rt;
                    s_rawGraphicsProp = rt.GetProperty("Graphics");
                }
                object? gfx = s_rawGraphicsProp?.GetValue(raw);
                if (gfx == null) return null;
                var gt = gfx.GetType();
                if (gt != s_gfxType)
                {
                    s_gfxType = gt;
                    s_gfxCarCoordsField = gt.GetField("CarCoordinates");
                }
                return s_gfxCarCoordsField?.GetValue(gfx) as float[];
            }
            catch
            {
                return null;
            }
        }

        // Cached MethodInfo for SimHub.Plugins
        // PersistantTrackerPlugin.GetMap() — the public static accessor for the
        // recorded track map. Reflected (not a compile-time call) so a SimHub
        // build that moves or renames it degrades to null instead of throwing.
        private static MethodInfo? s_getMapMethod;
        private static bool s_getMapResolved;

        // The live recorded track-map record, or null when none exists yet or
        // SimHub's internals have shifted.
        private static DataRecordBase? TryGetMapRecord()
        {
            try
            {
                if (!s_getMapResolved)
                {
                    s_getMapResolved = true;
                    var t = Type.GetType(
                        "SimHub.Plugins.DataPlugins.PersistantTracker.PersistantTrackerPlugin, SimHub.Plugins");
                    s_getMapMethod = t?.GetMethod("GetMap",
                        BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                }
                return s_getMapMethod?.Invoke(null, null) as DataRecordBase;
            }
            catch
            {
                return null;
            }
        }

        // ── Track bounding box (from SimHub's recorded map) ───────────────────
        // The recorded points (DataRecord.CarCoordinates[].Value) are already in
        // the space ToAbsoluteCoordinates resolves cars into — world metres, or
        // for lap-relative games (iRacing) SimHub's integrated map metres. Cached
        // per map instance: SimHub swaps the record when a better lap is saved or
        // the online map loads, and that moves the space.
        private static DataRecordBase? s_boundsMap;
        private static int s_boundsCoordCount = -1;
        private static (double minX, double maxX, double minY, double maxY, double minZ, double maxZ)? s_boundsCache;

        private static (double minX, double maxX, double minY, double maxY, double minZ, double maxZ)? GetMapBounds(
            DataRecordBase? map)
        {
            if (map == null) return null;
            int count = map.CarCoordinates?.Count ?? 0;
            if (ReferenceEquals(map, s_boundsMap) && count == s_boundsCoordCount) return s_boundsCache;
            var b = ComputeMapBounds(map);
            s_boundsMap = map;
            s_boundsCoordCount = count;
            s_boundsCache = b;
            if (b.HasValue)
                MozaLog.Info($"[AZOM] track map bounds: X {b.Value.minX:F0}..{b.Value.maxX:F0} " +
                    $"Z {b.Value.minZ:F0}..{b.Value.maxZ:F0} Y {b.Value.minY:F0}..{b.Value.maxY:F0} " +
                    $"(n={count}, relative={map.HasRelativeCarCoordinates}, SimHub recorded map)");
            return b;
        }

        // Fraction trimmed off each end of each axis of a world-space map to drop
        // pit-lane / off-track excursions — sparse spatial tails that inflate the raw
        // min/max but aren't part of the drawn track surface (e.g. Imola's recorded X
        // runs to 879 in the pit lane vs the track's 331). Lap-relative maps are the
        // racing line itself and match the wheel's outline untrimmed.
        private const double BoundsTrimPct = 1.0;

        private static double Percentile(List<double> sorted, double pct)
        {
            if (sorted.Count == 0) return 0.0;
            int i = (int)Math.Round(pct / 100.0 * (sorted.Count - 1));
            if (i < 0) i = 0; else if (i >= sorted.Count) i = sorted.Count - 1;
            return sorted[i];
        }

        private static (double minX, double maxX, double minY, double maxY, double minZ, double maxZ)? ComputeMapBounds(
            DataRecordBase map)
        {
            try
            {
                var coords = map.CarCoordinates;
                if (coords == null || coords.Count < 20) return null;
                var xs = new List<double>(coords.Count);
                var ys = new List<double>(coords.Count);
                var zs = new List<double>(coords.Count);
                foreach (var pt in coords)
                {
                    var v = pt?.Value;
                    if (v == null || v.Length < 3) continue;
                    xs.Add(v[0]); ys.Add(v[1]); zs.Add(v[2]);
                }
                if (xs.Count < 20) return null;
                xs.Sort(); ys.Sort(); zs.Sort();

                double p = map.HasRelativeCarCoordinates ? 0.0 : BoundsTrimPct;
                double minX = Percentile(xs, p), maxX = Percentile(xs, 100 - p);
                double minZ = Percentile(zs, p), maxZ = Percentile(zs, 100 - p);
                if (maxX - minX < 10.0 || maxZ - minZ < 10.0) return null;
                return (minX, maxX, ys[0], ys[ys.Count - 1], minZ, maxZ);
            }
            catch { return null; }
        }

        // Project a car's coordinate array to world (X, Y=elevation, Z). When a
        // track map is recorded for a lap-relative-coordinate game, route through
        // DataRecordBase.ToAbsoluteCoordinates (→ world space); a passthrough for
        // world-coordinate games. Mirrors RadarItem.UpdateData. Defensive: any
        // failure or short result keeps the raw [0],[1],[2].
        private static (float X, float Y, float Z) ToWorldXyz(DataRecordBase? map, double[] c)
        {
            double x = c[0], y = c.Length > 1 ? c[1] : 0.0, z = c.Length > 2 ? c[2] : 0.0;
            if (map != null)
            {
                try
                {
                    double[]? a = map.ToAbsoluteCoordinates(c);
                    if (a != null && a.Length >= 3) { x = a[0]; y = a[1]; z = a[2]; }
                }
                catch { /* keep raw [0],[1],[2] */ }
            }
            return ((float)x, (float)y, (float)z);
        }

        // Car position from its coordinate array. On an outline line, [0] is the lap
        // fraction; the result is metres in SimHub's lap-relative frame (X = outline
        // x, Z = −outline y), offset by one unit so no on-track point is (0, 0) — the
        // empty-slot marker. Y (elevation) is unknown there and left 0.
        private static (float X, float Y, float Z) ResolveXyz(
            DataRecordBase? map, TrackOutline? line, double scale, double[] c)
        {
            if (line == null) return ToWorldXyz(map, c);
            double f = c[0];
            if (!(f >= 0.0 && f <= 1.5)) return (0f, 0f, 0f);   // iRacing: −1 = not in world
            var (u, v) = line.AtLapFraction(f);
            return ((float)((1.0 + u) * scale), 0f, (float)(-(1.0 + v) * scale));
        }

        // Without a known track length the map still fits (the transform is
        // scale-free); only the radar needs real metres.
        private const double LineScaleUnknown = 1000.0;
        private static TrackOutline? s_lineScaleOutline;
        private static double s_lineScaleLength = double.NaN;

        // Metres per outline unit: SimHub's track length over the line's length.
        private static double LineMetresPerUnit(StatusDataBase data, TrackOutline line, out bool metres)
        {
            double len = 0.0;
            try { len = data.TrackLength > 0.0 ? data.TrackLength : data.ReportedTrackLength; } catch { }
            metres = len >= 500.0 && len <= 30000.0;
            double scale = metres ? len / line.Perimeter : LineScaleUnknown;
            if (!ReferenceEquals(line, s_lineScaleOutline) || len != s_lineScaleLength)
            {
                s_lineScaleOutline = line;
                s_lineScaleLength = len;
                MozaLog.Info($"[AZOM] track outline line '{line.Path}': {line.Line!.Length / 2} pts, " +
                    $"TrackLength={len:F0} -> {scale:F1} m/unit{(metres ? "" : " (no length: radar off)")}");
            }
            return scale;
        }

        private static double ParseGear(string? gear)
        {
            if (gear == null || gear.Length == 0) return 0;
            if (gear == "R") return -1.0; // int30: -1 encodes as raw=31 (5-bit two's complement)
            if (gear == "N") return 0;
            if (int.TryParse(gear, out int n)) return n;
            return 0;
        }

        public double GetField(SimHubField field)
        {
            switch (field)
            {
                case SimHubField.SpeedKmh:              return SpeedKmh;
                case SimHubField.Rpms:                  return Rpms;
                case SimHubField.Gear:                  return Gear;
                case SimHubField.Throttle:              return Throttle;
                case SimHubField.Brake:                 return Brake;
                case SimHubField.BestLapTimeSeconds:    return BestLapTimeSeconds;
                case SimHubField.CurrentLapTimeSeconds: return CurrentLapTimeSeconds;
                case SimHubField.LastLapTimeSeconds:    return LastLapTimeSeconds;
                case SimHubField.DeltaToSessionBest:    return DeltaToSessionBest;
                case SimHubField.FuelPercent:           return FuelPercent;
                case SimHubField.DrsEnabled:            return DrsEnabled;
                case SimHubField.ErsPercent:            return ErsPercent;
                case SimHubField.TyreWearFrontLeft:     return TyreWearFrontLeft;
                case SimHubField.TyreWearFrontRight:    return TyreWearFrontRight;
                case SimHubField.TyreWearRearLeft:      return TyreWearRearLeft;
                case SimHubField.TyreWearRearRight:     return TyreWearRearRight;
                case SimHubField.CurrentLap:            return CurrentLap;
                case SimHubField.PlayerIndex:           return PlayerIndex;
                default:                                return 0.0;
            }
        }
    }
}

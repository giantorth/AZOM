using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;

namespace MozaPlugin.Protocol
{
    /// <summary>Per-device class for the HID reader's routing decision.</summary>
    internal enum MozaHidClass
    {
        /// <summary>Wheelbase / pedals / handbrake / hub — standard usage→field mapping.</summary>
        Standard = 0,
        /// <summary>
        /// mBooster Pedals (PID 0x0008) — single axis, position routed via
        /// <c>MBoosterAxisChanged</c> event so the registry can fan it out
        /// to whichever role the user picked. Direct usage→field assignment
        /// is skipped for this class.
        /// </summary>
        MBooster = 1,
        /// <summary>
        /// Pedals plugged STRAIGHT into the PC (their own VID 0x346E PID 0x0001/
        /// 0x0003/0x0011 HID), NOT through a wheelbase. Their 3-axis HID layout
        /// (Rx/Ry/Rz = 0x33/0x34/0x35) differs from the base's pedal layout
        /// (Z/Rz/Slider), so the Standard usage→field switch would route them to
        /// paddle/brake fields. This class maps them to throttle/brake/clutch.
        /// </summary>
        Pedals = 2,
        /// <summary>
        /// MOZA Multi-Function Stalks (PID 0x0024) — a 28-button HID joystick.
        /// Its buttons are routed to <c>MozaData.StalksButtonStates</c> (NOT the
        /// shared wheel button array) and surfaced as edge events via
        /// <c>StalksButtonChanged</c> so the truck-sim keyboard feature can read
        /// them without colliding with the wheel's own button indices.
        /// </summary>
        Stalks = 3,
    }

    /// <summary>Reads physical input positions from Moza HID devices (VID 0x346E).</summary>
    internal sealed class MozaHidReader : IDisposable
    {
        // HID usage IDs (page << 16 | usage)
        private const uint UsageX      = 0x00010030; // GenericDesktop.X      → steering
        private const uint UsageY      = 0x00010031; // GenericDesktop.Y      → combined paddles
        private const uint UsageZ      = 0x00010032; // GenericDesktop.Z      → throttle
        private const uint UsageRx     = 0x00010033; // GenericDesktop.Rx     → right paddle
        private const uint UsageRy     = 0x00010034; // GenericDesktop.Ry     → left paddle
        private const uint UsageRz     = 0x00010035; // GenericDesktop.Rz     → brake
        private const uint UsageSlider = 0x00010036; // GenericDesktop.Slider → clutch   (kernel: ABS_THROTTLE)
        private const uint UsageDial   = 0x00010037; // GenericDesktop.Dial   → handbrake (Wine maps ABS_RUDDER here)
        private const uint UsageSimRud = 0x000200BA; // Simulation.Rudder     → handbrake (native Windows)
        private const uint UsageSimThr = 0x000200BB; // Simulation.Throttle   → clutch   (winebus udev backend maps ABS_THROTTLE here)

        private static readonly uint[] TrackedUsages = { UsageX, UsageY, UsageZ, UsageRx, UsageRy, UsageRz, UsageSlider, UsageDial, UsageSimRud, UsageSimThr };

        private readonly MozaData _data;
        private Thread? _thread;
        private volatile bool _stop;
        private long _hidParseErrorCount;

        /// <summary>
        /// Fires when an mBooster's HID axis changes. Args: device identity
        /// (extracted USB parent instance segment from
        /// <see cref="HidDevice.DevicePath"/>); Windows Container ID of the HID
        /// interface (identical to the CDC lane's — used to pair HID↔CDC across
        /// multiple lanes; "" when unavailable); axis index (0 = the master
        /// unit's pedal, 1 = 2nd chained device, 2 = 3rd — a stable order by
        /// ascending GenericDesktop usage id, since a chain host reports all
        /// its pedals as separate axes on one HID report); normalized position
        /// in [0, 1]. Subscribed by <c>MozaMBoosterRegistry.OnHidAxisUpdate</c>
        /// which routes each axis into MozaData by its per-axis role.
        /// </summary>
        public event Action<string, string, int, double>? MBoosterAxisChanged;

        /// <summary>
        /// Fires on every MOZA Stalks button edge: (0-based button index, pressed).
        /// Raised on the HID read thread. Consumed by the truck-sim keyboard
        /// controller (<c>StalksTruckSimController</c>) for low-latency momentary
        /// key output; also mirrored into <c>MozaData.StalksButtonStates</c>.
        /// </summary>
        public event Action<int, bool>? StalksButtonChanged;

        // Live HidStreams so Dispose can force-close silent devices' blocked reads.
        private readonly object _streamsLock = new object();
        private readonly List<HidStream> _liveStreams = new List<HidStream>();

        public MozaHidReader(MozaData data)
        {
            _data = data;
        }

        private volatile bool _disposed;

        public void Start()
        {
            if (_disposed) return;
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MozaHidReader",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;

            // Force-close every live stream so any HidSharp internal reader
            // wedged in stream.Read throws and the owning ReadDevice thread
            // exits its wait loop.
            HidStream[] snapshot;
            lock (_streamsLock) snapshot = _liveStreams.ToArray();
            foreach (var s in snapshot)
            {
                try { s.Close(); } catch { }
            }

            try { _thread?.Join(2000); } catch { }
            _thread = null;
            _data.IsHidConnected = false;
        }

        /// <summary>
        /// Returns the current wheel position in degrees, where 0 is center
        /// and ±(maxAngleDeg/2) is full lock.
        /// </summary>
        public double GetCurrentAngleDegrees(int maxAngleDeg)
        {
            if (!_data.IsHidConnected) return 0.0;
            int min = _data.SteeringAngleRawMin;
            int max = _data.SteeringAngleRawMax;
            int range = max - min;
            if (range <= 0) return 0.0;
            int raw = _data.SteeringAngleRaw;
            double normalized = ((raw - min) / (double)range) * 2.0 - 1.0;
            return normalized * (maxAngleDeg / 2.0);
        }

        /// <summary>
        /// Returns the current wheel position as a 0-100 percentage of physical
        /// travel, where 0 = full lock in one direction, 50 = center, and
        /// 100 = full lock in the other. Unlike <see cref="GetCurrentAngleDegrees"/>
        /// this is independent of the base's reported max-angle, so it is valid
        /// as soon as the HID min/max range has been observed. Returns -1 when
        /// no HID device is connected or the range is unknown.
        /// </summary>
        public double GetSteeringPositionPercent()
        {
            if (!_data.IsHidConnected) return -1.0;
            int min = _data.SteeringAngleRawMin;
            int max = _data.SteeringAngleRawMax;
            int range = max - min;
            if (range <= 0) return -1.0;
            int raw = _data.SteeringAngleRaw;
            double pct = ((raw - min) / (double)range) * 100.0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return pct;
        }

        private void Run()
        {
            while (!_stop)
            {
                var devices = FindMozaDevices();
                _data.IsStalksConnected = devices.Any(d => d.Item3 == MozaHidClass.Stalks);
                if (devices.Count == 0)
                {
                    _data.IsHidConnected = false;
                    SleepInterruptible(2000);
                    continue;
                }

                var threads = new List<Thread>();
                int openCount = 0;

                try
                {
                    foreach (var (device, usages, deviceClass, identity) in devices)
                    {
                        if (!device.TryOpen(out HidStream stream)) continue;
                        try
                        {
                            openCount++;
                            // Register so Dispose() can force-close on shutdown.
                            lock (_streamsLock) _liveStreams.Add(stream);

                            bool isHandbrake = MozaUsbIds.IsHandbrakePid((ushort)device.ProductID);
                            string idCapture = identity;
                            MozaHidClass classCapture = deviceClass;
                            // ReadDevice owns `stream` and disposes it before returning.
                            var t = new Thread(() => ReadDevice(device, stream, usages, isHandbrake, classCapture, idCapture))
                            {
                                IsBackground = true,
                                Name = $"MozaHid_{device.ProductID:X4}",
                            };
                            threads.Add(t);
                            t.Start();
                        }
                        catch
                        {
                            lock (_streamsLock) _liveStreams.Remove(stream);
                            try { stream.Dispose(); } catch { }
                            throw;
                        }
                    }

                    if (openCount > 0)
                        _data.IsHidConnected = true;

                    // Wait until we're stopping, or ANY device thread exits.
                    // With multiple Moza HID devices (e.g. a wheelbase + Universal
                    // Hub) one device's stream dropping MUST trigger a full
                    // re-enumerate so that device gets reopened. Waiting for ALL
                    // threads instead would park on the still-live sibling forever
                    // and freeze the dead device's values permanently — the base's
                    // HID thread (the only one carrying the steering X axis) dies,
                    // the hub thread keeps the reader alive, and Moza.SteeringAngle
                    // stays stuck at its last value while pedals/handbrake (hub)
                    // keep updating. Single-device setups always recovered because
                    // the lone thread dying ended the wait and re-enumerated.
                    while (!_stop && threads.Count > 0 && threads.All(t => t.IsAlive))
                        SleepInterruptible(250);

                    // A device dropped (or we're shutting down): force every
                    // still-open stream closed so the surviving ReadDevice threads
                    // unblock from their read wait and exit (same mechanism as
                    // Dispose), then join them all so each disposes its own stream
                    // before we re-enumerate from the top.
                    HidStream[] snapshot;
                    lock (_streamsLock) snapshot = _liveStreams.ToArray();
                    foreach (var s in snapshot)
                    {
                        try { s.Close(); } catch { }
                    }
                    foreach (var t in threads)
                        try { t.Join(1000); } catch { }
                }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] HID error: {ex.Message}");
                }

                _data.IsHidConnected = false;
                SleepInterruptible(1000);
            }
        }

        /// <summary>
        /// Finds all Moza HID devices by VID 0x346E, then categorizes by PID
        /// via <see cref="MozaUsbIds"/>: wheelbase / pedals / handbrake / hub are
        /// treated as <see cref="MozaHidClass.Standard"/>; mBooster (PID 0x0008)
        /// is tagged with <see cref="MozaHidClass.MBooster"/> + a stable identity
        /// extracted from <see cref="HidDevice.DevicePath"/> so the registry
        /// can pair the HID device with its serial-port sibling. AB9 / shifter
        /// PIDs are skipped (no axes we consume). Unknown Moza PIDs are admitted
        /// optimistically so future hardware works without a code change.
        ///
        /// VID-based matching replaces an earlier friendly-name regex, which
        /// only worked on Linux: HidSharp's <c>GetFriendlyName()</c> returns the
        /// kernel HID name there (e.g. "Gudsen MOZA R9 Ultra Base") but the
        /// generic SetupAPI device description on Windows ("HID-compliant game
        /// controller"), so no regex pattern matched and the steering / pedal /
        /// handbrake bars stayed blank.
        /// </summary>
        private static List<(HidDevice device, Dictionary<uint, (int min, int max)> usages, MozaHidClass kind, string identity)> FindMozaDevices()
        {
            var result = new List<(HidDevice, Dictionary<uint, (int, int)>, MozaHidClass, string)>();
            IEnumerable<HidDevice> allDevices;
            try
            {
                allDevices = DeviceList.Local.GetHidDevices();
            }
            catch { return result; }

            foreach (var dev in allDevices)
            {
                try
                {
                    if (dev.VendorID != MozaPortDiscovery.MozaVid) continue;

                    ushort pid = (ushort)dev.ProductID;
                    var category = MozaUsbIds.Categorize(pid);

                    bool isMBooster = category == MozaDeviceCategory.MBooster;
                    bool isStandard =
                        category == MozaDeviceCategory.Wheelbase ||
                        category == MozaDeviceCategory.Pedals    ||
                        category == MozaDeviceCategory.Handbrake ||
                        category == MozaDeviceCategory.Hub       ||
                        category == MozaDeviceCategory.Stalks    ||
                        category == MozaDeviceCategory.Unknown;  // forward-compat for new PIDs

                    if (!isMBooster && !isStandard) continue;

                    var usages = new Dictionary<uint, (int min, int max)>();
                    // Usages the filter below rejects, logged so a support bundle
                    // shows axes the plugin ignored (e.g. Wine renaming an axis to
                    // a Simulation-page usage) instead of just omitting them.
                    var dropped = new HashSet<uint>();
                    var descriptor = dev.GetReportDescriptor();
                    foreach (var item in descriptor.DeviceItems)
                    {
                        foreach (var report in item.InputReports)
                        {
                            foreach (var dataItem in report.DataItems)
                            {
                                foreach (uint usage in dataItem.Usages.GetAllValues())
                                {
                                    // For mBooster we want EVERY GenericDesktop axis
                                    // (page 0x0001, usages 0x30..0x37) — the doc
                                    // doesn't pin a specific axis, so we accept the
                                    // first one that streams data at runtime.
                                    bool isAxis = (usage >> 16) == 0x0001 && (usage & 0xFFFF) >= 0x30 && (usage & 0xFFFF) <= 0x37;
                                    bool isButton = (usage >> 16) == 0x0009;
                                    bool tracked = Array.IndexOf(TrackedUsages, usage) >= 0;
                                    if (tracked || isButton || (isMBooster && isAxis))
                                        usages[usage] = (dataItem.LogicalMinimum, dataItem.LogicalMaximum);
                                    else
                                        dropped.Add(usage);
                                }
                            }
                        }
                    }
                    if (dropped.Count > 0)
                        MozaLog.Debug(
                            $"[AZOM] HID descriptor PID {pid:X4}: {dropped.Count} untracked usage(s): " +
                            string.Join(", ", dropped.OrderBy(u => u).Take(24).Select(u => $"0x{u:X8}")));
                    if (usages.Count > 0)
                    {
                        var kind = isMBooster ? MozaHidClass.MBooster
                                 : category == MozaDeviceCategory.Pedals ? MozaHidClass.Pedals
                                 : category == MozaDeviceCategory.Stalks ? MozaHidClass.Stalks
                                 : MozaHidClass.Standard;
                        string identity = isMBooster ? ExtractUsbParentInstance(dev) : "";
                        if (isMBooster)
                        {
                            // Diagnostic for the HID/CDC identity-reconciliation gap
                            // (docs/protocol/devices/mbooster.md) — logs the raw
                            // DevicePath alongside the extracted identity so a
                            // mismatch against the CDC InstanceId can be read
                            // straight out of a support-bundle export.
                            string path = "", hidSerial = "";
                            try { path = dev.DevicePath ?? ""; } catch { }
                            // USB iSerialNumber (present on the mBooster — descriptor
                            // index 3, capture-confirmed). If HidSharp surfaces it,
                            // it's a per-unit key that matches across the HID + CDC
                            // interfaces — a candidate HID↔CDC pairing token (Stage 4).
                            try { hidSerial = dev.GetSerialNumber() ?? ""; } catch { }
                            MozaLog.Debug($"[AZOM/mBooster] HID identity extracted: '{identity}' serial='{MozaLog.RedactId(hidSerial)}' from path '{path}'");
                        }
                        result.Add((dev, usages, kind, identity));
                    }
                }
                catch { }
            }

            return result;
        }

        /// <summary>
        /// Extract the USB parent device instance segment from a HID device path.
        /// Windows HID device paths look like
        /// <c>\\?\HID#VID_346E&amp;PID_0008&amp;MI_02#a&amp;399b951f&amp;0&amp;0002#{4d1e55b2-...}</c>;
        /// the parent USB device's instance ID is the second '#'-delimited
        /// segment. Mirroring the registry walk in <see cref="MozaPortDiscovery"/>
        /// lets the registry's CDC port match against the HID device.
        ///
        /// Falls back to the full device path if the format doesn't match —
        /// the registry tolerates non-canonical identities (it's still
        /// deterministic per-physical-device, just less collision-tolerant
        /// across replug-to-different-USB-port).
        /// </summary>
        internal static string ExtractUsbParentInstance(HidDevice dev)
        {
            string path = "";
            try { path = dev.DevicePath ?? ""; } catch { }
            if (string.IsNullOrEmpty(path)) return "hid:unknown";

            // Split on '#'. Expect: [\\?\HID, VID_..., parent-instance, {GUID}]
            var parts = path.Split('#');
            if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
                return parts[2];
            return path;
        }

        private void ReadDevice(HidDevice device, HidStream stream, Dictionary<uint, (int min, int max)> usages, bool isHandbrake, MozaHidClass kind = MozaHidClass.Standard, string identity = "")
        {
            // Every exit path must de-register + dispose the stream: Run() re-enumerates
            // as soon as this thread ends, and a stream left in _liveStreams is one more
            // dead entry per pass.
            bool released = false;
            void Release()
            {
                if (released) return;
                released = true;
                lock (_streamsLock) _liveStreams.Remove(stream);
                try { stream.Dispose(); } catch { }
            }
            try
            {
                var descriptor = device.GetReportDescriptor();
                // Find the DeviceItem that contains our tracked usages
                DeviceItem? targetItem = null;
                foreach (var item in descriptor.DeviceItems)
                {
                    foreach (var report in item.InputReports)
                    {
                        foreach (var dataItem in report.DataItems)
                        {
                            if (dataItem.Usages.GetAllValues().Any(u => usages.ContainsKey(u)))
                            {
                                targetItem = item;
                                break;
                            }
                        }
                        if (targetItem != null) break;
                    }
                    if (targetItem != null) break;
                }

                if (targetItem == null)
                {
                    if (kind == MozaHidClass.MBooster)
                        MozaLog.Warn($"[AZOM/mBooster] {identity}: no DeviceItem contains any tracked usage — HID read never started for this device.");
                    return;
                }

                // Cache steering axis range if this device has it
                if (usages.ContainsKey(UsageX))
                {
                    _data.SteeringAngleRawMin = usages[UsageX].min;
                    _data.SteeringAngleRawMax = usages[UsageX].max;
                }

                stream.ReadTimeout = Timeout.Infinite;
                var parser = targetItem.CreateDeviceItemInputParser();
                var receiver = descriptor.CreateHidDeviceInputReceiver();
                var buffer = new byte[descriptor.MaxInputReportLength];
                var stopped = new ManualResetEventSlim(false);

                // Diagnostic-only counters for the mBooster HID pipeline —
                // every step here (report parse failure, no changed usages,
                // an untracked usage firing) previously failed completely
                // silently, which made a stuck-at-zero position bar
                // impossible to root-cause from logs alone.
                long mbReceivedCount = 0;
                long mbParseFailCount = 0;
                long mbChangeCount = 0;

                // Stable axis order for an mBooster chain host: sort this
                // device's GenericDesktop axis usages (0x30..0x37) ascending and
                // map each to an index. Axis 0 = the master unit, 1 = 2nd
                // chained device, etc. (the "deterministic order" the registry
                // routes on). Computed once per device open.
                Dictionary<uint, int>? mBoosterAxisIndex = null;
                string mBoosterContainerId = "";
                if (kind == MozaHidClass.MBooster)
                {
                    var axisUsages = usages.Keys
                        .Where(u => (u >> 16) == 0x0001 && (u & 0xFFFF) >= 0x30 && (u & 0xFFFF) <= 0x37)
                        .OrderBy(u => u)
                        .ToList();
                    mBoosterAxisIndex = new Dictionary<uint, int>(axisUsages.Count);
                    for (int ai = 0; ai < axisUsages.Count; ai++)
                        mBoosterAxisIndex[axisUsages[ai]] = ai;
                    // Container ID (same across this device's HID + CDC interfaces)
                    // so the registry can pair this axis stream to its CDC lane.
                    try { mBoosterContainerId = MozaPortDiscovery.Instance.GetHidContainerId(device.DevicePath ?? ""); }
                    catch { }
                    if (mBoosterContainerId.Length == 0)
                    {
                        // No Container ID: Wine has no Enum\HID registry at all,
                        // and some Windows driver stacks omit the value. The USB
                        // serial is the same on both interfaces of one device and
                        // is what the sysfs source publishes as the CDC-side
                        // ContainerId, so it pairs them. On Windows this is a
                        // harmless no-op — a serial never matches a Container GUID.
                        try { mBoosterContainerId = device.GetSerialNumber() ?? ""; }
                        catch { }
                    }
                    MozaLog.Debug($"[AZOM/mBooster] {identity}: {axisUsages.Count} axis/axes " +
                        $"[{string.Join(", ", axisUsages.Select(u => $"0x{u & 0xFFFF:X2}"))}] container='{mBoosterContainerId}'");
                }

                // changedIndex → usage is fixed by the report descriptor; caching
                // it avoids a LINQ enumerator per changed value per report (the
                // steering axis changes continuously at report rate).
                var usageByChangedIndex = new Dictionary<int, uint>();

                receiver.Received += (sender, e) =>
                {
                    try
                    {
                        if (kind == MozaHidClass.MBooster)
                        {
                            long n = System.Threading.Interlocked.Increment(ref mbReceivedCount);
                            if (n == 1 || n % 500 == 0)
                                MozaLog.Debug($"[AZOM/mBooster] {identity}: Received event #{n}");
                        }
                        while (receiver.TryRead(buffer, 0, out Report report))
                        {
                            if (!parser.TryParseReport(buffer, 0, report))
                            {
                                if (kind == MozaHidClass.MBooster)
                                {
                                    long n = System.Threading.Interlocked.Increment(ref mbParseFailCount);
                                    if (n == 1 || n % 500 == 0)
                                        MozaLog.Debug($"[AZOM/mBooster] {identity}: TryParseReport failed #{n}");
                                }
                                continue;
                            }

                            while (parser.HasChanged)
                            {
                                int changedIndex = parser.GetNextChangedIndex();
                                var value = parser.GetValue(changedIndex);
                                if (!usageByChangedIndex.TryGetValue(changedIndex, out uint usage))
                                {
                                    usage = value.Usages.FirstOrDefault();
                                    usageByChangedIndex[changedIndex] = usage;
                                }

                                if (kind == MozaHidClass.MBooster)
                                {
                                    long n = System.Threading.Interlocked.Increment(ref mbChangeCount);
                                    if (n <= 20 || n % 200 == 0)
                                        MozaLog.Debug(
                                            $"[AZOM/mBooster] {identity}: changed usage 0x{usage:X8} " +
                                            $"raw={value.GetLogicalValue()} tracked={usages.ContainsKey(usage)}");
                                }

                                if (usage == 0 || !usages.ContainsKey(usage)) continue;

                                if (usage == UsageX)
                                {
                                    _data.SteeringAngleRaw = value.GetLogicalValue();
                                }
                                else if ((usage >> 16) == 0x0009)
                                {
                                    // mBooster Pedals don't share the wheel button surface
                                    // — never route their button reports into the wheel's
                                    // button-state table or the handbrake's pressed flag.
                                    if (kind == MozaHidClass.MBooster) continue;

                                    bool pressed = value.GetLogicalValue() != 0;

                                    // MOZA Stalks buttons ride their own surface so they
                                    // never collide with wheel button indices — route to
                                    // StalksButtonStates and raise an edge event on change.
                                    if (kind == MozaHidClass.Stalks)
                                    {
                                        int stalkIndex = (int)(usage & 0xFFFF) - 1;
                                        if (stalkIndex >= 0 && stalkIndex < MozaData.MaxStalksButtons)
                                        {
                                            if (_data.StalksButtonStates[stalkIndex] != pressed)
                                            {
                                                _data.StalksButtonStates[stalkIndex] = pressed;
                                                if (stalkIndex >= _data.StalksButtonCount)
                                                    _data.StalksButtonCount = stalkIndex + 1;
                                                try { StalksButtonChanged?.Invoke(stalkIndex, pressed); } catch { }
                                            }
                                        }
                                        continue;
                                    }

                                    if (isHandbrake)
                                    {
                                        _data.HandbrakeButtonPressed = pressed;
                                    }
                                    else
                                    {
                                        int buttonIndex = (int)(usage & 0xFFFF) - 1;
                                        if (buttonIndex == 120)
                                        {
                                            _data.HandbrakeButtonPressed = pressed;
                                        }
                                        else if (buttonIndex >= 0 && buttonIndex < MozaData.MaxButtons)
                                        {
                                            _data.ButtonStates[buttonIndex] = pressed;
                                            if (buttonIndex >= _data.ButtonCount)
                                                _data.ButtonCount = buttonIndex + 1;
                                        }
                                    }
                                }
                                else
                                {
                                    var range = usages[usage];
                                    if (range.max > range.min)
                                    {
                                        if (kind == MozaHidClass.MBooster)
                                        {
                                            // mBooster axes route via the registry — never directly into
                                            // MozaData. A chain host reports EVERY hosted pedal as its own
                                            // GenericDesktop axis on one report, so we emit ALL of them
                                            // (each tagged with its stable index) rather than only the
                                            // first; the registry routes each axis to throttle/brake/clutch
                                            // by its per-axis role and merges across lanes first-wins.
                                            double raw = value.GetLogicalValue();
                                            double normalized01 = (raw - range.min) / (double)(range.max - range.min);
                                            if (normalized01 < 0) normalized01 = 0;
                                            if (normalized01 > 1) normalized01 = 1;
                                            int axisIndex = (mBoosterAxisIndex != null && mBoosterAxisIndex.TryGetValue(usage, out var ai)) ? ai : 0;
                                            try { MBoosterAxisChanged?.Invoke(identity, mBoosterContainerId, axisIndex, normalized01); }
                                            catch (Exception ex) { MozaLog.Debug($"[AZOM] mBooster axis handler: {ex.Message}"); }
                                            continue;
                                        }

                                        int pct = NormalizePct(value.GetLogicalValue(), range.min, range.max);

                                        if (kind == MozaHidClass.Pedals)
                                        {
                                            // Standalone pedal HID exposes only Rx/Ry/Rz; these
                                            // are throttle/brake/clutch (NOT paddles), confirmed
                                            // on CRP2 hardware (axis order matches the base's
                                            // throttle<brake<clutch usage ordering).
                                            switch (usage)
                                            {
                                                case UsageRx: _data.ThrottlePosition = pct; break;
                                                case UsageRy: _data.BrakePosition    = pct; break;
                                                case UsageRz: _data.ClutchPosition   = pct; break;
                                            }
                                            continue;
                                        }

                                        switch (usage)
                                        {
                                            case UsageY:      _data.CombinedPaddlePosition = pct; break;
                                            case UsageZ:      _data.ThrottlePosition       = pct; break;
                                            case UsageRx:     _data.RightPaddlePosition    = pct; break;
                                            case UsageRy:     _data.LeftPaddlePosition     = pct; break;
                                            case UsageRz:     _data.BrakePosition          = pct; break;
                                            case UsageSlider: _data.ClutchPosition         = pct; break;
                                            case UsageSimThr: _data.ClutchPosition         = pct; break;
                                            case UsageDial:   _data.HandbrakePosition      = pct; break;
                                            case UsageSimRud: _data.HandbrakePosition      = pct; break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        long n = System.Threading.Interlocked.Increment(ref _hidParseErrorCount);
                        // Log first, then every 1000th error to avoid spam.
                        if (n == 1 || n % 1000 == 0)
                            MozaLog.Debug($"[AZOM] HID parse error #{n}: {ex.Message}");
                    }
                };
                receiver.Stopped += (sender, e) =>
                {
                    try { stopped.Set(); } catch (ObjectDisposedException) { }
                };

                try
                {
                    receiver.Start(stream);
                    MozaLog.Debug(
                        $"[AZOM] HID device opened: {device.GetFriendlyName()} " +
                        $"(VID {device.VendorID:X4} PID {device.ProductID:X4}, " +
                        $"usages: {string.Join(", ", usages.Keys.Select(u => $"0x{u:X8}"))})");

                    while (!_stop && !stopped.Wait(250)) { }
                }
                finally
                {
                    // De-register + dispose here so Stopped fires while `stopped` is still alive.
                    Release();
                    // Give receiver a chance to drain Stopped before disposing the event.
                    try { stopped.Wait(500); } catch { }
                    stopped.Dispose();
                }
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] HID device read error ({device.GetFriendlyName()}): {ex.Message}");
            }
            finally
            {
                // Covers the early returns / throws before receiver.Start.
                Release();
            }
        }

        private static int NormalizePct(int raw, int min, int max)
        {
            double pct = (raw - min) / (double)(max - min) * 100.0;
            return Math.Max(0, Math.Min(100, (int)Math.Round(pct)));
        }

        private void SleepInterruptible(int ms)
        {
            int remaining = ms;
            while (remaining > 0 && !_stop)
            {
                int step = Math.Min(100, remaining);
                Thread.Sleep(step);
                remaining -= step;
            }
        }
    }
}

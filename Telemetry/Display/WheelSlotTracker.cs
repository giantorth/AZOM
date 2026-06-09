using System;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry.Display
{
    /// <summary>
    /// Tracks the wheel's currently-bound dashboard slot from b2h type-04
    /// records on sess=0x02, and detects wheel-initiated switches.
    ///
    /// STRICT VALIDATION: the wheel emits MANY 0x04-prefixed records on
    /// sess=0x02 b2h (TIER records inside tier-defs, URL backrefs, …).
    /// Earlier loose match (just "tag==0x04, read 4 bytes as slot")
    /// produced false-positive slots that triggered bogus
    /// WheelInitiatedSwitch + hot-reneg burst storms. Padding-zero +
    /// u8 bound + configJsonList range checks filter out non-slot records.
    /// </summary>
    internal sealed class WheelSlotTracker
    {
        private readonly TelemetrySender _sender;

        private int _wheelReportedSlot = -1;
        public int WheelReportedSlot => _wheelReportedSlot;

        // Buffered "wheel-initiated switch" event that arrived before the
        // wheel's configJsonList was available (the sess=0x02 type-04 slot
        // record races the sess=0x09 configJson state burst by ~180 ms on
        // hot-swap captures). Replayed by ReplayPendingSwitchIfReady() once
        // ConfigJsonLastState is non-null. -1 = nothing pending.
        private int _pendingSwitchSlot = -1;

        // Auto-detected slot-field layout: which u32 field the wheel echoes the
        // dashboard slot in. The two families mirror each other (W17/CS-Pro =
        // field A [1:5], W13/FSR2 = field B [5:9]); rather than hardcode every
        // model we LEARN it from the wheel's echo of a host kind=4 — the genuine
        // echo carries the commanded slot value in the wheel's field with the
        // other field zero. -1 = not yet detected (fall back to the model hint
        // _sender.SlotInFieldA until then). Reset on hot-swap.
        private int _detectedSlotFieldA = -1;

        // Last slot the host emitted FF kind=4 to. STATIC: survives plugin
        // instance recycle within a single SimHub process (game-switch path)
        // so the new game's profile-apply can skip the always-spurious 11 s
        // restart when targeting the same dashboard. Resets across SimHub
        // process boundary.
        private static int _lastEmittedKind4Slot = -1;
        public int LastEmittedKind4Slot => _lastEmittedKind4Slot;

        public WheelSlotTracker(TelemetrySender sender)
        {
            _sender = sender;
        }

        /// <summary>Set when the host emits FF kind=4 (SendDashboardSwitch).</summary>
        public void NoteHostEmittedKind4(int slot) => _lastEmittedKind4Slot = slot;

        /// <summary>Reset on wheel hot-swap: new wheel may not be bound to the prior slot.</summary>
        public void Reset()
        {
            _lastEmittedKind4Slot = -1;
            _wheelReportedSlot = -1;
            _pendingSwitchSlot = -1;
            _detectedSlotFieldA = -1;
        }

        /// <summary>
        /// Called by <see cref="TelemetrySender"/> after a configJson state
        /// blob has been parsed and <see cref="TelemetrySender.ConfigJsonLastState"/>
        /// is non-null. If a slot change was observed before the list arrived,
        /// re-validate against the now-available bounds and raise the deferred
        /// WheelInitiatedSwitch event. No-op when no pending switch exists.
        /// </summary>
        public void ReplayPendingSwitchIfReady()
        {
            if (_pendingSwitchSlot < 0) return;
            int slot = _pendingSwitchSlot;
            _pendingSwitchSlot = -1;

            if (slot == _lastEmittedKind4Slot) return;       // it was an echo after all
            if (!_sender.EnableHotRenegotiation) return;

            var state = _sender.ConfigJsonLastState;
            int listCount = state?.ConfigJsonList?.Count ?? 0;
            if (listCount == 0 || slot >= listCount)
            {
                MozaLog.Debug(
                    $"[AZOM] WheelSlotTracker: deferred slot={slot} still out of " +
                    $"configJsonList range (count={listCount}) after replay — dropping");
                return;
            }

            MozaLog.Info(
                $"[AZOM] Wheel-initiated switch (deferred replay): slot={slot} " +
                $"(lastEmitted={_lastEmittedKind4Slot}). Arming hot-reneg burst; " +
                "raising WheelInitiatedSwitch event.");
            _lastEmittedKind4Slot = slot;
            _sender.ArmHotSwitchBurst();
            _sender.RaiseWheelInitiatedSwitch(slot);
        }

        /// <summary>
        /// Parse a sess=0x02 b2h session-data chunk for a wheel-reported
        /// dashboard slot indicator (type-04 record). Updates
        /// <see cref="WheelReportedSlot"/> if found; raises
        /// <see cref="TelemetrySender.WheelInitiatedSwitch"/> when the slot
        /// change is a wheel-side action (not an echo of the host's kind=4).
        /// </summary>
        public void TryAbsorbType04Slot(byte[] chunkPayload)
        {
            // type-04 slot record (exactly 13 bytes) on b2h:
            //   payload[0]     = 0x04 (record type)
            //   payload[1..5]  = u32 LE field A
            //   payload[5..9]  = u32 LE field B
            //   payload[9..13] = CRC32 over payload[0..9]
            // The slot lives in ONE field, the other is a zero pad. Which field
            // is per-family (W17/CS-Pro = A, W13/FSR2 = B) and is AUTO-DETECTED
            // below from the wheel's echo of our kind=4 — so untested 2026-era
            // wheels self-calibrate. Reading the wheel's field + requiring the
            // other to be zero rejects the same-shape 0x04 catalog backrefs.
            if (chunkPayload == null || chunkPayload.Length != 13) return;
            if (chunkPayload[0] != 0x04) return;
            uint fieldA = (uint)(chunkPayload[1] | (chunkPayload[2] << 8)
                               | (chunkPayload[3] << 16) | (chunkPayload[4] << 24));
            uint fieldB = (uint)(chunkPayload[5] | (chunkPayload[6] << 8)
                               | (chunkPayload[7] << 16) | (chunkPayload[8] << 24));

            // Auto-detect the layout from the wheel's echo of a host kind=4 to a
            // non-zero slot: the genuine echo carries that slot value in exactly
            // one field with the other zero — latch which one. Until detected we
            // use the model hint (slot 0 is all-zero and decodes either way).
            if (_detectedSlotFieldA < 0)
            {
                int n = _lastEmittedKind4Slot;
                if (n > 0 && fieldA == (uint)n && fieldB == 0)
                {
                    _detectedSlotFieldA = 1;
                    MozaLog.Info($"[AZOM] Slot field auto-detected: A (wheel echoed kind=4 slot {n} in field A)");
                }
                else if (n > 0 && fieldB == (uint)n && fieldA == 0)
                {
                    _detectedSlotFieldA = 0;
                    MozaLog.Info($"[AZOM] Slot field auto-detected: B (wheel echoed kind=4 slot {n} in field B)");
                }
            }

            bool fieldAIsSlot = _detectedSlotFieldA >= 0
                ? _detectedSlotFieldA == 1
                : _sender.SlotInFieldA;
            uint slotField = fieldAIsSlot ? fieldA : fieldB;
            uint padField  = fieldAIsSlot ? fieldB : fieldA;
            if (padField != 0) return;          // the non-slot field must be the zero pad
            if (slotField > 255) return;        // real slot indices are u8
            int slot = (int)slotField;
            if (slot == _wheelReportedSlot) return;

            int prevSlot = _wheelReportedSlot;
            _wheelReportedSlot = slot;
            MozaLog.Debug($"[AZOM] Wheel reported active dashboard slot={slot} (was {prevSlot})");

            // Wheel-initiated switch detection. Wheel emits a b2h type-04
            // record after EVERY switch — echo of host kind=4 OR wheel's own
            // announcement. _lastEmittedKind4Slot match = echo; mismatch = wheel-side action.
            //
            // prevSlot == -1 → cold-start first-push, not a user switch.
            // EnableHotRenegotiation gates the wheel-initiated handler too:
            // legacy Stop+Start mode handles wheel switches via catalog-resync
            // probe + RestartForSwitch.
            if (prevSlot < 0) return;
            if (slot == _lastEmittedKind4Slot) return;
            if (!_sender.EnableHotRenegotiation) return;

            // Second-tier validation: slot must be in current configJsonList range.
            // Distinguish two cases:
            //   listCount == 0  → configJson state hasn't arrived yet (sess=0x09
            //                     state burst races the sess=0x02 type-04 slot
            //                     record by ~180 ms on hot-swap). Buffer the
            //                     switch for replay once state lands; KEEP
            //                     _wheelReportedSlot at the new value so the
            //                     tracker reflects ground truth.
            //   slot >= count   → genuine out-of-range; roll back so the next
            //                     legitimate record is recognised as a change.
            var stateForBounds = _sender.ConfigJsonLastState;
            int listCount = stateForBounds?.ConfigJsonList?.Count ?? 0;
            if (listCount == 0)
            {
                _pendingSwitchSlot = slot;
                MozaLog.Debug(
                    $"[AZOM] WheelSlotTracker: slot={slot} change observed before " +
                    "configJsonList arrived — buffering for replay when state lands");
                return;
            }
            if (slot >= listCount)
            {
                MozaLog.Debug(
                    $"[AZOM] WheelSlotTracker: slot={slot} out of " +
                    $"configJsonList range (count={listCount}) — not arming wheel-init burst");
                _wheelReportedSlot = prevSlot;
                return;
            }

            MozaLog.Info(
                $"[AZOM] Wheel-initiated switch detected: slot={slot} " +
                $"(was {prevSlot}, lastEmitted={_lastEmittedKind4Slot}). " +
                $"Arming hot-reneg burst; raising WheelInitiatedSwitch event.");

            // Track the new slot as if WE emitted kind=4 — prevents the next
            // echo from re-triggering this branch.
            _lastEmittedKind4Slot = slot;

            // Arm hot-reneg burst (same as plugin-initiated path but without
            // the kind=4 emit — wheel already sent its own).
            _sender.ArmHotSwitchBurst();

            _sender.RaiseWheelInitiatedSwitch(slot);
        }
    }
}

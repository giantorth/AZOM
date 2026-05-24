# MOZA SDK 1.0.1.8 — Public Header Inventory

Source: `/tmp/moza_sdk/MOZA_SDK/1.0.1.8/MSVC2022-64/include/`
Cross-checked against mangled exports in `lib/MOZA_SDK.lib` and Doxygen HTML.
All free functions live in `namespace moza`; effect classes live in `namespace RS21::direct_input`.

## 0. Object model

- Free C-style functions in `moza::` dominate (~100 setters/getters). Most reads take an `ERRORCODE&` out-param.
- Three user-owned C++ classes: `moza::HidDevice` (base, move-only, pimpl), `moza::SwitchesDevice : HidDevice`, `moza::ShifterDevice : HidDevice`.
- Six FFB effect classes in `RS21::direct_input::` all derive `Effect` (abstract). Handed to caller as `std::shared_ptr<>`; lifecycle owned by an internal `Device`.
- `MOZAPitHouseExeManager` appears in Doxygen but its source is `mozaAPI.cc` (Doxygen "generated from the following file") and the only members are inline ctor/dtor — **not in any public header**. Internal RAII.
- Other Doxygen-listed classes (`MotorControl`, `ScopeExit`, `moza_priv::Version` from private `privateAPI.h`, `*Private` pimpls) are internal. Lib's user-facing exports match the public headers exactly — no hidden public API.

## 1. Callbacks / event model

**None.** Zero callback typedefs, zero `std::function`, zero observer/listener/signal types in any public header. The only `enumDevicesCallback` symbol in `.lib` is a static internal DirectInput enumerator. Every state read is **polled** via `get*(ERRORCODE&)`. No connect/disconnect event. A shim needs no event-pump thread for the SDK surface.

## 2. PitHouse manager presence

`MOZAPitHouseExeManager` exists only inside `mozaAPI.cc` — not in any public header. Public surface only references PitHouse via:

- Error code `PITHOUSENOTREADY = 10` in `enumCode.h`.
- Comments in `switches_device.h` noting that some `SwitchesDevice::getStateInfo` data comes from PitHouse and `getStateInfoByHid` is the HID-only fallback.
- `installMozaSDK()` / `removeMozaSDK()` free functions — likely the IPC handshake / process launch.

**Implication:** the SDK talks to PitHouse internally (IPC). A drop-in replacement DLL does NOT need PitHouse running; just satisfy every public function with your own data. `installMozaSDK`/`removeMozaSDK` become no-ops in a shim.

### PitHouse's second external API (not in any SDK header)

Beyond the CoAP-on-UDP-40266 surface the `MOZA_SDK.dll` headers above
target, PitHouse also exposes a **plain CBOR-over-UDP** control protocol
on port 40288. It is **not** in the public SDK headers and there are no
C++ free functions that wrap it — third-party tools speak it directly.

Confirmed consumer: **RallySimFans launcher** (RBR) uses it to read and
write the wheelbase's steering-lock degrees per car. Source:
`rsf_launcher/RSFControlConfig/RSFControlConfig.dll` →
`ControlConfig.SteeringWheels.Moza_Generic` (decompiled 2026-05-23).
Other wheel-config tools (those that don't link the SDK DLL) are
expected to use the same surface for additional PacketIds we haven't
catalogued yet.

Wire format and full PacketId catalog: see
[`../protocol/pithouse-udp/README.md`](../protocol/pithouse-udp/README.md).
Plugin-side implementation: [`../../Sdk/PitHouseUdp/`](../../Sdk/PitHouseUdp/).
The CoAP server and this UDP control server both target the same
`HardwareApplier` commands (`base-limit`, `base-max-angle`, …) so the
wheelbase EEPROM stays consistent regardless of which protocol a given
client chose.

A drop-in SDK replacement that **only** services the CoAP surface
covers iRacing but **not** RSF / RBR. Full PitHouse coverage requires
both servers.

## 3. Inventory by group

Legend: `[PC]` = PLUGIN-COVERED, `[PP]` = PLUGIN-PARTIAL, `[G]` = GAP.

### 3.1 Library lifecycle / enumeration

| Sig | Purpose | Cov |
|---|---|---|
| `void installMozaSDK()` | Init SDK / connect to PitHouse IPC | `[PP]` plugin owns wheel but no IPC server |
| `void removeMozaSDK()` | Tear down | `[PP]` |
| `const char* getDeviceParent(PRODUCTTYPE, ERRORCODE&)` | Device instance name string for enum; empty = not connected | `[PC]` hub-probe |
| `std::vector<SwitchesDevice> enumSwitchesDevices(ERRORCODE&)` | Enumerate Switches devices | `[G]` |
| `std::vector<ShifterDevice> enumShifterDevices(ERRORCODE&)` | Enumerate H-pattern shifters | `[PP]` HID-visible only |

`PRODUCTTYPE`: WHEELBASE, STEERINGWHEEL, DISPLAYSCREEN, PEDALS, METER, ADAPTER, HANDBRAKE, GEARSHIFTER, UNKNOWDEVICE.
`ERRORCODE`: NORMAL, NOINSTALLSDK, NODEVICES, OUTOFRANGE, PARAMETERERR, COLLECTIONCYCLEDATALOSS, CREATEFFECTERR, ENCODINGFAILED, FFBERR, FIRMWARETOOOLD, PITHOUSENOTREADY.

### 3.2 Wheelbase FFB configuration (motor settings)

All `get*(ERRORCODE&) -> int` and `set*(int) -> ERRORCODE` unless noted.

| Function | Range / Notes | Cov |
|---|---|---|
| `get/setMotorLimitAngle(limit, gameMax)` | `pair<int,int>*`; 90–2000, gameMax 90–limit | `[PC]` mech-stop / max-angle |
| `get/setMotorRoadSensitivity` | 0–10 road feel | `[PC]` |
| `get/setMotorFfbStrength` | 0–100 | `[PC]` motor strength |
| `get/setMotorLimitWheelSpeed` | 10–100 rpm cap | `[PP]` |
| `get/setMotorSpringStrength` | 0–100 mech return | `[PC]` |
| `get/setMotorNaturalDamper` | 0–100 | `[PC]` damping |
| `get/setMotorNaturalFriction` | 0–100 | `[PC]` friction |
| `get/setMotorSpeedDamping` | 0–100 | `[PC]` |
| `get/setMotorPeakTorque` | 50–100 | `[PC]` |
| `get/setMotorNaturalInertiaRatio` | 100–4000 | `[PP]` |
| `get/setMotorNaturalInertia` | 100–500 | `[PP]` |
| `get/setMotorSpeedDampingStartPoint` | 0–400 | `[PP]` |
| `get/setMotorHandsOffProtection` | 0/1 | `[G]` |
| `get/setMotorFfbReverse` | 0/1 | `[PP]` |
| `get/setMotorEqualizerAmp` | `map<string,int>*`: keys `EqualizerAmp7_5/13/22_5/39/55` (0–500), `EqualizerAmp100` (0–100) | `[PC]` equalizer |
| `ERRORCODE SoftReboot()` | Motor soft reset | `[PP]` |
| `void motorMoveTo(HWND, float angle_deg, float speed_rpm, ERRORCODE&)` | Programmatic position move | `[G]` |
| `void motorStopMove()` | Cancel motorMoveTo | `[G]` |

### 3.3 Wheelbase FFB effects (DirectInput-style)

Return `std::shared_ptr<RS21::direct_input::ET*>`. See §5 for lifecycle.

| Function | Cov |
|---|---|
| `createWheelbaseETSine(HWND, ERRORCODE&)` | `[G]` |
| `createWheelbaseETConstantForce(HWND, ERRORCODE&)` | `[G]` |
| `createWheelbaseETSpring(HWND, ERRORCODE&)` | `[G]` |
| `createWheelbaseETDamper(HWND, ERRORCODE&)` | `[G]` |
| `createWheelbaseETInertia(HWND, ERRORCODE&)` | `[G]` |
| `createWheelbaseETFriction(HWND, ERRORCODE&)` | `[G]` |
| `ERRORCODE stopForceFeedback()` | Stop all effects | `[G]` |

### 3.4 Wheelbase telemetry readout

| Function | Cov |
|---|---|
| `const HIDData* getHIDData(ERRORCODE&)` | Latest cached HID frame | `[PC]` |

`HIDData` (from `hid_struct.h`): floats `fSteeringWheelAngle`, `fSteeringWheelVelocity` (>= fw 1.2.4), `fSteeringWheelAcceleration`; int16 `steeringWheelAxle`, `clutchSynthesisShaft`, `clutchIndependentShaftL/R`, `throttle`, `clutch`, `brake`, `handbrake`; `HIDButton[128]`; 2 `HIDRocker`; 2 `HIDKnob`; 5 `HIDMultiSegmentKnob`; `GEAR shift`; `bool buttonHandbrake`.

NB: **no motor temperature / current / voltage / raw motor params on public surface** — `[G]` despite the plugin having `base-motor-temp`. The SDK simply never exposes this.

### 3.5 Steering wheel (LEDs, paddles, screen)

| Get/Set pair | Range | Cov |
|---|---|---|
| `CenterWheel()` | one-shot align | `[PP]` |
| `SteeringWheelShiftIndicatorBrightness` | 0–100 | `[PC]` |
| `SteeringWheelClutchPaddleAxisMode` | 1–3 | `[PP]` |
| `SteeringWheelClutchPaddleCombinePos` | 0–100 | `[PP]` |
| `SteeringWheelKnobMode` | 0/1 | `[PP]` |
| `SteeringWheelJoystickHatswitchMode` | 0/1 | `[PP]` |
| `SteeringWheelShiftIndicatorSwitch` | 1–3 | `[PC]` |
| `SteeringWheelShiftIndicatorMode` | 0/1 | `[PC]` |
| `SteeringWheelShiftIndicatorColor` | `vector<string>*` per-LED | `[PC]` |
| `SteeringWheelShiftIndicatorLightRpm` | `vector<int>*` thresholds | `[PC]` |
| `SteeringWheelSpeedUnit` | 0/1 | `[PP]` |
| `SteeringWheelTemperatureUnit` | 0/1 | `[PP]` |
| `SteeringWheelScreenBrightness` | 0–100 | `[PC]` |
| `SteeringWheelScreenUIList` | `map<int,string>*` | `[PP]` plugin uploads .mzdash but no list surface |
| `SteeringWheelScreenCurrentUI` | int id | `[PP]` |

### 3.6 Display screen (standalone)

`get/setDisplayScreenSpeedUnit` (0/1 metric/imperial), `…TemperatureUnit` (0/1 C/F), `…ScreenBrightness` (0–100), `…ScreenUIList` (`map<int,string>*`), `…ScreenCurrentUI` (int). All `[PP]`.

### 3.7 Pedals

| Function | Cov |
|---|---|
| `get/setPedalClutchOutDir, BrakeOutDir, AccOutDir` (0/1) | `[PC]` |
| `get/setPedalBrakePressCombine` (0–100) | `[PC]` |
| `get/setPedalClutchNonLinear, AccNonLinear, BrakeNonLinear` (`vector<int>*`) | `[PC]` |
| `ClutchCalibrateStrat / ClutchCalibrateFinish` *(sic)* | `[PC]` |
| `AccCalibrateStrat / AccCalibrateFinish` | `[PC]` |
| `BrakeCalibrateStrat / BrakeCalibrateFinish` | `[PC]` |

Pedal positions read via `HIDData` — `[PC]`. **No mBooster motor controls in public surface** — `[G]` (plugin has them via separate wire, but SDK never surfaces them).

### 3.8 Handbrake

`get/setHandbrakeOutDir` (0/1), `get/setHandbrakeApplicationMode` (0/1), `get/setHandbrakeNonLinear` (`vector<int>*`), `HandbrakeCalibrateStart / HandbrakeCalibrateFinish`. All `[PC]`. Position via `HIDData.handbrake`.

### 3.9 H-pattern shifter (not AB9)

Free functions (auto-blip downshift): `get/setHandingShifterAutoBlipOutput` (0–100), `…AutoBlipDuration` (0–1000 ms), `…AutoBlipSwitch` (0/1), `ShifterCalibrateStart / ShifterCalibrateFinish`. All `[G]`.

Class `moza::ShifterDevice : HidDevice`:
- `int getCurrentGear() const` — blocking; -1 reverse, 0 neutral, 1–7 forward. `[PP]`.

### 3.10 Switches device

Class `moza::SwitchesDevice : HidDevice`:
- ctors (default + `(const std::string& path)`), move ctor/assign.
- `bool open() override`, `void close() override`.
- `bool isRotarySwitchStateReady() const`.
- `std::vector<uint8_t> getStateInfo(ERRORCODE&)` — partly PitHouse-sourced.
- `std::vector<uint8_t> getStateInfoByHid() const` — HID-only fallback.

`SwitchesIndex` (28 entries, MAX_SWITCHES_INDEX=28): Headlight Off/Park/High, HighBeam, Flasher Off/On, FogLight, TurnRight/Off/Left, RearWiper Off/Spray/Wash, WiperSensitivity 1–5, FrontWiperWash, FrontWiper Single/Off/Interval/Low/High, Cruise OnOff/Decrease/Increase/Cancel. Six rotary groups in `SWITCHES_GROUPS[]`. All `[G]`.

### 3.11 HID-direct read (base class)

`moza::HidDevice` (pimpl, move-only): ctor default and `(const std::string& path)`, virtual dtor; `path()`/`setPath()`; `isOpen() const`; `isConnected() const` (last-op errno); `virtual bool open()`; `virtual void close()`. Protected: `getReportSize`, `getNumInputReports`, `read(size_t)`, `readLatestReport()`.

`hid_struct.h` enums: `ROCKERMODE`, `KNOBMODE`, `CLUTCHPICKMODE`, `ROCKEREDIR` (8-way + NONEDIR), `GEAR`. Structs: `HIDButton` (with helper methods `isPressed/lastPressState/pressNum`), `HIDRocker`, `HIDKnob`, `HIDMultiSegmentKnob`. All `[PC]`.

### 3.12 Exception types

`RS21::direct_input::EffectException : std::exception` (string), `WinDirectInputApiException : std::exception` (carries HRESULT). Effects throw these. `[G]` — shim must reproduce.

### 3.13 Not exposed in public surface

No version-query function. No connection callback. No motor temperature/voltage/current. No `.mzdash` upload. No AB9 / mBooster. No LED panel beyond shift indicator on wheel. (Plugin has all of these; for a shim DLL they aren't required.)

## 4. Top 5 hardest-to-emulate surfaces

1. **DirectInput FFB effects (§3.3, §5)** — entire DI8 effect lifecycle. Each `ET*` overrides `downloadToDevice(LPDIRECTINPUTDEVICE8)`. Plugin has zero wire understanding of how the wheel firmware consumes DI commands; can't translate DI parameters to CDC frames without reverse-engineering the firmware's DI mapping.
2. **`createWheelbaseET*(HWND)`** — they take an `HWND` (DI requires cooperative-level acquisition with a window). Returns live `shared_ptr<Effect>` tracked in `Device::getCreatedEffects` (visible in lib). A shim must own a real `IDirectInputDevice8`, or fake the entire DI surface.
3. **`SwitchesDevice::getStateInfo`** — half the data comes from PitHouse over unknown IPC. `getStateInfoByHid` exposes the byte-array format (28-byte indexed by `SwitchesIndex`) so the HID side is in principle reverse-engineerable, but full rotary state needs PitHouse impersonation. `[G]` end-to-end.
4. **`getMotorEqualizerAmp` map shape** — string keys (`EqualizerAmp7_5/13/22_5/39/55` + `EqualizerAmp100`) with non-uniform ranges (first five 0–500, last 0–100). Shim must match keys exactly; mismatch returns empty map silently.
5. **`installMozaSDK` / `removeMozaSDK` + PitHouse handshake** — public symbols are void/void but internally launch / IPC with PitHouse.exe. A shim should make these no-ops; for the few consumers that ship a bundled SDK and expect PitHouse-only side-channel data, return `PITHOUSENOTREADY` from select calls — every getter is designed to expect that error.

## 5. Effects lifecycle — detail

`Effect` is the abstract base for `ETSine`, `ETConstantForce`, `ETSpring`, `ETDamper`, `ETInertia`, `ETFriction`. Header says "abstract class, but has no virtual functions" — abstractness is enforced by `virtual void downloadToDevice(LPDIRECTINPUTDEVICE8) = 0` (the only pure-virtual). Ctor `Effect(Device*)` is protected; user never calls it directly.

**Lifecycle:**

1. **Create**: call free function `createWheelbaseET<Kind>(HWND, ERRORCODE&)` → `shared_ptr<ET<Kind>>`. Internally invokes `Device::createET<Kind>()` (visible in lib exports). New effect appended to internal `list<shared_ptr<Effect>>`.
2. **Parameterize** — shared base setters (all `unsigned long` unless noted):
   - `setAttackLevel/attackLevel` (default 0), `setAttackTime/attackTime` (ms, default 500), `setFadeLevel/fadeLevel` (default 0), `setFadeTime/fadeTime` (ms, default 1000), `setDuration/duration` (ms, default 2000), `setSamplePeriod/samplePeriod` (ms, default 0), `setGain/gain` (0–10000, default `DI_FFNOMINALMAX`), `setTriggerButton/triggerButton` (default `DIEB_NOTRIGGER`), `setTriggerRepeatInterval/triggerRepeatInterval` (default 0), `setXDirection(long)/xDirection()` (degrees, default 1), `rgdAxesCount()` / `unsigned long* rgdAxes()` / `setRgdAxes(unsigned long*, size)`, `setEffectName(string)/effectName() const`, `setIndex(unsigned int)/index() const`, public field `bool m_isRunning`.
   - Condition-style (`ETSpring`, `ETDamper`, `ETFriction`, `ETInertia` — **identical** surface, all backed by `DICONDITION`): `offset/setOffset` (long −10000..10000), `positiveCoefficient`, `negativeCoefficient` (long −10000..10000), `positiveSaturation`, `negativeSaturation` (unsigned long 0..10000), `deadBand` (long 0..10000).
   - `ETConstantForce` (backed by `DICONSTANTFORCE`): `magnitude/setMagnitude(long)`.
   - `ETSine` (backed by `DIPERIODIC`): `magnitude/setMagnitude(unsigned long)`, `offset/setOffset(long)`, `phase/setPhase(unsigned long)`, `period/setPeriod(unsigned long)`.
3. **Start**: `Effect::start()` — downloads to DI device via virtual `downloadToDevice` on first call, sets `m_isRunning=true`.
4. **Stop**: `Effect::stop()`. Setters can be called while stopped; mid-running setter behavior not documented.
5. **Destroy**: drop the `shared_ptr`. `Effect::~Effect` is protected — "lifecycle managed by device". Lib exports `?deleteEffect@Device@…@@QEAA_NI@Z` (`Device::deleteEffect(unsigned int)`), called via the shared_ptr's custom deleter.

Global helper: `stopForceFeedback()` stops all effects at the wheelbase level.

**Key facts for a shim:**
- No user-visible effect handle table. Identity = the `shared_ptr` (or `index()` for debugging).
- The internal `Device` (in `RS21::direct_input::DevicesManager`) is hidden — shim need only fabricate effect objects whose setters cache state and whose `start/stop` translate to your transport (plugin wire protocol, or passthrough to Windows DI on a different virtual joystick).
- The four condition-style effects are surface-identical — one wrapper struct suffices internally.

---

**Investigation summary:**
- Public API is **~100 free functions + 3 device classes + 6 effect classes**; ~120 mangled exports in `.lib` match the headers exactly with no hidden APIs.
- **Pure polling, zero callbacks** — no event-pump thread needed.
- `MOZAPitHouseExeManager` is internal-only (defined in `.cc`, not headers); shim does not need to expose it.
- The plugin already covers ~70% of the non-effects surface; the **DirectInput FFB effects family is the single dominant gap** (all 7 effect-related functions are `[G]`).
- Secondary gaps: Switches device (28-element state vector, half-from-PitHouse), `motorMoveTo` (programmatic positioning), H-shifter auto-blip, hands-off-protection toggle.

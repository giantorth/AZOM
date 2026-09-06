# MOZA Dashboard Studio — command-line integration

Research date: 2026-09-05
Observed against: MOZA Pit House install at `C:\Program Files (x86)\MOZA Pit House`,
`bin\MOZA Dashboard Studio.exe` **1.0.6.14**

MOZA's dashboard editor ships as a **standalone Qt/QML executable inside the PitHouse
install**, not as a view inside PitHouse itself. PitHouse drives it entirely over the
command line, which is what lets this plugin drive it too — see
[`UI/DashboardStudioLauncher.cs`](../UI/DashboardStudioLauncher.cs) and the Files tab in
[`Devices/Ui/DashboardFilesControl.xaml.cs`](../Devices/Ui/DashboardFilesControl.xaml.cs).

Everything below was captured live by polling `Win32_Process.CommandLine` while driving
PitHouse's own UI, and cross-checked against string tables in the two executables.

## Layout

```
C:\Program Files (x86)\MOZA Pit House\
  MOZA Pit House.exe            ← launcher shim
  bin\
    MOZA Pit House.exe          ← the real app
    MOZA Dashboard Studio.exe   ← the editor
    DashboardFonts\
```

`HKCU\Software\MOZA\PitHouse` → `path` holds the PitHouse exe path. **The plugin's own
`Sdk/CoapStubManager` deliberately hijacks that value** to point at its CoAP impersonation
stub while SDK emulation runs, saving the original to
`%LOCALAPPDATA%\SimHub\MozaPlugin\CoapStub\registry-backup.path`. Any discovery code must
read the backup first and reject a live value that resolves inside the stub directory, or it
will "find" our own stub instead of PitHouse.

## Dashboard storage

```
%LOCALAPPDATA%\MOZA Pit House\_dashes\
  dashes\                        ← Studio's editable project set (its projectRoot)
    <Name>\<Name>.mzdash
    <Name>\1.png, 2.png          ← preview images
  images\
    MD5\<32-hex>.png             ← shared widget image pool
  <24-hex wheel MCU UID>\        ← the copy PitHouse SYNCS TO that wheel
    <Name>\<Name>.mzdash
```

**Widget images live in the shared pool, not beside the dashboard.** A dashboard authored in
Studio references `MD5/<32hex>.png`, and that file resolves against `imageRoot`
(`_dashes\images\MD5\`) — Studio never copies it into the project folder, so a Studio project
directory typically contains *only* `<Name>.mzdash`. This differs from a dashboard **downloaded
from the wheel** or exported by PitHouse, which carries its own `Resource\MD5\` subtree. The
uploader has to search both (`DashboardUploader.ImageSearchDirectories`); searching only the
per-dashboard `Resource\MD5\` silently drops every custom image from the bundle and the widget
renders blank on the wheel.

Studio's roots come from `%LOCALAPPDATA%\MOZA Pit House\settings.ini`:

```ini
[DashboardStudio]
projectRoot=C:\\Users\\me\\AppData\\Local/MOZA Pit House/_dashes/dashes
imageRoot=C:\\Users\\me\\AppData\\Local/MOZA Pit House/_dashes/images
fontRoot=C:/Program Files (x86)/MOZA Pit House/bin/DashboardFonts
```

Note the format: Qt writes **doubled backslashes and mixed separators in the same value**.
Collapse `\\` → `\` *before* substituting `/` → `\`, or the slash pass turns every forward
slash into a second backslash and the collapse then eats real separators.

`projectRoot` governs only where Studio's **project browser lists** and where it **saves a
newly created dashboard**. It does *not* constrain what Studio can open — see below.

**Neither tree is a superset of the other**, which is why the plugin's dashboard library reads
both. Studio authors into `projectRoot`, so a dashboard you just created exists only there;
PitHouse's per-wheel tree holds whatever was synced to that specific wheel, including
dashboards that were never in the local project set. `MozaPlugin.ReloadDashboardLibrary()` is
the single entry point that composes the list and hands it to
`DashboardCache.LoadFromFolders(...)`, which scans in order with **later folders winning** on
a duplicate profile name — the user's configured folder is scanned last, so it wins. The
Files tab's folder line reports every folder actually scanned, so a dashboard appearing from
a path the user never configured isn't baffling.

## Command line

Studio has exactly **two flags**, plus a positional path.

### 1. Open a dashboard for editing — bare positional argument

```
"…\bin\MOZA Dashboard Studio.exe" "C:/…/_dashes/dashes/ETS2-ATS/ETS2-ATS.mzdash"
"…\bin\MOZA Dashboard Studio.exe" "C:/…/_dashes/8ae5d…08/radarrr/radarrr.mzdash"
```

Absolute path, **forward slashes**, quoted, no flag. The second capture is the load-bearing
one: `radarrr` exists only in the per-wheel synced folder, **not** under `projectRoot` — so
**the path is arbitrary and Studio opens any `.mzdash` anywhere on disk**. No folder
reconciliation is needed to offer an "edit this dashboard" button.

### 2. Create a new dashboard — `--create-by-idealDeviceInfos <json>`

```
"…\bin\MOZA Dashboard Studio.exe" --create-by-idealDeviceInfos ^
  "[{\"hardwareVersion\":\"RS21-W08-HW SM-DU-V14\",\"productType\":\"Display\",\"networkId\":1,\"deviceId\":8}]"
```

Seeds the new-dashboard dialog with the target display's geometry. That exact literal is
embedded in the exe as its built-in default — **don't reuse it as a fallback**, it describes
one specific wheel's screen and would give a different wheel the wrong canvas with no way to
fix it afterwards; launch unseeded instead.

The same shape arrives from the wheel's own configJson blob, parsed into
`WheelDashboardDeviceInfo` (`Telemetry/Dashboard/WheelDashboardState.cs`) on
`WheelDashboardEntry.IdealDeviceInfos`, so the plugin can seed a project for the actually
connected display without inventing anything. Studio persists the last set to
`%LOCALAPPDATA%\MOZA Dashboard Studio\editor\uila.cfg` under `window.idealDeviceInfos`.
Each mzdash also stores its own `idealDeviceInfos` inside the file, and
`idealDeviceInfoMap.json` (a Qt resource inside the exe) maps a `hardwareVersion` to its
canvas geometry.

### 3. Regenerate preview screenshots — `--update-preview-image <path>`

**This is how PitHouse generates the dashboard thumbnails it shows in its own library.**

```
".\MOZA Dashboard Studio.exe"  --update-preview-image ^
  "C:/Users/me/AppData/Local/MOZA Pit House/_dashes/8ae5d…08/radarrr/radarrr.mzdash"
```

Observed behaviour, captured at PitHouse startup:

- PitHouse spawns Studio **headlessly, per dashboard**, with the working directory set to
  `bin\` (hence the `.\` relative exe path). Several instances run **concurrently** — two
  were live at once in the capture — so this mode is not subject to the editor's
  single-instance lock.
- Studio loads the mzdash into an **offscreen QQuickWindow** (`OffScreenWindow`,
  `OffScreenRenderer`, `QOpenGLFramebufferObject`), renders it, and writes the result next
  to the source file as `<dir>\1.png`, `<dir>\2.png` — one per dashboard page. Its log
  (`%LOCALAPPDATA%\MOZA Dashboard Studio\editor\log_Studio.txt`) shows the round trip:

  ```
  OffScreenWindow::saveImageToLocal 334 "ETS2-ATS" reset render time: 1  screen index: 0
  Off screen render over
  ```

- Those PNGs are what `WheelDashboardEntry.PreviewImageFilePaths` refers to, and what the
  wheel receives alongside the mzdash in an upload bundle.
- Missing widget images are logged and skipped rather than failing the render:
  `QML AnimatedImage: Error Reading Animated Image File file:///…/_dashes/images/MD5/<md5>.png`.
- The process exits on its own when the render completes.

On the PitHouse side this is driven from `Sync_DashboardManager::startProcess` (for the
per-wheel synced tree) and `Local_DashboardFileManager::startProcess` (for the local
project tree); both build the same `dashId` → `--update-preview-image` → `.\MOZA Dashboard
Studio.exe` invocation and log `Failed to start process with ID` when the spawn fails.

**The plugin deliberately never passes this flag.** It is a maintenance mode with no window,
so a user pressing a button would see nothing happen; and the previews it writes are
PitHouse's concern, not the telemetry pipeline's. It is documented here because the flag is
the reason stray headless `MOZA Dashboard Studio.exe` processes appear at PitHouse startup —
which is exactly what a naïve "is Studio already running?" check would trip over.

## Other observed internals

- **Single instance for the editor UI**: `MOZA::DashboardEditor::lockProcess`. Studio also
  carries a shared-memory command channel
  (`MOZA::DashboardEditor::CommandProcessPrivate::sharedMemoryParser`,
  `CommandProcess::receive`), which is how a second launch hands a dashboard to the live
  editor rather than starting a rival window. The plugin therefore launches unconditionally,
  exactly as PitHouse does, instead of special-casing "already running".
- Qt organisation name is `somename`, so its QML cache lands in
  `%LOCALAPPDATA%\somename\MOZA Dashboard Studio\cache\qmlcache`.
- Build path in the binary: `sw-pc/rs21/dashboard-studio` — the same `rs21` product tree the
  wheel firmware's `RS21-*` hardware strings come from.
- There is **no `.mzdash` file association** registered, so shell-executing a dashboard path
  does not work; the exe must be invoked directly.

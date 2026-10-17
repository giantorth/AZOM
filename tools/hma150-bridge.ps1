<#
  SimHub -> MOZA HMA150 bridge (v14)

  Drives the HMA150 controller (COM6) in GAME mode (mode 1) exactly like MOZA
  Motion Manager does, fed live from a SimHub "Custom serial device" through a
  com0com virtual COM pair (SimHub writes COM20, this script reads COM21).
  See README.md for the protocol, SimHub setup and caveats.

  Close Motion Manager / Pithouse motion / AZOM before running (they hold COM6).
  KEEP THE E-STOP IN REACH.

  Usage:
    .\hma150_bridge_v14.ps1 -SimHubSerial COM21              # live game mode from SimHub
    .\hma150_bridge_v14.ps1 -SimHubSerial COM21 -ListenOnly  # print what SimHub sends, rig untouched
    .\hma150_bridge_v14.ps1 -GameReplay spa_game_stream.csv  # replay a captured MM stream (no game needed)
    .\hma150_bridge_v14.ps1 -Test                            # legacy manual-mode (mode 2) wiggle test

  Tuning: -PitchIntensity/-PitchSmooth, -RollIntensity/... (same % as Motion
  Manager's Motion Effects sliders), sign flips -SurgeSign/-SwaySign/-HeaveSign/
  -PitchSign/-RollSign (+1/-1), -ShockScale for the synthesized suspension.
  v14 defaults = tested on a TR160 rig, iRacing Porsche 911 GT3 Cup, 2026-10-09.
#>
param(
    [string]$ComPort      = "COM6",
    [int]   $UdpPort      = 11000,
    [double]$MaxCommand   = 0.15,     # hard clamp, fraction of full travel (1.0 = 100%)
    [double]$PitchFullScale = 7.59,   # SimHub pitch value that maps to 100%  (degrees)
    [double]$RollFullScale  = 12.04,  # SimHub roll value that maps to 100%   (degrees)
    [double]$HeaveFullScale = 75.0,   # SimHub heave value that maps to 100%  (mm)
    [int]   $SendHz       = 60,       # max setpoint rate per axis
    [int]   $ExitMode     = 0,        # mode on exit: 0 = idle, 13 = park/shutdown
    [double]$Gain         = 1.0,      # overall intensity multiplier
    [switch]$InvertPitch,
    [switch]$InvertRoll,
    [switch]$InvertHeave,
    [string]$GameReplay,               # path to a captured game-stream CSV: replay it in game mode (mode 1)
    [double]$ReplayScale  = 0.3,      # scales the motion inputs of the replay (1.0 = as recorded)
    [string]$SimHubSerial,             # virtual COM port fed by a SimHub Custom Serial Device (game mode, live)
    [switch]$ListenOnly,               # with -SimHubSerial: just print what SimHub sends, never touch the rig
    [double]$SurgeScale   = 1.0,      # SimHub surge/sway/heave are already m/s2 (verified 2026-10-09)
    [double]$SurgeSign    = 1.0,      # +1 or -1 so that braking comes out positive
    [double]$SwaySign     = 1.0,
    [double]$HeaveSign    = 1.0,
    [double]$PitchSign    = 1.0,
    [double]$RollSign     = -1.0,     # SimHub roll is opposite to MM (verified on curbs 2026-10-09)
    [double]$LiveScale    = 1.0,      # Motion Manager sends the game's physics unscaled; strength comes from the effect sliders below
    # Motion Manager "Motion Effects" (percent). Defaults = your "iRacing gt3" preset (Porsche 911 GT3 Cup)
    [double]$PitchIntensity = 28,  [double]$PitchSmooth = 90,
    [double]$RollIntensity  = 72,  [double]$RollSmooth  = 84,
    [double]$HeaveIntensity = 54,  [double]$HeaveSmooth = 80,
    [double]$AccelPitchIntensity = 53, [double]$AccelPitchSmooth = 100,
    [double]$AccelRollIntensity  = 39, [double]$AccelRollSmooth  = 100,
    [double]$RoadDetail     = 79,  [double]$RoadSmooth  = 40,
    [double]$ShockScale   = 1.0,      # extra multiplier on the synthesized suspension movement
    [switch]$Test,
    [switch]$SkipSelfCheck,
    [switch]$Quiet,
    [switch]$ShowAll          # also print the controller's fault/telemetry dumps
)

$ErrorActionPreference = "Stop"
$AXES = @{ 0 = "PITCH"; 1 = "ROLL"; 3 = "HEAVE" }

# ---------------------------------------------------------------- framing
function New-MozaFrame([byte]$group, [byte[]]$payload, [byte]$addr = 0x12) {
    [byte[]]$raw = @(0x7E, [byte]$payload.Length, $group, $addr) + $payload
    # Wire checksum: every 0x7E from index 2 counts twice (Motion Manager captures).
    $sum = 0x0D
    for ($i = 0; $i -lt $raw.Length; $i++) { $sum += $raw[$i]; if ($i -ge 2 -and $raw[$i] -eq 0x7E) { $sum += 0x7E } }
    $raw += [byte]($sum -band 0xFF)
    # escape: any 0x7E after the start byte is doubled on the wire
    $out = New-Object System.Collections.Generic.List[byte]
    $out.Add(0x7E)
    for ($i = 1; $i -lt $raw.Length; $i++) {
        $out.Add($raw[$i]); if ($raw[$i] -eq 0x7E) { $out.Add(0x7E) }
    }
    return ,$out.ToArray()
}
function Get-FloatBE([single]$v) {
    $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); return ,$b
}
function Hex([byte[]]$b) { ($b | ForEach-Object { $_.ToString("X2") }) -join " " }

# ---------------------------------------------------------------- serial I/O
$script:rx = New-Object System.Collections.Generic.List[byte]
$script:mode = @{ target = -1; current = -1; err = -1 }
$script:state = -1
$script:selfCheckOk = $false
$script:actMode = @{}          # actuator address -> last reported mode
$script:hb = [Diagnostics.Stopwatch]::StartNew()
$script:lastHb = -1000
$script:lastLog = ""

function Send-Frame([byte]$group, [byte[]]$payload, [string]$why = "") {
    $f = New-MozaFrame $group $payload
    $serial.Write($f, 0, $f.Length)
    if ($why -and -not $Quiet) { Write-Host ("  -> {0,-28} {1}" -f $why, (Hex $f)) -ForegroundColor DarkGray }
}

function Poll-Serial {
    if ($script:shPort -and $script:shPort.IsOpen) { Read-SimHub }
    $n = $serial.BytesToRead
    if ($n -gt 0) {
        $buf = New-Object byte[] $n
        $null = $serial.Read($buf, 0, $n)
        $script:rx.AddRange($buf)
    }
    while ($true) {
        $s = $script:rx.IndexOf([byte]0x7E)
        if ($s -lt 0) { $script:rx.Clear(); return }
        if ($s -gt 0) { $script:rx.RemoveRange(0, $s) }
        # unescape
        $raw = New-Object System.Collections.Generic.List[byte]; $raw.Add(0x7E)
        $i = 1; $need = 0; $broken = $false; $complete = $false
        while ($i -lt $script:rx.Count) {
            $b = $script:rx[$i]
            if ($b -eq 0x7E) {
                if ($i + 1 -ge $script:rx.Count) { break }
                if ($script:rx[$i + 1] -eq 0x7E) { $i += 2 } else { $broken = $true; break }
            } else { $i++ }
            $raw.Add($b)
            if ($raw.Count -eq 2) { $need = $raw[1] + 5 }
            if ($need -gt 0 -and $raw.Count -ge $need) { $complete = $true; break }
        }
        if ($broken) { $script:rx.RemoveRange(0, $i); continue }
        if (-not $complete) { return }
        $script:rx.RemoveRange(0, $i)
        Handle-Frame $raw.ToArray()
    }
}

function Handle-Frame([byte[]]$f) {
    $len = $f[1]; $group = $f[2]; $addr = $f[3]
    if ($len -eq 0) { return }
    [byte[]]$p = New-Object byte[] $len
    [Array]::Copy($f, 4, $p, 0, $len)
    $sum = 0x0D; for ($k = 0; $k -lt $f.Length - 1; $k++) { $sum += $f[$k]; if ($k -ge 2 -and $f[$k] -eq 0x7E) { $sum += 0x7E } }
    if ((($sum -band 0xFF)) -ne $f[$f.Length - 1]) { return }

    # device debug log text: group 0x0E, cmd 0x05
    if ($group -eq 0x0E -and $p[0] -eq 0x05) {
        $txt = [System.Text.Encoding]::ASCII.GetString([byte[]]$p, 1, $p.Length - 1).Trim()
        if ($txt -match "selfCheckSuccess") { $script:selfCheckOk = $true }
        if ($txt -match "Actuator Set curr mode:(\d+)") { $script:actMode[$addr] = [int]$Matches[1] }
        $who = "CTRL"; if ($addr -ne 0x21) { $who = "ACT" + ("{0:X2}" -f $addr) }
        $noise = $txt -match 'I_total|^V=|mos_t|Fault|is_connected|access_dev_num|^Slave\(|^\[\d|^[\d\.,=\-]|^P\d'
        if ($ShowAll -or ((-not $noise) -and (-not $Quiet -or $txt -match "ERRO|mode|selfCheck"))) {
            $color = "Cyan"; if ($txt -match "ERRO") { $color = "Red" }
            Write-Host ("  [{0}] {1}" -f $who, $txt) -ForegroundColor $color
        }
        return
    }
    # controller event (e.g. error raised/cleared) -> acknowledge like Pithouse does
    if ($group -eq 0x0E -and $p[0] -eq 0x03 -and $p.Length -ge 5) {
        Send-Frame 0x0E @(0x04, $p[1], $p[2], $p[4])
        if (-not $Quiet) { Write-Host ("  [EVENT] {0}  (acked)" -f (Hex $p)) -ForegroundColor DarkYellow }
        return
    }
    # mode readback: 9F 03 [target] [current] ..
    if ($group -eq 0x9F -and $p[0] -eq 0x03 -and $p.Length -ge 3) {
        $err = 0; if ($p.Length -ge 5) { $err = $p[4] }
        if ($script:mode.target -ne $p[1] -or $script:mode.current -ne $p[2] -or $script:mode.err -ne $err) {
            Write-Host ("  [MODE] target={0} current={1} err=0x{2:X2}" -f $p[1], $p[2], $err) -ForegroundColor Yellow
        }
        $script:mode.target = $p[1]; $script:mode.current = $p[2]; $script:mode.err = $err
        return
    }
    # port binding readback: 9F 02 [port] [?] [axis] ..
    if ($group -eq 0x9F -and $p[0] -eq 0x02 -and $p.Length -ge 4) {
        Write-Host ("  [PORT] index {0} -> axis {1}   ({2})" -f $p[1], $p[3], (Hex $p)) -ForegroundColor DarkCyan
        return
    }
    # 21 03 state readback
    if ($group -eq 0xA1 -and $p[0] -eq 0x03 -and $p.Length -ge 2) {
        if ($script:state -ne $p[1]) { Write-Host ("  [STATE 21/03] {0}" -f $p[1]) -ForegroundColor Yellow }
        $script:state = $p[1]
        return
    }
}

function Read-Mode { Send-Frame 0x1F @(0x03, 0, 0, 0, 0) }

# Pithouse polls mode (1F 03) and status (21 01) every 500 ms the whole time it is connected.
# The controller declares "Host Disconnected" (error 269) and drops the platform if the PC goes
# quiet for ~1 s, so we poll every 250 ms and NEVER block longer than that (use Pump, not Start-Sleep).
function Heartbeat {
    if ($script:hb.ElapsedMilliseconds - $script:lastHb -ge 250) {
        $script:lastHb = $script:hb.ElapsedMilliseconds
        Read-Mode
        Send-Frame 0x21 @(0x01)
    }
}

function Pump([int]$ms) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    do { Heartbeat; Poll-Serial; Start-Sleep -Milliseconds 10 } while ($t.ElapsedMilliseconds -lt $ms)
}

function Wait-Actuators([int]$m, [int]$timeoutMs) {
    $ok = Wait-For { $n = 0; foreach ($v in $script:actMode.Values) { if ($v -eq $m) { $n++ } }; $n -ge 4 } $timeoutMs "all 4 actuators in mode $m"
    return $ok
}

function Wait-For([scriptblock]$cond, [int]$timeoutMs, [string]$what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        Heartbeat
        Start-Sleep -Milliseconds 20
        Poll-Serial
        if (& $cond) { return $true }
    }
    Write-Host "  !! timed out waiting for $what" -ForegroundColor Red
    return $false
}

function Set-Mode([int]$m, [string]$name, [int]$timeoutMs = 3000) {
    Write-Host "Mode -> $m ($name)" -ForegroundColor Green
    Send-Frame 0x1E @(0x03, [byte]$m, 0, 0, 0) "1E 03 set mode $m"
    return (Wait-For { $script:mode.target -eq $m } $timeoutMs "mode $m")
}

function Send-Axis([int]$axis, [double]$v) {
    $v = [Math]::Max(-$MaxCommand, [Math]::Min($MaxCommand, $v))
    Send-Frame 0x1E (@(0x07, [byte]$axis) + (Get-FloatBE ([single]$v)))
}

function Zero-All { foreach ($a in 0, 1, 3) { Send-Axis $a 0.0; Start-Sleep -Milliseconds 15 } }


# ---------------------------------------------------------------- game mode (mode 1) replay
# Group 0x22 cmd 0x00 = Motion Manager's in-game telemetry, ~30 Hz, 13 fields:
#  0 pitch rad | 1 roll rad | 2 yaw rad | 3 yaw rate? | 4 long accel m/s2 (braking +) | 5 lat accel
#  6 vert accel | 7 speed m/s | 8..11 four shock deflections mm | 12 = bytes [throttle%][brake%][?][gear]
function Get-F([byte[]]$b, [int]$i) { $x = New-Object byte[] 4; [Array]::Copy($b, 1 + 4 * $i, $x, 0, 4); [Array]::Reverse($x); [BitConverter]::ToSingle($x, 0) }
function Set-F([byte[]]$b, [int]$i, [double]$v) { $x = Get-FloatBE ([single]$v); [Array]::Copy($x, 0, $b, 1 + 4 * $i, 4) }

function Invoke-GameReplay {
    $rows = Import-Csv $GameReplay
    $frames = foreach ($r in $rows) {
        $h = $r.payload_hex
        [byte[]]$b = for ($k = 0; $k -lt $h.Length; $k += 2) { [Convert]::ToByte($h.Substring($k, 2), 16) }
        [pscustomobject]@{ t = [double]$r.t_s; b = $b }
    }
    Write-Host ("Loaded {0} game frames ({1:F1} s). Motion scale {2}" -f $frames.Count, $frames[-1].t, $ReplayScale) -ForegroundColor Green
    # baselines: attitude from the first frame, shocks averaged over the first second
    $b0 = $frames[0].b
    $base = @{}; foreach ($i in 0, 1) { $base[$i] = Get-F $b0 $i }
    foreach ($i in 8..11) { $sum = 0.0; $n = 0; foreach ($f in $frames) { if ($f.t -gt 1.0) { break }; $sum += (Get-F $f.b $i); $n++ }; $base[$i] = $sum / [Math]::Max(1, $n) }
    function Scaled([byte[]]$src) {
        [byte[]]$b = $src.Clone()
        foreach ($i in 0, 1, 8, 9, 10, 11) { Set-F $b $i ($base[$i] + $ReplayScale * ((Get-F $src $i) - $base[$i])) }
        foreach ($i in 3, 4, 5, 6) { Set-F $b $i ($ReplayScale * (Get-F $src $i)) }
        return ,$b
    }
    # Enter game mode the way Motion Manager does: stream 22 frames and repeat 1E 03 01 until the controller is in mode 1
    Write-Host "Mode -> 1 (game): streaming idle frames until the controller switches..." -ForegroundColor Green
    $first = Scaled $b0
    $sw = [Diagnostics.Stopwatch]::StartNew(); $sentState = $false
    while ($script:mode.current -ne 1) {
        if ($sw.ElapsedMilliseconds -gt 15000) { throw ("Controller did not enter game mode (now {0}, err=0x{1:X2})" -f $script:mode.current, $script:mode.err) }
        Send-Frame 0x22 $first
        Send-Frame 0x1E @(0x03, 0x01, 0, 0, 0)
        if (-not $sentState -and $sw.ElapsedMilliseconds -gt 300) { Send-Frame 0x20 @(0x02, 0x03, 0x00) "20 02 03 00"; $sentState = $true }
        Pump 33
    }
    Write-Host "GAME MODE - replaying in 3 s (Ctrl+C or E-stop to abort)" -ForegroundColor Green
    $hold = [Diagnostics.Stopwatch]::StartNew()
    while ($hold.ElapsedMilliseconds -lt 3000) { Send-Frame 0x22 $first; Pump 33 }
    $clock = [Diagnostics.Stopwatch]::StartNew(); $lastPrint = -1000
    foreach ($f in $frames) {
        while ($clock.ElapsedMilliseconds / 1000.0 -lt $f.t) { Heartbeat; Poll-Serial; Start-Sleep -Milliseconds 2 }
        $b = Scaled $f.b
        Send-Frame 0x22 $b
        if ($clock.ElapsedMilliseconds - $lastPrint -ge 1000) {
            $lastPrint = $clock.ElapsedMilliseconds
            Write-Host ("{0,5:F1}s  speed {1,5:F1} m/s  long {2,6:F2}  lat {3,6:F2}  pitch {4,6:F3}  roll {5,6:F3}  brk {6,3} thr {7,3}  mode {8}" -f $f.t, (Get-F $b 7), (Get-F $b 4), (Get-F $b 5), (Get-F $b 0), (Get-F $b 1), $b[49], $b[50], $script:mode.current)
            if ($script:mode.current -ne 1) { throw ("Controller left game mode (now {0}, err=0x{1:X2})" -f $script:mode.current, $script:mode.err) }
        }
    }
    Write-Host "Replay finished - holding the last frame for 2 s."
    $last = Scaled $frames[-1].b
    $hold = [Diagnostics.Stopwatch]::StartNew()
    while ($hold.ElapsedMilliseconds -lt 2000) { Send-Frame 0x22 $last; Pump 33 }
}


# ---------------------------------------------------------------- SimHub -> game mode (live)
# SimHub Custom Serial Device sends one text line per update:
#   HMA,pitchDeg,rollDeg,yawDeg,yawRateDegPerS,surge,sway,heave,speedKmh,brake%,throttle%,gear
$script:shBuf = ""
$script:shLast = $null
$script:shPort = $null
$script:lastGear = -1
$script:shiftUntil = 0
$script:shClock = [Diagnostics.Stopwatch]::StartNew()
$script:shBytes = 0
$script:shCount = 0
function Read-SimHub {
    $txt = $script:shPort.ReadExisting()
    if (-not $txt) { return }
    $script:shBytes += $txt.Length
    $script:shBuf += $txt
    $lines = $script:shBuf -split "`n"
    $script:shBuf = $lines[-1]
    for ($k = $lines.Count - 2; $k -ge 0; $k--) {
        $l = $lines[$k].Trim()
        if ($l.StartsWith("HMA,")) {
            $v = $l.Substring(4).Split(",")
            if ($v.Count -ge 11) {
                $d = New-Object double[] 11
                for ($j = 0; $j -lt 10; $j++) { $x = 0.0; [void][double]::TryParse($v[$j], [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$x); $d[$j] = $x }
                $g = 0; [void][int]::TryParse($v[10].Trim(), [ref]$g); $d[10] = $g
                $script:shLast = $d; $script:shCount++
                break
            }
        }
    }
}
$SHOCK_BASE = @(34.7, 38.4, 38.7, 33.1)   # mm, from the Motion Manager capture at rest
# per field: base, long, lat, vert, speed^2   (fit to the Motion Manager Spa capture)
$SHOCK_FIT = @(
    @(34.40,  0.427,  0.555, -1.089, 0.00048),
    @(38.72, -0.203,  0.346, -0.174, 0.01362),
    @(39.25, -0.225, -1.119, -0.268, 0.01285),
    @(33.66,  0.450, -0.848, -1.158, 0.00276))
function Build-GameFrame([double[]]$d, [double]$scale) {
    $rad = [Math]::PI / 180.0
    [byte[]]$b = New-Object byte[] 53
    $f = @(
        ($PitchSign * $scale * $d[0] * $rad),
        ($RollSign * $scale * $d[1] * $rad),
        (-$d[2] * $rad),                # SimHub heading is mirrored vs Motion Manager
        ($scale * $d[3] * $rad),        # SimHub yaw rate already has MM's sign
        ($SurgeSign * $scale * $SurgeScale * $d[4]),
        ($SwaySign * $scale * $SurgeScale * $d[5]),
        ($HeaveSign * $scale * $SurgeScale * $d[6]),
        ($d[7] / 3.6),
        0, 0, 0, 0)
    # Synthesize the four suspension deflections (fields 8..11) from the accelerations, using a
    # least-squares fit to Motion Manager's own stream (Spa capture, R2 0.78-0.90):
    #   shock = base + a*long + b*lat + c*vert + d*speed^2
    $lon = $SurgeSign * $SurgeScale * $d[4]; $lat = $SwaySign * $SurgeScale * $d[5]; $ver = $HeaveSign * $SurgeScale * $d[6]; $v = $d[7] / 3.6
    for ($k = 0; $k -lt 4; $k++) {
        $c = $SHOCK_FIT[$k]
        $f[8 + $k] = $c[0] + $ShockScale * $scale * ($c[1] * $lon + $c[2] * $lat + $c[3] * $ver + $c[4] * $v * $v)
    }
    for ($i = 0; $i -lt 12; $i++) { Set-F $b $i $f[$i] }
    $b[49] = [byte][Math]::Max(0, [Math]::Min(100, [Math]::Round($d[8])))
    $b[50] = [byte][Math]::Max(0, [Math]::Min(100, [Math]::Round($d[9])))
    # Last byte is a status/flag byte, NOT the gear (Motion Manager sends 01 in every gear):
    #   bit0 = In Game Flag, bit1 = Gear Shift (pulsed briefly when the gear changes)
    $flags = 1
    if ($d[10] -ne $script:lastGear) { $script:lastGear = $d[10]; $script:shiftUntil = $script:shClock.ElapsedMilliseconds + 120 }
    if ($script:shClock.ElapsedMilliseconds -lt $script:shiftUntil) { $flags = 3 }
    $b[51] = 0
    $b[52] = [byte]$flags
    return ,$b
}
function Open-SimHubPort {
    $script:shPort = New-Object System.IO.Ports.SerialPort $SimHubSerial, 115200
    $script:shPort.ReadTimeout = 10
    $script:shPort.ReadBufferSize = 65536
    $script:shPort.DtrEnable = $true
    $script:shPort.Open()
    Write-Host "Reading SimHub on $SimHubSerial" -ForegroundColor Green
}
function Invoke-SimHubListen {
    Open-SimHubPort
    $names = "pitch", "roll", "yaw", "yawrate", "surge", "sway", "heave", "kmh", "brake", "throttle", "gear"
    $mn = New-Object double[] 11; $mx = New-Object double[] 11
    for ($j = 0; $j -lt 11; $j++) { $mn[$j] = [double]::MaxValue; $mx[$j] = [double]::MinValue }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $lastPrint = 0; $lastCount = 0
    try {
        while ($true) {
            Read-SimHub
            if ($script:shLast) { for ($j = 0; $j -lt 11; $j++) { $x = $script:shLast[$j]; if ($x -lt $mn[$j]) { $mn[$j] = $x }; if ($x -gt $mx[$j]) { $mx[$j] = $x } } }
            if ($sw.ElapsedMilliseconds - $lastPrint -ge 1000) {
                $rate = $script:shCount - $lastCount; $lastCount = $script:shCount; $lastPrint = $sw.ElapsedMilliseconds
                if ($script:shLast) {
                    $d = $script:shLast
                    Write-Host ("{0,3}/s  pitch {1,6:F2}  roll {2,6:F2}  yaw {3,7:F1}  yawrate {4,6:F2}  surge {5,6:F2}  sway {6,6:F2}  heave {7,6:F2}  {8,5:F0} km/h  brk {9,3:F0} thr {10,3:F0}  gear {11}" -f $rate, $d[0], $d[1], $d[2], $d[3], $d[4], $d[5], $d[6], $d[7], $d[8], $d[9], $d[10])
                } else { Write-Host "no HMA lines yet ($rate/s)" }
            }
            Start-Sleep -Milliseconds 5
        }
    } finally {
        $script:shPort.Close()
        Write-Host ""; Write-Host "Session ranges:"
        for ($j = 0; $j -lt 11; $j++) { if ($mn[$j] -le $mx[$j]) { Write-Host ("  {0,-9} {1,10:F3} .. {2,10:F3}" -f $names[$j], $mn[$j], $mx[$j]) } }
    }
}
function Invoke-SimHubGame {
    $idle = Build-GameFrame (New-Object double[] 11) $LiveScale; $idle[52] = 1
    Write-Host "Mode -> 1 (game) ..." -ForegroundColor Green
    $sw = [Diagnostics.Stopwatch]::StartNew(); $sentState = $false
    while ($script:mode.current -ne 1) {
        if ($sw.ElapsedMilliseconds -gt 15000) { throw ("Controller did not enter game mode (now {0}, err=0x{1:X2})" -f $script:mode.current, $script:mode.err) }
        Send-Frame 0x22 $idle; Send-Frame 0x1E @(0x03, 0x01, 0, 0, 0)
        if (-not $sentState -and $sw.ElapsedMilliseconds -gt 300) { Send-Frame 0x20 @(0x02, 0x03, 0x00) "20 02 03 00"; $sentState = $true }
        Pump 33
    }
    Write-Host "GAME MODE - live from SimHub (scale $LiveScale). Ctrl+C to stop." -ForegroundColor Green
    $clock = [Diagnostics.Stopwatch]::StartNew(); $next = 0; $lastPrint = 0; $lastData = 0; $lastCount = 0
    while ($true) {
        Read-SimHub
        if ($script:shCount -ne $lastCount) { $lastCount = $script:shCount; $lastData = $clock.ElapsedMilliseconds }
        if ($clock.ElapsedMilliseconds -ge $next) {
            $next = $clock.ElapsedMilliseconds + 33
            $stale = ($clock.ElapsedMilliseconds - $lastData) -gt 500 -or -not $script:shLast
            $b = $(if ($stale) { $idle } else { Build-GameFrame $script:shLast $LiveScale })
            Send-Frame 0x22 $b
        }
        Heartbeat; Poll-Serial
        if ($clock.ElapsedMilliseconds - $lastPrint -ge 1000) {
            $lastPrint = $clock.ElapsedMilliseconds
            $b = $(if ($script:shLast) { Build-GameFrame $script:shLast $LiveScale } else { $idle })
            Write-Host ("speed {0,5:F1} m/s  long {1,6:F2}  lat {2,6:F2}  vert {3,6:F2}  pitch {4,6:F3}  roll {5,6:F3}  susp F {13,4:F1}/{14,4:F1} R {15,4:F1}/{16,4:F1}  brk {6,3} thr {7,3} flags {8}  mode {9}  simhub {11} lines / {12} bytes{10}" -f (Get-F $b 7), (Get-F $b 4), (Get-F $b 5), (Get-F $b 6), (Get-F $b 0), (Get-F $b 1), $b[49], $b[50], $b[52], $script:mode.current, $(if (($clock.ElapsedMilliseconds - $lastData) -gt 500) { "  (no SimHub data - idle)" } else { "" }), $script:shCount, $script:shBytes, (Get-F $b 8), (Get-F $b 11), (Get-F $b 9), (Get-F $b 10))
            if ($script:mode.current -ne 1) { throw ("Controller left game mode (now {0}, err=0x{1:X2})" -f $script:mode.current, $script:mode.err) }
        }
        Start-Sleep -Milliseconds 2
    }
}

# ---------------------------------------------------------------- main
Write-Host "==============================================="
Write-Host " SimHub -> HMA150 bridge v14"
Write-Host " Serial $ComPort   UDP $UdpPort   clamp +/-$MaxCommand"
Write-Host " E-STOP WITHIN REACH. Ctrl+C to stop."
Write-Host "==============================================="

if ($SimHubSerial -and $ListenOnly) { Invoke-SimHubListen; return }
$serial = New-Object System.IO.Ports.SerialPort $ComPort, 115200, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
$serial.Handshake = [System.IO.Ports.Handshake]::None
$serial.DtrEnable = $true
$serial.RtsEnable = $true
$serial.ReadTimeout = 50
$serial.WriteTimeout = 500
$serial.Open()
Write-Host "$ComPort open."
if ($SimHubSerial) { Open-SimHubPort }
$udp = $null

try {
    Pump 300
    $null = Wait-For { $script:mode.current -ge 0 } 2000 "first mode readback"
    Write-Host ("Controller currently in mode {0} (target {1})" -f $script:mode.current, $script:mode.target)

    # Replay what Motion Manager sends when it connects (from your Coldstart capture):
    # port-binding reads, per-axis settings (1E 05 [axis] A B), 1E 06, 20 02 01 00, 1E 0A 01.
    Write-Host "Sending Motion Manager connect sequence..." -ForegroundColor Green
    foreach ($i in 1..8) { Send-Frame 0x1F @(0x02, [byte]$i, 0, 0, 0) }
    # 1E 05 [slot] [intensity] [smoothness] = Motion Manager's "Motion Effects" sliders (decoded 2026-10-09):
    #   slot 00 Pitch, 01 Roll, 03 Heave, 07 Acceleration Pitch, 08 Acceleration Roll
    #   1E 06 [intensity] [smoothness] = Road Detail.  Sent the way Motion Manager does: zeros, then A, then A+B.
    function FHex([double]$pct) { ((Get-FloatBE ([single]($pct / 100.0))) | ForEach-Object { $_.ToString("x2") }) -join " " }
    $fx = @(
        @("00", $PitchIntensity, $PitchSmooth), @("01", $RollIntensity, $RollSmooth), @("03", $HeaveIntensity, $HeaveSmooth),
        @("07", $AccelPitchIntensity, $AccelPitchSmooth), @("08", $AccelRollIntensity, $AccelRollSmooth))
    $cfg = New-Object System.Collections.Generic.List[string]
    foreach ($e in $fx) {
        $cfg.Add("1e 05 $($e[0]) 00 00 00 00 00 00 00 00")
        $cfg.Add("1e 05 $($e[0]) $(FHex $e[1]) 00 00 00 00")
        $cfg.Add("1e 05 $($e[0]) $(FHex $e[1]) $(FHex $e[2])")
    }
    $cfg.Add("1e 06 00 00 00 00 00 00 00 00")
    $cfg.Add("1e 06 $(FHex $RoadDetail) 00 00 00 00")
    $cfg.Add("1e 06 $(FHex $RoadDetail) $(FHex $RoadSmooth)")
    $cfg.Add("20 02 01 00")
    $cfg.Add("1e 0a 01")
    Write-Host ("Motion effects: pitch {0}/{1}  roll {2}/{3}  heave {4}/{5}  accel-pitch {6}/{7}  accel-roll {8}/{9}  road {10}/{11}  (intensity/smoothness %)" -f $PitchIntensity, $PitchSmooth, $RollIntensity, $RollSmooth, $HeaveIntensity, $HeaveSmooth, $AccelPitchIntensity, $AccelPitchSmooth, $AccelRollIntensity, $AccelRollSmooth, $RoadDetail, $RoadSmooth)
    foreach ($line in $cfg) {
        [byte[]]$b = $line.Split(" ") | ForEach-Object { [Convert]::ToByte($_, 16) }
        Send-Frame $b[0] ([byte[]]$b[1..($b.Length - 1)])
        $t1 = [Diagnostics.Stopwatch]::StartNew(); while ($t1.ElapsedMilliseconds -lt 15) { Poll-Serial; Start-Sleep -Milliseconds 5 }
    }
    Send-Frame 0x21 @(0x03)
    Pump 1500

    if ($script:mode.current -ne 2) {
        if (-not $SkipSelfCheck) {
            Write-Host "Self-check: platform will move to its ready position." -ForegroundColor Magenta
            $null = Set-Mode 7 "self-check"
            if (-not (Wait-For { $script:selfCheckOk } 30000 "selfCheckSuccess")) { throw "Self-check did not report success - stopping." }
            Pump 300
        }
        if ($GameReplay) { Invoke-GameReplay; return }
        if ($SimHubSerial) { Invoke-SimHubGame; return }
        $null = Set-Mode 0 "idle"
        Send-Frame 0x20 @(0x02, 0x02, 0x00) "20 02 02 00"
        Send-Frame 0x21 @(0x03)
        $null = Set-Mode 3 "run"
        # The actuators follow the controller into mode 3 a few seconds later. Asking for
        # manual mode before they get there makes the controller fail (ErrCode 32) and drop to 0.
        Write-Host "Waiting for the actuators to settle in run mode..."
        if (-not (Wait-Actuators 3 10000)) { throw "Actuators did not reach run mode." }
        Pump 1000
        if (-not (Set-Mode 2 "manual input")) { throw "Controller did not enter manual mode." }
    }
    Zero-All
    Write-Host "READY - manual input mode." -ForegroundColor Green

    $period = [int](1000 / $SendHz)
    $lastSent = @{ 0 = [double]::NaN; 1 = [double]::NaN; 3 = [double]::NaN }
    $lastTime = @{ 0 = 0; 1 = 0; 3 = 0 }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastModePoll = 0; $lastUdp = 0; $zeroed = $true

    if ($Test) {
        # 3 slow cycles per axis at 30% of the clamp. Heave, then pitch, then roll.
        $amp = 0.3 * $MaxCommand
        foreach ($axis in 3, 0, 1) {
            Write-Host ("TEST {0}: +/-{1:P0} of travel, 0.25 Hz" -f $AXES[$axis], $amp)
            $t0 = $clock.ElapsedMilliseconds
            while ($clock.ElapsedMilliseconds - $t0 -lt 12000) {
                $t = ($clock.ElapsedMilliseconds - $t0) / 1000.0
                Send-Axis $axis ($amp * [Math]::Sin(2 * [Math]::PI * 0.25 * $t))
                Heartbeat
                Poll-Serial
                Start-Sleep -Milliseconds $period
            }
            Send-Axis $axis 0.0; Pump 1500
            if ($script:mode.current -ne 2) { throw ("Controller left manual mode (now {0}, err=0x{1:X2})" -f $script:mode.current, $script:mode.err) }
        }
        Write-Host "Test done."
        return
    }

    $udp = New-Object System.Net.Sockets.UdpClient($UdpPort)
    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    Write-Host "Listening for SimHub on UDP $UdpPort ..."

    $sign = @{ 0 = 1.0; 1 = 1.0; 3 = 1.0 }
    if ($InvertPitch) { $sign[0] = -1.0 }; if ($InvertRoll) { $sign[1] = -1.0 }; if ($InvertHeave) { $sign[3] = -1.0 }
    $fullScale = @{ 0 = $PitchFullScale; 1 = $RollFullScale; 3 = $HeaveFullScale }
    $latest = @{}
    $sum = @{}; foreach ($a in 0, 1, 3) { $sum[$a] = @{ min = [double]::MaxValue; max = [double]::MinValue; clip = 0; n = 0 } }
    $lastSummary = 0
    Write-Host ("Scaling: pitch {0} deg, roll {1} deg, heave {2} mm = 100%   gain {3}   clamp +/-{4}" -f $PitchFullScale, $RollFullScale, $HeaveFullScale, $Gain, $MaxCommand)

    while ($true) {
        $now = $clock.ElapsedMilliseconds
        while ($udp.Available -gt 0) {
            $data = $udp.Receive([ref]$remote)
            $lastUdp = $now; $zeroed = $false
            if ($data.Length -ne 5) { continue }
            # SimHub packet: [axis][float32 big-endian]  pitch/roll in degrees, heave in mm
            $a = [int]$data[0]
            if (-not $fullScale.ContainsKey($a)) { continue }
            [byte[]]$fb = $data[1..4]; [Array]::Reverse($fb)
            $latest[$a] = [double][BitConverter]::ToSingle($fb, 0)
        }
        foreach ($a in @($latest.Keys)) {
            if (($now - $lastTime[$a]) -lt $period) { continue }
            $raw = $latest[$a]
            $want = $sign[$a] * $Gain * $raw / $fullScale[$a]
            $v = [Math]::Max(-$MaxCommand, [Math]::Min($MaxCommand, $want))
            $st = $sum[$a]; $st.n++; if ($raw -lt $st.min) { $st.min = $raw }; if ($raw -gt $st.max) { $st.max = $raw }
            if ($v -ne $want) { $st.clip++ }
            if ([double]::IsNaN($lastSent[$a]) -or [Math]::Abs($v - $lastSent[$a]) -gt 0.0005) {
                Send-Axis $a $v; $lastSent[$a] = $v
            }
            $lastTime[$a] = $now
        }
        if (-not $Quiet -and ($now - $lastSummary) -ge 1000) {
            $lastSummary = $now
            $parts = foreach ($a in 0, 1, 3) {
                $st = $sum[$a]
                if ($st.n -eq 0) { "{0} --" -f $AXES[$a] }
                else {
                    $out = $lastSent[$a]; if ([double]::IsNaN($out)) { $out = 0 }
                    "{0} {1,6:F2}..{2,-6:F2} out {3,6:P0}{4}" -f $AXES[$a], $st.min, $st.max, $out, $(if ($st.clip -gt 0) { " CLIP" } else { "" })
                }
                $sum[$a] = @{ min = [double]::MaxValue; max = [double]::MinValue; clip = 0; n = 0 }
            }
            Write-Host (($parts) -join "  |  ")
        }
        # SimHub went quiet -> return to centre
        if (-not $zeroed -and ($now - $lastUdp) -gt 500) {
            Write-Host "No SimHub data for 500 ms - centring." -ForegroundColor Yellow
            Zero-All; $zeroed = $true; $latest = @{}
            foreach ($a in 0, 1, 3) { $lastSent[$a] = 0.0 }
        }
        Heartbeat
        if ($now - $lastModePoll -ge 500) {
            $lastModePoll = $now
            if ($script:mode.current -ge 0 -and $script:mode.current -ne 2 -and $script:mode.target -ne 2) {
                Write-Host ("Controller left manual mode (now {0}) - stopping." -f $script:mode.current) -ForegroundColor Red
                break
            }
        }
        Poll-Serial
        Start-Sleep -Milliseconds 2
    }
}
finally {
    Write-Host ""
    Write-Host "Stopping: centring all axes..."
    try { Zero-All; Pump 1500 } catch {}
    try { Send-Frame 0x1E @(0x03, [byte]$ExitMode, 0, 0, 0) "exit mode $ExitMode"; Pump 300 } catch {}
    if ($udp) { $udp.Close() }
    $serial.Close()
    Write-Host "$ComPort closed."
}

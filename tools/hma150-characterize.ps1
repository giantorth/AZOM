<#
  HMA150 controller characterization (Phase 0 of the SimHub Motion output).

  Puts the MOZA Motion Controller in game mode (mode 1) and drives the 0x22/00 telemetry
  frame with scripted inputs, to measure how the controller's own cueing turns each field
  into platform motion: pass-through gain, washout, bandwidth, the heave path, gating
  fields, whether the 0x21/02 port word is position feedback, and the uncaptured setup
  writes. Protocol: docs/protocol/motion/. Derived from tools/hma150-bridge.ps1.

  Close Motion Manager first (it holds the COM port). KEEP THE E-STOP IN REACH.
  Amplitudes are clamped to +/-6 deg and +/-15 mm of shock offset.

  Every frame in both directions is logged to <Log>.jsonl ({"t","dir","hex"}, readable by
  tools/motion-decode); step boundaries go to <Log>.markers.csv. Send both files back.

  Usage:
    .\hma150-characterize.ps1                      # All: Gain pitch+roll, Heave, Sweep, Gating, Feedback (~20 min)
    .\hma150-characterize.ps1 -Experiment Gain -Axis Pitch
    .\hma150-characterize.ps1 -Experiment Gain -Axis Roll -Intensities 1
    .\hma150-characterize.ps1 -Experiment Sweep -Axis Pitch -RateHz 60
    .\hma150-characterize.ps1 -Experiment Heave
    .\hma150-characterize.ps1 -Experiment Gating
    .\hma150-characterize.ps1 -Experiment Feedback
    .\hma150-characterize.ps1 -Experiment Writes -StrokeValue 0.5 -RigL1 520 -RigL2 480
#>
param(
    [string]  $ComPort      = "COM6",
    [ValidateSet("All", "Gain", "Sweep", "Heave", "Gating", "Feedback", "Writes")]
    [string]  $Experiment   = "All",
    [ValidateSet("Pitch", "Roll")]
    [string]  $Axis         = "Pitch",
    [double[]]$Intensities  = @(1.0, 0.5, 0.25),
    [double[]]$StepsDeg     = @(1, 3, 5, -1, -3, -5),
    [double]  $HoldSec      = 10,
    [double[]]$SweepHz      = @(0.5, 1, 2, 4, 8),
    [double]  $SweepAmpDeg  = 2,
    [double[]]$Smooths      = @(0.0, 0.5),
    [int]     $RateHz       = 33,
    [double]  $SpeedMs      = 0,       # f7 during experiments
    [double]  $StrokeValue  = -1,      # Writes: Max Available Stroke 0..1 (-1 = skip)
    [double]  $RigL1        = -1,      # Writes: rig length mm (-1 = skip)
    [double]  $RigL2        = -1,      # Writes: rig width mm
    [switch]  $SkipLighting,
    [switch]  $SkipSelfCheck,
    [int]     $ExitMode     = 13,
    [string]  $Log          = ("hma150-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
)

$ErrorActionPreference = "Stop"
$MaxDeg = 6.0
$MaxShockMm = 15.0
$SHOCK_REST = @(34.7, 38.4, 38.7, 33.1)   # f8..f11 = LF, LR, RR, RF at rest (Motion Manager capture)
$Inv = [Globalization.CultureInfo]::InvariantCulture

# ---------------------------------------------------------------- logging
$logStream = New-Object System.IO.StreamWriter ((Join-Path (Get-Location) "$Log.jsonl"), $false)
$markStream = New-Object System.IO.StreamWriter ((Join-Path (Get-Location) "$Log.markers.csv"), $false)
$markStream.WriteLine("t,label")
$epoch = [DateTime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
function Now-Epoch { ([DateTime]::UtcNow - $epoch).TotalSeconds }
function Hex([byte[]]$b) { ($b | ForEach-Object { $_.ToString("x2") }) -join "" }
function Log-Frame([string]$dir, [byte[]]$frame) {
    $logStream.WriteLine(('{{"t": {0}, "dir": "{1}", "hex": "{2}"}}' -f (Now-Epoch).ToString("F6", $Inv), $dir, (Hex $frame)))
}
function Mark([string]$label) {
    $markStream.WriteLine(("{0},{1}" -f (Now-Epoch).ToString("F6", $Inv), $label)); $markStream.Flush()
    Write-Host ("  [{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $label) -ForegroundColor Magenta
}

# ---------------------------------------------------------------- framing
# Unstuffed frame: 7E len group dev payload chk. Wire checksum counts every 0x7E from
# index 2 twice; any 0x7E after the start byte is doubled on the wire.
function New-Frame([byte]$group, [byte[]]$payload, [byte]$dev = 0x12) {
    [byte[]]$raw = @(0x7E, [byte]$payload.Length, $group, $dev) + $payload
    $sum = 0x0D
    for ($i = 0; $i -lt $raw.Length; $i++) { $sum += $raw[$i]; if ($i -ge 2 -and $raw[$i] -eq 0x7E) { $sum += 0x7E } }
    return ,([byte[]]($raw + [byte]($sum -band 0xFF)))
}
function Stuff([byte[]]$f) {
    $out = New-Object System.Collections.Generic.List[byte]
    $out.Add($f[0]); $out.Add($f[1])
    for ($i = 2; $i -lt $f.Length; $i++) { $out.Add($f[$i]); if ($f[$i] -eq 0x7E) { $out.Add(0x7E) } }
    return ,$out.ToArray()
}
function F32([double]$v) { $b = [BitConverter]::GetBytes([single]$v); [Array]::Reverse($b); return ,$b }
function ReadF32([byte[]]$p, [int]$off) { $x = New-Object byte[] 4; [Array]::Copy($p, $off, $x, 0, 4); [Array]::Reverse($x); [BitConverter]::ToSingle($x, 0) }

function Send([byte]$group, [byte[]]$payload, [byte]$dev = 0x12) {
    $f = New-Frame $group $payload $dev
    $w = Stuff $f
    $serial.Write($w, 0, $w.Length)
    Log-Frame "h2b" $f
}

# ---------------------------------------------------------------- receive
$script:rx = New-Object System.Collections.Generic.List[byte]
$script:mode = @{ target = -1; current = -1; flags = -1 }
$script:selfCheckOk = $false
$script:ports = @{}         # port -> present
$script:reads = @{}         # "grp/cmd/idx" -> payload
$script:logBuf = @{}

function Poll-Serial {
    $n = $serial.BytesToRead
    if ($n -gt 0) { $buf = New-Object byte[] $n; $null = $serial.Read($buf, 0, $n); $script:rx.AddRange($buf) }
    while ($true) {
        $s = $script:rx.IndexOf([byte]0x7E)
        if ($s -lt 0) { $script:rx.Clear(); return }
        if ($s -gt 0) { $script:rx.RemoveRange(0, $s) }
        $raw = New-Object System.Collections.Generic.List[byte]; $raw.Add(0x7E)
        $i = 1; $need = 0; $broken = $false; $complete = $false
        while ($i -lt $script:rx.Count) {
            $b = $script:rx[$i]
            if ($b -eq 0x7E -and $i -ge 2) {
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
        $f = $raw.ToArray()
        $sum = 0x0D; for ($k = 0; $k -lt $f.Length - 1; $k++) { $sum += $f[$k]; if ($k -ge 2 -and $f[$k] -eq 0x7E) { $sum += 0x7E } }
        if (($sum -band 0xFF) -ne $f[$f.Length - 1]) { continue }
        Log-Frame "b2h" $f
        Handle-Frame $f
    }
}

function Handle-Frame([byte[]]$f) {
    $len = $f[1]; $group = $f[2]; $dev = $f[3]
    [byte[]]$p = New-Object byte[] $len
    if ($len -gt 0) { [Array]::Copy($f, 4, $p, 0, $len) }
    if ($group -eq 0x0E -and $len -ge 1 -and $p[0] -eq 0x05) {
        $txt = $script:logBuf[$dev] + [Text.Encoding]::ASCII.GetString($p, 1, $len - 1)
        if (-not $txt.EndsWith("`n")) { $script:logBuf[$dev] = $txt; return }
        $script:logBuf[$dev] = ""
        $line = $txt.Trim()
        if ($line -match "selfCheckSuccess") { $script:selfCheckOk = $true }
        $noise = $line -match 'I_total|^V=|mos_t|Fault|is_connected|access_dev_num|^Slave\(|^\[\d|^[\d\.,=\-]|^P\d'
        if (-not $noise) {
            $color = "DarkCyan"; if ($line -match "ERRO|error_code") { $color = "Red" }
            Write-Host ("    [{0:x2}] {1}" -f $dev, $line) -ForegroundColor $color
        }
        return
    }
    if ($group -eq 0x0E -and $len -ge 5 -and $p[0] -eq 0x03) { Send 0x0E @(0x04, $p[1], $p[2], $p[4]); return }
    if ($group -eq 0x9F -and $len -ge 5 -and $p[0] -eq 0x03) {
        if ($script:mode.current -ne $p[2] -or $script:mode.flags -ne $p[4]) {
            Write-Host ("    [MODE] target={0} current={1} flags=0x{2:X2}" -f $p[1], $p[2], $p[4]) -ForegroundColor Yellow
        }
        $script:mode.target = $p[1]; $script:mode.current = $p[2]; $script:mode.flags = $p[4]
        return
    }
    if ($group -eq 0xA1 -and $len -ge 3 -and $p[0] -eq 0x02) { $script:ports[[int]$p[1]] = ($p[2] -ne 0); return }
    # Read-reply cache keyed by cmd (+ index): 02/port, 04/idx, 08, 0b/zone/param.
    if ($group -eq 0x9F -and $len -ge 2) {
        $key = switch ($p[0]) {
            0x08 { "08" }
            0x0B { if ($len -ge 3) { "0b/{0:x2}/{1:x2}" -f $p[1], $p[2] } else { "0b" } }
            default { "{0:x2}/{1:x2}" -f $p[0], $p[1] }
        }
        $script:reads[$key] = $p
    }
}

# ---------------------------------------------------------------- heartbeat / pump
$script:clock = [Diagnostics.Stopwatch]::StartNew()
$script:lastHb = -1000
$script:lastPortPoll = -1000
$script:feedbackHz = 2
function Heartbeat {
    $now = $script:clock.ElapsedMilliseconds
    if ($now - $script:lastHb -ge 250) {
        $script:lastHb = $now
        Send 0x1F @(0x03, 0, 0, 0, 0)
        Send 0x21 @(0x01)
    }
    if ($now - $script:lastPortPoll -ge (1000 / $script:feedbackHz)) {
        $script:lastPortPoll = $now
        foreach ($port in @($script:ports.Keys)) { if ($script:ports[$port]) { Send 0x21 @(0x02, [byte]$port) } }
    }
}
function Pump([int]$ms) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    do { Heartbeat; Poll-Serial; Start-Sleep -Milliseconds 5 } while ($t.ElapsedMilliseconds -lt $ms)
}
function Wait-For([scriptblock]$cond, [int]$timeoutMs, [string]$what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { Heartbeat; Poll-Serial; if (& $cond) { return $true }; Start-Sleep -Milliseconds 10 }
    throw "Timed out waiting for $what (mode current=$($script:mode.current) flags=0x$('{0:X2}' -f $script:mode.flags))"
}

# ---------------------------------------------------------------- telemetry frame
function New-Inputs { @{ f = @(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, $SpeedMs) + $SHOCK_REST; brake = 0; throttle = 0; flags = 1 } }
function Build-Game($in) {
    [byte[]]$p = New-Object byte[] 53
    $p[0] = 0x00
    for ($i = 0; $i -lt 12; $i++) { [Array]::Copy((F32 $in.f[$i]), 0, $p, 1 + 4 * $i, 4) }
    $p[49] = [byte][Math]::Max(0, [Math]::Min(100, $in.brake))
    $p[50] = [byte][Math]::Max(0, [Math]::Min(100, $in.throttle))
    $p[51] = 0
    $p[52] = [byte]$in.flags
    return ,$p
}
$script:nextFrame = 0
$script:frameMs = [Math]::Max(5, [int][Math]::Round(1000.0 / $RateHz))
# Streams the inputs produced by $gen (called with elapsed seconds) for $sec seconds.
function Stream([double]$sec, [scriptblock]$gen) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    while ($t.ElapsedMilliseconds -lt $sec * 1000) {
        $now = $script:clock.ElapsedMilliseconds
        if ($now -ge $script:nextFrame) {
            $script:nextFrame = $now + $script:frameMs
            Send 0x22 (Build-Game (& $gen ($t.ElapsedMilliseconds / 1000.0)))
        }
        Heartbeat; Poll-Serial
        if ($script:mode.current -ne 1) { throw "Controller left game mode (current=$($script:mode.current) flags=0x$('{0:X2}' -f $script:mode.flags))" }
        Start-Sleep -Milliseconds 1
    }
}
function Neutral([double]$sec) { Stream $sec { param($t) New-Inputs } }
function Rad([double]$deg) { [Math]::Max(-$MaxDeg, [Math]::Min($MaxDeg, $deg)) * [Math]::PI / 180.0 }

# ---------------------------------------------------------------- controller config
function Set-Slot([byte]$slot, [double]$a, [double]$b) { Send 0x1E (@(0x05, $slot) + (F32 $a) + (F32 $b)); Pump 20 }
function Set-Road([double]$a, [double]$b) { Send 0x1E (@(0x06) + (F32 $a) + (F32 $b)); Pump 20 }
# Pass-through baseline: everything off except the slot under test.
function Set-Slots([double]$pitch, [double]$roll, [double]$heave, [double]$smooth, [double]$road = 0.0) {
    Set-Slot 0x00 $pitch $smooth
    Set-Slot 0x01 $roll $smooth
    Set-Slot 0x03 $heave $smooth
    Set-Slot 0x07 0.0 0.0
    Set-Slot 0x08 0.0 0.0
    Set-Road $road $smooth
    Mark ("slots pitch={0} roll={1} heave={2} smooth={3} road={4}" -f $pitch, $roll, $heave, $smooth, $road)
}

function Connect-Controller {
    Pump 300
    $null = Wait-For { $script:mode.current -ge 0 } 2000 "first mode readback"
    foreach ($port in 1..8) { Send 0x21 @(0x02, [byte]$port); Send 0x1F @(0x02, [byte]$port, 0, 0, 0) }
    Send 0x1F @(0x00, 0, 0); Send 0x1F @(0x04, 0x00, 0, 0, 0, 0); Send 0x1F @(0x08, 0, 0, 0, 0, 0, 0, 0, 0)
    Pump 500
    $present = @($script:ports.Keys | Where-Object { $script:ports[$_] } | Sort-Object)
    Write-Host ("Controller mode {0}, actuators on ports {1}" -f $script:mode.current, ($present -join ","))
    foreach ($port in $present) {
        $r = $script:reads[("02/{0:x2}" -f $port)]
        if ($r) { Write-Host ("  port {0} -> role {1}" -f $port, $r[3]) }
    }
    $r = $script:reads["04/00"]; if ($r) { Write-Host ("  Max Available Stroke {0:F3}" -f (ReadF32 $r 2)) }
    $r = $script:reads["08"]
    if ($r) { Write-Host ("  Rig L1 {0:F1} mm, L2 {1:F1} mm" -f (ReadF32 $r 1), (ReadF32 $r 5)) }
    Send 0x20 @(0x02, 0x01, 0x00); Pump 20
    Send 0x1E @(0x0A, 0x01); Pump 20
}

function Enter-Game {
    if (-not $SkipSelfCheck) {
        Write-Host "Self-check: the platform moves to its ready position." -ForegroundColor Magenta
        $script:selfCheckOk = $false
        Send 0x1E @(0x03, 0x07, 0, 0, 0)
        $null = Wait-For { $script:selfCheckOk } 30000 "selfCheckSuccess"
        Pump 300
    }
    Write-Host "Entering game mode..." -ForegroundColor Green
    $sw = [Diagnostics.Stopwatch]::StartNew(); $sentRun = $false
    while ($script:mode.current -ne 1) {
        if ($sw.ElapsedMilliseconds -gt 15000) { throw "Controller did not enter game mode (current=$($script:mode.current))" }
        Send 0x22 (Build-Game (New-Inputs)); Send 0x1E @(0x03, 0x01, 0, 0, 0)
        if (-not $sentRun -and $sw.ElapsedMilliseconds -gt 300) { Send 0x20 @(0x02, 0x03, 0x00); $sentRun = $true }
        Pump 33
    }
    if (-not $sentRun) { Send 0x20 @(0x02, 0x03, 0x00) }
    Mark "game mode"
    Neutral 3
}

# ---------------------------------------------------------------- experiments
function Exp-Gain([string]$ax = $Axis) {
    $fieldIdx = @{ Pitch = 0; Roll = 1 }[$ax]
    foreach ($I in $Intensities) {
        if ($ax -eq "Pitch") { Set-Slots $I 0 0 0 } else { Set-Slots 0 $I 0 0 }
        Neutral 3
        foreach ($s in $StepsDeg) {
            Mark ("STEP {0} {1:+0.0;-0.0} deg intensity {2} - read the inclinometer at the start and end of the hold" -f $ax, $s, $I)
            $v = Rad $s
            Stream $HoldSec { param($t) $in = New-Inputs; $in.f[$fieldIdx] = $v; $in }
            Mark "release"
            Neutral ([Math]::Max(3, $HoldSec / 2))
        }
    }
}

function Exp-Sweep([string]$ax = $Axis) {
    $fieldIdx = @{ Pitch = 0; Roll = 1 }[$ax]
    foreach ($sm in $Smooths) {
        if ($ax -eq "Pitch") { Set-Slots 1.0 0 0 $sm } else { Set-Slots 0 1.0 0 $sm }
        Neutral 3
        foreach ($hz in $SweepHz) {
            $dur = [Math]::Max(5.0, 6.0 / $hz)
            Mark ("SWEEP {0} {1} Hz +/-{2} deg smooth {3} rate {4} Hz" -f $ax, $hz, $SweepAmpDeg, $sm, $RateHz)
            $amp = Rad $SweepAmpDeg
            Stream $dur { param($t) $in = New-Inputs; $in.f[$fieldIdx] = $amp * [Math]::Sin(2 * [Math]::PI * $hz * $t); $in }
            Neutral 3
        }
    }
}

function Exp-Heave {
    Set-Slots 0 0 1.0 0 1.0
    Neutral 3
    foreach ($a in @(2.0, 5.0, -2.0, -5.0)) {
        Mark ("HEAVE f6 vertical accel {0:+0.0;-0.0} m/s2" -f $a)
        Stream $HoldSec { param($t) $in = New-Inputs; $in.f[6] = $a; $in }
        Neutral 4
    }
    $shockSets = @(
        @("all +5 mm", @(5, 5, 5, 5)), @("all +10 mm", @(10, 10, 10, 10)), @("all -5 mm", @(-5, -5, -5, -5)),
        @("front +5 rear -5", @(5, -5, -5, 5)), @("left +5 right -5", @(5, 5, -5, -5)),
        @("f8 only +8", @(8, 0, 0, 0)), @("f9 only +8", @(0, 8, 0, 0)), @("f10 only +8", @(0, 0, 8, 0)), @("f11 only +8", @(0, 0, 0, 8)))
    foreach ($set in $shockSets) {
        Mark ("SHOCKS {0} (f8..f11 offsets from rest; note which corner moves)" -f $set[0])
        $off = $set[1]
        Stream $HoldSec { param($t) $in = New-Inputs; for ($k = 0; $k -lt 4; $k++) { $in.f[8 + $k] = $SHOCK_REST[$k] + [Math]::Max(-$MaxShockMm, [Math]::Min($MaxShockMm, $off[$k])) }; $in }
        Neutral 4
    }
}

function Exp-Gating {
    Set-Slots 1.0 0 0 0
    Neutral 3
    $v = Rad 3
    $variants = @(
        @("speed 0", { param($t) $in = New-Inputs; $in.f[0] = $v; $in.f[7] = 0; $in }),
        @("speed 30 m/s", { param($t) $in = New-Inputs; $in.f[0] = $v; $in.f[7] = 30; $in }),
        @("brake 50", { param($t) $in = New-Inputs; $in.f[0] = $v; $in.brake = 50; $in }),
        @("throttle 50", { param($t) $in = New-Inputs; $in.f[0] = $v; $in.throttle = 50; $in }),
        @("flags 00 (not in game)", { param($t) $in = New-Inputs; $in.f[0] = $v; $in.flags = 0; $in }),
        @("gear-shift bit pulsed every 2 s", { param($t) $in = New-Inputs; $in.f[0] = $v; if (($t % 2.0) -lt 0.12) { $in.flags = 3 }; $in }))
    foreach ($var in $variants) {
        Mark ("GATING pitch +3 deg with {0}" -f $var[0])
        Stream $HoldSec $var[1]
        Neutral 4
    }
    Mark "GATING run state 02 (hold) while pitch +3 deg"
    Send 0x20 @(0x02, 0x02, 0x00)
    Stream $HoldSec { param($t) $in = New-Inputs; $in.f[0] = $v; $in }
    Send 0x20 @(0x02, 0x03, 0x00)
    Mark "GATING run state back to 03"
    Neutral 4
}

function Exp-Feedback {
    $script:feedbackHz = 10
    Set-Slots 1.0 1.0 0 0
    Neutral 3
    $amp = Rad 3
    Mark "FEEDBACK pitch 0.2 Hz +/-3 deg (0x21/02 polled at 10 Hz)"
    Stream 30 { param($t) $in = New-Inputs; $in.f[0] = $amp * [Math]::Sin(2 * [Math]::PI * 0.2 * $t); $in }
    Mark "FEEDBACK roll 0.2 Hz +/-3 deg"
    Stream 30 { param($t) $in = New-Inputs; $in.f[1] = $amp * [Math]::Sin(2 * [Math]::PI * 0.2 * $t); $in }
    Neutral 3
    $script:feedbackHz = 2
}

function Exp-Writes {
    # Uncaptured writes inferred from their reads. Each is read back; the old value is restored.
    if ($StrokeValue -ge 0) {
        Send 0x1F @(0x04, 0x00, 0, 0, 0, 0); Pump 200
        $old = $script:reads["04/00"]
        Mark ("WRITE 1E/04 00 Max Available Stroke {0}" -f $StrokeValue)
        Send 0x1E (@(0x04, 0x00) + (F32 $StrokeValue)); Pump 300
        Send 0x1F @(0x04, 0x00, 0, 0, 0, 0); Pump 300
        $r = $script:reads["04/00"]
        Write-Host ("  stroke readback {0}" -f $(if ($r) { (ReadF32 $r 2).ToString("F3") } else { "none" })) -ForegroundColor Yellow
        if ($old) { Send 0x1E (@(0x04, 0x00) + $old[2..5]); Pump 300; Mark "stroke restored" }
    }
    if ($RigL1 -gt 0 -and $RigL2 -gt 0) {
        Send 0x1F @(0x08, 0, 0, 0, 0, 0, 0, 0, 0); Pump 200
        $old = $script:reads["08"]
        Mark ("WRITE 1E/08 rig dimensions {0} x {1} mm" -f $RigL1, $RigL2)
        Send 0x1E (@(0x08) + (F32 $RigL1) + (F32 $RigL2)); Pump 300
        Send 0x1F @(0x08, 0, 0, 0, 0, 0, 0, 0, 0); Pump 300
        $r = $script:reads["08"]
        Write-Host ("  rig readback {0}" -f $(if ($r) { "{0:F1} x {1:F1}" -f (ReadF32 $r 1), (ReadF32 $r 5) } else { "none" })) -ForegroundColor Yellow
        Mark "mode 0 then 3 (watch the log for 'Calib Length')"
        Send 0x1E @(0x03, 0x00, 0, 0, 0); Pump 1500
        Send 0x1E @(0x03, 0x03, 0, 0, 0); Pump 5000
        if ($old) { Send 0x1E (@(0x08) + $old[1..8]); Pump 300; Mark "rig dimensions restored" }
        Send 0x1E @(0x03, 0x00, 0, 0, 0); Pump 1000
    }
    if (-not $SkipLighting) {
        # Captured table: global 0B 03 01 = brightness 80; zones 4..6 params 03..06 = RGB.
        Mark "LIGHT global brightness 20 (captured value 80)"
        Send 0x1E @(0x0B, 0x03, 0x01, 20); Pump 3000
        Send 0x1E @(0x0B, 0x03, 0x01, 80); Pump 500
        foreach ($zone in 4, 5, 6) {
            Mark ("LIGHT zone {0} params 03..06 = FF0000 for 3 s, then 00FF00" -f $zone)
            Send 0x1F @(0x0B, [byte]$zone, 0x03, 0, 0, 0); Pump 200
            $old = $script:reads[("0b/{0:x2}/03" -f $zone)]
            foreach ($param in 3..6) { Send 0x1E @(0x0B, [byte]$zone, [byte]$param, 0xFF, 0x00, 0x00); Pump 30 }
            Pump 3000
            foreach ($param in 3..6) { Send 0x1E @(0x0B, [byte]$zone, [byte]$param, 0x00, 0xFF, 0x00); Pump 30 }
            Pump 3000
            if ($old -and $old.Length -ge 6) { foreach ($param in 3..6) { Send 0x1E @(0x0B, [byte]$zone, [byte]$param, $old[3], $old[4], $old[5]); Pump 30 } }
        }
    }
}

# ---------------------------------------------------------------- main
Write-Host "==============================================="
Write-Host " HMA150 characterization: $Experiment ($Axis), $RateHz Hz frames"
Write-Host " Log: $Log.jsonl / $Log.markers.csv"
Write-Host " E-STOP WITHIN REACH. Ctrl+C to stop."
Write-Host "==============================================="
$serial = New-Object System.IO.Ports.SerialPort $ComPort, 115200, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
$serial.Handshake = [System.IO.Ports.Handshake]::None
$serial.DtrEnable = $true
$serial.RtsEnable = $true
$serial.ReadTimeout = 50
$serial.WriteTimeout = 500
$serial.Open()
try {
    Connect-Controller
    if ($Experiment -eq "Writes") { Exp-Writes; return }
    Enter-Game
    switch ($Experiment) {
        "All"      {
            $runs = @(
                @("Gain Pitch", { Exp-Gain "Pitch" }), @("Gain Roll", { Exp-Gain "Roll" }), @("Heave", { Exp-Heave }),
                @("Sweep Pitch", { Exp-Sweep "Pitch" }), @("Gating", { Exp-Gating }), @("Feedback", { Exp-Feedback }))
            foreach ($run in $runs) {
                Write-Host ("--- {0}" -f $run[0]) -ForegroundColor Cyan
                Mark ("EXPERIMENT {0}" -f $run[0])
                & $run[1]
                Neutral 5
            }
        }
        "Gain"     { Exp-Gain }
        "Sweep"    { Exp-Sweep }
        "Heave"    { Exp-Heave }
        "Gating"   { Exp-Gating }
        "Feedback" { Exp-Feedback }
    }
    Mark "done"
    Neutral 2
}
finally {
    try {
        Send 0x20 @(0x02, 0x01, 0x00); Pump 200
        Send 0x1E @(0x03, [byte]$ExitMode, 0, 0, 0); Pump 1500
        Mark "exit mode $ExitMode"
    } catch {}
    $serial.Close()
    $logStream.Close(); $markStream.Close()
    Write-Host "Closed. Send back $Log.jsonl and $Log.markers.csv."
}

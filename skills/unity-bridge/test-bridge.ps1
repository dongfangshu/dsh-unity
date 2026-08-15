# Protocol self-test: drop commands into Library/UnityBridge/in/ and check out/.
# Unity editor must be open with the bridge enabled.
#
#   pwsh skills/unity-bridge/test-bridge.ps1
#   pwsh skills/unity-bridge/test-bridge.ps1 -SkipPlay -SkipRefresh
#   pwsh skills/unity-bridge/test-bridge.ps1 -IncludeSceneOps
#
# Default covers every domain. Play enters and exits play mode. Refresh
# ForceUpdates the project (may domain-reload). OpenScene is off unless
# -IncludeSceneOps — reopening the current scene can freeze on a save dialog.

param(
    [string]$Project = "",
    [switch]$SkipPlay,
    [switch]$SkipRefresh,
    [switch]$IncludeSceneOps
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding $false

if (-not $Project) {
    $Project = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..")) "UnityMain"
}
$root = Join-Path $Project "Library\UnityBridge"
$inDir = Join-Path $root "in"
$outDir = Join-Path $root "out"
$hbPath = Join-Path $root "status\heartbeat.json"

if (-not (Test-Path $hbPath)) {
    Write-Error "bridge offline: missing $hbPath (open the Unity project)"
}
$hb = Get-Content $hbPath -Raw | ConvertFrom-Json
$hbAge = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 - [double]$hb.ts
if ($hbAge -gt 15) {
    Write-Error ("bridge offline: heartbeat {0:N1}s old" -f $hbAge)
}

function New-Stem([string]$op) {
    Start-Sleep -Milliseconds 5
    return "$op-$(Get-Date -Format 'yyyyMMdd-HHmmssfff')"
}

function Send-Json([string]$op, [string]$domain, $argsObj) {
    $stem = New-Stem $op
    $out = Join-Path $outDir "$stem.json"
    if (Test-Path $out) { Remove-Item $out -Force }
    $payload = @{ domain = $domain; op = $op }
    if ($null -ne $argsObj) { $payload.args = $argsObj }
    $tmp = Join-Path $inDir "$stem.json.tmp"
    [System.IO.File]::WriteAllText($tmp, ($payload | ConvertTo-Json -Compress -Depth 10), $utf8)
    Move-Item -LiteralPath $tmp -Destination (Join-Path $inDir "$stem.json") -Force
    return $stem
}

function Send-Cs([string]$code) {
    $stem = New-Stem "cs"
    $out = Join-Path $outDir "$stem.json"
    if (Test-Path $out) { Remove-Item $out -Force }
    $tmp = Join-Path $inDir "$stem.cs.tmp"
    [System.IO.File]::WriteAllText($tmp, $code, $utf8)
    Move-Item -LiteralPath $tmp -Destination (Join-Path $inDir "$stem.cs") -Force
    return $stem
}

function Wait-Out([string]$stem, [int]$timeoutSec = 25) {
    $out = Join-Path $outDir "$stem.json"
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $out) {
            return Get-Content $out -Raw | ConvertFrom-Json
        }
        Start-Sleep -Milliseconds 80
    }
    return [pscustomobject]@{ id = $stem; ok = $false; error = "TIMEOUT waiting for out/$stem.json" }
}

function Wait-Heartbeat([double]$beforeTs, [int]$timeoutSec = 45) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $h = Get-Content $hbPath -Raw | ConvertFrom-Json
            if ([double]$h.ts -gt $beforeTs + 2) { return $h }
        } catch {}
        Start-Sleep -Milliseconds 400
    }
    return $null
}

$script:pass = 0
$script:fail = 0
$script:rows = New-Object System.Collections.Generic.List[string]

function Record([string]$name, $resp, [scriptblock]$okWhen) {
    $ok = $false
    try { $ok = [bool](& $okWhen $resp) } catch { $ok = $false }
    $detail = if ($resp.ok) {
        ($resp.result | ConvertTo-Json -Compress -Depth 5)
    } else {
        [string]$resp.error
    }
    if ($detail.Length -gt 120) { $detail = $detail.Substring(0, 117) + "..." }
    if ($ok) {
        $script:pass++
        $script:rows.Add("PASS  $name  $detail")
    } else {
        $script:fail++
        $script:rows.Add("FAIL  $name  $detail")
    }
}

Write-Output "bridge $root  scene=$($hb.scene)  playing=$($hb.playing)"
Write-Output ""

# --- core: ping / status ---
$r = Wait-Out (Send-Json "ping" "core" @{}); Record "core.ping" $r { param($x) $x.ok -and $x.result.pong }
$r = Wait-Out (Send-Json "status" "core" @{}); Record "core.status" $r { param($x) $x.ok -and $x.result.activeScene }
$scene = $hb.scene
if ($r.ok -and $r.result.activeScene) { $scene = [string]$r.result.activeScene }

# --- log ---
$r = Wait-Out (Send-Json "log" "log" @{ lines = 10 }); Record "log.log" $r { param($x) $x.ok -and $null -ne $x.result.entries }

# --- read ---
$r = Wait-Out (Send-Json "select" "read" @{}); Record "read.select" $r { param($x) $x.ok }
$r = Wait-Out (Send-Json "hierarchy" "read" @{ path = $scene }); Record "read.hierarchy scene" $r { param($x) $x.ok -and $x.result.kind -eq "scene" }
$player = "$scene/Player"
$r = Wait-Out (Send-Json "hierarchy" "read" @{ path = $player }); Record "read.hierarchy Player" $r { param($x) $x.ok -and $x.result.kind -eq "gameObject" }
$r = Wait-Out (Send-Json "assets" "read" @{ path = "Assets/Scripts/PlayerController.cs" }); Record "read.assets PlayerController" $r { param($x) $x.ok -and $x.result.kind -eq "text" }

# --- execute (dropped .cs, no scene mutation) ---
$code = @"
public static class Entry {
  public static object Main(object args) {
    return "protocol-ok";
  }
}
"@
$r = Wait-Out (Send-Cs $code); Record "execute.cs drop" $r { param($x) $x.ok -and [string]$x.result.value -eq "protocol-ok" }
$r = Wait-Out (Send-Json "cs" "execute" @{ code = "ignored" }); Record "execute JSON rejected" $r { param($x) -not $x.ok }

# --- core session ---
$r = Wait-Out (Send-Json "menuitem" "core" @{ item = "Tools/Unity Bridge/Enable" }); Record "core.menuitem Enable" $r { param($x) $x.ok -and $x.result.executed }
$r = Wait-Out (Send-Json "menuitem" "core" @{ item = "No/Such/Menu" }); Record "core.menuitem missing" $r { param($x) -not $x.ok }
$r = Wait-Out (Send-Json "saveassets" "core" @{}); Record "core.saveassets" $r { param($x) $x.ok -and $x.result.saved }
$r = Wait-Out (Send-Json "savescene" "core" @{ path = $scene }); Record "core.savescene" $r { param($x) $x.ok -and $x.result.saved }
$r = Wait-Out (Send-Json "removescene" "core" @{ path = $scene }); Record "core.removescene last" $r { param($x) -not $x.ok }
$r = Wait-Out (Send-Json "explode" "core" @{}); Record "core unknown op" $r { param($x) -not $x.ok }
$r = Wait-Out (Send-Json "ping" "nope" @{}); Record "unknown domain" $r { param($x) -not $x.ok }

if ($IncludeSceneOps) {
    $r = Wait-Out (Send-Json "openscene" "core" @{ path = $scene; mode = "single" }); Record "core.openscene" $r { param($x) $x.ok }
}

if (-not $SkipPlay) {
    $r = Wait-Out (Send-Json "play" "core" @{}); Record "core.play" $r { param($x) $x.ok }
    Start-Sleep -Seconds 2
    $r = Wait-Out (Send-Json "status" "core" @{}); Record "core.status playing" $r { param($x) $x.ok -and $x.result.playing }
    $r = Wait-Out (Send-Json "pause" "core" @{}); Record "core.pause" $r { param($x) $x.ok -and $x.result.paused }
    $r = Wait-Out (Send-Json "step" "core" @{}); Record "core.step" $r { param($x) $x.ok }
    $r = Wait-Out (Send-Json "resume" "core" @{}); Record "core.resume" $r { param($x) $x.ok -and -not $x.result.paused }
    $r = Wait-Out (Send-Json "stop" "core" @{}); Record "core.stop" $r { param($x) $x.ok }
    Start-Sleep -Seconds 2
    $r = Wait-Out (Send-Json "status" "core" @{}); Record "core.status stopped" $r { param($x) $x.ok -and -not $x.result.playing }
}

if (-not $SkipRefresh) {
    $before = [double]((Get-Content $hbPath -Raw | ConvertFrom-Json).ts)
    $stem = Send-Json "refresh" "core" @{}
    $r = Wait-Out $stem 20
    Record "core.refresh" $r { param($x) $x.ok -and $x.result.refreshing }
    $h = Wait-Heartbeat $before 45
    if ($h) {
        $script:pass++
        $script:rows.Add("PASS  heartbeat after refresh  ts=$($h.ts)")
    } else {
        $script:fail++
        $script:rows.Add("FAIL  heartbeat after refresh  did not advance")
    }
    Start-Sleep -Seconds 2
    $r = Wait-Out (Send-Json "ping" "core" @{}); Record "core.ping after refresh" $r { param($x) $x.ok -and $x.result.pong }
}

Write-Output ""
$script:rows | ForEach-Object { Write-Output $_ }
Write-Output ""
Write-Output ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0

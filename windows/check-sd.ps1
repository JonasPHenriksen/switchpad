# check-sd.ps1 - prints what's on the Pi's SD card so problems can be diagnosed.
# Usage: double-click check-sd.cmd with the SD card in the PC, then copy the whole output.
param([string]$BootPath)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$piDir = Join-Path (Split-Path -Parent $here) 'pi'

function Version($path) {
    if (-not (Test-Path $path)) { return 'missing' }
    $t = [IO.File]::ReadAllText($path)
    if ($t.Contains('/dev/tty1')) { return 'v3 (logs to card + screen)' }
    if ($t.Contains('switchpad.log')) { return 'v2 (logs to card)' }
    return 'v1 (old, no logs)'
}

"=== This project folder: $(Split-Path -Parent $here)"
"  pi\switchpad.py version: $(Version (Join-Path $piDir 'switchpad.py'))"
"  prepare-sd.ps1 can update installed cards: $([bool](Select-String -Quiet -Path (Join-Path $here 'prepare-sd.ps1') -Pattern 'already installed'))"

$found = $false
$roots = @()
if ($BootPath) { $roots = @($BootPath) }
else { foreach ($d in [IO.DriveInfo]::GetDrives()) { if ($d.IsReady) { $roots += $d.RootDirectory.FullName } } }
foreach ($r in $roots) {
    $label = ''
    try { $label = (New-Object IO.DriveInfo $r).VolumeLabel } catch { }
    if (-not ((Test-Path "$r\cmdline.txt") -and (Test-Path "$r\config.txt"))) { continue }
    $found = $true
    ""
    "=== Pi SD card at $r (label '$label')"
    "--- first-boot files:"
    foreach ($f in 'user-data', 'meta-data', 'network-config', 'firstrun.sh') {
        if (Test-Path "$r\$f") { "  $f  ($((Get-Item "$r\$f").LastWriteTime))" } else { "  $f  (none)" }
    }
    if (Test-Path "$r\user-data") {
        "  user-data has SwitchPad hook: $([bool](Select-String -Quiet -Path "$r\user-data" -Pattern 'switchpad/install.sh'))"
        "  user-data has power_state:   $([bool](Select-String -Quiet -Path "$r\user-data" -Pattern '^power_state:'))"
    }
    if (Test-Path "$r\meta-data") { "--- meta-data:"; Get-Content "$r\meta-data" | ForEach-Object { "  $_" } }
    "--- switchpad folder:"
    if (Test-Path "$r\switchpad") {
        Get-ChildItem "$r\switchpad" -Force | ForEach-Object { "  {0,-16} {1,8} bytes  {2}" -f $_.Name, $_.Length, $_.LastWriteTime }
        "  switchpad.py on card version: $(Version "$r\switchpad\switchpad.py")"
    } else { "  (missing - prepare-sd was never run on this card)" }
    $cmd = [IO.File]::ReadAllText("$r\cmdline.txt")
    "  cmdline.txt: one-time setup scheduled: $($cmd.Contains('switchpad/firstboot.sh'))   other boot script: $($cmd.Contains('systemd.run=') -and -not $cmd.Contains('switchpad/firstboot.sh'))"
    foreach ($f in 'diag.txt', 'install.log', 'switchpad.log') {
        $p = "$r\switchpad\$f"
        if (Test-Path $p) { "--- $f (last 40 lines):"; Get-Content $p -Tail 40 | ForEach-Object { "  $_" } }
        else { "--- ${f}: none" }
    }
    $p = "$r\switchpad\cloud-init-output.log"
    if (Test-Path $p) { "--- cloud-init-output.log (last 25 lines):"; Get-Content $p -Tail 25 | ForEach-Object { "  $_" } }
    $p = "$r\switchpad\cloud-init.log"
    if (Test-Path $p) {
        "--- cloud-init.log: runcmd / errors:"
        Select-String -Path $p -Pattern 'runcmd|scripts_user|WARNING|ERROR|Traceback|power_state|schema' |
            Select-Object -Last 25 | ForEach-Object { "  " + $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) }
    }
    "  USB device mode line (dtoverlay=dwc2,dr_mode=peripheral) in config.txt: $([bool](Select-String -Quiet -Path "$r\config.txt" -Pattern '^dtoverlay=dwc2,dr_mode=peripheral'))"
    "--- config.txt (last 6 lines):"
    Get-Content "$r\config.txt" -Tail 6 | ForEach-Object { "  $_" }
}
if (-not $found) { ""; "No Raspberry Pi SD card found. Insert it (take it out and back in after flashing)." }

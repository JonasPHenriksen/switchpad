# prepare-sd.ps1 - run after Raspberry Pi Imager has written the SD card (and to update it later).
# Copies the Pi files to the card's boot partition, turns on USB device mode, and schedules
# the one-time install for the Pi's next boot via cmdline.txt, so no SSH, Wi-Fi or keyboard
# is needed.
# Usage: prepare-sd.cmd            (finds the card automatically)
#        prepare-sd.ps1 -BootPath E:\
param([string]$BootPath)
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$piDir = Join-Path (Split-Path -Parent $here) 'pi'
$utf8 = New-Object System.Text.UTF8Encoding($false)   # no BOM, the Pi needs plain files
$piFiles = 'install.sh', 'switchpad.py', 'firstboot.sh'
$bootOpts = 'systemd.run=/boot/firmware/switchpad/firstboot.sh systemd.run_success_action=reboot ' +
            'systemd.run_failure_action=reboot systemd.unit=kernel-command-line.target'

function Fail($msg) { Write-Host ""; Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

foreach ($n in $piFiles) {
    if (-not (Test-Path (Join-Path $piDir $n))) { Fail "Can't find pi\$n next to the 'windows' folder ($piDir)." }
}

# --- find the boot partition (has cmdline.txt and config.txt) ---
if (-not $BootPath) {
    $found = @()
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $d.IsReady) { continue }
        $r = $d.RootDirectory.FullName
        if ((Test-Path (Join-Path $r 'cmdline.txt')) -and (Test-Path (Join-Path $r 'config.txt'))) { $found += $r }
    }
    if ($found.Count -eq 0) { Fail "No Raspberry Pi SD card found. Flash it with Raspberry Pi Imager first, then re-insert it." }
    if ($found.Count -gt 1) { Fail ("Several Pi SD cards found: " + ($found -join ', ') + ". Run: prepare-sd.ps1 -BootPath X:\") }
    $BootPath = $found[0]
}
Write-Host "SD card boot partition: $BootPath"
$dest = Join-Path $BootPath 'switchpad'

function Read-File($path) {
    $text = [System.IO.File]::ReadAllText($path)
    $nl = "`n"; if ($text.Contains("`r`n")) { $nl = "`r`n" }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.AddRange([string[]]($text -split "\r?\n"))
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') { $lines.RemoveAt($lines.Count - 1) }
    return @{ Lines = $lines; NL = $nl; Text = $text }
}
function Write-File($path, $f) {
    [System.IO.File]::WriteAllText($path, (($f.Lines -join $f.NL) + $f.NL), $utf8)
}

# --- 1. USB device mode: the Pi's "USB" port must act as a device (the controller) ---
# Our own exact line under [all] at the end. Raspberry Pi OS ships "dtoverlay=dwc2,dr_mode=host"
# under [cm5]; that one is for another board and doesn't count.
$configPath = Join-Path $BootPath 'config.txt'
$cfg = Read-File $configPath
if ($cfg.Text -match '(?m)^dtoverlay=dwc2,dr_mode=peripheral') {
    Write-Host "config.txt: USB device mode already on."
} else {
    $cfg.Lines.Add('')
    $cfg.Lines.Add('[all]')
    $cfg.Lines.Add('dtoverlay=dwc2,dr_mode=peripheral')
    Write-File $configPath $cfg
    Write-Host "config.txt: turned on USB device mode."
}
# Bluetooth isn't used. Its radio shares the Pi Zero W's one chip with Wi-Fi, so switch it off.
$cfg = Read-File $configPath
if ($cfg.Text -notmatch '(?m)^dtoverlay=disable-bt') {
    $cfg.Lines.Add('')
    $cfg.Lines.Add('[all]')
    $cfg.Lines.Add('dtoverlay=disable-bt')
    Write-File $configPath $cfg
    Write-Host "config.txt: switched the Pi's Bluetooth radio off (Wi-Fi gets the chip to itself)."
}

# --- 2. copy the Pi files ---
New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($n in $piFiles) { Copy-Item (Join-Path $piDir $n) (Join-Path $dest $n) -Force }
Write-Host ("Copied " + ($piFiles -join ', ') + " to $dest")

# --- 3. already installed with v3 (Wi-Fi only) or later? Then copying was the whole update. ---
# Older installs get the install again: it removes the old Bluetooth setup.
$installLog = Join-Path $dest 'install.log'
if ((Test-Path $installLog) -and ((Get-Content $installLog -Raw) -match 'INSTALL OK \(v[3-9]')) {
    Write-Host ""
    Write-Host "This card is already installed: updated the files only." -ForegroundColor Green
    Write-Host "Put it back in the Pi; the new version runs from the next boot."
    Write-Host "The Pi's log will be in $dest\switchpad.log"
    exit 0
}

# --- 4. schedule the one-time install for the next boot (cmdline.txt, single line) ---
$cmdPath = Join-Path $BootPath 'cmdline.txt'
$cmdText = [System.IO.File]::ReadAllText($cmdPath)
if ($cmdText.Contains('switchpad/firstboot.sh')) {
    Write-Host "cmdline.txt: one-time setup already scheduled."
} elseif ($cmdText.Contains('systemd.run=')) {
    # Older Raspberry Pi OS uses the same mechanism for its own first-boot setup (firstrun.sh).
    Fail ("cmdline.txt already has a one-time boot script of Raspberry Pi OS itself. Boot the Pi once " +
          "(wait 5 minutes), then put the card back and run prepare-sd again.")
} else {
    $line = ($cmdText -split "\r?\n")[0].TrimEnd()
    [System.IO.File]::WriteAllText($cmdPath, $line + ' ' + $bootOpts + "`n", $utf8)
    Write-Host "cmdline.txt: scheduled the one-time setup for the next boot."
}

Write-Host ""
Write-Host "Done. Eject the card, put it in the Pi, connect HDMI and power it from PWR IN." -ForegroundColor Green
Write-Host "On the screen you'll see 'firstboot:' and '1/5 ... 5/5', 'INSTALL OK', then it reboots."
Write-Host "(A freshly flashed card may reboot once more for its own first-boot setup.)"
Write-Host "After that, lines starting with 'switchpad [' appear and the Pi is ready:"
Write-Host "start SwitchPadBridge.exe on the PC."

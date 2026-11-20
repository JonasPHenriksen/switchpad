# collect-logs.ps1 - packs everything needed for troubleshooting into one zip file.
#   collect-logs.cmd                      PC logs (+ the SD card's logs if the card is in this PC)
#   collect-logs.cmd USER@switchpad.local  also fetches a full report from the running Pi over SSH
param([string]$Pi)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $here "logs\collect-$stamp"
New-Item -ItemType Directory -Force $work | Out-Null

# 1. SwitchPadBridge logs from the last 3 days
$bridge = Get-ChildItem (Join-Path $here 'logs') -Filter 'bridge-*.log' -ErrorAction SilentlyContinue |
          Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-3) }
foreach ($f in $bridge) { Copy-Item $f.FullName $work }
Write-Host "PC logs: $(@($bridge).Count) file(s)"

# 2. Basic PC info: XInput pads, the PC's Wi-Fi connection and hotspot, network adapters
$pcInfo = Join-Path $work 'pc-info.txt'
& (Join-Path $here 'SwitchPadBridge.exe') --list > $pcInfo 2>&1
foreach ($c in 'netsh wlan show interfaces', 'netsh wlan show hostednetwork', 'ipconfig') {
    Add-Content $pcInfo "`r`n===== $c"
    cmd /c $c 2>&1 | Out-String -Width 200 | Add-Content $pcInfo
}

# 2b. Ping the Pi for 30 s: shows Wi-Fi delay spikes and lost packets without any game running.
if ($Pi) {
    $piHost = ($Pi -split '@')[-1]
    $ip = $null
    try { $ip = [System.Net.Dns]::GetHostAddresses($piHost) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1 } catch { }
    if (-not $ip) { $ip = $piHost }
    Write-Host "Pinging $piHost ($ip) for 30 seconds..."
    Add-Content $pcInfo "`r`n===== ping $piHost ($ip), 60 times, every 0.5 s"
    $times = @(); $lost = 0
    $pinger = New-Object System.Net.NetworkInformation.Ping
    for ($i = 0; $i -lt 60; $i++) {
        try { $r = $pinger.Send($ip, 1000) } catch { $r = $null }
        if ($r -and $r.Status -eq 'Success') { $times += $r.RoundtripTime } else { $lost++ }
        Start-Sleep -Milliseconds 500
    }
    if ($times.Count) {
        $m = $times | Measure-Object -Average -Maximum -Minimum
        $line = "replies {0}, lost {1}, min {2} ms, avg {3:0.0} ms, max {4} ms" -f $times.Count, $lost, $m.Minimum, $m.Average, $m.Maximum
    } else { $line = "no replies (lost $lost)" }
    Add-Content $pcInfo $line
    Add-Content $pcInfo ("all: " + ($times -join ' '))
    Write-Host "Ping: $line"
}

# 3. Report from the running Pi over SSH
if ($Pi) {
    Write-Host "Asking the Pi ($Pi) for a full report. Enter the Pi's password if asked (up to twice)."
    ssh -t $Pi "sudo python3 /boot/firmware/switchpad/switchpad.py report > /dev/null && echo REPORT-OK"
    # The report, plus the Pi's complete log (the report only has its last lines). One scp, one password.
    $piFiles = New-Item -ItemType Directory -Force (Join-Path $work 'pi')
    scp "${Pi}:/boot/firmware/switchpad/{report.txt,switchpad.log*}" $piFiles.FullName
    if (Test-Path (Join-Path $piFiles.FullName 'report.txt')) { Write-Host "Pi report and log: OK" } else { Write-Host "Pi report: FAILED (see messages above)" -ForegroundColor Yellow }
}

# 4. The SD card, if it's in this PC
foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
    if (-not $d.IsReady) { continue }
    $r = $d.RootDirectory.FullName
    if ((Test-Path "$r\cmdline.txt") -and (Test-Path "$r\switchpad")) {
        $sd = Join-Path $work 'sd-card'
        New-Item -ItemType Directory -Force $sd | Out-Null
        Get-ChildItem "$r\switchpad" -File | Where-Object { $_.Extension -in '.log', '.txt', '.old' -or $_.Name -like '*.log*' } |
            ForEach-Object { Copy-Item $_.FullName $sd }
        Copy-Item "$r\config.txt", "$r\cmdline.txt" $sd
        Write-Host "SD card ($r): copied its logs"
    }
}

# 5. Zip it
$zip = Join-Path $here "logs\switchpad-logs-$stamp.zip"
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
Remove-Item $work -Recurse -Force
Write-Host ""
Write-Host "Done. Send this file:" -ForegroundColor Green
Write-Host "  $zip"

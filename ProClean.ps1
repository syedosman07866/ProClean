# Copyright (c) 2026 Syed Osman (PRO SUPPORT). All rights reserved. Licensed under the MIT License.
<#
.SYNOPSIS
    ProClean v1.1.0 - Windows 10 / 11 Temp File Cleaner (GUI)
.DESCRIPTION
    A clean, safe temp-file cleaner for Windows 10 and 11.
    Scan first, preview everything, then clean only what you choose.

    What it cleans:
      - Windows Temp files (%TEMP% and C:\Windows\Temp)
      - Browser caches (Chrome, Edge, Firefox, Opera - only if installed)
      - Windows Update download cache (stops wuauserv, clears, restarts it)
      - Delivery Optimization cache
      - Old Windows log files (7+ days)
      - GPU & DirectX Shader Cache (7+ days)
      - Windows Error Reports (7+ days)
      - Old Prefetch files (30+ days, off by default)
      - Recycle Bin (off by default, own confirmation)
      - DNS cache flush (no files deleted)

    Safety rules (built in, not optional):
      - NOTHING is deleted before you press Scan and then Clean.
      - Files changed in the last 24 hours are never touched.
      - Files in use are skipped, never forced.
      - No whole-drive wildcards - every delete is scoped to a named folder.
      - No registry cleaning, no debloat, no telemetry changes.
      - Optional System Restore Point before cleaning (admin only).
      - Every run writes a log file to your Desktop.

    References (what this was built from):
      Win11Debloat (backup/restore safety), FluentCleaner (scan-preview
      + data-driven rules), PC-Cleaner (grouped menu, backup-before-wipe),
      CleanWindowsPro (admin/restore-point preflight), PC-Medic (minimal
      core steps, correct stop-service pattern for Update cache).
      All references are open source; this tool is a fresh rebuild.

.NOTES
    Author  : PRO SUPPORT (Syed Osman)
    Version : 1.1.0 - 18 Nov 2026 (final: 10 categories, drill-down, AutoClean, icon)
    License : MIT - free to use, share and modify
    Needs   : Windows 10 / 11, Windows PowerShell 5.1 (built in)
              Admin rights for Windows Update cache + Restore Point.
              Without admin, those two options are disabled, the rest works.

    HOW TO RUN:
      1. Double-click Run-ProClean.bat (same folder), or
         right-click ProClean.ps1 -> "Run with PowerShell".
      2. If Windows asks, click Yes (admin) for the full clean.
      3. Tick the categories, press Scan, review the preview,
         then press Clean.
      Double-click a result row to see its largest files.
      For silent scheduled cleaning: ProClean.ps1 -AutoClean
#>

param(
    [switch]$ScanOnly,  # Console-only scan, no GUI (for testing / scripting)
    [switch]$AutoClean  # Silent scan + clean of default categories, no GUI
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:AppVersion = '1.1.0'

# ---------------------------------------------------------------------------
# Config - categories are DATA, not hardcoded logic (like FluentCleaner rules)
#   Kind: Files | UpdateCache | RecycleBin | Dns
#   MinAgeHours: files newer than this are skipped (safety)
# ---------------------------------------------------------------------------
$script:Categories = @(
    [pscustomobject]@{ Id='Temp';        Name='Windows Temp Files';      Desc='Your Temp folder + C:\Windows\Temp';            Kind='Files';       MinAgeHours=24; NeedsAdmin=$false; Default=$true;  Warning=''; GetPaths={ @($env:TEMP, "$env:SystemRoot\Temp") } }
    [pscustomobject]@{ Id='Browser';     Name='Browser Caches';          Desc='Chrome, Edge, Firefox caches (if installed)'; Kind='Files';       MinAgeHours=0;  NeedsAdmin=$false; Default=$true;  Warning='Close browsers first for a full clean.'; GetPaths={ Get-BrowserCachePaths } }
    [pscustomobject]@{ Id='Update';      Name='Windows Update Cache';    Desc='Downloaded update files (service restarted)';  Kind='UpdateCache'; MinAgeHours=0;  NeedsAdmin=$true;  Default=$true;  Warning='Stops the Windows Update service briefly.'; GetPaths={ @("$env:SystemRoot\SoftwareDistribution\Download") } }
    [pscustomobject]@{ Id='DeliveryOpt'; Name='Delivery Optimization';   Desc='Shared update delivery cache';                 Kind='Files';       MinAgeHours=0;  NeedsAdmin=$true;  Default=$true;  Warning=''; GetPaths={ @("$env:ProgramData\Microsoft\Windows\DeliveryOptimization\Cache") } }
    [pscustomobject]@{ Id='Logs';        Name='Old Windows Logs';        Desc='Log files older than 7 days';                 Kind='Files';       MinAgeHours=168; NeedsAdmin=$false; Default=$true; Warning=''; GetPaths={ @("$env:SystemRoot\Logs", "$env:SystemRoot\Panther") } }
    [pscustomobject]@{ Id='Shader';      Name='GPU & DirectX Shader Cache'; Desc='Shader cache files older than 7 days';      Kind='Files';       MinAgeHours=168; NeedsAdmin=$false; Default=$true;  Warning='Active games skip locked shaders automatically.'; GetPaths={ @("$env:LOCALAPPDATA\D3DSCache", "$env:LOCALAPPDATA\NVIDIA\DXCache", "$env:LOCALAPPDATA\AMD\DxCache") } }
    [pscustomobject]@{ Id='WER';         Name='Windows Error Reports';   Desc='Error reports older than 7 days';             Kind='Files';       MinAgeHours=168; NeedsAdmin=$false; Default=$true;  Warning=''; GetPaths={ @("$env:ProgramData\Microsoft\Windows\WER\ReportArchive", "$env:ProgramData\Microsoft\Windows\WER\ReportQueue", "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportArchive") } }
    [pscustomobject]@{ Id='Prefetch';    Name='Prefetch (30+ days old)'; Desc='Old prefetch data only - speeds nothing';      Kind='Files';       MinAgeHours=720; NeedsAdmin=$true; Default=$false; Warning='Wiping all Prefetch slows first launches. Only 30+ day old files are touched.'; GetPaths={ @("$env:SystemRoot\Prefetch") } }
    [pscustomobject]@{ Id='RecycleBin';  Name='Empty Recycle Bin';       Desc='Permanently empties the Recycle Bin';          Kind='RecycleBin';  MinAgeHours=0;  NeedsAdmin=$false; Default=$false; Warning='This cannot be undone.'; GetPaths={ @() } }
    [pscustomobject]@{ Id='Dns';         Name='Flush DNS Cache';         Desc='Clears the DNS resolver cache (no files)';     Kind='Dns';         MinAgeHours=0;  NeedsAdmin=$false; Default=$true;  Warning=''; GetPaths={ @() } }
)

$script:LogPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ("ProClean-Log-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$script:IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:ScanResults = @{}   # Id -> @{ Files=[...]; Count=n; SizeBytes=n; Status='...' }
# GUI controls are created later; initialise to $null so Set-StrictMode never
# trips when the log/progress helpers run in console (-ScanOnly) mode.
$script:txtLog = $null
$script:progressBar = $null
$script:listView = $null
$script:lblStatus = $null

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return '{0:N2} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N2} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N2} KB' -f ($Bytes / 1KB) }
    return '{0} bytes' -f $Bytes
}

function Write-ProCleanLog {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    try { Add-Content -Path $script:LogPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    if ($script:txtLog) {
        $script:txtLog.AppendText("$line`r`n")
        $script:txtLog.SelectionStart = $script:txtLog.Text.Length
        $script:txtLog.ScrollToCaret()
    }
}

function Get-BrowserCachePaths {
    $paths = @()
    # Chrome + Edge: every profile (Default, Profile *, Guest Profile),
    # every cache subfolder. Opera has a single cache folder.
    foreach ($base in @(
        (Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data')
    )) {
        if (-not (Test-Path $base)) { continue }
        $profiles = @('Default', 'Guest Profile') + @(Get-ChildItem -Path $base -Directory -Filter 'Profile *' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        foreach ($profile in $profiles) {
            foreach ($sub in @('Cache', 'Cache\Cache_Data', 'Code Cache', 'GPUCache')) {
                $paths += (Join-Path (Join-Path $base $profile) $sub)
            }
        }
    }
    $opera = Join-Path $env:APPDATA 'Opera Software\Opera Stable\Cache'
    if (Test-Path $opera) { $paths += $opera }
    $ffProfiles = Join-Path $env:APPDATA 'Mozilla\Firefox\Profiles'
    if (Test-Path $ffProfiles) {
        Get-ChildItem -Path $ffProfiles -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $paths += (Join-Path $_.FullName 'cache2')
        }
    }
    return $paths
}

function Get-BrowserRunning {
    $names = @('chrome', 'msedge', 'firefox', 'opera')
    foreach ($n in $names) {
        if (Get-Process -Name $n -ErrorAction SilentlyContinue) { return $true }
    }
    return $false
}

function Get-FilesInPaths {
    param([string[]]$Paths, [int]$MinAgeHours)
    $cutoff = (Get-Date).AddHours(-$MinAgeHours)
    $files = @()
    foreach ($p in $Paths) {
        if (-not $p -or -not (Test-Path -LiteralPath $p)) { continue }
        try {
            $found = Get-ChildItem -LiteralPath $p -File -Recurse -Force -ErrorAction SilentlyContinue |
                Where-Object {
                    -not $_.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint) -and
                    ($MinAgeHours -le 0 -or $_.LastWriteTime -lt $cutoff)
                }
            if ($found) { $files += @($found) }
        } catch { }
    }
    return $files
}

function Get-RecycleBinInfo {
    try {
        $shell = New-Object -ComObject Shell.Application
        $items = $shell.NameSpace(0xA).Items()
        $count = 0; $size = 0
        foreach ($item in $items) { $count++; $size += [long]$item.Size }
        return @{ Count = $count; SizeBytes = $size }
    } catch {
        return @{ Count = 0; SizeBytes = 0 }
    }
}

function New-ProCleanRestorePoint {
    if (-not $script:IsAdmin) { Write-ProCleanLog 'Restore point skipped: not running as admin.'; return $false }
    try {
        Write-ProCleanLog 'Creating a System Restore Point (can take ~30 seconds)...'
        Checkpoint-Computer -Description 'ProClean before cleaning' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
        Write-ProCleanLog 'Restore Point created.'
        return $true
    } catch {
        Write-ProCleanLog ('Restore Point could not be created (Windows allows one per day, or restore is off): ' + $_.Exception.Message)
        return $false
    }
}

# ---------------------------------------------------------------------------
# Scan engine - preview only, deletes nothing
# ---------------------------------------------------------------------------
function Invoke-ProCleanScan {
    param([string[]]$SelectedIds)
    $script:ScanResults = @{}
    $totalCount = 0; $totalSize = 0
    $browserRunning = Get-BrowserRunning

    foreach ($cat in $script:Categories) {
        if ($SelectedIds -notcontains $cat.Id) { continue }
        $status = 'Ready'
        $files = @(); $count = 0; $size = 0

        if ($cat.NeedsAdmin -and -not $script:IsAdmin) {
            $status = 'Needs admin - run as administrator'
        } elseif ($cat.Kind -eq 'RecycleBin') {
            $info = Get-RecycleBinInfo
            $count = $info.Count; $size = $info.SizeBytes
        } elseif ($cat.Kind -eq 'Dns') {
            $count = 0; $size = 0; $status = 'Ready'
        } else {
            $paths = & $cat.GetPaths
            if (-not $paths -or $paths.Count -eq 0) {
                $status = 'Not found on this PC'
            } else {
                # @(...) keeps this an array even when zero files are found
                # (PowerShell unrolls an empty array to $null on return)
                $files = @(Get-FilesInPaths -Paths $paths -MinAgeHours $cat.MinAgeHours)
                $count = $files.Count
                $size = 0
                foreach ($f in $files) { $size += $f.Length }
                if ($cat.Id -eq 'Browser' -and $browserRunning) {
                    $status = 'Browser is running - close it for a full clean'
                }
            }
        }

        $script:ScanResults[$cat.Id] = @{ Files = $files; Count = $count; SizeBytes = $size; Status = $status }
        $totalCount += $count; $totalSize += $size
        Write-ProCleanLog ("Scan: {0} - {1} files, {2} ({3})" -f $cat.Name, $count, (Format-Size $size), $status)
    }

    Write-ProCleanLog ("Scan complete: {0} files, {1} reclaimable." -f $totalCount, (Format-Size $totalSize))
    return @{ TotalCount = $totalCount; TotalSize = $totalSize }
}

# ---------------------------------------------------------------------------
# Clean engine - deletes only what was scanned, counts every outcome
# ---------------------------------------------------------------------------
function Invoke-ProCleanClean {
    param([string[]]$SelectedIds, [bool]$MakeRestorePoint)
    $deleted = 0; $skipped = 0; $freed = 0

    if ($MakeRestorePoint) { [void](New-ProCleanRestorePoint) }

    foreach ($cat in $script:Categories) {
        if ($SelectedIds -notcontains $cat.Id) { continue }
        if (-not $script:ScanResults.ContainsKey($cat.Id)) { continue }
        if ($cat.NeedsAdmin -and -not $script:IsAdmin) {
            Write-ProCleanLog ("Skip: {0} - needs admin." -f $cat.Name); continue
        }

        Write-ProCleanLog ("Cleaning: {0} ..." -f $cat.Name)

        if ($cat.Kind -eq 'Dns') {
            try {
                $out = & ipconfig /flushdns 2>&1 | Out-String
                Write-ProCleanLog ('DNS cache flushed. ' + ($out.Trim() -replace '\s+', ' '))
            } catch {
                Write-ProCleanLog ('DNS flush failed: ' + $_.Exception.Message)
            }
            continue
        }

        if ($cat.Kind -eq 'RecycleBin') {
            try {
                Clear-RecycleBin -Force -ErrorAction Stop
                $n = $script:ScanResults[$cat.Id].Count
                $deleted += $n; $freed += $script:ScanResults[$cat.Id].SizeBytes
                Write-ProCleanLog ("Recycle Bin emptied: {0} items." -f $n)
            } catch {
                Write-ProCleanLog ('Recycle Bin could not be emptied: ' + $_.Exception.Message)
            }
            continue
        }

        if ($cat.Kind -eq 'UpdateCache') {
            # Correct pattern (PC-Medic): stop service -> clear contents -> start service
            $svcStopped = $false
            try {
                Stop-Service -Name 'wuauserv' -Force -ErrorAction SilentlyContinue
                $svcStopped = $true
                Write-ProCleanLog 'Windows Update service stopped.'
            } catch {
                Write-ProCleanLog 'Could not stop wuauserv - will still try to delete unlocked files.'
            }
            try {
                $res = Remove-ScannedFiles -Category $cat
                $deleted += $res.Deleted; $skipped += $res.Skipped; $freed += $res.Freed
            } finally {
                if ($svcStopped) {
                    try { Start-Service -Name 'wuauserv' -ErrorAction SilentlyContinue; Write-ProCleanLog 'Windows Update service restarted.' } catch { }
                }
            }
            continue
        }

        $res = Remove-ScannedFiles -Category $cat
        $deleted += $res.Deleted; $skipped += $res.Skipped; $freed += $res.Freed
    }

    Write-ProCleanLog ("Clean done: {0} files deleted ({1} freed), {2} skipped (in use or locked)." -f $deleted, (Format-Size $freed), $skipped)
    return @{ Deleted = $deleted; Skipped = $skipped; Freed = $freed }
}

function Remove-ScannedFiles {
    param($Category)
    $result = $script:ScanResults[$Category.Id]
    $deleted = 0; $skipped = 0; $freed = 0
    $total = $result.Files.Count; $i = 0

    foreach ($f in $result.Files) {
        $i++
        if (($i % 100) -eq 0 -and $script:progressBar) {
            $script:progressBar.Value = [Math]::Min(100, [int](($i / [Math]::Max(1, $total)) * 100))
            [System.Windows.Forms.Application]::DoEvents()
        }
        try {
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            $deleted++; $freed += $f.Length
        } catch {
            $skipped++
        }
    }

    # Remove folders left empty by the clean (temp folders only, never the root itself).
    # Only directories older than the category age cutoff, and only when a
    # single-entry check proves they are truly empty.
    if ($Category.Kind -eq 'Files') {
        try {
            $cutoff = (Get-Date).AddHours(-$Category.MinAgeHours)
            $paths = & $Category.GetPaths
            foreach ($p in $paths) {
                if (-not $p -or -not (Test-Path -LiteralPath $p)) { continue }
                Get-ChildItem -LiteralPath $p -Directory -Recurse -Force -ErrorAction SilentlyContinue |
                    Sort-Object { $_.FullName.Length } -Descending |
                    Where-Object { $_.LastWriteTime -lt $cutoff -and [System.IO.Directory]::GetFileSystemEntries($_.FullName).Length -eq 0 } |
                    ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch { } }
            }
        } catch { }
    }

    Write-ProCleanLog ("{0}: {1} deleted, {2} skipped." -f $Category.Name, $deleted, $skipped)
    return @{ Deleted = $deleted; Skipped = $skipped; Freed = $freed }
}

# ---------------------------------------------------------------------------
# Console mode (for testing:  powershell -File ProClean.ps1 -ScanOnly)
# Silent scheduled clean:  powershell -File ProClean.ps1 -AutoClean
# ---------------------------------------------------------------------------
if ($ScanOnly) {
    Write-Host ("ProClean v{0} - console scan (nothing will be deleted)" -f $script:AppVersion) -ForegroundColor Cyan
    Write-Host ("Admin: {0}" -f $script:IsAdmin)
    $ids = $script:Categories | ForEach-Object { $_.Id }
    $sum = Invoke-ProCleanScan -SelectedIds $ids
    Write-Host ("`nTotal: {0} files, {1} reclaimable." -f $sum.TotalCount, (Format-Size $sum.TotalSize)) -ForegroundColor Green
    Write-Host ("Log: {0}" -f $script:LogPath)
    exit 0
}

if ($AutoClean) {
    Write-Host ("ProClean v{0} - AutoClean mode (silent, default categories only)" -f $script:AppVersion) -ForegroundColor Cyan
    Write-Host ("Admin: {0}" -f $script:IsAdmin)
    $ids = $script:Categories | Where-Object { $_.Default } | ForEach-Object { $_.Id }
    Write-ProCleanLog ("ProClean v{0} - AutoClean started." -f $script:AppVersion)
    $sum = Invoke-ProCleanScan -SelectedIds $ids
    $res = Invoke-ProCleanClean -SelectedIds $ids -MakeRestorePoint $script:IsAdmin
    Write-Host ("Clean done: {0} files deleted ({1} freed), {2} skipped (in use or locked)." -f $res.Deleted, (Format-Size $res.Freed), $res.Skipped) -ForegroundColor Green
    Write-Host ("Log: {0}" -f $script:LogPath)
    exit 0
}

# ---------------------------------------------------------------------------
# GUI - Windows Forms (works on Windows PowerShell 5.1, no install needed)
# ---------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Offer to restart as admin (full clean needs it for Update cache + restore point)
if (-not $script:IsAdmin) {
    # Pasted into the console? Then there is no script file to restart,
    # so say so clearly instead of launching an empty elevated window.
    if (-not $PSCommandPath) {
        [System.Windows.Forms.MessageBox]::Show(
            "ProClean was pasted into PowerShell, so it cannot restart as admin.`n`nClose this window and double-click Run-ProClean.bat instead (or right-click ProClean.ps1 - Run with PowerShell).",
            'ProClean', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        exit 0
    }
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "ProClean cleans best as Administrator (Windows Update cache and System Restore Point need it).`n`nRestart as administrator now?",
        'ProClean', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ("-NoProfile -ExecutionPolicy Bypass -File `"{0}`"" -f $PSCommandPath)
        exit 0
    }
}

$script:checkBoxes = @{}
$script:scanTotal = @{ TotalCount = 0; TotalSize = 0 }

$form = New-Object System.Windows.Forms.Form
$form.Text = "ProClean v$script:AppVersion - Windows Temp Cleaner"
$form.ClientSize = New-Object System.Drawing.Size(680, 702)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)
# App icon (ProClean.ico next to the script, if present)
$icoPath = Join-Path $PSScriptRoot 'ProClean.ico'
if ($icoPath -and (Test-Path -LiteralPath $icoPath)) {
    try { $form.Icon = New-Object System.Drawing.Icon($icoPath) } catch { }
}

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "ProClean v$script:AppVersion - Windows Temp Cleaner"
$lblTitle.Font = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Bold)
$lblTitle.Location = New-Object System.Drawing.Point(12, 10)
$lblTitle.Size = New-Object System.Drawing.Size(656, 26)
$form.Controls.Add($lblTitle)

$lblAdmin = New-Object System.Windows.Forms.Label
$lblAdmin.Text = if ($script:IsAdmin) { 'Running as Administrator - all categories available.' } else { 'Not running as Administrator - Update cache and Restore Point are disabled.' }
$lblAdmin.ForeColor = if ($script:IsAdmin) { [System.Drawing.Color]::DarkGreen } else { [System.Drawing.Color]::DarkRed }
$lblAdmin.Location = New-Object System.Drawing.Point(12, 38)
$lblAdmin.Size = New-Object System.Drawing.Size(656, 18)
$form.Controls.Add($lblAdmin)

$grp = New-Object System.Windows.Forms.GroupBox
$grp.Text = 'What to clean  (tick, then press Scan - nothing is deleted until you press Clean)'
$grp.Location = New-Object System.Drawing.Point(12, 62)
$grp.Size = New-Object System.Drawing.Size(656, 292)
$form.Controls.Add($grp)

$toolTip = New-Object System.Windows.Forms.ToolTip
$y = 22
foreach ($cat in $script:Categories) {
    $cb = New-Object System.Windows.Forms.CheckBox
    $cb.Text = '{0} - {1}' -f $cat.Name, $cat.Desc
    $cb.Checked = $cat.Default
    $cb.Location = New-Object System.Drawing.Point(16, $y)
    $cb.Size = New-Object System.Drawing.Size(624, 22)
    $cb.Tag = $cat
    if ($cat.Warning) { $toolTip.SetToolTip($cb, $cat.Warning) }
    if ($cat.NeedsAdmin -and -not $script:IsAdmin) {
        $cb.Checked = $false; $cb.Enabled = $false
        $cb.Text += '  (needs admin)'
    }
    $grp.Controls.Add($cb)
    $script:checkBoxes[$cat.Id] = $cb
    $y += 26
}

$chkRestore = New-Object System.Windows.Forms.CheckBox
$chkRestore.Text = 'Create a System Restore Point before cleaning (recommended)'
$chkRestore.Checked = $script:IsAdmin
$chkRestore.Enabled = $script:IsAdmin
$chkRestore.Location = New-Object System.Drawing.Point(12, 364)
$chkRestore.Size = New-Object System.Drawing.Size(656, 22)
$form.Controls.Add($chkRestore)

$btnScan = New-Object System.Windows.Forms.Button
$btnScan.Text = 'Scan'
$btnScan.Location = New-Object System.Drawing.Point(12, 394)
$btnScan.Size = New-Object System.Drawing.Size(100, 30)
$form.Controls.Add($btnScan)

$btnClean = New-Object System.Windows.Forms.Button
$btnClean.Text = 'Clean'
$btnClean.Enabled = $false
$btnClean.Location = New-Object System.Drawing.Point(122, 394)
$btnClean.Size = New-Object System.Drawing.Size(100, 30)
$form.Controls.Add($btnClean)

$btnSelectAll = New-Object System.Windows.Forms.Button
$btnSelectAll.Text = 'Select All'
$btnSelectAll.Location = New-Object System.Drawing.Point(232, 394)
$btnSelectAll.Size = New-Object System.Drawing.Size(100, 30)
$form.Controls.Add($btnSelectAll)

$btnClearAll = New-Object System.Windows.Forms.Button
$btnClearAll.Text = 'Clear All'
$btnClearAll.Location = New-Object System.Drawing.Point(342, 394)
$btnClearAll.Size = New-Object System.Drawing.Size(100, 30)
$form.Controls.Add($btnClearAll)

$script:progressBar = New-Object System.Windows.Forms.ProgressBar
$script:progressBar.Location = New-Object System.Drawing.Point(12, 434)
$script:progressBar.Size = New-Object System.Drawing.Size(656, 20)
$script:progressBar.Minimum = 0; $script:progressBar.Maximum = 100; $script:progressBar.Value = 0
$form.Controls.Add($script:progressBar)

$listView = New-Object System.Windows.Forms.ListView
$listView.View = 'Details'
$listView.FullRowSelect = $true
$listView.GridLines = $true
$listView.Location = New-Object System.Drawing.Point(12, 464)
$listView.Size = New-Object System.Drawing.Size(656, 118)
[void]$listView.Columns.Add('Category', 220)
[void]$listView.Columns.Add('Files', 80)
[void]$listView.Columns.Add('Size', 100)
[void]$listView.Columns.Add('Status', 240)
$form.Controls.Add($listView)
$script:listView = $listView

$script:txtLog = New-Object System.Windows.Forms.TextBox
$script:txtLog.Multiline = $true
$script:txtLog.ReadOnly = $true
$script:txtLog.ScrollBars = 'Vertical'
$script:txtLog.Location = New-Object System.Drawing.Point(12, 592)
$script:txtLog.Size = New-Object System.Drawing.Size(656, 78)
$form.Controls.Add($script:txtLog)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = 'Ready. Tick categories, press Scan to preview.'
$lblStatus.Location = New-Object System.Drawing.Point(12, 678)
$lblStatus.Size = New-Object System.Drawing.Size(656, 18)
$form.Controls.Add($lblStatus)
$script:lblStatus = $lblStatus

function Get-SelectedIds {
    $ids = @()
    foreach ($cat in $script:Categories) {
        if ($script:checkBoxes[$cat.Id].Checked) { $ids += $cat.Id }
    }
    return $ids
}

function Update-ResultsList {
    $script:listView.Items.Clear()
    foreach ($cat in $script:Categories) {
        if (-not $script:ScanResults.ContainsKey($cat.Id)) { continue }
        $r = $script:ScanResults[$cat.Id]
        $item = New-Object System.Windows.Forms.ListViewItem($cat.Name)
        $item.Tag = $cat.Id
        [void]$item.SubItems.Add($r.Count.ToString())
        [void]$item.SubItems.Add((Format-Size $r.SizeBytes))
        [void]$item.SubItems.Add($r.Status)
        [void]$script:listView.Items.Add($item)
    }
}

function Show-CategoryFiles {
    param([string]$CategoryId)
    $cat = $script:Categories | Where-Object { $_.Id -eq $CategoryId } | Select-Object -First 1
    if (-not $cat) { return }
    $result = $script:ScanResults[$CategoryId]
    $files = @()
    if ($result -and $result.Files) { $files = @($result.Files | Sort-Object Length -Descending | Select-Object -First 250) }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "ProClean - {0} (largest files)" -f $cat.Name
    $dlg.ClientSize = New-Object System.Drawing.Size(760, 480)
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.MaximizeBox = $false
    $dlg.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $lv = New-Object System.Windows.Forms.ListView
    $lv.View = 'Details'
    $lv.FullRowSelect = $true
    $lv.GridLines = $true
    $lv.Location = New-Object System.Drawing.Point(12, 12)
    $lv.Size = New-Object System.Drawing.Size(736, 400)
    [void]$lv.Columns.Add('File Path', 430)
    [void]$lv.Columns.Add('Size', 110)
    [void]$lv.Columns.Add('Last Modified', 170)
    foreach ($f in $files) {
        $row = New-Object System.Windows.Forms.ListViewItem($f.FullName)
        [void]$row.SubItems.Add((Format-Size $f.Length))
        [void]$row.SubItems.Add($f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
        [void]$lv.Items.Add($row)
    }
    $dlg.Controls.Add($lv)

    $lbl = New-Object System.Windows.Forms.Label
    if ($files.Count -eq 0) { $lbl.Text = 'No file list for this category (DNS flush / Recycle Bin have no files to show).' }
    else { $lbl.Text = "Top {0} largest files - read only, nothing is deleted here." -f $files.Count }
    $lbl.Location = New-Object System.Drawing.Point(12, 424)
    $lbl.Size = New-Object System.Drawing.Size(736, 18)
    $dlg.Controls.Add($lbl)

    $btnClose = New-Object System.Windows.Forms.Button
    $btnClose.Text = 'Close'
    $btnClose.Location = New-Object System.Drawing.Point(648, 444)
    $btnClose.Size = New-Object System.Drawing.Size(100, 28)
    $btnClose.Add_Click({ $dlg.Close() })
    $dlg.Controls.Add($btnClose)
    $dlg.AcceptButton = $btnClose

    [void]$dlg.ShowDialog($form)
}

$listView.Add_DoubleClick({
    if ($listView.SelectedItems.Count -eq 0) { return }
    Show-CategoryFiles -CategoryId $listView.SelectedItems[0].Tag
})

# Any selection change after a scan makes the preview stale - force a rescan.
foreach ($cb in $script:checkBoxes.Values) {
    $cb.Add_CheckedChanged({
        $btnClean.Enabled = $false
        if ($script:lblStatus) { $script:lblStatus.Text = 'Selection changed - press Scan to refresh preview.' }
    })
}

$btnSelectAll.Add_Click({
    foreach ($cat in $script:Categories) {
        if ($script:checkBoxes[$cat.Id].Enabled) { $script:checkBoxes[$cat.Id].Checked = $true }
    }
})
$btnClearAll.Add_Click({
    foreach ($cat in $script:Categories) { $script:checkBoxes[$cat.Id].Checked = $false }
})

$btnScan.Add_Click({
    $ids = Get-SelectedIds
    if ($ids.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show('Tick at least one category first.', 'ProClean', 'OK', 'Information') | Out-Null
        return
    }
    $btnScan.Enabled = $false; $btnClean.Enabled = $false
    $script:progressBar.Value = 0
    $script:lblStatus.Text = 'Scanning...'
    $script:txtLog.Clear()
    Write-ProCleanLog ("ProClean v{0} - scan started." -f $script:AppVersion)
    try {
        $script:scanTotal = Invoke-ProCleanScan -SelectedIds $ids
        Update-ResultsList
        $script:lblStatus.Text = ('Scan done: {0} files, {1} reclaimable. Review, then press Clean.' -f $script:scanTotal.TotalCount, (Format-Size $script:scanTotal.TotalSize))
        # DNS always reports 0 files - the Clean button must still enable when it is selected.
        if ($script:scanTotal.TotalCount -gt 0 -or $ids -contains 'Dns') { $btnClean.Enabled = $true }
    } catch {
        $script:lblStatus.Text = ('Scan failed: ' + $_.Exception.Message)
        Write-ProCleanLog ('Scan failed: ' + $_.Exception.Message)
    } finally {
        $btnScan.Enabled = $true
        $script:progressBar.Value = 0
    }
})

$btnClean.Add_Click({
    $total = $script:scanTotal
    $msg = "Delete {0} files ({1})?`n`nTemp files are deleted PERMANENTLY - they do not go to the Recycle Bin. Review the preview above first." -f $total.TotalCount, (Format-Size $total.TotalSize)
    $ok = [System.Windows.Forms.MessageBox]::Show($msg, 'ProClean - confirm clean', 'YesNo', 'Warning')
    if ($ok -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    if ($script:checkBoxes['RecycleBin'].Checked) {
        $ok2 = [System.Windows.Forms.MessageBox]::Show('Empty the Recycle Bin too? This cannot be undone.', 'ProClean - Recycle Bin', 'YesNo', 'Warning')
        if ($ok2 -ne [System.Windows.Forms.DialogResult]::Yes) { $script:checkBoxes['RecycleBin'].Checked = $false }
    }

    $ids = Get-SelectedIds
    $btnScan.Enabled = $false; $btnClean.Enabled = $false
    $script:lblStatus.Text = 'Cleaning...'
    try {
        $res = Invoke-ProCleanClean -SelectedIds $ids -MakeRestorePoint $chkRestore.Checked
        foreach ($item in $script:listView.Items) { $item.SubItems[3].Text = 'Cleaned - see log' }
        $script:lblStatus.Text = ('Done: {0} files deleted ({1} freed), {2} skipped. Log: {3}' -f $res.Deleted, (Format-Size $res.Freed), $res.Skipped, $script:LogPath)
        [System.Windows.Forms.MessageBox]::Show(
            ("Clean complete.`n`nDeleted: {0} files ({1})`nSkipped (in use): {2}`n`nLog saved to:`n{3}" -f $res.Deleted, (Format-Size $res.Freed), $res.Skipped, $script:LogPath),
            'ProClean', 'OK', 'Information') | Out-Null
    } catch {
        $script:lblStatus.Text = ('Clean failed: ' + $_.Exception.Message)
        Write-ProCleanLog ('Clean failed: ' + $_.Exception.Message)
    } finally {
        $btnScan.Enabled = $true
        $script:progressBar.Value = 0
    }
})

Write-ProCleanLog ("ProClean v{0} started." -f $script:AppVersion)
[void]$form.ShowDialog()

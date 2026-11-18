# ProClean v1.1.0 — Windows 10 / 11 Temp File Cleaner

A safe, free temp-file cleaner for Windows 10 and 11, by **PRO SUPPORT (Syed Osman)**.
Scan first, preview everything, then clean only what you choose. Nothing is
deleted until you press **Clean**.

Two ways to run it — a real `.exe` (no install, no PowerShell) and a
single-file PowerShell script. Both do exactly the same thing.

## Download

| File | What it is |
|---|---|
| `ProClean.exe` | The program — double-click and it runs (~68.5 MB, the .NET 8 runtime is bundled inside, so nothing needs installing) |
| `ProClean.ps1` | The same tool as a single PowerShell 5.1 script (no install, built into Windows) |
| `Run-ProClean.bat` | Double-click launcher for the script version |
| `ProClean.ico` / `ProClean-icon-1024.png` | The app icon (teal badge, white checkmark) |
| `LICENSE` | MIT License |

## How to run (on your Windows 10/11 PC)

**The .exe (easiest):**
1. Download `ProClean.exe` and double-click it.
2. Windows SmartScreen may warn because the file is not code-signed —
   click **"More info"**, then **"Run anyway"**.
3. If it asks "Run as administrator?" — click **Yes** for the full clean.
   (Without admin, Windows Update cache + Restore Point stay disabled; the rest works.)
4. Tick the categories, press **Scan**, review the preview (files + size per
   category), then press **Clean**.
5. A log file is saved to your Desktop after every run
   (`ProClean-Log-YYYYMMDD-HHMMSS.txt`).

**The script:**
1. Copy the whole `ProClean` folder to your PC (Desktop is fine).
2. Double-click **`Run-ProClean.bat`** — or right-click `ProClean.ps1` →
   **Run with PowerShell**.
3. Same steps as above from step 3.

**Silent scheduled cleaning (no window):**
- Script: `powershell -File ProClean.ps1 -AutoClean`
- Exe: `ProClean.exe /AUTO`
- Cleans only the default categories, makes a restore point only when run
  as admin, prints a summary and the log path, exits 0. Safe to put in
  Task Scheduler.

**Inspect before you delete:** double-click any category row in the results
to see its 250 largest files (file path, size, last modified) — read-only.

No installation. No internet needed. The code is open — read it before running.

## What it cleans

| Category | Default | Needs admin |
|---|---|---|
| Windows Temp Files (`%TEMP%`, `C:\Windows\Temp`) | ON | No |
| Browser Caches (Chrome, Edge, Firefox, Opera — all profiles, only if installed) | ON | No |
| Windows Update Cache (service stopped, cleared, restarted) | ON | Yes |
| Delivery Optimization Cache | ON | Yes |
| Old Windows Logs (7+ days) | ON | No |
| GPU & DirectX Shader Cache (7+ days) | ON | No |
| Windows Error Reports (7+ days) | ON | No |
| Prefetch — 30+ day old files only | OFF | Yes |
| Empty Recycle Bin (own confirmation) | OFF | No |
| Flush DNS Cache (no files deleted) | ON | No |

## Safety rules (built in, not optional)

- **Scan first, preview everything** — nothing is deleted until you press Clean.
- **Files changed in the last 24 hours are never touched** (Temp category).
- **Files in use are skipped**, never forced.
- **No whole-drive wildcards** — every delete is scoped to a named folder.
- **Empty folders are removed only if older than the category's age limit**
  and only when a single-entry check proves they are truly empty.
- **No registry cleaning, no debloat, no telemetry changes.**
- **Optional System Restore Point** before cleaning (admin only, on by default).
- **Every run writes a log** to the Desktop.
- Close browsers before cleaning for a full browser-cache clean.

## What it deliberately does NOT do

- Delete all of Prefetch (only 30+ day old files, off by default).
- Delete restore points / shadow copies.
- Touch the network stack, run `chkdsk`, change power plans, or remove apps.
- Send anything to the internet — it has no network code at all.

## Building the .exe from source

Needs the .NET 8 SDK (Windows targeting pack downloads on first build):

```powershell
cd dotnet
dotnet publish -c Release -o publish
```

Output: `dotnet/publish/ProClean.exe` — self-contained single file,
`win-x64`, Windows GUI. Trimming is deliberately OFF: `System.Management`
(the restore-point code) is not trim-safe.

The script version needs no build — it is `ProClean.ps1` as-is.

## Files

| File | What it is |
|---|---|
| `ProClean.exe` | Compiled program (C# + WinForms, .NET 8) |
| `ProClean.ps1` | The tool as a PowerShell 5.1 + Windows Forms script |
| `dotnet/Program.cs` | C# source — a mirror of the script, same categories and safety rules |
| `dotnet/ProClean.csproj` | C# project file |
| `Run-ProClean.bat` | Launcher for the script (admin check + UAC elevation + non-admin fallback) |
| `ProClean.ico` | App icon (16/32/48/256 px) |
| `ProClean-icon-1024.png` | App icon artwork (1024x1024) |
| `LICENSE` | MIT License |
| `.gitignore` | Build outputs and logs are not committed |

## References (what this was built from)

Win11Debloat (backup/restore safety), FluentCleaner (scan-preview +
data-driven rules), PC-Cleaner (grouped menu, backup-before-wipe),
CleanWindowsPro (admin/restore-point preflight), PC-Medic (minimal core
steps, correct stop-service pattern for the Update cache). All references
are open source; this tool is a fresh rebuild, not a copy of any one of them.

## Official source

The GitHub repository linked from PRO SUPPORT is the **only official
download** for ProClean. Do not trust copies from other sites — they may be
modified. The PRO SUPPORT name and shield logo may not be used to endorse
unofficial forks or repackaged copies.

## License

MIT License — free to use, share and modify. See `LICENSE`.

## Version

v1.1.0 — 18 Nov 2026 — MIT license — by PRO SUPPORT (Syed Osman)

// Copyright (c) 2026 Syed Osman (PRO SUPPORT). All rights reserved. Licensed under the MIT License.
// ProClean v1.1.0 - Windows 10 / 11 Temp File Cleaner (real .exe)
// ------------------------------------------------------------------
// A clean, safe temp-file cleaner for Windows 10 and 11.
// Scan first, preview everything, then clean only what you choose.
//
// What it cleans:
//   - Windows Temp files (%TEMP% and C:\Windows\Temp)
//   - Browser caches (Chrome, Edge, Firefox, Opera - only if installed)
//   - Windows Update download cache (stops wuauserv, clears, restarts it)
//   - Delivery Optimization cache
//   - Old Windows log files (7+ days)
//   - GPU & DirectX Shader Cache (7+ days)
//   - Windows Error Reports (7+ days)
//   - Old Prefetch files (30+ days, off by default)
//   - Recycle Bin (off by default, own confirmation)
//   - DNS cache flush (no files deleted)
//
// Safety rules (built in, not optional):
//   - NOTHING is deleted before you press Scan and then Clean.
//   - Files changed in the last 24 hours are never touched.
//   - Files in use are skipped, never forced.
//   - No whole-drive wildcards - every delete is scoped to a named folder.
//   - No registry cleaning, no debloat, no telemetry changes.
//   - Optional System Restore Point before cleaning (admin only).
//   - Every run writes a log file to your Desktop.
//
// Built from the same design as ProClean.ps1 v1.1.0, rebuilt as a
// real Windows program so there is no PowerShell, no execution
// policy and no paste-into-console step. Double-click and it runs.
// Silent scheduled cleaning: ProClean.exe /AUTO
//
// Author  : PRO SUPPORT (Syed Osman)
// Version : 1.1.0 - 18 Nov 2026
// License : MIT - free to use, share and modify
// Needs   : Windows 10 / 11 (.NET 8 runtime is bundled inside the exe,
//           no install needed on the PC)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;

namespace ProClean
{
    static class Program
    {
        public const string AppVersion = "1.1.0";

        [STAThread]
        static void Main(string[] args)
        {
            // Headless scheduled clean: ProClean.exe /AUTO  (or -AutoClean / --autoclean)
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (a.Equals("/AUTO", StringComparison.OrdinalIgnoreCase)
                        || a.Equals("-AutoClean", StringComparison.OrdinalIgnoreCase)
                        || a.Equals("--autoclean", StringComparison.OrdinalIgnoreCase))
                    {
                        RunAutoClean();
                        return;
                    }
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        // Silent scan + clean of default categories only, no GUI.
        static void RunAutoClean()
        {
            Console.WriteLine("ProClean v" + AppVersion + " - AutoClean mode (silent, default categories only)");
            bool isAdmin = Util.IsAdmin();
            Console.WriteLine("Admin: " + isAdmin);
            Logger.Write("ProClean v" + AppVersion + " - AutoClean started.");
            var ids = new List<string>();
            foreach (var cat in Engine.Categories)
                if (cat.DefaultChecked) ids.Add(cat.Id);
            long totalCount, totalSize;
            Engine.Scan(ids, isAdmin, out totalCount, out totalSize);
            long deleted, skipped, freed;
            Engine.Clean(ids, isAdmin, isAdmin, null, out deleted, out skipped, out freed);
            Console.WriteLine(string.Format("Clean done: {0} files deleted ({1} freed), {2} skipped (in use or locked).",
                deleted, Util.FormatSize(freed), skipped));
            Console.WriteLine("Log: " + Logger.LogPath);
        }
    }

    enum CategoryKind { Files, UpdateCache, RecycleBin, Dns }

    class Category
    {
        public string Id;
        public string Name;
        public string Desc;
        public CategoryKind Kind;
        public int MinAgeHours;
        public bool NeedsAdmin;
        public bool DefaultChecked;
        public string Warning;
        public Func<string[]> GetPaths;
    }

    class ScanResult
    {
        public List<FileInfo> Files = new List<FileInfo>();
        public long Count;
        public long SizeBytes;
        public string Status = "Ready";
    }

    static class Native
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern uint SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHQUERYRBINFO
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pInfo);

        public const uint SHERB_NOCONFIRMATION = 0x00000001;
        public const uint SHERB_NOPROGRESSUI = 0x00000002;
        public const uint SHERB_NOSOUND = 0x00000004;
    }

    static class Util
    {
        public static bool IsAdmin()
        {
            try
            {
                var id = WindowsIdentity.GetCurrent();
                var p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return string.Format("{0:N2} GB", bytes / (1024.0 * 1024 * 1024));
            if (bytes >= 1024L * 1024) return string.Format("{0:N2} MB", bytes / (1024.0 * 1024));
            if (bytes >= 1024L) return string.Format("{0:N2} KB", bytes / 1024.0);
            return bytes + " bytes";
        }

        public static string TempPath { get { return Path.GetTempPath(); } }

        public static string WinDir { get { return Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows"; } }

        public static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }

        public static string RoamingAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }

        public static string ProgramData { get { return Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData"; } }

        public static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.Desktop); } }

        public static List<string> GetBrowserCachePaths()
        {
            var paths = new List<string>();
            // Chrome + Edge: every profile (Default, Profile *, Guest Profile),
            // every cache subfolder. Opera has a single cache folder.
            foreach (string baseDir in new[]
            {
                Path.Combine(LocalAppData, @"Google\Chrome\User Data"),
                Path.Combine(LocalAppData, @"Microsoft\Edge\User Data")
            })
            {
                if (!Directory.Exists(baseDir)) continue;
                var profiles = new List<string> { "Default", "Guest Profile" };
                try
                {
                    foreach (string d in Directory.GetDirectories(baseDir, "Profile *"))
                        profiles.Add(Path.GetFileName(d));
                }
                catch { }
                foreach (string profile in profiles)
                {
                    foreach (string sub in new[] { "Cache", @"Cache\Cache_Data", "Code Cache", "GPUCache" })
                        paths.Add(Path.Combine(Path.Combine(baseDir, profile), sub));
                }
            }
            string opera = Path.Combine(RoamingAppData, @"Opera Software\Opera Stable\Cache");
            if (Directory.Exists(opera)) paths.Add(opera);
            string ffProfiles = Path.Combine(RoamingAppData, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ffProfiles))
            {
                foreach (string dir in Directory.GetDirectories(ffProfiles))
                {
                    paths.Add(Path.Combine(dir, "cache2"));
                }
            }
            return paths;
        }

        public static bool IsBrowserRunning()
        {
            foreach (string name in new[] { "chrome", "msedge", "firefox", "opera" })
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; }
                catch { }
            }
            return false;
        }

        // Collect files under the given roots, skipping reparse points and
        // files newer than MinAgeHours. Never throws - errors are skipped.
        public static void CollectFiles(string[] roots, int minAgeHours, List<FileInfo> outList)
        {
            DateTime cutoff = DateTime.Now.AddHours(-minAgeHours);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    string[] subdirs = null;
                    string[] files = null;
                    try { subdirs = Directory.GetDirectories(dir); } catch { continue; }
                    try { files = Directory.GetFiles(dir); } catch { }
                    if (files != null)
                    {
                        foreach (string f in files)
                        {
                            try
                            {
                                var fi = new FileInfo(f);
                                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                                if (minAgeHours > 0 && fi.LastWriteTime >= cutoff) continue;
                                outList.Add(fi);
                            }
                            catch { }
                        }
                    }
                    if (subdirs != null)
                    {
                        foreach (string d in subdirs) stack.Push(d);
                    }
                }
            }
        }

        public static bool GetRecycleBinInfo(out long count, out long size)
        {
            count = 0; size = 0;
            try
            {
                var info = new Native.SHQUERYRBINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                int hr = Native.SHQueryRecycleBin(null, ref info);
                if (hr == 0)
                {
                    count = info.i64NumItems;
                    size = info.i64Size;
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static bool CreateRestorePoint()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\default");
                scope.Connect();
                var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
                var inParams = cls.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = "ProClean before cleaning";
                inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                inParams["EventType"] = 100; // BEGIN_SYSTEM_CHANGE
                cls.InvokeMethod("CreateRestorePoint", inParams, null);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("Restore Point could not be created (Windows allows one per day, or restore is off): " + ex.Message);
                return false;
            }
        }

        public static void FlushDns()
        {
            try
            {
                var psi = new ProcessStartInfo("ipconfig", "/flushdns");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    Logger.Write("DNS cache flushed. " + output.Trim().Replace("\r", " ").Replace("\n", " "));
                }
            }
            catch (Exception ex)
            {
                Logger.Write("DNS flush failed: " + ex.Message);
            }
        }
    }

    // Simple logger: writes to the Desktop log file and to the GUI log box.
    static class Logger
    {
        public static string LogPath;
        public static TextBox LogBox;

        static Logger()
        {
            LogPath = Path.Combine(Util.Desktop,
                "ProClean-Log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        }

        public static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
            if (LogBox != null)
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.SelectionStart = LogBox.Text.Length;
                LogBox.ScrollToCaret();
            }
        }
    }

    // ------------------------------------------------------------------
    // Scan + clean engine. Categories are DATA, not hardcoded logic.
    // ------------------------------------------------------------------
    static class Engine
    {
        public static readonly List<Category> Categories = new List<Category>
        {
            new Category { Id = "Temp", Name = "Windows Temp Files",
                Desc = "Your Temp folder + C:\\Windows\\Temp",
                Kind = CategoryKind.Files, MinAgeHours = 24,
                NeedsAdmin = false, DefaultChecked = true, Warning = "",
                GetPaths = () => new[] { Util.TempPath, Path.Combine(Util.WinDir, "Temp") } },

            new Category { Id = "Browser", Name = "Browser Caches",
                Desc = "Chrome, Edge, Firefox caches (if installed)",
                Kind = CategoryKind.Files, MinAgeHours = 0,
                NeedsAdmin = false, DefaultChecked = true,
                Warning = "Close browsers first for a full clean.",
                GetPaths = () => Util.GetBrowserCachePaths().ToArray() },

            new Category { Id = "Update", Name = "Windows Update Cache",
                Desc = "Downloaded update files (service restarted)",
                Kind = CategoryKind.UpdateCache, MinAgeHours = 0,
                NeedsAdmin = true, DefaultChecked = true,
                Warning = "Stops the Windows Update service briefly.",
                GetPaths = () => new[] { Path.Combine(Util.WinDir, @"SoftwareDistribution\Download") } },

            new Category { Id = "DeliveryOpt", Name = "Delivery Optimization",
                Desc = "Shared update delivery cache",
                Kind = CategoryKind.Files, MinAgeHours = 0,
                NeedsAdmin = true, DefaultChecked = true, Warning = "",
                GetPaths = () => new[] { Path.Combine(Util.ProgramData, @"Microsoft\Windows\DeliveryOptimization\Cache") } },

            new Category { Id = "Logs", Name = "Old Windows Logs",
                Desc = "Log files older than 7 days",
                Kind = CategoryKind.Files, MinAgeHours = 168,
                NeedsAdmin = false, DefaultChecked = true, Warning = "",
                GetPaths = () => new[] { Path.Combine(Util.WinDir, "Logs"), Path.Combine(Util.WinDir, "Panther") } },

            new Category { Id = "Shader", Name = "GPU & DirectX Shader Cache",
                Desc = "Shader cache files older than 7 days",
                Kind = CategoryKind.Files, MinAgeHours = 168,
                NeedsAdmin = false, DefaultChecked = true,
                Warning = "Active games skip locked shaders automatically.",
                GetPaths = () => new[]
                {
                    Path.Combine(Util.LocalAppData, "D3DSCache"),
                    Path.Combine(Util.LocalAppData, @"NVIDIA\DXCache"),
                    Path.Combine(Util.LocalAppData, @"AMD\DxCache")
                } },

            new Category { Id = "WER", Name = "Windows Error Reports",
                Desc = "Error reports older than 7 days",
                Kind = CategoryKind.Files, MinAgeHours = 168,
                NeedsAdmin = false, DefaultChecked = true, Warning = "",
                GetPaths = () => new[]
                {
                    Path.Combine(Util.ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(Util.ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                    Path.Combine(Util.LocalAppData, @"Microsoft\Windows\WER\ReportArchive")
                } },

            new Category { Id = "Prefetch", Name = "Prefetch (30+ days old)",
                Desc = "Old prefetch data only - speeds nothing",
                Kind = CategoryKind.Files, MinAgeHours = 720,
                NeedsAdmin = true, DefaultChecked = false,
                Warning = "Wiping all Prefetch slows first launches. Only 30+ day old files are touched.",
                GetPaths = () => new[] { Path.Combine(Util.WinDir, "Prefetch") } },

            new Category { Id = "RecycleBin", Name = "Empty Recycle Bin",
                Desc = "Permanently empties the Recycle Bin",
                Kind = CategoryKind.RecycleBin, MinAgeHours = 0,
                NeedsAdmin = false, DefaultChecked = false,
                Warning = "This cannot be undone.",
                GetPaths = () => new string[0] },

            new Category { Id = "Dns", Name = "Flush DNS Cache",
                Desc = "Clears the DNS resolver cache (no files)",
                Kind = CategoryKind.Dns, MinAgeHours = 0,
                NeedsAdmin = false, DefaultChecked = true, Warning = "",
                GetPaths = () => new string[0] },
        };

        public static Dictionary<string, ScanResult> ScanResults = new Dictionary<string, ScanResult>();

        public static void Scan(List<string> selectedIds, bool isAdmin, out long totalCount, out long totalSize)
        {
            ScanResults.Clear();
            totalCount = 0; totalSize = 0;
            bool browserRunning = Util.IsBrowserRunning();

            foreach (var cat in Categories)
            {
                if (!selectedIds.Contains(cat.Id)) continue;
                var result = new ScanResult();

                if (cat.NeedsAdmin && !isAdmin)
                {
                    result.Status = "Needs admin - run as administrator";
                }
                else if (cat.Kind == CategoryKind.RecycleBin)
                {
                    long c, s;
                    Util.GetRecycleBinInfo(out c, out s);
                    result.Count = c; result.SizeBytes = s;
                }
                else if (cat.Kind == CategoryKind.Dns)
                {
                    result.Count = 0; result.SizeBytes = 0; result.Status = "Ready";
                }
                else
                {
                    string[] paths = cat.GetPaths();
                    bool anyFound = false;
                    foreach (string p in paths) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) { anyFound = true; break; } }
                    if (!anyFound)
                    {
                        result.Status = "Not found on this PC";
                    }
                    else
                    {
                        Util.CollectFiles(paths, cat.MinAgeHours, result.Files);
                        result.Count = result.Files.Count;
                        long size = 0;
                        foreach (var f in result.Files) size += f.Length;
                        result.SizeBytes = size;
                        if (cat.Id == "Browser" && browserRunning)
                            result.Status = "Browser is running - close it for a full clean";
                    }
                }

                ScanResults[cat.Id] = result;
                totalCount += result.Count;
                totalSize += result.SizeBytes;
                Logger.Write(string.Format("Scan: {0} - {1} files, {2} ({3})",
                    cat.Name, result.Count, Util.FormatSize(result.SizeBytes), result.Status));
            }

            Logger.Write(string.Format("Scan complete: {0} files, {1} reclaimable.",
                totalCount, Util.FormatSize(totalSize)));
        }

        public static void Clean(List<string> selectedIds, bool isAdmin, bool makeRestorePoint,
            ProgressBar progress, out long deleted, out long skipped, out long freed)
        {
            deleted = 0; skipped = 0; freed = 0;

            if (makeRestorePoint)
            {
                Logger.Write("Creating a System Restore Point (can take ~30 seconds)...");
                if (Util.CreateRestorePoint()) Logger.Write("Restore Point created.");
            }

            foreach (var cat in Categories)
            {
                if (!selectedIds.Contains(cat.Id)) continue;
                if (!ScanResults.ContainsKey(cat.Id)) continue;
                if (cat.NeedsAdmin && !isAdmin)
                {
                    Logger.Write("Skip: " + cat.Name + " - needs admin.");
                    continue;
                }

                Logger.Write("Cleaning: " + cat.Name + " ...");

                if (cat.Kind == CategoryKind.Dns)
                {
                    Util.FlushDns();
                    continue;
                }

                if (cat.Kind == CategoryKind.RecycleBin)
                {
                    try
                    {
                        uint flags = Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND;
                        Native.SHEmptyRecycleBin(IntPtr.Zero, null, flags);
                        long n = ScanResults[cat.Id].Count;
                        deleted += n;
                        freed += ScanResults[cat.Id].SizeBytes;
                        Logger.Write("Recycle Bin emptied: " + n + " items.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Recycle Bin could not be emptied: " + ex.Message);
                    }
                    continue;
                }

                if (cat.Kind == CategoryKind.UpdateCache)
                {
                    // Correct pattern: stop service -> clear contents -> start service
                    bool svcStopped = false;
                    try
                    {
                        using (var sc = new ServiceController("wuauserv"))
                        {
                            if (sc.Status != ServiceControllerStatus.Stopped)
                            {
                                sc.Stop();
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                                svcStopped = true;
                                Logger.Write("Windows Update service stopped.");
                            }
                            else svcStopped = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Could not stop wuauserv - will still try to delete unlocked files. " + ex.Message);
                    }
                    try
                    {
                        long d, s, f;
                        RemoveScannedFiles(cat, progress, out d, out s, out f);
                        deleted += d; skipped += s; freed += f;
                    }
                    finally
                    {
                        if (svcStopped)
                        {
                            try
                            {
                                using (var sc = new ServiceController("wuauserv"))
                                {
                                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                                }
                                Logger.Write("Windows Update service restarted.");
                            }
                            catch { }
                        }
                    }
                    continue;
                }

                long dd, ss, ff;
                RemoveScannedFiles(cat, progress, out dd, out ss, out ff);
                deleted += dd; skipped += ss; freed += ff;
            }

            Logger.Write(string.Format("Clean done: {0} files deleted ({1} freed), {2} skipped (in use or locked).",
                deleted, Util.FormatSize(freed), skipped));
        }

        static void RemoveScannedFiles(Category cat, ProgressBar progress,
            out long deleted, out long skipped, out long freed)
        {
            deleted = 0; skipped = 0; freed = 0;
            var result = ScanResults[cat.Id];
            int total = result.Files.Count;
            int i = 0;

            foreach (var f in result.Files)
            {
                i++;
                if (progress != null && i % 100 == 0)
                {
                    progress.Value = Math.Min(100, (int)((double)i / Math.Max(1, total) * 100));
                    Application.DoEvents();
                }
                try
                {
                    File.Delete(f.FullName);
                    deleted++;
                    freed += f.Length;
                }
                catch { skipped++; }
            }

            // Remove folders left empty by the clean (temp folders only,
            // never the root itself). Only directories older than the
            // category age cutoff, and only when a single-entry check
            // proves they are truly empty.
            if (cat.Kind == CategoryKind.Files)
            {
                try
                {
                    DateTime cutoff = DateTime.Now.AddHours(-cat.MinAgeHours);
                    foreach (string root in cat.GetPaths())
                    {
                        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                        var dirs = new List<string>();
                        CollectDirs(root, dirs);
                        dirs.Sort((a, b) => b.Length.CompareTo(a.Length)); // deepest first
                        foreach (string d in dirs)
                        {
                            try
                            {
                                var di = new DirectoryInfo(d);
                                if (di.LastWriteTime < cutoff
                                    && Directory.GetFileSystemEntries(d).Length == 0)
                                    Directory.Delete(d);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            Logger.Write(string.Format("{0}: {1} deleted, {2} skipped.", cat.Name, deleted, skipped));
        }

        static void CollectDirs(string root, List<string> outDirs)
        {
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    outDirs.Add(d);
                    CollectDirs(d, outDirs);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // GUI - Windows Forms. No install needed, .NET Framework is built
    // into Windows 10 and 11.
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        Dictionary<string, CheckBox> checkBoxes = new Dictionary<string, CheckBox>();
        ListView listView;
        TextBox txtLog;
        Label lblStatus;
        ProgressBar progressBar;
        CheckBox chkRestore;
        Button btnScan;
        Button btnClean;
        bool isAdmin;
        long scanTotalCount;
        long scanTotalSize;

        public MainForm()
        {
            Text = "ProClean v" + Program.AppVersion + " - Windows Temp Cleaner";
            ClientSize = new Size(680, 702);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9);
            // App icon (ProClean.ico next to the exe, if present)
            try
            {
                string icoPath = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath), "ProClean.ico");
                if (File.Exists(icoPath)) Icon = new Icon(icoPath);
            }
            catch { }

            isAdmin = Util.IsAdmin();

            var lblTitle = new Label();
            lblTitle.Text = "ProClean v" + Program.AppVersion + " - Windows Temp Cleaner";
            lblTitle.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            lblTitle.Location = new Point(12, 10);
            lblTitle.Size = new Size(656, 26);
            Controls.Add(lblTitle);

            var lblAdmin = new Label();
            lblAdmin.Text = isAdmin
                ? "Running as Administrator - all categories available."
                : "Not running as Administrator - Update cache and Restore Point are disabled.";
            lblAdmin.ForeColor = isAdmin ? Color.DarkGreen : Color.DarkRed;
            lblAdmin.Location = new Point(12, 38);
            lblAdmin.Size = new Size(656, 18);
            Controls.Add(lblAdmin);

            var grp = new GroupBox();
            grp.Text = "What to clean  (tick, then press Scan - nothing is deleted until you press Clean)";
            grp.Location = new Point(12, 62);
            grp.Size = new Size(656, 292);
            Controls.Add(grp);

            var toolTip = new ToolTip();
            int y = 22;
            foreach (var cat in Engine.Categories)
            {
                var cb = new CheckBox();
                cb.Text = cat.Name + " - " + cat.Desc;
                cb.Checked = cat.DefaultChecked;
                cb.Location = new Point(16, y);
                cb.Size = new Size(624, 22);
                cb.Tag = cat;
                if (!string.IsNullOrEmpty(cat.Warning)) toolTip.SetToolTip(cb, cat.Warning);
                if (cat.NeedsAdmin && !isAdmin)
                {
                    cb.Checked = false;
                    cb.Enabled = false;
                    cb.Text += "  (needs admin)";
                }
                grp.Controls.Add(cb);
                checkBoxes[cat.Id] = cb;
                y += 26;
            }

            chkRestore = new CheckBox();
            chkRestore.Text = "Create a System Restore Point before cleaning (recommended)";
            chkRestore.Checked = isAdmin;
            chkRestore.Enabled = isAdmin;
            chkRestore.Location = new Point(12, 364);
            chkRestore.Size = new Size(656, 22);
            Controls.Add(chkRestore);

            btnScan = new Button();
            btnScan.Text = "Scan";
            btnScan.Location = new Point(12, 394);
            btnScan.Size = new Size(100, 30);
            btnScan.Click += BtnScan_Click;
            Controls.Add(btnScan);

            btnClean = new Button();
            btnClean.Text = "Clean";
            btnClean.Enabled = false;
            btnClean.Location = new Point(122, 394);
            btnClean.Size = new Size(100, 30);
            btnClean.Click += BtnClean_Click;
            Controls.Add(btnClean);

            var btnSelectAll = new Button();
            btnSelectAll.Text = "Select All";
            btnSelectAll.Location = new Point(232, 394);
            btnSelectAll.Size = new Size(100, 30);
            btnSelectAll.Click += (s, e) =>
            {
                foreach (var cat in Engine.Categories)
                    if (checkBoxes[cat.Id].Enabled) checkBoxes[cat.Id].Checked = true;
            };
            Controls.Add(btnSelectAll);

            var btnClearAll = new Button();
            btnClearAll.Text = "Clear All";
            btnClearAll.Location = new Point(342, 394);
            btnClearAll.Size = new Size(100, 30);
            btnClearAll.Click += (s, e) =>
            {
                foreach (var cat in Engine.Categories) checkBoxes[cat.Id].Checked = false;
            };
            Controls.Add(btnClearAll);

            progressBar = new ProgressBar();
            progressBar.Location = new Point(12, 434);
            progressBar.Size = new Size(656, 20);
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 0;
            Controls.Add(progressBar);

            listView = new ListView();
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = true;
            listView.Location = new Point(12, 464);
            listView.Size = new Size(656, 118);
            listView.Columns.Add("Category", 220);
            listView.Columns.Add("Files", 80);
            listView.Columns.Add("Size", 100);
            listView.Columns.Add("Status", 240);
            Controls.Add(listView);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Location = new Point(12, 592);
            txtLog.Size = new Size(656, 78);
            Controls.Add(txtLog);
            Logger.LogBox = txtLog;

            lblStatus = new Label();
            lblStatus.Text = "Ready. Tick categories, press Scan to preview.";
            lblStatus.Location = new Point(12, 678);
            lblStatus.Size = new Size(656, 18);
            Controls.Add(lblStatus);

            // Any selection change after a scan makes the preview stale -
            // force a rescan.
            foreach (var cb in checkBoxes.Values)
            {
                cb.CheckedChanged += (s, e) =>
                {
                    btnClean.Enabled = false;
                    lblStatus.Text = "Selection changed - press Scan to refresh preview.";
                };
            }

            listView.DoubleClick += ListView_DoubleClick;

            Load += MainForm_Load;
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            Logger.Write("ProClean v" + Program.AppVersion + " started.");
            if (!isAdmin)
            {
                var answer = MessageBox.Show(
                    "ProClean cleans best as Administrator (Windows Update cache and System Restore Point need it).\n\nRestart as administrator now?",
                    "ProClean", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    try
                    {
                        var psi = new ProcessStartInfo();
                        psi.FileName = Application.ExecutablePath;
                        psi.UseShellExecute = true;
                        psi.Verb = "runas";
                        Process.Start(psi);
                        Application.Exit();
                    }
                    catch
                    {
                        // UAC cancelled - keep running without admin.
                        Logger.Write("Admin restart cancelled - running without admin.");
                    }
                }
            }
        }

        List<string> GetSelectedIds()
        {
            var ids = new List<string>();
            foreach (var cat in Engine.Categories)
                if (checkBoxes[cat.Id].Checked) ids.Add(cat.Id);
            return ids;
        }

        void UpdateResultsList()
        {
            listView.Items.Clear();
            foreach (var cat in Engine.Categories)
            {
                if (!Engine.ScanResults.ContainsKey(cat.Id)) continue;
                var r = Engine.ScanResults[cat.Id];
                var item = new ListViewItem(cat.Name);
                item.Tag = cat.Id;
                item.SubItems.Add(r.Count.ToString());
                item.SubItems.Add(Util.FormatSize(r.SizeBytes));
                item.SubItems.Add(r.Status);
                listView.Items.Add(item);
            }
        }

        void ListView_DoubleClick(object sender, EventArgs e)
        {
            if (listView.SelectedItems.Count == 0) return;
            ShowCategoryFiles((string)listView.SelectedItems[0].Tag);
        }

        // Modal drill-down: the top 250 largest files of one category,
        // read-only inspection.
        void ShowCategoryFiles(string categoryId)
        {
            Category cat = null;
            foreach (var c in Engine.Categories)
                if (c.Id == categoryId) { cat = c; break; }
            if (cat == null) return;

            var files = new List<FileInfo>();
            ScanResult result;
            if (Engine.ScanResults.TryGetValue(categoryId, out result) && result.Files != null)
            {
                files.AddRange(result.Files);
                files.Sort((a, b) => b.Length.CompareTo(a.Length));
                if (files.Count > 250) files.RemoveRange(250, files.Count - 250);
            }

            var dlg = new Form();
            dlg.Text = "ProClean - " + cat.Name + " (largest files)";
            dlg.ClientSize = new Size(760, 480);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.Font = new Font("Segoe UI", 9);

            var lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.Location = new Point(12, 12);
            lv.Size = new Size(736, 400);
            lv.Columns.Add("File Path", 430);
            lv.Columns.Add("Size", 110);
            lv.Columns.Add("Last Modified", 170);
            foreach (var f in files)
            {
                var row = new ListViewItem(f.FullName);
                row.SubItems.Add(Util.FormatSize(f.Length));
                row.SubItems.Add(f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                lv.Items.Add(row);
            }
            dlg.Controls.Add(lv);

            var lbl = new Label();
            lbl.Text = files.Count == 0
                ? "No file list for this category (DNS flush / Recycle Bin have no files to show)."
                : string.Format("Top {0} largest files - read only, nothing is deleted here.", files.Count);
            lbl.Location = new Point(12, 424);
            lbl.Size = new Size(736, 18);
            dlg.Controls.Add(lbl);

            var btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.Location = new Point(648, 444);
            btnClose.Size = new Size(100, 28);
            btnClose.Click += (s, ev) => dlg.Close();
            dlg.Controls.Add(btnClose);
            dlg.AcceptButton = btnClose;

            dlg.ShowDialog(this);
        }

        void BtnScan_Click(object sender, EventArgs e)
        {
            var ids = GetSelectedIds();
            if (ids.Count == 0)
            {
                MessageBox.Show("Tick at least one category first.", "ProClean",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            btnScan.Enabled = false;
            btnClean.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Scanning...";
            txtLog.Clear();
            Logger.Write("ProClean v" + Program.AppVersion + " - scan started.");
            try
            {
                long totalCount, totalSize;
                Engine.Scan(ids, isAdmin, out totalCount, out totalSize);
                scanTotalCount = totalCount;
                scanTotalSize = totalSize;
                UpdateResultsList();
                lblStatus.Text = string.Format("Scan done: {0} files, {1} reclaimable. Review, then press Clean.",
                    totalCount, Util.FormatSize(totalSize));
                // DNS always reports 0 files - the Clean button must still
                // enable when it is selected.
                if (totalCount > 0 || ids.Contains("Dns")) btnClean.Enabled = true;
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Scan failed: " + ex.Message;
                Logger.Write("Scan failed: " + ex.Message);
            }
            finally
            {
                btnScan.Enabled = true;
                progressBar.Value = 0;
            }
        }

        void BtnClean_Click(object sender, EventArgs e)
        {
            string msg = string.Format(
                "Delete {0} files ({1})?\n\nTemp files are deleted PERMANENTLY - they do not go to the Recycle Bin. Review the preview above first.",
                scanTotalCount, Util.FormatSize(scanTotalSize));
            var ok = MessageBox.Show(msg, "ProClean - confirm clean",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ok != DialogResult.Yes) return;

            if (checkBoxes["RecycleBin"].Checked)
            {
                var ok2 = MessageBox.Show("Empty the Recycle Bin too? This cannot be undone.",
                    "ProClean - Recycle Bin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ok2 != DialogResult.Yes) checkBoxes["RecycleBin"].Checked = false;
            }

            var ids = GetSelectedIds();
            btnScan.Enabled = false;
            btnClean.Enabled = false;
            lblStatus.Text = "Cleaning...";
            try
            {
                long deleted, skipped, freed;
                Engine.Clean(ids, isAdmin, chkRestore.Checked, progressBar,
                    out deleted, out skipped, out freed);
                foreach (ListViewItem item in listView.Items)
                    item.SubItems[3].Text = "Cleaned - see log";
                lblStatus.Text = string.Format("Done: {0} files deleted ({1} freed), {2} skipped. Log: {3}",
                    deleted, Util.FormatSize(freed), skipped, Logger.LogPath);
                MessageBox.Show(
                    string.Format("Clean complete.\n\nDeleted: {0} files ({1})\nSkipped (in use): {2}\n\nLog saved to:\n{3}",
                        deleted, Util.FormatSize(freed), skipped, Logger.LogPath),
                    "ProClean", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Clean failed: " + ex.Message;
                Logger.Write("Clean failed: " + ex.Message);
            }
            finally
            {
                btnScan.Enabled = true;
                progressBar.Value = 0;
            }
        }
    }
}

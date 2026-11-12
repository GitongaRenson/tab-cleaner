// Tab Cleaner: a taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, using the
// browser's own Task Manager (Shift+Esc) to tell real tabs from things embedded in them. Nothing inside the browser
// is changed.
//
// How it decides (every minute):
//  1. Active = the selected tab of each WorkStream window, read from the browser's tab strip. It is never ended, even
//     while its window is minimised or covered. Every other tab is idle from the moment you leave it - tabs playing
//     sound and busy tabs included.
//  2. WorkStream's Task Manager (kept open off-screen, no taskbar button) tells which process holds which tab:
//       "Tab: <title>"            a tab - the only kind Tab Cleaner ends
//       "Subframe: https://site/" something embedded inside a tab (ads, videos, maps...) - never ended on its own:
//                                 ending it breaks the page around it, and it closes anyway when its tab is ended
//  3. A tab idle for 30 minutes - or 10 minutes when it uses more than 1.5% of the computer's memory - is ended, unless
//     it is allow-listed ("Google Maps", "Dataminr", ...). Ending = ending exactly that tab's process, which is what
//     the Task Manager's "End task" does.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program {
    public const string Version = "1.3.0";

    [STAThread]
    static void Main(string[] args) {
        if (Array.IndexOf(args, "--uninstall") >= 0) { Installer.Uninstall(Array.IndexOf(args, "--quiet") >= 0); return; }
        bool created;
        using (new Mutex(true, "TabCleaner.SingleInstance", out created)) {
            if (!created) {                                             // already running: just say so
                MessageBox.Show("Tab Cleaner is already running. Right-click its icon in the taskbar's notification area to start or stop it.",
                                "Tab Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.EnableVisualStyles();
            Application.Run(new TrayApp(args));
        }
    }
}

/// Uninstall for the current user (the installer registers "TabCleaner.exe --uninstall" in Apps).
static class Installer {
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TabCleaner";

    public static void Uninstall(bool quiet) {
        if (!quiet && MessageBox.Show("Remove Tab Cleaner from this computer?", "Tab Cleaner", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("TabCleaner")) { try { if (p.Id != me) { p.Kill(); p.WaitForExit(3000); } } catch { } }
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("TabCleaner", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        foreach (var f in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tab Cleaner.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Tab Cleaner.lnk") })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        // remove the program folder once this process has exited (settings and log are kept in %LOCALAPPDATA%\TabCleaner)
        string folder = Path.GetDirectoryName(Application.ExecutablePath);
        if (Path.GetFileName(folder).Equals("TabCleaner", StringComparison.OrdinalIgnoreCase) && folder.IndexOf(@"\Programs\", StringComparison.OrdinalIgnoreCase) >= 0) {
            var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + folder + "\"");
            psi.WindowStyle = ProcessWindowStyle.Hidden; psi.CreateNoWindow = true;
            try { Process.Start(psi); } catch { }
        }
        if (!quiet) MessageBox.Show("Tab Cleaner has been removed.", "Tab Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

class TmRow { public string Task; public int Pid; }

/// WorkStream's own Task Manager (Shift+Esc), read through Windows UI Automation.
static class BrowserTaskManager {
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static AutomationElement Find(int mainPid) {
        var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, mainPid);
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, cond))
            if (w.Current.Name.StartsWith("Task Manager", StringComparison.OrdinalIgnoreCase)) return w;
        return null;
    }

    /// Opens the Task Manager (Shift+Esc in the browser), parks it off-screen and gives focus back.
    public static AutomationElement Open(int mainPid) {
        IntPtr previous = GetForegroundWindow();
        IntPtr main;
        try { using (var p = Process.GetProcessById(mainPid)) main = p.MainWindowHandle; } catch { return null; }
        if (main == IntPtr.Zero) return null;
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // lets Windows hand over focus
        SetForegroundWindow(main);
        Thread.Sleep(400);
        keybd_event(0x10, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 0, UIntPtr.Zero);  // Shift+Esc
        keybd_event(0x1B, 0, 2, UIntPtr.Zero); keybd_event(0x10, 0, 2, UIntPtr.Zero);
        AutomationElement tm = null;
        for (int i = 0; i < 24 && tm == null; i++) { Thread.Sleep(250); tm = Find(mainPid); }
        if (tm != null) Park(tm);
        if (previous != IntPtr.Zero) SetForegroundWindow(previous);
        return tm;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList { void HrInit(); void AddTab(IntPtr h); void DeleteTab(IntPtr h); void ActivateTab(IntPtr h); void SetActiveAlt(IntPtr h); }
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarList { }

    public class StripTab { public string Name; public bool Selected; }

    /// Every tab in the browsers' tab strips, and which one is selected in each window (= the active tabs).
    public static List<StripTab> TabStrip(int mainPid) {
        var tabs = new List<StripTab>();
        var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, mainPid);
        var tabCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, cond)) {
            if (w.Current.Name.StartsWith("Task Manager", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (AutomationElement t in w.FindAll(TreeScope.Descendants, tabCond)) {
                object sel;
                bool selected = t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sel) && ((SelectionItemPattern)sel).Current.IsSelected;
                tabs.Add(new StripTab { Name = t.Current.Name, Selected = selected });
            }
        }
        return tabs;
    }

    /// A tab's title without what the tab strip adds after it (" - Memory usage - 120 MB", " - Crashed", ...).
    public static string CleanTitle(string name) {
        return System.Text.RegularExpressions.Regex.Replace(name,
            @"( - (Memory usage - .*|Crashed|Audio playing|Playing audio|Muted|Network error|Loading|Pinned|Sleeping))+$", "").Trim();
    }

    /// Off-screen, as tall as Windows allows, and without a taskbar button (so WorkStream shows as one window).
    public static void Park(AutomationElement tm) {
        try {
            IntPtr h = new IntPtr(tm.Current.NativeWindowHandle);
            var r = tm.Current.BoundingRectangle;
            if (r.Left > -3000) SetWindowPos(h, IntPtr.Zero, -4000, 0, 900, 3000, 0x0014);
            long ex = GetWindowLongPtr(h, -20).ToInt64();
            if ((ex & 0x80) == 0 || (ex & 0x40000) != 0) {
                SetWindowLongPtr(h, -20, new IntPtr((ex | 0x80) & ~0x40000L));      // tool window, not an app window
                var tb = (ITaskbarList)new TaskbarList();
                tb.HrInit();
                tb.DeleteTab(h);
            }
        } catch { }
    }

    /// The tab rows of the list (with the embedded frames of the same processes). The table only exposes the rows that
    /// fit in its window and can't be scrolled by automation, so its search box is used: "Tab:" lists just the tabs, and
    /// a tab that is still missing is searched for by its own title.
    public static List<TmRow> ReadTabs(AutomationElement tm, List<StripTab> strip) {
        var all = new List<TmRow>();
        var seen = new HashSet<string>();
        try {                                                           // the "Tabs & extensions" view
            foreach (AutomationElement t in tm.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))) {
                object sel;
                if (t.Current.Name == "Tabs & extensions" && t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sel) && !((SelectionItemPattern)sel).Current.IsSelected)
                    ((SelectionItemPattern)sel).Select();
            }
        } catch { }
        var edit = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        object vp;
        ValuePattern search = edit != null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out vp) ? (ValuePattern)vp : null;
        var grid = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
        if (grid == null) return all;
        Action<string> pass = delegate (string text) {
            if (search != null) { try { search.SetValue(text); } catch { } Thread.Sleep(350); }
            foreach (var r in ReadVisible(grid)) if (seen.Add(r.Pid + "|" + r.Task)) all.Add(r);
        };
        pass("Tab:");
        if (search != null) {
            int lookups = 0;
            foreach (var t in strip) {
                if (t.Name.EndsWith(" - Crashed")) continue;            // no process to find
                string title = CleanTitle(t.Name);
                if (title.Length == 0) continue;
                bool found = false;
                foreach (var r in all) if (r.Task.StartsWith("Tab:") && title.StartsWith(r.Task.Substring(4).Trim(), StringComparison.Ordinal) && r.Task.Length > 4) found = true;
                if (found || ++lookups > 40) continue;
                pass(title.Length > 40 ? title.Substring(0, 40) : title);   // asleep tabs simply aren't listed
            }
            try { search.SetValue("Tab:"); } catch { }
        }
        return all;
    }

    static List<TmRow> ReadVisible(AutomationElement grid) {
        var rows = new List<TmRow>();
        var walk = TreeWalker.RawViewWalker;
        int lastPid = 0;
        for (var row = walk.GetFirstChild(grid); row != null; row = walk.GetNextSibling(row)) {
            var cells = new List<string>();
            for (var c = walk.GetFirstChild(row); c != null; c = walk.GetNextSibling(c)) cells.Add(c.Current.Name);
            if (cells.Count < 5) continue;
            string task = cells[0];
            if (task.StartsWith("Column ")) continue;                   // header
            int pid;
            if (int.TryParse(cells[cells.Count - 1], out pid)) lastPid = pid; else pid = lastPid;   // no ID: same process as above
            if (pid == 0) continue;                                     // continuation of a process above the view
            rows.Add(new TmRow { Task = task, Pid = pid });
        }
        return rows;
    }
}

class TrayApp : ApplicationContext {
    // ---- settings (%LOCALAPPDATA%\TabCleaner\settings.ini)
    int idleMinutes = 30;
    bool stopped = false;
    bool notify = true;
    double bigTabPercent = 1.5;     // a tab using more than this share of the computer's memory...
    int bigTabMinutes = 10;         // ...is ended after this many idle minutes instead
    double totalMemoryMB = 0;
    List<string> allow = new List<string> { "Google Maps", "Dataminr", "dataminr.com", "google.com/maps", "maps.google" };
    string exeName = "workstream.exe";
    bool dryRun = false;            // --dry-run: only log what would be ended
    DateTime iniStamp = DateTime.MinValue;

    readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabCleaner");
    string LogPath { get { return Path.Combine(dir, "log.txt"); } }
    string IniPath { get { return Path.Combine(dir, "settings.ini"); } }

    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem toggleItem = new ToolStripMenuItem();
    readonly ToolStripMenuItem statusItem = new ToolStripMenuItem();
    readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows");
    readonly ToolStripMenuItem notifyItem = new ToolStripMenuItem("Show a message when tabs are ended");
    readonly ToolStripMenuItem limitMenu = new ToolStripMenuItem("Idle limit");
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly SynchronizationContext ui;
    volatile bool working = false;
    int endedToday = 0;
    DateTime today = DateTime.Today;
    int lastTabCount = 0, lastIdleCount = 0;
    Icon iconOn, iconStopped;

    public TrayApp(string[] args) {
        Directory.CreateDirectory(dir);
        if (!File.Exists(IniPath)) SaveSettings();
        LoadSettings();
        for (int i = 0; i < args.Length; i++) {                         // testing switches (not saved)
            if (args[i] == "--exe" && i + 1 < args.Length) exeName = args[++i];
            else if (args[i] == "--minutes" && i + 1 < args.Length) idleMinutes = int.Parse(args[++i]);
            else if (args[i] == "--dry-run") dryRun = true;
            else if (args[i] == "--big-minutes" && i + 1 < args.Length) bigTabMinutes = int.Parse(args[++i]);
        }
        iconOn = MakeIcon(false);
        iconStopped = MakeIcon(true);
        try {
            using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            foreach (ManagementObject mo in s.Get()) totalMemoryMB = Convert.ToDouble(mo["TotalPhysicalMemory"]) / (1024 * 1024);
        } catch { }

        var menu = new ContextMenuStrip();
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
        toggleItem.Click += delegate { SetStopped(!stopped); };
        menu.Items.Add(toggleItem);
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        foreach (int m in new int[] { 1, 15, 30, 45, 60, 90 }) {
            int mins = m;
            var it = new ToolStripMenuItem(m == 1 ? "1 minute (for testing)" : m + " minutes");
            it.Tag = m;
            it.Click += delegate { idleMinutes = mins; SaveSettings(); RefreshMenu(); Log("Idle limit set to " + mins + " minutes"); };
            limitMenu.DropDownItems.Add(it);
        }
        menu.Items.Add(limitMenu);
        menu.Items.Add("Edit allow list...", null, delegate { SaveSettings(); Process.Start("notepad.exe", "\"" + IniPath + "\""); });
        notifyItem.Click += delegate { notify = !notify; SaveSettings(); RefreshMenu(); };
        menu.Items.Add(notifyItem);
        startupItem.Click += delegate { SetStartup(!IsStartup()); RefreshMenu(); };
        menu.Items.Add(startupItem);
        menu.Items.Add("Open log", null, delegate { if (!File.Exists(LogPath)) File.WriteAllText(LogPath, ""); Process.Start("notepad.exe", "\"" + LogPath + "\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (close Tab Cleaner)", null, delegate { tray.Visible = false; Log("Exited"); ExitThread(); });
        menu.Opening += delegate { RefreshMenu(); };

        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += delegate (object s, MouseEventArgs e) {      // left click: show the menu too
            if (e.Button == MouseButtons.Left) {
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(tray, null);
            }
        };
        RefreshMenu();

        Log("Started v" + Program.Version + (dryRun ? " (DRY RUN: nothing will be ended)" : "") + (stopped ? " (stopped)" : "")
            + ": ends " + exeName + " tabs idle for " + idleMinutes + " minutes, or " + bigTabMinutes + " minutes when using over "
            + bigTabPercent + "% of memory (" + (int)BigTabMB + " MB); never: " + string.Join(", ", allow.ToArray()));
        if (stopped) tray.ShowBalloonTip(4000, "Tab Cleaner", "Tab Cleaner is stopped. Click its icon and choose Start to turn it on.", ToolTipIcon.Info);
        timer.Interval = 60 * 1000;                                     // look once a minute: light on the PC
        timer.Tick += delegate { Check(); };
        timer.Start();
        Check();
    }

    void SetStopped(bool stop) {
        stopped = stop;
        lock (idleSince) { idleSince.Clear(); reported.Clear(); }      // idle time counts again from now
        SaveSettings();
        RefreshMenu();
        Log(stop ? "Stopped" : "Started (idle time counts from now)");
        tray.ShowBalloonTip(3000, "Tab Cleaner", stop ? "Stopped. No tabs will be ended until you start it again."
                                                        : "Started. Tabs not opened for " + idleMinutes + " minutes will be ended.", ToolTipIcon.Info);
        if (!stop) Check();
    }

    // ---- every minute (read in the background, so the tray stays responsive):
    //  active = the selected tab of each WorkStream window (read from its tab strip); every other tab is idle from the
    //  moment it stops being selected - playing sound or busy makes no difference. Tabs are matched to their processes
    //  through WorkStream's Task Manager.
    readonly Dictionary<int, DateTime> idleSince = new Dictionary<int, DateTime>();   // tab process -> not selected since
    readonly HashSet<int> reported = new HashSet<int>();                               // allow-listed / dry-run lines logged once
    DateTime lastOpenTry = DateTime.MinValue;

    void Check() {
        if (DateTime.Today != today) { today = DateTime.Today; endedToday = 0; }
        if (File.Exists(IniPath) && File.GetLastWriteTime(IniPath) != iniStamp) {
            int keepLimit = idleMinutes;
            LoadSettings();
            if (dryRun) idleMinutes = keepLimit;
            lock (idleSince) reported.Clear();
            Log("Settings reloaded; never: " + string.Join(", ", allow.ToArray()));
        }
        if (stopped) { RefreshMenu(); return; }
        if (working) return;
        working = true;
        var allowNow = new List<string>(allow);
        bool dry = dryRun;
        int limit = idleMinutes, bigLimit = bigTabMinutes;
        double bigMB = BigTabMB;
        bool mayOpen = (DateTime.Now - lastOpenTry).TotalMinutes >= 10;   // reopen a closed Task Manager at most every 10 min
        string exe = exeName;
        var t2 = new Thread(delegate () {
            var ended = new List<string>();
            var notes = new List<string>();
            int[] counts = new int[2];
            bool triedOpen = false;
            try { Scan(exe, allowNow, dry, limit, bigLimit, bigMB, mayOpen, ended, notes, counts, ref triedOpen); }
            catch (Exception e) { notes.Add("Check failed: " + e.Message); }
            ui.Post(delegate { Finished(ended, notes, counts, triedOpen); }, null);
        });
        t2.IsBackground = true;
        t2.SetApartmentState(ApartmentState.MTA);                       // UI Automation works best off the UI thread
        t2.Start();
    }

    void Scan(string exe, List<string> allowNow, bool dry, int limit, int bigLimit, double bigMB, bool mayOpen,
              List<string> ended, List<string> notes, int[] counts, ref bool triedOpen) {
        DateTime now = DateTime.Now;
        // 1. WorkStream's tab-capable processes and their memory
        int mainPid = 0;
        var mem = new Dictionary<int, double>();
        string q = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + exe.Replace("'", "") + "'";
        using (var s = new ManagementObjectSearcher(q))
        foreach (ManagementObject mo in s.Get()) {
            string cmd = (mo["CommandLine"] as string) ?? "";
            int pid = Convert.ToInt32(mo["ProcessId"]);
            if (cmd.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0) { mainPid = pid; continue; }   // the browser
            if (cmd.IndexOf("--type=renderer", StringComparison.OrdinalIgnoreCase) < 0) continue;            // GPU, helpers
            if (cmd.IndexOf("--extension-process", StringComparison.OrdinalIgnoreCase) >= 0) continue;       // extensions
            try { using (var p = Process.GetProcessById(pid)) mem[pid] = p.PrivateMemorySize64 / (1024.0 * 1024.0); } catch { }
        }
        if (mainPid == 0) { lock (idleSince) { idleSince.Clear(); reported.Clear(); } return; }   // WorkStream not running

        // 2. the tab strip: the selected tab of every window is active and never ended (if that can't be read, nothing
        //    is ended this round)
        var strip = BrowserTaskManager.TabStrip(mainPid);
        var selected = new List<string>();
        foreach (var t in strip) if (t.Selected) selected.Add(BrowserTaskManager.CleanTitle(t.Name));
        if (selected.Count == 0) return;

        // 3. WorkStream's Task Manager: which process holds which tab
        var tm = BrowserTaskManager.Find(mainPid);
        if (tm == null) {
            if (!mayOpen) return;
            triedOpen = true;
            tm = BrowserTaskManager.Open(mainPid);
            if (tm == null) { notes.Add("Could not open WorkStream's Task Manager; will try again in 10 minutes"); return; }
            notes.Add("Opened WorkStream's Task Manager (kept off-screen, no taskbar button)");
        } else BrowserTaskManager.Park(tm);
        var tasksByPid = new Dictionary<int, List<string>>();
        foreach (var r in BrowserTaskManager.ReadTabs(tm, strip)) {
            List<string> l;
            if (!tasksByPid.TryGetValue(r.Pid, out l)) { l = new List<string>(); tasksByPid[r.Pid] = l; }
            l.Add(r.Task);
        }

        lock (idleSince) {
            var seen = new HashSet<int>();
            foreach (var kv in tasksByPid) {
                int pid = kv.Key;
                if (!mem.ContainsKey(pid)) continue;
                var titles = new List<string>();
                foreach (var t in kv.Value) if (t.StartsWith("Tab:")) titles.Add(t.Substring(4).Trim());
                if (titles.Count == 0) continue;                        // embedded content only: never ended on its own
                counts[0]++;
                seen.Add(pid);
                bool active = false;
                foreach (var t in titles) foreach (var sname in selected)
                    if (t.Length > 0 && sname.StartsWith(t, StringComparison.Ordinal)) active = true;
                if (active) { idleSince.Remove(pid); reported.Remove(pid); continue; }
                counts[1]++;
                DateTime since;
                if (!idleSince.TryGetValue(pid, out since)) { idleSince[pid] = now; continue; }   // idle from now
                double idleMin = (now - since).TotalMinutes;
                string why = null;
                if (idleMin >= limit) why = "not opened for " + (int)idleMin + " min";
                else if (idleMin >= bigLimit && bigMB > 0 && mem[pid] >= bigMB) why = "not opened for " + (int)idleMin + " min, using " + (int)mem[pid] + " MB";
                if (why == null) continue;
                string what = Short(string.Join(" | ", kv.Value.ToArray()));
                string hit = null;
                foreach (var t in kv.Value) foreach (var a in allowNow)
                    if (hit == null && t.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) hit = a;
                if (hit != null) { if (reported.Add(pid)) notes.Add("Kept " + pid + " (allow-listed: " + hit + "): " + what); continue; }
                if (dry) { if (reported.Add(pid)) notes.Add("WOULD END " + pid + " (" + why + "): " + what); continue; }
                bool gone = false;
                try { using (var p = Process.GetProcessById(pid)) p.Kill(); } catch { }
                for (int i = 0; i < 12 && !gone; i++) { Thread.Sleep(250); gone = !Alive(pid); }
                if (gone) { ended.Add(pid + " (" + why + "): " + what); idleSince.Remove(pid); }
                else notes.Add("Could not end " + pid + ": " + what);
            }
            foreach (var pid in new List<int>(idleSince.Keys)) if (!seen.Contains(pid)) idleSince.Remove(pid);   // closed tabs
            reported.RemoveWhere(pid => !seen.Contains(pid));
        }
    }

    double BigTabMB { get { return totalMemoryMB * bigTabPercent / 100.0; } }

    static bool Alive(int pid) {
        try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; } catch { return false; }
    }
    static string Short(string s) { return s.Length > 160 ? s.Substring(0, 160) + "..." : s; }

    void Finished(List<string> ended, List<string> notes, int[] counts, bool triedOpen) {
        working = false;
        if (triedOpen) lastOpenTry = DateTime.Now;
        lastTabCount = counts[0];
        lastIdleCount = counts[1];
        foreach (var n in notes) Log(n);
        foreach (var e in ended) Log("Ended tab " + e);
        if (ended.Count > 0) {
            endedToday += ended.Count;
            if (notify) tray.ShowBalloonTip(5000, "Tab Cleaner",
                "Ended " + ended.Count + " idle tab" + (ended.Count == 1 ? "" : "s") + ". Reload a tab to bring it back.", ToolTipIcon.Info);
        }
        RefreshMenu();
    }

    // ---- menu and icon
    string StatusText() {
        string ended = endedToday + " tab" + (endedToday == 1 ? "" : "s") + " ended today";
        if (stopped) return "Stopped. " + ended + ".";
        return "Running: " + lastIdleCount + " of " + lastTabCount + " open tabs idle. " + ended + "." + (dryRun ? " DRY RUN" : "");
    }

    void RefreshMenu() {
        toggleItem.Text = stopped ? "Start Tab Cleaner" : "Stop Tab Cleaner";
        statusItem.Text = StatusText();
        notifyItem.Checked = notify;
        startupItem.Checked = IsStartup();
        foreach (ToolStripMenuItem it in limitMenu.DropDownItems) it.Checked = (int)it.Tag == idleMinutes;
        limitMenu.Text = idleMinutes == 1 ? "Idle limit: 1 minute (testing - every tab)"
                       : "Idle limit: " + idleMinutes + " minutes (" + Math.Min(bigTabMinutes, idleMinutes) + " for tabs over " + bigTabPercent + "% memory)";
        tray.Icon = stopped ? iconStopped : iconOn;
        string tip = "Tab Cleaner: " + (stopped ? "stopped" : "running, " + idleMinutes + " min limit") + ", " + endedToday + " ended today";
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    /// Small original icon: a browser tab with a clock, grey when stopped.
    static Icon MakeIcon(bool off) {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color main = off ? Color.FromArgb(150, 150, 150) : Color.FromArgb(32, 132, 230);
            using (var b = new SolidBrush(main)) { g.FillRectangle(b, 2, 9, 28, 20); g.FillRectangle(b, 2, 4, 13, 7); }
            using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, 9, 12, 14, 14);
            using (var pen = new Pen(main, 2f)) { g.DrawLine(pen, 16, 19, 16, 14); g.DrawLine(pen, 16, 19, 20, 21); }
            if (off) using (var red = new Pen(Color.FromArgb(220, 60, 60), 3f)) g.DrawLine(red, 4, 28, 28, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---- start with Windows (current user only)
    bool IsStartup() {
        using (var k = Registry.CurrentUser.OpenSubKey(Installer.RunKey)) return k != null && k.GetValue("TabCleaner") != null;
    }
    void SetStartup(bool on) {
        using (var k = Registry.CurrentUser.CreateSubKey(Installer.RunKey)) {
            if (on) k.SetValue("TabCleaner", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("TabCleaner", false);
        }
        Log(on ? "Will start with Windows" : "Will not start with Windows");
    }

    // ---- settings and log
    void LoadSettings() {
        if (!File.Exists(IniPath)) return;
        iniStamp = File.GetLastWriteTime(IniPath);
        foreach (var line in File.ReadAllLines(IniPath)) {
            if (line.TrimStart().StartsWith("#")) continue;
            var kv = line.Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            string k = kv[0].Trim(), v = kv[1].Trim();
            if (k == "idleMinutes") int.TryParse(v, out idleMinutes);
            else if (k == "stopped" || k == "paused") stopped = v == "true";
            else if (k == "notify") notify = v != "false";
            else if (k == "browser" && v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName = v;
            else if (k == "bigTabPercent") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out bigTabPercent);
            else if (k == "bigTabMinutes") int.TryParse(v, out bigTabMinutes);
            else if (k == "allow") {
                allow = new List<string>();
                foreach (var a in v.Split(',')) {
                    string x = a.Trim();
                    if (x.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) x = x.Substring(8);
                    else if (x.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) x = x.Substring(7);
                    if (x.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) x = x.Substring(4);
                    if (x.Length >= 3) allow.Add(x);
                }
            }
        }
        if (idleMinutes < 1) idleMinutes = 30;
    }
    void SaveSettings() {
        File.WriteAllText(IniPath,
            "# Tab Cleaner settings. Save this file and the change is picked up within a minute.\r\n"
          + "# allow: a tab is never ended if its name in WorkStream's Task Manager (\"Tab: <page title>\") or a site embedded\r\n"
          + "# in it contains any of these words (comma-separated, not case-sensitive).\r\n"
          + "allow=" + string.Join(", ", allow.ToArray()) + "\r\n"
          + "idleMinutes=" + idleMinutes + "\r\nstopped=" + (stopped ? "true" : "false") + "\r\nnotify=" + (notify ? "true" : "false") + "\r\n"
          + "# a tab using more than bigTabPercent % of the computer's memory is ended after bigTabMinutes idle minutes\r\n"
          + "bigTabPercent=" + bigTabPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"
          + "bigTabMinutes=" + bigTabMinutes + "\r\n"
          + "browser=" + exeName + "\r\n");
        iniStamp = File.GetLastWriteTime(IniPath);
    }
    void Log(string msg) {
        try {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
        } catch { }
    }
}

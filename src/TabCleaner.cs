// Tab Cleaner: a taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, using the
// browser's own Task Manager (Shift+Esc) to tell real tabs from things embedded in them. Nothing inside the browser
// is changed.
//
// How it decides (every minute):
//  1. Only WorkStream's tab strip is read (read-only, like a screen reader). Active = the selected tab of each window;
//     it is never ended, even while its window is minimised or covered. Every other tab is idle from the moment you
//     leave it - tabs playing sound and busy tabs included.
//  2. A tab idle for 30 minutes - or 10 minutes when it uses more than 1.5% of the computer's memory - is due, unless it
//     is allow-listed ("Google Maps", "Dataminr", ...). Only then is WorkStream's Task Manager used: one search for that
//     tab's title (cleared straight after) gives its process, which is ended - what the Task Manager's "End task" does.
//     Embedded frames ("Subframe: ...") are never ended on their own. At most 5 tabs a minute, one at a time.

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
    public const string Version = "1.5.2";

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

    /// The process(es) holding the tab with this title, each with everything that process holds (tab + embedded frames).
    /// One search in the Task Manager's search box (its list only exposes the rows that fit in the window), cleared
    /// straight after so the Task Manager is never left filtered.
    public static Dictionary<int, List<string>> FindTab(AutomationElement tm, string title) {
        var result = new Dictionary<int, List<string>>();
        try {                                                           // the "Tabs & extensions" view
            foreach (AutomationElement t in tm.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))) {
                object sel;
                if (t.Current.Name == "Tabs & extensions" && t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sel) && !((SelectionItemPattern)sel).Current.IsSelected)
                    ((SelectionItemPattern)sel).Select();
            }
        } catch { }
        var edit = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var grid = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
        object vp;
        if (edit == null || grid == null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) return result;
        var search = (ValuePattern)vp;
        try {
            search.SetValue(title.Length > 50 ? title.Substring(0, 50) : title);
            Thread.Sleep(500);
            var rows = ReadVisible(grid);
            var tabPids = new HashSet<int>();
            foreach (var r in rows)
                if (r.Task.StartsWith("Tab:") && r.Task.Substring(4).Trim() == title) tabPids.Add(r.Pid);
            foreach (var r in rows) if (tabPids.Contains(r.Pid)) {
                List<string> l;
                if (!result.TryGetValue(r.Pid, out l)) { l = new List<string>(); result[r.Pid] = l; }
                l.Add(r.Task);
            }
        } finally {
            try { search.SetValue(""); } catch { }                     // never leave it filtered
            Thread.Sleep(200);
        }
        return result;
    }

    /// title -> process ids, for every tab currently listed. Filters the list to tabs ("Tab:") and scrolls through it so
    /// all rows are captured even with many tabs open; the filter is cleared straight after. Used to seed idle clocks
    /// and to end tabs. The out flag says whether the whole list was read (so callers don't mistake a miss for asleep).
    public static Dictionary<string, List<int>> MapTabs(AutomationElement tm, out bool complete) {
        complete = false;
        var map = new Dictionary<string, List<int>>();
        try {
            foreach (AutomationElement t in tm.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))) {
                object sel;
                if (t.Current.Name == "Tabs & extensions" && t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sel) && !((SelectionItemPattern)sel).Current.IsSelected)
                    ((SelectionItemPattern)sel).Select();
            }
        } catch { }
        var edit = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var grid = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
        object vp;
        if (edit == null || grid == null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) return map;
        var search = (ValuePattern)vp;
        Action collect = delegate () {
            foreach (var r in ReadVisible(grid)) {
                if (!r.Task.StartsWith("Tab:")) continue;
                string title = r.Task.Substring(4).Trim();
                List<int> l;
                if (!map.TryGetValue(title, out l)) { l = new List<int>(); map[title] = l; }
                if (!l.Contains(r.Pid)) l.Add(r.Pid);
            }
        };
        try {
            search.SetValue("Tab:");
            Thread.Sleep(450);
            // find a vertical scroll on the list (or an ancestor) and page through the whole thing
            ScrollPattern scroll = null;
            object sp;
            for (var e = grid; e != null && scroll == null; e = TreeWalker.RawViewWalker.GetParent(e))
                if (e.TryGetCurrentPattern(ScrollPattern.Pattern, out sp) && ((ScrollPattern)sp).Current.VerticallyScrollable) scroll = (ScrollPattern)sp;
            if (scroll == null) { collect(); complete = true; return map; }   // no scrollbar: it all fits
            try { scroll.SetScrollPercent(ScrollPattern.NoScroll, 0); Thread.Sleep(120); } catch { }
            complete = true;
            for (int page = 0; page < 60; page++) {
                collect();
                double before;
                try { before = scroll.Current.VerticalScrollPercent; } catch { break; }
                if (before < 0 || before >= 99.5) break;
                try { scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); } catch { break; }
                Thread.Sleep(90);
                double after;
                try { after = scroll.Current.VerticalScrollPercent; } catch { break; }
                if (after <= before) { if (page > 50) complete = false; break; }   // couldn't advance
            }
        } finally {
            try { search.SetValue(""); } catch { }
            Thread.Sleep(150);
        }
        return map;
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
    int idleMinutes = 15;   // default idle limit (minutes)
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
    string StatePath { get { return Path.Combine(dir, "idle-state.txt"); } }

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
        LoadState();   // idle clocks remembered from the last run, so a restart doesn't reset them
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
        var header = new ToolStripMenuItem("Tab Cleaner  v" + Program.Version) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());
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

    // ---- every minute (in the background, so the tray stays responsive):
    //  1. Only the tab strip is read (a read-only view, like a screen reader): the selected tab of each window is
    //     active; every other tab is idle from the moment you leave it (playing sound or busy makes no difference).
    //  2. Only when a tab is due does Tab Cleaner look it up in WorkStream's Task Manager - one search for that title,
    //     cleared straight after - and end that tab's process. At most 5 tabs a minute, one at a time.
    //     (Keeping the Task Manager filtered while many tabs closed is the likely cause of a WorkStream crash.)
    class TabState { public DateTime Since; }
    readonly Dictionary<string, TabState> idleSince = new Dictionary<string, TabState>();   // tab (title#n) -> state
    readonly HashSet<string> reported = new HashSet<string>();                             // allow-listed lines logged once
    DateTime lastOpenTry = DateTime.MinValue;
    DateTime scanStarted = DateTime.MinValue;
    const int MaxEndsPerScan = 25;   // clears a big backlog in a scan or two; killed 120 ms apart so the browser copes

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
        if (working) {                                                   // a previous scan is still going
            if ((DateTime.Now - scanStarted).TotalSeconds < 150) return;
            Log("Previous check was still running after 150 s; starting a fresh one.");   // watchdog: never freeze
        }
        working = true;
        scanStarted = DateTime.Now;
        var allowNow = new List<string>(allow);
        bool dry = dryRun;
        int limit = idleMinutes, bigLimit = Math.Min(bigTabMinutes, idleMinutes);
        double bigMB = BigTabMB;
        bool mayOpen = (DateTime.Now - lastOpenTry).TotalMinutes >= 10;   // reopen a closed Task Manager at most every 10 min
        string exe = exeName;
        var t2 = new Thread(delegate () {
            var ended = new List<string>();
            var notes = new List<string>();
            int[] counts = new int[2];
            var triedOpen = new bool[1];
            try { Scan(exe, allowNow, dry, limit, bigLimit, bigMB, mayOpen, ended, notes, counts, triedOpen); }
            catch (Exception e) { notes.Add("Check failed: " + e.Message); }
            ui.Post(delegate { Finished(ended, notes, counts, triedOpen[0]); }, null);
        });
        t2.IsBackground = true;
        t2.SetApartmentState(ApartmentState.MTA);                       // UI Automation works best off the UI thread
        t2.Start();
    }

    class DueTab { public string Key, Title, Why; public bool NeedsMemory; }

    void Scan(string exe, List<string> allowNow, bool dry, int limit, int bigLimit, double bigMB, bool mayOpen,
              List<string> ended, List<string> notes, int[] counts, bool[] triedOpen) {
        DateTime now = DateTime.Now;
        // WorkStream's tab-capable processes (renderers, not extensions) and their memory
        int mainPid = 0;
        var mem = new Dictionary<int, double>();
        var start = new Dictionary<int, DateTime>();   // renderer process start time ~ when the tab was last opened/loaded
        string q = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + exe.Replace("'", "") + "'";
        using (var s = new ManagementObjectSearcher(q))
        foreach (ManagementObject mo in s.Get()) {
            string cmd = (mo["CommandLine"] as string) ?? "";
            int pid = Convert.ToInt32(mo["ProcessId"]);
            if (cmd.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0) { mainPid = pid; continue; }   // the browser
            if (cmd.IndexOf("--type=renderer", StringComparison.OrdinalIgnoreCase) < 0) continue;            // GPU, helpers
            if (cmd.IndexOf("--extension-process", StringComparison.OrdinalIgnoreCase) >= 0) continue;       // extensions
            try { using (var p = Process.GetProcessById(pid)) { mem[pid] = p.PrivateMemorySize64 / (1024.0 * 1024.0); start[pid] = p.StartTime; } } catch { }
        }
        if (mainPid == 0) { lock (idleSince) { idleSince.Clear(); reported.Clear(); } return; }   // WorkStream not running

        // 1. the tab strip: which tab is selected in each window, how long every other tab has been left
        var strip = BrowserTaskManager.TabStrip(mainPid);
        bool anySelected = false;
        foreach (var t in strip) if (t.Selected) anySelected = true;
        if (!anySelected) return;                                       // can't tell what's active: do nothing
        // give each tab a stable key and note which are being seen for the first time (no idle clock yet)
        var entries = new List<KeyValuePair<BrowserTaskManager.StripTab, string>>();       // tab -> key "title#n"
        var titleCount = new Dictionary<string, int>();
        var seen = new HashSet<string>();
        var firstSeen = new List<KeyValuePair<string, string>>();       // key -> title, first time seen and not selected
        foreach (var t in strip) {
            string title = BrowserTaskManager.CleanTitle(t.Name);
            if (title.Length == 0) continue;
            int n; titleCount.TryGetValue(title, out n); titleCount[title] = ++n;
            string key = title + "#" + n;
            seen.Add(key);
            entries.Add(new KeyValuePair<BrowserTaskManager.StripTab, string>(t, key));
            if (!t.Name.EndsWith(" - Crashed") && !t.Selected) {
                lock (idleSince) if (!idleSince.ContainsKey(key)) firstSeen.Add(new KeyValuePair<string, string>(key, title));
            }
        }

        AutomationElement tm = null;
        bool haveTm = false;
        Func<bool> getTm = delegate () {                               // open WorkStream's Task Manager once, lazily
            if (haveTm) return tm != null;
            haveTm = true;
            tm = BrowserTaskManager.Find(mainPid);
            if (tm != null) { BrowserTaskManager.Park(tm); return true; }
            if (!mayOpen) return false;
            triedOpen[0] = true;
            tm = BrowserTaskManager.Open(mainPid);
            if (tm == null) notes.Add("Could not open WorkStream's Task Manager; will try again in 10 minutes");
            else notes.Add("Opened WorkStream's Task Manager (kept off-screen, no taskbar button)");
            return tm != null;
        };

        Dictionary<string, List<int>> map = null;   // title -> process ids, read from the Task Manager at most once per scan
        bool mapComplete = false, mapDone = false;
        Func<Dictionary<string, List<int>>> ensureMap = delegate () {
            if (!mapDone) { mapDone = true; bool c; map = BrowserTaskManager.MapTabs(tm, out c); mapComplete = c; }
            return map;
        };

        // seed first-seen tabs from when the browser opened/loaded them (their process start time), not from now,
        // so tabs already left idle before Tab Cleaner started are counted from when they were opened
        if (firstSeen.Count > 0 && getTm()) {
            ensureMap();
            lock (idleSince) {
                foreach (var fs in firstSeen) {
                    DateTime since = now;                              // asleep / unknown: start now
                    List<int> pids;
                    if (map.TryGetValue(fs.Value, out pids)) {
                        foreach (var pid in pids) { DateTime s2; if (start.TryGetValue(pid, out s2) && s2 < since) since = s2; }
                    }
                    if (!idleSince.ContainsKey(fs.Key)) idleSince[fs.Key] = new TabState { Since = since };
                }
            }
        }

        var due = new List<DueTab>();
        var protectedTitles = new HashSet<string>();   // a title with a live copy that is active or not yet idle: never end it
        lock (idleSince) {
            foreach (var e in entries) {
                var t = e.Key; string key = e.Value; string title = BrowserTaskManager.CleanTitle(t.Name);
                if (t.Name.EndsWith(" - Crashed")) continue;            // already ended: no process, ignore it
                counts[0]++;
                if (t.Selected) { idleSince.Remove(key); protectedTitles.Add(title); continue; }   // active: keep this title
                counts[1]++;
                TabState st;
                if (!idleSince.TryGetValue(key, out st)) { idleSince[key] = new TabState { Since = now }; protectedTitles.Add(title); continue; }
                double idleMin = (now - st.Since).TotalMinutes;
                if (idleMin < bigLimit) { protectedTitles.Add(title); continue; }   // a copy not idle long enough yet
                string hit = Allowed(title, allowNow);
                if (hit != null) { if (reported.Add(key)) notes.Add("Kept \"" + Short(title) + "\" (allow-listed: " + hit + ")"); continue; }
                if (idleMin >= limit) due.Add(new DueTab { Key = key, Title = title, Why = "not opened for " + (int)idleMin + " min" });
                else due.Add(new DueTab { Key = key, Title = title, Why = "not opened for " + (int)idleMin + " min", NeedsMemory = true });
            }
            foreach (var k in new List<string>(idleSince.Keys)) if (!seen.Contains(k)) idleSince.Remove(k);   // closed tabs
            reported.RemoveWhere(k => !seen.Contains(k));
        }
        if (due.Count == 0) return;                                     // nothing due: the Task Manager isn't touched past seeding
        if (!getTm()) return;                                          // couldn't reach the Task Manager this round

        // 2. read the whole tab->process map ONCE, then end the due tabs straight from it (no per-tab search), so a
        //    backlog clears quickly; the Task Manager's filter is already cleared before any process is ended
        ensureMap();
        int ends = 0, fallbacks = 0;
        var doneTitles = new HashSet<string>();
        foreach (var d in due) {
            if (stopped || ends >= MaxEndsPerScan) break;
            // same title open more than once: end only when no live copy is active or still within its idle time
            // (a crashed copy is already ended and has no process, so it never blocks ending the live one)
            if (protectedTitles.Contains(d.Title) || !doneTitles.Add(d.Title)) continue;
            List<int> pids;
            if (!map.TryGetValue(d.Title, out pids) || pids.Count == 0) {
                // not in the map. If the map couldn't read the whole (long) list, look this tab up directly rather than
                // assuming it's asleep; never reset the idle clock for a tab we simply didn't see, or it would never end.
                if (!mapComplete && fallbacks < 10) {
                    fallbacks++;
                    var found = BrowserTaskManager.FindTab(tm, d.Title);
                    pids = new List<int>(found.Keys);
                }
                if (pids == null || pids.Count == 0) continue;   // truly asleep (no process): leave the clock, skip
            }
            foreach (var pid in pids) {
                if (stopped || ends >= MaxEndsPerScan) break;
                if (!mem.ContainsKey(pid)) continue;                    // not a tab process
                string why = d.Why;
                if (d.NeedsMemory) {                                    // not past the full limit yet: only if it's a big tab
                    if (mem[pid] < bigMB) continue;
                    why += ", using " + (int)mem[pid] + " MB";
                }
                if (dry) { if (reported.Add(d.Key)) notes.Add("WOULD END " + pid + " (" + why + "): " + Short(d.Title)); continue; }
                try { using (var p = Process.GetProcessById(pid)) p.Kill(); ended.Add(pid + " (" + why + "): " + Short(d.Title)); ends++; }
                catch (Exception ex) { notes.Add("Could not end " + pid + ": " + ex.Message); }
                lock (idleSince) idleSince.Remove(d.Key);
                Thread.Sleep(120);                                      // light pacing, so the browser isn't hit all at once
            }
        }
    }

    static string Allowed(string text, List<string> allowNow) {
        foreach (var a in allowNow) if (text.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) return a;
        return null;
    }

    // ---- idle clocks kept on disk, so closing and reopening Tab Cleaner (or a Windows restart) doesn't reset them
    void LoadState() {
        try {
            if (!File.Exists(StatePath)) return;
            DateTime now = DateTime.Now;
            foreach (var line in File.ReadAllLines(StatePath)) {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                long ticks;
                if (!long.TryParse(line.Substring(0, tab), out ticks)) continue;
                var when = new DateTime(ticks);
                if (when > now || (now - when).TotalDays > 7) continue;      // ignore the far future or very stale
                idleSince[line.Substring(tab + 1)] = new TabState { Since = when };
            }
        } catch { }
    }
    void SaveState() {
        try {
            var sb = new System.Text.StringBuilder();
            lock (idleSince) foreach (var kv in idleSince) {
                string key = kv.Key.Replace("\r", " ").Replace("\n", " ");
                sb.Append(kv.Value.Since.Ticks).Append('\t').Append(key).Append("\r\n");
            }
            File.WriteAllText(StatePath, sb.ToString());
        } catch { }
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
        SaveState();
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
        string tip = "Tab Cleaner v" + Program.Version + " - " + (stopped ? "stopped" : "running, " + idleMinutes + " min limit") + ", " + endedToday + " ended today";
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
        if (idleMinutes < 1) idleMinutes = 15;
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

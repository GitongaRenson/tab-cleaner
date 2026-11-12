// Tab Cleaner: a taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, using the
// browser's own Task Manager (Shift+Esc) to tell real tabs from things embedded in them. Nothing inside the browser
// is changed.
//
// How it decides:
//  1. Every minute it looks at WorkStream's tab processes. Chromium drops a process to Idle priority while its tab isn't
//     on screen and raises it when it is (the tab on screen, tabs in other visible windows and tabs playing sound stay
//     Normal = active, never ended). A process that has stayed at Idle priority for the whole limit is due, unless it
//     is busy (using real CPU each minute, e.g. an upload or processing). The tab each window had on screen last is
//     kept too, even while the window is covered.
//  2. For due processes it reads WorkStream's Task Manager (the whole list, scrolling through it), which names what
//     each process holds:
//       "Tab: <title>"            a tab - the only kind Tab Cleaner ends
//       "Subframe: https://site/" something embedded inside a tab (ads, videos, maps...) - never ended on its own:
//                                 ending it breaks the page around it, and it closes anyway when its tab is ended
//  3. A process holding an allow-listed tab ("Google Maps", "Dataminr", ...) is never ended.
//  4. Otherwise it ends exactly that process - what the Task Manager's "End task" does. (The button itself is not
//     pressed: with the Task Manager kept off-screen its selection can't be trusted.)

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
    public const string Version = "1.2.1";

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

class Tracked {
    public DateTime IdleSince;      // first time seen at Idle priority (not on screen) in this stretch
    public double LastCpu;          // CPU seconds at the previous check (to see if it's busy right now)
}

class Due { public int Pid; public int IdleMin; public string Why; }

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

    /// Every row of the list: the table only exposes the rows in view, so it scrolls through all of them.
    public static List<TmRow> ReadAll(AutomationElement tm) {
        var all = new List<TmRow>();
        var seen = new HashSet<string>();
        var grid = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
        if (grid == null) return all;
        ScrollPattern scroll = null;
        object sp;
        for (var e = grid; e != null && scroll == null; e = TreeWalker.RawViewWalker.GetParent(e))
            if (e.TryGetCurrentPattern(ScrollPattern.Pattern, out sp) && ((ScrollPattern)sp).Current.VerticallyScrollable) scroll = (ScrollPattern)sp;
        if (scroll != null) try { scroll.SetScrollPercent(ScrollPattern.NoScroll, 0); Thread.Sleep(120); } catch { }
        for (int page = 0; page < 40; page++) {
            foreach (var r in ReadVisible(grid)) {
                string key = r.Pid + "|" + r.Task;
                if (seen.Add(key)) all.Add(r);
            }
            if (scroll == null) break;
            double before;
            try { before = scroll.Current.VerticalScrollPercent; } catch { break; }
            if (before < 0 || before >= 99.5) break;
            try { scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); } catch { break; }
            Thread.Sleep(120);
            double after;
            try { after = scroll.Current.VerticalScrollPercent; } catch { break; }
            if (after <= before) break;
        }
        if (scroll != null) try { scroll.SetScrollPercent(ScrollPattern.NoScroll, 0); } catch { }
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
    double busyCpuPerMinute = 6;    // CPU seconds in one minute that count as "still working" (10% of a core)
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
    readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();
    readonly Dictionary<int, DateTime> lastOnScreen = new Dictionary<int, DateTime>();
    readonly Dictionary<int, DateTime> keepUntil = new Dictionary<int, DateTime>();   // checked in the Task Manager: keep
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
        foreach (int m in new int[] { 15, 30, 45, 60, 90 }) {
            int mins = m;
            var it = new ToolStripMenuItem(m + " minutes");
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
        tracked.Clear();                                                // idle time counts again from now
        keepUntil.Clear();
        SaveSettings();
        RefreshMenu();
        Log(stop ? "Stopped" : "Started (idle time counts from now)");
        tray.ShowBalloonTip(3000, "Tab Cleaner", stop ? "Stopped. No tabs will be ended until you start it again."
                                                        : "Started. Tabs not opened for " + idleMinutes + " minutes will be ended.", ToolTipIcon.Info);
        if (!stop) Check();
    }

    // ---- step 1 (every minute, on the tray's thread): which tab processes have been idle long enough
    void Check() {
        if (DateTime.Today != today) { today = DateTime.Today; endedToday = 0; }
        if (File.Exists(IniPath) && File.GetLastWriteTime(IniPath) != iniStamp) {
            int keepLimit = idleMinutes; bool wasStopped = stopped;
            LoadSettings();
            if (dryRun) idleMinutes = keepLimit;
            keepUntil.Clear();
            if (wasStopped != stopped) tracked.Clear();
            Log("Settings reloaded; never: " + string.Join(", ", allow.ToArray()));
        }
        if (stopped) { RefreshMenu(); return; }
        if (working) return;
        var seen = new HashSet<int>();
        var due = new List<Due>();
        int tabs = 0, idle = 0, mainPid = 0;
        DateTime now = DateTime.Now;
        try {
            string q = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + exeName.Replace("'", "") + "'";
            using (var s = new ManagementObjectSearcher(q))
            foreach (ManagementObject mo in s.Get()) {
                string cmd = (mo["CommandLine"] as string) ?? "";
                int pid = Convert.ToInt32(mo["ProcessId"]);
                if (cmd.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0) { mainPid = pid; continue; }   // the browser
                if (cmd.IndexOf("--type=renderer", StringComparison.OrdinalIgnoreCase) < 0) continue;            // GPU, helpers
                if (cmd.IndexOf("--extension-process", StringComparison.OrdinalIgnoreCase) >= 0) continue;       // extensions
                Process p;
                try { p = Process.GetProcessById(pid); } catch { continue; }
                using (p) {
                    tabs++;
                    seen.Add(pid);
                    ProcessPriorityClass prio;
                    double cpu, memMB;
                    try { prio = p.PriorityClass; cpu = p.TotalProcessorTime.TotalSeconds; memMB = p.PrivateMemorySize64 / (1024.0 * 1024.0); } catch { continue; }
                    if (prio != ProcessPriorityClass.Idle) { tracked.Remove(pid); lastOnScreen[pid] = now; continue; }   // active
                    idle++;
                    Tracked t;
                    if (!tracked.TryGetValue(pid, out t)) { tracked[pid] = new Tracked { IdleSince = now, LastCpu = cpu }; continue; }
                    double lastMinute = cpu - t.LastCpu;
                    t.LastCpu = cpu;
                    if (lastMinute > busyCpuPerMinute) { t.IdleSince = now; continue; }            // busy: still working
                    DateTime until;
                    if (keepUntil.TryGetValue(pid, out until) && until > now) continue;            // already checked: keep
                    double idleMin = (now - t.IdleSince).TotalMinutes;
                    if (idleMin >= idleMinutes)
                        due.Add(new Due { Pid = pid, IdleMin = (int)idleMin, Why = "not on screen " + (int)idleMin + " min" });
                    else if (idleMin >= bigTabMinutes && BigTabMB > 0 && memMB >= BigTabMB)   // a big tab: sooner
                        due.Add(new Due { Pid = pid, IdleMin = (int)idleMin, Why = "not on screen " + (int)idleMin + " min, using " + (int)memMB + " MB" });
                }
            }
            // the current tab of each window: the tab process(es) seen on screen most recently
            DateTime newest = DateTime.MinValue;
            foreach (var kv in lastOnScreen) if (seen.Contains(kv.Key) && kv.Value > newest) newest = kv.Value;
            due.RemoveAll(d => {
                DateTime when;
                return newest != DateTime.MinValue && lastOnScreen.TryGetValue(d.Pid, out when) && (newest - when).TotalSeconds < 90;
            });
        } catch (Exception e) { Log("Check failed: " + e.Message); }
        foreach (var pid in new List<int>(tracked.Keys)) if (!seen.Contains(pid)) tracked.Remove(pid);
        foreach (var pid in new List<int>(lastOnScreen.Keys)) if (!seen.Contains(pid)) lastOnScreen.Remove(pid);
        foreach (var pid in new List<int>(keepUntil.Keys)) if (!seen.Contains(pid)) keepUntil.Remove(pid);
        lastTabCount = tabs;
        lastIdleCount = idle;
        RefreshMenu();
        if (due.Count == 0 || mainPid == 0) return;

        // ---- step 2 (in the background): look them up in WorkStream's Task Manager and end idle tabs
        working = true;
        var allowNow = new List<string>(allow);
        bool dry = dryRun;
        int browser = mainPid;
        var t2 = new Thread(delegate () {
            var ended = new List<string>();
            var notes = new List<string>();
            var keep = new List<int>();
            try {
                var tm = BrowserTaskManager.Find(browser);
                if (tm == null) { tm = BrowserTaskManager.Open(browser); if (tm != null) notes.Add("Opened WorkStream's Task Manager (kept off-screen)"); }
                else BrowserTaskManager.Park(tm);
                if (tm == null) { notes.Add("Could not open WorkStream's Task Manager; nothing ended"); }
                else {
                    var rows = BrowserTaskManager.ReadAll(tm);
                    foreach (var d in due) {
                        if (stopped) break;
                        var tasks = new List<string>();
                        foreach (var r in rows) if (r.Pid == d.Pid) tasks.Add(r.Task);
                        string what = string.Join(" | ", tasks.ToArray());
                        if (tasks.Count == 0) { keep.Add(d.Pid); continue; }                         // not listed (spare process)
                        bool isTab = false;
                        foreach (var x in tasks) if (x.StartsWith("Tab:")) isTab = true;
                        if (!isTab) { keep.Add(d.Pid); continue; }                                   // embedded content only
                        string hit = null;
                        foreach (var x in tasks) foreach (var a in allowNow)
                            if (hit == null && x.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0) hit = a;
                        if (hit != null) { keep.Add(d.Pid); notes.Add("Kept " + d.Pid + " (allow-listed: " + hit + "): " + Short(what)); continue; }
                        if (dry) { keep.Add(d.Pid); notes.Add("WOULD END " + d.Pid + " (" + d.Why + "): " + Short(what)); continue; }
                        bool gone = false;
                        try { using (var p = Process.GetProcessById(d.Pid)) p.Kill(); } catch { }
                        for (int i = 0; i < 12 && !gone; i++) { Thread.Sleep(250); gone = !Alive(d.Pid); }
                        if (gone) ended.Add(d.Pid + " (" + d.Why + "): " + Short(what));
                        else notes.Add("Could not end " + d.Pid + ": " + Short(what));
                    }
                }
            } catch (Exception e) { notes.Add("Task Manager step failed: " + e.Message); }
            ui.Post(delegate { Finished(ended, notes, keep); }, null);
        });
        t2.IsBackground = true;
        t2.SetApartmentState(ApartmentState.MTA);                       // UI Automation works best off the UI thread
        t2.Start();
    }

    double BigTabMB { get { return totalMemoryMB * bigTabPercent / 100.0; } }

    static bool Alive(int pid) {
        try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; } catch { return false; }
    }
    static string Short(string s) { return s.Length > 160 ? s.Substring(0, 160) + "..." : s; }

    void Finished(List<string> ended, List<string> notes, List<int> keep) {
        working = false;
        foreach (var pid in keep) keepUntil[pid] = DateTime.Now.AddMinutes(10);         // look again in 10 minutes
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
        return "Running: " + lastIdleCount + " of " + lastTabCount + " tab processes not on screen. " + ended + "." + (dryRun ? " DRY RUN" : "");
    }

    void RefreshMenu() {
        toggleItem.Text = stopped ? "Start Tab Cleaner" : "Stop Tab Cleaner";
        statusItem.Text = StatusText();
        notifyItem.Checked = notify;
        startupItem.Checked = IsStartup();
        foreach (ToolStripMenuItem it in limitMenu.DropDownItems) it.Checked = it.Text == idleMinutes + " minutes";
        limitMenu.Text = "Idle limit: " + idleMinutes + " minutes (" + bigTabMinutes + " for tabs over " + bigTabPercent + "% memory)";
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
            else if (k == "busyCpuPerMinute") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out busyCpuPerMinute);
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
          + "# a background tab using more CPU seconds than this in a minute is still working and is not ended\r\n"
          + "busyCpuPerMinute=" + busyCpuPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"
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

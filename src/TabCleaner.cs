// Tab Cleaner: a taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes, using the
// browser's own Task Manager (Shift+Esc) and its "End task" button. Nothing inside the browser is changed.
//
// How it decides:
//  1. Every minute it looks at WorkStream's tab processes. Chromium drops a process to Idle priority while its tab isn't
//     on screen and raises it when it is (the tab on screen, tabs in other visible windows and tabs playing sound stay
//     Normal = active, never ended). A process that has stayed at Idle priority for the whole limit, using almost no
//     CPU, is due. The tab each window had on screen last is kept too, even while the window is covered.
//  2. For due processes it reads WorkStream's Task Manager, which names what each process holds:
//       "Tab: <title>"            a tab (the only kind Tab Cleaner ends)
//       "Subframe: https://site/" something embedded inside a tab (ads, videos, maps...) - never ended on its own:
//                                 ending it breaks the page around it, and it closes anyway when its tab is ended
//  3. A process holding an allow-listed tab ("Google Maps", "Dataminr", ...) is never ended.
//  4. Otherwise it selects that row in the Task Manager, checks it is the right process, and presses "End task".
// The Task Manager window is kept open off-screen so it doesn't get in the way.

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
    [STAThread]
    static void Main(string[] args) {
        bool created;
        using (new Mutex(true, "TabCleaner.SingleInstance", out created)) {
            if (!created) return;                                       // already running in the tray
            Application.EnableVisualStyles();
            Application.Run(new TrayApp(args));
        }
    }
}

class Tracked {
    public DateTime IdleSince;      // first time seen at Idle priority (not on screen) in this stretch
    public double CpuAtStart;       // CPU seconds used when that stretch began
}

class Due { public int Pid; public int IdleMin; public double Cpu; }

class TmRow { public string Task; public int Pid; public AutomationElement TaskCell; public AutomationElement PidCell; }

/// WorkStream's own Task Manager (Shift+Esc), read and used through Windows UI Automation.
static class BrowserTaskManager {
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static AutomationElement Windows(int mainPid, bool taskManager) {
        var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, mainPid);
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, cond)) {
            if (w.Current.Name.StartsWith("Task Manager", StringComparison.OrdinalIgnoreCase) == taskManager) return w;
        }
        return null;
    }

    public static AutomationElement Find(int mainPid) { return Windows(mainPid, true); }

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

    /// Off-screen and tall, so every row is laid out (the table only exposes rows that fit in the window).
    public static void Park(AutomationElement tm) {
        try {
            var r = tm.Current.BoundingRectangle;
            if (r.Left > -3000) SetWindowPos(new IntPtr(tm.Current.NativeWindowHandle), IntPtr.Zero, -4000, 0, 900, 3000, 0x0014);
        } catch { }
    }

    public static List<TmRow> Read(AutomationElement tm) {
        var rows = new List<TmRow>();
        var grid = tm.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
        if (grid == null) return rows;
        var walk = TreeWalker.RawViewWalker;
        int lastPid = 0;
        for (var row = walk.GetFirstChild(grid); row != null; row = walk.GetNextSibling(row)) {
            var cells = new List<AutomationElement>();
            for (var c = walk.GetFirstChild(row); c != null; c = walk.GetNextSibling(c)) cells.Add(c);
            if (cells.Count < 5) continue;
            string task = cells[0].Current.Name;
            if (task.StartsWith("Column ")) continue;                   // header
            string pidText = cells[cells.Count - 1].Current.Name;
            int pid;
            if (int.TryParse(pidText, out pid)) lastPid = pid; else pid = lastPid;   // no ID: same process as the row above
            rows.Add(new TmRow { Task = task, Pid = pid, TaskCell = cells[0], PidCell = cells[cells.Count - 1] });
        }
        return rows;
    }

    /// Selects the process's row and presses "End task" (after checking the selected row is still that process).
    /// Returns "" when pressed, otherwise why it couldn't be.
    public static string EndTask(AutomationElement tm, int pid) {
        TmRow row = null;
        foreach (var r in Read(tm)) if (r.Pid == pid && r.PidCell.Current.Name == pid.ToString()) { row = r; break; }
        if (row == null) return "row not found";
        object sel;
        if (!row.TaskCell.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sel)) return "row can't be selected";
        try { ((SelectionItemPattern)sel).Select(); } catch (Exception e) { return "select failed: " + e.Message; }
        var btnCond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                       new PropertyCondition(AutomationElement.NameProperty, "End task"));
        AutomationElement btn = null;
        for (int i = 0; i < 10; i++) {                                  // wait for the selection and the button
            Thread.Sleep(100);
            btn = tm.FindFirst(TreeScope.Descendants, btnCond);
            if (btn != null && btn.Current.IsEnabled && ((SelectionItemPattern)sel).Current.IsSelected) break;
        }
        if (btn == null) return "End task button not found";
        if (!btn.Current.IsEnabled) return "End task button disabled";
        if (!((SelectionItemPattern)sel).Current.IsSelected) return "row not selected";
        if (row.PidCell.Current.Name != pid.ToString()) return "list moved";
        object inv;
        if (!btn.TryGetCurrentPattern(InvokePattern.Pattern, out inv)) return "button can't be pressed";
        try { ((InvokePattern)inv).Invoke(); } catch (Exception e) { return "press failed: " + e.Message; }
        return "";
    }
}

class TrayApp : ApplicationContext {
    // ---- settings (%LOCALAPPDATA%\TabCleaner\settings.ini)
    int idleMinutes = 30;
    bool paused = false;
    bool notify = true;
    double maxCpuSeconds = 5;       // a background tab using more CPU than this during the idle time is still working
    List<string> allow = new List<string> { "Google Maps", "Dataminr", "dataminr.com", "google.com/maps", "maps.google" };
    string exeName = "workstream.exe";
    bool dryRun = false;            // --dry-run: only log what would be ended
    DateTime iniStamp = DateTime.MinValue;

    readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabCleaner");
    string LogPath { get { return Path.Combine(dir, "log.txt"); } }
    string IniPath { get { return Path.Combine(dir, "settings.ini"); } }

    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem statusItem = new ToolStripMenuItem();
    readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem("Pause");
    readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows");
    readonly ToolStripMenuItem notifyItem = new ToolStripMenuItem("Show a message when tabs are ended");
    readonly ToolStripMenuItem limitMenu = new ToolStripMenuItem("Idle limit");
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();
    readonly Dictionary<int, DateTime> lastOnScreen = new Dictionary<int, DateTime>();
    readonly Dictionary<int, DateTime> keepUntil = new Dictionary<int, DateTime>();   // checked in the Task Manager: keep
    readonly SynchronizationContext ui;
    volatile bool working = false;
    int endedToday = 0, keptCount = 0;
    DateTime today = DateTime.Today;
    int lastTabCount = 0, lastIdleCount = 0;
    Icon iconOn, iconPaused;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "TabCleaner";

    public TrayApp(string[] args) {
        Directory.CreateDirectory(dir);
        if (!File.Exists(IniPath)) SaveSettings();
        LoadSettings();
        for (int i = 0; i < args.Length; i++) {                         // testing switches (not saved)
            if (args[i] == "--exe" && i + 1 < args.Length) exeName = args[++i];
            else if (args[i] == "--minutes" && i + 1 < args.Length) idleMinutes = int.Parse(args[++i]);
            else if (args[i] == "--dry-run") dryRun = true;
        }
        iconOn = MakeIcon(false);
        iconPaused = MakeIcon(true);

        var menu = new ContextMenuStrip();
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
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
        pauseItem.Click += delegate { paused = !paused; if (paused) tracked.Clear(); SaveSettings(); RefreshMenu(); Log(paused ? "Paused" : "Resumed"); };
        menu.Items.Add(pauseItem);
        notifyItem.Click += delegate { notify = !notify; SaveSettings(); RefreshMenu(); };
        menu.Items.Add(notifyItem);
        startupItem.Click += delegate { SetStartup(!IsStartup()); RefreshMenu(); };
        menu.Items.Add(startupItem);
        menu.Items.Add("Open log", null, delegate { if (!File.Exists(LogPath)) File.WriteAllText(LogPath, ""); Process.Start("notepad.exe", "\"" + LogPath + "\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { tray.Visible = false; Log("Exited"); ExitThread(); });
        menu.Opening += delegate { RefreshMenu(); };

        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate { tray.ShowBalloonTip(4000, "Tab Cleaner", StatusText(), ToolTipIcon.Info); };
        RefreshMenu();

        Log("Started" + (dryRun ? " (DRY RUN: nothing will be ended)" : "") + ": ends " + exeName + " tabs idle for "
            + idleMinutes + " minutes; never: " + string.Join(", ", allow.ToArray()));
        timer.Interval = 60 * 1000;                                     // look once a minute: light on the PC
        timer.Tick += delegate { Check(); };
        timer.Start();
        Check();
    }

    // ---- step 1 (every minute, on the tray's thread): which tab processes have been idle long enough
    void Check() {
        if (DateTime.Today != today) { today = DateTime.Today; endedToday = 0; }
        if (File.Exists(IniPath) && File.GetLastWriteTime(IniPath) != iniStamp) {
            int keepLimit = idleMinutes; LoadSettings(); if (dryRun) idleMinutes = keepLimit;
            keepUntil.Clear();
            Log("Settings reloaded; never: " + string.Join(", ", allow.ToArray()));
        }
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
                    double cpu;
                    try { prio = p.PriorityClass; cpu = p.TotalProcessorTime.TotalSeconds; } catch { continue; }
                    if (prio != ProcessPriorityClass.Idle) { tracked.Remove(pid); lastOnScreen[pid] = now; continue; }   // active
                    idle++;
                    Tracked t;
                    if (!tracked.TryGetValue(pid, out t)) { t = new Tracked { IdleSince = now, CpuAtStart = cpu }; tracked[pid] = t; continue; }
                    double used = cpu - t.CpuAtStart;
                    if (used > maxCpuSeconds) { t.IdleSince = now; t.CpuAtStart = cpu; continue; }   // still working
                    DateTime until;
                    if (keepUntil.TryGetValue(pid, out until) && until > now) continue;            // already checked: keep
                    if ((now - t.IdleSince).TotalMinutes >= idleMinutes)
                        due.Add(new Due { Pid = pid, IdleMin = (int)(now - t.IdleSince).TotalMinutes, Cpu = used });
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
        if (due.Count == 0 || paused || mainPid == 0) return;

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
                    var rows = BrowserTaskManager.Read(tm);
                    foreach (var d in due) {
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
                        if (dry) { keep.Add(d.Pid); notes.Add("WOULD END " + d.Pid + " (not on screen " + d.IdleMin + " min): " + Short(what)); continue; }
                        string why = BrowserTaskManager.EndTask(tm, d.Pid);
                        bool gone = false;
                        for (int i = 0; i < 12 && !gone; i++) { Thread.Sleep(250); gone = !Alive(d.Pid); }
                        string how = why == "" ? "End task" : "ended directly (End task: " + why + ")";
                        if (!gone) {                                     // the same process the Task Manager listed for this tab
                            if (why == "") how = "ended directly (End task pressed, process still running)";
                            try { using (var p = Process.GetProcessById(d.Pid)) p.Kill(); } catch { }
                            for (int i = 0; i < 8 && !gone; i++) { Thread.Sleep(250); gone = !Alive(d.Pid); }
                        }
                        if (gone) ended.Add(d.Pid + " (not on screen " + d.IdleMin + " min, " + how + "): " + Short(what));
                        else notes.Add("Could not end " + d.Pid + " (" + how + "): " + Short(what));
                    }
                }
            } catch (Exception e) { notes.Add("Task Manager step failed: " + e.Message); }
            ui.Post(delegate { Finished(ended, notes, keep); }, null);
        });
        t2.IsBackground = true;
        t2.SetApartmentState(ApartmentState.MTA);                       // UI Automation works best off the UI thread
        t2.Start();
    }

    static bool Alive(int pid) {
        try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; } catch { return false; }
    }
    static string Short(string s) { return s.Length > 160 ? s.Substring(0, 160) + "..." : s; }

    void Finished(List<string> ended, List<string> notes, List<int> keep) {
        working = false;
        foreach (var pid in keep) keepUntil[pid] = DateTime.Now.AddMinutes(10);         // look again in 10 minutes
        keptCount = keepUntil.Count;
        foreach (var n in notes) Log(n);
        foreach (var e in ended) Log("Ended tab " + e);
        if (ended.Count > 0) {
            endedToday += ended.Count;
            if (notify) tray.ShowBalloonTip(5000, "Tab Cleaner",
                "Ended " + ended.Count + " idle tab" + (ended.Count == 1 ? "" : "s") + " (not opened for " + idleMinutes + "+ minutes). Reload a tab to bring it back.",
                ToolTipIcon.Info);
        }
        RefreshMenu();
    }

    // ---- menu and icon
    string StatusText() {
        string ended = endedToday + " ended today";
        if (paused) return "Paused. " + ended + ".";
        return lastTabCount + " tab processes, " + lastIdleCount + " not on screen. " + ended + ". Limit " + idleMinutes + " min."
             + (dryRun ? " DRY RUN" : "");
    }

    void RefreshMenu() {
        statusItem.Text = StatusText();
        pauseItem.Text = paused ? "Resume" : "Pause";
        notifyItem.Checked = notify;
        startupItem.Checked = IsStartup();
        foreach (ToolStripMenuItem it in limitMenu.DropDownItems) it.Checked = it.Text == idleMinutes + " minutes";
        limitMenu.Text = "Idle limit: " + idleMinutes + " minutes";
        tray.Icon = paused ? iconPaused : iconOn;
        string tip = "Tab Cleaner: " + StatusText();
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    /// Small original icon: a browser tab with a clock, grey when paused.
    static Icon MakeIcon(bool paused) {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color main = paused ? Color.FromArgb(150, 150, 150) : Color.FromArgb(32, 132, 230);
            using (var b = new SolidBrush(main)) { g.FillRectangle(b, 2, 9, 28, 20); g.FillRectangle(b, 2, 4, 13, 7); }
            using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, 9, 12, 14, 14);
            using (var pen = new Pen(main, 2f)) { g.DrawLine(pen, 16, 19, 16, 14); g.DrawLine(pen, 16, 19, 20, 21); }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---- start with Windows (current user only)
    bool IsStartup() {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
    }
    void SetStartup(bool on) {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
            if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunName, false);
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
            else if (k == "paused") paused = v == "true";
            else if (k == "notify") notify = v != "false";
            else if (k == "maxCpuSeconds") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out maxCpuSeconds);
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
          + "idleMinutes=" + idleMinutes + "\r\npaused=" + (paused ? "true" : "false") + "\r\nnotify=" + (notify ? "true" : "false")
          + "\r\nmaxCpuSeconds=" + maxCpuSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
        iniStamp = File.GetLastWriteTime(IniPath);
    }
    void Log(string msg) {
        try {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
        } catch { }
    }
}

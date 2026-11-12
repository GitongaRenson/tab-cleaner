// WorkStream Tab Cleaner: a taskbar (system tray) app that ends WorkStream Browser tabs left idle for 30 minutes.
//
// How it tells a tab is idle, from outside the browser (nothing inside WorkStream is changed):
//  - WorkStream is Chromium-based: every tab runs in its own "renderer" process.
//  - Chromium drops a tab's process to Idle priority while the tab is not being viewed and back to Normal when it
//    is (tabs playing sound, the tab on screen and tabs in other visible windows stay Normal).
//  - A tab process that has stayed at Idle priority for the whole limit AND used almost no CPU in that time is idle:
//    it gets ended, exactly like "End task" in Task Manager (the tab shows a reload page; reloading brings it back).
// Never touched: the browser itself, extensions (CloudFactory's included), the WorkStream monitor, GPU and helpers.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program {
    [STAThread]
    static void Main(string[] args) {
        bool created;
        using (new Mutex(true, "WorkStreamTabCleaner.SingleInstance", out created)) {
            if (!created) return;                                       // already running in the tray
            Application.EnableVisualStyles();
            Application.Run(new TrayApp(args));
        }
    }
}

class Tracked {
    public DateTime IdleSince;      // first time seen at Idle priority (unviewed) in this stretch
    public double CpuAtStart;       // CPU seconds used when that stretch began
}

class TrayApp : ApplicationContext {
    // ---- settings (saved in %LOCALAPPDATA%\WorkStreamTabCleaner\settings.ini)
    int idleMinutes = 30;
    bool paused = false;
    bool notify = true;
    double maxCpuSeconds = 5;       // a background tab using more CPU than this during the idle time is still working
    string exeName = "workstream.exe";
    string mustContain = "";        // optional: only processes whose command line contains this (testing)

    readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkStreamTabCleaner");
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
    // when each tab process was last seen on screen (Normal priority). The tab(s) on screen most recently are the
    // current tab of each window: kept even when the whole WorkStream window is covered or minimised.
    readonly Dictionary<int, DateTime> lastOnScreen = new Dictionary<int, DateTime>();
    DateTime checkTime;
    int endedToday = 0;
    DateTime today = DateTime.Today;
    int lastTabCount = 0, lastIdleCount = 0;
    Icon iconOn, iconPaused;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "WorkStreamTabCleaner";

    public TrayApp(string[] args) {
        Directory.CreateDirectory(dir);
        LoadSettings();
        for (int i = 0; i < args.Length; i++) {                         // testing switches
            if (args[i] == "--exe" && i + 1 < args.Length) exeName = args[++i];
            else if (args[i] == "--match" && i + 1 < args.Length) mustContain = args[++i];
            else if (args[i] == "--minutes" && i + 1 < args.Length) idleMinutes = int.Parse(args[++i]);
        }
        iconOn = MakeIcon(false);
        iconPaused = MakeIcon(true);

        var menu = new ContextMenuStrip();
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
        pauseItem.Click += delegate { paused = !paused; if (paused) tracked.Clear(); SaveSettings(); RefreshMenu(); Log(paused ? "Paused" : "Resumed"); };
        menu.Items.Add(pauseItem);
        notifyItem.Click += delegate { notify = !notify; SaveSettings(); RefreshMenu(); };
        menu.Items.Add(notifyItem);
        startupItem.Click += delegate { SetStartup(!IsStartup()); RefreshMenu(); };
        menu.Items.Add(startupItem);
        menu.Items.Add("End idle tabs now", null, delegate { Check(true); });
        menu.Items.Add("Open log", null, delegate { if (!File.Exists(LogPath)) File.WriteAllText(LogPath, ""); Process.Start("notepad.exe", "\"" + LogPath + "\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { tray.Visible = false; Log("Exited"); ExitThread(); });
        menu.Opening += delegate { RefreshMenu(); };

        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate { tray.ShowBalloonTip(4000, "WorkStream Tab Cleaner", StatusText(), ToolTipIcon.Info); };
        RefreshMenu();

        Log("Started: ends " + exeName + " tabs idle for " + idleMinutes + " minutes" + (mustContain != "" ? " (only \"" + mustContain + "\")" : ""));
        timer.Interval = 60 * 1000;                                     // look once a minute: light on the PC
        timer.Tick += delegate { Check(false); };
        timer.Start();
        Check(false);
    }

    // ---- the check
    void Check(bool now) {
        if (DateTime.Today != today) { today = DateTime.Today; endedToday = 0; }
        if (paused && !now) { RefreshMenu(); return; }
        var seen = new HashSet<int>();
        var ended = 0;
        int tabs = 0, idle = 0;
        checkTime = DateTime.Now;
        var candidates = new List<KeyValuePair<int, Tracked>>();
        var cpuNow = new Dictionary<int, double>();
        try {
            string q = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + exeName.Replace("'", "") + "'";
            using (var s = new ManagementObjectSearcher(q))
            foreach (ManagementObject mo in s.Get()) {
                string cmd = (mo["CommandLine"] as string) ?? "";
                int pid = Convert.ToInt32(mo["ProcessId"]);
                if (!IsTab(cmd)) continue;
                if (mustContain != "" && cmd.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Process p;
                try { p = Process.GetProcessById(pid); } catch { continue; }
                using (p) {
                    tabs++;
                    seen.Add(pid);
                    ProcessPriorityClass prio;
                    double cpu;
                    try { prio = p.PriorityClass; cpu = p.TotalProcessorTime.TotalSeconds; } catch { continue; }
                    if (prio != ProcessPriorityClass.Idle) { tracked.Remove(pid); lastOnScreen[pid] = checkTime; continue; }   // on screen or playing sound
                    idle++;
                    Tracked t;
                    if (!tracked.TryGetValue(pid, out t)) { t = new Tracked { IdleSince = DateTime.Now, CpuAtStart = cpu }; tracked[pid] = t; continue; }
                    double used = cpu - t.CpuAtStart;
                    if (used > maxCpuSeconds) { t.IdleSince = DateTime.Now; t.CpuAtStart = cpu; continue; }  // still working in the background
                    if (paused) continue;
                    if ((DateTime.Now - t.IdleSince).TotalMinutes >= idleMinutes) { candidates.Add(new KeyValuePair<int, Tracked>(pid, t)); cpuNow[pid] = used; }
                }
            }
            // keep the current tab of each window: the tab process(es) seen on screen most recently
            DateTime newest = DateTime.MinValue;
            foreach (var kv in lastOnScreen) if (seen.Contains(kv.Key) && kv.Value > newest) newest = kv.Value;
            foreach (var c in candidates) {
                DateTime when;
                if (newest != DateTime.MinValue && lastOnScreen.TryGetValue(c.Key, out when) && (newest - when).TotalSeconds < 90) continue;
                try {
                    using (var p = Process.GetProcessById(c.Key)) p.Kill();
                    ended++;
                    tracked.Remove(c.Key);
                    Log("Ended tab process " + c.Key + ": unviewed for " + (int)(DateTime.Now - c.Value.IdleSince).TotalMinutes + " min, CPU used " + cpuNow[c.Key].ToString("0.0") + " s");
                } catch (Exception e) { Log("Could not end " + c.Key + ": " + e.Message); }
            }
        } catch (Exception e) { Log("Check failed: " + e.Message); }
        foreach (var pid in new List<int>(tracked.Keys)) if (!seen.Contains(pid)) tracked.Remove(pid);   // closed tabs
        foreach (var pid in new List<int>(lastOnScreen.Keys)) if (!seen.Contains(pid)) lastOnScreen.Remove(pid);
        lastTabCount = tabs - ended;
        lastIdleCount = idle - ended;
        if (ended > 0) {
            endedToday += ended;
            if (notify) tray.ShowBalloonTip(5000, "WorkStream Tab Cleaner",
                "Ended " + ended + " idle tab" + (ended == 1 ? "" : "s") + " (unviewed for " + idleMinutes + "+ minutes). Reload a tab to bring it back.", ToolTipIcon.Info);
        }
        RefreshMenu();
    }

    /// A tab's renderer process. Never: the browser, extensions, GPU, network/utility helpers, crash handler.
    static bool IsTab(string cmd) {
        if (cmd.IndexOf("--type=renderer", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (cmd.IndexOf("--extension-process", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (Regex.IsMatch(cmd, "chrome-extension://", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    // ---- menu and icon
    string StatusText() {
        if (paused) return "Paused. " + endedToday + " tab" + (endedToday == 1 ? "" : "s") + " ended today.";
        return lastTabCount + " WorkStream tab process" + (lastTabCount == 1 ? "" : "es") + ", " + lastIdleCount + " unviewed. "
             + endedToday + " ended today. Limit " + idleMinutes + " min.";
    }

    void RefreshMenu() {
        statusItem.Text = StatusText();
        pauseItem.Text = paused ? "Resume" : "Pause";
        notifyItem.Checked = notify;
        startupItem.Checked = IsStartup();
        foreach (ToolStripMenuItem it in limitMenu.DropDownItems) it.Checked = it.Text == idleMinutes + " minutes";
        limitMenu.Text = "Idle limit: " + idleMinutes + " minutes";
        tray.Icon = paused ? iconPaused : iconOn;
        string tip = "WorkStream Tab Cleaner: " + StatusText();
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    /// Small original icon: a browser tab with a clock, grey when paused.
    static Icon MakeIcon(bool paused) {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color main = paused ? Color.FromArgb(150, 150, 150) : Color.FromArgb(32, 132, 230);
            using (var b = new SolidBrush(main)) {
                g.FillRectangle(b, 2, 9, 28, 20);
                g.FillRectangle(b, 2, 4, 13, 7);
            }
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
        foreach (var line in File.ReadAllLines(IniPath)) {
            var kv = line.Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            string k = kv[0].Trim(), v = kv[1].Trim();
            if (k == "idleMinutes") int.TryParse(v, out idleMinutes);
            else if (k == "paused") paused = v == "true";
            else if (k == "notify") notify = v != "false";
            else if (k == "maxCpuSeconds") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out maxCpuSeconds);
        }
        if (idleMinutes < 1) idleMinutes = 30;
    }
    void SaveSettings() {
        File.WriteAllText(IniPath, "idleMinutes=" + idleMinutes + "\r\npaused=" + (paused ? "true" : "false") + "\r\nnotify=" + (notify ? "true" : "false")
            + "\r\nmaxCpuSeconds=" + maxCpuSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
    }
    void Log(string msg) {
        try {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
        } catch { }
    }
}

// TabCleaner-Setup.exe: installs Tab Cleaner for the current user (no administrator rights needed).
//  - copies TabCleaner.exe (carried inside this file) to %LOCALAPPDATA%\Programs\TabCleaner
//  - Start Menu shortcut (and optionally a desktop shortcut), optionally "Start with Windows"
//  - listed in Settings > Apps, so it can be uninstalled like any other app
// Silent use: TabCleaner-Setup.exe /silent [/nodesktop] [/nostartup] [/nolaunch]

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup {
    const string Version = "1.4.0";
    static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\TabCleaner"); } }
    static string ExePath { get { return Path.Combine(InstallDir, "TabCleaner.exe"); } }

    [STAThread]
    static void Main(string[] args) {
        bool silent = false, desktop = true, startup = true, launch = true;
        foreach (var a in args) {
            string x = a.ToLowerInvariant();
            if (x == "/silent") silent = true;
            else if (x == "/nodesktop") desktop = false;
            else if (x == "/nostartup") startup = false;
            else if (x == "/nolaunch") launch = false;
        }
        if (silent) {
            string err = Install(desktop, startup, launch);
            Environment.ExitCode = err == null ? 0 : 1;
            if (err != null) Console.Error.WriteLine(err);
            return;
        }
        Application.EnableVisualStyles();
        Application.Run(new SetupForm());
    }

    /// Returns null when installed, otherwise what went wrong.
    public static string Install(bool desktop, bool startup, bool launch) {
        try {
            foreach (var p in Process.GetProcessesByName("TabCleaner")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }   // updating
            Directory.CreateDirectory(InstallDir);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("TabCleaner.exe"))
            using (var f = File.Create(ExePath)) res.CopyTo(f);

            Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tab Cleaner.lnk"));
            string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Tab Cleaner.lnk");
            if (desktop) Shortcut(desk); else if (File.Exists(desk)) File.Delete(desk);

            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                if (startup) k.SetValue("TabCleaner", "\"" + ExePath + "\""); else k.DeleteValue("TabCleaner", false);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TabCleaner")) {
                k.SetValue("DisplayName", "Tab Cleaner");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Tab Cleaner");
                k.SetValue("DisplayIcon", ExePath);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + ExePath + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + ExePath + "\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024) + 1, RegistryValueKind.DWord);
            }
            if (launch) Process.Start(ExePath);
            return null;
        } catch (Exception e) { return e.Message; }
    }

    static void Shortcut(string lnk) {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { ExePath });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { InstallDir });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { ExePath + ",0" });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Ends idle WorkStream Browser tabs" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }
}

class SetupForm : Form {
    readonly CheckBox desktop = new CheckBox { Text = "Add a desktop shortcut", Checked = true, AutoSize = true };
    readonly CheckBox startup = new CheckBox { Text = "Start Tab Cleaner with Windows", Checked = true, AutoSize = true };
    readonly CheckBox launch = new CheckBox { Text = "Start Tab Cleaner now", Checked = true, AutoSize = true };

    public SetupForm() {
        Text = "Install Tab Cleaner";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var title = new Label { Text = "Tab Cleaner", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(20, 16) };
        var info = new Label {
            Text = "Ends WorkStream Browser tabs you haven't opened for 30 minutes, using the browser's Task Manager. "
                 + "Active tabs, tabs playing sound and allow-listed tabs (Google Maps, Dataminr) are never ended.\r\n\r\n"
                 + "It runs from the taskbar's notification area: click its icon to start or stop it.",
            Location = new Point(20, 56), Size = new Size(420, 100)
        };
        desktop.Location = new Point(22, 160);
        startup.Location = new Point(22, 186);
        launch.Location = new Point(22, 212);
        var install = new Button { Text = "Install", Location = new Point(264, 252), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", Location = new Point(358, 252), Size = new Size(82, 30), DialogResult = DialogResult.Cancel };
        install.Click += delegate {
            install.Enabled = false;
            Cursor = Cursors.WaitCursor;
            string err = Setup.Install(desktop.Checked, startup.Checked, launch.Checked);
            Cursor = Cursors.Default;
            if (err == null) {
                MessageBox.Show(this, "Tab Cleaner is installed." + (launch.Checked ? " Its icon is in the taskbar's notification area." : "")
                    + "\r\n\r\nTo remove it: Settings > Apps > Installed apps > Tab Cleaner.", "Tab Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            } else {
                MessageBox.Show(this, "Installation failed: " + err, "Tab Cleaner", MessageBoxButtons.OK, MessageBoxIcon.Error);
                install.Enabled = true;
            }
        };
        AcceptButton = install;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, info, desktop, startup, launch, install, cancel });
    }
}

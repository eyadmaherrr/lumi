// Lumi Setup: installs Lumi for the current user (no admin needed), adds Start menu / desktop
// shortcuts, optional start-with-Windows, and an entry in Settings > Apps with a real uninstaller.
// Lumi.exe is embedded in this exe as a resource by build.ps1.
//
//   LumiSetup.exe              show the installer window
//   LumiSetup.exe /S           install silently (start with Windows on, no desktop shortcut)
//   LumiSetup.exe /uninstall   remove Lumi (add /S to skip the questions)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lumi Setup")]
[assembly: AssemblyProduct("Lumi")]
[assembly: AssemblyDescription("Installs Lumi, a Dynamic Island for Windows")]
[assembly: AssemblyCopyright("Eyad Maher")]
[assembly: AssemblyVersion(LumiSetup.Setup.Version)]
[assembly: AssemblyFileVersion(LumiSetup.Setup.Version)]

namespace LumiSetup
{
    static class Setup
    {
        public const string AppName = "Lumi", Version = "1.0.0", Publisher = "Eyad Maher";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lumi";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lumi");
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");
        static string AppExe { get { return Path.Combine(InstallDir, "Lumi.exe"); } }
        static string Uninstaller { get { return Path.Combine(InstallDir, "Uninstall Lumi.exe"); } }
        static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lumi.lnk"); } }
        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lumi.lnk"); } }

        public static bool IsInstalled { get { return File.Exists(AppExe); } }

        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
            Application.EnableVisualStyles();
            try
            {
                if (uninstall)
                {
                    bool deleteData = false;
                    if (!silent)
                    {
                        if (MessageBox.Show("Remove Lumi from this PC?", "Uninstall Lumi", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                        deleteData = Directory.Exists(DataDir) && MessageBox.Show(
                            "Also delete your Lumi notes, todos, colours and settings?\n\nChoose No to keep them in case you install Lumi again.",
                            "Uninstall Lumi", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    }
                    Uninstall(deleteData);
                    if (!silent) MessageBox.Show("Lumi has been removed.", "Uninstall Lumi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (silent)
                {
                    Install(true, false, false);
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show(e.Message, "Lumi Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        // Closes Lumi (and its old name) so its files can be replaced or removed.
        static void StopLumi()
        {
            foreach (var p in Process.GetProcessesByName("Lumi").Concat(Process.GetProcessesByName("AgentsAlwaysOn")))
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
            }
        }

        public static void Install(bool startWithWindows, bool desktopShortcut, bool launch)
        {
            StopLumi();
            Directory.CreateDirectory(InstallDir);

            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lumi.exe"))
            {
                if (resource == null) throw new InvalidOperationException("This installer is missing Lumi.exe. Rebuild it with build.ps1.");
                using (var file = File.Create(AppExe)) resource.CopyTo(file);
            }
            var self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(self, Uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(self, Uninstaller, true);

            CreateShortcut(StartMenuLink, AppExe);
            if (desktopShortcut) CreateShortcut(DesktopLink, AppExe);

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                run.DeleteValue("AgentsAlwaysOn", false);
                if (startWithWindows) run.SetValue(AppName, "\"" + AppExe + "\"");
                else run.DeleteValue(AppName, false);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayIcon", AppExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + Uninstaller + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + Uninstaller + "\" /uninstall /S");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)((new FileInfo(AppExe).Length + new FileInfo(Uninstaller).Length) / 1024), RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }

            if (launch) Process.Start(AppExe);
        }

        public static void Uninstall(bool deleteData)
        {
            StopLumi();
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (run != null) run.DeleteValue(AppName, false);
            }
            foreach (var link in new[] { StartMenuLink, DesktopLink })
                if (File.Exists(link)) File.Delete(link);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (File.Exists(AppExe)) File.Delete(AppExe);
            if (deleteData && Directory.Exists(DataDir)) Directory.Delete(DataDir, true);

            // This uninstaller may be running from the install folder, so remove the folder after it exits.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        static void CreateShortcut(string path, string target)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object link = null;
            try
            {
                link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Lumi - a Dynamic Island for Windows" });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    // The installer window: dark, one screen, three choices.
    class SetupForm : Form
    {
        static readonly Color Background = Color.FromArgb(18, 18, 20), Muted = Color.FromArgb(152, 152, 158), Green = Color.FromArgb(48, 209, 88);
        readonly CheckBox startup, desktop, launch;
        readonly Button action;
        readonly Label status;
        bool done;

        public SetupForm()
        {
            Text = "Lumi Setup";
            ClientSize = new Size(460, 346);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.75f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Controls.Add(new Label { Text = "Lumi", Font = new Font("Segoe UI Semibold", 24f), AutoSize = true, Location = new Point(28, 20) });
            Controls.Add(new Label { Text = "v" + Setup.Version, ForeColor = Muted, AutoSize = true, Location = new Point(118, 36) });
            Controls.Add(new Label
            {
                Text = "A Dynamic Island for Windows. Music, focus timer, todos, notes, clipboard history, " +
                       "AI quick-ask, weather and more, in a notch at the top of your screen.",
                ForeColor = Muted,
                Location = new Point(30, 72),
                Size = new Size(400, 58)
            });
            Controls.Add(new Label
            {
                Text = "Installs for you only (no admin needed) into:\n" + Setup.InstallDir,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(30, 138),
                Size = new Size(400, 36)
            });

            startup = Check("Start Lumi when I sign in to Windows", 188, true);
            desktop = Check("Add a desktop shortcut", 216, false);
            launch = Check("Open Lumi when setup finishes", 244, true);

            status = new Label { ForeColor = Muted, Location = new Point(30, 296), Size = new Size(260, 24), TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(status);

            action = new Button
            {
                Text = Setup.IsInstalled ? "Update" : "Install",
                Size = new Size(124, 36),
                Location = new Point(306, 290),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Segoe UI Semibold", 10f),
                Cursor = Cursors.Hand
            };
            action.FlatAppearance.BorderSize = 0;
            action.Click += OnAction;
            Controls.Add(action);
            AcceptButton = action;
            if (Setup.IsInstalled) status.Text = "Lumi is already installed; this updates it.";
        }

        CheckBox Check(string text, int top, bool on)
        {
            var box = new CheckBox { Text = text, Checked = on, AutoSize = true, Location = new Point(30, top), FlatStyle = FlatStyle.Standard, ForeColor = Color.White, BackColor = Background };
            Controls.Add(box);
            return box;
        }

        void OnAction(object sender, EventArgs e)
        {
            if (done) { Close(); return; }
            action.Enabled = false;
            status.ForeColor = Muted;
            status.Text = "Installing…";
            Refresh();
            try
            {
                Setup.Install(startup.Checked, desktop.Checked, launch.Checked);
                done = true;
                status.ForeColor = Green;
                status.Text = launch.Checked ? "Installed. Look at the top of your screen." : "Installed. Find Lumi in the Start menu.";
                action.Text = "Close";
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.FromArgb(255, 69, 58);
                status.Text = "Couldn't install: " + ex.Message;
            }
            action.Enabled = true;
        }
    }
}

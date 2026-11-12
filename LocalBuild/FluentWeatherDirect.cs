// FluentWeatherDirect.cs
//
// The program behind FluentWeather-Plus-Setup.exe and FluentWeather-Plus-Uninstall.exe.
// Make-DirectInstaller.ps1 compiles this file twice with the C# compiler that ships with Windows:
//   without symbols       FluentWeather-Plus-Setup.exe      installs the app that was built on the sender's PC
//   /define:UNINSTALL     FluentWeather-Plus-Uninstall.exe  removes everything the setup added
//
// Everything runs in this program. It uses the normal Windows interfaces, so it starts no
// PowerShell and no command prompt. The files to install are embedded as resources named
// "payload/<file name>". The program extracts them to a temporary folder, does its work, and
// deletes the folder.
//
// The program runs as the signed-in user. Two steps need administrator rights: adding the signing
// certificate on install, and removing it on uninstall. For those the program starts one more copy
// of itself with administrator approval and a hidden switch. Nothing else runs with administrator
// rights, so Windows installs the app for the signed-in user.
//
// Language level: C# 5, so the compiler built into Windows can build it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Management.Deployment;

#if UNINSTALL
[assembly: AssemblyTitle("FluentWeather-Plus uninstaller")]
[assembly: AssemblyDescription("Removes FluentWeather-Plus, its tray icons and its signing certificate")]
#else
[assembly: AssemblyTitle("FluentWeather-Plus setup")]
[assembly: AssemblyDescription("Installs FluentWeather-Plus with its tray icons")]
#endif
[assembly: AssemblyProduct("FluentWeather-Plus")]
[assembly: AssemblyCompany("duedilly")]
// AssemblyVersion, AssemblyFileVersion, AssemblyInformationalVersion and AssemblyCopyright come from
// VersionInfo.cs, which Make-DirectInstaller.ps1 writes with the version of the app it packs.

namespace FluentWeatherLocal
{
    // Values that both the setup and the uninstaller read from install-info.json.
    internal sealed class InstallInfo
    {
        public string PackageName;
        public string PackageFamilyName;
        public string Version;
        public string Architecture;
        public string Thumbprint;
        public string CertSubject;
        public string MsixFile;
        public List<string> Dependencies = new List<string>();
    }

    internal static class DirectSetup
    {
        private const string Title = "FluentWeather-Plus";
        private const string Prefix = "payload/";
        private const string PackageName = "FluentWeather.LocalBuild";

        // Hidden switches for the one copy of this program that runs with administrator rights.
        private const string TrustSwitch = "--trust-cert";
        private const string RemoveCertSwitch = "--remove-cert";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The administrator copy of this program runs one of these and nothing else.
            if (args.Length == 2 && args[0] == TrustSwitch)
                return RunTrustCertificate(args[1]);
            if (args.Length == 3 && args[0] == RemoveCertSwitch)
                return RunRemoveCertificates(args[1], args[2]);

            try
            {
#if UNINSTALL
                return Uninstall();
#else
                return Install();
#endif
            }
            catch (Exception ex)
            {
                MessageBox.Show("Stopped with an error:" + Environment.NewLine + Environment.NewLine + ex.Message,
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ---- Shared helpers -----------------------------------------------------------------------

        // The FluentWeather-Plus version of the app in this setup, for example v45. It is the
        // informational version. The build number, for example 2.1.0.45, is the file version.
        private static string VersionText
        {
            get
            {
                try
                {
                    var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                    if (attributes.Length > 0)
                        return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                    return Assembly.GetExecutingAssembly().GetName().Version.ToString();
                }
                catch { return ""; }
            }
        }

        private static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        private static string InstallDir { get { return Path.Combine(LocalAppData, @"Programs\FluentWeather"); } }
        private static string TrayWorkDir { get { return Path.Combine(LocalAppData, "FluentWeatherLocal"); } }
        private static string StartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FluentWeather-Plus"); } }

        // The link type that the app's "Show tray icons" button opens.
        private const string TrayProtocolKey = @"Software\Classes\fluentweather-tray";

        // The Start menu folder of versions before the rename to FluentWeather-Plus, when the app was named "FluentWeather (Local)".
        private static string OldStartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FluentWeather"); } }
        private static string StartupDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FluentWeatherDirect";

        private static List<string> GetPayloadNames()
        {
            var list = new List<string>();
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!resource.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;
                string name = resource.Substring(Prefix.Length);
                // Plain file names only. This blocks paths such as "..\x" or "C:\x".
                if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    continue;
                list.Add(name);
            }
            return list;
        }

        private static void Extract(List<string> names, string folder)
        {
            Directory.CreateDirectory(folder);
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string name in names)
            {
                using (Stream input = assembly.GetManifestResourceStream(Prefix + name))
                using (var output = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static void TryDeleteFolder(string folder)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Thread.Sleep(500);
            }
        }

        private static InstallInfo ReadInfo(string folder)
        {
            string path = Path.Combine(folder, "install-info.json");
            return JsonTiny.ParseInstallInfo(File.ReadAllText(path));
        }

        // Starts one more copy of this program with administrator approval and waits for it.
        // Returns the exit code, or -1 when approval was declined.
        private static int RunElevated(string switchName, params string[] values)
        {
            var info = new ProcessStartInfo();
            info.FileName = Assembly.GetExecutingAssembly().Location;
            string arguments = "\"" + switchName + "\"";
            foreach (string v in values)
                arguments += " \"" + (v ?? "") + "\"";
            info.Arguments = arguments;
            info.UseShellExecute = true;
            info.Verb = "runas";
            try
            {
                using (Process p = Process.Start(info))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return -1; // The user declined the administrator prompt.
            }
        }

        // Waits for a package install or removal to finish. It polls the Windows operation directly,
        // so the program needs no extra .NET helper library. It throws with the Windows error text.
        private static void WaitFor(Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> op, string what)
        {
            while (op.Status == Windows.Foundation.AsyncStatus.Started)
                Thread.Sleep(100);
            if (op.Status == Windows.Foundation.AsyncStatus.Completed)
                return;

            string text = null;
            try
            {
                DeploymentResult result = op.GetResults();
                if (result != null) text = result.ErrorText;
            }
            catch { }
            if (string.IsNullOrEmpty(text))
            {
                Exception error = op.ErrorCode;
                text = op.Status == Windows.Foundation.AsyncStatus.Canceled ? "The operation was canceled."
                    : error != null ? error.Message + " (0x" + error.HResult.ToString("X8") + ")" : "Unknown error.";
            }
            throw new Exception(what + ": " + text);
        }

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingName, string newName, int flags);

        // ---- Certificate steps that run with administrator rights ---------------------------------

        // Adds the signing certificate to Trusted People of this PC and removes older copies with the
        // same name. The certificate file holds only the public key. Its private key was deleted right
        // after the app was signed, so nothing else can be signed with it.
        private static int RunTrustCertificate(string cerPath)
        {
            try
            {
                var cert = new X509Certificate2(cerPath);
                var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                try
                {
                    var old = new List<X509Certificate2>();
                    foreach (X509Certificate2 c in store.Certificates)
                        if (c.Subject == cert.Subject && c.Thumbprint != cert.Thumbprint)
                            old.Add(c);
                    foreach (X509Certificate2 c in old)
                        store.Remove(c);
                    store.Add(cert);
                }
                finally { store.Close(); }
                return 0;
            }
            catch { return 1; }
        }

        // Removes the signing certificates and asks Windows to delete the install folder at the next
        // restart. Only an administrator can schedule that deletion.
        private static int RunRemoveCertificates(string subject, string installFolder)
        {
            if (!string.IsNullOrEmpty(installFolder) && Directory.Exists(installFolder) &&
                installFolder.TrimEnd('\\').EndsWith(@"\Programs\FluentWeather", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(installFolder, "*", SearchOption.AllDirectories))
                        MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    foreach (string dir in Directory.GetDirectories(installFolder, "*", SearchOption.AllDirectories))
                        MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    MoveFileEx(installFolder, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(subject))
                return 0;
            try
            {
                var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                try
                {
                    var toRemove = new List<X509Certificate2>();
                    foreach (X509Certificate2 c in store.Certificates)
                        if (c.Subject == subject)
                            toRemove.Add(c);
                    foreach (X509Certificate2 c in toRemove)
                        store.Remove(c);
                }
                finally { store.Close(); }
                return 0;
            }
            catch { return 1; }
        }

        private static bool CertificateTrusted(string thumbprint)
        {
            var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            try
            {
                foreach (X509Certificate2 c in store.Certificates)
                    if (string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            finally { store.Close(); }
        }

        // Ends the tray icons that run from the install folder. Windows cannot replace or delete a
        // program file while it runs. Tray icons from another folder are left alone.
        private static void StopTray()
        {
            foreach (Process p in Process.GetProcessesByName("FluentWeatherTray"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (path != null && path.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
            }
        }

#if !UNINSTALL
        // ---- Install ------------------------------------------------------------------------------

        private static int Install()
        {
            List<string> names = GetPayloadNames();
            if (!names.Contains("install-info.json"))
            {
                MessageBox.Show("This file is incomplete. The setup data is missing.", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            bool addStartMenu, startWithWindows;
            if (!ShowOptions(out addStartMenu, out startWithWindows))
                return 0;

            string folder = Path.Combine(Path.GetTempPath(), "FluentWeather-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                Extract(names, folder);
                InstallInfo info = ReadInfo(folder);

                string problem = CheckWindows(info);
                if (problem != null)
                {
                    MessageBox.Show(problem, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                string cerPath = Path.Combine(folder, "FluentWeatherLocalBuild.cer");
                if (!CertificateTrusted(info.Thumbprint))
                {
                    int trust = RunElevated(TrustSwitch, cerPath);
                    if (trust == -1 || !CertificateTrusted(info.Thumbprint))
                    {
                        MessageBox.Show("Administrator approval is needed once, to let Windows accept the app. " +
                            "Nothing was installed.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 1;
                    }
                }

                // The tray icons of an earlier version run from the install folder. End them first, so
                // their program file can be replaced. The setup starts them again at the end.
                StopTray();

                InstallPackage(folder, info);
                string pfn = CopyFilesAndShortcuts(folder, info, addStartMenu, startWithWindows);
                StartApp(pfn);

                MessageBox.Show("FluentWeather-Plus is installed and opens now." + Environment.NewLine + Environment.NewLine +
                    "The tray icons are in the notification area. If you do not see them, click the ^ arrow " +
                    "next to the clock.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            finally
            {
                TryDeleteFolder(folder);
            }
        }

        // The options window. Both choices are on by default.
        private static bool ShowOptions(out bool addStartMenu, out bool startWithWindows)
        {
            addStartMenu = true;
            startWithWindows = true;

            using (var form = new Form())
            {
                form.Text = Title + " " + VersionText + " setup";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ClientSize = new Size(430, 210);
                try
                {
                    string me = Assembly.GetExecutingAssembly().Location;
                    form.Icon = Icon.ExtractAssociatedIcon(me);
                }
                catch { }

                var intro = new Label();
                intro.AutoSize = false;
                intro.SetBounds(16, 14, 400, 54);
                intro.Text = "FluentWeather-Plus is a weather app with saved locations and tray icons. " +
                    "It is based on FluentWeather by Gabboxl, with features added by duedilly108.";

                var optionStartMenu = new CheckBox();
                optionStartMenu.SetBounds(16, 78, 400, 24);
                optionStartMenu.Text = "Add a Start menu shortcut";
                optionStartMenu.Checked = true;

                var optionStartup = new CheckBox();
                optionStartup.SetBounds(16, 106, 400, 24);
                optionStartup.Text = "Start the tray icons when Windows starts";
                optionStartup.Checked = true;

                var note = new Label();
                note.AutoSize = false;
                note.SetBounds(16, 134, 400, 20);
                note.Text = "Windows asks for administrator approval once, to accept the app.";
                note.ForeColor = SystemColors.GrayText;

                var install = new Button();
                install.Text = "Install";
                install.DialogResult = DialogResult.OK;
                install.SetBounds(234, 166, 85, 28);

                var cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(329, 166, 85, 28);

                form.Controls.Add(intro);
                form.Controls.Add(optionStartMenu);
                form.Controls.Add(optionStartup);
                form.Controls.Add(note);
                form.Controls.Add(install);
                form.Controls.Add(cancel);
                form.AcceptButton = install;
                form.CancelButton = cancel;

                DialogResult result = form.ShowDialog();
                addStartMenu = optionStartMenu.Checked;
                startWithWindows = optionStartup.Checked;
                return result == DialogResult.OK;
            }
        }

        private static string CheckWindows(InstallInfo info)
        {
            if (Environment.OSVersion.Version.Build < 17763)
                return "FluentWeather needs Windows 10 version 1809 or later, or Windows 11.";

            string osArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (string.IsNullOrEmpty(osArch))
                osArch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (osArch != null) osArch = osArch.ToUpperInvariant();
            if (info.Architecture == "x64" && osArch != "AMD64" && osArch != "ARM64")
                return "This setup has the 64-bit version of the app. This PC runs " + osArch + " Windows.";

            string oldBuild = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"FluentWeatherLocal\config.json");
            if (File.Exists(oldBuild))
                return "This PC has the FluentWeather local build from Setup.cmd. It already has everything this setup installs.";

            return null;
        }

        private static void InstallPackage(string folder, InstallInfo info)
        {
            var manager = new PackageManager();
            var main = new Uri(Path.Combine(folder, info.MsixFile));
            var deps = new List<Uri>();
            foreach (string d in info.Dependencies)
            {
                string p = Path.Combine(folder, d);
                if (File.Exists(p)) deps.Add(new Uri(p));
            }

            var op = manager.AddPackageAsync(main, deps,
                DeploymentOptions.ForceApplicationShutdown | DeploymentOptions.ForceUpdateFromAnyVersion);
            WaitFor(op, "The app did not install");
        }

        private static string CopyFilesAndShortcuts(string folder, InstallInfo info, bool addStartMenu, bool startWithWindows)
        {
            string pfn = info.PackageFamilyName;

            Directory.CreateDirectory(InstallDir);
            foreach (string name in new[] { "FluentWeatherTray.exe", "FluentWeather-Uninstall.exe", "README.txt", "FluentWeather.ico", "install-info.json" })
            {
                string src = Path.Combine(folder, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(InstallDir, name), true);
            }

            // The tray icons read the app's data folder name from this file.
            Directory.CreateDirectory(TrayWorkDir);
            File.WriteAllText(Path.Combine(TrayWorkDir, "package-family-name.txt"), pfn);
            File.WriteAllText(Path.Combine(TrayWorkDir, "direct-install.txt"), "Created by FluentWeather-Setup.exe");
            string trayIconSrc = Path.Combine(folder, "tray-icon.png");
            if (File.Exists(trayIconSrc)) File.Copy(trayIconSrc, Path.Combine(TrayWorkDir, "tray-icon.png"), true);

            string tray = Path.Combine(InstallDir, "FluentWeatherTray.exe");
            string uninstaller = Path.Combine(InstallDir, "FluentWeather-Uninstall.exe");
            string ico = Path.Combine(InstallDir, "FluentWeather.ico");
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

            try { if (Directory.Exists(OldStartMenuDir)) Directory.Delete(OldStartMenuDir, true); } catch { }
            if (addStartMenu)
            {
                Directory.CreateDirectory(StartMenuDir);
                Shortcut.Create(Path.Combine(StartMenuDir, "FluentWeather-Plus.lnk"), explorer, "shell:AppsFolder\\" + pfn + "!App", InstallDir, ico, "Open FluentWeather-Plus");
                Shortcut.Create(Path.Combine(StartMenuDir, "FluentWeather-Plus tray icons.lnk"), tray, null, InstallDir, ico, "Start the FluentWeather-Plus tray icons");
                Shortcut.Create(Path.Combine(StartMenuDir, "Read me.lnk"), Path.Combine(InstallDir, "README.txt"), null, InstallDir, null, "How FluentWeather-Plus is installed, used and removed");
                Shortcut.Create(Path.Combine(StartMenuDir, "Uninstall FluentWeather-Plus.lnk"), uninstaller, null, InstallDir, ico, "Remove FluentWeather-Plus");
            }
            if (startWithWindows)
                Shortcut.Create(Path.Combine(StartupDir, "FluentWeather Tray.lnk"), tray, null, InstallDir, ico, "FluentWeather-Plus tray icons");

            // The app's "Show tray icons" button opens fluentweather-tray:, which starts the tray program.
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(TrayProtocolKey))
            {
                key.SetValue("", "URL:FluentWeather-Plus tray icons");
                key.SetValue("URL Protocol", "");
                using (RegistryKey command = key.CreateSubKey(@"shell\open\command"))
                    command.SetValue("", "\"" + tray + "\" \"%1\"");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "FluentWeather-Plus with tray icons");
                key.SetValue("DisplayVersion", info.Version);
                key.SetValue("Publisher", "duedilly");
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + uninstaller + "\"");
                key.SetValue("DisplayIcon", ico);
                key.SetValue("Comments", "Based on FluentWeather by Gabboxl, with saved locations and tray icons");
                key.SetValue("URLInfoAbout", "https://github.com/Duedilly108/FluentWeather-Plus");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            return pfn;
        }

        private static void StartApp(string pfn)
        {
            string tray = Path.Combine(InstallDir, "FluentWeatherTray.exe");
            try { Process.Start(new ProcessStartInfo(tray) { WorkingDirectory = InstallDir, UseShellExecute = false }); } catch { }

            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            try { Process.Start(new ProcessStartInfo(explorer, "shell:AppsFolder\\" + pfn + "!App") { UseShellExecute = true }); } catch { }
        }
#endif

#if UNINSTALL
        // ---- Uninstall ----------------------------------------------------------------------------

        private static int Uninstall()
        {
            string oldBuild = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"FluentWeatherLocal\config.json");
            if (File.Exists(oldBuild))
            {
                MessageBox.Show("This PC has the FluentWeather local build from Setup.cmd. Remove it in " +
                    "Settings > Apps > \"FluentWeather local build\". Nothing was changed.", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            string question =
                "This removes FluentWeather-Plus from this PC: the app with its saved locations and " +
                "settings, the tray icons, the Start menu items and the signing certificate." + Environment.NewLine + Environment.NewLine +
                "Windows asks for administrator approval once, to remove the certificate." + Environment.NewLine + Environment.NewLine +
                "Remove FluentWeather-Plus now?";
            if (MessageBox.Show(question, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return 0;

            string subject = null;
            string pfn = null;
            try
            {
                InstallInfo info = JsonTiny.ParseInstallInfo(File.ReadAllText(Path.Combine(InstallDir, "install-info.json")));
                subject = info.CertSubject;
                pfn = info.PackageFamilyName;
            }
            catch { }

            // The administrator step comes first. If it is declined, nothing is removed.
            if (RunElevated(RemoveCertSwitch, subject, InstallDir) == -1)
            {
                MessageBox.Show("Administrator approval is needed to remove the signing certificate. " +
                    "Nothing was removed.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            StopTray();
            RemovePackage(pfn);
            RemoveShortcutsAndRegistry();
            RemoveTrayWorkDir();
            DeleteInstallFiles();

            MessageBox.Show("FluentWeather-Plus is removed.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static void RemovePackage(string pfn)
        {
            try
            {
                var manager = new PackageManager();
                IEnumerable<Windows.ApplicationModel.Package> found;
                if (!string.IsNullOrEmpty(pfn))
                    found = manager.FindPackagesForUser("", pfn);
                else
                    found = manager.FindPackagesForUser("");

                foreach (var pkg in found)
                {
                    if (pkg.Id.Name != PackageName) continue;
                    var op = manager.RemovePackageAsync(pkg.Id.FullName);
                    WaitFor(op, "The app was not removed");
                }
            }
            catch { }
        }

        private static void RemoveShortcutsAndRegistry()
        {
            try { File.Delete(Path.Combine(StartupDir, "FluentWeather Tray.lnk")); } catch { }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
            try { if (Directory.Exists(OldStartMenuDir)) Directory.Delete(OldStartMenuDir, true); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(TrayProtocolKey, false); } catch { }
        }

        private static void RemoveTrayWorkDir()
        {
            try
            {
                if (File.Exists(Path.Combine(TrayWorkDir, "direct-install.txt")) && Directory.Exists(TrayWorkDir))
                    Directory.Delete(TrayWorkDir, true);
            }
            catch { }
        }

        // Deletes the installed files now. The running uninstaller cannot delete its own file, so that
        // file and the folder go at the next restart, as scheduled by the administrator copy.
        private static void DeleteInstallFiles()
        {
            if (!Directory.Exists(InstallDir)) return;
            string me = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            bool runningInside = me.StartsWith(Path.GetFullPath(InstallDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            if (!runningInside)
            {
                TryDeleteFolder(InstallDir);
                return;
            }
            foreach (string file in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), me, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { }
            }
        }
#endif
    }

    // Creates a .lnk shortcut through the IShellLink COM interface. No extra files are needed.
    internal static class Shortcut
    {
        public static void Create(string linkPath, string target, string arguments, string workingDir, string iconPath, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
            if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)) link.SetIconLocation(iconPath, 0);
            ((IPersistFile)link).Save(linkPath, true);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr data, int flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon, int maxPath, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }
    }

    // Reads the few values this program needs from install-info.json. The file is written by
    // Make-DirectInstaller.ps1 and holds only simple strings and a string array, so a small reader is enough.
    internal static class JsonTiny
    {
        public static InstallInfo ParseInstallInfo(string json)
        {
            var info = new InstallInfo();
            info.PackageName = GetString(json, "PackageName");
            info.PackageFamilyName = GetString(json, "PackageFamilyName");
            info.Version = GetString(json, "Version");
            info.Architecture = GetString(json, "Architecture");
            info.Thumbprint = GetString(json, "Thumbprint");
            info.CertSubject = GetString(json, "CertSubject");
            info.MsixFile = GetString(json, "MsixFile");
            info.Dependencies = GetStringArray(json, "Dependencies");
            return info;
        }

        private static string GetString(string json, string key)
        {
            string marker = "\"" + key + "\"";
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            int colon = json.IndexOf(':', at + marker.Length);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            return ReadJsonString(json, ref i);
        }

        private static List<string> GetStringArray(string json, string key)
        {
            var list = new List<string>();
            string marker = "\"" + key + "\"";
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return list;
            int open = json.IndexOf('[', at + marker.Length);
            int close = json.IndexOf(']', open + 1);
            if (open < 0 || close < 0) return list;
            int i = open + 1;
            while (i < close)
            {
                if (json[i] == '"')
                {
                    list.Add(ReadJsonString(json, ref i));
                }
                else i++;
            }
            return list;
        }

        // Reads a JSON string. On entry json[i] is the opening quote. On exit i is past the closing quote.
        private static string ReadJsonString(string json, ref int i)
        {
            i++; // opening quote
            var sb = new System.Text.StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c == '\\' && i < json.Length)
                {
                    char e = json[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u':
                            if (i + 4 <= json.Length)
                            {
                                sb.Append((char)Convert.ToInt32(json.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

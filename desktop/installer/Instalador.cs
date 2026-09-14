/* Instalador de Koogle Kerbin.

   Un asistente propio en WinForms sobre .NET Framework 4.8, que ya viene con Windows 10 y
   11: así arranca aunque falte .NET 10, que es justo lo que tiene que comprobar. Los
   ficheros de la aplicación van dentro del .exe, en un zip incrustado.

   Se instala para el usuario actual, sin permisos de administrador, en
   %LOCALAPPDATA%\Programs\Koogle Kerbin, con accesos directos y su entrada en
   «Aplicaciones». El propio instalador se queda en la carpeta como desinstalar.exe, y
   una lista (instalacion.txt) apunta lo instalado: al desinstalar solo se borra eso.

   Opciones de línea de órdenes:
     /silent          sin ventanas. Códigos de salida: 0 bien, 1 falta .NET 10, 2 error,
                      3 cancelado, 4 la aplicación está abierta
     /dir=<carpeta>   carpeta de instalación
     /noshortcuts     sin accesos directos
     /noregistry      sin entrada en «Aplicaciones»
     /nolaunch        no proponer abrir la aplicación al terminar
     /uninstall       desinstalar

   Está escrito en C# 7.3 para compilar con cualquier Roslyn contra .NET Framework. */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Instalador de Koogle Kerbin")]
[assembly: AssemblyProduct("Koogle Kerbin")]
[assembly: AssemblyDescription("Instala Koogle Kerbin para el usuario actual")]
[assembly: AssemblyCompany("alonso49noob")]

namespace KoogleKerbinSetup
{
    static class App
    {
        public const string Name = "Koogle Kerbin";
        public const string Exe = "KoogleKerbin.exe";
        public const string Uninstaller = "desinstalar.exe";
        public const string Manifest = "instalacion.txt";
        public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KoogleKerbin";
        public const string Publisher = "alonso49noob";
        public const string RepoUrl = "https://github.com/alonso49noob/koogle-kerbin";
        public const string DotnetUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
        public const int DotnetMajor = 10;

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name); }
        }
    }

    sealed class Options
    {
        public bool Uninstall, Silent, NoShortcuts, NoRegistry, NoLaunch, FromTemp;
        public string Dir;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            foreach (var raw in args)
            {
                string a = raw.Trim(), l = a.ToLowerInvariant();
                if (l == "/uninstall") o.Uninstall = true;
                else if (l == "/silent") o.Silent = true;
                else if (l == "/noshortcuts") o.NoShortcuts = true;
                else if (l == "/noregistry") o.NoRegistry = true;
                else if (l == "/nolaunch") o.NoLaunch = true;
                else if (l == "/fromtemp") o.FromTemp = true;
                else if (l.StartsWith("/dir=")) o.Dir = a.Substring(5).Trim('"');
            }
            return o;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var o = Options.Parse(args);
            // desinstalar.exe es este mismo ejecutable: abierto con doble clic, desinstala
            string self = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            if (string.Equals(self, App.Uninstaller, StringComparison.OrdinalIgnoreCase)) o.Uninstall = true;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (o.Uninstall) return Uninstaller.Run(o);
                if (!Payload.Present)
                {
                    if (!o.Silent) MessageBox.Show("Este ejecutable no lleva dentro los ficheros de la aplicación.", App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                if (o.Silent) return SilentInstall(o);
                Application.Run(new Wizard(o));
                return 0;
            }
            catch (Exception ex)
            {
                Util.Log(ex);
                if (!o.Silent) MessageBox.Show(ex.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        static int SilentInstall(Options o)
        {
            if (!Requirements.Os64 || Requirements.DesktopRuntime() == null) return 1;
            string dir = Path.GetFullPath(o.Dir ?? Registration.InstalledDir() ?? App.DefaultDir);
            if (Util.RunningIn(dir).Count > 0) return 4;
            try
            {
                new Installer { Dir = dir, Desktop = !o.NoShortcuts, StartMenu = !o.NoShortcuts, Register = !o.NoRegistry }.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Util.Log(ex);
                return 2;
            }
        }
    }

    /* ------------------------------------------------------------------ requisitos */

    static class Requirements
    {
        public static bool Os64 { get { return Environment.Is64BitOperatingSystem; } }

        /* La versión más alta del runtime de escritorio de .NET 10 (Microsoft.WindowsDesktop.App),
           buscada donde la busca el propio lanzador de .NET: la carpeta registrada, DOTNET_ROOT
           y Archivos de programa\dotnet. Null si no está. */
        public static Version DesktopRuntime()
        {
            Version best = null;
            foreach (var root in DotnetRoots())
            {
                string dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) continue;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    int dash = name.IndexOf('-');                       // «10.0.0-rc.2»
                    Version v;
                    if (!Version.TryParse(dash > 0 ? name.Substring(0, dash) : name, out v) || v.Major != App.DotnetMajor) continue;
                    if (!File.Exists(Path.Combine(sub, "System.Windows.Forms.dll"))) continue;
                    if (best == null || v > best) best = v;
                }
            }
            return best;
        }

        static IEnumerable<string> DotnetRoots()
        {
            var list = new List<string>();
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
                {
                    var loc = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(loc)) list.Add(loc);
                }
            }
            catch { }
            string env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(env)) list.Add(env);
            string pf = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(pf)) list.Add(Path.Combine(pf, "dotnet"));
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    /* ------------------------------------------------------------------ instalación */

    static class Payload
    {
        public static Stream Open() { return Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"); }

        public static bool Present
        {
            get { using (var s = Open()) return s != null; }
        }

        public static long UncompressedSize()
        {
            using (var s = Open())
            using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                return z.Entries.Sum(e => e.Length);
        }
    }

    /* Lo que instaló una versión: ficheros relativos a la carpeta y accesos directos. */
    sealed class InstallManifest
    {
        public readonly List<string> Files = new List<string>();
        public readonly List<string> Shortcuts = new List<string>();

        public static InstallManifest Read(string dir)
        {
            var m = new InstallManifest();
            string p = Path.Combine(dir, App.Manifest);
            if (!File.Exists(p)) return m;
            foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
            {
                if (line.StartsWith("file:")) m.Files.Add(line.Substring(5));
                else if (line.StartsWith("lnk:")) m.Shortcuts.Add(line.Substring(4));
            }
            return m;
        }

        public static void Write(string dir, IEnumerable<string> files, IEnumerable<string> shortcuts)
        {
            var lines = new List<string> { "# Instalado por " + App.Name + " " + Build.Version + ". El desinstalador borra solo lo que aparece aquí." };
            lines.AddRange(files.Select(f => "file:" + f));
            lines.Add("file:" + App.Manifest);
            lines.AddRange(shortcuts.Where(s => s != null).Select(s => "lnk:" + s));
            File.WriteAllLines(Path.Combine(dir, App.Manifest), lines, Encoding.UTF8);
        }
    }

    sealed class Installer
    {
        public string Dir;
        public bool Desktop, StartMenu, Register;
        public Action<long, long, string> Progress;

        public void Run()
        {
            string dir = Path.GetFullPath(Dir);
            Directory.CreateDirectory(dir);
            var old = InstallManifest.Read(dir);
            var files = new List<string>();

            using (var s = Payload.Open())
            using (var z = new ZipArchive(s, ZipArchiveMode.Read))
            {
                long total = Math.Max(1, z.Entries.Sum(e => e.Length)), done = 0;
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;
                    string rel = e.FullName.Replace('/', '\\');
                    string dest = Path.Combine(dir, rel);
                    if (!Util.Inside(dir, dest)) throw new InvalidDataException("Ruta no válida en el paquete: " + e.FullName);
                    if (Progress != null) Progress(done, total, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    string tmp = dest + ".nuevo";
                    using (var input = e.Open())
                    using (var output = File.Create(tmp))
                        input.CopyTo(output);
                    ReplaceFile(tmp, dest);
                    files.Add(rel);
                    done += e.Length;
                }
            }

            // el instalador se queda como desinstalador
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            string un = Path.Combine(dir, App.Uninstaller);
            if (!string.Equals(self, un, StringComparison.OrdinalIgnoreCase))
            {
                string tmp = un + ".nuevo";
                File.Copy(self, tmp, true);
                ReplaceFile(tmp, un);
            }
            files.Add(App.Uninstaller);

            // lo que dejó una versión anterior y esta ya no trae; los accesos directos se rehacen
            foreach (var rel in old.Files)
                if (!files.Contains(rel, StringComparer.OrdinalIgnoreCase) && rel != App.Manifest && Util.Inside(dir, Path.Combine(dir, rel)))
                    Util.TryDelete(Path.Combine(dir, rel));
            foreach (var lnk in old.Shortcuts)
                if (lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) Util.TryDelete(lnk);
            Util.RemoveEmptyDirs(dir, true);

            string exe = Path.Combine(dir, App.Exe);
            var shortcuts = new List<string>();
            if (Desktop) shortcuts.Add(Shortcut.Create(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), exe));
            if (StartMenu) shortcuts.Add(Shortcut.Create(Environment.GetFolderPath(Environment.SpecialFolder.Programs), exe));
            InstallManifest.Write(dir, files, shortcuts);

            if (Register)
            {
                long bytes = files.Select(f => Path.Combine(dir, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
                Registration.Write(dir, (int)Math.Min(int.MaxValue, bytes / 1024));
            }
            if (Progress != null) Progress(1, 1, "");
        }

        static void ReplaceFile(string tmp, string dest)
        {
            if (File.Exists(dest))
            {
                try
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                    File.Delete(dest);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Util.TryDelete(tmp);
                    throw new IOException("No se puede reemplazar " + Path.GetFileName(dest) + ". ¿Está abierta la aplicación?", ex);
                }
            }
            File.Move(tmp, dest);
        }
    }

    static class Registration
    {
        public static void Write(string dir, int sizeKb)
        {
            string un = Path.Combine(dir, App.Uninstaller);
            using (var k = Registry.CurrentUser.CreateSubKey(App.RegKey))
            {
                k.SetValue("DisplayName", App.Name);
                k.SetValue("DisplayVersion", Build.Version);
                k.SetValue("Publisher", App.Publisher);
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", Path.Combine(dir, App.Exe) + ",0");
                k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + un + "\" /uninstall /silent");
                k.SetValue("URLInfoAbout", App.RepoUrl);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        static string Get(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(App.RegKey))
                    return k == null ? null : k.GetValue(name) as string;
            }
            catch { return null; }
        }

        public static string InstalledDir() { return Get("InstallLocation"); }
        public static string InstalledVersion() { return Get("DisplayVersion"); }

        /* Solo si la entrada es de esta carpeta: otra instalación conserva la suya. */
        public static void DeleteIf(string dir)
        {
            string loc = InstalledDir();
            if (loc == null || !string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(App.RegKey, false); } catch { }
        }
    }

    static class Shortcut
    {
        /* Acceso directo con WScript.Shell, por reflexión para no depender de un ensamblado
           de interoperabilidad. */
        public static string Create(string folder, string target)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return null;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, App.Name + ".lnk");
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                try
                {
                    object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    try
                    {
                        var lt = lnk.GetType();
                        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                        lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Visor de Kerbin para Kerbal Space Program" });
                        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                    }
                    finally { Marshal.FinalReleaseComObject(lnk); }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
                return path;
            }
            catch (Exception ex)
            {
                Util.Log(ex);
                return null;
            }
        }
    }

    static class Util
    {
        public static string LogPath { get { return Path.Combine(Path.GetTempPath(), "KoogleKerbin-instalador.log"); } }

        public static void Log(Exception ex)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine); } catch { }
        }

        public static Icon AppIcon(int size)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            {
                if (s == null) return null;
                return size > 0 ? new Icon(s, size, size) : new Icon(s);
            }
        }

        public static bool Inside(string dir, string path)
        {
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /* Procesos de la aplicación que se ejecutan desde esa carpeta. */
        public static List<Process> RunningIn(string dir)
        {
            var r = new List<Process>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(App.Exe)))
            {
                try { if (Inside(dir, p.MainModule.FileName)) r.Add(p); }
                catch { }
            }
            return r;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch { }
        }

        public static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        public static void RemoveEmptyDirs(string dir, bool keepRoot)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir)) RemoveEmptyDirs(sub, false);
            if (!keepRoot && !Directory.EnumerateFileSystemEntries(dir).Any())
                try { Directory.Delete(dir); } catch { }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log(ex); }
        }

        public static void Launch(string exe)
        {
            try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true }); }
            catch (Exception ex) { Log(ex); }
        }
    }

    /* ------------------------------------------------------------------ desinstalación */

    static class Uninstaller
    {
        public static int Run(Options o)
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetFullPath(o.Dir ?? Path.GetDirectoryName(self));

            if (!o.FromTemp)
            {
                // un ejecutable no puede borrarse a sí mismo: trabaja una copia desde %TEMP%
                string tmp = Path.Combine(Path.GetTempPath(), "KoogleKerbin-desinstalar-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, tmp, true);
                string args = "/uninstall /fromtemp \"/dir=" + dir + "\"" + (o.Silent ? " /silent" : "") + (o.NoRegistry ? " /noregistry" : "");
                var p = Process.Start(new ProcessStartInfo(tmp, args) { UseShellExecute = false });
                if (!o.Silent) return 0;
                p.WaitForExit();
                return p.ExitCode;
            }
            var pending = new List<string> { self };
            try { return Remove(o, dir, pending); }
            finally { DeleteLater(pending, dir); }
        }

        static int Remove(Options o, string dir, List<string> pending)
        {
            string caption = "Desinstalar " + App.Name;
            var man = InstallManifest.Read(dir);
            if (man.Files.Count == 0)
            {
                if (!o.Silent) MessageBox.Show("No encuentro una instalación de " + App.Name + " en:\n" + dir, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            bool deleteData = false;
            if (!o.Silent)
            {
                using (var f = new UninstallForm(dir))
                {
                    if (f.ShowDialog() != DialogResult.OK) return 3;
                    deleteData = f.DeleteData;
                }
            }
            while (Util.RunningIn(dir).Count > 0)
            {
                if (o.Silent) return 4;
                if (MessageBox.Show(App.Name + " está abierto. Ciérralo y pulsa «Reintentar».", caption, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return 3;
            }

            foreach (var rel in man.Files)
            {
                string path = Path.Combine(dir, rel);
                if (!Util.Inside(dir, path)) continue;
                Util.TryDelete(path);
                // desinstalar.exe sigue en uso mientras su proceso espera a esta copia: se borra al terminar
                if (File.Exists(path)) pending.Add(path);
            }
            foreach (var lnk in man.Shortcuts)
                if (lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) Util.TryDelete(lnk);
            Util.RemoveEmptyDirs(dir, false);
            if (!o.NoRegistry) Registration.DeleteIf(dir);

            if (deleteData)
            {
                Util.TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KoogleKerbin"));
                Util.TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoogleKerbin"));
            }

            if (!o.Silent)
            {
                bool others = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                    .Any(f => !pending.Contains(f, StringComparer.OrdinalIgnoreCase) && File.Exists(f));
                MessageBox.Show(others
                        ? App.Name + " se ha desinstalado. La carpeta\n" + dir + "\nse queda porque contiene otros ficheros."
                        : App.Name + " se ha desinstalado.",
                    caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        /* Lo que aún está en uso (esta copia de %TEMP% y el desinstalar.exe que la lanzó) se
           borra en cuanto terminan los dos procesos, y con ello la carpeta si ha quedado vacía. */
        static void DeleteLater(List<string> files, string dir)
        {
            try
            {
                var cmd = new StringBuilder("/d /c ping 127.0.0.1 -n 4 >nul");
                foreach (var f in files) cmd.Append(" & del /f /q \"").Append(f).Append('"');
                cmd.Append(" & rd \"").Append(dir).Append("\" 2>nul");
                Process.Start(new ProcessStartInfo("cmd.exe", cmd.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }
    }

    /* ------------------------------------------------------------------ interfaz */

    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(11, 17, 26), Bg2 = Color.FromArgb(20, 29, 41), Line = Color.FromArgb(40, 54, 72);
        public static readonly Color Fg = Color.FromArgb(219, 230, 242), Dim = Color.FromArgb(138, 160, 184), Accent = Color.FromArgb(78, 163, 255);
        public static readonly Color Ok = Color.FromArgb(126, 231, 135), Bad = Color.FromArgb(255, 122, 122);

        public static float DpiScale(Control c)
        {
            using (var g = c.CreateGraphics()) return g.DpiX / 96f;
        }

        public static Button Btn(string text, bool primary, float k)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = primary ? Accent : Bg2,
                ForeColor = primary ? Color.FromArgb(8, 16, 28) : Fg,
                Size = new Size((int)(110 * k), (int)(30 * k)),
                Font = new Font("Segoe UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(112, 182, 255) : Line;
            return b;
        }

        public static Label Para(Control parent, string text, int x, int y, int width, Color? color = null, float size = 9.5f, FontStyle style = FontStyle.Regular)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Location = new Point(x, y),
                ForeColor = color ?? Fg,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, style),
                UseMnemonic = false
            };
            parent.Controls.Add(l);
            return l;
        }

        public static CheckBox Check(Control parent, string text, int x, int y, bool on)
        {
            var c = new CheckBox
            {
                Text = text, Checked = on, AutoSize = true, Location = new Point(x, y),
                ForeColor = Fg, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9.5f), UseMnemonic = false
            };
            parent.Controls.Add(c);
            return c;
        }
    }

    sealed class Wizard : Form
    {
        static readonly string[] Steps = { "Bienvenida", "Requisitos", "Opciones", "Instalación", "Listo" };
        static readonly string[] Titles =
        {
            "Te damos la bienvenida", "Requisitos del equipo", "Dónde instalarlo", "Instalando…", "Instalación completada"
        };

        readonly Options opts;
        readonly float k;
        readonly Panel side;
        readonly Label title;
        readonly Button back, next, cancel;
        readonly Panel pWelcome, pReq, pOptions, pProgress, pDone;
        readonly Label reqOs, reqNet, reqHelp, spaceLabel, barFile, doneText, doneHint;
        readonly Button reqDownload, reqRetry;
        readonly System.Windows.Forms.Timer reqTimer;
        readonly TextBox dirBox;
        readonly CheckBox chkDesktop, chkStart, chkLaunch;
        readonly ProgressBar bar;
        readonly Bitmap sideIcon;
        readonly long needBytes;
        int page;
        bool reqOk, installing, finished, failed;
        string installedDir;

        int S(double v) { return (int)Math.Round(v * k); }

        Label Para(Panel p, string text, int y, Color? color = null, float size = 9.5f, FontStyle style = FontStyle.Regular)
        {
            return Ui.Para(p, text, 0, y, p.Width, color, size, style);
        }

        public Wizard(Options o)
        {
            opts = o;
            k = Ui.DpiScale(this);
            Text = "Instalar " + App.Name;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(680), S(450));
            BackColor = Ui.Bg;
            ForeColor = Ui.Fg;
            Font = new Font("Segoe UI", 9.5f);
            Icon = Util.AppIcon(0);
            DoubleBuffered = true;
            // del .png: los marcos grandes del .ico van comprimidos en PNG y Icon.ToBitmap no los entiende
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png"))
                if (s != null) sideIcon = new Bitmap(s);

            string existingDir = Registration.InstalledDir(), existingVersion = Registration.InstalledVersion();
            if (existingDir != null && !File.Exists(Path.Combine(existingDir, App.Exe))) existingDir = existingVersion = null;
            needBytes = Payload.UncompressedSize() + new FileInfo(Assembly.GetExecutingAssembly().Location).Length;

            side = new Panel { Bounds = new Rectangle(0, 0, S(196), ClientSize.Height), BackColor = Ui.Bg2 };
            side.Paint += PaintSide;
            Controls.Add(side);

            title = new Label { AutoSize = true, Location = new Point(S(224), S(26)), Font = new Font("Segoe UI Semibold", 15f), ForeColor = Ui.Fg };
            Controls.Add(title);

            int by = ClientSize.Height - S(46);
            cancel = Ui.Btn("Cancelar", false, k);
            cancel.Location = new Point(ClientSize.Width - S(28) - cancel.Width, by);
            next = Ui.Btn("Siguiente  ›", true, k);
            next.Location = new Point(cancel.Left - S(10) - next.Width, by);
            back = Ui.Btn("‹  Atrás", false, k);
            back.Location = new Point(next.Left - S(8) - back.Width, by);
            cancel.Click += (s, e) => Close();
            next.Click += (s, e) => OnNext();
            back.Click += (s, e) => { if (page == 1 || page == 2) ShowPage(page - 1); };
            Controls.AddRange(new Control[] { back, next, cancel });
            AcceptButton = next;

            /* Bienvenida */
            pWelcome = NewPage();
            int y = Para(pWelcome, "Este asistente instala " + App.Name + " " + Build.Version + ", un visor de Kerbin para Kerbal Space " +
                                   "Program: mapa plano, globo 3D, vista del cielo con día y noche, y las naves de tu partida con sus " +
                                   "órbitas y sus modelos.", 0).Bottom + S(14);
            if (existingVersion != null)
                y = Para(pWelcome, "Ya tienes instalada la versión " + existingVersion + ". Se actualizará en la misma carpeta; tus " +
                                   "ajustes, marcadores y la partida guardada se conservan.", y, Ui.Accent).Bottom + S(14);
            y = Para(pWelcome, "Se instala solo para tu usuario y no pide permisos de administrador. Necesita Windows de 64 bits y " +
                               "el runtime de escritorio de .NET 10, que se comprueba en el paso siguiente.", y, Ui.Dim).Bottom + S(14);
            Para(pWelcome, App.Name + " es un proyecto de aficionados, sin relación con Squad ni con Take-Two.", y, Ui.Dim);

            /* Requisitos */
            pReq = NewPage();
            reqOs = Para(pReq, " ", 0, null, 10.5f);
            reqNet = Para(pReq, " ", S(32), null, 10.5f);
            reqHelp = Para(pReq, App.Name + " necesita el runtime de escritorio de .NET 10, de Microsoft y gratuito. En la página de " +
                                 "descarga elige «.NET Desktop Runtime 10» para Windows x64, instálalo y vuelve aquí: este paso lo " +
                                 "detecta solo.", S(78), Ui.Dim);
            reqDownload = Ui.Btn("Descargar .NET 10", true, k);
            reqRetry = Ui.Btn("Comprobar de nuevo", false, k);
            reqDownload.Width = reqRetry.Width = S(160);
            reqDownload.Location = new Point(0, reqHelp.Bottom + S(18));
            reqRetry.Location = new Point(reqDownload.Right + S(10), reqDownload.Top);
            reqDownload.Click += (s, e) => Util.OpenUrl(App.DotnetUrl);
            reqRetry.Click += (s, e) => CheckRequirements();
            pReq.Controls.Add(reqDownload);
            pReq.Controls.Add(reqRetry);
            reqTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            reqTimer.Tick += (s, e) => { if (!reqOk) CheckRequirements(); };

            /* Opciones */
            pOptions = NewPage();
            var lbl = Para(pOptions, "Carpeta de instalación", 0, null, 9.5f, FontStyle.Bold);
            dirBox = new TextBox
            {
                Location = new Point(0, lbl.Bottom + S(8)), Width = pOptions.Width - S(122),
                Text = o.Dir ?? existingDir ?? App.DefaultDir,
                BackColor = Ui.Bg2, ForeColor = Ui.Fg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f)
            };
            pOptions.Controls.Add(dirBox);
            var browse = Ui.Btn("Examinar…", false, k);
            browse.Location = new Point(pOptions.Width - browse.Width, dirBox.Top + (dirBox.Height - browse.Height) / 2);
            browse.Click += (s, e) => Browse();
            pOptions.Controls.Add(browse);
            spaceLabel = Para(pOptions, " ", dirBox.Bottom + S(10), Ui.Dim, 9f);
            chkDesktop = Ui.Check(pOptions, "Crear un acceso directo en el escritorio", 0, spaceLabel.Bottom + S(24), !o.NoShortcuts);
            chkStart = Ui.Check(pOptions, "Crear un acceso directo en el menú Inicio", 0, chkDesktop.Bottom + S(8), !o.NoShortcuts);
            Para(pOptions, "Tus ajustes, marcadores y la partida guardada van aparte, en %APPDATA% y %LOCALAPPDATA%\\KoogleKerbin, " +
                           "y no dependen de esta carpeta.", chkStart.Bottom + S(24), Ui.Dim, 9f);
            dirBox.TextChanged += (s, e) => UpdateSpace();
            UpdateSpace();

            /* Progreso */
            pProgress = NewPage();
            var pl = Para(pProgress, "Copiando los ficheros de " + App.Name + "…", 0);
            bar = new ProgressBar
            {
                Location = new Point(0, pl.Bottom + S(16)), Size = new Size(pProgress.Width, S(16)),
                Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous
            };
            pProgress.Controls.Add(bar);
            barFile = Para(pProgress, " ", bar.Bottom + S(10), Ui.Dim, 9f);

            /* Final */
            pDone = NewPage();
            doneText = Para(pDone, " ", 0, null, 10f);
            chkLaunch = Ui.Check(pDone, "Abrir " + App.Name + " ahora", 0, S(80), !o.NoLaunch);
            doneHint = Para(pDone, "Para desinstalarlo: Configuración › Aplicaciones › Aplicaciones instaladas › " + App.Name + ".", S(120), Ui.Dim, 9f);

            ShowPage(0);
        }

        Panel NewPage()
        {
            var p = new Panel
            {
                Location = new Point(S(224), S(78)),
                Size = new Size(ClientSize.Width - S(224) - S(28), ClientSize.Height - S(78) - S(70)),
                BackColor = Ui.Bg,
                Visible = false
            };
            Controls.Add(p);
            return p;
        }

        void ShowPage(int i)
        {
            page = i;
            var pages = new[] { pWelcome, pReq, pOptions, pProgress, pDone };
            for (int j = 0; j < pages.Length; j++) pages[j].Visible = j == i;
            title.Text = Titles[i];
            back.Visible = i < 3;
            back.Enabled = i == 1 || i == 2;
            cancel.Visible = i < 3;
            next.Text = i == 2 ? "Instalar" : i == 4 ? "Finalizar" : "Siguiente  ›";
            next.Enabled = i != 3;
            reqTimer.Enabled = i == 1;
            if (i == 1) CheckRequirements();
            side.Invalidate();
            if (i == 3) StartInstall();
        }

        void PaintSide(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (sideIcon != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(sideIcon, S(24), S(30), S(48), S(48));
            }
            using (var f = new Font("Segoe UI Semibold", 15f))
            {
                int w = TextRenderer.MeasureText(g, "Koogle", f, Size.Empty, flags).Width;
                TextRenderer.DrawText(g, "Koogle", f, new Point(S(24), S(90)), Ui.Fg, flags);
                TextRenderer.DrawText(g, "Kerbin", f, new Point(S(24) + w, S(90)), Ui.Accent, flags);
            }
            TextRenderer.DrawText(g, "Versión " + Build.Version, Font, new Point(S(24), S(124)), Ui.Dim, flags);
            int sy = S(176);
            for (int i = 0; i < Steps.Length; i++)
            {
                Color c = i == page ? Ui.Accent : i < page ? Ui.Fg : Ui.Dim;
                using (var f = new Font("Segoe UI", 9.5f, i == page ? FontStyle.Bold : FontStyle.Regular))
                {
                    TextRenderer.DrawText(g, i < page ? "✓" : i == page ? "›" : "•", f, new Point(S(24), sy), c, flags);
                    TextRenderer.DrawText(g, Steps[i], f, new Point(S(44), sy), c, flags);
                }
                sy += S(30);
            }
            using (var pen = new Pen(Ui.Line)) g.DrawLine(pen, side.Width - 1, 0, side.Width - 1, side.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Ui.Line))
                e.Graphics.DrawLine(pen, side.Width, ClientSize.Height - S(62), ClientSize.Width, ClientSize.Height - S(62));
        }

        void CheckRequirements()
        {
            bool os = Requirements.Os64;
            var rt = Requirements.DesktopRuntime();
            reqOs.Text = (os ? "✔  " : "✘  ") + "Windows de 64 bits";
            reqOs.ForeColor = os ? Ui.Ok : Ui.Bad;
            reqNet.Text = rt != null ? "✔  .NET 10 Desktop Runtime " + rt : "✘  Falta .NET 10 Desktop Runtime";
            reqNet.ForeColor = rt != null ? Ui.Ok : Ui.Bad;
            reqOk = os && rt != null;
            reqHelp.Visible = reqDownload.Visible = reqRetry.Visible = rt == null && os;
            if (!os)
            {
                reqHelp.Text = App.Name + " solo funciona en Windows de 64 bits.";
                reqHelp.Visible = true;
            }
            if (page == 1) next.Enabled = reqOk;
        }

        void UpdateSpace()
        {
            string text = "Espacio necesario: " + Util.Size(needBytes);
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dirBox.Text.Trim()));
                text += "     ·     libre en " + root.TrimEnd('\\') + ": " + Util.Size(new DriveInfo(root).AvailableFreeSpace);
            }
            catch { }
            spaceLabel.Text = text;
        }

        void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "Elige dónde instalar " + App.Name, ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string chosen = d.SelectedPath;
                // una carpeta con otras cosas recibe su propia subcarpeta, para no mezclar ficheros
                bool ours = string.Equals(Path.GetFileName(chosen.TrimEnd('\\')), App.Name, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(chosen, App.Manifest));
                if (!ours && Directory.Exists(chosen) && Directory.EnumerateFileSystemEntries(chosen).Any()) chosen = Path.Combine(chosen, App.Name);
                dirBox.Text = chosen;
            }
        }

        void Warn(string msg)
        {
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        bool ValidateDir()
        {
            string text = dirBox.Text.Trim(), dir;
            try
            {
                if (!Path.IsPathRooted(text)) throw new ArgumentException();
                dir = Path.GetFullPath(text).TrimEnd('\\');
            }
            catch
            {
                Warn("Escribe una ruta completa, por ejemplo " + App.DefaultDir + ".");
                return false;
            }
            if (string.Equals(Path.GetPathRoot(dir).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))
            {
                Warn("Elige una carpeta, no la raíz de una unidad.");
                return false;
            }
            bool existed = Directory.Exists(dir);
            if (existed && Directory.EnumerateFileSystemEntries(dir).Any() && !File.Exists(Path.Combine(dir, App.Manifest)) &&
                MessageBox.Show(this, "La carpeta «" + dir + "» ya tiene otros ficheros.\n\n" + App.Name + " se instalará junto a ellos y, al " +
                                      "desinstalar, solo se borrará lo que haya instalado. ¿Continuar?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            // que se pueda escribir ahí: Archivos de programa, por ejemplo, pide administrador
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".prueba-escritura");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                if (!existed) Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                Warn("No se puede escribir en esa carpeta (" + ex.Message + ").\n\nElige una de tu usuario, como la que viene propuesta.");
                return false;
            }
            while (Util.RunningIn(dir).Count > 0)
                if (MessageBox.Show(this, App.Name + " está abierto desde esa carpeta. Ciérralo y pulsa «Reintentar».", Text,
                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                    return false;
            dirBox.Text = dir;
            return true;
        }

        void OnNext()
        {
            switch (page)
            {
                case 0: ShowPage(1); break;
                case 1: if (reqOk) ShowPage(2); break;
                case 2: if (ValidateDir()) ShowPage(3); break;
                case 4:
                    if (!failed && chkLaunch.Checked) Util.Launch(Path.Combine(installedDir, App.Exe));
                    finished = true;
                    Close();
                    break;
            }
        }

        void StartInstall()
        {
            string dir = Path.GetFullPath(dirBox.Text.Trim());
            installing = true;
            var inst = new Installer { Dir = dir, Desktop = chkDesktop.Checked, StartMenu = chkStart.Checked, Register = !opts.NoRegistry };
            inst.Progress = (done, total, file) =>
            {
                if (!IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    bar.Value = (int)Math.Min(1000, done * 1000 / Math.Max(1, total));
                    barFile.Text = file;
                }));
            };
            var th = new Thread(() =>
            {
                Exception error = null;
                try { inst.Run(); }
                catch (Exception ex) { error = ex; Util.Log(ex); }
                BeginInvoke((Action)(() => Finish(dir, error)));
            }) { IsBackground = true };
            th.Start();
        }

        void Finish(string dir, Exception error)
        {
            installing = false;
            installedDir = dir;
            failed = error != null;
            if (failed)
            {
                doneText.Text = "No se pudo completar la instalación:\n\n" + error.Message + "\n\nLos detalles quedan en " + Util.LogPath + ".";
                doneText.ForeColor = Ui.Bad;
                chkLaunch.Visible = false;
            }
            else doneText.Text = App.Name + " " + Build.Version + " está instalado en:\n" + dir;
            // con la página oculta la etiqueta aún no se ha medido: se calcula su alto con el texto nuevo
            int textH = doneText.GetPreferredSize(new Size(pDone.Width, 0)).Height;
            chkLaunch.Top = doneText.Top + textH + S(22);
            doneHint.Top = (chkLaunch.Visible ? chkLaunch.Top + chkLaunch.Height : doneText.Top + textH) + S(22);
            ShowPage(4);
            if (failed) title.Text = "No se pudo instalar";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (installing) { e.Cancel = true; return; }
            if (!finished && page < 4 && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "¿Salir del instalador? " + App.Name + " no se instalará.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                e.Cancel = true;
            base.OnFormClosing(e);
        }
    }

    sealed class UninstallForm : Form
    {
        readonly CheckBox chk;
        public bool DeleteData { get { return chk.Checked; } }

        public UninstallForm(string dir)
        {
            float k = Ui.DpiScale(this);
            Func<double, int> S = v => (int)Math.Round(v * k);
            Text = "Desinstalar " + App.Name;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(480), S(250));
            BackColor = Ui.Bg;
            ForeColor = Ui.Fg;
            Font = new Font("Segoe UI", 9.5f);
            Icon = Util.AppIcon(0);

            int w = ClientSize.Width - S(48);
            var t = Ui.Para(this, "Se quitará " + App.Name + " de:", S(24), S(22), w, null, 11f, FontStyle.Bold);
            var d = Ui.Para(this, dir, S(24), t.Bottom + S(6), w, Ui.Accent);
            var info = Ui.Para(this, "Tus ajustes, marcadores, mapas cargados y la copia de la partida se guardan aparte y se conservan, " +
                                     "salvo que marques la casilla.", S(24), d.Bottom + S(14), w, Ui.Dim, 9f);
            chk = Ui.Check(this, "Borrar también mis ajustes y datos de " + App.Name, S(24), info.Bottom + S(14), false);

            var cancel = Ui.Btn("Cancelar", false, k);
            var ok = Ui.Btn("Desinstalar", true, k);
            cancel.Location = new Point(ClientSize.Width - S(24) - cancel.Width, ClientSize.Height - S(46));
            ok.Location = new Point(cancel.Left - S(10) - ok.Width, cancel.Top);
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}

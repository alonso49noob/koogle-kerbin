using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KerbinMaps.Core
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, PageUrl, AssetName, AssetUrl, Sha256, Notes;
        public long Size;
    }

    /* Automatic updates.

       Checks the latest release of the repository on GitHub; if it has a newer version than the
       one running, downloads its installer (KoogleKerbin-Setup-<version>.exe), checks its
       SHA-256 against the one GitHub publishes and launches it with /update: the installer
       waits for this application to close, installs into the same folder, keeps whatever
       shortcuts there were and opens the application again.

       It only installs on its own if this copy came from the installer (there's an
       instalacion.txt next to the .exe). A loose or development copy announces the new version
       and opens the release page, but doesn't touch itself.

       With KOOGLE_UPDATE_CURRENT=<version> it pretends to be that version, for testing. */
    public static class Updater
    {
        const string Repo = "alonso49noob/koogle-kerbin";
        const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
        const string ManifestName = "instalacion.txt";

        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("KoogleKerbin-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static Version Current
        {
            get
            {
                string fake = Environment.GetEnvironmentVariable("KOOGLE_UPDATE_CURRENT");
                if (!string.IsNullOrEmpty(fake) && Version.TryParse(fake, out var f)) return Norm(f);
                var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
                return Norm(v);
            }
        }

        // 1.5 and 1.5.0.0 are the same version; the fourth number (1.6.5.1) only counts if it isn't 0
        static Version Norm(Version v) => v.Revision > 0
            ? new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), v.Revision)
            : new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        /* Did this copy come from the installer? Only then can it update itself. */
        public static bool CanSelfUpdate => File.Exists(Path.Combine(AppContext.BaseDirectory, ManifestName));

        static string TempDir => Path.Combine(Path.GetTempPath(), "KoogleKerbin-Update");

        /* The latest release, or null if there's nothing newer. Throws if it couldn't check. */
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            using var resp = await http.GetAsync(ApiUrl, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            string tag = Str(root, "tag_name");
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            v = Norm(v);
            if (v <= Current) return null;

            var info = new UpdateInfo { Version = v, Tag = tag, PageUrl = Str(root, "html_url"), Notes = Str(root, "body") };
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    string name = Str(a, "name"), url = Str(a, "browser_download_url");
                    if (!name.StartsWith("KoogleKerbin-Setup-", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    // only download from this repository's releases, whatever the JSON says
                    if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                    info.AssetName = name;
                    info.AssetUrl = url;
                    info.Size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n) ? n : 0;
                    string digest = Str(a, "digest");
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) info.Sha256 = digest.Substring(7).ToLowerInvariant();
                    break;
                }
            return info;
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

        /* Can it be installed without intervention? It needs the installer and its hash. */
        public static bool CanInstall(UpdateInfo u) => CanSelfUpdate && u?.AssetUrl != null && !string.IsNullOrEmpty(u.Sha256);

        /* Downloads the installer to %TEMP% and verifies it. Returns its path. */
        public static async Task<string> DownloadAsync(UpdateInfo u, Action<long, long> progress, CancellationToken ct = default)
        {
            Directory.CreateDirectory(TempDir);
            string path = Path.Combine(TempDir, u.AssetName), tmp = path + ".part";
            try
            {
                using (var resp = await http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? u.Size, done = 0;
                    using var src = await resp.Content.ReadAsStreamAsync(ct);
                    using var dst = File.Create(tmp);
                    var buf = new byte[1 << 16];
                    int n;
                    var last = Stopwatch.StartNew();
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (last.ElapsedMilliseconds > 100) { progress?.Invoke(done, total); last.Restart(); }
                    }
                    progress?.Invoke(done, total);
                }

                string hash;
                using (var fs = File.OpenRead(tmp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (hash != u.Sha256) throw new InvalidDataException("La huella SHA-256 del instalador no coincide con la publicada.");

                File.Move(tmp, path, true);
                return path;
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }

        /* Launches the installer and returns true; the application has to close right
           afterwards, which is what the installer waits for so it can replace the files. */
        public static bool Launch(string installer)
        {
            try
            {
                string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                Process.Start(new ProcessStartInfo(installer, "/update \"/dir=" + dir + "\"") { UseShellExecute = false });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[update] no se pudo lanzar el instalador: " + ex.Message);
                return false;
            }
        }

        /* Installers from previous updates, which are no longer needed. */
        public static void Cleanup()
        {
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); }
            catch { /* may be in use by a running installer: it'll be deleted next time */ }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KerbinMaps.Core
{
    /* Where KSP can be with Steam: the Steam folder and all its libraries. Steam lets you
       install games in others (D:\SteamLibrary...) and lists them in
       steamapps\libraryfolders.vdf; looking only at the Steam folder missed a KSP installed in
       another one. The installer has the same search (Instalador.cs, Extras). */
    public static class Steam
    {
        static List<string> bibliotecas;

        /* The KSP GameData folders that exist in some Steam library. Checked once per session:
           changing libraries with the application open isn't the usual thing. */
        public static IReadOnlyList<string> GameDatasDeKsp()
        {
            bibliotecas ??= Bibliotecas()
                .Select(b => Path.Combine(b, "steamapps", "common", "Kerbal Space Program", "GameData"))
                .Where(Directory.Exists)
                .ToList();
            return bibliotecas;
        }

        static IEnumerable<string> Bibliotecas()
        {
            var raices = new List<string>();
            foreach (var (vista, clave, valor) in new[]
            {
                (Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Microsoft.Win32.RegistryHive.LocalMachine, @"Software\WOW6432Node\Valve\Steam", "InstallPath"),
                (Microsoft.Win32.RegistryHive.LocalMachine, @"Software\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    using var raiz = Microsoft.Win32.RegistryKey.OpenBaseKey(vista, Microsoft.Win32.RegistryView.Default);
                    using var k = raiz.OpenSubKey(clave);
                    if (k?.GetValue(valor) is string p && p.Length > 0) raices.Add(p.Replace('/', '\\'));
                }
                catch (Exception) { }
            }
            raices.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

            var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in raices)
            {
                if (vistas.Add(Path.GetFullPath(r))) yield return r;
                string vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
                string texto;
                try { texto = File.Exists(vdf) ? File.ReadAllText(vdf) : null; }
                catch (Exception) { texto = null; }
                if (texto == null) continue;
                // «"path"		"D:\\SteamLibrary"»: the backslashes come escaped
                foreach (Match m in Regex.Matches(texto, "\"path\"\\s+\"([^\"]+)\""))
                {
                    string b = m.Groups[1].Value.Replace(@"\\", @"\");
                    string full;
                    try { full = Path.GetFullPath(b); } catch (Exception) { continue; }
                    if (vistas.Add(full)) yield return b;
                }
            }
        }
    }
}

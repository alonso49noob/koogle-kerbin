using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KerbinMaps.Core
{
    /* Dónde puede estar KSP con Steam: la carpeta de Steam y todas sus bibliotecas. Steam
       deja instalar juegos en otras (D:\SteamLibrary...) y las apunta en
       steamapps\libraryfolders.vdf; mirando solo la carpeta de Steam no se encontraba un KSP
       instalado en otra. El instalador tiene la misma búsqueda (Instalador.cs, Extras). */
    public static class Steam
    {
        static List<string> bibliotecas;

        /* Las carpetas GameData de KSP que existen en alguna biblioteca de Steam. Se mira una
           vez por sesión: cambiar de biblioteca con la aplicación abierta no es lo normal. */
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
                // «"path"		"D:\\SteamLibrary"»: las barras van escapadas
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

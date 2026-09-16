using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KerbinMaps.Core
{
    /* Idioma de la interfaz.

       Los textos se escriben en español dentro del código y se traducen al vuelo: la
       clave es el propio texto en español y la traducción sale de data/i18n/<idioma>.json.
       Lo que no esté traducido se queda en español, que es peor pero nunca rompe nada.

       La traducción se aplica al crear los controles (botones, casillas, secciones,
       ayudas, HUD...), así que cambiar de idioma reinicia la aplicación.

       Con la variable de entorno KOOGLE_I18N_LOG=1 se apunta en
       %LOCALAPPDATA%\KoogleKerbin\i18n-faltan.txt cada texto sin traducir, que es como se
       arma el fichero de traducción sin copiar los textos a mano. */
    public static class Lang
    {
        public static readonly (string Code, string Name)[] Available =
        {
            ("es", "Español"),
            ("en", "English")
        };

        public static string Code { get; private set; } = "es";

        static Dictionary<string, string> map = new(StringComparer.Ordinal);
        static readonly HashSet<string> missing = new(StringComparer.Ordinal);
        static readonly bool logMissing = Environment.GetEnvironmentVariable("KOOGLE_I18N_LOG") == "1";

        /* El idioma guardado, o el del sistema la primera vez. */
        public static string Detect(string saved)
        {
            if (!string.IsNullOrEmpty(saved)) return saved;
            string sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            foreach (var (code, _) in Available) if (code == sys) return code;
            return "en";
        }

        public static void Use(string code, string dataDir)
        {
            Code = code ?? "es";
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Code == "es") return;
            try
            {
                string p = Path.Combine(dataDir ?? "", "i18n", Code + ".json");
                if (!File.Exists(p)) return;
                var leido = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p));
                if (leido != null)
                    foreach (var kv in leido)
                        if (!string.IsNullOrEmpty(kv.Value)) map[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[i18n] no se pudo leer la traducción: " + ex.Message);
            }
        }

        /* El texto en el idioma actual. */
        public static string T(string es)
        {
            if (string.IsNullOrEmpty(es) || Code == "es") return es;
            if (map.TryGetValue(es, out var t)) return t;
            Miss(es);
            return es;
        }

        /* Igual, con huecos: la traducción conserva los {0}, {1}... */
        public static string F(string es, params object[] args)
        {
            try { return string.Format(T(es), args); }
            catch (FormatException) { return T(es); }
        }

        static void Miss(string es)
        {
            if (!logMissing || !missing.Add(es)) return;
            try
            {
                string dir = Store.LocalDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "i18n-faltan.txt"), es.Replace("\n", "\\n") + "\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[i18n] no se pudo apuntar el texto que falta: " + ex.Message);
            }
        }
    }
}

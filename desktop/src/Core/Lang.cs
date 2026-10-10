using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KerbinMaps.Core
{
    /* Interface language.

       Texts are written in Spanish inside the code and translated on the fly: the key is the
       Spanish text itself and the translation comes from data/i18n/<language>.json. Whatever
       isn't translated stays in Spanish, which is worse but never breaks anything.

       The translation is applied when controls are created (buttons, checkboxes, sections,
       hints, HUD...), so changing language restarts the application.

       With the environment variable KOOGLE_I18N_LOG=1, every untranslated text is logged to
       %LOCALAPPDATA%\KoogleKerbin\i18n-faltan.txt, which is how the translation file is put
       together without copying texts by hand. */
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

        /* The saved language, or the system's the first time. */
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

        /* The text in the current language. */
        public static string T(string es)
        {
            if (string.IsNullOrEmpty(es) || Code == "es") return es;
            if (map.TryGetValue(es, out var t)) return t;
            Miss(es);
            return es;
        }

        /* Same, with placeholders: the translation keeps the {0}, {1}... */
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

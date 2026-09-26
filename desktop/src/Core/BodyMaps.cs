using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KerbinMaps.Ksp;

namespace KerbinMaps.Core
{
    /* Los mapas de un cuerpo dentro de la carpeta elegida. */
    public sealed class BodyMapSet
    {
        public string Color, Height, Biome;
        public bool Any => Color != null || Height != null || Biome != null;
    }

    /* Mapas de los demás cuerpos sacados de una carpeta: las texturas del propio juego,
       volcadas a PNG con cualquier herramienta de las que extraen los assets.

       Vienen en espejo horizontal respecto al convenio de los mapas equirectangulares al
       uso, y giradas 90° en longitud. Se comprobó cuadrando las texturas contra los mapas
       de biomas de la wiki: Kerbin encaja al 95,6 % (el control entre los dos mapas que
       trae el visor da 95,9 %), y el mismo par espejo+90° sale en Duna, Eve, Laythe, Moho
       y Dres. Por eso el visor aplica esa vuelta solo, y deja cambiarla por si tu volcado
       viene de otra herramienta. */
    public static class BodyMaps
    {
        public const double DefaultOffset = 90;

        /* Esas texturas no usan toda la escala de grises: el tope es 145, no 255. Medido
           en los quince cuerpos del volcado de Parallax, donde el gris maximo va de 141 a
           145 y con ese tope la altura sale justo en el maxTerrainAltitude que el mod
           tiene tabulado. En Kerbin, ademas, pone el KSC en 70 m (su altitud real) y el
           mar abierto en -1052 m (la referencia de SCANsat da entre -1090 y -935). */
        public const double GrisTope = 145;

        /* De rango de terreno a calibracion de la rampa de grises (gris 0 y gris 255). */
        public static (double Min, double Max) Calibracion(double minTerreno, double maxTerreno) =>
            (minTerreno, minTerreno + 255.0 / GrisTope * (maxTerreno - minTerreno));

        /* Un fichero es de un cuerpo si su nombre empieza por el nombre del cuerpo; de ahí
           se mira qué ranura es. Los mapas de normales no sirven para nada aquí, y las
           versiones «_PQS» son de menos resolución que la principal. */
        public static Dictionary<string, BodyMapSet> Index(string dir)
        {
            var found = new Dictionary<string, BodyMapSet>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return found;

            var nombres = SolarSystem.Bodies.Select(b => b.Name).OrderByDescending(n => n.Length).ToList();
            var mejor = new Dictionary<(string, string), (string path, long size)>();

            foreach (var path in Directory.EnumerateFiles(dir))
            {
                string file = Path.GetFileName(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp") continue;
                if (file.Contains("normal", StringComparison.OrdinalIgnoreCase)) continue;

                string body = nombres.FirstOrDefault(n => file.StartsWith(n + "_", StringComparison.OrdinalIgnoreCase)
                                                       || file.StartsWith(n + ".", StringComparison.OrdinalIgnoreCase)
                                                       || file.Equals(n + ext, StringComparison.OrdinalIgnoreCase));
                if (body == null) continue;

                string slot = file.Contains("height", StringComparison.OrdinalIgnoreCase) ? "height"
                            : file.Contains("biome", StringComparison.OrdinalIgnoreCase) ? "biome"
                            : file.Contains("color", StringComparison.OrdinalIgnoreCase) ? "color"
                            : null;
                if (slot == null) continue;

                long size = new FileInfo(path).Length;
                if (file.Contains("_PQS", StringComparison.OrdinalIgnoreCase)) size /= 4;   // la de respaldo
                var key = (body, slot);
                if (mejor.TryGetValue(key, out var prev) && prev.size >= size) continue;
                mejor[key] = (path, size);
            }

            foreach (var ((body, slot), (path, _)) in mejor)
            {
                if (!found.TryGetValue(body, out var set)) found[body] = set = new BodyMapSet();
                switch (slot)
                {
                    case "color": set.Color = path; break;
                    case "height": set.Height = path; break;
                    case "biome": set.Biome = path; break;
                }
            }
            return found;
        }

        /* Rango de alturas de cada cuerpo, que es lo que convierte el gris del mapa en
           metros. SCANsat lo trae tabulado en su configuración; si no está instalado, se
           usa el de la partida (que es el mismo dato) o uno por defecto. */
        public static Dictionary<string, (double Min, double Max)> Ranges(string gameData)
        {
            var r = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(gameData)) return r;
                string cfg = Path.Combine(gameData, "SCANsat", "Resources", "SCANcolors.cfg");
                if (!File.Exists(cfg)) return r;
                var root = ConfigNode.ParseFile(cfg);
                foreach (var cc in root.Children("SCAN_Color_Config"))
                    foreach (var alt in cc.Children("SCANsat_Altimetry"))
                        foreach (var item in alt.Children("Item"))
                        {
                            string name = item.Get("name");
                            if (string.IsNullOrEmpty(name)) continue;
                            if (Num(item.Get("minHeightRange")) is double mn && Num(item.Get("maxHeightRange")) is double mx && mx > mn)
                                r[name] = (mn, mx);
                        }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[mapas] SCANcolors: " + ex.Message); }
            return r;
        }

        /* Alturas reales de cada cuerpo segun Parallax, que es de donde salen estas
           texturas: son las que convierten su gris en metros. */
        public static Dictionary<string, (double Min, double Max)> ParallaxRanges(string gameData)
        {
            var r = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(gameData)) return r;
                string cfg = Path.Combine(gameData, "Parallax_StockPlanetTextures", "_Configs", "ParallaxScaled.cfg");
                if (!File.Exists(cfg)) return r;
                Recorrer(ConfigNode.ParseFile(cfg), null, r);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[mapas] ParallaxScaled: " + ex.Message); }
            return r;
        }

        static void Recorrer(ConfigNode n, string cuerpo, Dictionary<string, (double, double)> r)
        {
            // los nodos del parche vienen como «@Body[Kerbin]»
            if (n.Name.StartsWith("@Body[", StringComparison.Ordinal)) cuerpo = n.Name.Substring(6).TrimEnd(']');
            foreach (var hijo in n.Nodes)
            {
                if (cuerpo != null && hijo.Name.Contains("ScaledProperties", StringComparison.Ordinal)
                    && Num(hijo.Get("minTerrainAltitude")) is double mn && Num(hijo.Get("maxTerrainAltitude")) is double mx && mx > mn)
                    r[cuerpo] = (mn, mx);
                Recorrer(hijo, cuerpo, r);
            }
        }

        static double? Num(string s) =>
            s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }
}

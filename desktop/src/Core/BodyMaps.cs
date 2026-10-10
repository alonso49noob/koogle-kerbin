using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KerbinMaps.Ksp;

namespace KerbinMaps.Core
{
    /* The maps of a body inside the chosen folder. */
    public sealed class BodyMapSet
    {
        public string Color, Height, Biome;
        public bool Any => Color != null || Height != null || Biome != null;
    }

    /* Maps of the other bodies taken from a folder: the game's own textures, dumped to PNG with
       any of the tools that extract assets.

       They come mirrored horizontally relative to the usual equirectangular map convention, and
       rotated 90° in longitude. This was checked by matching the textures against the wiki's
       biome maps: Kerbin fits at 95.6% (the control between the two maps the viewer ships gives
       95.9%), and the same mirror+90° pair shows up on Duna, Eve, Laythe, Moho and Dres. That's
       why the viewer applies that flip on its own, and lets you change it in case your dump
       comes from another tool. */
    public static class BodyMaps
    {
        public const double DefaultOffset = 90;

        /* Those textures don't use the full gray scale: the top is 145, not 255. Measured on
           the fifteen bodies of the Parallax dump, where the maximum gray goes from 141 to 145,
           and with that top the height lands exactly on the maxTerrainAltitude the mod has
           tabulated. On Kerbin it also puts the KSC at 70 m (its real altitude) and the open
           sea at -1052 m (the SCANsat reference gives between -1090 and -935). */
        public const double GrisTope = 145;

        /* From terrain range to calibration of the gray ramp (gray 0 and gray 255). */
        public static (double Min, double Max) Calibracion(double minTerreno, double maxTerreno) =>
            (minTerreno, minTerreno + 255.0 / GrisTope * (maxTerreno - minTerreno));

        /* A file belongs to a body if its name starts with the body's name; from there we work
           out which slot it is. Normal maps are of no use here, and the «_PQS» versions have
           less resolution than the main one. */
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
                if (file.Contains("_PQS", StringComparison.OrdinalIgnoreCase)) size /= 4;   // the fallback one
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

        /* Height range of each body, which is what turns the map's gray into meters. SCANsat
           has it tabulated in its configuration; if it isn't installed, the save's is used
           (it's the same data) or a default one. */
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

        /* Real heights of each body according to Parallax, which is where these textures come
           from: they're what turn their gray into meters. */
        public static Dictionary<string, (double Min, double Max)> ParallaxRanges(string gameData)
        {
            var r = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string cfg = ParallaxPlanets.GameDatas(gameData)
                    .Select(gd => Path.Combine(gd, "Parallax_StockPlanetTextures", "_Configs", "ParallaxScaled.cfg"))
                    .FirstOrDefault(File.Exists);
                if (cfg == null) return r;
                Recorrer(ConfigNode.ParseFile(cfg), null, r);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[mapas] ParallaxScaled: " + ex.Message); }
            return r;
        }

        static void Recorrer(ConfigNode n, string cuerpo, Dictionary<string, (double, double)> r)
        {
            // the patch nodes come as «@Body[Kerbin]»
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

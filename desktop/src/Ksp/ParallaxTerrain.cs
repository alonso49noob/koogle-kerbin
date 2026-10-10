using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Parallax surface textures for a body, with how the mod blends them.

       Parallax paints the ground with four textures per body: low, mid and high by altitude,
       and a slope one for steep ground. When it switches from one to another (in meters) and
       how sharp the transition to the slope one is comes from Terrain.cfg, and the textures
       live inside a Unity bundle read with UnityBundle. */
    public sealed class ParallaxTerrain
    {
        public string Body;
        public TextureFile Low, Mid, High, Steep;
        /* What gives the ground variety in Parallax, tiled like the other four and with one
           channel per texture (r low, g mid, b high, a slope):
           - influence: how much the texture dominates over the planet color;
           - displacement: the fine relief, used to decide the blend between textures (grass
             peeks out between stones instead of fading into them);
           - occlusion: the shadows of that relief. */
        public TextureFile Influence, Displacement, Occlusion;
        /* Normal maps of each texture: the fine relief that lights the ground. */
        public TextureFile BumpLow, BumpMid, BumpHigh, BumpSteep;
        public double LowMidStart, LowMidEnd, MidHighStart, MidHighEnd;
        public double SteepPower = 8, SteepContrast = 4, SteepMidpoint = 0.7;
        public double Tiling = 0.03;                 // repeats per meter

        public double MetrosPorRepeticion => Tiling > 0 ? 1 / Tiling : 30;

        /* Looks for the Parallax configuration and bundle in GameData. Returns null if it isn't
           installed or that body has no textures. */
        public static ParallaxTerrain Load(string gameData, string body, int maxAncho = 2048)
        {
            // in the player's KSP or in whatever the installer downloaded
            string dir = ParallaxPlanets.GameDatas(gameData)
                .Select(gd => Path.Combine(gd, "Parallax_StockTerrainTextures"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Terrain.cfg")));
            if (dir == null) return null;
            string cfg = Path.Combine(dir, "Terrain.cfg");

            ConfigNode props = null;
            foreach (var pt in ConfigNode.ParseFile(cfg).Children("ParallaxTerrain"))
                foreach (var b in pt.Children("Body"))
                    if (string.Equals(b.Get("name"), body, StringComparison.OrdinalIgnoreCase))
                        props = b.Children("ShaderProperties").FirstOrDefault();
            if (props == null) return null;

            var t = new ParallaxTerrain
            {
                Body = body,
                LowMidStart = Num(props, "_LowMidBlendStart", 0),
                LowMidEnd = Num(props, "_LowMidBlendEnd", 1),
                MidHighStart = Num(props, "_MidHighBlendStart", 1e6),
                MidHighEnd = Num(props, "_MidHighBlendEnd", 1e6 + 1),
                SteepPower = Num(props, "_SteepPower", 8),
                SteepContrast = Num(props, "_SteepContrast", 4),
                SteepMidpoint = Num(props, "_SteepMidpoint", 0.7),
                Tiling = Num(props, "_Tiling", 0.03),
            };

            string paquete = Directory.GetFiles(dir, "*.unity3d").FirstOrDefault();
            if (paquete == null) return null;
            using var ub = new UnityBundle(paquete);
            var cab = ub.Nodos.First(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                       && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
            var sf = new UnitySerialized(ub, cab.Offset, cab.Size);

            (long, long)? Recurso(string p)
            {
                string nombre = p.Substring(p.LastIndexOf('/') + 1);
                var n = ub.Nodos.FirstOrDefault(x => x.Path.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                return n.Path == null ? null : (n.Offset, n.Size);
            }

            // the same texture can serve several slots (the Mun uses mid00 in all three)
            var leidas = new Dictionary<string, TextureFile>(StringComparer.OrdinalIgnoreCase);
            TextureFile Leer(string clave)
            {
                string ruta = props.Get(clave);
                if (string.IsNullOrEmpty(ruta)) return null;
                if (leidas.TryGetValue(ruta, out var ya)) return ya;
                var id = sf.Buscar(ruta);
                if (id == null) return leidas[ruta] = null;
                try { return leidas[ruta] = Convertir(sf.LeerTextura(id.Value, Recurso), maxAncho); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[parallax] " + ruta + ": " + ex.Message);
                    return leidas[ruta] = null;
                }
            }

            t.Low = Leer("_MainTexLow");
            t.Mid = Leer("_MainTexMid");
            t.High = Leer("_MainTexHigh");
            t.Steep = Leer("_MainTexSteep");
            t.Influence = Leer("_InfluenceMap");
            t.Displacement = Leer("_DisplacementMap");
            t.Occlusion = Leer("_OcclusionMap");
            t.BumpLow = Leer("_BumpMapLow");
            t.BumpMid = Leer("_BumpMapMid");
            t.BumpHigh = Leer("_BumpMapHigh");
            t.BumpSteep = Leer("_BumpMapSteep");
            if (t.Low == null && t.Mid == null && t.High == null && t.Steep == null) return null;
            // missing ones are covered by another, so no slot is left empty
            t.Mid ??= t.Low ?? t.High ?? t.Steep;
            t.Low ??= t.Mid; t.High ??= t.Mid; t.Steep ??= t.Mid;
            return t;
        }

        /* From a Unity texture to a list of levels ready for the GPU. DXT1 and DXT5, which is
           what Parallax uses, are uploaded as is (crunched ones too, undone down to their DXT
           blocks); levels wider than `maxAncho` are skipped so video memory isn't filled with
           4096 textures that don't show up close. Everything else (some map in R8 or RGBA32) is
           decompressed to RGBA. */
        internal static TextureFile Convertir(UnitySerialized.Textura u, int maxAncho)
        {
            int block;
            uint fmt;
            switch (u.Formato)
            {
                case 10: block = 8; fmt = TextureFile.DXT1; break;       // DXT1
                case 12: block = 16; fmt = TextureFile.DXT5; break;      // DXT5
                case 28: case 29:                                         // crunched DXT1 and DXT5
                {
                    // in the game they're 4096 masks for a couple of km: 1024 is plenty
                    var c = Crunch.Decodificar(u.Datos, Math.Min(maxAncho, 1024));
                    if (c.Niveles.Count == 0) throw new InvalidDataException("sin niveles utilizables");
                    var ct = new TextureFile { CompressedFormat = c.Dxt5 ? TextureFile.DXT5 : TextureFile.DXT1, HasAlpha = c.Dxt5,
                                               Width = c.Niveles[0].W, Height = c.Niveles[0].H };
                    foreach (var n in c.Niveles) ct.Levels.Add(n.Bloques);
                    return ct;
                }
                default:
                {
                    var img = ParallaxPlanets.Decodificar(u, maxAncho) ?? throw new InvalidDataException("sin niveles utilizables");
                    var rgba = new TextureFile { Width = img.Width, Height = img.Height, HasAlpha = true };
                    rgba.Levels.Add(img.Rgba);
                    return rgba;
                }
            }
            var tf = new TextureFile { CompressedFormat = fmt, HasAlpha = u.Formato == 12 };
            int w = u.Ancho, h = u.Alto, off = 0;
            for (int i = 0; i < u.Mips && off < u.Datos.Length; i++)
            {
                int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
                if (off + size > u.Datos.Length) break;
                if (w <= maxAncho)
                {
                    if (tf.Levels.Count == 0) { tf.Width = w; tf.Height = h; }
                    var lvl = new byte[size];
                    Buffer.BlockCopy(u.Datos, off, lvl, 0, size);
                    tf.Levels.Add(lvl);
                }
                off += size;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            if (tf.Levels.Count == 0) throw new InvalidDataException("sin niveles utilizables");
            return tf;
        }

        static double Num(ConfigNode n, string k, double def) =>
            double.TryParse(n.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KerbinMaps.Core;

namespace KerbinMaps.Ksp
{
    /* Color and height maps of the bodies taken from Parallax - Stock Planet Textures.

       They're the same textures that can be dumped to PNG with an asset extractor, but read
       straight from the mod's Unity bundle: nothing needs dumping. They come as Unity stores
       them, with the first row at the bottom; once flipped they're identical to a dump (checked
       byte by byte on Kerbin) and from there they follow the same path: mirror and 90° rotation
       (see BodyMaps).

       Color is DXT1/DXT5 and heights are R8, an 8-bit gray on Parallax's scale (top at gray
       145). */
    public static class ParallaxPlanets
    {
        public const string Carpeta = "Parallax_StockPlanetTextures";

        /* GameData folders to search: KSP's and the one with the textures downloaded by the
           installer, for those who don't have Parallax in the game. */
        public static IEnumerable<string> GameDatas(string gameData)
        {
            if (!string.IsNullOrEmpty(gameData)) yield return gameData;
            string bajadas = Path.Combine(Store.LocalDir, "parallax", "GameData");
            if (Directory.Exists(bajadas)) yield return bajadas;
        }

        public static string FindBundle(string gameData)
        {
            foreach (var gd in GameDatas(gameData))
            {
                string dir = Path.Combine(gd, Carpeta);
                if (!Directory.Exists(dir)) continue;
                var b = Directory.GetFiles(dir, "*.unity3d").FirstOrDefault();
                if (b != null) return b;
            }
            return null;
        }

        /* Which bodies have a map in the bundle, without reading any texture. */
        public static HashSet<string> Cuerpos(string bundle)
        {
            var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (bundle == null) return r;
            try
            {
                using var ub = new UnityBundle(bundle);
                var sf = Abrir(ub);
                foreach (var k in sf.Contenedor.Keys)
                {
                    // «parallax_stockplanettextures/kerbin/plugindata/kerbin_color.dds»
                    var partes = k.Split('/');
                    if (partes.Length >= 4 && partes[^1].EndsWith("_color.dds", StringComparison.OrdinalIgnoreCase))
                        r.Add(partes[^3]);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[parallax] " + ex.Message); }
            return r;
        }

        /* Color and heights of a body, already with the first row at the top. `maxAncho` limits
           the mipmap level that's read: Kerbin's height map is 8192 wide. */
        public static (ImageData Color, ImageData Height) Load(string bundle, string body, int maxAncho = 4096)
        {
            using var ub = new UnityBundle(bundle);
            var sf = Abrir(ub);
            (long, long)? Recurso(string p)
            {
                string nombre = p.Substring(p.LastIndexOf('/') + 1);
                var n = ub.Nodos.FirstOrDefault(x => x.Path.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                return n.Path == null ? null : (n.Offset, n.Size);
            }

            ImageData Leer(string sufijo)
            {
                var id = sf.Buscar($"{body}/PluginData/{body}_{sufijo}.dds");
                if (id == null) return null;
                var t = sf.LeerTextura(id.Value, Recurso);
                return Decodificar(t, maxAncho)?.FlipY();
            }

            return (Leer("Color"), Leer("Height"));
        }

        /* The height map just as it comes in the bundle: R8 with all its mipmap levels one
           after another, first row at the bottom. For the detail tiles, which read at full
           resolution only the area under the camera. Null if it isn't R8. */
        public static (byte[] Datos, int Ancho, int Alto, int Mips)? CargarAlturasCrudas(string bundle, string body)
        {
            using var ub = new UnityBundle(bundle);
            var sf = Abrir(ub);
            (long, long)? Recurso(string p)
            {
                string nombre = p.Substring(p.LastIndexOf('/') + 1);
                var n = ub.Nodos.FirstOrDefault(x => x.Path.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                return n.Path == null ? null : (n.Offset, n.Size);
            }
            var id = sf.Buscar($"{body}/PluginData/{body}_Height.dds");
            if (id == null) return null;
            var t = sf.LeerTextura(id.Value, Recurso);
            if (t.Formato != 63 && t.Formato != 1) return null;
            return (t.Datos, t.Ancho, t.Alto, t.Mips);
        }

        static UnitySerialized Abrir(UnityBundle ub)
        {
            var cab = ub.Nodos.First(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                       && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
            return new UnitySerialized(ub, cab.Offset, cab.Size);
        }

        /* Picks the first mipmap level that fits in `maxAncho` and converts it to RGBA. */
        internal static ImageData Decodificar(UnitySerialized.Textura t, int maxAncho)
        {
            int bpp;           // in 4×4 blocks for DXT, in bytes per pixel for R8
            bool dxt = t.Formato == 10 || t.Formato == 12;
            switch (t.Formato)
            {
                case 10: bpp = 8; break;
                case 12: bpp = 16; break;
                case 63: bpp = 1; break;            // R8
                case 1: bpp = 1; break;             // Alpha8
                case 4: bpp = 4; break;             // RGBA32
                case 3: bpp = 3; break;             // RGB24
                default: throw new NotSupportedException("formato de textura de Unity " + t.Formato);
            }
            int w = t.Ancho, h = t.Alto, off = 0;
            for (int i = 0; i < t.Mips; i++)
            {
                int size = dxt ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * bpp : w * h * bpp;
                if (w <= maxAncho)
                {
                    if (off + size > t.Datos.Length) return null;
                    var nivel = new byte[size];
                    Buffer.BlockCopy(t.Datos, off, nivel, 0, size);
                    byte[] rgba;
                    if (dxt) rgba = DxtDecoder.Decode(nivel, w, h, t.Formato == 12);
                    else if (bpp == 4) rgba = nivel;
                    else if (bpp == 3)
                    {
                        rgba = new byte[w * h * 4];
                        for (int k = 0; k < w * h; k++)
                        {
                            rgba[k * 4] = nivel[k * 3]; rgba[k * 4 + 1] = nivel[k * 3 + 1]; rgba[k * 4 + 2] = nivel[k * 3 + 2]; rgba[k * 4 + 3] = 255;
                        }
                    }
                    else
                    {
                        rgba = new byte[w * h * 4];
                        for (int k = 0; k < w * h; k++)
                        {
                            byte v = nivel[k];
                            rgba[k * 4] = v; rgba[k * 4 + 1] = v; rgba[k * 4 + 2] = v; rgba[k * 4 + 3] = 255;
                        }
                    }
                    return ImageData.FromRgba(rgba, w, h);
                }
                off += size;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return null;
        }
    }
}

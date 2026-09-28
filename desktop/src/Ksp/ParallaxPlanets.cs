using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KerbinMaps.Core;

namespace KerbinMaps.Ksp
{
    /* Mapas de color y alturas de los cuerpos sacados de Parallax - Stock Planet Textures.

       Son las mismas texturas que se pueden volcar a PNG con un extractor de assets, pero
       leídas directamente del paquete de Unity del mod: no hace falta volcar nada. Vienen
       como las guarda Unity, con la primera fila abajo; tras darles la vuelta quedan
       idénticas a un volcado (comprobado byte a byte en Kerbin) y a partir de ahí siguen
       el mismo camino: espejo y 90° de giro (ver BodyMaps).

       El color va en DXT1/DXT5 y las alturas en R8, un gris de 8 bits con la escala de
       Parallax (tope en el gris 145). */
    public static class ParallaxPlanets
    {
        public const string Carpeta = "Parallax_StockPlanetTextures";

        /* Carpetas GameData donde buscar: la de KSP y la de las texturas descargadas por el
           instalador, para quien no tenga Parallax en el juego. */
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

        /* Qué cuerpos tienen mapa en el paquete, sin leer ninguna textura. */
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

        /* Color y alturas de un cuerpo, ya con la primera fila arriba. `maxAncho` limita el
           nivel de mipmap que se lee: el de alturas de Kerbin es de 8192 de ancho. */
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

        static UnitySerialized Abrir(UnityBundle ub)
        {
            var cab = ub.Nodos.First(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                       && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
            return new UnitySerialized(ub, cab.Offset, cab.Size);
        }

        /* Elige el primer nivel de mipmap que quepa en `maxAncho` y lo pasa a RGBA. */
        static ImageData Decodificar(UnitySerialized.Textura t, int maxAncho)
        {
            int bpp;           // en bloques de 4×4 para DXT, en bytes por píxel para R8
            bool dxt = t.Formato == 10 || t.Formato == 12;
            switch (t.Formato)
            {
                case 10: bpp = 8; break;
                case 12: bpp = 16; break;
                case 63: bpp = 1; break;            // R8
                case 1: bpp = 1; break;             // Alpha8
                case 4: bpp = 4; break;             // RGBA32
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KerbinMaps.Core;

namespace KerbinMaps.Ksp
{
    /* The Kerbin maps the installation itself ships, better than the viewer's:

       - Color: the game's texture of Kerbin seen from afar (KerbinScaledSpace300 in
         sharedassets2.assets), 8192×4096 in BC7. BC7 isn't decompressed here: the GPU does it
         (see Texture.DescomprimirEnGpu), so this only gives the level's bytes.
       - Biomes: the game's attribute map («kerbin_biome», a MonoBehaviour in
         sharedassets9.assets with the CBAttributeMapSO script): 4096×2048 in RGB, with the
         names and colors of each biome. The colors are the same as in the usual biome maps, so
         the names match.
       - Height: the one from the Parallax bundle, 8192×4096 and with the full gray scale.

       All three come in Unity's and Parallax's convention: first row at the bottom, longitude
       reversed and rotated 90°. Here they're left like the viewer's maps (lat 90 at the top,
       lon −180 on the left), with rotation at 0. Measured against the 1800 biome map: 98% match
       on biomes and 95% on color. */
    public static class MapasDelJuego
    {
        /* Kerbin's color from the game, not decompressed: Unity format, size and bytes of the
           level no wider than `maxAncho`. Null if it isn't there. */
        public static (int Formato, int Ancho, int Alto, byte[] Nivel)? ColorCrudo(StockAssets sa, string cuerpo, int maxAncho = 8192)
        {
            if (sa == null || cuerpo != "Kerbin") return null;
            var clave = sa.Find("KerbinScaledSpace300");
            var t = clave == null ? null : sa.LoadCruda(clave);
            if (t == null || t.Formato != 25) return null;
            int w = t.Ancho, h = t.Alto, off = 0;
            for (int i = 0; i < t.Mips; i++)
            {
                int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16;
                if (off + size > t.Datos.Length) return null;
                if (w <= maxAncho)
                {
                    var nivel = new byte[size];
                    Buffer.BlockCopy(t.Datos, off, nivel, 0, size);
                    return (t.Formato, w, h, nivel);
                }
                off += size;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            return null;
        }

        /* The body's biome map and the name of each color («#3762ab» → «Water»). */
        public static (ImageData Mapa, Dictionary<string, string> Nombres)? Biomas(StockAssets sa, string cuerpo)
        {
            if (sa == null) return null;
            string buscado = cuerpo.ToLowerInvariant() + "_biome";
            foreach (var file in sa.Ficheros())
            {
                var s = sa.Serializado(file);
                if (s == null) continue;
                foreach (var kv in s.Objetos)
                {
                    if (kv.Value.ClassId != 114 || kv.Value.Size < 100000) continue;
                    // m_GameObject (12), m_Enabled (4), m_Script (12) and the name
                    var cab = s.LeerInicio(kv.Key, 96);
                    if (cab.Length < 40) continue;
                    int n = BitConverter.ToInt32(cab, 28);
                    if (n != buscado.Length || 32 + n > cab.Length) continue;
                    if (!Encoding.ASCII.GetString(cab, 32, n).Equals(buscado, StringComparison.OrdinalIgnoreCase)) continue;
                    try { return Leer(s.Leer(kv.Key)); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[biomas del juego] " + ex.Message); return null; }
                }
            }
            return null;
        }

        static (ImageData, Dictionary<string, string>) Leer(byte[] d)
        {
            // after the name, the width, the height, the bytes per pixel and the data size,
            // which have to agree with each other (in between there's another name and an integer)
            int p = -1;
            for (int i = 32; i + 16 <= Math.Min(d.Length, 200); i += 4)
            {
                int w = BitConverter.ToInt32(d, i), h = BitConverter.ToInt32(d, i + 4), bpp = BitConverter.ToInt32(d, i + 8), n = BitConverter.ToInt32(d, i + 12);
                if (w > 0 && h > 0 && w == 2 * h && (bpp == 3 || bpp == 4) && (long)w * h * bpp == n && i + 16 + (long)n <= d.Length) { p = i; break; }
            }
            if (p < 0) throw new InvalidDataException("no reconozco el mapa de biomas");
            int ancho = BitConverter.ToInt32(d, p), alto = BitConverter.ToInt32(d, p + 4), bytes = BitConverter.ToInt32(d, p + 8);
            int datos = p + 16;
            var rgba = Alinear((x, y) =>
            {
                int i = datos + (y * ancho + x) * bytes;
                return (d[i], d[i + 1], d[i + 2]);
            }, ancho, alto, filaCeroAbajo: true);

            // the attributes: an integer, how many, and each one with its color, name, tag and value
            var nombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int q = (datos + ancho * alto * bytes + 3) & ~3;
                q += 4;
                int cuantos = BitConverter.ToInt32(d, q); q += 4;
                for (int k = 0; k < cuantos && k < 64; k++)
                {
                    float r = BitConverter.ToSingle(d, q), g = BitConverter.ToSingle(d, q + 4), b = BitConverter.ToSingle(d, q + 8);
                    q += 16;
                    string nombre = Cadena(d, ref q);
                    Cadena(d, ref q);                                  // translation tag
                    q += 12;                                           // value and flags
                    string hex = "#" + ((int)Math.Round(r * 255)).ToString("x2") + ((int)Math.Round(g * 255)).ToString("x2") + ((int)Math.Round(b * 255)).ToString("x2");
                    if (!string.IsNullOrWhiteSpace(nombre)) nombres[hex] = nombre;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[biomas del juego] nombres: " + ex.Message); }
            return (ImageData.FromRgba(rgba, ancho, alto), nombres);
        }

        static string Cadena(byte[] d, ref int q)
        {
            int n = BitConverter.ToInt32(d, q);
            if (n < 0 || n > 256 || q + 4 + n > d.Length) throw new InvalidDataException("cadena inválida");
            string s = Encoding.UTF8.GetString(d, q + 4, n);
            q = (q + 4 + n + 3) & ~3;
            return s;
        }

        /* RGBA of an already-decompressed level (first row at the bottom, as the GPU gives it
           with Unity's data), to the viewer's convention. */
        public static ImageData DesdeRgbaDeUnity(byte[] rgba, int ancho, int alto) =>
            ImageData.FromRgba(Alinear((x, y) =>
            {
                int i = (y * ancho + x) * 4;
                return (rgba[i], rgba[i + 1], rgba[i + 2]);
            }, ancho, alto, filaCeroAbajo: true), ancho, alto);

        /* A Parallax map already with the first row at the top (ParallaxPlanets.Load). */
        public static ImageData DesdeParallax(ImageData img) =>
            img == null ? null : ImageData.FromRgba(Alinear((x, y) =>
            {
                int i = (y * img.Width + x) * 4;
                return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
            }, img.Width, img.Height, filaCeroAbajo: false), img.Width, img.Height);

        /* Viewer column x = original column width−1−((x − 3/4·width) mod width): longitude
           reversed and rotated 90°. */
        static byte[] Alinear(Func<int, int, (byte R, byte G, byte B)> leer, int ancho, int alto, bool filaCeroAbajo)
        {
            var o = new byte[(long)ancho * alto * 4];
            int giro = ancho * 3 / 4;
            var col = new int[ancho];
            for (int x = 0; x < ancho; x++) col[x] = ancho - 1 - (((x - giro) % ancho + ancho) % ancho);
            System.Threading.Tasks.Parallel.For(0, alto, y =>
            {
                int ys = filaCeroAbajo ? alto - 1 - y : y;
                long fila = (long)y * ancho * 4;
                for (int x = 0; x < ancho; x++)
                {
                    var (r, g, b) = leer(col[x], ys);
                    long i = fila + x * 4;
                    o[i] = r; o[i + 1] = g; o[i + 2] = b; o[i + 3] = 255;
                }
            });
            return o;
        }
    }
}

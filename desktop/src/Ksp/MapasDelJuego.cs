using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KerbinMaps.Core;

namespace KerbinMaps.Ksp
{
    /* Los mapas de Kerbin que trae la propia instalación, mejores que los del visor:

       - Color: la textura de Kerbin visto de lejos del juego (KerbinScaledSpace300 en
         sharedassets2.assets), de 8192×4096 en BC7. Aquí no se descomprime BC7: lo hace la
         GPU (ver Texture.DescomprimirEnGpu), así que esto solo da los bytes del nivel.
       - Biomas: el mapa de atributos del juego («kerbin_biome», un MonoBehaviour de
         sharedassets9.assets con el script de CBAttributeMapSO): 4096×2048 en RGB, con los
         nombres y colores de cada bioma. Los colores son los mismos que los de los mapas de
         biomas de siempre, así que los nombres casan.
       - Altura: la del paquete de Parallax, de 8192×4096 y con toda la escala de grises.

       Los tres vienen en la convención de Unity y de Parallax: la primera fila abajo, la
       longitud al revés y girada 90°. Aquí se dejan como los mapas del visor (lat 90 arriba,
       lon −180 a la izquierda), con el giro en 0. Medido contra el mapa de biomas de 1800:
       98 % de coincidencia en los biomas y 95 % en el color. */
    public static class MapasDelJuego
    {
        /* El color de Kerbin del juego, sin descomprimir: formato de Unity, tamaño y bytes
           del nivel de no más de `maxAncho`. Null si no está. */
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

        /* El mapa de biomas del cuerpo y el nombre de cada color («#3762ab» → «Water»). */
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
                    // m_GameObject (12), m_Enabled (4), m_Script (12) y el nombre
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
            // tras el nombre, el ancho, el alto, los bytes por píxel y el tamaño de los datos,
            // que tienen que cuadrar entre sí (entre medias hay otro nombre y un entero)
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

            // los atributos: un entero, cuántos, y cada uno con su color, nombre, etiqueta y valor
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
                    Cadena(d, ref q);                                  // etiqueta de traducción
                    q += 12;                                           // valor y marcas
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

        /* RGBA de un nivel ya descomprimido (primera fila abajo, como lo da la GPU con los
           datos de Unity), a la convención del visor. */
        public static ImageData DesdeRgbaDeUnity(byte[] rgba, int ancho, int alto) =>
            ImageData.FromRgba(Alinear((x, y) =>
            {
                int i = (y * ancho + x) * 4;
                return (rgba[i], rgba[i + 1], rgba[i + 2]);
            }, ancho, alto, filaCeroAbajo: true), ancho, alto);

        /* Un mapa de Parallax ya con la primera fila arriba (ParallaxPlanets.Load). */
        public static ImageData DesdeParallax(ImageData img) =>
            img == null ? null : ImageData.FromRgba(Alinear((x, y) =>
            {
                int i = (y * img.Width + x) * 4;
                return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
            }, img.Width, img.Height, filaCeroAbajo: false), img.Width, img.Height);

        /* Columna x del visor = columna ancho−1−((x − 3/4·ancho) mod ancho) del original:
           longitud al revés y girada 90°. */
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
